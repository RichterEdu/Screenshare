package dev.screenshare.android.video

import android.os.Handler
import android.os.HandlerThread
import android.os.Process
import android.os.SystemClock
import android.view.Surface
import dev.screenshare.android.protocol.ConfigMessage
import dev.screenshare.android.protocol.FrameMessage
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

/**
 * O vídeo da conexão: recebe CONFIG e FRAMEs (como [VideoSink]) e a superfície da tela, e roda o [DecoderCore] numa
 * thread com prioridade de display. Vive no ViewModel, então sobrevive a mudanças de configuração.
 */
class VideoPlayer(
    decoders: () -> List<DecoderInfo>,
    private val clock: () -> ClockSync,
    requestKeyframe: () -> Unit,
) : VideoSink {
    private val thread = HandlerThread("ScreenShare vídeo", Process.THREAD_PRIORITY_URGENT_DISPLAY).apply { start() }
    private val handler = Handler(thread.looper)

    @Volatile
    private var surface: Surface? = null

    val stats = VideoStats()

    private val core: DecoderCore = DecoderCore(
        MediaCodecPort(handler, { surface }, decoders, object : MediaCodecPort.Listener {
            override fun onInput(generation: Int, index: Int, capacity: Int) = core.onInputAvailable(generation, index, capacity)
            override fun onOutput(generation: Int, index: Int) = core.onOutputAvailable(generation, index)
            override fun onError(generation: Int) = core.onError(generation)
            override fun onRendered(ptsUs: Long, renderNs: Long) {
                val renderUs = renderNs / 1_000
                stats.onRendered(renderUs, clock().toLocalUs(ptsUs)?.let { renderUs - it })
            }
        }),
        requestKeyframe,
        nowMs = { SystemClock.elapsedRealtime() },
    )

    /** A cada 100 ms: o DecoderCore manda o pedido de keyframe que o limite de 1 a cada 500 ms tinha adiado. */
    private val tick = object : Runnable {
        override fun run() {
            core.onTick()
            handler.postDelayed(this, TICK_MS)
        }
    }

    init {
        handler.postDelayed(tick, TICK_MS)
    }

    override fun onConfig(config: ConfigMessage) {
        handler.post { core.onConfig(config) }
    }

    override fun onFrame(frame: FrameMessage) {
        stats.onReceived(frame.data.size, System.nanoTime() / 1_000)
        handler.post { core.onFrame(frame) }
    }

    /** A superfície da tela ficou pronta. */
    fun attach(surface: Surface) {
        handler.post {
            this.surface = surface
            core.onSurface(true)
        }
    }

    /** A superfície vai sumir (tela fechada, app em segundo plano): solta o decoder antes, esperando até 500 ms. */
    fun detach() {
        val done = CountDownLatch(1)
        handler.post {
            core.onSurface(false)
            surface = null
            done.countDown()
        }
        done.await(500, TimeUnit.MILLISECONDS)
    }

    /** Conexão nova: esquece o CONFIG anterior. */
    fun reset() {
        handler.post { core.reset() }
    }

    fun release() {
        handler.removeCallbacks(tick)
        handler.post { core.release() }
        thread.quitSafely()
    }

    private companion object {
        const val TICK_MS = 100L
    }
}
