package dev.screenshare.android.net

import dev.screenshare.android.pairing.PairingInfo
import dev.screenshare.android.protocol.AuthMessage
import dev.screenshare.android.protocol.ConfigMessage
import dev.screenshare.android.protocol.DeniedMessage
import dev.screenshare.android.protocol.DeniedReason
import dev.screenshare.android.protocol.HelloMessage
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
import java.io.IOException
import java.io.OutputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.security.cert.CertificateException
import javax.net.ssl.SSLException
import javax.net.ssl.SSLSocket
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

/** Tamanho e densidade da tela do celular, enviados ao PC no HELLO. */
data class ScreenInfo(val width: Int, val height: Int, val densityDpi: Int)

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
    /** Cabo USB: 127.0.0.1 depois do `adb reverse tcp:38701 tcp:38701`, sem TLS nem pareamento. */
    data class Usb(val port: Int = USB_PORT) : ConnectTarget

    /** Wi-Fi com o PC já pareado: TLS com a digital fixa, depois AUTH. */
    data class Wifi(val address: HostAddress, val pc: PairedPc) : ConnectTarget

    /** Primeiro contato vindo do QR: TLS com a digital do QR, PAIR → PAIRED, depois AUTH. */
    data class Pairing(val info: PairingInfo, val deviceName: String) : ConnectTarget
}

/**
 * Conexão com o host: (TLS + PAIR/AUTH no Wi-Fi) → HELLO → CONFIG, depois PING periódico para medir a latência.
 * Uma conexão por vez; chamar [connect] de novo encerra a anterior.
 * [onPaired] é chamado (na thread de IO) quando um pareamento termina, com os dados a salvar.
 */
class Connection(
    private val scope: CoroutineScope,
    private val screen: ScreenInfo,
    private val pingIntervalMs: Long = 1_000,
    private val handshakeTimeoutMs: Int = 5_000,
    private val onPaired: (PairedPc) -> Unit = {},
) {
    private val _state = MutableStateFlow<ConnectionState>(ConnectionState.Disconnected)
    val state: StateFlow<ConnectionState> = _state.asStateFlow()

    private var job: Job? = null
    private var socket: Socket? = null

    fun connect(target: ConnectTarget) {
        disconnect()
        _state.value = ConnectionState.Connecting
        val s = Socket().also { socket = it } // guardado já aqui para disconnect() poder interromper o connect
        job = scope.launch(Dispatchers.IO) { run(s, target) }
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
            val s = when (target) {
                is ConnectTarget.Usb -> raw
                is ConnectTarget.Wifi -> raw.upgradeToTls(host, port, target.pc.fingerprint)
                is ConnectTarget.Pairing -> raw.upgradeToTls(host, port, target.info.fingerprint)
            }
            s.soTimeout = handshakeTimeoutMs
            val out = s.getOutputStream()
            val reader = MessageReader(s.getInputStream())

            when (target) {
                is ConnectTarget.Usb -> Unit
                is ConnectTarget.Wifi -> out.send(AuthMessage(target.pc.token))
                is ConnectTarget.Pairing -> {
                    out.send(PairMessage(target.info.secret, target.deviceName))
                    val token = when (val reply = reader.read()) {
                        is PairedMessage -> reply.token
                        is DeniedMessage -> return denied(reply.reason)
                        else -> return fail("Resposta inesperada do PC durante o pareamento")
                    }
                    onPaired(PairedPc(target.info.pcName, target.info.fingerprint, token, host))
                    out.send(AuthMessage(token))
                }
            }

            out.send(HelloMessage(MessageCodec.PROTOCOL_VERSION, screen.width, screen.height, screen.densityDpi, VideoCodec.ALL))
            val config = when (val reply = reader.read()) {
                is ConfigMessage -> reply
                is DeniedMessage -> return denied(reply.reason)
                else -> return fail("O PC recusou a conexão")
            }

            s.soTimeout = 0 // daqui em diante a leitura bloqueia até chegar um PONG ou a conexão cair
            _state.value = ConnectionState.Connected(config, rttMs = null)
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
                    val message = reader.read() ?: break
                    if (message is PongMessage) {
                        val rtt = (nowMicros() - message.timestampUs) / 1_000.0
                        _state.update { if (it is ConnectionState.Connected) it.copy(rttMs = rtt) else it }
                    }
                }
            } finally {
                pinger.cancel()
            }
            fail("Conexão perdida")
        } catch (e: IOException) { // inclui ProtocolException, EOFException e erros de TLS
            fail(e.describe())
        } finally {
            raw.closeQuietly()
        }
    }

    private fun ConnectTarget.endpoint(): kotlin.Pair<String, Int> = when (this) {
        is ConnectTarget.Usb -> "127.0.0.1" to port
        is ConnectTarget.Wifi -> address.host to address.port
        is ConnectTarget.Pairing -> info.host to info.port
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
        this is ProtocolException -> "Resposta inválida do PC: $message"
        this is SSLException && causes().any { it is CertificateException } ->
            "Este não é o PC pareado (certificado diferente). Pareie de novo."
        this is SSLException -> "Falha na conexão segura: ${message ?: "erro de TLS"}"
        else -> message?.takeIf { it.isNotBlank() } ?: "Erro de rede"
    }

    private fun Throwable.causes() = generateSequence(this) { it.cause }

    private fun OutputStream.send(message: Message) = synchronized(this) {
        write(MessageCodec.encode(message))
        flush()
    }

    private fun nowMicros() = System.nanoTime() / 1_000

    private fun Socket.closeQuietly() = try { close() } catch (_: IOException) {}
}
