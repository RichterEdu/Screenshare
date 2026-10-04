package dev.screenshare.android.net

import android.app.Application
import android.os.Build
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import dev.screenshare.android.pairing.PairingUri
import dev.screenshare.android.pairing.deviceNameOf
import dev.screenshare.android.protocol.DeniedReason
import dev.screenshare.android.security.KeystoreTokenCipher
import dev.screenshare.android.security.PairedPc
import dev.screenshare.android.security.PairingStore
import dev.screenshare.android.security.pairingDataStore
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.catch
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch

/** Liga pareamento, descoberta e conexão à interface. */
class ConnectionViewModel(application: Application) : AndroidViewModel(application) {
    private val store = PairingStore(application.pairingDataStore, KeystoreTokenCipher())

    private val _pairedPc = MutableStateFlow<PairedPc?>(null)
    val pairedPc: StateFlow<PairedPc?> = _pairedPc.asStateFlow()

    private val _message = MutableStateFlow<String?>(null)

    /** Aviso para o usuário fora do estado da conexão (QR inválido, leitor indisponível…). */
    val message: StateFlow<String?> = _message.asStateFlow()

    private val connection = Connection(
        viewModelScope, landscapeScreenInfo(application),
        // Roda na thread de IO dentro da conexão: não pode lançar (derrubaria o app), então só muda estado e dispara o salvamento.
        onPaired = { pc ->
            persist("Não foi possível salvar o pareamento") { store.save(pc) }
            _pairedPc.value = pc
        },
    )

    val connectionState: StateFlow<ConnectionState> = connection.state

    /** PCs encontrados por mDNS, só os do PC pareado; a busca só roda enquanto a lista está na tela. */
    val hosts: StateFlow<List<DiscoveredHost>> = HostDiscovery(application).hosts()
        .catch { emit(emptyList()) } // sem permissão/serviço NSD: a entrada manual continua funcionando
        .combine(pairedPc) { found, pc -> found.ofPairedPc(pc) }
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(stopTimeoutMillis = 5_000), emptyList())

    init {
        viewModelScope.launch { _pairedPc.value = store.load() }
        viewModelScope.launch {
            connection.state.collect { state ->
                // O PC removeu este celular: esquece o pareamento para o usuário parear de novo.
                if (state is ConnectionState.Failed && state.denied == DeniedReason.UNKNOWN_DEVICE) forgetPc()
            }
        }
    }

    /** Texto lido do QR; [overUsb] pareia pelo cabo (127.0.0.1, depois do `adb reverse`) em vez de pelo IP do QR. */
    fun pair(qrText: String, overUsb: Boolean = false) {
        val info = PairingUri.parse(qrText)
        if (info == null) {
            _message.value = "Este QR não é de pareamento do ScreenShare."
            return
        }
        _message.value = null
        connection.connect(ConnectTarget.Pairing(info, deviceNameOf(Build.MODEL), overUsb))
    }

    fun showMessage(text: String) {
        _message.value = text
    }

    fun connectWifi(address: HostAddress) {
        val pc = _pairedPc.value
        if (pc == null) {
            _message.value = "Pareie com o PC primeiro."
            return
        }
        _message.value = null
        connection.connect(ConnectTarget.Wifi(address, pc))
        persist("Não foi possível salvar o último endereço") { store.updateLastHost(address.host) }
    }

    fun connectUsb() {
        val pc = _pairedPc.value
        if (pc == null) {
            _message.value = "Pareie com o PC primeiro (pelo Wi-Fi ou pelo cabo)."
            return
        }
        _message.value = null
        connection.connect(ConnectTarget.Usb(pc))
    }

    fun forgetPc() {
        _pairedPc.value = null
        persist("Não foi possível esquecer o pareamento") { store.clear() }
    }

    fun disconnect() = connection.disconnect()

    override fun onCleared() = connection.disconnect()

    /**
     * Roda uma gravação do [store] sem deixar a falha escapar: o viewModelScope não tem handler e uma exceção
     * solta derrubaria o app. A falha vira [message]; o cancelamento do escopo passa adiante.
     */
    private fun persist(failure: String, block: suspend () -> Unit) {
        viewModelScope.launch {
            try {
                block()
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                _message.value = "$failure: ${e.message ?: e.javaClass.simpleName}"
            }
        }
    }

    private companion object {
        /** A tela do celular é usada como monitor na horizontal: largura é sempre o lado maior. */
        fun landscapeScreenInfo(application: Application): ScreenInfo {
            val metrics = application.resources.displayMetrics
            return ScreenInfo(
                width = maxOf(metrics.widthPixels, metrics.heightPixels),
                height = minOf(metrics.widthPixels, metrics.heightPixels),
                densityDpi = metrics.densityDpi,
            )
        }
    }
}
