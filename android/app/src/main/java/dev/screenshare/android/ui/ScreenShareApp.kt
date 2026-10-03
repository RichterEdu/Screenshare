package dev.screenshare.android.ui

import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import dev.screenshare.android.net.ConnectionState
import dev.screenshare.android.net.ConnectionViewModel

/** Escolhe a tela pelo estado da conexão: conectado → tela imersiva; qualquer outro estado → lista de PCs. */
@Composable
fun ScreenShareApp(viewModel: ConnectionViewModel) {
    val state by viewModel.connectionState.collectAsState()

    when (val current = state) {
        is ConnectionState.Connected -> ImmersiveScreen(current, onDisconnect = viewModel::disconnect)
        else -> {
            val hosts by viewModel.hosts.collectAsState()
            HostListScreen(
                hosts = hosts,
                state = current,
                onConnect = viewModel::connect,
            )
        }
    }
}
