package dev.screenshare.android.video

import dev.screenshare.android.protocol.ConfigMessage
import dev.screenshare.android.protocol.FrameMessage

/** O decoder de verdade (MediaCodec) visto pelo [DecoderCore]. Os callbacks do decoder levam a geração recebida em [start]. */
interface CodecPort {
    /** Cria e começa um decoder para este formato. false = não deu (formato não suportado ou superfície inválida). */
    fun start(generation: Int, codec: Int, width: Int, height: Int, csd: Csd): Boolean

    /** Entrega um access unit no buffer de entrada [index]. */
    fun queue(index: Int, data: ByteArray, ptsUs: Long)

    /** Mostra o quadro de saída [index] já. */
    fun render(index: Int)

    fun release()
}

/**
 * O decoder como máquina de estados, sem Android, numa thread só (a do decoder). Regras:
 * - só cria o decoder com CONFIG, parâmetros e superfície; os parâmetros vêm do CONFIG ou, se ele vier vazio, do primeiro keyframe;
 * - depois de (re)criar, descarta tudo até um keyframe;
 * - CONFIG igual não faz nada; diferente recria;
 * - mais de [MAX_WAITING] quadros esperando buffer de entrada: descarta-os, espera keyframe e pede um;
 * - quadro maior que o buffer de entrada: descarta, espera keyframe e pede;
 * - P-frame chegando enquanto espera keyframe: pede de novo (o pedido anterior pode ter sido o limitado);
 * - superfície perdida: solta o decoder; de volta: cria e pede keyframe;
 * - erro do decoder: recria e pede keyframe;
 * - callbacks de um decoder antigo (geração anterior) são ignorados;
 * - no máximo um pedido de keyframe a cada [KEYFRAME_REQUEST_INTERVAL_MS].
 */
class DecoderCore(
    private val port: CodecPort,
    private val requestKeyframe: () -> Unit,
    private val nowMs: () -> Long,
) {
    private var config: ConfigMessage? = null
    private var csd: Csd? = null
    private var hasSurface = false
    private var running = false
    private var awaitingKeyframe = true
    private var lastRequestMs: Long? = null
    private val waiting = ArrayDeque<FrameMessage>()
    private val inputs = ArrayDeque<Pair<Int, Int>>() // (índice, capacidade)

    /** Muda a cada decoder criado ou solto; callbacks com outra geração são de um decoder que já foi. */
    var generation = 0
        private set

    val isRunning: Boolean get() = running

    fun onConfig(config: ConfigMessage) {
        if (config == this.config) return
        this.config = config
        csd = if (config.codecConfig.isEmpty()) null else CsdBuilder.build(config.codec, config.codecConfig)
        stop()
        startIfReady(askKeyframe = false) // o PC manda o IDR logo depois do CONFIG
    }

    fun onFrame(frame: FrameMessage) {
        val config = config ?: return
        if (!running && hasSurface && csd == null && frame.isKeyframe) {
            csd = CsdBuilder.build(config.codec, frame.data) // CONFIG sem parâmetros: eles vêm no keyframe
            startIfReady(askKeyframe = false)
        }
        if (!running) return
        if (awaitingKeyframe && !frame.isKeyframe) {
            askKeyframe()
            return
        }
        awaitingKeyframe = false
        if (frame.isKeyframe) waiting.clear() // o keyframe torna inúteis os quadros que ainda esperavam
        waiting.addLast(frame)
        if (waiting.size > MAX_WAITING) {
            dropUntilKeyframe()
            return
        }
        feed()
    }

    fun onSurface(available: Boolean) {
        hasSurface = available
        if (available) startIfReady(askKeyframe = true) else stop()
    }

    fun onInputAvailable(generation: Int, index: Int, capacity: Int) {
        if (generation != this.generation || !running) return
        inputs.addLast(index to capacity)
        feed()
    }

    fun onOutputAvailable(generation: Int, index: Int) {
        if (generation != this.generation || !running) return
        port.render(index)
    }

    fun onError(generation: Int) {
        if (generation != this.generation || !running) return
        stop()
        startIfReady(askKeyframe = true)
    }

    fun release() = stop()

    /** Conexão nova: solta o decoder e esquece o CONFIG (o próximo, mesmo igual, recria). */
    fun reset() {
        stop()
        config = null
        csd = null
    }

    private fun feed() {
        while (waiting.isNotEmpty() && inputs.isNotEmpty()) {
            val frame = waiting.removeFirst()
            val (index, capacity) = inputs.removeFirst()
            if (frame.data.size > capacity) {
                inputs.addFirst(index to capacity) // o buffer continua livre para o próximo keyframe
                dropUntilKeyframe()
                return
            }
            port.queue(index, frame.data, frame.timestampUs)
        }
    }

    private fun dropUntilKeyframe() {
        waiting.clear()
        awaitingKeyframe = true
        askKeyframe()
    }

    private fun startIfReady(askKeyframe: Boolean) {
        val config = config ?: return
        val csd = csd ?: return
        if (!hasSurface || running) return
        generation++
        if (!port.start(generation, config.codec, config.width, config.height, csd)) return
        running = true
        awaitingKeyframe = true
        if (askKeyframe) askKeyframe()
    }

    private fun stop() {
        if (running) {
            port.release()
            running = false
            generation++
        }
        waiting.clear()
        inputs.clear()
        awaitingKeyframe = true
    }

    private fun askKeyframe() {
        val now = nowMs()
        val last = lastRequestMs
        if (last != null && now - last < KEYFRAME_REQUEST_INTERVAL_MS) return
        lastRequestMs = now
        requestKeyframe()
    }

    companion object {
        const val MAX_WAITING = 3
        const val KEYFRAME_REQUEST_INTERVAL_MS = 500L
    }
}
