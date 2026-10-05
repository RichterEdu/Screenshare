package dev.screenshare.android.video

import dev.screenshare.android.protocol.ConfigMessage
import dev.screenshare.android.protocol.FrameMessage

/** Para onde vai o vídeo que chega do PC. Chamado na thread de leitura da conexão: não pode bloquear. */
interface VideoSink {
    fun onConfig(config: ConfigMessage)

    fun onFrame(frame: FrameMessage)

    companion object {
        /** Sem vídeo (testes, ou antes de o player existir). */
        val NONE = object : VideoSink {
            override fun onConfig(config: ConfigMessage) = Unit
            override fun onFrame(frame: FrameMessage) = Unit
        }
    }
}
