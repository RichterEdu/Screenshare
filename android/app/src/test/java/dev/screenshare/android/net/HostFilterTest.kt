package dev.screenshare.android.net

import dev.screenshare.android.security.PairedPc
import org.junit.Assert.assertEquals
import org.junit.Test

class HostFilterTest {
    // digital com os bytes 0x20..0x3F → mdnsId "2021222324252627"
    private val pc = PairedPc("PC da Sala", "ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8", ByteArray(32), null)
    private val mine = DiscoveredHost("SALA", HostAddress("192.168.0.10", 38700), "2021222324252627")
    private val other = DiscoveredHost("VIZINHO", HostAddress("192.168.0.20", 38700), "ffffffffffffffff")
    private val old = DiscoveredHost("ANTIGO", HostAddress("192.168.0.30", 38700), null)

    @Test
    fun withAPairedPcOnlyItsAnnouncementsAreShown() =
        assertEquals(listOf(mine), listOf(mine, other, old).ofPairedPc(pc))

    @Test
    fun withoutPairingNothingIsShown() =
        assertEquals(emptyList<DiscoveredHost>(), listOf(mine, other, old).ofPairedPc(null))
}
