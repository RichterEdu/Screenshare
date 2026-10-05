package dev.screenshare.android.net

import dev.screenshare.android.pairing.PairingInfo
import dev.screenshare.android.protocol.AuthMessage
import dev.screenshare.android.protocol.ConfigMessage
import dev.screenshare.android.protocol.DeniedMessage
import dev.screenshare.android.protocol.DeniedReason
import dev.screenshare.android.protocol.FrameMessage
import dev.screenshare.android.protocol.HelloMessage
import dev.screenshare.android.protocol.KeyframeRequestMessage
import dev.screenshare.android.protocol.Message
import dev.screenshare.android.protocol.MessageCodec
import dev.screenshare.android.protocol.MessageReader
import dev.screenshare.android.protocol.PairMessage
import dev.screenshare.android.protocol.PairedMessage
import dev.screenshare.android.protocol.PingMessage
import dev.screenshare.android.protocol.PongMessage
import dev.screenshare.android.protocol.ProtocolException
import dev.screenshare.android.protocol.VideoCodec
import dev.screenshare.android.security.PairedPc
import dev.screenshare.android.security.PinnedTrustManager
import dev.screenshare.android.video.ClockSync
import dev.screenshare.android.video.VideoSink
import java.io.EOFException
import java.io.IOException
import java.io.OutputStream
import java.net.ConnectException
import java.net.InetSocketAddress
import java.net.Socket
import java.net.SocketException
import java.net.SocketTimeoutException
import java.security.cert.CertificateException
import javax.net.ssl.SSLException
import javax.net.ssl.SSLSocket
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

/** Tamanho e densidade da tela do celular e os codecs que ele decodifica, enviados ao PC no HELLO. */
data class ScreenInfo(val width: Int, val height: Int, val densityDpi: Int, val codecs: Int = VideoCodec.ALL)

sealed interface ConnectionState {
    data object Disconnected : ConnectionState
    data object Connecting : ConnectionState

    /** Handshake concluído. [rttMs] é o último tempo de ida e volta medido por PING/PONG (null até o primeiro PONG). */
    data class Connected(val config: ConfigMessage, val rttMs: Double?) : ConnectionState

    /** [denied] vem preenchido quando o PC recusou com DENIED. */
    data class Failed(val reason: String, val denied: DeniedReason? = null) : ConnectionState
}

/** Para onde e como conectar. */
sealed interface ConnectTarget {
    /** Cabo USB com o PC já pareado: 127.0.0.1 depois do `adb reverse`, e então o mesmo fluxo do Wi-Fi (TLS com a digital fixa, AUTH). */
    data class Usb(val pc: PairedPc, val port: Int = USB_PORT) : ConnectTarget

    /** Wi-Fi com o PC já pareado: TLS com a digital fixa, depois AUTH. */
    data class Wifi(val address: HostAddress, val pc: PairedPc) : ConnectTarget

    /** Primeiro contato vindo do QR: TLS com a digital do QR, PAIR → PAIRED, depois AUTH. */
    data class Pairing(
        val info: PairingInfo,
        val deviceName: String,
        /** Pareia pelo cabo: conecta em 127.0.0.1:[usbPort] em vez do IP do QR (o IP continua sendo o `lastHost` salvo). */
        val overUsb: Boolean = false,
        val usbPort: Int = USB_PORT,
    ) : ConnectTarget
}

/**
 * Conexão com o host: (TLS + PAIR/AUTH, no Wi-Fi e no USB) → HELLO → CONFIG, depois PING periódico para medir a latência.
 * Depois do CONFIG: responde o PING do PC com PONG (e acerta o relógio por ele), entrega CONFIGs novos e FRAMEs ao
 * [sink] e derruba a conexão se o PC ficar mudo por [idleTimeoutMs] (ele pinga a cada segundo).
 * Uma conexão por vez; chamar [connect] de novo encerra a anterior.
 * [screen] é lido a cada conexão (a tela em uso pode mudar num dobrável).
 * [onPaired] é chamado (na thread de IO) quando um pareamento termina, com os dados a salvar.
 */
