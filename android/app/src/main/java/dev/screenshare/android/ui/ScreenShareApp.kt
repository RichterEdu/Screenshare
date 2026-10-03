package dev.screenshare.android.ui

import android.content.Context
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.platform.LocalContext
import com.google.mlkit.vision.barcode.common.Barcode
import com.google.mlkit.vision.codescanner.GmsBarcodeScannerOptions
import com.google.mlkit.vision.codescanner.GmsBarcodeScanning
import dev.screenshare.android.net.ConnectionState
import dev.screenshare.android.net.ConnectionViewModel

/** Escolhe a tela pelo estado da conexão: conectado → tela imersiva; qualquer outro estado → lista de PCs. */
@Composable
fun ScreenShareApp(viewModel: ConnectionViewModel) {
    val state by viewModel.connectionState.collectAsState()
    val context = LocalContext.current

    when (val current = state) {
        is ConnectionState.Connected -> ImmersiveScreen(current, onDisconnect = viewModel::disconnect)
        else -> {
            val hosts by viewModel.hosts.collectAsState()
            val pairedPc by viewModel.pairedPc.collectAsState()
            val message by viewModel.message.collectAsState()
            HostListScreen(
                hosts = hosts,
                state = current,
                pairedPc = pairedPc,
                message = message,
                onPair = { scanPairingQr(context, onResult = viewModel::pair, onError = viewModel::showMessage) },
                onConnect = viewModel::connectWifi,
                onConnectUsb = viewModel::connectUsb,
                onForget = viewModel::forgetPc,
            )
        }
    }
}

/** Abre o leitor de QR do Google Play Services (traz a própria tela de câmera; o app não pede permissão). */
private fun scanPairingQr(context: Context, onResult: (String) -> Unit, onError: (String) -> Unit) {
    val options = GmsBarcodeScannerOptions.Builder().setBarcodeFormats(Barcode.FORMAT_QR_CODE).build()
    GmsBarcodeScanning.getClient(context, options).startScan()
        .addOnSuccessListener { barcode -> barcode.rawValue?.let(onResult) ?: onError("O QR está vazio.") }
        .addOnFailureListener { onError("Não foi possível abrir o leitor de QR: ${it.message}") }
}
