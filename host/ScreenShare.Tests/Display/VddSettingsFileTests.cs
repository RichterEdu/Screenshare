using System.Text;
using System.Xml.Linq;
using ScreenShare.Display;

namespace ScreenShare.Tests.Display;

public sealed class VddSettingsFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screenshare-tests", Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_dir, "vdd_settings.xml");

    public VddSettingsFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // Trecho do arquivo que vem no zip do VDD 24.12.24, com comentário e elementos que não conhecemos.
    private const string OfficialSample = """
        <?xml version='1.0' encoding='utf-8'?>
        <vdd_settings>
            <monitors>
                <count>1</count>
            </monitors>
            <gpu>
                <friendlyname>default</friendlyname>
            </gpu>
        	<global>
        		<!--These are global refreshrates-->
        		<g_refresh_rate>60</g_refresh_rate>
        		<g_refresh_rate>90</g_refresh_rate>
        	</global>
            <resolutions>
                <resolution>
                    <width>1920</width>
                    <height>1080</height>
                    <refresh_rate>30</refresh_rate>
                </resolution>
            </resolutions>
            <options>
        		<CustomEdid>false</CustomEdid> <!-- Custom Edid should be named "user_edid.bin"! -->
            </options>
        </vdd_settings>
        """;

    [Fact]
    public void WriteMinimal_creates_file_the_driver_can_read()
    {
        VddSettingsFile.WriteMinimal(SettingsPath, monitorCount: 1, [(1920, 1080)]);

        var doc = XDocument.Load(SettingsPath);
        Assert.Equal("vdd_settings", doc.Root!.Name.LocalName);
        Assert.Equal("1", doc.Root.Element("monitors")!.Element("count")!.Value);
        Assert.Equal("default", doc.Root.Element("gpu")!.Element("friendlyname")!.Value);
        Assert.Equal("60", doc.Root.Element("global")!.Element("g_refresh_rate")!.Value);
        Assert.Equal(new[] { (1920, 1080) }, VddSettingsFile.ReadModes(SettingsPath));
        var resolution = doc.Root.Element("resolutions")!.Element("resolution")!;
        Assert.Equal("60", resolution.Element("refresh_rate")!.Value);
    }

    [Fact]
    public void WriteMinimal_creates_missing_directory()
    {
        var nested = Path.Combine(_dir, "VirtualDisplayDriver", "vdd_settings.xml");

        VddSettingsFile.WriteMinimal(nested, monitorCount: 0, [(1920, 1080)]);

        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void EnsureMode_adds_missing_mode_and_keeps_everything_else()
    {
        File.WriteAllText(SettingsPath, OfficialSample);

        Assert.True(VddSettingsFile.EnsureMode(SettingsPath, 2400, 1080));

        Assert.Equal(new[] { (1920, 1080), (2400, 1080) }, VddSettingsFile.ReadModes(SettingsPath));
        var text = File.ReadAllText(SettingsPath);
        Assert.Contains("<!--These are global refreshrates-->", text);
        Assert.Contains("<CustomEdid>false</CustomEdid>", text);
        Assert.Contains("<g_refresh_rate>90</g_refresh_rate>", text);
        var added = XDocument.Load(SettingsPath).Root!.Element("resolutions")!.Elements("resolution").Last();
        Assert.Equal("60", added.Element("refresh_rate")!.Value);
    }

    [Fact]
    public void EnsureMode_does_not_duplicate_existing_mode_whatever_its_refresh_rate()
    {
        File.WriteAllText(SettingsPath, OfficialSample); // 1920×1080 está lá com 30 Hz
        var before = File.ReadAllText(SettingsPath);

        Assert.False(VddSettingsFile.EnsureMode(SettingsPath, 1920, 1080));

        Assert.Equal(before, File.ReadAllText(SettingsPath)); // nem regravou
    }

    [Fact]
    public void EnsureMode_creates_minimal_file_when_missing()
    {
        Assert.True(VddSettingsFile.EnsureMode(SettingsPath, 2400, 1080));

        Assert.Equal(new[] { (2400, 1080) }, VddSettingsFile.ReadModes(SettingsPath));
        Assert.Equal("1", XDocument.Load(SettingsPath).Root!.Element("monitors")!.Element("count")!.Value);
    }

    [Fact]
    public void EnsureMode_creates_resolutions_element_when_absent()
    {
        File.WriteAllText(SettingsPath, "<vdd_settings><monitors><count>1</count></monitors></vdd_settings>");

        Assert.True(VddSettingsFile.EnsureMode(SettingsPath, 2400, 1080));

        Assert.Equal(new[] { (2400, 1080) }, VddSettingsFile.ReadModes(SettingsPath));
    }

    [Fact]
    public void ReadModes_skips_entries_that_are_not_numbers()
    {
        File.WriteAllText(SettingsPath, """
            <vdd_settings><resolutions>
              <resolution><width>abc</width><height>1080</height></resolution>
              <resolution><width>2400</width><height>1080</height></resolution>
            </resolutions></vdd_settings>
            """);

        Assert.Equal(new[] { (2400, 1080) }, VddSettingsFile.ReadModes(SettingsPath));
    }

    [Fact]
    public void Corrupted_xml_raises_clear_error_and_is_not_overwritten()
    {
        File.WriteAllText(SettingsPath, "<vdd_settings><resolutions>");

        var error = Assert.Throws<VddSettingsException>(() => VddSettingsFile.EnsureMode(SettingsPath, 2400, 1080));

        Assert.Contains(SettingsPath, error.Message);
        Assert.Equal("<vdd_settings><resolutions>", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Wrong_root_element_raises_clear_error()
    {
        File.WriteAllText(SettingsPath, "<outra_coisa/>");

        Assert.Throws<VddSettingsException>(() => VddSettingsFile.ReadModes(SettingsPath));
    }

    [Fact]
    public void Writing_leaves_no_temporary_file_behind()
    {
        File.WriteAllText(SettingsPath, OfficialSample);

        VddSettingsFile.EnsureMode(SettingsPath, 2400, 1080);

        Assert.Equal(new[] { "vdd_settings.xml" }, Directory.GetFiles(_dir).Select(f => Path.GetFileName(f)));
    }

    [Fact]
    public void Saved_file_is_utf8_without_bom_and_keeps_other_encoding_mentions()
    {
        File.WriteAllText(SettingsPath, """
            <?xml version='1.0' encoding='utf-8'?>
            <vdd_settings>
              <!-- exemplo: <?xml version="1.0" encoding="utf-16"?> -->
              <gpu><friendlyname>Placa de vídeo — ação</friendlyname></gpu>
              <resolutions>
                <resolution><width>1920</width><height>1080</height></resolution>
              </resolutions>
            </vdd_settings>
            """);

        VddSettingsFile.EnsureMode(SettingsPath, 2400, 1080);

        var bytes = File.ReadAllBytes(SettingsPath);
        Assert.Equal("<?xml"u8.ToArray(), bytes[..5]); // sem BOM
        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", text);
        Assert.Contains("<!-- exemplo: <?xml version=\"1.0\" encoding=\"utf-16\"?> -->", text);
        Assert.Contains("Placa de vídeo — ação", text);
    }

    [Fact]
    public void Added_resolution_goes_on_its_own_line_with_the_file_indentation()
    {
        File.WriteAllText(SettingsPath, OfficialSample);

        VddSettingsFile.EnsureMode(SettingsPath, 2400, 1080);

        var text = File.ReadAllText(SettingsPath).Replace("\r\n", "\n");
        Assert.Contains(
            "</resolution>\n        <resolution><width>2400</width><height>1080</height><refresh_rate>60</refresh_rate></resolution>\n    </resolutions>",
            text);
    }

    [Fact]
    public void Failed_write_keeps_the_original_file_and_leaves_no_temporary()
    {
        File.WriteAllText(SettingsPath, OfficialSample);

        // Outro programa com o arquivo aberto sem permitir apagar: dá para ler, mas não para trocar.
        using (new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Record.Exception(() => VddSettingsFile.EnsureMode(SettingsPath, 2400, 1080));
            Assert.True(error is IOException or UnauthorizedAccessException, $"erro inesperado: {error}");
        }

        Assert.Equal(OfficialSample, File.ReadAllText(SettingsPath));
        Assert.Equal(new[] { "vdd_settings.xml" }, Directory.GetFiles(_dir).Select(f => Path.GetFileName(f)));
    }
}
