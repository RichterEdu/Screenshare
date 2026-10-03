package dev.screenshare.android.net

import dev.screenshare.android.protocol.ConfigMessage
import dev.screenshare.android.protocol.HelloMessage
import dev.screenshare.android.protocol.Message
import dev.screenshare.android.protocol.MessageCodec
import dev.screenshare.android.protocol.MessageReader
import dev.screenshare.android.protocol.PingMessage
import dev.screenshare.android.protocol.PongMessage
import dev.screenshare.android.protocol.ProtocolException
import dev.screenshare.android.protocol.VideoCodec
import java.io.IOException
import java.io.OutputStream
import java.net.InetSocketAddress
import java.net.Socket
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

    data class Failed(val reason: String) : ConnectionState
}

/**
 * Conexão TCP com o host: HELLO → CONFIG, depois PING periódico para medir a latência.
 * Uma conexão por vez; chamar [connect] de novo encerra a anterior.
 */
class Connection(
    private val scope: CoroutineScope,
    private val screen: ScreenInfo,
    private val pingIntervalMs: Long = 1_000,
    private val handshakeTimeoutMs: Int = 5_000,
) {
    private val _state = MutableStateFlow<ConnectionState>(ConnectionState.Disconnected)
    val state: StateFlow<ConnectionState> = _state.asStateFlow()

    private var job: Job? = null
    private var socket: Socket? = null

    fun connect(host: String, port: Int) {
        disconnect()
        _state.value = ConnectionState.Connecting
        val s = Socket().also { socket = it } // guardado já aqui para disconnect() poder interromper o connect
        job = scope.launch(Dispatchers.IO) { run(s, host, port) }
    }

    fun disconnect() {
        job?.cancel()
        job = null
        socket?.closeQuietly() // destrava a leitura bloqueante
        socket = null
        _state.value = ConnectionState.Disconnected
    }

    private suspend fun run(s: Socket, host: String, port: Int) {
        try {
            s.tcpNoDelay = true
            s.connect(InetSocketAddress(host, port), handshakeTimeoutMs)
            s.soTimeout = handshakeTimeoutMs
            val out = s.getOutputStream()
            val reader = MessageReader(s.getInputStream())

            out.send(HelloMessage(MessageCodec.PROTOCOL_VERSION, screen.width, screen.height, screen.densityDpi, VideoCodec.ALL))
            val config = reader.read() as? ConfigMessage
            if (config == null) {
                fail("O PC recusou a conexão (versão incompatível?)")
                return
            }

            s.soTimeout = 0 // daqui em diante a leitura bloqueia até chegar um PONG ou a conexão cair
            _state.value = ConnectionState.Connected(config, rttMs = null)
            val pinger = scope.launch(Dispatchers.IO) {
                while (isActive) {
                    delay(pingIntervalMs)
                    try {
                        out.send(PingMessage(nowMicros()))
                    } catch (_: IOException) {
                        s.closeQuietly() // a leitura abaixo falha e trata o erro
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
        } catch (e: IOException) { // inclui ProtocolException e EOFException
            fail(e.describe())
        } finally {
            s.closeQuietly()
        }
    }

    /** Só publica a falha se esta conexão ainda é a atual: disconnect() e um novo connect() cancelam a corrotina antiga. */
    private suspend fun fail(reason: String) {
        if (currentCoroutineContext().isActive) _state.value = ConnectionState.Failed(reason)
    }

    private fun IOException.describe() = when (this) {
        is ProtocolException -> "Resposta inválida do PC: $message"
        else -> message?.takeIf { it.isNotBlank() } ?: "Erro de rede"
    }

    private fun OutputStream.send(message: Message) = synchronized(this) {
        write(MessageCodec.encode(message))
        flush()
    }

    private fun nowMicros() = System.nanoTime() / 1_000

    private fun Socket.closeQuietly() = try { close() } catch (_: IOException) {}
}
