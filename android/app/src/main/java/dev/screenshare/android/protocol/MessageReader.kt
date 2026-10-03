package dev.screenshare.android.protocol

import java.io.EOFException
import java.io.InputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder

/** Lê mensagens completas de um stream (ex.: socket TCP), remontando quadros entregues em pedaços. Bloqueante. */
class MessageReader(private val input: InputStream) {
    private val header = ByteArray(MessageCodec.HEADER_SIZE)

    /**
     * Próxima mensagem, ou null se o stream terminou exatamente entre mensagens.
     * @throws EOFException o stream terminou no meio de uma mensagem.
     * @throws ProtocolException os bytes recebidos violam o protocolo.
     */
    fun read(): Message? {
        val headerRead = readFully(header)
        if (headerRead == 0) return null
        if (headerRead < header.size) throw EOFException("stream terminou dentro do cabeçalho de uma mensagem")

        val length = ByteBuffer.wrap(header, 1, 4).order(ByteOrder.LITTLE_ENDIAN).int.toLong() and 0xFFFFFFFFL
        if (length > MessageCodec.MAX_PAYLOAD_LENGTH) {
            throw ProtocolException("payload de $length bytes excede o limite de ${MessageCodec.MAX_PAYLOAD_LENGTH}")
        }

        val payload = ByteArray(length.toInt())
        if (readFully(payload) < payload.size) throw EOFException("stream terminou dentro do payload de uma mensagem")
        return MessageCodec.decode(header[0].toInt() and 0xFF, payload)
    }

    /** Lê até encher [buffer] ou o stream acabar; devolve quantos bytes leu. */
    private fun readFully(buffer: ByteArray): Int {
        var total = 0
        while (total < buffer.size) {
            val n = input.read(buffer, total, buffer.size - total)
            if (n < 0) break
            total += n
        }
        return total
    }
}
