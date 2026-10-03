package dev.screenshare.android

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.viewModels
import dev.screenshare.android.net.ConnectionViewModel
import dev.screenshare.android.ui.ScreenShareApp
import dev.screenshare.android.ui.theme.ScreenShareTheme

class MainActivity : ComponentActivity() {
    private val viewModel: ConnectionViewModel by viewModels()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent {
            ScreenShareTheme {
                ScreenShareApp(viewModel)
            }
        }
    }
}
