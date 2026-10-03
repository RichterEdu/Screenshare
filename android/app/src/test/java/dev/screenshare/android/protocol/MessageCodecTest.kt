package dev.screenshare.android.protocol

import java.nio.ByteBuffer
import java.nio.ByteOrder
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class MessageCodecTest {
    private fun decodeVector(vector: ByteArray): Message =
        MessageCodec.decode(vector[0].toInt() and 0xFF, vector.copyOfRange(MessageCodec.HEADER_SIZE, vector.size))

    private fun assertMatchesVector(name: String, message: Message) {
        val vector = Vectors.load(name)
        assertArrayEquals(vector, MessageCodec.encode(message))
        assertEquals(message, decodeVector(vector))
    }

    private fun assertRejected(type: Int, payload: ByteArray) {
        assertThrows(ProtocolException::class.java) { MessageCodec.decode(type, payload) }
    }

    @Test
    fun hello() = assertMatchesVector("hello.hex", HelloMessage(1, 2400, 1080, 420, VideoCodec.H264 or VideoCodec.H265))

    @Test
    fun config() = assertMatchesVector(
        "config.hex",
        ConfigMessage(2400, 1080, VideoCodec.H265, 20000, byteArrayOf(0x00, 0x00, 0x00, 0x01, 0x40, 0x01, 0x0C, 0x01)),
    )

    @Test
    fun frame() = assertMatchesVector(
        "frame.hex",
        FrameMessage(1_000_000, true, byteArrayOf(0x00, 0x00, 0x00, 0x01, 0x26, 0x01, 0xAF.toByte())),
    )

    @Test
    fun touch() = assertMatchesVector(
        "touch.hex",
        TouchMessage(
            listOf(
                TouchPointer(0, TouchAction.MOVE, 0.25f, 0.5f, 1.0f),
                TouchPointer(1, TouchAction.DOWN, 0.75f, 0.125f, 0.5f),
            ),
        ),
    )

    @Test
    fun ping() = assertMatchesVector("ping.hex", PingMessage(123456789))

    @Test
    fun pong() = assertMatchesVector("pong.hex", PongMessage(123456789))

    @Test
    fun keyframeRequest() = assertMatchesVector("keyframe_req.hex", KeyframeRequestMessage)

    @Test
    fun unknownTypeIsRejected() = assertRejected(0x63, ByteArray(0))

    @Test
    fun truncatedPayloadIsRejected() = assertRejected(MessageType.HELLO, ByteArray(8))

    @Test
    fun trailingBytesAreRejected() = assertRejected(MessageType.PING, ByteArray(9))

    @Test
    fun helloWithInvalidCodecFlagsIsRejected() {
        for (flags in listOf(0, 4)) { // nenhum codec; bit desconhecido
            assertRejected(
                MessageType.HELLO,
                byteArrayOf(1, 0, 0x60, 0x09, 0x38, 0x04, 0xA4.toByte(), 0x01, flags.toByte()),
            )
        }
    }

    @Test
    fun configWithUnknownCodecIsRejected() = // codec=3 (H264|H265) não é um codec único
        assertRejected(MessageType.CONFIG, byteArrayOf(0x60, 0x09, 0x38, 0x04, 3, 0x20, 0x4E, 0, 0, 0, 0))

    @Test
    fun configWithCodecConfigLongerThanPayloadIsRejected() = // declara 8 bytes, traz 2
        assertRejected(
            MessageType.CONFIG,
            byteArrayOf(0x60, 0x09, 0x38, 0x04, 2, 0x20, 0x4E, 0, 0, 8, 0, 0xAA.toByte(), 0xBB.toByte()),
        )

    @Test
    fun frameShorterThanItsFixedFieldsIsRejected() = assertRejected(MessageType.FRAME, ByteArray(8))

    @Test
    fun touchWithInvalidPointerCountIsRejected() {
        for (count in listOf(0, 11)) {
            assertRejected(MessageType.TOUCH, ByteArray(1 + 14 * count).also { it[0] = count.toByte() })
        }
    }

    @Test
    fun touchWithUnknownActionIsRejected() =
        assertRejected(MessageType.TOUCH, ByteArray(15).also { it[0] = 1; it[2] = 9 })

    @Test
    fun touchWithNonFiniteValueIsRejected() {
        val payload = ByteBuffer.allocate(15).order(ByteOrder.LITTLE_ENDIAN)
            .put(1.toByte()).put(0.toByte()).put(0.toByte()) // count, id, action
            .putFloat(Float.NaN).putFloat(0f).putFloat(1f) // x, y, pressure
            .array()
        assertRejected(MessageType.TOUCH, payload)
    }

    @Test
    fun encodingTouchWithoutPointersIsRejected() {
        assertThrows(ProtocolException::class.java) { MessageCodec.encode(TouchMessage(emptyList())) }
    }

    @Test
    fun encodingCodecConfigOver65535BytesIsRejected() {
        assertThrows(ProtocolException::class.java) {
            MessageCodec.encode(ConfigMessage(1920, 1080, VideoCodec.H264, 8000, ByteArray(70_000)))
        }
    }
}
