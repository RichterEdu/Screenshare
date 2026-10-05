package dev.screenshare.android.video

import dev.screenshare.android.protocol.VideoCodec
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class VideoStatsTest {
    @Test
    fun lastSecondGivesFpsMbpsAndMedianLatency() {
        val stats = VideoStats()
        for (i in 0 until 60) {
            val at = 1_000_000L + i * 16_667L
            stats.onReceived(25_000, at)
            stats.onRendered(at, latencyUs = 30_000L + (i % 3) * 5_000L)
        }

        val snapshot = stats.snapshot(nowUs = 2_000_000)

        assertEquals(60.0, snapshot.fps, 0.01)
        assertEquals(12.0, snapshot.mbps, 0.01)
        assertEquals(35.0, snapshot.latencyMs!!, 0.01)
    }

    @Test
    fun oldEventsLeaveTheWindowAndLatencyWithoutClockIsUnknown() {
        val stats = VideoStats()
        stats.onReceived(1_000_000, 0)
        stats.onRendered(500_000, latencyUs = null)

        val snapshot = stats.snapshot(nowUs = 1_200_000)

        assertEquals(1.0, snapshot.fps, 0.01) // o recebido em t = 0 já saiu; o exibido em 0,5 s ainda conta
        assertEquals(0.0, snapshot.mbps, 0.01)
        assertNull(snapshot.latencyMs)
    }

    @Test
    fun overlayTextIsInPortuguese() {
        assertEquals("≈38 ms · 60 fps · 12,4 Mbps · RTT 3,1 ms", VideoStats.overlayText(StatsSnapshot(60.0, 12.43, 38.2), 3.14))
        assertEquals("≈— ms · 0 fps · 0,0 Mbps · RTT —", VideoStats.overlayText(StatsSnapshot(0.0, 0.0, null), null))
    }

    @Test
    fun lowLatencyDecoderIsPreferredAndCodecsDependOnTheSize() {
        val any: (Int, Int) -> Boolean = { _, _ -> true }
        val upTo1080p: (Int, Int) -> Boolean = { w, h -> w <= 1920 && h <= 1088 }
        val decoders = listOf(
            DecoderInfo("c2.qti.avc.decoder", CodecSupport.MIME_H264, hardware = true, lowLatency = false, any),
            DecoderInfo("c2.qti.avc.decoder.low_latency", CodecSupport.MIME_H264, hardware = true, lowLatency = true, any),
            DecoderInfo("c2.android.hevc.decoder", CodecSupport.MIME_H265, hardware = false, lowLatency = false, upTo1080p),
        )

        assertEquals("c2.qti.avc.decoder.low_latency", CodecSupport.choose(decoders, VideoCodec.H264, 2520, 1080)?.name)
        assertEquals(VideoCodec.H264, CodecSupport.supported(decoders, 2520, 1080))
        assertEquals(VideoCodec.ALL, CodecSupport.supported(decoders, 1920, 1080))
    }
}
