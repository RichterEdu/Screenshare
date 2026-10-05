package dev.screenshare.android.video

import dev.screenshare.android.protocol.ConfigMessage
import dev.screenshare.android.protocol.FrameMessage
import dev.screenshare.android.protocol.Vectors
import dev.screenshare.android.protocol.VideoCodec
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class DecoderCoreTest {
    private val idr = Vectors.load("annexb/h264-idr.hex")
    private val config = ConfigMessage(2520, 1080, VideoCodec.H264, 50_000, AnnexB.extractParameterSets(idr, VideoCodec.H264)!!)
    private val port = FakePort()
    private var now = 0L
    private var keyframeRequests = 0
    private val core = DecoderCore(port, requestKeyframe = { keyframeRequests++ }, nowMs = { now })

    private fun key(ts: Long) = FrameMessage(ts, true, idr)
    private fun p(ts: Long) = FrameMessage(ts, false, byteArrayOf(0, 0, 0, 1, 0x41, 0x9A.toByte()))

    /** Decoder falso: guarda o que recebeu. */
    private class FakePort : CodecPort {
        val started = mutableListOf<Int>()
        val queued = mutableListOf<Long>()
        val rendered = mutableListOf<Int>()
        var releases = 0
        var accept = true

        override fun start(generation: Int, codec: Int, width: Int, height: Int, csd: Csd): Boolean {
            if (accept) started += generation
            return accept
        }

        override fun queue(index: Int, data: ByteArray, ptsUs: Long) {
            queued += ptsUs
        }

        override fun render(index: Int) {
            rendered += index
        }

        override fun release() {
            releases++
        }
    }

    private fun running(): DecoderCore {
        core.onSurface(true)
        core.onConfig(config)
        keyframeRequests = 0
        return core
    }

    private fun DecoderCore.input(index: Int, capacity: Int = 1 shl 20) = onInputAvailable(generation, index, capacity)

    @Test
    fun decoderStartsOnlyWithConfigAndSurface() {
        core.onConfig(config)
        assertTrue(port.started.isEmpty())

        core.onSurface(true)

        assertEquals(1, port.started.size)
        assertEquals(1, keyframeRequests) // superfície nova: pede um quadro completo
    }

    @Test
    fun framesBeforeTheKeyframeAreDroppedThenFramesGoInOrder() {
        val core = running()
        core.input(0)
        core.input(1)
        core.input(2)

        core.onFrame(p(1))
        core.onFrame(key(2))
        core.onFrame(p(3))

        assertEquals(listOf(2L, 3L), port.queued)
    }

    @Test
    fun framesWaitForInputBuffers() {
        val core = running()
        core.onFrame(key(1))
        core.onFrame(p(2))
        assertTrue(port.queued.isEmpty())

        core.input(7)
        core.input(8)

        assertEquals(listOf(1L, 2L), port.queued)
    }

    @Test
    fun outputIsRenderedAtOnce() {
        val core = running()

        core.onOutputAvailable(core.generation, 4)

        assertEquals(listOf(4), port.rendered)
    }

    @Test
    fun sameConfigKeepsTheDecoderAndADifferentOneRecreatesIt() {
        val core = running()

        core.onConfig(config.copy())
        assertEquals(1, port.started.size)

        core.onConfig(config.copy(width = 1920))
        assertEquals(2, port.started.size)
        assertEquals(1, port.releases)
        assertEquals(0, keyframeRequests) // o PC já manda o IDR depois do CONFIG
    }

    @Test
    fun tooManyFramesWaitingAreDroppedAndAKeyframeIsRequested() {
        val core = running()
        core.onFrame(key(1))
        repeat(DecoderCore.MAX_WAITING) { core.onFrame(p(2L + it)) }

        core.input(0)

        assertTrue(port.queued.isEmpty())
        assertEquals(1, keyframeRequests)
        core.onFrame(p(10))
        core.onFrame(key(11))
        assertEquals(listOf(11L), port.queued)
    }

    @Test
    fun frameBiggerThanTheInputBufferIsDroppedAndAKeyframeIsRequested() {
        val core = running()
        core.input(0, capacity = 10)

        core.onFrame(key(1))

        assertTrue(port.queued.isEmpty())
        assertEquals(1, keyframeRequests)
    }

    @Test
    fun keyframeRequestsAreLimitedToOneEvery500ms() {
        val core = running()
        core.onFrame(p(1))
        core.onFrame(p(2))
        assertEquals(1, keyframeRequests)

        now = 600
        core.onFrame(p(3))
        assertEquals(2, keyframeRequests)
    }

    @Test
    fun requestBlockedByTheLimitIsSentByALaterTick() {
        val core = running()
        core.onFrame(p(1)) // pede (t = 0)
        now = 100
        core.onFrame(p(2)) // barrado pelo limite; a tela do PC para e não vem mais nada
        assertEquals(1, keyframeRequests)

        now = 300
        core.onTick()
        assertEquals(1, keyframeRequests)
        now = 600
        core.onTick()
        assertEquals(2, keyframeRequests)
        now = 1_200
        core.onTick()
        assertEquals(2, keyframeRequests) // o pedido guardado sai uma vez só
    }

    @Test
    fun configThatOnlyChangesTheBitrateKeepsTheDecoder() {
        val core = running()

        core.onConfig(config.copy(bitrateKbps = 25_000))

        assertEquals(1, port.started.size)
        assertEquals(0, port.releases)
    }

    @Test
    fun keyframeThatArrivesBeforeTheSurfaceStartsTheDecoderWhenItAppears() {
        core.onConfig(config.copy(codecConfig = ByteArray(0)))
        core.onFrame(key(1)) // sem superfície ainda

        core.onSurface(true)

        assertEquals(1, port.started.size)
        assertEquals(1, keyframeRequests)
    }

    @Test
    fun lostSurfaceReleasesAndItsReturnRecreatesAndAsksForAKeyframe() {
        val core = running()

        core.onSurface(false)
        assertEquals(1, port.releases)
        assertFalse(core.isRunning)

        now = 1_000
        core.onSurface(true)
        assertEquals(2, port.started.size)
        assertEquals(1, keyframeRequests)
    }

    @Test
    fun decoderErrorRecreatesItAndAsksForAKeyframe() {
        val core = running()

        core.onError(core.generation)

        assertEquals(2, port.started.size)
        assertEquals(1, keyframeRequests)
    }

    @Test
    fun callbacksFromAnOldDecoderAreIgnored() {
        val core = running()
        val old = core.generation
        core.onConfig(config.copy(width = 1920))

        core.onInputAvailable(old, 0, 1 shl 20)
        core.onOutputAvailable(old, 1)
        core.onError(old)
        core.onFrame(key(1))

        assertTrue(port.queued.isEmpty())
        assertTrue(port.rendered.isEmpty())
        assertEquals(2, port.started.size)
    }

    @Test
    fun resetForgetsTheConfigSoTheSameOneStartsAgain() {
        val core = running()

        core.reset()
        assertEquals(1, port.releases)
        core.onConfig(config)

        assertEquals(2, port.started.size)
    }

    @Test
    fun configWithoutParameterSetsStartsOnTheFirstKeyframe() {
        core.onSurface(true)
        core.onConfig(config.copy(codecConfig = ByteArray(0)))
        assertTrue(port.started.isEmpty())

        core.onFrame(key(1))
        core.input(0)

        assertEquals(1, port.started.size)
        assertEquals(listOf(1L), port.queued)
    }
}
