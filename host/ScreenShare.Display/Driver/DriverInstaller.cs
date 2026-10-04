namespace ScreenShare.Display.Driver;

/// <summary>Códigos de saída dos modos install-driver, uninstall-driver e restart-driver.</summary>
public enum DriverExitCode
{
    Ok = 0,
    HashMismatch = 2,
    DownloadFailed = 3,
    SetupFailed = 4,
    UserDeclined = 5,
}

/// <summary>
/// Instala, remove e reinicia o Virtual Display Driver. Roda como administrador (o DevHost se reabre com UAC para isso).
/// </summary>
public sealed class DriverInstaller(IDriverSystem system, Action<string> log, string expectedSha256 = VddPackage.Sha256)
{
    // Erros do Windows que significam "o usuário disse não" na confirmação de instalação do driver.
    private const int ErrorCancelled = 1223;
    private const int ErrorAuthenticodePublisherNotTrusted = unchecked((int)0xE0000243);
    private const int ErrorAuthenticodeTrustNotEstablished = unchecked((int)0xE0000242);

    public async Task<DriverExitCode> InstallAsync(string userSid, CancellationToken cancellationToken = default)
    {
        if (system.IsInstalled())
        {
            system.PrepareSettings(VddSettingsFile.DefaultPath, userSid);
            log("O driver de monitor virtual já está instalado.");
            return DriverExitCode.Ok;
        }

        byte[] zip;
        try
        {
            log($"Baixando o Virtual Display Driver de {VddPackage.Url} ...");
            zip = await system.DownloadAsync(VddPackage.Url, cancellationToken);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException)
        {
            log($"Não foi possível baixar o driver: {e.Message}");
            log($"Para instalar à mão: {VddPackage.ReleasePage}");
            return DriverExitCode.DownloadFailed;
        }
        if (!VddPackage.HasHash(zip, expectedSha256))
        {
            log("O arquivo baixado não é o esperado (SHA-256 diferente). Nada foi instalado.");
            return DriverExitCode.HashMismatch;
        }

        var directory = system.Extract(zip);
        try
        {
            // Lê o catálogo antes de instalar: se ele não abrir, nada foi instalado ainda.
            var fromCatalog = system.CatalogThumbprints(Path.Combine(directory, VddPackage.CatalogRelativePath));
            var trustedBefore = system.TrustedPublisherThumbprints();
            log("Instalando o driver (o Windows vai pedir confirmação) ...");
            var error = system.InstallDevice(Path.Combine(directory, VddPackage.InfRelativePath));
            ForgetPublisherTrustedByTheInstall(trustedBefore, fromCatalog);
            if (error != 0) return Failed(error);

            system.PrepareSettings(VddSettingsFile.DefaultPath, userSid);
            log("Driver de monitor virtual instalado.");
            return DriverExitCode.Ok;
        }
        finally
        {
            system.DeleteDirectory(directory);
        }
    }

    public DriverExitCode Uninstall()
    {
        var error = system.UninstallDevices();
        if (error != 0)
        {
            log($"O Windows não removeu o driver (erro 0x{error:X8}).");
            return DriverExitCode.SetupFailed;
        }
        system.DeleteSettings(VddPackage.SettingsDirectory);
        log("Driver de monitor virtual removido.");
        return DriverExitCode.Ok;
    }

    public DriverExitCode Restart()
    {
        var code = system.RestartDevices();
        if (code != 0)
        {
            log($"Não foi possível reiniciar o driver de monitor virtual (código {code}).");
            return DriverExitCode.SetupFailed;
        }
        log("Driver de monitor virtual reiniciado.");
        return DriverExitCode.Ok;
    }

    /// <summary>
    /// A confirmação do Windows vem com "Sempre confiar em software de ..." marcado, o que grava o certificado do
    /// editor em TrustedPublisher. O driver instalado não precisa disso: tira o que entrou agora e veio do catálogo.
    /// </summary>
    private void ForgetPublisherTrustedByTheInstall(IReadOnlySet<string> trustedBefore, IReadOnlySet<string> fromCatalog)
    {
        var added = system.TrustedPublisherThumbprints()
            .Where(thumbprint => !trustedBefore.Contains(thumbprint) && fromCatalog.Contains(thumbprint))
            .ToList();
        if (added.Count == 0) return;
        system.RemoveTrustedPublishers(added);
        log("Confiança permanente no editor do driver removida (o driver instalado não precisa dela).");
    }

    private DriverExitCode Failed(int error)
    {
        if (error is ErrorCancelled or ErrorAuthenticodePublisherNotTrusted or ErrorAuthenticodeTrustNotEstablished)
        {
            log($"A instalação do driver foi recusada (0x{error:X8}).");
            return DriverExitCode.UserDeclined;
        }
        log($"O Windows não instalou o driver (erro 0x{error:X8}).");
        return DriverExitCode.SetupFailed;
    }
}
