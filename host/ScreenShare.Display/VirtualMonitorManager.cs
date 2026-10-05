namespace ScreenShare.Display;

/// <summary>
/// Liga o monitor virtual quando um celular conecta e o desliga 10 s depois da última sessão. Um monitor só, com
/// contagem de referências: uma segunda sessão simultânea recebe o monitor como está. Resolução que o driver ainda não
/// conhece vai para o XML e pede um reinício do driver (UAC) em segundo plano; enquanto isso vale a mais próxima.
/// Nunca lança por causa do monitor: falhas viram lease sem monitor.
/// </summary>
public sealed class VirtualMonitorManager : IVirtualMonitorManager, IDisposable
{
    public static readonly TimeSpan TurnOffDelay = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan OutputWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly Lock _gate = new();
    private readonly IDisplayTopology _topology;
    private readonly IDriverRestarter _restarter;
    private readonly DisplayStateStore _state;
    private readonly string _settingsPath;
    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private readonly HashSet<(int Width, int Height)> _newModesRequested = [];
    private readonly List<VirtualMonitorLease> _active = [];
    private readonly List<VirtualMonitorLease> _toNotify = [];
    private int _leases;
    private ITimer? _turnOff;
    private bool _disposed;

    public VirtualMonitorManager(IDisplayTopology topology, IDriverRestarter restarter, DisplayStateStore state,
        string settingsPath, TimeProvider time, Action<string>? log = null)
    {
        _topology = topology;
        _restarter = restarter;
        _state = state;
        _settingsPath = settingsPath;
        _time = time;
        _log = log ?? (_ => { });
        // Um monitor que ficou na área de trabalho (host que caiu antes) sai dela: ninguém está conectado ainda.
        TurnOff("ao iniciar");
    }

    /// <summary>O reinício do driver em andamento (resolução nova), para os testes esperarem.</summary>
    internal Task PendingRestart { get; private set; } = Task.CompletedTask;

    public VirtualMonitorLease Acquire(int width, int height, int densityDpi)
    {
        try
        {
            return AcquireUnderLock(MonitorRequest.Normalize(width, height, densityDpi));
        }
        finally
        {
            NotifyChanged();
        }
    }

    private VirtualMonitorLease AcquireUnderLock(MonitorRequest request)
    {
        lock (_gate)
        {
            if (_disposed) return VirtualMonitorLease.Without(request);
            VirtualMonitor? monitor;
            try
            {
                monitor = Apply(request, shareIfInUse: true);
            }
            catch (Exception e)
            {
                _log($"Monitor virtual indisponível nesta conexão: {e.Message}");
                // Se chegou a ligar antes de falhar, não fica ligado sem ninguém usando.
                if (_leases == 0 && _turnOff is null) TurnOff("falha ao ligar");
                monitor = null;
            }
            if (monitor is null) return VirtualMonitorLease.Without(request);
            UpdateLeases(monitor); // quem já usa o monitor passa a ver o nome e o tamanho de agora
            _leases++;
            CancelTurnOff();
            VirtualMonitorLease? lease = null;
            lease = new VirtualMonitorLease(request, monitor, () => Release(lease!), () => Refresh(lease!));
            _active.Add(lease);
            return lease;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            CancelTurnOff();
            TurnOff("host encerrado");
        }
    }

    /// <summary>Liga ou ajusta o monitor para o pedido. Chamado com a trava.</summary>
    private VirtualMonitor? Apply(MonitorRequest request, bool shareIfInUse)
    {
        var output = _topology.FindVddOutput();
        if (output is null)
        {
            _log("Saída do monitor virtual não encontrada; seguindo sem monitor.");
            return null;
        }
        if (shareIfInUse && _leases > 0 && output.Attached) return Describe(output);
        if (output.Modes.Count == 0)
        {
            _log("O driver de monitor virtual não oferece nenhuma resolução.");
            return null;
        }

        var wanted = (request.Width, request.Height);
        var exact = output.Modes.Contains(wanted);
        if (!exact) RequestNewMode(request);
        var (width, height) = exact ? wanted : Nearest(output.Modes, wanted);

        if (!output.Attached)
        {
            if (!AttachSomewhere(output.DeviceName, width, height)) return null;
        }
        else if ((output.Width, output.Height) != (width, height) && !_topology.SetMode(output.DeviceName, width, height))
        {
            _log($"O Windows recusou {width}×{height}; o monitor continua em {output.Width}×{output.Height}.");
        }

        var applied = _topology.FindVddOutput();
        if (applied is not { Attached: true })
        {
            _log("O monitor virtual não ficou ativo.");
            return null;
        }
        if ((applied.Width, applied.Height) == wanted) ApplyScaleOnce(applied.DeviceName, request);
        return Describe(applied);
    }

