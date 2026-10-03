package dev.screenshare.android.ui

import android.app.Activity
import android.content.pm.ActivityInfo
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.unit.dp
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import dev.screenshare.android.net.ConnectionState
import dev.screenshare.android.protocol.VideoCodec

/**
 * Tela cheia, na horizontal, que fará o papel de monitor. Ainda não há vídeo (Parte 3):
 * mostra um placeholder escuro e o overlay de latência medida por PING/PONG.
 */
@Composable
fun ImmersiveScreen(state: ConnectionState.Connected, onDisconnect: () -> Unit) {
    ImmersiveWindowEffect()
    BackHandler(onBack = onDisconnect)

    Box(modifier = Modifier.fillMaxSize().background(Color.Black)) {
        Column(
            modifier = Modifier.align(Alignment.Center),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(4.dp),
        ) {
            Text("Conectado", color = Color.White)
            val codec = if (state.config.codec == VideoCodec.H265) "H.265" else "H.264"
            Text(
                "${state.config.width}×${state.config.height} · $codec · vídeo ainda não implementado",
                color = Color.Gray,
            )
        }

        Text(
            text = state.rttMs?.let { "%.1f ms".format(it) } ?: "— ms",
            color = Color.Green,
            modifier = Modifier
                .align(Alignment.TopStart)
                .safeDrawingPadding()
                .padding(8.dp)
                .background(Color(0x99000000), RoundedCornerShape(4.dp))
                .padding(horizontal = 8.dp, vertical = 2.dp),
        )

        Button(
            onClick = onDisconnect,
            modifier = Modifier.align(Alignment.TopEnd).safeDrawingPadding().padding(8.dp),
        ) {
            Text("Desconectar")
        }
    }
}

/** Esconde as barras do sistema, trava a horizontal e mantém a tela acesa enquanto a tela imersiva está visível. */
@Composable
private fun ImmersiveWindowEffect() {
    val view = LocalView.current
    DisposableEffect(view) {
        val activity = view.context as? Activity
        val window = activity?.window
        val previousOrientation = activity?.requestedOrientation
        val controller = window?.let { WindowCompat.getInsetsController(it, view) }

        activity?.requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
        controller?.systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        controller?.hide(WindowInsetsCompat.Type.systemBars())
        view.keepScreenOn = true

        onDispose {
            view.keepScreenOn = false
            controller?.show(WindowInsetsCompat.Type.systemBars())
            previousOrientation?.let { activity.requestedOrientation = it }
        }
    }
}
