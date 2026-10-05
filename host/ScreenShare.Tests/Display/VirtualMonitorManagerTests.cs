using System.ComponentModel;
using Microsoft.Extensions.Time.Testing;
using ScreenShare.Display;

namespace ScreenShare.Tests.Display;

public sealed class VirtualMonitorManagerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screenshare-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new();
    private readonly FakeDisplayTopology _topology = new();
    private readonly List<string> _log = [];
    private FakeRestarter _restarter = new(() => Task.FromResult(true));

    public VirtualMonitorManagerTests()
    {
        Directory.CreateDirectory(_dir);
        VddSettingsFile.WriteMinimal(SettingsPath, monitorCount: 1, [(1920, 1080)]);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string SettingsPath => Path.Combine(_dir, "vdd_settings.xml");
    private DisplayStateStore State => new(Path.Combine(_dir, "display.json"));

    private VirtualMonitorManager Create() => new(_topology, _restarter, State, SettingsPath, _time, message =>
    {
        lock (_log) _log.Add(message);
    });

    /// <summary>Avança o relógio falso em passos de 250 ms até o reinício em segundo plano terminar (ele espera até 5 s).</summary>
    private async Task AdvanceUntilRestartFinishesAsync(VirtualMonitorManager manager)
    {
        for (var step = 0; !manager.PendingRestart.IsCompleted; step++)
        {
            Assert.True(step < 400, "o reinício em segundo plano não terminou");
            _time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(5);
        }
        await manager.PendingRestart;
    }

    [Fact]
    public void Startup_turns_off_a_monitor_left_attached_and_remembers_where_it_was()
    {
        _topology.Attached = true;
        _topology.Position = new DisplayPosition(-1920, 0);

        using var manager = Create();

        Assert.False(_topology.Attached);
        Assert.Equal(new DisplayPosition(-1920, 0), State.LastPosition);
    }

    [Fact]
    public void Startup_with_monitor_off_changes_nothing()
    {
        using var manager = Create();

        Assert.Equal(0, _topology.DetachCalls);
    }

    [Fact]
    public void First_connection_attaches_at_default_position_with_exact_mode_and_scale()
    {
        using var manager = Create();

        using var lease = manager.Acquire(1920, 1080, 280);

        Assert.Equal(new VirtualMonitor(@"\\.\DISPLAY5", 3440, 0, 1920, 1080, 175), lease.Monitor);
        Assert.Equal((1920, 1080), (lease.Width, lease.Height));
        Assert.True(State.IsScaled(1920, 1080));
    }

    [Fact]
    public void Second_connection_shares_the_monitor_as_it_is()
    {
        _topology.Modes.Add((2400, 1080));
        using var manager = Create();

        using var first = manager.Acquire(1920, 1080, 160);
        using var second = manager.Acquire(2400, 1080, 160);

        Assert.Equal((1920, 1080), (second.Monitor!.Width, second.Monitor.Height));
        Assert.Equal(1, _topology.AttachCalls);
        Assert.Equal(0, _topology.SetModeCalls);
        Assert.Equal(0, _restarter.Calls);
    }

    [Fact]
    public void Turns_off_ten_seconds_after_the_last_lease()
    {
        using var manager = Create();
        var first = manager.Acquire(1920, 1080, 160);
        var second = manager.Acquire(1920, 1080, 160);

        first.Dispose();
        _time.Advance(TimeSpan.FromSeconds(11));
        Assert.True(_topology.Attached);

        second.Dispose();
        _time.Advance(TimeSpan.FromSeconds(9.9));
        Assert.True(_topology.Attached);
        _time.Advance(TimeSpan.FromSeconds(0.2));
        Assert.False(_topology.Attached);
    }

    [Fact]
    public void Reconnecting_within_the_delay_keeps_the_monitor_on()
    {
        using var manager = Create();
        manager.Acquire(1920, 1080, 160).Dispose();

        _time.Advance(TimeSpan.FromSeconds(9));
        using var again = manager.Acquire(1920, 1080, 160);
        _time.Advance(TimeSpan.FromSeconds(10));

        Assert.True(_topology.Attached);
        Assert.Equal(0, _topology.DetachCalls);
        Assert.Equal(1, _topology.AttachCalls);
    }

    [Fact]
    public void Another_resolution_during_the_delay_changes_the_mode()
    {
        _topology.Modes.Add((2400, 1080));
        using var manager = Create();
        manager.Acquire(1920, 1080, 160).Dispose();
        _time.Advance(TimeSpan.FromSeconds(2));

        using var lease = manager.Acquire(2400, 1080, 160);

        Assert.Equal((2400, 1080), _topology.Size);
        Assert.Equal(1, _topology.SetModeCalls);
        Assert.Equal(1, _topology.AttachCalls);
    }

    [Fact]
    public void Scale_is_applied_only_the_first_time_for_a_resolution()
    {
        using var manager = Create();
        manager.Acquire(1920, 1080, 320).Dispose();
        _time.Advance(TimeSpan.FromSeconds(11));
        _topology.Scale = 150; // o usuário mudou em Configurações › Tela

        using var lease = manager.Acquire(1920, 1080, 320);

        Assert.Equal(1, _topology.SetScaleCalls);
        Assert.Equal(150, lease.Monitor!.ScalePercent);
    }

    [Fact]
    public void Refused_scale_is_retried_next_time()
    {
        _topology.FailScale = true;
        using var manager = Create();
        manager.Acquire(1920, 1080, 320).Dispose();
        Assert.False(State.IsScaled(1920, 1080));
        _time.Advance(TimeSpan.FromSeconds(11));

        _topology.FailScale = false;
        using var lease = manager.Acquire(1920, 1080, 320);

        Assert.Equal(2, _topology.SetScaleCalls);
        Assert.True(State.IsScaled(1920, 1080));
    }

    [Fact]
    public async Task Unknown_resolution_uses_nearest_now_and_exact_after_driver_restart()
    {
        _restarter = new FakeRestarter(() =>
        {
            // O driver reinicia: relê o XML e a saída volta com outro nome.
            _topology.Modes.Add((2400, 1080));
            _topology.DeviceName = @"\\.\DISPLAY6";
            return Task.FromResult(true);
        });
        using var manager = Create();

        using var lease = manager.Acquire(2400, 1080, 420);
        Assert.Equal((1920, 1080), (lease.Monitor!.Width, lease.Monitor.Height));
        Assert.Contains((2400, 1080), VddSettingsFile.ReadModes(SettingsPath));

        await manager.PendingRestart;

        Assert.Equal(1, _restarter.Calls);
        Assert.Equal((2400, 1080), _topology.Size);
        Assert.Equal(175, _topology.Scale);
        Assert.True(State.IsScaled(2400, 1080));
    }

    [Fact]
    public async Task Declined_restart_is_not_asked_again()
    {
        _restarter = new FakeRestarter(() => Task.FromResult(false));
        using var manager = Create();

        manager.Acquire(2400, 1080, 420).Dispose();
        await manager.PendingRestart;
        using var again = manager.Acquire(2400, 1080, 420);
        await manager.PendingRestart;

        Assert.Equal(1, _restarter.Calls);
        Assert.Equal((1920, 1080), (again.Monitor!.Width, again.Monitor.Height));
    }

    [Fact]
    public async Task Failed_restart_still_settles_the_monitor()
    {
        var restart = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _restarter = new FakeRestarter(() => restart.Task);
        using var manager = Create();
        manager.Acquire(2400, 1080, 420).Dispose();
        _time.Advance(TimeSpan.FromSeconds(11));

        _topology.Attached = true; // o pnputil falhou, mas o driver chegou a reiniciar e o Windows religou a saída
        restart.SetResult(false);
        await manager.PendingRestart;

        Assert.False(_topology.Attached);
    }

    [Fact]
    public async Task Restart_that_throws_still_settles_the_monitor()
    {
        var restart = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _restarter = new FakeRestarter(() => restart.Task);
        using var manager = Create();
        manager.Acquire(2400, 1080, 420).Dispose();
        _time.Advance(TimeSpan.FromSeconds(11));

        _topology.Attached = true; // o processo elevado falhou de um jeito inesperado depois de o driver reiniciar
        restart.SetException(new InvalidOperationException("falha inesperada"));
        await manager.PendingRestart;

        Assert.False(_topology.Attached);
        Assert.Contains(_log, line => line.Contains("Falha ao reiniciar o driver"));
    }

    [Fact]
    public async Task Restart_finishing_with_nobody_connected_leaves_the_monitor_off()
    {
        var restart = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _restarter = new FakeRestarter(() => restart.Task);
        using var manager = Create();
        manager.Acquire(2400, 1080, 420).Dispose();
        _time.Advance(TimeSpan.FromSeconds(11));
        Assert.False(_topology.Attached);

        _topology.Modes.Add((2400, 1080));
        _topology.Attached = true; // o Windows religou a saída quando o driver voltou
        restart.SetResult(true);
        await manager.PendingRestart;

        Assert.False(_topology.Attached);
    }

    [Fact]
    public async Task Restart_without_the_new_mode_leaves_the_monitor_off_when_nobody_is_connected()
    {
        var restart = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _restarter = new FakeRestarter(() => restart.Task);
        using var manager = Create();
        manager.Acquire(2400, 1080, 420).Dispose();
        _time.Advance(TimeSpan.FromSeconds(11));
        Assert.False(_topology.Attached);

        _topology.Attached = true; // o Windows religou a saída, mas o driver voltou sem 2400×1080
        restart.SetResult(true);
        await AdvanceUntilRestartFinishesAsync(manager);

        Assert.False(_topology.Attached);
        Assert.Contains(_log, line => line.Contains("não apareceu"));
    }

    [Fact]
    public async Task Restart_without_the_new_mode_puts_the_monitor_back_for_a_connected_phone()
    {
        var restart = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _restarter = new FakeRestarter(() => restart.Task);
        using var manager = Create();
        using var lease = manager.Acquire(2400, 1080, 420);
        Assert.True(_topology.Attached); // na mais próxima (1920×1080) enquanto o UAC espera

        _topology.Attached = false; // o driver voltou com a saída fora da área de trabalho e sem 2400×1080
        restart.SetResult(true);
        await AdvanceUntilRestartFinishesAsync(manager);

        Assert.True(_topology.Attached);
        Assert.Equal((1920, 1080), _topology.Size);
    }

    [Fact]
    public void Corrupted_settings_file_uses_nearest_mode_without_restart()
    {
        File.WriteAllText(SettingsPath, "<vdd_settings><resolutions>");
        using var manager = Create();

        using var lease = manager.Acquire(2400, 1080, 420);

        Assert.Equal((1920, 1080), (lease.Monitor!.Width, lease.Monitor.Height));
        Assert.Equal(0, _restarter.Calls);
        Assert.Equal("<vdd_settings><resolutions>", File.ReadAllText(SettingsPath));
        Assert.Contains(_log, line => line.Contains("2400×1080"));
    }

    [Fact]
    public void Without_driver_the_lease_has_no_monitor_and_uses_the_normalized_request()
    {
        _topology.Present = false;
        using var manager = Create();

        var lease = manager.Acquire(2273, 1080, 420);
        lease.Dispose();
        _time.Advance(TimeSpan.FromSeconds(11));

        Assert.Null(lease.Monitor);
        Assert.Equal((2272, 1080), (lease.Width, lease.Height));
        Assert.Equal(0, _topology.DetachCalls);
    }

    [Fact]
    public void Topology_errors_become_a_lease_without_monitor()
    {
        _topology.ThrowOnFind = new Win32Exception(5);
        using var manager = Create();

        using var lease = manager.Acquire(1920, 1080, 160);

        Assert.Null(lease.Monitor);
        Assert.Contains(_log, line => line.Contains("Monitor virtual indisponível"));
    }

    [Fact]
    public void Attach_falls_back_to_default_position_when_saved_one_is_refused()
    {
        State.SaveLastPosition(new DisplayPosition(-5000, 0));
        _topology.AcceptPosition = position => position == _topology.Default;
        using var manager = Create();

        using var lease = manager.Acquire(1920, 1080, 160);

        Assert.Equal(_topology.Default, _topology.Position);
        Assert.Equal(new[] { new DisplayPosition(-5000, 0), _topology.Default }, _topology.AttachedAt);
        Assert.NotNull(lease.Monitor);
    }

    [Fact]
    public void Turning_off_remembers_the_position_and_next_attach_uses_it()
    {
        using var manager = Create();
        manager.Acquire(1920, 1080, 160).Dispose();
        _topology.Position = new DisplayPosition(-1920, 200); // o usuário arrastou o monitor em Configurações
        _time.Advance(TimeSpan.FromSeconds(11));

        using var lease = manager.Acquire(1920, 1080, 160);

        Assert.Equal(new DisplayPosition(-1920, 200), State.LastPosition);
        Assert.Equal(new DisplayPosition(-1920, 200), _topology.Position);
    }

    [Fact]
    public void Failing_to_save_state_never_keeps_the_monitor_on()
    {
        File.WriteAllText(Path.Combine(_dir, "arquivo"), "");
        // A "pasta" do display.json é um arquivo: toda gravação do estado falha.
        var brokenState = new DisplayStateStore(Path.Combine(_dir, "arquivo", "display.json"));
        using var manager = new VirtualMonitorManager(_topology, _restarter, brokenState, SettingsPath, _time, message =>
        {
            lock (_log) _log.Add(message);
        });

        var lease = manager.Acquire(1920, 1080, 320);
        Assert.NotNull(lease.Monitor);
        Assert.Equal(175, _topology.Scale); // a escala foi aplicada mesmo sem poder ser lembrada

        lease.Dispose();
        _time.Advance(TimeSpan.FromSeconds(11));

        Assert.False(_topology.Attached);
        Assert.Contains(_log, line => line.Contains("Não foi possível gravar"));
    }

    [Fact]
    public void Error_after_attaching_turns_the_monitor_back_off()
    {
        _topology.ThrowOnGetScale = new Win32Exception(5);
        using var manager = Create();

        using var lease = manager.Acquire(1920, 1080, 160);

        Assert.Null(lease.Monitor);
        Assert.False(_topology.Attached);
    }

    [Fact]
    public void Error_while_another_phone_uses_the_monitor_keeps_it_on()
    {
        using var manager = Create();
        using var first = manager.Acquire(1920, 1080, 160);
        _topology.ThrowOnGetScale = new Win32Exception(5);

        using var second = manager.Acquire(1920, 1080, 160);

        Assert.Null(second.Monitor);
        Assert.True(_topology.Attached);
    }

    [Fact]
    public void Dispose_turns_off_at_once_and_later_acquires_have_no_monitor()
    {
        var manager = Create();
        var lease = manager.Acquire(1920, 1080, 160);

        manager.Dispose();
        lease.Dispose();
        using var late = manager.Acquire(1920, 1080, 160);

        Assert.False(_topology.Attached);
        Assert.Null(late.Monitor);
    }

    [Fact]
    public void Lease_disposed_twice_releases_once()
    {
        using var manager = Create();
        var first = manager.Acquire(1920, 1080, 160);
        using var second = manager.Acquire(1920, 1080, 160);

        first.Dispose();
        first.Dispose();
        _time.Advance(TimeSpan.FromSeconds(11));

        Assert.True(_topology.Attached);
    }

    [Theory]
    [InlineData(2273, 1081, 420, 2272, 1080, 420)]
    [InlineData(100, 100, 0, 640, 360, 72)]
    [InlineData(9000, 5000, 5000, 7680, 4320, 1000)]
    [InlineData(641, 361, 73, 640, 360, 73)]
    public void Request_is_clamped_and_made_even(int width, int height, int dpi, int expectedWidth, int expectedHeight, int expectedDpi)
    {
        Assert.Equal(new MonitorRequest(expectedWidth, expectedHeight, expectedDpi), MonitorRequest.Normalize(width, height, dpi));
    }

    [Fact]
    public void Null_manager_returns_the_normalized_request_without_monitor()
    {
        using var lease = NullVirtualMonitorManager.Instance.Acquire(2273, 1080, 420);

        Assert.Null(lease.Monitor);
        Assert.Equal((2272, 1080), (lease.Width, lease.Height));
    }

    /// <summary>Reinício do driver que só acontece quando o teste manda (para assinar o Changed antes).</summary>
    private TaskCompletionSource GatedRestartThatRenamesTo(string deviceName)
    {
        var go = new TaskCompletionSource();
        _restarter = new FakeRestarter(async () =>
        {
            await go.Task;
            _topology.Modes.Add((2400, 1080));
            _topology.DeviceName = deviceName;
            return true;
        });
        return go;
    }

    [Fact]
    public async Task Driver_restart_renames_the_output_and_fires_Changed_with_the_exact_mode()
    {
        var go = GatedRestartThatRenamesTo(@"\\.\DISPLAY6");
        using var manager = Create();
        using var lease = manager.Acquire(2400, 1080, 420);
        var changes = new List<VirtualMonitor?>();
        lease.Changed += () => changes.Add(lease.Current);

        go.SetResult();
        await manager.PendingRestart;

        var expected = new VirtualMonitor(@"\\.\DISPLAY6", 3440, 0, 2400, 1080, 175);
        Assert.Equal([expected], changes);
        Assert.Equal((2400, 1080), (lease.Width, lease.Height));
        Assert.Equal(1920, lease.Monitor!.Width); // o monitor do Acquire não muda
    }

    [Fact]
    public void Refresh_finds_the_new_name_and_fires_Changed()
    {
        using var manager = Create();
        using var lease = manager.Acquire(1920, 1080, 160);
        var changed = 0;
        lease.Changed += () => changed++;
        _topology.DeviceName = @"\\.\DISPLAY7"; // o driver reiniciou por fora do gerenciador

        var current = lease.Refresh();

        Assert.Equal(@"\\.\DISPLAY7", current!.DeviceName);
        Assert.Equal(current, lease.Current);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void Refresh_without_changes_does_not_fire_Changed()
    {
        using var manager = Create();
        using var lease = manager.Acquire(1920, 1080, 160);
        var changed = 0;
        lease.Changed += () => changed++;

        Assert.Equal(lease.Monitor, lease.Refresh());
        Assert.Equal(0, changed);
    }

    [Fact]
    public async Task Changed_handler_can_call_the_manager_without_deadlock()
    {
        var go = GatedRestartThatRenamesTo(@"\\.\DISPLAY6");
        using var manager = Create();
        using var lease = manager.Acquire(2400, 1080, 420);
        VirtualMonitor? seen = null;
        lease.Changed += () =>
        {
            seen = lease.Refresh();
            manager.Acquire(2400, 1080, 420).Dispose();
        };

        go.SetResult();
        await manager.PendingRestart.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(@"\\.\DISPLAY6", seen!.DeviceName);
    }

    [Fact]
    public async Task Released_lease_is_not_updated_and_the_active_one_is()
    {
        var go = GatedRestartThatRenamesTo(@"\\.\DISPLAY6");
        using var manager = Create();
        var gone = manager.Acquire(2400, 1080, 420);
        using var stays = manager.Acquire(2400, 1080, 420);
        gone.Dispose();

        go.SetResult();
        await manager.PendingRestart;

        Assert.Equal(@"\\.\DISPLAY5", gone.Current!.DeviceName);
        Assert.Equal(@"\\.\DISPLAY6", stays.Current!.DeviceName);
    }

    [Fact]
    public void Lease_without_monitor_has_no_current_and_refresh_returns_null()
    {
        using var lease = NullVirtualMonitorManager.Instance.Acquire(2400, 1080, 420);

        Assert.Null(lease.Current);
        Assert.Null(lease.Refresh());
        Assert.Equal((2400, 1080), (lease.Width, lease.Height));
    }

    private sealed class FakeRestarter(Func<Task<bool>> restart) : IDriverRestarter
    {
        private int _calls;
        public int Calls => _calls;

        public Task<bool> RestartAsync()
        {
            Interlocked.Increment(ref _calls);
            return restart();
        }
    }
}
