package dev.screenshare.android.pairing

import dev.screenshare.android.protocol.MessageCodec
import java.net.URI
import java.net.URISyntaxException
import java.net.URLDecoder
import java.util.Base64
import java.util.Objects

/** Dados lidos do QR de pareamento. [fingerprint] em base64url; [secret] tem 32 bytes. */
data class PairingInfo(
    val host: String,
    val port: Int,
    val fingerprint: String,
    val secret: ByteArray,
    val pcName: String,
) {
    override fun equals(other: Any?) = other is PairingInfo && host == other.host && port == other.port &&
        fingerprint == other.fingerprint && secret.contentEquals(other.secret) && pcName == other.pcName

    override fun hashCode() = Objects.hash(host, port, fingerprint, secret.contentHashCode(), pcName)
}

/** Lê `screenshare://pair?h=…&p=…&fp=…&s=…&n=…` (formato em docs/protocol-vectors/pairing-uri.txt). */
object PairingUri {
    /** null se o texto não for um QR de pareamento válido. */
    fun parse(text: String): PairingInfo? {
        val uri = try {
            URI(text.trim())
        } catch (_: URISyntaxException) {
            return null
        }
        if (uri.scheme != "screenshare" || uri.host != "pair") return null
        val params = uri.rawQuery?.split('&')
            ?.mapNotNull { part -> part.split('=', limit = 2).takeIf { it.size == 2 } }
            ?.associate { (key, value) -> key to URLDecoder.decode(value, "UTF-8") } // decode(String, Charset) só existe a partir do API 33
            ?: return null

        val host = params["h"]?.takeIf { it.isNotBlank() } ?: return null
        val port = params["p"]?.toIntOrNull()?.takeIf { it in 1..65535 } ?: return null
        val fingerprint = params["fp"]?.takeIf { decodeBase64Url(it)?.size == 32 } ?: return null
        val secret = params["s"]?.let(::decodeBase64Url)?.takeIf { it.size == MessageCodec.SECRET_LENGTH } ?: return null
        val pcName = params["n"]?.takeIf { it.isNotBlank() } ?: return null
        return PairingInfo(host, port, fingerprint, secret, pcName)
    }

    private fun decodeBase64Url(text: String): ByteArray? = try {
        Base64.getUrlDecoder().decode(text)
    } catch (_: IllegalArgumentException) {
        null
    }
}

/** Nome do aparelho para o PAIR: o modelo, cortado em até 64 bytes UTF-8 sem partir caracteres. */
fun deviceNameOf(model: String): String {
    val name = model.trim().ifEmpty { "Android" }
    val result = StringBuilder()
    var bytes = 0
    for (codePoint in name.codePoints().toArray()) {
        val char = String(Character.toChars(codePoint))
        val size = char.toByteArray(Charsets.UTF_8).size
        if (bytes + size > MessageCodec.MAX_DEVICE_NAME_BYTES) break
        result.append(char)
        bytes += size
    }
    return result.toString()
}
