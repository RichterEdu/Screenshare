using ScreenShare.Display;

namespace ScreenShare.Tests.Display;

public sealed class DisplayStateStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screenshare-tests", Guid.NewGuid().ToString("N"));
    private string StorePath => Path.Combine(_dir, "ScreenShare", "display.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Missing_file_has_nothing()
    {
        var store = new DisplayStateStore(StorePath);

        Assert.False(store.IsScaled(2400, 1080));
        Assert.Null(store.LastPosition);
    }

    [Fact]
    public void Scaled_resolution_survives_a_new_instance()
    {
        new DisplayStateStore(StorePath).MarkScaled(2400, 1080);

        var reopened = new DisplayStateStore(StorePath);
        Assert.True(reopened.IsScaled(2400, 1080));
        Assert.False(reopened.IsScaled(1080, 2400));
    }

    [Fact]
    public void Marking_twice_keeps_a_single_entry()
    {
        var store = new DisplayStateStore(StorePath);
        store.MarkScaled(2400, 1080);
        store.MarkScaled(2400, 1080);

        Assert.Equal(1, File.ReadAllText(StorePath).Split("2400x1080").Length - 1);
    }

    [Fact]
    public void Last_position_survives_and_keeps_scaled_resolutions()
    {
        var store = new DisplayStateStore(StorePath);
        store.MarkScaled(2400, 1080);
        store.SaveLastPosition(new DisplayPosition(-2400, 120));

        var reopened = new DisplayStateStore(StorePath);
        Assert.Equal(new DisplayPosition(-2400, 120), reopened.LastPosition);
        Assert.True(reopened.IsScaled(2400, 1080));
    }

    [Theory]
    [InlineData("isto não é json")]
    [InlineData("{\"scaled\": 42}")]
    [InlineData("{\"lastPosition\": \"x\"}")]
    [InlineData("null")]
    [InlineData("")]
    public void Corrupted_file_counts_as_empty_and_is_rewritten(string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, contents);
        var store = new DisplayStateStore(StorePath);

        Assert.False(store.IsScaled(2400, 1080));
        Assert.Null(store.LastPosition);
        store.MarkScaled(2400, 1080);

        Assert.True(new DisplayStateStore(StorePath).IsScaled(2400, 1080));
    }

    [Fact]
    public void Default_path_is_under_local_app_data()
    {
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenShare", "display.json"),
            DisplayStateStore.DefaultPath);
    }
}
