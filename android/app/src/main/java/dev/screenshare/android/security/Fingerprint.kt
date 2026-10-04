package dev.screenshare.android.security

import java.security.MessageDigest
import java.util.Base64

/** Digital do certificado do PC: SHA-256 do DER em base64url sem padding (mesmo formato do QR). */
object Fingerprint {
    fun of(der: ByteArray): String =
        Base64.getUrlEncoder().withoutPadding().encodeToString(MessageDigest.getInstance("SHA-256").digest(der))

    /** 16 primeiros caracteres hex da digital: é o que o PC anuncia no TXT `fp` do mDNS. */
    fun mdnsId(fingerprint: String): String =
        Base64.getUrlDecoder().decode(fingerprint).take(8).joinToString("") { "%02x".format(it) }
}
