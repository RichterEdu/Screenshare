package dev.screenshare.android.security

import android.content.Context
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import java.util.Base64
import java.util.Objects
import kotlinx.coroutines.CancellationException
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
    /**
     * Devolve o PC pareado, ou null se não há nenhum ou se não foi possível recuperá-lo. Nunca lança (exceto
     * [CancellationException]): o Keystore pode falhar de muitas formas nos aparelhos reais (ProviderException,
     * IOException, exceções de serviço do sistema) e o DataStore pode estar corrompido ou ilegível. Quem chama
     * (a tela inicial) não tem como se recuperar disso, então qualquer falha vira "esquecer o pareamento e pedir
     * para parear de novo".
     */
    suspend fun load(): PairedPc? {
        try {
            val prefs = dataStore.data.first()
            val name = prefs[NAME] ?: return null
            val fingerprint = prefs[FINGERPRINT] ?: return null
            val sealed = prefs[TOKEN] ?: return null
            val token = cipher.decrypt(Base64.getDecoder().decode(sealed))
            return PairedPc(name, fingerprint, token, prefs[LAST_HOST])
        } catch (e: CancellationException) {
            throw e
        } catch (_: Exception) {
            forgetBestEffort()
            return null
        }
    }

    /** Apaga o pareamento sem deixar a falha de apagar escapar: se nem isso der, o próximo load() tenta de novo. */
    private suspend fun forgetBestEffort() {
        try {
            clear()
        } catch (e: CancellationException) {
            throw e
        } catch (_: Exception) {
            // melhor esforço
        }
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
