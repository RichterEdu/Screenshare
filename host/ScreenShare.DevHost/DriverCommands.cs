using ScreenShare.Display.Driver;

namespace ScreenShare.DevHost;

/// <summary>
/// Os modos install-driver, uninstall-driver e restart-driver do DevHost: os únicos que rodam como administrador.
/// Chamados de um terminal comum, eles se reabrem como administrador (UAC).
/// </summary>
public static class DriverCommands
{
    public static bool IsDriverCommand(string[] args) => args is ["install-driver" or "uninstall-driver" or "restart-driver", ..];

    public static async Task<int> RunAsync(string[] args)
    {
        var command = args[0];
        if (!ElevatedCommand.IsElevated)
        {
            if (!ElevatedCommand.CanRelaunch(Environment.ProcessPath))
            {
                Console.WriteLine($"Rode com \"dotnet run --project host/ScreenShare.DevHost -- {command}\" ou pelo ScreenShare.DevHost.exe (não com \"dotnet ScreenShare.DevHost.dll\").");
                return (int)DriverExitCode.SetupFailed;
            }
            var code = command == "install-driver"
                ? ElevatedCommand.Run(command, ElevatedCommand.CurrentUserSid)
                : ElevatedCommand.Run(command);
            Console.WriteLine(Describe(command, code));
            return code;
        }

        DriverExitCode result;
        try
        {
            var installer = new DriverInstaller(new WindowsDriverSystem(), Console.WriteLine);
            result = command switch
            {
                "install-driver" => await installer.InstallAsync(args.Length > 1 ? args[1] : ElevatedCommand.CurrentUserSid),
                "uninstall-driver" => installer.Uninstall(),
                _ => installer.Restart(),
            };
        }
        catch (Exception e)
        {
            Console.WriteLine($"Falha inesperada: {e.Message}");
            result = DriverExitCode.SetupFailed;
        }

        if (result != DriverExitCode.Ok)
        {
            // A janela elevada fecha ao sair: dá tempo de ler o erro.
            Console.WriteLine("Pressione Enter para fechar.");
            Console.ReadLine();
        }
        return (int)result;
    }

    /// <summary>A mensagem para o usuário a partir do código de saída do processo elevado.</summary>
    public static string Describe(string command, int code) => code switch
    {
        (int)DriverExitCode.Ok => command switch
        {
            "install-driver" => "Driver de monitor virtual instalado.",
            "uninstall-driver" => "Driver de monitor virtual removido.",
            _ => "Driver de monitor virtual reiniciado.",
        },
        (int)DriverExitCode.HashMismatch => "O download do driver não confere (SHA-256 diferente); nada foi instalado.",
        (int)DriverExitCode.DownloadFailed => $"Não foi possível baixar o driver. Para instalar à mão: {VddPackage.ReleasePage}",
        (int)DriverExitCode.UserDeclined => "Operação recusada (UAC ou confirmação do Windows).",
        _ => $"O Windows não concluiu a operação no driver de monitor virtual (código {code}).",
    };
}
