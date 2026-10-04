package dev.screenshare.android.net

/** Porta padrão do host (docs/protocol.md). */
const val DEFAULT_PORT = 38700

/** Porta do USB: o app conecta em 127.0.0.1 depois do `adb reverse tcp:38701 tcp:38701`. */
const val USB_PORT = 38701

data class HostAddress(val host: String, val port: Int)

/** Lê "host" ou "host:porta" digitado pelo usuário; null se inválido. IPv6 literal não é suportado. */
fun parseHostAddress(input: String): HostAddress? {
    val text = input.trim()
    if (text.isEmpty() || text.any { it.isWhitespace() }) return null

    val parts = text.split(':')
    if (parts.size > 2) return null
    val host = parts[0]
    if (host.isEmpty()) return null
    if (parts.size == 1) return HostAddress(host, DEFAULT_PORT)

    val port = parts[1].toIntOrNull()?.takeIf { it in 1..65535 } ?: return null
    return HostAddress(host, port)
}
