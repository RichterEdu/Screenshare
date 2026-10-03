package dev.screenshare.android.security

import android.content.Context
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import java.security.GeneralSecurityException
import java.util.Base64
import java.util.Objects
import kotlinx.coroutines.flow.first

/** O PC pareado com este celular. [token] é a chave de acesso em claro (só em memória). */
data class PairedPc(val name: String, val fingerprint: String, val token: ByteArray, val lastHost: String?) {
    override fun equals(other: Any?) = other is PairedPc && name == other.name && fingerprint == other.fingerprint &&
        token.contentEquals(other.token) && lastHost == other.lastHost

    override fun hashCode() = Objects.hash(name, fingerprint, token.contentHashCode(), lastHost)
}

/** Cifra a chave de acesso antes de ir para o disco. */
interface TokenCipher {
    fun encrypt(plain: ByteArray): ByteArray
    fun decrypt(sealed: ByteArray): ByteArray
}

val Context.pairingDataStore: DataStore<Preferences> by preferencesDataStore(name = "pairing")

/**
 * Guarda um único PC pareado. A chave vai cifrada ([TokenCipher]); se não der para decifrar
 * (chave do Keystore perdida após reinstalação ou backup), o pareamento é apagado e [load] devolve null.
 */
class PairingStore(private val dataStore: DataStore<Preferences>, private val cipher: TokenCipher) {
    suspend fun load(): PairedPc? {
        val prefs = dataStore.data.first()
        val name = prefs[NAME] ?: return null
        val fingerprint = prefs[FINGERPRINT] ?: return null
        val sealed = prefs[TOKEN] ?: return null
        val token = try {
            cipher.decrypt(Base64.getDecoder().decode(sealed))
        } catch (_: GeneralSecurityException) {
            clear()
            return null
        } catch (_: IllegalArgumentException) {
            clear()
            return null
        }
        return PairedPc(name, fingerprint, token, prefs[LAST_HOST])
    }

    suspend fun save(pc: PairedPc) {
        val sealed = Base64.getEncoder().encodeToString(cipher.encrypt(pc.token))
        dataStore.edit { prefs ->
            prefs[NAME] = pc.name
            prefs[FINGERPRINT] = pc.fingerprint
            prefs[TOKEN] = sealed
            if (pc.lastHost != null) prefs[LAST_HOST] = pc.lastHost else prefs.remove(LAST_HOST)
        }
    }

    suspend fun updateLastHost(host: String) {
        dataStore.edit { prefs -> if (prefs[NAME] != null) prefs[LAST_HOST] = host }
    }

    suspend fun clear() {
        dataStore.edit { it.clear() }
    }

    private companion object {
        val NAME = stringPreferencesKey("pc_name")
        val FINGERPRINT = stringPreferencesKey("pc_fingerprint")
        val TOKEN = stringPreferencesKey("token_sealed")
        val LAST_HOST = stringPreferencesKey("last_host")
    }
}
