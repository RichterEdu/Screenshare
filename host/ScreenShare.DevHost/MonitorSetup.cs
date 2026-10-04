using ScreenShare.Display;
using ScreenShare.Display.Driver;

namespace ScreenShare.DevHost;

/// <summary>Prepara o monitor virtual ao iniciar: oferece instalar o driver se faltar.</summary>
public static class MonitorSetup
{
    /// <summary>O gerenciador do monitor virtual, ou null para seguir sem monitor (driver ausente e não instalado).</summary>
    public static VirtualMonitorManager? Create(Action<string> log)
    {
        var system = new WindowsDriverSystem();
        if (!system.IsInstalled() && !OfferInstall(system, log)) return null;
        return new VirtualMonitorManager(new DisplayTopology(), new ElevatedDriverRestarter(),
            new DisplayStateStore(DisplayStateStore.DefaultPath), VddSettingsFile.DefaultPath, TimeProvider.System, log);
    }

    /// <summary>Resposta a uma pergunta [S/n]: Enter vazio conta como sim.</summary>
    public static bool IsYes(string answer) => answer.Trim().ToLowerInvariant() is "" or "s" or "sim" or "y" or "yes";

    private static bool OfferInstall(WindowsDriverSystem system, Action<string> log)
    {
        Console.Write("O driver de monitor virtual não está instalado. Instalar agora? [S/n] ");
        var answer = Console.ReadLine();
        if (answer is null || !IsYes(answer))
        {
            log("Seguindo sem monitor virtual. Para instalar depois, rode o DevHost de novo e responda S.");
            return false;
        }
        var code = ElevatedCommand.Run("install-driver", ElevatedCommand.CurrentUserSid);
        Console.WriteLine(DriverCommands.Describe("install-driver", code));
        if (code == (int)DriverExitCode.Ok && system.IsInstalled()) return true;
        log("Seguindo sem monitor virtual.");
        return false;
    }
}
