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
        Assert.NotEmpty(_log);
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
