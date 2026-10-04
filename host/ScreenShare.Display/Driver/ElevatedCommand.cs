using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace ScreenShare.Display.Driver;

/// <summary>Reabre o próprio executável como administrador (o Windows mostra o UAC) e espera ele terminar.</summary>
public static class ElevatedCommand
{
    private const int ErrorCancelled = 1223;

    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>SID do usuário atual: o processo elevado o recebe para dar permissão no XML a quem pediu a instalação.</summary>
    public static string CurrentUserSid
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User!.Value;
        }
    }

    /// <summary>
    /// Só o executável do próprio host pode ser reaberto como administrador. Rodando como "dotnet ScreenShare.DevHost.dll",
    /// o caminho é o do dotnet.exe, e reabri-lo não acharia o DevHost.
    /// </summary>
    public static bool CanRelaunch(string? processPath) =>
        processPath is not null && !Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Roda este executável como administrador com os argumentos dados (sem espaços: comandos e SIDs) e devolve o
    /// código de saída. UAC recusado = <see cref="DriverExitCode.UserDeclined"/>. Se não dá para reabrir o executável
    /// (veja <see cref="CanRelaunch"/>) ou o Windows se recusa a iniciá-lo, devolve <see cref="DriverExitCode.SetupFailed"/>.
    /// </summary>
    public static int Run(params string[] arguments)
    {
        if (!CanRelaunch(Environment.ProcessPath)) return (int)DriverExitCode.SetupFailed;

        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = string.Join(' ', arguments),
        };
        try
        {
            using var process = Process.Start(start)!;
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
        {
            return (int)DriverExitCode.UserDeclined;
        }
        catch (Win32Exception)
        {
            return (int)DriverExitCode.SetupFailed;
        }
    }
}
