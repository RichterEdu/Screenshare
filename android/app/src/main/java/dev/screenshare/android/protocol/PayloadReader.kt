package dev.screenshare.android.protocol

import java.nio.ByteBuffer
import java.nio.ByteOrder

/** Lê campos little-endian em sequência; falta ou sobra de bytes vira ProtocolException. */
internal class PayloadReader(payload: ByteArray) {
    private val buffer = ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN)

    fun u8(): Int { need(1); return buffer.get().toInt() and 0xFF }
    fun u16(): Int { need(2); return buffer.short.toInt() and 0xFFFF }
    fun u32(): Long { need(4); return buffer.int.toLong() and 0xFFFFFFFFL }
    fun u64(): Long { need(8); return buffer.long }
    fun f32(): Float { need(4); return buffer.float }
    fun bytes(count: Int): ByteArray { need(count); return ByteArray(count).also { buffer.get(it) } }
    fun remaining(): ByteArray = bytes(buffer.remaining())

    fun ensureEnd() {
        if (buffer.hasRemaining()) throw ProtocolException("${buffer.remaining()} bytes inesperados no fim do payload")
    }

    private fun need(count: Int) {
        if (buffer.remaining() < count) throw ProtocolException("payload truncado")
    }
}
