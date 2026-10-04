namespace ScreenShare.Display.Driver;

/// <summary>
/// As operações do Windows que a instalação do driver usa. Ficam atrás desta interface para a sequência
/// (<see cref="DriverInstaller"/>) ser testada sem instalar nada de verdade.
/// </summary>
public interface IDriverSystem
{
    /// <summary>Existe um dispositivo Root\MttVDD presente e com driver? Não exige administrador.</summary>
    bool IsInstalled();

    Task<byte[]> DownloadAsync(Uri url, CancellationToken cancellationToken);

    /// <summary>Extrai o zip numa pasta temporária nova e devolve o caminho dela.</summary>
    string Extract(byte[] zip);

    void DeleteDirectory(string path);

    /// <summary>Impressões digitais dos certificados em TrustedPublisher (máquina).</summary>
    IReadOnlySet<string> TrustedPublisherThumbprints();

    /// <summary>Impressões digitais dos certificados contidos no catálogo assinado (.cat).</summary>
    IReadOnlySet<string> CatalogThumbprints(string catalogPath);

    void RemoveTrustedPublishers(IEnumerable<string> thumbprints);

    /// <summary>Cria o dispositivo Root\MttVDD e instala o .inf. Devolve 0 ou o erro do Windows.</summary>
    int InstallDevice(string infPath);

    /// <summary>Cria o XML mínimo se faltar e dá permissão de modificação na pasta ao usuário e a LOCAL SERVICE.</summary>
    void PrepareSettings(string settingsPath, string userSid);

    /// <summary>Remove todos os dispositivos Root\MttVDD e o pacote do driver. Devolve 0 ou o erro do Windows.</summary>
    int UninstallDevices();

    void DeleteSettings(string settingsDirectory);

    /// <summary>Reinicia os dispositivos Root\MttVDD presentes (o driver relê o XML). Devolve 0 ou o código do pnputil.</summary>
    int RestartDevices();
}
