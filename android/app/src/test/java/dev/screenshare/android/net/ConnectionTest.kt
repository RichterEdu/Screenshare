package dev.screenshare.android.net

import dev.screenshare.android.protocol.ConfigMessage
import dev.screenshare.android.protocol.HelloMessage
import dev.screenshare.android.protocol.Message
import dev.screenshare.android.protocol.MessageCodec
import dev.screenshare.android.protocol.MessageReader
import dev.screenshare.android.protocol.PingMessage
import dev.screenshare.android.protocol.PongMessage
import dev.screenshare.android.protocol.VideoCodec
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Testa a Connection contra um servidor TCP real em loopback (sem mocks de socket). */
class ConnectionTest {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val server = ServerSocket(0, 1, InetAddress.getLoopbackAddress())
    private val screen = ScreenInfo(width = 2400, height = 1080, densityDpi = 420)
    private val config = ConfigMessage(2400, 1080, VideoCodec.H264, 8000, ByteArray(0))

    @After
    fun tearDown() {
        scope.cancel()
        server.close()
    }

    private fun newConnection() = Connection(scope, screen, pingIntervalMs = 20, handshakeTimeoutMs = 2_000)

    private fun Connection.connectToServer() = connect(server.inetAddress.hostAddress!!, server.localPort)

    private fun Socket.send(message: Message) = getOutputStream().apply { write(MessageCodec.encode(message)); flush() }

    private suspend fun Connection.await(predicate: (ConnectionState) -> Boolean): ConnectionState =
        withTimeout(5_000) { state.first(predicate) }

    /** Roda o lado servidor numa thread de IO enquanto o teste dirige o cliente. */
    private fun serving(block: (Socket, MessageReader) -> Unit) = scope.async {
        server.accept().use { socket -> block(socket, MessageReader(socket.getInputStream())) }
    }

    @Test
    fun handshakeSendsHelloWithScreenInfoAndEndsConnected() = runBlocking {
        var received: Message? = null
        val serverSide = serving { socket, reader ->
            received = reader.read()
            socket.send(config)
            reader.read() // mantém a conexão aberta até o cliente pingar
        }
        val connection = newConnection()

        connection.connectToServer()
        val state = connection.await { it is ConnectionState.Connected }

        assertEquals(config, (state as ConnectionState.Connected).config)
        assertEquals(HelloMessage(MessageCodec.PROTOCOL_VERSION, 2400, 1080, 420, VideoCodec.ALL), received)
        connection.disconnect()
        serverSide.await()
    }

    @Test
    fun pongsProduceARoundTripTime() = runBlocking {
        val serverSide = serving { socket, reader ->
            reader.read() // HELLO
            socket.send(config)
            while (true) {
                val ping = reader.read() as? PingMessage ?: break
                socket.send(PongMessage(ping.timestampUs))
            }
        }
        val connection = newConnection()

        connection.connectToServer()
        val state = connection.await { it is ConnectionState.Connected && it.rttMs != null }

        val rtt = (state as ConnectionState.Connected).rttMs
        assertNotNull(rtt)
        assertTrue("rtt inesperado: $rtt", rtt!! >= 0.0 && rtt < 1_000.0)
        connection.disconnect()
        serverSide.await()
    }

    @Test
    fun serverClosingBeforeConfigFails() = runBlocking {
        val serverSide = serving { _, reader -> reader.read() } // lê o HELLO e fecha
        val connection = newConnection()

        connection.connectToServer()
        val state = connection.await { it is ConnectionState.Failed }

        assertTrue(state is ConnectionState.Failed)
        serverSide.await()
    }

    @Test
    fun serverDroppingAfterConnectedFails() = runBlocking {
        val serverSide = serving { socket, reader ->
            reader.read()
            socket.send(config)
        } // sai do bloco e fecha o socket
        val connection = newConnection()

        connection.connectToServer()
        val state = connection.await { it is ConnectionState.Failed }

        assertEquals("Conexão perdida", (state as ConnectionState.Failed).reason)
        serverSide.await()
    }

    @Test
    fun disconnectEndsInDisconnectedAndServerSeesTheClose() = runBlocking {
        var serverSawClose = false
        val serverSide = serving { socket, reader ->
            reader.read()
            socket.send(config)
            while (reader.read() != null) { /* ignora PINGs até o cliente fechar */ }
            serverSawClose = true
        }
        val connection = newConnection()
        connection.connectToServer()
        connection.await { it is ConnectionState.Connected }

        connection.disconnect()

        assertEquals(ConnectionState.Disconnected, connection.state.value)
        serverSide.await()
        assertTrue(serverSawClose)
    }

    @Test
    fun connectionRefusedFails() = runBlocking {
        val port = server.localPort
        server.close() // nada escutando nessa porta
        val connection = newConnection()

        connection.connect("127.0.0.1", port)
        val state = connection.await { it is ConnectionState.Failed }

        assertTrue((state as ConnectionState.Failed).reason.isNotBlank())
    }
}
