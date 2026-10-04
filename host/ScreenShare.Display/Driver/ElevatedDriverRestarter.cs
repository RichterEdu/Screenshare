namespace ScreenShare.Display.Driver;

/// <summary>Reinicia o driver rodando este executável com "restart-driver" como administrador (UAC).</summary>
public sealed class ElevatedDriverRestarter : IDriverRestarter
{
    public Task<bool> RestartAsync() =>
        Task.Run(() => ElevatedCommand.Run("restart-driver") == (int)DriverExitCode.Ok);
}
