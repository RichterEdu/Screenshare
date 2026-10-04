package dev.screenshare.android.security

import androidx.datastore.preferences.core.PreferenceDataStoreFactory
import java.io.File
import java.security.ProviderException
import java.util.Base64
import javax.crypto.AEADBadTagException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class PairingStoreTest {
    @get:Rule
    val tmp = TemporaryFolder()

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val file by lazy { File(tmp.root, "pairing.preferences_pb") }
    private val dataStore by lazy { PreferenceDataStoreFactory.create(scope = scope, produceFile = { file }) }
    private val pc = PairedPc("PC da Sala", "ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8", ByteArray(32) { (0xA0 + it).toByte() }, "192.168.0.10")

    /** Cifra de mentira para o JVM (o Android Keystore só existe no aparelho). */
    private object XorCipher : TokenCipher {
        override fun encrypt(plain: ByteArray) = ByteArray(plain.size) { (plain[it].toInt() xor 0x5A).toByte() }
        override fun decrypt(sealed: ByteArray) = encrypt(sealed)
    }

    /** Simula a chave do Keystore perdida (app reinstalado, backup restaurado em outro aparelho). */
    private object LostKeyCipher : TokenCipher {
        override fun encrypt(plain: ByteArray) = XorCipher.encrypt(plain)
        override fun decrypt(sealed: ByteArray): ByteArray = throw AEADBadTagException("chave do Keystore perdida")
    }

    /** Simula o Keystore quebrando com uma RuntimeException (comum em alguns aparelhos), não só com GeneralSecurityException. */
    private object BrokenKeystoreCipher : TokenCipher {
        override fun encrypt(plain: ByteArray) = XorCipher.encrypt(plain)
        override fun decrypt(sealed: ByteArray): ByteArray = throw ProviderException("Keystore indisponível")
    }

    @After
    fun tearDown() = scope.cancel()

    @Test
    fun savedPcIsLoadedBack() = runBlocking {
        val store = PairingStore(dataStore, XorCipher)
        store.save(pc)

        assertEquals(pc, store.load())
    }

    @Test
    fun nothingSavedLoadsNull() = runBlocking {
        assertNull(PairingStore(dataStore, XorCipher).load())
    }

    @Test
    fun tokenIsNotStoredInPlainText() = runBlocking {
        PairingStore(dataStore, XorCipher).save(pc)

        val bytes = file.readBytes()
        assertFalse(bytes.toList().windowed(pc.token.size).any { it == pc.token.toList() })
        // O DataStore guarda o valor como texto: se a cifra fosse pulada, o Base64 do token apareceria no arquivo.
        val tokenAsBase64 = Base64.getEncoder().encodeToString(pc.token)
        assertFalse(String(bytes, Charsets.ISO_8859_1).contains(tokenAsBase64))
    }

    @Test
    fun undecryptableTokenForgetsThePairingInsteadOfCrashing() = runBlocking {
        PairingStore(dataStore, XorCipher).save(pc)
        val store = PairingStore(dataStore, LostKeyCipher)

        assertNull(store.load())
        assertNull(PairingStore(dataStore, XorCipher).load()) // foi apagado
    }

    @Test
    fun keystoreRuntimeFailureForgetsThePairingInsteadOfCrashing() = runBlocking {
        PairingStore(dataStore, XorCipher).save(pc)
        val store = PairingStore(dataStore, BrokenKeystoreCipher)

        assertNull(store.load())
        assertNull(PairingStore(dataStore, XorCipher).load()) // foi apagado
    }

    @Test
    fun clearForgetsThePc() = runBlocking {
        val store = PairingStore(dataStore, XorCipher)
        store.save(pc)

        store.clear()

        assertNull(store.load())
    }

    @Test
    fun updateLastHostKeepsTheRest() = runBlocking {
        val store = PairingStore(dataStore, XorCipher)
        store.save(pc)

        store.updateLastHost("10.0.0.7")

        assertEquals(pc.copy(lastHost = "10.0.0.7"), store.load())
    }

    @Test
    fun updateLastHostWithoutPairingDoesNothing() = runBlocking {
        val store = PairingStore(dataStore, XorCipher)

        store.updateLastHost("10.0.0.7")

        assertNull(store.load())
    }
}