class Connection(
    private val scope: CoroutineScope,
    private val screen: () -> ScreenInfo,
    private val pingIntervalMs: Long = 1_000,
    private val handshakeTimeoutMs: Int = 5_000,
    private val idleTimeoutMs: Int = 5_000,
    private val sink: VideoSink = VideoSink.NONE,
    private val onPaired: (PairedPc) -> Unit = {},
) {
    private val _state = MutableStateFlow<ConnectionState>(ConnectionState.Disconnected)
    val state: StateFlow<ConnectionState> = _state.asStateFlow()

    private var job: Job? = null
    private var socket: Socket? = null

    @Volatile
    private var output: OutputStream? = null

    /** O relógio do PC visto do celular, para a latência do vídeo. Recomeça a cada conexão. */
    @Volatile
    var clock = ClockSync()
        private set

    fun connect(target: ConnectTarget) {
        disconnect()
        clock = ClockSync()
        _state.value = ConnectionState.Connecting
        val s = Socket().also { socket = it } // guardado já aqui para disconnect() poder interromper o connect
        job = scope.launch(Dispatchers.IO) { run(s, target) }
    }

    /** Pede ao PC um quadro completo (decoder novo ou com erro). Não bloqueia; sem conexão, não faz nada. */
    fun requestKeyframe() {
        val out = output ?: return
        scope.launch(Dispatchers.IO) {
            try {
                out.send(KeyframeRequestMessage)
            } catch (_: IOException) {
                // a leitura percebe a queda e trata
            }
        }
    }

    fun disconnect() {
        job?.cancel()
        job = null
        socket?.closeQuietly() // destrava a leitura bloqueante (fechar o socket de baixo também derruba o TLS)
        socket = null
        _state.value = ConnectionState.Disconnected
    }

    private suspend fun run(raw: Socket, target: ConnectTarget) {
        try {
            val (host, port) = target.endpoint()
            raw.tcpNoDelay = true
            raw.connect(InetSocketAddress(host, port), handshakeTimeoutMs)
            val fingerprint = when (target) {
                is ConnectTarget.Usb -> target.pc.fingerprint
                is ConnectTarget.Wifi -> target.pc.fingerprint
                is ConnectTarget.Pairing -> target.info.fingerprint
            }
            val s = try {
                raw.upgradeToTls(host, port, fingerprint)
            } catch (e: IOException) {
                // Com o adb reverse ativo e o ScreenShare fechado no PC, o adbd aceita e fecha na hora: o TLS acaba
                // em EOF/reset, o mesmo que "ninguém escutando". O erro de certificado não passa por aqui.
                if (e.closedByPeerDuringHandshake()) throw ConnectException(e.message) else throw e
            }
            s.soTimeout = handshakeTimeoutMs
            val out = s.getOutputStream()
            val reader = MessageReader(s.getInputStream())

            when (target) {
                is ConnectTarget.Usb -> out.send(AuthMessage(target.pc.token))
                is ConnectTarget.Wifi -> out.send(AuthMessage(target.pc.token))
                is ConnectTarget.Pairing -> {
                    out.send(PairMessage(target.info.secret, target.deviceName))
                    val token = when (val reply = reader.read()) {
                        is PairedMessage -> reply.token
                        is DeniedMessage -> return denied(reply.reason)
                        else -> return fail("Resposta inesperada do PC durante o pareamento")
                    }
                    onPaired(PairedPc(target.info.pcName, target.info.fingerprint, token, target.info.host))
                    out.send(AuthMessage(token))
                }
            }

            val info = screen()
            out.send(HelloMessage(MessageCodec.PROTOCOL_VERSION, info.width, info.height, info.densityDpi, info.codecs))
            val config = when (val reply = reader.read()) {
                is ConfigMessage -> reply
                is DeniedMessage -> return denied(reply.reason)
                else -> return fail("O PC recusou a conexão")
            }

            s.soTimeout = idleTimeoutMs // o PC pinga a cada segundo: mudo por mais que isso é conexão morta
            // disconnect() pode ter corrido com a leitura do CONFIG: não publicar Connected depois de Disconnected
            if (!currentCoroutineContext().isActive) return
            _state.value = ConnectionState.Connected(config, rttMs = null)
            output = out
            sink.onConfig(config)
            val pinger = scope.launch(Dispatchers.IO) {
                while (isActive) {
                    delay(pingIntervalMs)
                    try {
                        out.send(PingMessage(nowMicros()))
                    } catch (_: IOException) {
                        raw.closeQuietly() // a leitura abaixo falha e trata o erro
                        return@launch
                    }
                }
            }
            try {
                while (true) {
                    when (val message = reader.read() ?: break) {
                        is PingMessage -> {
                            val received = nowMicros()
                            out.send(PongMessage(message.timestampUs))
                            clock.onPcPing(message.timestampUs, received)
                        }
                        is PongMessage -> {
                            val now = nowMicros()
                            clock.onRtt(now - message.timestampUs, now)
                            val rtt = (now - message.timestampUs) / 1_000.0
                            _state.update { if (it is ConnectionState.Connected) it.copy(rttMs = rtt) else it }
                        }
                        is ConfigMessage -> { // o PC recomeçou o vídeo (resolução nova, encoder novo)
                            _state.update { if (it is ConnectionState.Connected) it.copy(config = message) else it }
                            sink.onConfig(message)
                        }
                        is FrameMessage -> sink.onFrame(message)
                        else -> Unit // nada mais vem do PC depois do CONFIG
                    }
                }
            } finally {
                pinger.cancel()
                output = null
            }
            fail("Conexão perdida")
        } catch (e: IOException) { // inclui ProtocolException, EOFException e erros de TLS
            fail(e.describe())
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) { // nada deve escapar da corrotina e derrubar o app
            fail("Erro inesperado na conexão: ${e.message ?: e.javaClass.simpleName}")
        } finally {
            raw.closeQuietly()
        }
    }

    private fun ConnectTarget.endpoint(): kotlin.Pair<String, Int> = when (this) {
        is ConnectTarget.Usb -> LOOPBACK to port
        is ConnectTarget.Wifi -> address.host to address.port
        is ConnectTarget.Pairing -> if (overUsb) LOOPBACK to usbPort else info.host to info.port
    }

    /** TLS por cima do socket já conectado, aceitando só o certificado com a digital esperada. */
    private fun Socket.upgradeToTls(host: String, port: Int, fingerprint: String): SSLSocket =
        (PinnedTrustManager(fingerprint).socketFactory().createSocket(this, host, port, true) as SSLSocket).apply {
            soTimeout = handshakeTimeoutMs
            startHandshake()
        }

    /** Só publica a falha se esta conexão ainda é a atual: disconnect() e um novo connect() cancelam a corrotina antiga. */
    private suspend fun fail(reason: String) {
        if (currentCoroutineContext().isActive) _state.value = ConnectionState.Failed(reason)
    }

    private suspend fun denied(reason: DeniedReason) {
        if (currentCoroutineContext().isActive) _state.value = ConnectionState.Failed(reason.describe(), reason)
    }

    private fun DeniedReason.describe() = when (this) {
        DeniedReason.INVALID_PAIRING_SECRET -> "QR de pareamento inválido ou expirado. Gere um novo no PC."
        DeniedReason.UNKNOWN_DEVICE -> "Este celular não está mais pareado com o PC. Pareie de novo."
        DeniedReason.INCOMPATIBLE_VERSION -> "Versão incompatível com o PC. Atualize o app e o ScreenShare do PC."
    }

    private fun IOException.describe() = when {
        this is ConnectException ->
            "Não foi possível conectar ao PC. No cabo, rode adb reverse tcp:$USB_PORT tcp:$USB_PORT; " +
                "no Wi-Fi, confira se o ScreenShare está aberto no PC."
        this is SocketTimeoutException ->
            "O PC não respondeu a tempo. Se a conexão anterior caiu agora, espere uns 10 segundos e tente de novo."
        this is ProtocolException -> "Resposta inválida do PC: $message"
        this is SSLException && causes().any { it is CertificateException } ->
            "Este não é o PC pareado (certificado diferente). Pareie de novo."
        this is SSLException -> "Falha na conexão segura: ${message ?: "erro de TLS"}"
        else -> message?.takeIf { it.isNotBlank() } ?: "Erro de rede"
    }

    private companion object {
        const val LOOPBACK = "127.0.0.1"
    }

    private fun IOException.closedByPeerDuringHandshake(): Boolean {
        if (causes().any { it is CertificateException || it is SocketTimeoutException }) return false
        return causes().any { it is EOFException || it is SocketException } ||
            (this is SSLException && message?.contains("terminated the handshake", ignoreCase = true) == true)
    }

    private fun Throwable.causes() = generateSequence(this) { it.cause }

    private fun OutputStream.send(message: Message) = synchronized(this) {
        write(MessageCodec.encode(message))
        flush()
    }

    private fun nowMicros() = System.nanoTime() / 1_000

    private fun Socket.closeQuietly() = try { close() } catch (_: IOException) {}
}
