package dev.screenshare.android.protocol

import java.util.Objects

/** Código do tipo de mensagem no cabeçalho do quadro. */
object MessageType {
    const val HELLO = 1
    const val CONFIG = 2
    const val FRAME = 3
    const val TOUCH = 4
    const val PING = 5
    const val PONG = 6
    const val KEYFRAME_REQUEST = 7
}

/** Flags de codec: o HELLO carrega uma combinação; o CONFIG, exatamente um. */
object VideoCodec {
    const val H264 = 1
    const val H265 = 2
    const val ALL = H264 or H265
}

enum class TouchAction(val code: Int) { DOWN(0), MOVE(1), UP(2), CANCEL(3) }

sealed interface Message

/** Celular → PC, primeira mensagem da conexão. */
data class HelloMessage(
    val protocolVersion: Int,
    val width: Int,
    val height: Int,
    val densityDpi: Int,
    val supportedCodecs: Int,
) : Message

/** PC → celular. codecConfig: SPS/PPS (+VPS no H.265) em Annex-B; vazio se vierem dentro dos FRAMEs. */
data class ConfigMessage(
    val width: Int,
    val height: Int,
    val codec: Int,
    val bitrateKbps: Long,
    val codecConfig: ByteArray,
) : Message {
    // ByteArray compara por referência; aqui comparamos o conteúdo.
    override fun equals(other: Any?) = other is ConfigMessage &&
        width == other.width && height == other.height && codec == other.codec &&
        bitrateKbps == other.bitrateKbps && codecConfig.contentEquals(other.codecConfig)

    override fun hashCode() = Objects.hash(width, height, codec, bitrateKbps, codecConfig.contentHashCode())
}

/** PC → celular: um quadro de vídeo codificado (NAL units em Annex-B). */
data class FrameMessage(
    val timestampUs: Long,
    val isKeyframe: Boolean,
    val data: ByteArray,
) : Message {
    override fun equals(other: Any?) = other is FrameMessage &&
        timestampUs == other.timestampUs && isKeyframe == other.isKeyframe && data.contentEquals(other.data)

    override fun hashCode() = Objects.hash(timestampUs, isKeyframe, data.contentHashCode())
}

/** Um dedo na tela. x e y normalizados ao monitor virtual (0 = esquerda/topo, 1 = direita/base). */
data class TouchPointer(val id: Int, val action: TouchAction, val x: Float, val y: Float, val pressure: Float)

/** Celular → PC: estado de todos os ponteiros ativos num instante. */
data class TouchMessage(val pointers: List<TouchPointer>) : Message

/** Qualquer lado; quem recebe responde PONG com o mesmo valor. */
data class PingMessage(val timestampUs: Long) : Message

data class PongMessage(val timestampUs: Long) : Message

/** Celular → PC: pede um keyframe após erro de decodificação. */
data object KeyframeRequestMessage : Message
