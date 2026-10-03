package dev.screenshare.android.net

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.catch
import kotlinx.coroutines.flow.stateIn

/** Liga descoberta e conexão à interface. */
class ConnectionViewModel(application: Application) : AndroidViewModel(application) {
    private val connection = Connection(viewModelScope, landscapeScreenInfo(application))

    val connectionState: StateFlow<ConnectionState> = connection.state

    /** PCs encontrados por mDNS; a busca só roda enquanto a lista está na tela. */
    val hosts: StateFlow<List<DiscoveredHost>> = HostDiscovery(application).hosts()
        .catch { emit(emptyList()) } // sem permissão/serviço NSD: a entrada manual continua funcionando
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(stopTimeoutMillis = 5_000), emptyList())

    fun connect(address: HostAddress) = connection.connect(address.host, address.port)

    fun disconnect() = connection.disconnect()

    override fun onCleared() = connection.disconnect()

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
