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
import dev.screenshare.android.protocol.VideoCodec
import dev.screenshare.android.security.PairedPc
import dev.screenshare.android.security.TestTls
import dev.screenshare.android.video.VideoSink
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
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Testa a Connection contra servidores TLS reais em loopback, com certificado de teste (Wi-Fi e USB usam o mesmo fluxo). */
class ConnectionTest {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val tlsServer = TestTls.serverSocket("host")
    private val screen = ScreenInfo(width = 2400, height = 1080, densityDpi = 420)
    private val config = ConfigMessage(2400, 1080, VideoCodec.H264, 8000, ByteArray(0))
    private val hostFingerprint = TestTls.fingerprint("host")
    private val paired = mutableListOf<PairedPc>()
    private val video = RecordingSink()

    @After
    fun tearDown() {
        scope.cancel()
        tlsServer.close()
    }

    private fun newConnection(idleTimeoutMs: Int = 5_000, currentScreen: () -> ScreenInfo = { screen }) = Connection(
        scope, currentScreen, pingIntervalMs = 20, handshakeTimeoutMs = 2_000, idleTimeoutMs = idleTimeoutMs, sink = video,
        onPaired = { synchronized(paired) { paired.add(it) } },
    )

    /** Guarda o que a conexão entregou ao vídeo. */
    private class RecordingSink : VideoSink {
        val received = mutableListOf<Message>()

        override fun onConfig(config: ConfigMessage) {
            synchronized(received) { received.add(config) }
        }

        override fun onFrame(frame: FrameMessage) {
            synchronized(received) { received.add(frame) }
        }

        fun snapshot(): List<Message> = synchronized(received) { received.toList() }
    }

    /** Espera o vídeo receber [count] mensagens (até 5 s). */
    private suspend fun RecordingSink.awaitCount(count: Int): List<Message> = withTimeout(5_000) {
        while (snapshot().size < count) kotlinx.coroutines.delay(10)
        snapshot()
    }

    /** Lê do cliente até chegar uma mensagem que não seja o PING periódico dele. */
    private fun MessageReader.readSkippingPings(): Message? {
        while (true) {
            val message = read()
            if (message !is PingMessage) return message
        }
    }

    private fun usb() = ConnectTarget.Usb(PairedPc("PC de teste", hostFingerprint, TOKEN, null), port = tlsServer.localPort)

    private fun wifi(fingerprint: String = hostFingerprint) = ConnectTarget.Wifi(
        HostAddress("127.0.0.1", tlsServer.localPort), PairedPc("PC de teste", fingerprint, TOKEN, null),
    )

    private fun pairing() = ConnectTarget.Pairing(
        PairingInfo("127.0.0.1", tlsServer.localPort, hostFingerprint, SECRET, "PC de teste"), "Pixel 8",
    )

    private fun Socket.send(message: Message) = getOutputStream().apply { write(MessageCodec.encode(message)); flush() }

    private suspend fun Connection.await(predicate: (ConnectionState) -> Boolean): ConnectionState =
        withTimeout(5_000) { state.first(predicate) }

    /** Lado PC (Wi-Fi e USB): TLS com o certificado "host". */
    private fun servingTls(block: (Socket, MessageReader) -> Unit) = scope.async {
        tlsServer.accept().use { socket -> block(socket, MessageReader(socket.getInputStream())) }
    }

    private fun hello() = HelloMessage(MessageCodec.PROTOCOL_VERSION, 2400, 1080, 420, VideoCodec.ALL)

    @Test
    fun usbHandshakeSendsHelloWithScreenInfoAndEndsConnected() = runBlocking {
        var received: Message? = null
        val serverSide = servingTls { socket, reader ->
            assertEquals(AuthMessage(TOKEN), reader.read())
            received = reader.read()
            socket.send(config)
            reader.read() // mantém a conexão aberta até o cliente pingar
        }
        val connection = newConnection()

        connection.connect(usb())
        val state = connection.await { it is ConnectionState.Connected }

        assertEquals(config, (state as ConnectionState.Connected).config)
        assertEquals(hello(), received)
        connection.disconnect()
        serverSide.await()
    }

