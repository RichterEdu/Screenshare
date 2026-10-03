package dev.screenshare.android.security

import java.security.cert.CertificateException
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class PinnedTrustManagerTest {
    private val host = TestTls.certificate("host")
    private val other = TestTls.certificate("other")
    private val manager = PinnedTrustManager(Fingerprint.of(host.encoded))

    @Test
    fun acceptsTheExpectedCertificate() = manager.checkServerTrusted(arrayOf(host), "ECDHE_ECDSA")

    @Test
    fun rejectsADifferentCertificate() {
        assertThrows(CertificateException::class.java) { manager.checkServerTrusted(arrayOf(other), "ECDHE_ECDSA") }
    }

    @Test
    fun rejectsAnEmptyChain() {
        assertThrows(CertificateException::class.java) { manager.checkServerTrusted(arrayOf(), "ECDHE_ECDSA") }
    }

    @Test
    fun fingerprintIsBase64UrlWithoutPadding() {
        val fingerprint = Fingerprint.of(host.encoded)
        assertEquals(43, fingerprint.length)
        assertEquals(false, fingerprint.any { it == '+' || it == '/' || it == '=' })
    }

    @Test
    fun mdnsIdIsTheLowercaseHexOfTheFirstEightBytes() =
        assertEquals("2021222324252627", Fingerprint.mdnsId("ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8"))
}
