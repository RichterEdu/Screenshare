package dev.screenshare.android.video

import java.util.Locale

/** O que o overlay mostra: quadros exibidos por segundo, Mbps recebidos e a latência mediana (null sem relógio acertado). */
data class StatsSnapshot(val fps: Double, val mbps: Double, val latencyMs: Double?)

/**
 * Números do vídeo na última janela (1 s): bytes recebidos, quadros exibidos e a latência de cada um (do instante em
 * que o PC capturou até aparecer na tela). Seguro entre threads.
 */
class VideoStats(private val windowUs: Long = 1_000_000) {
    private class Event(val atUs: Long, val value: Long)

    private val received = ArrayDeque<Event>()
    private val rendered = ArrayDeque<Event>()

    @Synchronized
    fun onReceived(bytes: Int, atUs: Long) = add(received, atUs, bytes.toLong())

    /** [latencyUs] null = ainda sem relógio do PC acertado. */
    @Synchronized
    fun onRendered(atUs: Long, latencyUs: Long?) = add(rendered, atUs, latencyUs ?: -1)

    @Synchronized
    fun snapshot(nowUs: Long): StatsSnapshot {
        evict(received, nowUs)
        evict(rendered, nowUs)
        val seconds = windowUs / 1_000_000.0
        val latencies = rendered.map { it.value }.filter { it >= 0 }.sorted()
        return StatsSnapshot(
            fps = rendered.size / seconds,
            mbps = received.sumOf { it.value } * 8 / seconds / 1_000_000,
            latencyMs = if (latencies.isEmpty()) null else latencies[latencies.size / 2] / 1_000.0,
        )
    }

    private fun add(events: ArrayDeque<Event>, atUs: Long, value: Long) {
        events.addLast(Event(atUs, value))
        evict(events, atUs)
    }

    private fun evict(events: ArrayDeque<Event>, nowUs: Long) {
        while (events.isNotEmpty() && nowUs - events.first().atUs > windowUs) events.removeFirst()
    }

    companion object {
        private val PT_BR = Locale.forLanguageTag("pt-BR")

        /** "≈38 ms · 60 fps · 12,4 Mbps · RTT 3,1 ms" ("—" no que ainda não se sabe). */
        fun overlayText(stats: StatsSnapshot, rttMs: Double?): String {
            val latency = stats.latencyMs?.let { String.format(PT_BR, "≈%.0f ms", it) } ?: "≈— ms"
            val rtt = rttMs?.let { String.format(PT_BR, "%.1f ms", it) } ?: "—"
            return String.format(PT_BR, "%s · %.0f fps · %.1f Mbps · RTT %s", latency, stats.fps, stats.mbps, rtt)
        }
    }
}
