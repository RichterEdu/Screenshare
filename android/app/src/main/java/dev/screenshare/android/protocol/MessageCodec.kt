package dev.screenshare.android.protocol

import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.CharBuffer
import java.nio.charset.CharacterCodingException
import java.nio.charset.CodingErrorAction

/**
 * Converte mensagens de/para quadros [type:u8][payloadLength:u32 LE][payload].
 * Layouts em docs/protocol.md; bytes de referência em docs/protocol-vectors.
 */
object MessageCodec {
    const val PROTOCOL_VERSION = 2
    const val HEADER_SIZE = 5
    const val MAX_PAYLOAD_LENGTH = 16 * 1024 * 1024
    const val MAX_TOUCH_POINTERS = 10
    const val SECRET_LENGTH = 32
    const val TOKEN_LENGTH = 32
    const val MAX_DEVICE_NAME_BYTES = 64

    private const val TOUCH_POINTER_SIZE = 14

    fun encode(message: Message): ByteArray = when (message) {
        is HelloMessage -> frame(MessageType.HELLO, 9) {
            putShort(message.protocolVersion.toShort())
            putShort(message.width.toShort())
            putShort(message.height.toShort())
            putShort(message.densityDpi.toShort())
            put(message.supportedCodecs.toByte())
        }
        is ConfigMessage -> {
            val size = message.codecConfig.size
            if (size > 0xFFFF) throw ProtocolException("codec config de $size bytes não cabe em u16")
            frame(MessageType.CONFIG, 11 + size) {
                putShort(message.width.toShort())
                putShort(message.height.toShort())
                put(message.codec.toByte())
                putInt(message.bitrateKbps.toInt())
                putShort(size.toShort())
                put(message.codecConfig)
            }
        }
        is FrameMessage -> frame(MessageType.FRAME, 9 + message.data.size) {
            putLong(message.timestampUs)
            put((if (message.isKeyframe) 1 else 0).toByte())
            put(message.data)
        }
        is TouchMessage -> {
            val count = message.pointers.size
            if (count !in 1..MAX_TOUCH_POINTERS) {
                throw ProtocolException("TOUCH precisa de 1..$MAX_TOUCH_POINTERS ponteiros, recebeu $count")
            }
            frame(MessageType.TOUCH, 1 + TOUCH_POINTER_SIZE * count) {
                put(count.toByte())
                for (p in message.pointers) {
                    put(p.id.toByte())
                    put(p.action.code.toByte())
                    putFloat(p.x)
                    putFloat(p.y)
                    putFloat(p.pressure)
                }
            }
        }
        is PairMessage -> {
            requireLength(message.secret, SECRET_LENGTH, "segredo")
            val name = encodeDeviceName(message.deviceName)
            frame(MessageType.PAIR, SECRET_LENGTH + 1 + name.size) {
                put(message.secret)
                put(name.size.toByte())
                put(name)
            }
        }
        is PairedMessage -> {
            requireLength(message.token, TOKEN_LENGTH, "chave")
            frame(MessageType.PAIRED, TOKEN_LENGTH) { put(message.token) }
        }
        is AuthMessage -> {
            requireLength(message.token, TOKEN_LENGTH, "chave")
            frame(MessageType.AUTH, TOKEN_LENGTH) { put(message.token) }
        }
        is DeniedMessage -> frame(MessageType.DENIED, 1) { put(message.reason.code.toByte()) }
        is PingMessage -> frame(MessageType.PING, 8) { putLong(message.timestampUs) }
        is PongMessage -> frame(MessageType.PONG, 8) { putLong(message.timestampUs) }
        KeyframeRequestMessage -> frame(MessageType.KEYFRAME_REQUEST, 0) {}
    }

    fun decode(type: Int, payload: ByteArray): Message {
        val r = PayloadReader(payload)
        val message = when (type) {
            MessageType.HELLO -> decodeHello(r)
            MessageType.CONFIG -> decodeConfig(r)
            MessageType.FRAME -> decodeFrame(r)
            MessageType.TOUCH -> decodeTouch(r)
            MessageType.PAIR -> decodePair(r)
            MessageType.PAIRED -> PairedMessage(r.bytes(TOKEN_LENGTH))
            MessageType.AUTH -> AuthMessage(r.bytes(TOKEN_LENGTH))
            MessageType.DENIED -> decodeDenied(r)
            MessageType.PING -> PingMessage(r.u64())
            MessageType.PONG -> PongMessage(r.u64())
            MessageType.KEYFRAME_REQUEST -> KeyframeRequestMessage
            else -> throw ProtocolException("tipo de mensagem desconhecido: 0x%02X".format(type))
        }
        r.ensureEnd()
        return message
    }

