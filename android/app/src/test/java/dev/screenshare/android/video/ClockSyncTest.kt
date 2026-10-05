package dev.screenshare.android.video

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class ClockSyncTest {
    /** O celular está 5 s à frente do PC; ida = volta = 2 ms quando a rede está livre. */
    private val offset = 5_000_000L
    private val oneWay = 2_000L

    private fun ClockSync.ping(pcUs: Long, extraDelayUs: Long = 0) = onPcPing(pcUs, pcUs + offset + oneWay + extraDelayUs)

    @Test
    fun noEstimateUntilThereIsAPcPingAndAnRtt() {
        val sync = ClockSync()
        assertNull(sync.offsetUs())

        sync.ping(1_000_000)
        assertNull(sync.offsetUs())

        sync.onRtt(2 * oneWay, 6_000_000)
        assertEquals(offset, sync.offsetUs())
    }

    @Test
    fun jitterAndQueuedPingsDoNotMoveTheEstimate() {
        val sync = ClockSync()
        for (i in 0 until 20) {
            val pcUs = 1_000_000L * (i + 1)
            sync.ping(pcUs, extraDelayUs = if (i == 7) 0 else 3_000L + i * 1_500L) // só um PING passou sem fila
            sync.onRtt(2 * oneWay + (i % 3) * 4_000L, pcUs + offset)
        }

        assertEquals(offset, sync.offsetUs())
        assertEquals(10_000_000L + offset, sync.toLocalUs(10_000_000L))
    }

    @Test
    fun oldSamplesLeaveAfterTwentySecondsSoDriftIsFollowed() {
        val sync = ClockSync()
        sync.ping(1_000_000)
        sync.onRtt(2 * oneWay, 1_000_000 + offset)

        // 25 s depois os relógios andaram 3 ms um em relação ao outro
        val pcUs = 26_000_000L
        sync.onPcPing(pcUs, pcUs + offset + 3_000 + oneWay)
        sync.onRtt(2 * oneWay, pcUs + offset)

        assertEquals(offset + 3_000, sync.offsetUs())
    }
}
