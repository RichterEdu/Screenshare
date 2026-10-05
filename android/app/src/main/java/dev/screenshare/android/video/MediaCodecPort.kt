package dev.screenshare.android.video

import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaCodecList
import android.media.MediaFormat
import android.os.Build
import android.os.Handler
import android.util.Log
import android.view.Surface
import java.nio.ByteBuffer

/** Os decoders H.264/H.265 do aparelho, para escolher o de menor latência e dizer ao PC o que o celular decodifica. */
object MediaCodecDecoders {
    fun list(): List<DecoderInfo> = MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos
        .filter { !it.isEncoder }
        .flatMap { info ->
            info.supportedTypes
                .filter { it == CodecSupport.MIME_H264 || it == CodecSupport.MIME_H265 }
                .map { mime ->
                    val capabilities = info.getCapabilitiesForType(mime)
                    DecoderInfo(
                        name = info.name,
                        mime = mime,
                        hardware = info.isHardwareAccelerated,
                        lowLatency = Build.VERSION.SDK_INT >= Build.VERSION_CODES.R &&
                            capabilities.isFeatureSupported(MediaCodecInfo.CodecCapabilities.FEATURE_LowLatency),
                    ) { width, height ->
                        val video = capabilities.videoCapabilities
                        video != null && (video.isSizeSupported(width, height) || video.isSizeSupported(height, width))
                    }
                }
        }
}

/**
 * O [CodecPort] com MediaCodec em modo assíncrono: os callbacks chegam no [handler] (a thread do vídeo, a mesma do
 * DecoderCore). Baixa latência: KEY_LOW_LATENCY (API 30+), prioridade de tempo real e as chaves do fabricante; cada
 * quadro é mostrado assim que sai do decoder.
 */
class MediaCodecPort(
    private val handler: Handler,
    private val surface: () -> Surface?,
    private val decoders: () -> List<DecoderInfo>,
    private val listener: Listener,
) : CodecPort {
    interface Listener {
        fun onInput(generation: Int, index: Int, capacity: Int)
        fun onOutput(generation: Int, index: Int)
        fun onError(generation: Int)

        /** [ptsUs] = timestamp do quadro (relógio do PC); [renderNs] = quando apareceu (System.nanoTime). */
        fun onRendered(ptsUs: Long, renderNs: Long)
    }

    private var codec: MediaCodec? = null
    private var generation = 0

    override fun start(generation: Int, codec: Int, width: Int, height: Int, csd: Csd): Boolean {
        val target = surface() ?: return false
        val info = CodecSupport.choose(decoders(), codec, width, height) ?: return false
        var decoder: MediaCodec? = null
        return try {
            decoder = MediaCodec.createByCodecName(info.name)
            decoder.setCallback(callback(generation), handler)
            decoder.setOnFrameRenderedListener({ _, ptsUs, renderNs -> listener.onRendered(ptsUs, renderNs) }, handler)
            decoder.configure(format(info, codec, width, height, csd), target, null, 0)
            decoder.start()
            this.codec = decoder
            this.generation = generation
            true
        } catch (e: Exception) { // formato recusado, superfície inválida, decoder ocupado
            Log.w(TAG, "Não foi possível abrir ${info.name}", e)
            decoder?.release()
            false
        }
    }

    override fun queue(index: Int, data: ByteArray, ptsUs: Long) {
        val decoder = codec ?: return
        try {
            val buffer = decoder.getInputBuffer(index) ?: return
            buffer.clear()
            buffer.put(data)
            decoder.queueInputBuffer(index, 0, data.size, ptsUs, 0)
        } catch (e: IllegalStateException) {
            fail(e)
        }
    }

    override fun render(index: Int) {
        try {
            codec?.releaseOutputBuffer(index, System.nanoTime())
        } catch (e: IllegalStateException) {
            fail(e)
        }
    }

    override fun release() {
        val decoder = codec ?: return
        codec = null
        try {
            decoder.stop()
        } catch (_: IllegalStateException) {
            // já em erro: o release abaixo resolve
        }
        decoder.release()
    }

    /** O decoder entrou em erro numa chamada nossa: avisa o DecoderCore depois, como um callback de erro. */
    private fun fail(error: Exception) {
        Log.w(TAG, "Erro no decoder", error)
        val failed = generation
        handler.post { listener.onError(failed) }
    }

    private fun callback(generation: Int) = object : MediaCodec.Callback() {
        override fun onInputBufferAvailable(codec: MediaCodec, index: Int) {
            val capacity = try {
                codec.getInputBuffer(index)?.capacity() ?: 0
            } catch (_: IllegalStateException) {
                return
            }
            listener.onInput(generation, index, capacity)
        }

        override fun onOutputBufferAvailable(codec: MediaCodec, index: Int, info: MediaCodec.BufferInfo) =
            listener.onOutput(generation, index)

        override fun onError(codec: MediaCodec, e: MediaCodec.CodecException) {
            Log.w(TAG, "Erro no decoder", e)
            listener.onError(generation)
        }

        override fun onOutputFormatChanged(codec: MediaCodec, format: MediaFormat) = Unit
    }

    private fun format(info: DecoderInfo, codec: Int, width: Int, height: Int, csd: Csd) =
        MediaFormat.createVideoFormat(CodecSupport.mimeOf(codec), width, height).apply {
            setByteBuffer("csd-0", ByteBuffer.wrap(csd.csd0))
            csd.csd1?.let { setByteBuffer("csd-1", ByteBuffer.wrap(it)) }
            setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, maxOf(1 shl 20, width * height))
            setInteger(MediaFormat.KEY_PRIORITY, 0) // tempo real
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R && info.lowLatency) setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
            // Chaves de baixa latência dos fabricantes (spike: o celular de teste é Qualcomm).
            if (info.name.startsWith("c2.qti") || info.name.startsWith("OMX.qcom")) setInteger("vendor.qti-ext-dec-low-latency.enable", 1)
            if (info.name.contains("exynos", ignoreCase = true)) setInteger("vendor.rtc-ext-dec-low-latency.enable", 1)
        }

    private companion object {
        const val TAG = "ScreenShare"
    }
}
