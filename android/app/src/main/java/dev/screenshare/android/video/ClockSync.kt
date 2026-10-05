package dev.screenshare.android.video

/**
 * Acerta o relógio do PC com o do celular, para medir a latência de ponta a ponta sem mudar o protocolo.
 * deslocamento = mín(recebido no celular − valor do PING do PC) − mín(RTT)/2, nos últimos 20 s: o PING que chegou mais
 * rápido é o que menos esperou na rede. A janela de 20 s deixa entrar uma rota nova ou a deriva dos relógios.
 * Seguro entre threads (a leitura da conexão grava, o vídeo lê).
 */
class ClockSync(private val windowUs: Long = 20_000_000) {
    private class Sample(val atUs: Long, val value: Long)

    private val pings = ArrayDeque<Sample>()
    private val rtts = ArrayDeque<Sample>()

    /** [pcUs] = valor do PING do PC (relógio dele); [receivedUs] = quando chegou (relógio do celular). */
    @Synchronized
    fun onPcPing(pcUs: Long, receivedUs: Long) = add(pings, receivedUs, receivedUs - pcUs)

    /** Um RTT medido pelos PINGs do próprio celular. Zero ou negativo (PONG inválido) é ignorado. */
    @Synchronized
    fun onRtt(rttUs: Long, atUs: Long) {
        if (rttUs > 0) add(rtts, atUs, rttUs)
    }

    /** Relógio do celular − relógio do PC, em µs; null até ter um PING do PC e um RTT. */
    @Synchronized
    fun offsetUs(): Long? {
        val ping = pings.minOfOrNull { it.value } ?: return null
        val rtt = rtts.minOfOrNull { it.value } ?: return null
        return ping - rtt / 2
    }

    /** O instante, no relógio do celular, em que o PC marcou [pcUs]; null sem estimativa. */
    fun toLocalUs(pcUs: Long): Long? = offsetUs()?.let { pcUs + it }

    private fun add(samples: ArrayDeque<Sample>, atUs: Long, value: Long) {
        samples.addLast(Sample(atUs, value))
        while (atUs - samples.first().atUs > windowUs) samples.removeFirst()
    }
}
