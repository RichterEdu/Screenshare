package dev.screenshare.android.protocol

import java.io.File

/** Carrega os vetores compartilhados de docs/protocol-vectors (pares hex; '#' inicia comentário). */
object Vectors {
    // O Gradle roda os testes com o diretório do módulo (android/app) como diretório de trabalho.
    private val dir = File(System.getProperty("user.dir"), "../../docs/protocol-vectors")

    fun load(name: String): ByteArray {
        val file = File(dir, name)
        require(file.isFile) { "vetor não encontrado: ${file.canonicalPath}" }
        return file.readLines()
            .map { it.substringBefore('#') }
            .flatMap { it.trim().split(Regex("\\s+")) }
            .filter { it.isNotEmpty() }
            .map { it.toInt(16).toByte() }
            .toByteArray()
    }
}
