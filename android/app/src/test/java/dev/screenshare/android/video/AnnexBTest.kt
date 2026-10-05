package dev.screenshare.android.video

import dev.screenshare.android.protocol.Vectors
import dev.screenshare.android.protocol.VideoCodec
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Os mesmos casos do AnnexBTests.cs, com os IDRs reais do encoder do PC. */
class AnnexBTest {
    private val h264Idr = Vectors.load("annexb/h264-idr.hex")
    private val h265Idr = Vectors.load("annexb/h265-idr.hex")

    private fun bytes(vararg values: Int) = ByteArray(values.size) { values[it].toByte() }

    @Test
    fun realH264IdrHasAudSpsPpsAndIdr() {
        assertEquals(listOf(9, 7, 8, 5), AnnexB.split(h264Idr, VideoCodec.H264).map { it.type }.distinct())
        assertTrue(AnnexB.isKeyframe(h264Idr, VideoCodec.H264))
    }

    @Test
    fun realH265IdrHasVpsSpsPpsAndAKeyframeSlice() {
        val types = AnnexB.split(h265Idr, VideoCodec.H265).map { it.type }
        assertTrue(types.containsAll(listOf(32, 33, 34)))
        assertTrue(AnnexB.isKeyframe(h265Idr, VideoCodec.H265))
    }

    @Test
    fun parameterSetsComeOutWithFourByteStartCodesInOrder() {
        val params = AnnexB.extractParameterSets(h265Idr, VideoCodec.H265)!!
        val units = AnnexB.split(params, VideoCodec.H265)

        assertEquals(listOf(32, 33, 34), units.map { it.type })
        assertArrayEquals(bytes(0, 0, 0, 1), params.copyOfRange(0, 4))
    }

    @Test
    fun threeByteStartCodesAndTrailingZerosAreHandled() {
        val data = bytes(0, 0, 1, 0x67, 0x42, 0, 0, 0, 1, 0x68, 0xCE, 0, 0, 1, 0x65, 0x88, 0, 0)

        val units = AnnexB.split(data, VideoCodec.H264)

        assertEquals(listOf(7, 8, 5), units.map { it.type })
        assertEquals(listOf(2, 2, 2), units.map { it.length })
    }

    @Test
    fun pFrameIsNotAKeyframe() {
        assertFalse(AnnexB.isKeyframe(bytes(0, 0, 0, 1, 0x41, 0x9A), VideoCodec.H264))
        assertFalse(AnnexB.isKeyframe(bytes(0, 0, 0, 1, 0x02, 0x01, 0xD0), VideoCodec.H265))
    }

    @Test
    fun missingPpsGivesNoParameterSets() {
        assertNull(AnnexB.extractParameterSets(bytes(0, 0, 0, 1, 0x67, 0x42, 0, 0, 0, 1, 0x65, 0x88), VideoCodec.H264))
    }

    @Test
    fun h264CsdIsSpsAndPpsSeparately() {
        val csd = CsdBuilder.build(VideoCodec.H264, h264Idr)!!

        assertEquals(listOf(7), AnnexB.split(csd.csd0, VideoCodec.H264).map { it.type })
        assertEquals(listOf(8), AnnexB.split(csd.csd1!!, VideoCodec.H264).map { it.type })
    }

    @Test
    fun h265CsdIsAllParameterSetsTogether() {
        val csd = CsdBuilder.build(VideoCodec.H265, h265Idr)!!

        assertArrayEquals(AnnexB.extractParameterSets(h265Idr, VideoCodec.H265), csd.csd0)
        assertNull(csd.csd1)
    }

    @Test
    fun csdFromTheConfigEqualsCsdFromTheKeyframe() {
        val config = AnnexB.extractParameterSets(h264Idr, VideoCodec.H264)!!

        assertEquals(CsdBuilder.build(VideoCodec.H264, h264Idr), CsdBuilder.build(VideoCodec.H264, config))
    }
}