    private bool AttachSomewhere(string deviceName, int width, int height)
    {
        var fallback = _topology.DefaultPosition();
        if (_state.LastPosition is { } saved && saved != fallback && _topology.Attach(deviceName, saved, width, height)) return true;
        if (_topology.Attach(deviceName, fallback, width, height)) return true;
        _log("O Windows não aceitou ligar o monitor virtual.");
        return false;
    }

    private void ApplyScaleOnce(string deviceName, MonitorRequest request)
    {
        if (_state.IsScaled(request.Width, request.Height)) return;
        var percent = ScaleCalculator.FromDpi(request.DensityDpi);
        if (!_topology.SetScale(deviceName, percent))
        {
            _log($"Não foi possível ajustar a escala do monitor virtual para {percent}%.");
            return;
        }
        TrySave(() => _state.MarkScaled(request.Width, request.Height));
    }

    /// <summary>Gravar o display.json é conveniência: se falhar, registra e segue (nunca impede ligar ou desligar).</summary>
    private void TrySave(Action save)
    {
        try
        {
            save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log($"Não foi possível gravar o estado do monitor virtual: {e.Message}");
        }
    }

    private VirtualMonitor Describe(VddOutput output) => new(output.DeviceName, output.X, output.Y, output.Width, output.Height,
        _topology.GetScale(output.DeviceName) ?? ScaleCalculator.MinPercent);

    private static (int Width, int Height) Nearest(IReadOnlyList<(int Width, int Height)> modes, (int Width, int Height) wanted) =>
        modes.MinBy(mode => Math.Abs(mode.Width - wanted.Width) + Math.Abs(mode.Height - wanted.Height));

    /// <summary>Uma vez por resolução por execução do host: põe no XML e reinicia o driver em segundo plano.</summary>
    private void RequestNewMode(MonitorRequest request)
    {
        if (!_newModesRequested.Add((request.Width, request.Height))) return;
        try
        {
            VddSettingsFile.EnsureMode(_settingsPath, request.Width, request.Height);
        }
        catch (Exception e) when (e is VddSettingsException or IOException or UnauthorizedAccessException)
        {
            _log($"Não foi possível acrescentar {request.Width}×{request.Height} à configuração do driver: {e.Message}");
            return;
        }
        _log($"Resolução nova {request.Width}×{request.Height}: o Windows vai pedir permissão para reiniciar o driver de monitor virtual (só desta vez).");
        PendingRestart = Task.Run(() => RestartThenApplyAsync(request));
    }

    private async Task RestartThenApplyAsync(MonitorRequest request)
    {
        try
        {
            await RestartThenApplyUnderLocksAsync(request);
        }
        finally
        {
            NotifyChanged();
        }
    }

    private async Task RestartThenApplyUnderLocksAsync(MonitorRequest request)
    {
        // Só começa depois que o Acquire que pediu termina de ligar o monitor (ele ainda segura a trava).
        lock (_gate)
        {
            if (_disposed) return;
        }

        bool restarted;
        try
        {
            restarted = await _restarter.RestartAsync();
        }
        catch (Exception e)
        {
            _log($"Falha ao reiniciar o driver de monitor virtual: {e.Message}");
            SettleUnderLock(request);
            return;
        }
        if (!restarted)
        {
            _log($"O driver não foi reiniciado; {request.Width}×{request.Height} fica para a próxima execução do host.");
            // O reinício pode ter acontecido em parte (o pnputil falhou depois de reiniciar): acerta do mesmo jeito.
            SettleUnderLock(request);
            return;
        }

        var deadline = _time.GetUtcNow() + OutputWait;
        while (true)
        {
            lock (_gate)
            {
                if (_disposed) return;
                try
                {
                    var output = _topology.FindVddOutput();
                    if (output is not null && output.Modes.Contains((request.Width, request.Height)))
                    {
                        if (_leases > 0)
                        {
                            if (Apply(request, shareIfInUse: false) is { } monitor)
                            {
                                _log($"Monitor virtual agora em {monitor.Width}×{monitor.Height} (escala {monitor.ScalePercent}%).");
                                UpdateLeases(monitor);
                            }
                        }
                        else if (_turnOff is null)
                        {
                            TurnOff("depois de reiniciar o driver");
                        }
                        return;
                    }
                }
                catch (Exception e)
                {
                    _log($"Falha ao aplicar a resolução nova: {e.Message}");
                    SettleAfterRestart(request);
                    return;
                }
            }
            if (_time.GetUtcNow() >= deadline)
            {
                _log($"O driver reiniciou, mas {request.Width}×{request.Height} não apareceu.");
                SettleUnderLock(request);
                return;
            }
            await Task.Delay(PollInterval, _time);
        }
    }