    @Test
    fun pongsProduceARoundTripTime() = runBlocking {
        val serverSide = servingTls { socket, reader ->
            reader.read() // AUTH
            reader.read() // HELLO
            socket.send(config)
            while (true) {
                val ping = reader.read() as? PingMessage ?: break
                socket.send(PongMessage(ping.timestampUs))
            }
        }
        val connection = newConnection()

        connection.connect(usb())
        val state = connection.await { it is ConnectionState.Connected && it.rttMs != null }

        val rtt = (state as ConnectionState.Connected).rttMs
        assertNotNull(rtt)
        assertTrue("rtt inesperado: $rtt", rtt!! >= 0.0 && rtt < 1_000.0)
        connection.disconnect()
        serverSide.await()
    }

    @Test
    fun serverClosingBeforeConfigFails() = runBlocking {
        val serverSide = servingTls { _, reader -> reader.read(); reader.read() } // lê AUTH e HELLO e fecha
        val connection = newConnection()

        connection.connect(usb())
        val state = connection.await { it is ConnectionState.Failed }

        assertTrue(state is ConnectionState.Failed)
        serverSide.await()
    }

    @Test
    fun serverDroppingAfterConnectedFails() = runBlocking {
        val serverSide = servingTls { socket, reader ->
            reader.read() // AUTH
            reader.read() // HELLO
            socket.send(config)
        } // sai do bloco e fecha o socket
        val connection = newConnection()

        connection.connect(usb())
        val state = connection.await { it is ConnectionState.Failed }

        assertEquals("Conexão perdida", (state as ConnectionState.Failed).reason)
        serverSide.await()
    }

    @Test
    fun disconnectEndsInDisconnectedAndServerSeesTheClose() = runBlocking {
        var serverSawClose = false
        val serverSide = servingTls { socket, reader ->
            reader.read() // AUTH
            reader.read() // HELLO
            socket.send(config)
            while (reader.read() != null) { /* ignora PINGs até o cliente fechar */ }
            serverSawClose = true
        }
        val connection = newConnection()
        connection.connect(usb())
        connection.await { it is ConnectionState.Connected }

        connection.disconnect()

        assertEquals(ConnectionState.Disconnected, connection.state.value)
        serverSide.await()
        assertTrue(serverSawClose)
    }

    @Test
    fun connectionRefusedFails() = runBlocking {
        val port = tlsServer.localPort
        tlsServer.close() // nada escutando nessa porta
        val connection = newConnection()

        connection.connect(ConnectTarget.Usb(PairedPc("PC de teste", hostFingerprint, TOKEN, null), port))
        val state = connection.await { it is ConnectionState.Failed }

        assertTrue((state as ConnectionState.Failed).reason.startsWith("Não foi possível conectar ao PC."))
    }

    @Test
    fun wifiSendsAuthWithTheTokenThenHello() = runBlocking {
        val received = mutableListOf<Message?>()
        val serverSide = servingTls { socket, reader ->
            received += reader.read()
            received += reader.read()
            socket.send(config)
            reader.read()
        }
        val connection = newConnection()

        connection.connect(wifi())
        connection.await { it is ConnectionState.Connected }

        assertEquals(listOf(AuthMessage(TOKEN), hello()), received)
        connection.disconnect()
        serverSide.await()
    }

    @Test
    fun pairingSendsTheSecretReportsThePairedPcAndAuthenticates() = runBlocking {
        val serverSide = servingTls { socket, reader ->
            assertEquals(PairMessage(SECRET, "Pixel 8"), reader.read())
            socket.send(PairedMessage(TOKEN))
            assertEquals(AuthMessage(TOKEN), reader.read())
            assertEquals(hello(), reader.read())
            socket.send(config)
            reader.read()
        }
        val connection = newConnection()

        connection.connect(pairing())
        connection.await { it is ConnectionState.Connected }

        assertEquals(listOf(PairedPc("PC de teste", hostFingerprint, TOKEN, "127.0.0.1")), synchronized(paired) { paired.toList() })
        connection.disconnect()
        serverSide.await()
    }

