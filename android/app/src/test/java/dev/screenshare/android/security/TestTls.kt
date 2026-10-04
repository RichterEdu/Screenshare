package dev.screenshare.android.security

import java.net.InetAddress
import java.security.KeyStore
import java.security.cert.X509Certificate
import javax.net.ssl.KeyManagerFactory
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLServerSocket

/** Certificados de teste em src/test/resources/tls (gerados com openssl, senha "test"). */
object TestTls {
    private val password = "test".toCharArray()

    fun keyStore(name: String): KeyStore = KeyStore.getInstance("PKCS12").apply {
        val stream = TestTls::class.java.getResourceAsStream("/tls/$name.p12") ?: error("certificado de teste não encontrado: $name")
        stream.use { load(it, password) }
    }

    fun certificate(name: String): X509Certificate =
        keyStore(name).let { it.getCertificate(it.aliases().nextElement()) as X509Certificate }

    fun fingerprint(name: String): String = Fingerprint.of(certificate(name).encoded)

    /** Servidor TLS em loopback com o certificado [name], como o PC. */
    fun serverSocket(name: String): SSLServerSocket {
        val keyManagers = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm())
            .apply { init(keyStore(name), password) }.keyManagers
        val context = SSLContext.getInstance("TLS").apply { init(keyManagers, null, null) }
        return context.serverSocketFactory.createServerSocket(0, 1, InetAddress.getLoopbackAddress()) as SSLServerSocket
    }
}
