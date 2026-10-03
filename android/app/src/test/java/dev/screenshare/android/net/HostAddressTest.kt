package dev.screenshare.android.net

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class HostAddressTest {
    @Test
    fun hostAloneUsesTheDefaultPort() =
        assertEquals(HostAddress("192.168.0.5", DEFAULT_PORT), parseHostAddress("192.168.0.5"))

    @Test
    fun hostWithPortIsSplit() =
        assertEquals(HostAddress("192.168.0.5", 1234), parseHostAddress("192.168.0.5:1234"))

    @Test
    fun surroundingSpacesAreIgnored() =
        assertEquals(HostAddress("pc-do-edu.local", DEFAULT_PORT), parseHostAddress("  pc-do-edu.local \n"))

    @Test
    fun blankInputIsRejected() {
        assertNull(parseHostAddress(""))
        assertNull(parseHostAddress("   "))
    }

    @Test
    fun invalidPortsAreRejected() {
        for (input in listOf("10.0.0.1:0", "10.0.0.1:65536", "10.0.0.1:abc", "10.0.0.1:")) {
            assertNull(input, parseHostAddress(input))
        }
    }

    @Test
    fun missingHostIsRejected() = assertNull(parseHostAddress(":38700"))

    @Test
    fun spacesInsideAreRejected() = assertNull(parseHostAddress("10.0.0. 1"))

    @Test
    fun ipv6LiteralsAreNotSupported() = assertNull(parseHostAddress("fe80::1"))
}