    @Test
    fun unknownDeviceIsReportedWithItsReason() = runBlocking {
        val serverSide = servingTls { socket, reader ->
            reader.read() // AUTH
            socket.send(DeniedMessage(DeniedReason.UNKNOWN_DEVICE))
        }
        val connection = newConnection()

        connection.connect(wifi())
        val state = connection.await { it is ConnectionState.Failed } as ConnectionState.Failed

        assertEquals(DeniedReason.UNKNOWN_DEVICE, state.denied)
        serverSide.await()
    }

    @Test
    fun invalidPairingSecretIsReportedAndNothingIsStored() = runBlocking {
        val serverSide = servingTls { socket, reader ->
            reader.read() // PAIR
            socket.send(DeniedMessage(DeniedReason.INVALID_PAIRING_SECRET))
        }
        val connection = newConnection()

        connection.connect(pairing())
        val state = connection.await { it is ConnectionState.Failed } as ConnectionState.Failed

        assertEquals(DeniedReason.INVALID_PAIRING_SECRET, state.denied)
        assertTrue(synchronized(paired) { paired.isEmpty() })
        serverSide.await()
    }

    @Test
    fun usbDeniedForIncompatibleVersionIsReported() = runBlocking {
        val serverSide = servingTls { socket, reader ->
            reader.read() // AUTH
            reader.read() // HELLO
            socket.send(DeniedMessage(DeniedReason.INCOMPATIBLE_VERSION))
        }
        val connection = newConnection()

        connection.connect(usb())
        val state = connection.await { it is ConnectionState.Failed } as ConnectionState.Failed

        assertEquals(DeniedReason.INCOMPATIBLE_VERSION, state.denied)
        serverSide.await()
    }

    @Test
    fun differentCertificateFailsWithoutSendingTheToken() = runBlocking {
        var received: Result<Message?>? = null
        val serverSide = servingTls { _, reader -> received = runCatching { reader.read() } }
        val connection = newConnection()

        connection.connect(wifi(fingerprint = TestTls.fingerprint("other")))
        val state = connection.await { it is ConnectionState.Failed } as ConnectionState.Failed

        assertTrue(state.reason, state.reason.contains("não é o PC pareado"))
        assertNull(state.denied)
        serverSide.await()
        assertTrue("o PC recebeu dados: $received", received!!.isFailure || received!!.getOrNull() == null)
    }

    @Test
    fun serverThatNeverAnswersHelloFailsWithATimeoutMessageInPortuguese() = runBlocking {
        val serverSide = servingTls { _, reader ->
            reader.read() // AUTH
            reader.read() // HELLO
            Thread.sleep(3_000) // nunca manda o CONFIG (> handshakeTimeoutMs)
        }
        val connection = newConnection()

        connection.connect(usb())
        val state = connection.await { it is ConnectionState.Failed } as ConnectionState.Failed

        assertEquals(
            "O PC não respondeu a tempo. Se a conexão anterior caiu agora, espere uns 10 segundos e tente de novo.",
            state.reason,
        )
        serverSide.await()
    }

    @Test
    fun usbSendsAuthOverTlsThenHello() = runBlocking {
        val received = mutableListOf<Message?>()
        val serverSide = servingTls { socket, reader ->
            received += reader.read()
            received += reader.read()
            socket.send(config)
            reader.read()
        }
        val connection = newConnection()

        connection.connect(usb())
        connection.await { it is ConnectionState.Connected }

        assertEquals(listOf(AuthMessage(TOKEN), hello()), received)
        connection.disconnect()
        serverSide.await()
    }

