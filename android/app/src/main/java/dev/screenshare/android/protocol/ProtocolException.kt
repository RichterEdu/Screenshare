package dev.screenshare.android.protocol

import java.io.IOException

/** Bytes recebidos (ou uma mensagem a enviar) violam o protocolo — ver docs/protocol.md. */
class ProtocolException(message: String) : IOException(message)