    private inline fun frame(type: Int, payloadLength: Int, write: ByteBuffer.() -> Unit): ByteArray {
        if (payloadLength > MAX_PAYLOAD_LENGTH) {
            throw ProtocolException("payload de $payloadLength bytes excede o limite de $MAX_PAYLOAD_LENGTH")
        }
        val buffer = ByteBuffer.allocate(HEADER_SIZE + payloadLength).order(ByteOrder.LITTLE_ENDIAN)
        buffer.put(type.toByte())
        buffer.putInt(payloadLength)
        buffer.write()
        return buffer.array()
    }

    private fun decodeHello(r: PayloadReader): HelloMessage {
        val version = r.u16()
        val width = r.u16()
        val height = r.u16()
        val dpi = r.u16()
        val codecs = r.u8()
        if (codecs == 0 || (codecs and VideoCodec.ALL.inv()) != 0) {
            throw ProtocolException("flags de codec inválidas: $codecs")
        }
        return HelloMessage(version, width, height, dpi, codecs)
    }

    private fun decodeConfig(r: PayloadReader): ConfigMessage {
        val width = r.u16()
        val height = r.u16()
        val codec = r.u8()
        if (codec != VideoCodec.H264 && codec != VideoCodec.H265) throw ProtocolException("codec inválido: $codec")
        val bitrate = r.u32()
        val codecConfigLength = r.u16()
        val codecConfig = r.bytes(codecConfigLength)
        return ConfigMessage(width, height, codec, bitrate, codecConfig)
    }

    private fun decodeFrame(r: PayloadReader): FrameMessage {
        val timestamp = r.u64()
        val flags = r.u8()
        return FrameMessage(timestamp, (flags and 1) != 0, r.remaining())
    }

    private fun decodeTouch(r: PayloadReader): TouchMessage {
        val count = r.u8()
        if (count !in 1..MAX_TOUCH_POINTERS) {
            throw ProtocolException("TOUCH precisa de 1..$MAX_TOUCH_POINTERS ponteiros, recebeu $count")
        }
        val pointers = List(count) {
            val id = r.u8()
            val actionCode = r.u8()
            val action = TouchAction.entries.firstOrNull { it.code == actionCode }
                ?: throw ProtocolException("ação de toque desconhecida: $actionCode")
            val x = r.f32()
            val y = r.f32()
            val pressure = r.f32()
            if (!x.isFinite() || !y.isFinite() || !pressure.isFinite()) {
                throw ProtocolException("coordenada ou pressão de toque não finita")
            }
            TouchPointer(id, action, x, y, pressure)
        }
        return TouchMessage(pointers)
    }

    private fun requireLength(value: ByteArray, length: Int, what: String) {
        if (value.size != length) throw ProtocolException("o $what precisa ter $length bytes, tem ${value.size}")
    }

    private fun encodeDeviceName(name: String): ByteArray {
        val buffer = try {
            Charsets.UTF_8.newEncoder()
                .onMalformedInput(CodingErrorAction.REPORT)
                .onUnmappableCharacter(CodingErrorAction.REPORT)
                .encode(CharBuffer.wrap(name))
        } catch (e: CharacterCodingException) {
            throw ProtocolException("nome do aparelho não é texto válido")
        }
        val bytes = ByteArray(buffer.remaining()).also { buffer.get(it) }
        if (bytes.size !in 1..MAX_DEVICE_NAME_BYTES) {
            throw ProtocolException("nome do aparelho precisa ter 1..$MAX_DEVICE_NAME_BYTES bytes, tem ${bytes.size}")
        }
        return bytes
    }

    private fun decodePair(r: PayloadReader): PairMessage {
        val secret = r.bytes(SECRET_LENGTH)
        val nameLength = r.u8()
        if (nameLength !in 1..MAX_DEVICE_NAME_BYTES) {
            throw ProtocolException("nome do aparelho precisa ter 1..$MAX_DEVICE_NAME_BYTES bytes, tem $nameLength")
        }
        val name = try {
            Charsets.UTF_8.newDecoder()
                .onMalformedInput(CodingErrorAction.REPORT)
                .onUnmappableCharacter(CodingErrorAction.REPORT)
                .decode(ByteBuffer.wrap(r.bytes(nameLength)))
                .toString()
        } catch (e: CharacterCodingException) {
            throw ProtocolException("nome do aparelho não é UTF-8 válido")
        }
        return PairMessage(secret, name)
    }

    private fun decodeDenied(r: PayloadReader): DeniedMessage {
        val code = r.u8()
        val reason = DeniedReason.entries.firstOrNull { it.code == code }
            ?: throw ProtocolException("motivo de DENIED desconhecido: $code")
        return DeniedMessage(reason)
    }
}
