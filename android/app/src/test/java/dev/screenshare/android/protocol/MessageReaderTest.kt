package dev.screenshare.android.protocol

import java.io.ByteArrayInputStream
import java.io.EOFException
import java.io.InputStream
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Test

class MessageReaderTest {
    @Test
    fun readsConsecutiveMessagesThenNullAtCleanEnd() {
        val bytes = Vectors.load("hello.hex") + Vectors.load("ping.hex") + Vectors.load("keyframe_req.hex")
        val reader = MessageReader(ByteArrayInputStream(bytes))

        assertEquals(HelloMessage(1, 2400, 1080, 420, VideoCodec.ALL), reader.read())
        assertEquals(PingMessage(123456789), reader.read())
        assertEquals(KeyframeRequestMessage, reader.read())
        assertNull(reader.read())
    }

    @Test
    fun reassemblesMessagesDeliveredOneByteAtATime() {
        val reader = MessageReader(OneByteAtATime(ByteArrayInputStream(Vectors.load("touch.hex"))))

        val touch = reader.read() as TouchMessage
        assertEquals(2, touch.pointers.size)
        assertNull(reader.read())
    }

    @Test
    fun streamEndingInsideHeaderThrowsEof() {
        val reader = MessageReader(ByteArrayInputStream(byteArrayOf(0x05, 0x08)))
        assertThrows(EOFException::class.java) { reader.read() }
    }

    @Test
    fun streamEndingInsidePayloadThrowsEof() {
        val ping = Vectors.load("ping.hex")
        val reader = MessageReader(ByteArrayInputStream(ping.copyOf(ping.size - 3)))
        assertThrows(EOFException::class.java) { reader.read() }
    }

    @Test
    fun oversizedLengthIsRejectedBeforeReadingPayload() {
        // FRAME declarando 4 GB de payload
        val reader = MessageReader(ByteArrayInputStream(byteArrayOf(0x03, -1, -1, -1, -1)))
        assertThrows(ProtocolException::class.java) { reader.read() }
    }

    /** Simula o TCP entregando um byte por leitura. */
    private class OneByteAtATime(private val inner: InputStream) : InputStream() {
        override fun read(): Int = inner.read()
        override fun read(b: ByteArray, off: Int, len: Int): Int = if (len == 0) 0 else inner.read(b, off, 1)
    }
}
