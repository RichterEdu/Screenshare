package dev.screenshare.android.ui

import android.app.Activity
import android.content.pm.ActivityInfo
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.WindowManager
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import dev.screenshare.android.net.ConnectionState
import dev.screenshare.android.video.VideoPlayer
import dev.screenshare.android.video.VideoStats
import kotlinx.coroutines.delay

/**
 * A tela do celular como monitor: o vídeo do PC em tela cheia, na horizontal, um pixel do PC por pixel do painel
 * (setFixedSize no tamanho do CONFIG), sobre fundo preto. Em cima, o overlay de latência e um botão discreto de sair.
 */
@Composable
fun ImmersiveScreen(state: ConnectionState.Connected, player: VideoPlayer, onDisconnect: () -> Unit) {
    ImmersiveWindowEffect()
    BackHandler(onBack = onDisconnect)
    val width = state.config.width
    val height = state.config.height

    Box(modifier = Modifier.fillMaxSize().background(Color.Black), contentAlignment = Alignment.Center) {
        AndroidView(
            factory = { context ->
                SurfaceView(context).apply {
                    holder.addCallback(object : SurfaceHolder.Callback {
                        override fun surfaceCreated(holder: SurfaceHolder) = player.attach(holder.surface)
                        override fun surfaceChanged(holder: SurfaceHolder, format: Int, w: Int, h: Int) = Unit
                        override fun surfaceDestroyed(holder: SurfaceHolder) = player.detach()
                    })
                }
            },
            update = { view -> view.holder.setFixedSize(width, height) },
            modifier = Modifier.aspectRatio(width.toFloat() / height),
        )

        VideoOverlay(
            stats = player.stats,
            rttMs = state.rttMs,
            modifier = Modifier.align(Alignment.TopStart).safeDrawingPadding().padding(8.dp),
        )

        TextButton(
            onClick = onDisconnect,
            modifier = Modifier.align(Alignment.TopEnd).safeDrawingPadding().alpha(0.6f),
        ) {
            Text("Sair", color = Color.White)
        }
    }
}

/** "≈latência · fps · Mbps · RTT", atualizado duas vezes por segundo. */
@Composable
private fun VideoOverlay(stats: VideoStats, rttMs: Double?, modifier: Modifier = Modifier) {
    var text by remember { mutableStateOf("") }
    LaunchedEffect(stats, rttMs) {
        while (true) {
            text = VideoStats.overlayText(stats.snapshot(System.nanoTime() / 1_000), rttMs)
            delay(500)
        }
    }
    Text(
        text = text,
        color = Color.Green,
        fontSize = 12.sp,
        modifier = modifier
            .background(Color(0x99000000), RoundedCornerShape(4.dp))
            .padding(horizontal = 6.dp, vertical = 2.dp),
    )
}

/**
 * Esconde as barras do sistema, trava a horizontal, cobre o recorte da câmera (o vídeo usa o painel inteiro) e mantém
 * a tela acesa enquanto a tela imersiva está visível.
 */
@Composable
private fun ImmersiveWindowEffect() {
    val view = LocalView.current
    DisposableEffect(view) {
        val activity = view.context as? Activity
        val window = activity?.window
        val previousOrientation = activity?.requestedOrientation
        val previousCutout = window?.attributes?.layoutInDisplayCutoutMode
        val controller = window?.let { WindowCompat.getInsetsController(it, view) }

        activity?.requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
        window?.attributes = window?.attributes?.also {
            it.layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES
        }
        controller?.systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        controller?.hide(WindowInsetsCompat.Type.systemBars())
        view.keepScreenOn = true

        onDispose {
            view.keepScreenOn = false
            controller?.show(WindowInsetsCompat.Type.systemBars())
            if (previousCutout != null) window.attributes = window.attributes.also { it.layoutInDisplayCutoutMode = previousCutout }
            previousOrientation?.let { activity.requestedOrientation = it }
        }
    }
}
