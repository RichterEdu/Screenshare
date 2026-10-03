package dev.screenshare.android.security

import java.security.MessageDigest
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocketFactory
import javax.net.ssl.X509TrustManager

/**
 * Confia só no certificado cuja digital é [expectedFingerprint] (fixada no pareamento).
 * Não usa autoridades certificadoras nem confere nome de host: o PC tem certificado autoassinado.
 */
class PinnedTrustManager(private val expectedFingerprint: String) : X509TrustManager {
    override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        val leaf = chain?.firstOrNull() ?: throw CertificateException("O PC não enviou certificado")
        val actual = Fingerprint.of(leaf.encoded)
        if (!MessageDigest.isEqual(actual.toByteArray(), expectedFingerprint.toByteArray())) {
            throw CertificateException("Este não é o PC pareado")
        }
    }

    override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) =
        throw CertificateException("Certificado de cliente não é usado")

    override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()

    fun socketFactory(): SSLSocketFactory =
        SSLContext.getInstance("TLS").apply { init(null, arrayOf(this@PinnedTrustManager), null) }.socketFactory
}