    @Test
    fun usbWithDifferentCertificateFailsWithoutSendingTheToken() = runBlocking {
        val otherServer = TestTls.serverSocket("other")
        try {
            var received: Result<Message?>? = null
            val serverSide = scope.async {
                otherServer.accept().use { socket -> received = runCatching { MessageReader(socket.getInputStream()).read() } }
            }
            val connection = newConnection()

            connection.connect(ConnectTarget.Usb(PairedPc("PC de teste", hostFingerprint, TOKEN, null), port = otherServer.localPort))
            val state = connection.await { it is ConnectionState.Failed } as ConnectionState.Failed

            assertTrue(state.reason, state.reason.contains("não é o PC pareado"))
            assertNull(state.denied)
            serverSide.await()
            assertTrue("o PC recebeu dados: $received", received!!.isFailure || received!!.getOrNull() == null)
        } finally {
            otherServer.close()
        }
    }

    @Test
    fun usbToPlainTcpSquatterFailsWithoutSendingTheToken() = runBlocking {
        val squatter = java.net.ServerSocket(0, 1, java.net.InetAddress.getLoopbackAddress())
        try {
            val received = java.io.ByteArrayOutputStream()
            val serverSide = scope.async {
                squatter.accept().use { socket ->
                    socket.soTimeout = 500
                    val buffer = ByteArray(4096)
                    try {
                        while (true) {
                            val n = socket.getInputStream().read(buffer)
                            if (n < 0) break
                            received.write(buffer, 0, n)
                        }
                    } catch (_: java.net.SocketTimeoutException) {
                    } catch (_: java.io.IOException) {
                    }
                }
            }
            val connection = newConnection()

            connection.connect(ConnectTarget.Usb(PairedPc("PC de teste", hostFingerprint, TOKEN, null), port = squatter.localPort))
            connection.await { it is ConnectionState.Failed }
            serverSide.await()

            val bytes = received.toByteArray()
            val leaked = (0..bytes.size - TOKEN.size).any { i -> TOKEN.indices.all { bytes[i + it] == TOKEN[it] } }
            assertTrue("a chave vazou para um servidor sem TLS", !leaked)
        } finally {
            squatter.close()
        }
    }

    @Test
    fun serverClosingDuringTlsHandshakeShowsCannotConnectMessage() = runBlocking {
        // adb reverse ativo, mas o ScreenShare do PC fechado: o adbd aceita e fecha na hora.
        val closer = java.net.ServerSocket(0, 1, java.net.InetAddress.getLoopbackAddress())
        try {
            val serverSide = scope.async { closer.accept().close() }
            val connection = newConnection()

            connection.connect(ConnectTarget.Usb(PairedPc("PC de teste", hostFingerprint, TOKEN, null), port = closer.localPort))
            val state = connection.await { it is ConnectionState.Failed }
            serverSide.await()

            val reason = (state as ConnectionState.Failed).reason
            assertTrue(reason, reason.startsWith("Não foi possível conectar ao PC."))
        } finally {
            closer.close()
        }
    }

    @Test
    fun pairingOverUsbConnectsToLoopbackUsbPort() = runBlocking {
        val serverSide = servingTls { socket, reader ->
            assertEquals(PairMessage(SECRET, "Pixel 8"), reader.read())
            socket.send(PairedMessage(TOKEN))
            assertEquals(AuthMessage(TOKEN), reader.read())
            assertEquals(hello(), reader.read())
            socket.send(config)
            reader.read()
        }
        val connection = newConnection()
        val target = ConnectTarget.Pairing(
            PairingInfo("192.168.0.10", DEFAULT_PORT, hostFingerprint, SECRET, "PC de teste"), "Pixel 8",
            overUsb = true, usbPort = tlsServer.localPort,
        )

        connection.connect(target)
        connection.await { it is ConnectionState.Connected }

        assertEquals(listOf(PairedPc("PC de teste", hostFingerprint, TOKEN, "192.168.0.10")), synchronized(paired) { paired.toList() })
        connection.disconnect()
        serverSide.await()
    }

