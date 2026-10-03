package dev.screenshare.android.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import dev.screenshare.android.net.ConnectionState
import dev.screenshare.android.net.DEFAULT_PORT
import dev.screenshare.android.net.DiscoveredHost
import dev.screenshare.android.net.HostAddress
import dev.screenshare.android.net.parseHostAddress

@Composable
fun HostListScreen(
    hosts: List<DiscoveredHost>,
    state: ConnectionState,
    onConnect: (HostAddress) -> Unit,
) {
    val connecting = state is ConnectionState.Connecting

    Surface(modifier = Modifier.fillMaxSize()) {
        Column(
            modifier = Modifier.safeDrawingPadding().padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            Text("ScreenShare", style = MaterialTheme.typography.headlineMedium)

            when {
                connecting -> LinearProgressIndicator(modifier = Modifier.fillMaxWidth())
                state is ConnectionState.Failed ->
                    Text(state.reason, color = MaterialTheme.colorScheme.error)
            }

            Text("PCs na rede", style = MaterialTheme.typography.titleMedium)
            if (hosts.isEmpty()) {
                Text(
                    "Procurando… Abra o ScreenShare no PC (mesma rede Wi-Fi) ou digite o endereço abaixo.",
                    style = MaterialTheme.typography.bodyMedium,
                )
            }
            LazyColumn(
                modifier = Modifier.weight(1f, fill = false),
                verticalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                items(hosts, key = { it.name }) { host ->
                    Card(
                        modifier = Modifier.fillMaxWidth().clickable(enabled = !connecting) { onConnect(host.address) },
                    ) {
                        Column(modifier = Modifier.padding(16.dp)) {
                            Text(host.name, style = MaterialTheme.typography.titleMedium)
                            Text("${host.address.host}:${host.address.port}", style = MaterialTheme.typography.bodySmall)
                        }
                    }
                }
            }

            ManualAddress(enabled = !connecting, onConnect = onConnect)
        }
    }
}

/** Plano B quando o mDNS não funciona na rede: digitar o IP do PC. */
@Composable
private fun ManualAddress(enabled: Boolean, onConnect: (HostAddress) -> Unit) {
    var text by rememberSaveable { mutableStateOf("") }
    val address = remember(text) { parseHostAddress(text) }

    Row(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
        OutlinedTextField(
            value = text,
            onValueChange = { text = it },
            modifier = Modifier.weight(1f),
            label = { Text("Endereço do PC") },
            placeholder = { Text("192.168.0.10 (porta $DEFAULT_PORT)") },
            singleLine = true,
            isError = text.isNotBlank() && address == null,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
        )
        Button(onClick = { address?.let(onConnect) }, enabled = enabled && address != null) {
            Text("Conectar")
        }
    }
}