    /// <summary>
    /// Depois de um reinício que não trouxe a resolução nova (ou que falhou no meio): deixa o monitor coerente com
    /// quem está conectado — ligado, na resolução mais próxima, se há sessão; desligado se não há e nada está agendado.
    /// O driver reiniciado pode voltar com a saída ligada ou desligada. Chamado com a trava; nunca lança.
    /// </summary>
    private void SettleAfterRestart(MonitorRequest request)
    {
        try
        {
            if (_leases > 0)
            {
                if (Apply(request, shareIfInUse: true) is { } monitor) UpdateLeases(monitor);
            }
            else if (_turnOff is null)
            {
                TurnOff("depois de reiniciar o driver");
            }
        }
        catch (Exception e)
        {
            _log($"Falha ao acertar o monitor virtual depois do reinício: {e.Message}");
        }
    }

    /// <summary><see cref="SettleAfterRestart"/> tomando a trava (para os caminhos fora dela).</summary>
    private void SettleUnderLock(MonitorRequest request)
    {
        lock (_gate)
        {
            if (!_disposed) SettleAfterRestart(request);
        }
    }

    /// <summary>
    /// Relê a saída agora (a captura não achou o nome que tinha) e atualiza todas as sessões. Devolve o monitor atual
    /// desta sessão.
    /// </summary>
    private VirtualMonitor? Refresh(VirtualMonitorLease lease)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed || !_active.Contains(lease)) return lease.Current;
                try
                {
                    if (_topology.FindVddOutput() is { Attached: true } output) UpdateLeases(Describe(output));
                }
                catch (Exception e)
                {
                    _log($"Falha ao reler o monitor virtual: {e.Message}");
                }
                return lease.Current;
            }
        }
        finally
        {
            NotifyChanged();
        }
    }

    /// <summary>Põe o monitor novo em todas as sessões e anota quais mudaram. Chamado com a trava.</summary>
    private void UpdateLeases(VirtualMonitor monitor)
    {
        foreach (var lease in _active)
        {
            if (lease.Update(monitor) && !_toNotify.Contains(lease)) _toNotify.Add(lease);
        }
    }

    /// <summary>
    /// Dispara Changed nas sessões cujo monitor mudou. Sempre fora da trava: quem recebe o aviso pode chamar o
    /// gerenciador (Refresh, Acquire) sem travar.
    /// </summary>
    private void NotifyChanged()
    {
        VirtualMonitorLease[] changed;
        lock (_gate)
        {
            if (_toNotify.Count == 0) return;
            changed = [.. _toNotify];
            _toNotify.Clear();
        }
        foreach (var lease in changed)
        {
            try
            {
                lease.RaiseChanged();
            }
            catch (Exception e)
            {
                _log($"Falha ao avisar a sessão da mudança do monitor virtual: {e.Message}");
            }
        }
    }

    private void Release(VirtualMonitorLease lease)
    {
        lock (_gate)
        {
            _active.Remove(lease);
            if (_disposed || --_leases > 0) return;
            CancelTurnOff();
            ITimer? timer = null;
            timer = _time.CreateTimer(_ =>
            {
                lock (_gate)
                {
                    if (_disposed || _leases > 0 || !ReferenceEquals(_turnOff, timer)) return;
                    CancelTurnOff();
                    TurnOff("sem celular conectado");
                }
            }, null, TurnOffDelay, Timeout.InfiniteTimeSpan);
            _turnOff = timer;
        }
    }

    private void CancelTurnOff()
    {
        _turnOff?.Dispose();
        _turnOff = null;
    }

    /// <summary>Tira o monitor da área de trabalho, guardando onde ele estava. Nunca lança.</summary>
    private void TurnOff(string reason)
    {
        try
        {
            if (_topology.FindVddOutput() is not { Attached: true } output) return;
            TrySave(() => _state.SaveLastPosition(new DisplayPosition(output.X, output.Y)));
            _log(_topology.Detach(output.DeviceName)
                ? $"Monitor virtual desligado ({reason})."
                : "O Windows não aceitou desligar o monitor virtual.");
        }
        catch (Exception e)
        {
            _log($"Falha ao desligar o monitor virtual: {e.Message}");
        }
    }
}
