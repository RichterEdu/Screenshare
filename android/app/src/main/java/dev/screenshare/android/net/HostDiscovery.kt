package dev.screenshare.android.net

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import dev.screenshare.android.security.Fingerprint
import dev.screenshare.android.security.PairedPc
import kotlinx.coroutines.channels.awaitClose
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.callbackFlow

/** Um PC anunciando o serviço ScreenShare na rede local. [mdnsId] é o TXT `fp` (16 hex da digital do PC). */
data class DiscoveredHost(val name: String, val address: HostAddress, val mdnsId: String? = null)

/** Com um PC pareado, só os anúncios dele (TXT `fp` igual); sem pareamento, nenhum (é preciso parear primeiro). */
fun List<DiscoveredHost>.ofPairedPc(pc: PairedPc?): List<DiscoveredHost> {
    val id = pc?.let { Fingerprint.mdnsId(it.fingerprint) } ?: return emptyList()
    return filter { it.mdnsId == id }
}

/** Descobre hosts por mDNS (`_screenshare._tcp`) usando o NsdManager do Android. */
class HostDiscovery(context: Context) {
    private val nsd = context.applicationContext.getSystemService(NsdManager::class.java)

    /** Lista atualizada conforme PCs aparecem e somem. Coletar inicia a busca; cancelar a coleta a encerra. */
    @Suppress("DEPRECATION") // resolveService(listener) é o único resolvedor disponível desde o minSdk 29
    fun hosts(): Flow<List<DiscoveredHost>> = callbackFlow {
        val found = LinkedHashMap<String, DiscoveredHost>() // por nome do serviço
        val pending = ArrayDeque<NsdServiceInfo>()
        var resolving = false
        val lock = Any()

        fun publish() = trySend(synchronized(lock) { found.values.toList() })

        // O NsdManager antigo só aceita uma resolução por vez, então enfileiramos.
        fun resolveNext() {
            val next = synchronized(lock) {
                if (resolving) return
                pending.removeFirstOrNull()?.also { resolving = true }
            } ?: return
            nsd.resolveService(next, object : NsdManager.ResolveListener {
                override fun onServiceResolved(info: NsdServiceInfo) {
                    val ip = info.host?.hostAddress
                    synchronized(lock) {
                        resolving = false
                        if (ip != null) {
                            val mdnsId = info.attributes["fp"]?.let { String(it, Charsets.UTF_8) }
                            found[info.serviceName] = DiscoveredHost(info.serviceName, HostAddress(ip, info.port), mdnsId)
                        }
                    }
                    publish()
                    resolveNext()
                }

                override fun onResolveFailed(info: NsdServiceInfo, errorCode: Int) {
                    synchronized(lock) { resolving = false }
                    resolveNext()
                }
            })
        }

        val listener = object : NsdManager.DiscoveryListener {
            override fun onServiceFound(info: NsdServiceInfo) {
                synchronized(lock) { pending.add(info) }
                resolveNext()
            }

            override fun onServiceLost(info: NsdServiceInfo) {
                synchronized(lock) { found.remove(info.serviceName) }
                publish()
            }

            override fun onDiscoveryStarted(serviceType: String) {}
            override fun onDiscoveryStopped(serviceType: String) {}
            override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) { close() }
            override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) {}
        }

        trySend(emptyList<DiscoveredHost>())
        nsd.discoverServices(SERVICE_TYPE, NsdManager.PROTOCOL_DNS_SD, listener)
        awaitClose { runCatching { nsd.stopServiceDiscovery(listener) } }
    }

    private companion object {
        const val SERVICE_TYPE = "_screenshare._tcp"
    }
}
