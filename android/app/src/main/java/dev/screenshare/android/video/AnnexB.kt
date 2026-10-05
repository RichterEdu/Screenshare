package dev.screenshare.android.video

import dev.screenshare.android.protocol.VideoCodec
import java.io.ByteArrayOutputStream

/** Uma NAL unit dentro de um access unit: posição e tamanho sem o start code, e o tipo. */
data class NalUnit(val offset: Int, val length: Int, val type: Int)

/** Leitura de vídeo em Annex-B (start codes de 3 ou 4 bytes), igual à do PC (host/ScreenShare.Core/Video/AnnexB.cs). */
object AnnexB {
    const val H264_IDR = 5
    const val H264_SPS = 7
    const val H264_PPS = 8
    const val H265_IDR_W_RADL = 19
    const val H265_IDR_N_LP = 20
    const val H265_CRA = 21
    const val H265_VPS = 32
    const val H265_SPS = 33
    const val H265_PPS = 34

    private val START_CODE = byteArrayOf(0, 0, 0, 1)

    /** As NAL units, na ordem. Bytes antes do primeiro start code são ignorados. */
    fun split(accessUnit: ByteArray, codec: Int): List<NalUnit> {
        requireSingleCodec(codec)
        val units = mutableListOf<NalUnit>()
        var start = -1
        var i = 0
        while (i + 2 < accessUnit.size) {
            if (accessUnit[i].toInt() == 0 && accessUnit[i + 1].toInt() == 0 && accessUnit[i + 2].toInt() == 1) {
                if (start >= 0) add(units, accessUnit, start, i, codec)
                start = i + 3
                i += 3
            } else {
                i++
            }
        }
        if (start >= 0) add(units, accessUnit, start, accessUnit.size, codec)
        return units
    }

    /** O tipo da NAL pelo primeiro byte do cabeçalho (H.264: 5 bits baixos; H.265: bits 1 a 6). */
    fun nalType(header: Byte, codec: Int): Int {
        val value = header.toInt() and 0xFF
        return if (requireSingleCodec(codec) == VideoCodec.H264) value and 0x1F else (value shr 1) and 0x3F
    }

    /** O access unit tem um IDR (H.264) ou um IDR/CRA (H.265), por onde o decoder pode começar. */
    fun isKeyframe(accessUnit: ByteArray, codec: Int): Boolean = split(accessUnit, codec).any {
        if (codec == VideoCodec.H264) it.type == H264_IDR else it.type in H265_IDR_W_RADL..H265_CRA
    }

    /** Os parâmetros (H.264: SPS e PPS; H.265: VPS, SPS e PPS), cada um com start code de 4 bytes. null se faltar algum. */
    fun extractParameterSets(accessUnit: ByteArray, codec: Int): ByteArray? {
        val required = if (codec == VideoCodec.H264) setOf(H264_SPS, H264_PPS) else setOf(H265_VPS, H265_SPS, H265_PPS)
        val found = mutableSetOf<Int>()
        val output = ByteArrayOutputStream()
        for (nal in split(accessUnit, codec)) {
            if (nal.type !in required) continue
            found += nal.type
            output.write(START_CODE)
            output.write(accessUnit, nal.offset, nal.length)
        }
        return if (found == required) output.toByteArray() else null
    }

    /** A NAL com start code de 4 bytes na frente. */
    fun withStartCode(accessUnit: ByteArray, nal: NalUnit): ByteArray =
        START_CODE + accessUnit.copyOfRange(nal.offset, nal.offset + nal.length)

    private fun add(units: MutableList<NalUnit>, data: ByteArray, start: Int, end: Int, codec: Int) {
        // Uma NAL nunca termina em 0x00: os zeros antes de um start code são do start code de 4 bytes ou de preenchimento.
        var last = end
        while (last > start && data[last - 1].toInt() == 0) last--
        if (last > start) units += NalUnit(start, last - start, nalType(data[start], codec))
    }

    private fun requireSingleCodec(codec: Int): Int {
        require(codec == VideoCodec.H264 || codec == VideoCodec.H265) { "O codec precisa ser H.264 ou H.265, recebeu $codec." }
        return codec
    }
}

/** csd-0 e csd-1 do MediaFormat. H.264: csd-0 = SPS e csd-1 = PPS; H.265: csd-0 = VPS + SPS + PPS e sem csd-1. */
class Csd(val csd0: ByteArray, val csd1: ByteArray?) {
    override fun equals(other: Any?) =
        other is Csd && csd0.contentEquals(other.csd0) && (csd1?.contentEquals(other.csd1) ?: (other.csd1 == null))

    override fun hashCode() = 31 * csd0.contentHashCode() + (csd1?.contentHashCode() ?: 0)
}

object CsdBuilder {
    /** Do codecConfig do CONFIG ou de um keyframe (os parâmetros vêm dentro dele). null se faltar algum. */
    fun build(codec: Int, data: ByteArray): Csd? {
        if (codec == VideoCodec.H265) return AnnexB.extractParameterSets(data, codec)?.let { Csd(it, null) }
        val units = AnnexB.split(data, codec)
        val sps = units.firstOrNull { it.type == AnnexB.H264_SPS } ?: return null
        val pps = units.firstOrNull { it.type == AnnexB.H264_PPS } ?: return null
        return Csd(AnnexB.withStartCode(data, sps), AnnexB.withStartCode(data, pps))
    }
}
