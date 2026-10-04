package dev.screenshare.android.pairing

import dev.screenshare.android.protocol.Vectors
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class PairingUriTest {
    private val example = Vectors.text("pairing-uri.txt")

    @Test
    fun parsesTheSharedExample() {
        val info = PairingUri.parse(example)!!

        assertEquals("192.168.0.10", info.host)
        assertEquals(38700, info.port)
        assertEquals("ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8", info.fingerprint)
        assertArrayEquals(ByteArray(32) { it.toByte() }, info.secret)
        assertEquals("PC da Sala", info.pcName)
    }

    @Test
    fun rejectsOtherSchemesAndHosts() {
        assertNull(PairingUri.parse(example.replace("screenshare://", "https://")))
        assertNull(PairingUri.parse(example.replace("://pair?", "://outra?")))
    }

    @Test
    fun rejectsMissingFields() {
        for (field in listOf("h", "p", "fp", "s", "n")) {
            val without = example.split('?', '&').filterNot { it.startsWith("$field=") }
            val uri = without.first() + "?" + without.drop(1).joinToString("&")
            assertNull("sem $field", PairingUri.parse(uri))
        }
    }

    @Test
    fun rejectsBadValues() {
        assertNull(PairingUri.parse(example.replace("p=38700", "p=99999")))
        assertNull(PairingUri.parse(example.replace("s=AAEC", "s=!!!!")))
        assertNull(PairingUri.parse(example.replace("s=AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8", "s=AAEC")))
        assertNull(PairingUri.parse(example.replace("fp=ICEi", "fp=")))
    }

    @Test
    fun rejectsGarbage() {
        assertNull(PairingUri.parse("isso não é uma URI"))
        assertNull(PairingUri.parse("https://example.com"))
        assertNull(PairingUri.parse(""))
    }

    @Test
    fun deviceNameIsTruncatedTo64Utf8Bytes() {
        val name = deviceNameOf("é".repeat(40)) // 2 bytes por caractere
        assertEquals("é".repeat(32), name)
        assertTrue(name.toByteArray(Charsets.UTF_8).size <= 64)
    }

    @Test
    fun blankDeviceNameFallsBackToAndroid() = assertEquals("Android", deviceNameOf("   "))
}