    @Test
    fun pcPingIsAnsweredWithAPongOfTheSameValue() = runBlocking {
        var answer: Message? = null
        val serverSide = servingTls { socket, reader ->
            reader.read() // AUTH
            reader.read() // HELLO
            socket.send(config)
            socket.send(PingMessage(123_456_789))
            while (true) {
                val message = reader.read() ?: break
                if (message is PongMessage && message.timestampUs == 123_456_789L) {
                    answer = message
                    break
                }
            }
        }
        val connection = newConnection()

        connection.connect(usb())
        serverSide.await()

        assertEquals(PongMessage(123_456_789), answer)
        connection.disconnect()
    }

    @Test
    fun configAndFramesReachTheVideoInOrderAndANewConfigUpdatesTheState() = runBlocking {
        val newConfig = ConfigMessage(1920, 1080, VideoCodec.H265, 25_000, byteArrayOf(0, 0, 0, 1, 0x40, 0x01))
        val key = FrameMessage(10, true, byteArrayOf(0, 0, 0, 1, 0x26, 0x01))
        val p = FrameMessage(20, false, byteArrayOf(0, 0, 0, 1, 0x02, 0x01))
        val serverSide = servingTls { socket, reader ->
            reader.read() // AUTH
            reader.read() // HELLO
            socket.send(config)
            socket.send(key)
            socket.send(p)
            socket.send(newConfig)
            while (reader.read() != null) { /* até o cliente fechar */ }
        }
        val connection = newConnection()

        connection.connect(usb())
        val received = video.awaitCount(4)
        val state = connection.await { it is ConnectionState.Connected && it.config == newConfig }

        assertEquals(listOf(config, key, p, newConfig), received)
        assertEquals(newConfig, (state as ConnectionState.Connected).config)
        connection.disconnect()
        serverSide.await()
    }

    @Test
    fun requestKeyframeSendsTheMessage() = runBlocking {
        var request: Message? = null
        val serverSide = servingTls { socket, reader ->
            reader.read() // AUTH
            reader.read() // HELLO
            socket.send(config)
            request = reader.readSkippingPings()
        }
        val connection = newConnection()
        connection.connect(usb())
        connection.await { it is ConnectionState.Connected }

        connection.requestKeyframe()
        serverSide.await()

        assertEquals(KeyframeRequestMessage, request)
        connection.disconnect()
    }

    @Test
    fun silentPcAfterTheConfigIsDroppedAfterTheIdleTimeout() = runBlocking {
        val serverSide = servingTls { socket, reader ->
            reader.read() // AUTH
            reader.read() // HELLO
            socket.send(config)
            Thread.sleep(1_500) // não responde PING nem manda nada
        }
        val connection = newConnection(idleTimeoutMs = 300)

        connection.connect(usb())
        val state = connection.await { it is ConnectionState.Failed } as ConnectionState.Failed

        assertTrue(state.reason, state.reason.startsWith("O PC parou de responder"))
        serverSide.await()
    }

    @Test
    fun helloCarriesTheScreenAndCodecsReadAtConnectTime() = runBlocking {
        var received: Message? = null
        val serverSide = servingTls { socket, reader ->
            reader.read() // AUTH
            received = reader.read()
            socket.send(config)
            reader.read()
        }
        var current = ScreenInfo(2520, 1080, 432, VideoCodec.H264)
        val connection = newConnection(currentScreen = { current })
        current = ScreenInfo(2504, 2256, 432, VideoCodec.H264) // a tela mudou antes de conectar (dobrável aberto)

        connection.connect(usb())
        connection.await { it is ConnectionState.Connected }

        assertEquals(HelloMessage(MessageCodec.PROTOCOL_VERSION, 2504, 2256, 432, VideoCodec.H264), received)
        connection.disconnect()
        serverSide.await()
    }

    private companion object {
        val SECRET = ByteArray(32) { it.toByte() }
        val TOKEN = ByteArray(32) { (0xA0 + it).toByte() }
    }
}
