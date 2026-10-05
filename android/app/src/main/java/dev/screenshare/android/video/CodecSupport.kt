package dev.screenshare.android.video

import dev.screenshare.android.protocol.VideoCodec

/** Um decoder do aparelho, como o MediaCodecList o descreve. */
class DecoderInfo(
    val name: String,
    val mime: String,
    val hardware: Boolean,
    val lowLatency: Boolean,
    private val sizeSupported: (width: Int, height: Int) -> Boolean,
) {
    fun supports(width: Int, height: Int) = sizeSupported(width, height)
}

/** Que codecs o celular decodifica de fato e qual decoder usar (regra do spike da Parte 3). */
object CodecSupport {
    const val MIME_H264 = "video/avc"
    const val MIME_H265 = "video/hevc"

    fun mimeOf(codec: Int) = if (codec == VideoCodec.H265) MIME_H265 else MIME_H264

    /**
     * O decoder para o codec no tamanho pedido: o do fabricante terminado em ".low_latency"; senão o primeiro de
     * hardware com baixa latência; senão o primeiro de hardware; senão qualquer um. null se nenhum abre esse tamanho.
     */
    fun choose(decoders: List<DecoderInfo>, codec: Int, width: Int, height: Int): DecoderInfo? {
        val candidates = decoders.filter { it.mime == mimeOf(codec) && it.supports(width, height) }
        return candidates.firstOrNull { it.name.endsWith(".low_latency") }
            ?: candidates.firstOrNull { it.hardware && it.lowLatency }
            ?: candidates.firstOrNull { it.hardware }
            ?: candidates.firstOrNull()
    }

    /**
     * Os codecs (flags do HELLO) que o celular decodifica no tamanho da tela. Se houver decoder de hardware para algum
     * codec, só os de hardware entram: o PC prefere H.265, e um H.265 só em software não aguenta 60 fps na tela inteira.
     */
    fun supported(decoders: List<DecoderInfo>, width: Int, height: Int): Int {
        val hardware = flags(decoders.filter { it.hardware }, width, height)
        return if (hardware != 0) hardware else flags(decoders, width, height)
    }

    private fun flags(decoders: List<DecoderInfo>, width: Int, height: Int): Int {
        var codecs = 0
        if (choose(decoders, VideoCodec.H264, width, height) != null) codecs = codecs or VideoCodec.H264
        if (choose(decoders, VideoCodec.H265, width, height) != null) codecs = codecs or VideoCodec.H265
        return codecs
    }
}
