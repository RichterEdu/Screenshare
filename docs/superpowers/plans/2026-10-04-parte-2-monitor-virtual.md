# Parte 2: monitor virtual, plano de implementação

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Quando um celular conecta, o Windows ganha um monitor de verdade (VDD) na resolução do celular, com escala automática. Quando a última sessão termina, o monitor sai da área de trabalho 10 s depois. O host instala o driver sozinho, com UAC, se ele faltar.

**Architecture:** Projeto novo `host/ScreenShare.Display` (`net10.0-windows`), com a lógica pura (XML do VDD, escala, estado salvo, gerenciador com contagem de referências) separada das chamadas ao Windows (SetupAPI, `ChangeDisplaySettingsEx`/CCD, `pnputil`), que ficam atrás de interfaces (`IDriverSystem`, `IDisplayTopology`, `IDriverRestarter`). O `HostServer` do DevHost recebe um `IVirtualMonitorManager` e manda no `CONFIG` a resolução realmente aplicada.

**Tech Stack:** C# / .NET 10, xUnit, P/Invoke (setupapi, newdev, user32), System.Text.Json, System.Xml.Linq, System.Security.Cryptography.Pkcs (ler o `.cat`), `Microsoft.Extensions.TimeProvider.Testing` (testes).

**Spec:** `docs/superpowers/specs/2026-10-04-parte-2-monitor-virtual-design.md` (revisado com os resultados do spike)

## Global Constraints

- Pacote: `https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/25.7.23/VirtualDisplayDriver-x86.Driver.Only.zip`, SHA-256 `e24210692b442b39af763536330ce78b423f19342b7a7792c26de3944e418b3a`; arquivos `VirtualDisplayDriver\MttVDD.inf` e `VirtualDisplayDriver\mttvdd.cat`; página do release `https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/tag/25.7.23`.
- ID de hardware `Root\MttVDD`; classe Display `{4d36e968-e325-11ce-bfc1-08002be10318}`.
- Configuração do VDD: `C:\VirtualDisplayDriver\vdd_settings.xml`, sempre com `count 1`; resoluções acrescentadas com 60 Hz.
- **O pipe do VDD não é usado.** Ligar = anexar com `ChangeDisplaySettingsEx` (posição + tamanho); desligar = desanexar (0×0). A saída é achada sempre pelo `DeviceID = Root\MttVDD`; o nome `\\.\DISPLAYn` nunca é guardado.
- Resolução nova: acrescentar no XML (sem admin) e reiniciar o driver com `pnputil /restart-device` (admin, modo `restart-driver`), uma vez por resolução por execução do host, em segundo plano.
- Esperar o driver voltar com a resolução nova: até 5 s. Desligar depois da última sessão: 10 s.
- Pedido normalizado: largura 640–7680, altura 360–4320, as duas arredondadas para baixo até número par; DPI 72–1000.
- Escala: `DPI ÷ 160 × 100`, arredondada (meio para cima) ao múltiplo de 25 mais próximo, limitada a 100–200 e ao máximo que o Windows informa para o monitor.
- Estado salvo: `%LOCALAPPDATA%\ScreenShare\display.json` (resoluções já escaladas e última posição).
- O host nunca roda como administrador, exceto nos modos `install-driver`, `uninstall-driver` e `restart-driver`. Códigos de saída: 0 ok, 2 SHA-256 diferente, 3 download, 4 SetupAPI/`pnputil`, 5 usuário recusou (UAC ou confirmação do Windows).
- O certificado do editor do `.cat` que a instalação acrescentar a `TrustedPublisher` é removido logo depois.
- A sessão nunca cai por causa do monitor: falhas viram lease sem monitor.
- O protocolo não muda.
- Mensagens ao usuário e comentários de código em pt-BR, como no resto do repositório.
- `ScreenShare.Display`, `ScreenShare.Tests` e (a partir da Task 3) `ScreenShare.DevHost` usam `net10.0-windows`.
- `dotnet build host/ScreenShare.slnx` com 0 avisos e `dotnet test host/ScreenShare.slnx` verde em cada commit.
- Cada commit termina com `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`, e cada task termina com `git push`.

## Review Focus

1. **O nome da saída muda depois do reinício do driver** (`\\.\DISPLAY5` → `\\.\DISPLAY6`) com o celular conectado: o host precisa achar a saída de novo e aplicar a resolução exata nela. *Teste:* Task 5, `Unknown_resolution_uses_nearest_now_and_exact_after_driver_restart`.
2. **O usuário recusa o UAC do reinício:** a conexão segue, e o host não fica pedindo de novo a cada conexão. *Teste:* Task 5, `Declined_restart_is_not_asked_again`.
3. **Largura ou altura ímpar no `HELLO`** (ex.: 2273×1080, porque o Android desconta a barra de navegação): o modo precisa ser par para o encoder da Parte 3. *Teste:* Task 5, `Request_is_clamped_and_made_even`.
4. **A posição salva deixou de valer** (o usuário trocou de monitor principal ou de arranjo): anexar ali falha, e o monitor precisa aparecer na posição padrão. *Teste:* Task 5, `Attach_falls_back_to_default_position_when_saved_one_is_refused`.
5. **XML do VDD ou `display.json` corrompidos:** não podem impedir a conexão nem ser sobrescritos às cegas. *Teste:* Task 2 (os dois arquivos) e Task 5, `Corrupted_settings_file_uses_nearest_mode_without_restart`.

---

### Task 1: spike do VDD no PC do usuário — **concluída**

Executada pelo controlador com o usuário em 2026-10-04. Os resultados (E1 a E9) e as decisões estão no spec, em "Resultados do spike" e nas seções revisadas. Resumo:
- a instalação via SetupAPI funciona;
- os comandos do pipe derrubam o driver;
- anexar e desanexar pelo Windows funciona sem admin e sobrevive ao reinício do PC;
- resolução nova exige `pnputil /restart-device` (admin);
- resolução e escala funcionam sem admin;
- a desinstalação é limpa.

---

### Task 2: projeto ScreenShare.Display com a lógica pura (XML do VDD, escala, estado salvo)

**Files:**
- Create: `host/ScreenShare.Display/ScreenShare.Display.csproj`
- Create: `host/ScreenShare.Display/VddSettingsFile.cs`
- Create: `host/ScreenShare.Display/ScaleCalculator.cs`
- Create: `host/ScreenShare.Display/DisplayStateStore.cs`
- Create: `host/ScreenShare.Display/AtomicFile.cs`
- Modify: `host/ScreenShare.slnx` (acrescentar o projeto)
- Modify: `host/ScreenShare.Tests/ScreenShare.Tests.csproj` (`net10.0-windows` + referência ao `ScreenShare.Display`)
- Test: `host/ScreenShare.Tests/Display/VddSettingsFileTests.cs`, `ScaleCalculatorTests.cs`, `DisplayStateStoreTests.cs`

**Interfaces:**
- Consumes: nada.
- Produces (as Tasks 3 a 7 usam exatamente estes nomes):
  - `ScreenShare.Display.VddSettingsFile`:
    - `const string DefaultPath = @"C:\VirtualDisplayDriver\vdd_settings.xml"`;
    - `static bool EnsureMode(string path, int width, int height)`: devolve `true` se acrescentou;
    - `static void WriteMinimal(string path, int monitorCount, IEnumerable<(int Width, int Height)> modes)`;
    - `static IReadOnlyList<(int Width, int Height)> ReadModes(string path)`.
  - `ScreenShare.Display.VddSettingsException : Exception`.
  - `ScreenShare.Display.ScaleCalculator`: `const int MinPercent = 100`, `const int MaxPercent = 200`, `static int FromDpi(int dpi)`.
  - `ScreenShare.Display.DisplayPosition`: `readonly record struct(int X, int Y)`.
  - `ScreenShare.Display.DisplayStateStore(string path)`: `bool IsScaled(int width, int height)`, `void MarkScaled(int width, int height)`, `DisplayPosition? LastPosition`, `void SaveLastPosition(DisplayPosition position)`, `static string DefaultPath`.
  - `ScreenShare.Display.AtomicFile` (`internal`): `static void WriteAllText(string path, string contents)`.

- [ ] **Step 1: Criar o projeto e ligar aos testes**

`host/ScreenShare.Display/ScreenShare.Display.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

</Project>
```

`host/ScreenShare.slnx` passa a ser:

```xml
<Solution>
  <Project Path="ScreenShare.Core/ScreenShare.Core.csproj" />
  <Project Path="ScreenShare.DevHost/ScreenShare.DevHost.csproj" />
  <Project Path="ScreenShare.Display/ScreenShare.Display.csproj" />
  <Project Path="ScreenShare.Tests/ScreenShare.Tests.csproj" />
</Solution>
```

Em `host/ScreenShare.Tests/ScreenShare.Tests.csproj`, troque `<TargetFramework>net10.0</TargetFramework>` por `<TargetFramework>net10.0-windows</TargetFramework>` e acrescente ao `ItemGroup` dos `ProjectReference`:

```xml
    <ProjectReference Include="..\ScreenShare.Display\ScreenShare.Display.csproj" />
```

Run: `dotnet test host/ScreenShare.slnx`
Expected: os 99 testes atuais passam, com 0 avisos.

- [ ] **Step 2: Escrever os testes do `ScaleCalculator`**

`host/ScreenShare.Tests/Display/ScaleCalculatorTests.cs`:

```csharp
using ScreenShare.Display;

namespace ScreenShare.Tests.Display;

public sealed class ScaleCalculatorTests
{
    [Theory]
    [InlineData(160, 100)]
    [InlineData(200, 125)]
    [InlineData(240, 150)]
    [InlineData(280, 175)]
    [InlineData(300, 200)] // 187,5 arredonda para 200
    [InlineData(320, 200)]
    [InlineData(420, 200)] // 262,5 limitado a 200
    [InlineData(72, 100)]  // 45 limitado a 100
    [InlineData(1000, 200)]
    [InlineData(0, 100)]
    [InlineData(-5, 100)]
    public void FromDpi_rounds_to_steps_of_25_within_limits(int dpi, int expected)
    {
        Assert.Equal(expected, ScaleCalculator.FromDpi(dpi));
    }
}
```

- [ ] **Step 3: Rodar e ver falhar**

Run: `dotnet test host/ScreenShare.slnx --filter FullyQualifiedName~ScaleCalculatorTests`
Expected: erro de compilação, `ScaleCalculator` não existe.

- [ ] **Step 4: Implementar o `ScaleCalculator`**

`host/ScreenShare.Display/ScaleCalculator.cs`:

```csharp
namespace ScreenShare.Display;

/// <summary>
/// Escala do Windows para o monitor virtual a partir do DPI do celular: DPI ÷ 160 × 100 (160 é a densidade de
/// referência do Android), arredondada ao degrau de 25% mais próximo e limitada a 100–200%.
/// </summary>
public static class ScaleCalculator
{
    public const int MinPercent = 100;
    public const int MaxPercent = 200;

    public static int FromDpi(int dpi)
    {
        var raw = dpi * 100.0 / 160;
        var rounded = (int)(Math.Round(raw / 25, MidpointRounding.AwayFromZero) * 25);
        return Math.Clamp(rounded, MinPercent, MaxPercent);
    }
}
```

Run: `dotnet test host/ScreenShare.slnx --filter FullyQualifiedName~ScaleCalculatorTests`
Expected: PASS (11 casos).

- [ ] **Step 5: Escrever os testes do `VddSettingsFile`**

`host/ScreenShare.Tests/Display/VddSettingsFileTests.cs`:

```csharp
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
}
```

- [ ] **Step 6: Rodar e ver falhar**

Run: `dotnet test host/ScreenShare.slnx --filter FullyQualifiedName~VddSettingsFileTests`
Expected: erro de compilação, `VddSettingsFile` não existe.

- [ ] **Step 7: Implementar `AtomicFile` e `VddSettingsFile`**

`host/ScreenShare.Display/AtomicFile.cs`:

```csharp
using System.Text;

namespace ScreenShare.Display;

/// <summary>
/// Grava um arquivo inteiro de uma vez: escreve num temporário na mesma pasta e troca pelo definitivo. Quem lê
/// (o driver, ou o próprio host depois de uma queda) nunca vê o arquivo pela metade.
/// </summary>
internal static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
```

`host/ScreenShare.Display/VddSettingsFile.cs`:

```csharp
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace ScreenShare.Display;

/// <summary>O vdd_settings.xml não pôde ser lido ou não tem o formato do VDD.</summary>
public sealed class VddSettingsException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Lê e grava o vdd_settings.xml do Virtual Display Driver. Só mexe no que precisa (a lista de resoluções) e
/// preserva o resto do arquivo, inclusive comentários e elementos que não conhece. Toda gravação é atômica.
/// </summary>
public static class VddSettingsFile
{
    public const string DefaultPath = @"C:\VirtualDisplayDriver\vdd_settings.xml";

    /// <summary>Taxa de atualização usada nos modos que o ScreenShare acrescenta.</summary>
    private const int RefreshRate = 60;

    /// <summary>
    /// Garante que a resolução <paramref name="width"/>×<paramref name="height"/> está na lista (qualquer taxa
    /// conta). Devolve true se precisou acrescentar. Sem arquivo, cria o mínimo com um monitor e esse modo.
    /// </summary>
    public static bool EnsureMode(string path, int width, int height)
    {
        if (!File.Exists(path))
        {
            WriteMinimal(path, monitorCount: 1, [(width, height)]);
            return true;
        }

        var doc = Load(path);
        if (ReadModes(doc).Contains((width, height))) return false;

        var root = doc.Root!;
        var resolutions = root.Element("resolutions");
        if (resolutions is null)
        {
            resolutions = new XElement("resolutions");
            root.Add(resolutions);
        }
        resolutions.Add(Resolution(width, height));
        Save(doc, path);
        return true;
    }

    /// <summary>Cria (ou substitui) o arquivo com o mínimo que o driver precisa.</summary>
    public static void WriteMinimal(string path, int monitorCount, IEnumerable<(int Width, int Height)> modes)
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("vdd_settings",
                new XElement("monitors", new XElement("count", monitorCount.ToString(CultureInfo.InvariantCulture))),
                new XElement("gpu", new XElement("friendlyname", "default")),
                new XElement("global", new XElement("g_refresh_rate", RefreshRate.ToString(CultureInfo.InvariantCulture))),
                new XElement("resolutions", modes.Select(m => Resolution(m.Width, m.Height)))));
        Save(doc, path);
    }

    /// <summary>As resoluções da lista, na ordem do arquivo; entradas que não são números são ignoradas.</summary>
    public static IReadOnlyList<(int Width, int Height)> ReadModes(string path) => ReadModes(Load(path));

    private static List<(int Width, int Height)> ReadModes(XDocument doc)
    {
        var modes = new List<(int Width, int Height)>();
        foreach (var resolution in doc.Root!.Element("resolutions")?.Elements("resolution") ?? [])
        {
            if (int.TryParse(resolution.Element("width")?.Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var w) &&
                int.TryParse(resolution.Element("height")?.Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var h))
                modes.Add((w, h));
        }
        return modes;
    }

    private static XElement Resolution(int width, int height) => new("resolution",
        new XElement("width", width.ToString(CultureInfo.InvariantCulture)),
        new XElement("height", height.ToString(CultureInfo.InvariantCulture)),
        new XElement("refresh_rate", RefreshRate.ToString(CultureInfo.InvariantCulture)));

    private static XDocument Load(string path)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException e)
        {
            throw new VddSettingsException($"O arquivo de configuração do driver de monitor virtual está corrompido: {path} ({e.Message})", e);
        }
        if (doc.Root?.Name.LocalName != "vdd_settings")
            throw new VddSettingsException($"O arquivo {path} não é uma configuração do Virtual Display Driver (falta <vdd_settings>).");
        return doc;
    }

    private static void Save(XDocument doc, string path)
    {
        using var buffer = new StringWriter(CultureInfo.InvariantCulture);
        using (var writer = XmlWriter.Create(buffer, new XmlWriterSettings { Indent = true, OmitXmlDeclaration = false }))
            doc.Save(writer);
        // StringWriter declara utf-16; o arquivo é gravado em UTF-8, então a declaração tem de dizer utf-8.
        var text = buffer.ToString().Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"", StringComparison.Ordinal);
        AtomicFile.WriteAllText(path, text);
    }
}
```

Run: `dotnet test host/ScreenShare.slnx --filter FullyQualifiedName~VddSettingsFileTests`
Expected: PASS (10 testes). Se `EnsureMode_adds_missing_mode_and_keeps_everything_else` falhar porque `Indent = true` reformata o trecho preservado, troque para `Indent = false`, já que o `PreserveWhitespace` mantém a formatação original. A asserção é sobre o conteúdo, não sobre o recuo.

- [ ] **Step 8: Escrever os testes do `DisplayStateStore`**

`host/ScreenShare.Tests/Display/DisplayStateStoreTests.cs`:

```csharp
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
```

- [ ] **Step 9: Rodar e ver falhar**

Run: `dotnet test host/ScreenShare.slnx --filter FullyQualifiedName~DisplayStateStoreTests`
Expected: erro de compilação, `DisplayStateStore` não existe.

- [ ] **Step 10: Implementar o `DisplayStateStore`**

`host/ScreenShare.Display/DisplayStateStore.cs`:

```csharp
using System.Text.Json;

namespace ScreenShare.Display;

/// <summary>Posição do monitor virtual na área de trabalho, em pixels (a tela principal começa em 0,0).</summary>
public readonly record struct DisplayPosition(int X, int Y);

/// <summary>
/// O que o host lembra do monitor virtual entre execuções (display.json): as resoluções que já receberam a escala
/// automática, para não sobrescrever a escala que o usuário escolher depois, e a última posição, para respeitar o
/// arranjo feito em Configurações › Tela. Arquivo ilegível conta como vazio: no pior caso a escala automática é
/// aplicada de novo uma vez e o monitor volta à posição padrão, o que nunca impede a conexão.
/// </summary>
public sealed class DisplayStateStore(string path)
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenShare", "display.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private sealed class Contents
    {
        public List<string>? Scaled { get; set; }
        public DisplayPosition? LastPosition { get; set; }
    }

    public bool IsScaled(int width, int height) => Load().Scaled!.Contains(Key(width, height));

    public void MarkScaled(int width, int height)
    {
        var contents = Load();
        var key = Key(width, height);
        if (contents.Scaled!.Contains(key)) return;
        contents.Scaled.Add(key);
        contents.Scaled.Sort(StringComparer.Ordinal);
        Save(contents);
    }

    public DisplayPosition? LastPosition => Load().LastPosition;

    public void SaveLastPosition(DisplayPosition position)
    {
        var contents = Load();
        if (contents.LastPosition == position) return;
        contents.LastPosition = position;
        Save(contents);
    }

    private static string Key(int width, int height) => $"{width}x{height}";

    private Contents Load()
    {
        Contents? contents = null;
        try
        {
            contents = JsonSerializer.Deserialize<Contents>(File.ReadAllText(path), Options);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // FileNotFound e DirectoryNotFound são IOException: sem arquivo, nada foi guardado ainda.
        }
        contents ??= new Contents();
        contents.Scaled ??= [];
        return contents;
    }

    private void Save(Contents contents) => AtomicFile.WriteAllText(path, JsonSerializer.Serialize(contents, Options));
}
```

Run: `dotnet test host/ScreenShare.slnx --filter FullyQualifiedName~DisplayStateStoreTests`
Expected: PASS (10 casos).

- [ ] **Step 11: Rodar tudo**

Run: `dotnet build host/ScreenShare.slnx` e depois `dotnet test host/ScreenShare.slnx`
Expected: 0 avisos; 99 + 11 + 10 + 10 = 130 testes passando.

- [ ] **Step 12: Commit e push**

```bash
git add host/ScreenShare.Display host/ScreenShare.slnx host/ScreenShare.Tests/ScreenShare.Tests.csproj host/ScreenShare.Tests/Display
git commit -m "feat(display): projeto ScreenShare.Display com XML do VDD, escala e estado salvo do monitor

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 3: instalação do driver (`install-driver`, `uninstall-driver`, `restart-driver`)

**Files:**
- Modify: `host/ScreenShare.Display/ScreenShare.Display.csproj` (pacote `System.Security.Cryptography.Pkcs`)
- Create: `host/ScreenShare.Display/Driver/VddPackage.cs`
- Create: `host/ScreenShare.Display/Driver/IDriverSystem.cs`
- Create: `host/ScreenShare.Display/Driver/DriverInstaller.cs`
- Create: `host/ScreenShare.Display/Driver/WindowsDriverSystem.cs`
- Create: `host/ScreenShare.Display/Driver/ElevatedCommand.cs`
- Create: `host/ScreenShare.Display/Native/SetupApi.cs`
- Modify: `host/ScreenShare.DevHost/ScreenShare.DevHost.csproj` (`net10.0-windows` + referência ao `ScreenShare.Display`)
- Create: `host/ScreenShare.DevHost/DriverCommands.cs`
- Modify: `host/ScreenShare.DevHost/Program.cs` (despachar os três modos no começo)
- Test: `host/ScreenShare.Tests/Display/DriverInstallerTests.cs`, `host/ScreenShare.Tests/DevHost/DriverCommandsTests.cs`

**Interfaces:**
- Consumes (Task 2): `VddSettingsFile.DefaultPath`, `VddSettingsFile.WriteMinimal(path, monitorCount, modes)`.
- Produces:
  - `ScreenShare.Display.Driver.VddPackage`: `Url` (`Uri`), `ReleasePage`, `Sha256`, `HardwareId`, `InfRelativePath`, `CatalogRelativePath` (`const string`), `SettingsDirectory` (`string`), `static bool HasHash(byte[] data, string expectedSha256)`.
  - `ScreenShare.Display.Driver.DriverExitCode` (`Ok = 0, HashMismatch = 2, DownloadFailed = 3, SetupFailed = 4, UserDeclined = 5`).
  - `ScreenShare.Display.Driver.IDriverSystem` e `WindowsDriverSystem` (`bool IsInstalled()`, além do resto da interface abaixo).
  - `ScreenShare.Display.Driver.DriverInstaller(IDriverSystem, Action<string> log, string expectedSha256 = VddPackage.Sha256)`: `Task<DriverExitCode> InstallAsync(string userSid, CancellationToken = default)`, `DriverExitCode Uninstall()`, `DriverExitCode Restart()`.
  - `ScreenShare.Display.Driver.ElevatedCommand`: `bool IsElevated`, `string CurrentUserSid`, `int Run(params string[] arguments)`.
  - `ScreenShare.DevHost.DriverCommands`: `bool IsDriverCommand(string[] args)`, `Task<int> RunAsync(string[] args)`, `string Describe(string command, int code)`.

- [ ] **Step 1: Pacote e projeto do DevHost**

Em `host/ScreenShare.Display/ScreenShare.Display.csproj`, acrescente:

```xml
  <ItemGroup>
    <PackageReference Include="System.Security.Cryptography.Pkcs" Version="10.0.0" />
  </ItemGroup>
```

Em `host/ScreenShare.DevHost/ScreenShare.DevHost.csproj`, troque `<TargetFramework>net10.0</TargetFramework>` por `<TargetFramework>net10.0-windows</TargetFramework>` e acrescente ao `ItemGroup` do `ProjectReference`:

```xml
    <ProjectReference Include="..\ScreenShare.Display\ScreenShare.Display.csproj" />
```

Run: `dotnet build host/ScreenShare.slnx`
Expected: 0 avisos, 0 erros.

- [ ] **Step 2: Escrever os testes do `DriverInstaller`**

`host/ScreenShare.Tests/Display/DriverInstallerTests.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using ScreenShare.Display;
using ScreenShare.Display.Driver;

namespace ScreenShare.Tests.Display;

public sealed class DriverInstallerTests
{
    private readonly FakeDriverSystem _system = new();
    private readonly List<string> _log = [];

    private DriverInstaller Installer() =>
        new(_system, _log.Add, Convert.ToHexString(SHA256.HashData(_system.DownloadBytes)));

    [Fact]
    public async Task Already_installed_only_prepares_settings()
    {
        _system.Installed = true;

        Assert.Equal(DriverExitCode.Ok, await Installer().InstallAsync("S-1-5-21-1"));

        Assert.False(_system.Downloaded);
        Assert.Null(_system.InstalledInf);
        Assert.Equal("S-1-5-21-1", _system.PreparedSid);
        Assert.Equal(VddSettingsFile.DefaultPath, _system.PreparedPath);
    }

    [Fact]
    public async Task Download_failure_points_to_manual_install()
    {
        _system.DownloadError = new HttpRequestException("sem rede");

        Assert.Equal(DriverExitCode.DownloadFailed, await Installer().InstallAsync("S-1-5-21-1"));

        Assert.Null(_system.InstalledInf);
        Assert.Contains(_log, line => line.Contains(VddPackage.ReleasePage));
    }

    [Fact]
    public async Task Wrong_hash_installs_nothing()
    {
        var installer = new DriverInstaller(_system, _log.Add, VddPackage.Sha256); // os bytes falsos não batem com o hash real

        Assert.Equal(DriverExitCode.HashMismatch, await installer.InstallAsync("S-1-5-21-1"));

        Assert.Null(_system.ExtractedTo);
        Assert.Null(_system.InstalledInf);
        Assert.Null(_system.PreparedSid);
    }

    [Fact]
    public async Task Successful_install_prepares_settings_and_cleans_up()
    {
        Assert.Equal(DriverExitCode.Ok, await Installer().InstallAsync("S-1-5-21-1"));

        Assert.Equal(Path.Combine(FakeDriverSystem.TempDirectory, @"VirtualDisplayDriver\MttVDD.inf"), _system.InstalledInf);
        Assert.Equal("S-1-5-21-1", _system.PreparedSid);
        Assert.True(_system.TempDeleted);
    }

    [Fact]
    public async Task Publisher_trusted_by_the_install_is_removed_and_others_stay()
    {
        _system.Trusted.Add("ANTIGO");
        _system.Catalog.UnionWith(["SIGNPATH", "ANTIGO"]);
        _system.TrustedByInstall.UnionWith(["SIGNPATH", "OUTRO"]);

        await Installer().InstallAsync("S-1-5-21-1");

        Assert.Equal(new[] { "SIGNPATH" }, _system.Removed);
        Assert.Contains("ANTIGO", _system.Trusted);
        Assert.Contains("OUTRO", _system.Trusted);
    }

    [Theory]
    [InlineData(1223)]                           // ERROR_CANCELLED
    [InlineData(unchecked((int)0xE0000243))]     // ERROR_AUTHENTICODE_PUBLISHER_NOT_TRUSTED
    [InlineData(unchecked((int)0xE0000244))]     // ERROR_AUTHENTICODE_TRUST_NOT_ESTABLISHED
    public async Task Declined_confirmation_is_reported_as_user_declined(int error)
    {
        _system.InstallError = error;
        _system.Catalog.Add("SIGNPATH");
        _system.TrustedByInstall.Add("SIGNPATH");

        Assert.Equal(DriverExitCode.UserDeclined, await Installer().InstallAsync("S-1-5-21-1"));

        Assert.Null(_system.PreparedSid);
        Assert.True(_system.TempDeleted);
        Assert.Equal(new[] { "SIGNPATH" }, _system.Removed);
    }

    [Fact]
    public async Task Other_setup_errors_are_reported_with_the_windows_code()
    {
        _system.InstallError = 5;

        Assert.Equal(DriverExitCode.SetupFailed, await Installer().InstallAsync("S-1-5-21-1"));

        Assert.Contains(_log, line => line.Contains("0x00000005"));
        Assert.True(_system.TempDeleted);
    }

    [Fact]
    public void Uninstall_removes_devices_and_settings_folder()
    {
        Assert.Equal(DriverExitCode.Ok, Installer().Uninstall());

        Assert.True(_system.Uninstalled);
        Assert.Equal(VddPackage.SettingsDirectory, _system.DeletedSettings);
    }

    [Fact]
    public void Failed_uninstall_keeps_settings_folder()
    {
        _system.UninstallError = 5;

        Assert.Equal(DriverExitCode.SetupFailed, Installer().Uninstall());

        Assert.Null(_system.DeletedSettings);
    }

    [Theory]
    [InlineData(0, DriverExitCode.Ok)]
    [InlineData(3010, DriverExitCode.SetupFailed)]
    public void Restart_maps_pnputil_result(int code, DriverExitCode expected)
    {
        _system.RestartCode = code;

        Assert.Equal(expected, Installer().Restart());
    }

    [Fact]
    public void HasHash_compares_sha256_ignoring_case()
    {
        var abc = Encoding.ASCII.GetBytes("abc");

        Assert.True(VddPackage.HasHash(abc, "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD"));
        Assert.True(VddPackage.HasHash(abc, "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"));
        Assert.False(VddPackage.HasHash(abc, VddPackage.Sha256));
    }

    private sealed class FakeDriverSystem : IDriverSystem
    {
        public const string TempDirectory = @"C:\temp\screenshare-vdd-teste";

        public bool Installed { get; set; }
        public byte[] DownloadBytes { get; } = [1, 2, 3];
        public Exception? DownloadError { get; set; }
        public int InstallError { get; set; }
        public int UninstallError { get; set; }
        public int RestartCode { get; set; }
        public HashSet<string> Trusted { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Catalog { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> TrustedByInstall { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Removed { get; } = [];
        public bool Downloaded { get; private set; }
        public string? ExtractedTo { get; private set; }
        public bool TempDeleted { get; private set; }
        public string? InstalledInf { get; private set; }
        public string? PreparedSid { get; private set; }
        public string? PreparedPath { get; private set; }
        public bool Uninstalled { get; private set; }
        public string? DeletedSettings { get; private set; }

        public bool IsInstalled() => Installed;

        public Task<byte[]> DownloadAsync(Uri url, CancellationToken cancellationToken)
        {
            Downloaded = true;
            Assert.Equal(VddPackage.Url, url);
            return DownloadError is { } error ? Task.FromException<byte[]>(error) : Task.FromResult(DownloadBytes);
        }

        public string Extract(byte[] zip) => ExtractedTo = TempDirectory;

        public void DeleteDirectory(string path) => TempDeleted = path == TempDirectory;

        public IReadOnlySet<string> TrustedPublisherThumbprints() => new HashSet<string>(Trusted, StringComparer.OrdinalIgnoreCase);

        public IReadOnlySet<string> CatalogThumbprints(string catalogPath)
        {
            Assert.Equal(Path.Combine(TempDirectory, @"VirtualDisplayDriver\mttvdd.cat"), catalogPath);
            return Catalog;
        }

        public void RemoveTrustedPublishers(IEnumerable<string> thumbprints)
        {
            foreach (var thumbprint in thumbprints)
            {
                Removed.Add(thumbprint);
                Trusted.Remove(thumbprint);
            }
        }

        public int InstallDevice(string infPath)
        {
            InstalledInf = infPath;
            Trusted.UnionWith(TrustedByInstall); // a confirmação do Windows vem com "Sempre confiar" marcado
            return InstallError;
        }

        public void PrepareSettings(string settingsPath, string userSid)
        {
            PreparedPath = settingsPath;
            PreparedSid = userSid;
        }

        public int UninstallDevices()
        {
            Uninstalled = true;
            return UninstallError;
        }

        public void DeleteSettings(string settingsDirectory) => DeletedSettings = settingsDirectory;

        public int RestartDevices() => RestartCode;
    }
}
```

- [ ] **Step 3: Rodar e ver falhar**

Run: `dotnet test host/ScreenShare.slnx --filter FullyQualifiedName~DriverInstallerTests`
Expected: erro de compilação, `ScreenShare.Display.Driver` não existe.

- [ ] **Step 4: Implementar `VddPackage`, `IDriverSystem` e `DriverInstaller`**

`host/ScreenShare.Display/Driver/VddPackage.cs`:

```csharp
using System.Security.Cryptography;

namespace ScreenShare.Display.Driver;

/// <summary>O pacote do Virtual Display Driver que o ScreenShare instala (versão fixada; veja o spec da Parte 2).</summary>
public static class VddPackage
{
    /// <summary>Release 25.7.23, asset "Driver Only": apesar do nome x86, traz o driver 24.12.24 para x64.</summary>
    public static readonly Uri Url = new("https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/25.7.23/VirtualDisplayDriver-x86.Driver.Only.zip");

    public const string ReleasePage = "https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/tag/25.7.23";
    public const string Sha256 = "e24210692b442b39af763536330ce78b423f19342b7a7792c26de3944e418b3a";
    public const string HardwareId = @"Root\MttVDD";
    public const string InfRelativePath = @"VirtualDisplayDriver\MttVDD.inf";
    public const string CatalogRelativePath = @"VirtualDisplayDriver\mttvdd.cat";

    /// <summary>A pasta da configuração do driver (C:\VirtualDisplayDriver).</summary>
    public static string SettingsDirectory { get; } = Path.GetDirectoryName(VddSettingsFile.DefaultPath)!;

    public static bool HasHash(byte[] data, string expectedSha256) =>
        Convert.ToHexString(SHA256.HashData(data)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
}
```

`host/ScreenShare.Display/Driver/IDriverSystem.cs`:

```csharp
namespace ScreenShare.Display.Driver;

/// <summary>
/// As operações do Windows que a instalação do driver usa. Ficam atrás desta interface para a sequência
/// (<see cref="DriverInstaller"/>) ser testada sem instalar nada de verdade.
/// </summary>
public interface IDriverSystem
{
    /// <summary>Existe um dispositivo Root\MttVDD presente? Não exige administrador.</summary>
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
```

`host/ScreenShare.Display/Driver/DriverInstaller.cs`:

```csharp
namespace ScreenShare.Display.Driver;

/// <summary>Códigos de saída dos modos install-driver, uninstall-driver e restart-driver.</summary>
public enum DriverExitCode
{
    Ok = 0,
    HashMismatch = 2,
    DownloadFailed = 3,
    SetupFailed = 4,
    UserDeclined = 5,
}

/// <summary>
/// Instala, remove e reinicia o Virtual Display Driver. Roda como administrador (o DevHost se reabre com UAC para isso).
/// </summary>
public sealed class DriverInstaller(IDriverSystem system, Action<string> log, string expectedSha256 = VddPackage.Sha256)
{
    // Erros do Windows que significam "o usuário disse não" na confirmação de instalação do driver.
    private const int ErrorCancelled = 1223;
    private const int ErrorAuthenticodePublisherNotTrusted = unchecked((int)0xE0000243);
    private const int ErrorAuthenticodeTrustNotEstablished = unchecked((int)0xE0000244);

    public async Task<DriverExitCode> InstallAsync(string userSid, CancellationToken cancellationToken = default)
    {
        if (system.IsInstalled())
        {
            system.PrepareSettings(VddSettingsFile.DefaultPath, userSid);
            log("O driver de monitor virtual já está instalado.");
            return DriverExitCode.Ok;
        }

        byte[] zip;
        try
        {
            log($"Baixando o Virtual Display Driver de {VddPackage.Url} ...");
            zip = await system.DownloadAsync(VddPackage.Url, cancellationToken);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException)
        {
            log($"Não foi possível baixar o driver: {e.Message}");
            log($"Para instalar à mão: {VddPackage.ReleasePage}");
            return DriverExitCode.DownloadFailed;
        }
        if (!VddPackage.HasHash(zip, expectedSha256))
        {
            log("O arquivo baixado não é o esperado (SHA-256 diferente). Nada foi instalado.");
            return DriverExitCode.HashMismatch;
        }

        var directory = system.Extract(zip);
        try
        {
            var trustedBefore = system.TrustedPublisherThumbprints();
            log("Instalando o driver (o Windows vai pedir confirmação) ...");
            var error = system.InstallDevice(Path.Combine(directory, VddPackage.InfRelativePath));
            ForgetPublisherTrustedByTheInstall(trustedBefore, Path.Combine(directory, VddPackage.CatalogRelativePath));
            if (error != 0) return Failed(error);

            system.PrepareSettings(VddSettingsFile.DefaultPath, userSid);
            log("Driver de monitor virtual instalado.");
            return DriverExitCode.Ok;
        }
        finally
        {
            system.DeleteDirectory(directory);
        }
    }

    public DriverExitCode Uninstall()
    {
        var error = system.UninstallDevices();
        if (error != 0)
        {
            log($"O Windows não removeu o driver (erro 0x{error:X8}).");
            return DriverExitCode.SetupFailed;
        }
        system.DeleteSettings(VddPackage.SettingsDirectory);
        log("Driver de monitor virtual removido.");
        return DriverExitCode.Ok;
    }

    public DriverExitCode Restart()
    {
        var code = system.RestartDevices();
        if (code != 0)
        {
            log($"Não foi possível reiniciar o driver de monitor virtual (código {code}).");
            return DriverExitCode.SetupFailed;
        }
        log("Driver de monitor virtual reiniciado.");
        return DriverExitCode.Ok;
    }

    /// <summary>
    /// A confirmação do Windows vem com "Sempre confiar em software de ..." marcado, o que grava o certificado do
    /// editor em TrustedPublisher. O driver instalado não precisa disso: tira o que entrou agora e veio do catálogo.
    /// </summary>
    private void ForgetPublisherTrustedByTheInstall(IReadOnlySet<string> trustedBefore, string catalogPath)
    {
        var fromCatalog = system.CatalogThumbprints(catalogPath);
        var added = system.TrustedPublisherThumbprints()
            .Where(thumbprint => !trustedBefore.Contains(thumbprint) && fromCatalog.Contains(thumbprint))
            .ToList();
        if (added.Count == 0) return;
        system.RemoveTrustedPublishers(added);
        log("Confiança permanente no editor do driver removida (o driver instalado não precisa dela).");
    }

    private DriverExitCode Failed(int error)
    {
        if (error is ErrorCancelled or ErrorAuthenticodePublisherNotTrusted or ErrorAuthenticodeTrustNotEstablished)
        {
            log("A instalação do driver foi recusada.");
            return DriverExitCode.UserDeclined;
        }
        log($"O Windows não instalou o driver (erro 0x{error:X8}).");
        return DriverExitCode.SetupFailed;
    }
}
```

Run: `dotnet test host/ScreenShare.slnx --filter FullyQualifiedName~DriverInstallerTests`
Expected: PASS (14 casos).

- [ ] **Step 5: Implementar as partes que falam com o Windows**

São validadas à mão (Step 9), porque instalam um driver de verdade. O P/Invoke é o mesmo que funcionou no spike.

`host/ScreenShare.Display/Native/SetupApi.cs`:

```csharp
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ScreenShare.Display.Native;

/// <summary>SetupAPI (setupapi.dll/newdev.dll): criar, achar e remover dispositivos de exibição criados pela raiz.</summary>
internal static class SetupApi
{
    private static readonly Guid DisplayClass = new("4d36e968-e325-11ce-bfc1-08002be10318");
    private static readonly IntPtr InvalidHandle = new(-1);
    private static readonly DEVPROPKEY DriverInfPath = new() { fmtid = new Guid("a8b865dd-2e3d-4094-ad97-e593a70c75d6"), pid = 5 };

    private const int DigcfPresent = 0x2;
    private const int DicdGenerateId = 0x1;
    private const int SpdrpHardwareId = 0x1;
    private const int DifRegisterDevice = 0x19;
    private const int DifRemove = 0x05;
    private const int SuoiForceDelete = 0x1;

    public sealed record Device(string InstanceId, string? InfName);

    public static List<Device> FindDevices(string hardwareId, bool presentOnly)
    {
        var devices = new List<Device>();
        ForEachDevice(hardwareId, presentOnly, (set, data) => devices.Add(new Device(InstanceId(set, data), InfName(set, data))));
        return devices;
    }

    /// <summary>Cria o dispositivo pela raiz e instala o driver do .inf. Devolve 0 ou o erro do Windows.</summary>
    public static int InstallRootDevice(string hardwareId, string infPath)
    {
        var classGuid = DisplayClass;
        var set = SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
        if (set == InvalidHandle) return Marshal.GetLastWin32Error();
        try
        {
            var data = NewData();
            if (!SetupDiCreateDeviceInfoW(set, "Display", ref classGuid, null, IntPtr.Zero, DicdGenerateId, ref data))
                return Marshal.GetLastWin32Error();
            var ids = Encoding.Unicode.GetBytes(hardwareId + "\0\0"); // multi-sz
            if (!SetupDiSetDeviceRegistryPropertyW(set, ref data, SpdrpHardwareId, ids, ids.Length))
                return Marshal.GetLastWin32Error();
            if (!SetupDiCallClassInstaller(DifRegisterDevice, set, ref data))
                return Marshal.GetLastWin32Error();
            if (UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, hardwareId, infPath, 0, out _))
                return 0;
            var error = Marshal.GetLastWin32Error();
            SetupDiCallClassInstaller(DifRemove, set, ref data); // não deixa um dispositivo sem driver para trás
            return error;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    /// <summary>Remove os dispositivos (presentes ou não) e o pacote oemNN.inf. Devolve 0 ou o primeiro erro.</summary>
    public static int RemoveDevicesAndPackages(string hardwareId)
    {
        var infs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var error = 0;
        ForEachDevice(hardwareId, presentOnly: false, (set, data) =>
        {
            if (InfName(set, data) is { } inf) infs.Add(inf);
            if (!SetupDiCallClassInstaller(DifRemove, set, ref data) && error == 0) error = Marshal.GetLastWin32Error();
        });
        foreach (var inf in infs.Where(name => name.StartsWith("oem", StringComparison.OrdinalIgnoreCase)))
        {
            if (!SetupUninstallOEMInfW(inf, SuoiForceDelete, IntPtr.Zero) && error == 0) error = Marshal.GetLastWin32Error();
        }
        return error;
    }

    private delegate void DeviceAction(IntPtr set, SP_DEVINFO_DATA data);

    private static void ForEachDevice(string hardwareId, bool presentOnly, DeviceAction action)
    {
        var classGuid = DisplayClass;
        var set = SetupDiGetClassDevsW(ref classGuid, null, IntPtr.Zero, presentOnly ? DigcfPresent : 0);
        if (set == InvalidHandle) throw new Win32Exception();
        try
        {
            // Junta antes de agir: remover durante a enumeração por índice pularia dispositivos.
            var matches = new List<SP_DEVINFO_DATA>();
            for (var index = 0; ; index++)
            {
                var data = NewData();
                if (!SetupDiEnumDeviceInfo(set, index, ref data)) break;
                if (HardwareIds(set, data).Contains(hardwareId, StringComparer.OrdinalIgnoreCase)) matches.Add(data);
            }
            foreach (var data in matches) action(set, data);
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static string[] HardwareIds(IntPtr set, SP_DEVINFO_DATA data)
    {
        var buffer = new byte[2048];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, SpdrpHardwareId, out _, buffer, buffer.Length, out var required))
            return [];
        return Encoding.Unicode.GetString(buffer, 0, Math.Min(required, buffer.Length)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string? InfName(IntPtr set, SP_DEVINFO_DATA data)
    {
        var key = DriverInfPath;
        var buffer = new byte[1024];
        if (!SetupDiGetDevicePropertyW(set, ref data, ref key, out _, buffer, buffer.Length, out var required, 0)) return null;
        return Encoding.Unicode.GetString(buffer, 0, Math.Min(required, buffer.Length)).TrimEnd('\0');
    }

    private static string InstanceId(IntPtr set, SP_DEVINFO_DATA data)
    {
        var id = new StringBuilder(512);
        return SetupDiGetDeviceInstanceIdW(set, ref data, id, id.Capacity, out _) ? id.ToString() : "";
    }

    private static SP_DEVINFO_DATA NewData() => new() { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid classGuid, IntPtr hwndParent);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiCreateDeviceInfoW(IntPtr set, string deviceName, ref Guid classGuid, string? description,
        IntPtr hwndParent, int creationFlags, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA data, int property, byte[] buffer, int size);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA data, int property,
        out int registryType, byte[] buffer, int size, out int required);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(int function, IntPtr set, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key,
        out uint propertyType, byte[] buffer, int size, out int required, int flags);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SP_DEVINFO_DATA data, StringBuilder id, int size, out int required);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupUninstallOEMInfW(string infFileName, int flags, IntPtr reserved);

    [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr hwndParent, string hardwareId, string fullInfPath,
        int installFlags, out bool rebootRequired);
}
```

`host/ScreenShare.Display/Driver/WindowsDriverSystem.cs`:

```csharp
using System.Diagnostics;
using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using ScreenShare.Display.Native;

namespace ScreenShare.Display.Driver;

/// <summary>As operações reais do Windows para instalar o Virtual Display Driver (veja <see cref="IDriverSystem"/>).</summary>
public sealed class WindowsDriverSystem : IDriverSystem
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    public bool IsInstalled() => SetupApi.FindDevices(VddPackage.HardwareId, presentOnly: true).Count > 0;

    public Task<byte[]> DownloadAsync(Uri url, CancellationToken cancellationToken) => Http.GetByteArrayAsync(url, cancellationToken);

    public string Extract(byte[] zip)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ScreenShare-VDD-{Guid.NewGuid():N}");
        using var stream = new MemoryStream(zip);
        ZipFile.ExtractToDirectory(stream, directory);
        return directory;
    }

    public void DeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // pasta temporária: se não sair agora, o Windows limpa depois
        }
    }

    public IReadOnlySet<string> TrustedPublisherThumbprints()
    {
        using var store = new X509Store(StoreName.TrustedPublisher, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates.Select(certificate => certificate.Thumbprint).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlySet<string> CatalogThumbprints(string catalogPath)
    {
        // O .cat é um PKCS#7 assinado: os certificados (editor, cadeia, carimbo de tempo) vêm junto.
        var catalog = new SignedCms();
        catalog.Decode(File.ReadAllBytes(catalogPath));
        return catalog.Certificates.Select(certificate => certificate.Thumbprint).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public void RemoveTrustedPublishers(IEnumerable<string> thumbprints)
    {
        using var store = new X509Store(StoreName.TrustedPublisher, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        foreach (var thumbprint in thumbprints)
            store.RemoveRange(store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false));
    }

    public int InstallDevice(string infPath) => SetupApi.InstallRootDevice(VddPackage.HardwareId, Path.GetFullPath(infPath));

    public void PrepareSettings(string settingsPath, string userSid)
    {
        if (!File.Exists(settingsPath))
            VddSettingsFile.WriteMinimal(settingsPath, monitorCount: 1, [(1920, 1080)]);

        // O host (sem admin) acrescenta resoluções; o processo do driver (LOCAL SERVICE) também grava no XML.
        var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(settingsPath))!);
        var security = directory.GetAccessControl();
        foreach (var sid in new[] { new SecurityIdentifier(userSid), new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null) })
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }
        directory.SetAccessControl(security);
    }

    public int UninstallDevices() => SetupApi.RemoveDevicesAndPackages(VddPackage.HardwareId);

    public void DeleteSettings(string settingsDirectory)
    {
        if (Directory.Exists(settingsDirectory)) Directory.Delete(settingsDirectory, recursive: true);
    }

    public int RestartDevices()
    {
        foreach (var device in SetupApi.FindDevices(VddPackage.HardwareId, presentOnly: true))
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "pnputil.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            start.ArgumentList.Add("/restart-device");
            start.ArgumentList.Add(device.InstanceId);
            using var process = Process.Start(start)!;
            process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) return process.ExitCode;
        }
        return 0;
    }
}
```

Se o compilador não achar `GetAccessControl`/`SetAccessControl` (extensões de `System.IO.FileSystemAclExtensions`), acrescente ao `ScreenShare.Display.csproj` o pacote `System.IO.FileSystem.AccessControl` versão `5.0.0` e registre isso no relatório.

`host/ScreenShare.Display/Driver/ElevatedCommand.cs`:

```csharp
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
    /// Roda este executável como administrador com os argumentos dados (sem espaços: comandos e SIDs) e devolve o
    /// código de saída. UAC recusado = <see cref="DriverExitCode.UserDeclined"/>.
    /// </summary>
    public static int Run(params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("Caminho do executável desconhecido."))
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
    }
}
```

Run: `dotnet build host/ScreenShare.slnx`
Expected: 0 avisos, 0 erros.

- [ ] **Step 6: Escrever os testes do `DriverCommands`**

`host/ScreenShare.Tests/DevHost/DriverCommandsTests.cs`:

```csharp
using ScreenShare.DevHost;
using ScreenShare.Display.Driver;

namespace ScreenShare.Tests.DevHost;

public sealed class DriverCommandsTests
{
    [Theory]
    [InlineData(new[] { "install-driver" }, true)]
    [InlineData(new[] { "install-driver", "S-1-5-21-1" }, true)]
    [InlineData(new[] { "uninstall-driver" }, true)]
    [InlineData(new[] { "restart-driver" }, true)]
    [InlineData(new[] { "--sem-monitor" }, false)]
    [InlineData(new string[0], false)]
    public void Recognizes_only_the_driver_commands(string[] args, bool expected)
    {
        Assert.Equal(expected, DriverCommands.IsDriverCommand(args));
    }

    [Theory]
    [InlineData("install-driver", 0, "instalado")]
    [InlineData("uninstall-driver", 0, "removido")]
    [InlineData("restart-driver", 0, "reiniciado")]
    [InlineData("install-driver", 2, "SHA-256")]
    [InlineData("install-driver", 5, "recusada")]
    [InlineData("install-driver", 4, "código 4")]
    [InlineData("install-driver", 1, "código 1")]
    public void Describes_each_exit_code(string command, int code, string expected)
    {
        Assert.Contains(expected, DriverCommands.Describe(command, code));
    }

    [Fact]
    public void Download_failure_mentions_the_release_page()
    {
        Assert.Contains(VddPackage.ReleasePage, DriverCommands.Describe("install-driver", 3));
    }
}
```

Run: `dotnet test host/ScreenShare.slnx --filter FullyQualifiedName~DriverCommandsTests`
Expected: erro de compilação, `DriverCommands` não existe.

- [ ] **Step 7: Implementar `DriverCommands` e despachar no `Program.cs`**

`host/ScreenShare.DevHost/DriverCommands.cs`:

```csharp
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
```

Em `host/ScreenShare.DevHost/Program.cs`, troque as duas primeiras linhas de comentário e acrescente o despacho logo depois delas, antes de `const int WifiPort = 38700;`:

```csharp
// Host de desenvolvimento: Wi-Fi com TLS + pareamento por QR (porta 38700) e USB com TLS + pareamento, só em loopback (porta 38701).
// Comandos no console: p = parear celular (mostra o QR), l = listar pareados, r <id> = remover, Ctrl+C = sair.
// Modos de linha de comando (pedem administrador): install-driver, uninstall-driver, restart-driver.
if (DriverCommands.IsDriverCommand(args)) return await DriverCommands.RunAsync(args);

```

Run: `dotnet test host/ScreenShare.slnx`
Expected: 0 avisos; todos os testes passando (130 da Task 2 + 14 do `DriverInstallerTests` + 14 do `DriverCommandsTests` = 158).

- [ ] **Step 8: Commit e push**

```bash
git add host/ScreenShare.Display host/ScreenShare.DevHost host/ScreenShare.Tests
git commit -m "feat(display): instalação, remoção e reinício do driver de monitor virtual

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

- [ ] **Step 9: Verificação manual com o usuário (controlador)**

O controlador pede ao usuário, que aceita os UACs e a confirmação do Windows:
1. `dotnet run --project host/ScreenShare.DevHost -- install-driver`. Esperado:
   - "Driver de monitor virtual instalado.";
   - `Get-PnpDevice -Class Display` mostra "Virtual Display Driver" OK;
   - `C:\VirtualDisplayDriver\vdd_settings.xml` existe com count 1;
   - `Test-Path Cert:\LocalMachine\TrustedPublisher\3CF8CF26D8BA266C3A483AB7D26D4A818E317D76` é `False`.
2. `dotnet run --project host/ScreenShare.DevHost -- restart-driver`. Esperado: "Driver de monitor virtual reiniciado." e o dispositivo OK.
3. **O driver fica instalado** para as Tasks 4 e 7. A desinstalação é verificada na Task 7.

Registre o resultado no relatório da task.

---

### Task 4: topologia de tela (achar, anexar, desanexar, resolução e escala)

**Files:**
- Create: `host/ScreenShare.Display/IDisplayTopology.cs`
- Create: `host/ScreenShare.Display/DisplayTopology.cs`
- Create: `host/ScreenShare.Display/Native/DisplayApi.cs`
- Test: `host/ScreenShare.Tests/Display/DisplayTopologyTests.cs` (cálculo de escala), `host/ScreenShare.Tests/Display/VddFactAttribute.cs`, `host/ScreenShare.Tests/Display/DisplayTopologyIntegrationTests.cs` (opcional, com o driver)

**Interfaces:**
- Consumes: `DisplayPosition` (Task 2), `VddPackage.HardwareId` (Task 3).
- Produces:
  - `ScreenShare.Display.VddOutput`: `record(string DeviceName, bool Attached, int X, int Y, int Width, int Height, IReadOnlyList<(int Width, int Height)> Modes)`.
  - `ScreenShare.Display.IDisplayTopology`:
    - `VddOutput? FindVddOutput()`;
    - `bool Attach(string deviceName, DisplayPosition position, int width, int height)`;
    - `bool Detach(string deviceName)`;
    - `bool SetMode(string deviceName, int width, int height)`;
    - `int? GetScale(string deviceName)`;
    - `bool SetScale(string deviceName, int percent)`;
    - `DisplayPosition DefaultPosition()`.
  - `ScreenShare.Display.DisplayTopology : IDisplayTopology`, com `static int ToRelativeStep(int minRelative, int maxRelative, int percent)` e `static int ToPercent(int minRelative, int currentRelative)`.

- [ ] **Step 1: Escrever os testes do cálculo de escala**

`host/ScreenShare.Tests/Display/DisplayTopologyTests.cs`:

```csharp
using ScreenShare.Display;

namespace ScreenShare.Tests.Display;

public sealed class DisplayTopologyTests
{
    // O Windows informa a escala em passos relativos à recomendada, na lista 100, 125, 150, 175, 200, 225, 250, 300...
    // O índice da recomendada é |min|; o spike viu min 0 e max 3 (100% recomendada, 175% no máximo).

    [Theory]
    [InlineData(0, 3, 100, 0)]
    [InlineData(0, 3, 150, 2)]
    [InlineData(0, 3, 200, 3)]   // limitado ao máximo do Windows (175%)
    [InlineData(0, 5, 200, 4)]
    [InlineData(-1, 3, 100, -1)] // recomendada 125%
    [InlineData(-1, 3, 125, 0)]
    [InlineData(0, 3, 110, 0)]   // degrau mais alto que não passa do pedido
    public void ToRelativeStep_finds_the_step_within_the_windows_limits(int min, int max, int percent, int expected)
    {
        Assert.Equal(expected, DisplayTopology.ToRelativeStep(min, max, percent));
    }

    [Theory]
    [InlineData(0, 0, 100)]
    [InlineData(0, 2, 150)]
    [InlineData(-1, 0, 125)]
    [InlineData(0, 99, 500)]   // fora da lista: fica no último degrau
    [InlineData(0, -5, 100)]
    public void ToPercent_reads_the_current_scale(int min, int current, int expected)
    {
        Assert.Equal(expected, DisplayTopology.ToPercent(min, current));
    }
}
```

Run: `dotnet test host/ScreenShare.slnx --filter FullyQualifiedName~DisplayTopologyTests`
Expected: erro de compilação, `DisplayTopology` não existe.

- [ ] **Step 2: Implementar a interface, o P/Invoke e a topologia**

`host/ScreenShare.Display/IDisplayTopology.cs`:

```csharp
namespace ScreenShare.Display;

/// <summary>
/// A saída do Virtual Display Driver como o Windows a vê agora. Desanexada (fora da área de trabalho) tem posição
/// e tamanho zerados. Modes: as resoluções que o driver oferece (as do XML, lidas quando o driver iniciou).
/// </summary>
public sealed record VddOutput(string DeviceName, bool Attached, int X, int Y, int Width, int Height,
    IReadOnlyList<(int Width, int Height)> Modes);

/// <summary>As operações de tela que o gerenciador usa. Tudo sem administrador.</summary>
public interface IDisplayTopology
{
    /// <summary>A saída do VDD, achada pelo ID de hardware (o nome \\.\DISPLAYn muda a cada reinício do driver); null sem driver.</summary>
    VddOutput? FindVddOutput();

    /// <summary>Põe a saída na área de trabalho, na posição e resolução dadas ("ligar").</summary>
    bool Attach(string deviceName, DisplayPosition position, int width, int height);

    /// <summary>Tira a saída da área de trabalho ("desligar"; o Windows lembra disso depois de reiniciar o PC).</summary>
    bool Detach(string deviceName);

    bool SetMode(string deviceName, int width, int height);

    /// <summary>Escala atual em %, ou null se a saída não está ativa.</summary>
    int? GetScale(string deviceName);

    /// <summary>Aplica a escala (limitada ao máximo que o Windows aceita para o monitor).</summary>
    bool SetScale(string deviceName, int percent);

    /// <summary>À direita da tela mais à direita, no topo: onde o monitor aparece da primeira vez.</summary>
    DisplayPosition DefaultPosition();
}
```

`host/ScreenShare.Display/Native/DisplayApi.cs`:

```csharp
using System.Runtime.InteropServices;

namespace ScreenShare.Display.Native;

/// <summary>Chamadas de tela do Windows (user32): saídas, modos, anexar/desanexar e escala via CCD.</summary>
internal static class DisplayApi
{
    public const uint AttachedToDesktop = 0x1;

    private const int EnumCurrentSettings = -1;
    private const uint DmPosition = 0x20, DmPelsWidth = 0x80000, DmPelsHeight = 0x100000;
    private const uint CdsUpdateRegistry = 0x1, CdsNoReset = 0x10000000;
    private const int DispChangeSuccessful = 0;
    private const uint QdcOnlyActivePaths = 0x2;
    private const int GetSourceName = 1, GetDpiScale = -3, SetDpiScale = -4; // -3/-4: não documentados, usados por Configurações

    public sealed record Adapter(string DeviceName, string DeviceId, uint StateFlags);

    public static IEnumerable<Adapter> Adapters()
    {
        for (uint index = 0; ; index++)
        {
            var device = new DISPLAY_DEVICEW { cb = Marshal.SizeOf<DISPLAY_DEVICEW>() };
            if (!EnumDisplayDevicesW(null, index, ref device, 0)) yield break;
            yield return new Adapter(device.DeviceName, device.DeviceID, device.StateFlags);
        }
    }

    public static (int X, int Y, int Width, int Height)? CurrentMode(string deviceName)
    {
        var mode = NewDevMode();
        return EnumDisplaySettingsExW(deviceName, EnumCurrentSettings, ref mode, 0)
            ? (mode.dmPositionX, mode.dmPositionY, (int)mode.dmPelsWidth, (int)mode.dmPelsHeight)
            : null;
    }

    public static IReadOnlyList<(int Width, int Height)> Modes(string deviceName)
    {
        var modes = new List<(int Width, int Height)>();
        for (var index = 0; ; index++)
        {
            var mode = NewDevMode();
            if (!EnumDisplaySettingsExW(deviceName, index, ref mode, 0)) break;
            var size = ((int)mode.dmPelsWidth, (int)mode.dmPelsHeight);
            if (!modes.Contains(size)) modes.Add(size);
        }
        return modes;
    }

    /// <summary>Anexa (posição + tamanho) ou desanexa (0×0) e aplica, como "Estender"/"Desconectar" em Configurações.</summary>
    public static bool SetPlacement(string deviceName, int x, int y, int width, int height)
    {
        var mode = NewDevMode();
        mode.dmFields = DmPosition | DmPelsWidth | DmPelsHeight;
        mode.dmPositionX = x;
        mode.dmPositionY = y;
        mode.dmPelsWidth = (uint)width;
        mode.dmPelsHeight = (uint)height;
        if (ChangeDisplaySettingsExW(deviceName, ref mode, IntPtr.Zero, CdsUpdateRegistry | CdsNoReset, IntPtr.Zero) != DispChangeSuccessful)
            return false;
        return ChangeDisplaySettingsExW(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero) == DispChangeSuccessful;
    }

    public static bool SetSize(string deviceName, int width, int height)
    {
        var mode = NewDevMode();
        mode.dmFields = DmPelsWidth | DmPelsHeight;
        mode.dmPelsWidth = (uint)width;
        mode.dmPelsHeight = (uint)height;
        return ChangeDisplaySettingsExW(deviceName, ref mode, IntPtr.Zero, CdsUpdateRegistry, IntPtr.Zero) == DispChangeSuccessful;
    }

    /// <summary>Escala em passos relativos à recomendada (min, atual, max); null se a saída não está ativa.</summary>
    public static (int Min, int Current, int Max)? GetScaleSteps(string deviceName)
    {
        if (FindSource(deviceName) is not { } source) return null;
        var info = new DPI_GET { header = Header(GetDpiScale, Marshal.SizeOf<DPI_GET>(), source) };
        return DisplayConfigGetDeviceInfo(ref info) == 0 ? (info.minScaleRel, info.curScaleRel, info.maxScaleRel) : null;
    }

    public static bool SetScaleStep(string deviceName, int relativeStep)
    {
        if (FindSource(deviceName) is not { } source) return false;
        var info = new DPI_SET { header = Header(SetDpiScale, Marshal.SizeOf<DPI_SET>(), source), scaleRel = relativeStep };
        return DisplayConfigSetDeviceInfo(ref info) == 0;
    }

    private static (LUID Adapter, uint Id)? FindSource(string deviceName)
    {
        if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount) != 0) return null;
        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
        if (QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return null;
        for (var index = 0; index < pathCount; index++)
        {
            var source = (paths[index].sourceInfo.adapterId, paths[index].sourceInfo.id);
            var name = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
            {
                header = Header(GetSourceName, Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(), source),
            };
            if (DisplayConfigGetDeviceInfo(ref name) == 0 &&
                string.Equals(name.viewGdiDeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                return source;
        }
        return null;
    }

    private static DISPLAYCONFIG_DEVICE_INFO_HEADER Header(int type, int size, (LUID Adapter, uint Id) source) =>
        new() { type = type, size = size, adapterId = source.Adapter, id = source.Id };

    private static DEVMODEW NewDevMode() => new() { dmSize = (ushort)Marshal.SizeOf<DEVMODEW>(), dmDeviceName = "", dmFormName = "" };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICEW
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODEW
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)] private struct DISPLAYCONFIG_RATIONAL { public uint Numerator, Denominator; }
    [StructLayout(LayoutKind.Sequential)] private struct DISPLAYCONFIG_PATH_SOURCE_INFO { public LUID adapterId; public uint id, modeInfoIdx, statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id, modeInfoIdx, outputTechnology, rotation, scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        [MarshalAs(UnmanagedType.Bool)] public bool targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO { public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo; public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo; public uint flags; }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct DISPLAYCONFIG_MODE_INFO { public uint infoType, id; public LUID adapterId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER { public int type; public int size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential)] private struct DPI_GET { public DISPLAYCONFIG_DEVICE_INFO_HEADER header; public int minScaleRel, curScaleRel, maxScaleRel; }
    [StructLayout(LayoutKind.Sequential)] private struct DPI_SET { public DISPLAYCONFIG_DEVICE_INFO_HEADER header; public int scaleRel; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(string? device, uint index, ref DISPLAY_DEVICEW displayDevice, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsExW(string device, int modeIndex, ref DEVMODEW mode, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsExW(string? device, ref DEVMODEW mode, IntPtr hwnd, uint flags, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsExW(string? device, IntPtr mode, IntPtr hwnd, uint flags, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] DISPLAYCONFIG_PATH_INFO[] paths,
        ref uint modeCount, [Out] DISPLAYCONFIG_MODE_INFO[] modes, IntPtr topologyId);

    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME info);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DPI_GET info);
    [DllImport("user32.dll")] private static extern int DisplayConfigSetDeviceInfo(ref DPI_SET info);
}
```

`host/ScreenShare.Display/DisplayTopology.cs`:

```csharp
using ScreenShare.Display.Driver;
using ScreenShare.Display.Native;

namespace ScreenShare.Display;

/// <summary>A topologia de tela real do Windows (veja <see cref="IDisplayTopology"/>). Validada no spike da Parte 2.</summary>
public sealed class DisplayTopology : IDisplayTopology
{
    /// <summary>Os degraus de escala do Windows, na ordem em que os passos relativos contam.</summary>
    private static readonly int[] ScaleSteps = [100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500];

    public VddOutput? FindVddOutput()
    {
        var adapter = DisplayApi.Adapters().FirstOrDefault(IsVdd);
        if (adapter is null) return null;
        var modes = DisplayApi.Modes(adapter.DeviceName);
        if ((adapter.StateFlags & DisplayApi.AttachedToDesktop) != 0 && DisplayApi.CurrentMode(adapter.DeviceName) is { } current)
            return new VddOutput(adapter.DeviceName, true, current.X, current.Y, current.Width, current.Height, modes);
        return new VddOutput(adapter.DeviceName, false, 0, 0, 0, 0, modes);
    }

    public bool Attach(string deviceName, DisplayPosition position, int width, int height) =>
        DisplayApi.SetPlacement(deviceName, position.X, position.Y, width, height);

    public bool Detach(string deviceName) => DisplayApi.SetPlacement(deviceName, 0, 0, 0, 0);

    public bool SetMode(string deviceName, int width, int height) => DisplayApi.SetSize(deviceName, width, height);

    public int? GetScale(string deviceName) =>
        DisplayApi.GetScaleSteps(deviceName) is { } steps ? ToPercent(steps.Min, steps.Current) : null;

    public bool SetScale(string deviceName, int percent) =>
        DisplayApi.GetScaleSteps(deviceName) is { } steps &&
        DisplayApi.SetScaleStep(deviceName, ToRelativeStep(steps.Min, steps.Max, percent));

    public DisplayPosition DefaultPosition()
    {
        var right = 0;
        foreach (var adapter in DisplayApi.Adapters())
        {
            if ((adapter.StateFlags & DisplayApi.AttachedToDesktop) == 0 || IsVdd(adapter)) continue;
            if (DisplayApi.CurrentMode(adapter.DeviceName) is { } mode) right = Math.Max(right, mode.X + mode.Width);
        }
        return new DisplayPosition(right, 0);
    }

    /// <summary>Passo relativo para pedir <paramref name="percent"/>: o degrau mais alto que não passa do pedido, limitado ao que o Windows aceita.</summary>
    public static int ToRelativeStep(int minRelative, int maxRelative, int percent)
    {
        var index = Math.Max(0, Array.FindLastIndex(ScaleSteps, step => step <= percent));
        return Math.Clamp(index - Math.Abs(minRelative), minRelative, maxRelative);
    }

    public static int ToPercent(int minRelative, int currentRelative) =>
        ScaleSteps[Math.Clamp(Math.Abs(minRelative) + currentRelative, 0, ScaleSteps.Length - 1)];

    private static bool IsVdd(DisplayApi.Adapter adapter) =>
        adapter.DeviceId.Equals(VddPackage.HardwareId, StringComparison.OrdinalIgnoreCase);
}
```

Run: `dotnet test host/ScreenShare.slnx --filter FullyQualifiedName~DisplayTopologyTests`
Expected: PASS (12 casos).

- [ ] **Step 3: Teste de integração opcional (só com o driver instalado)**

`host/ScreenShare.Tests/Display/VddFactAttribute.cs`:

```csharp
namespace ScreenShare.Tests.Display;

/// <summary>Teste que mexe nas telas de verdade: só roda com SCREENSHARE_VDD_TESTS=1 e o driver instalado.</summary>
public sealed class VddFactAttribute : FactAttribute
{
    public VddFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SCREENSHARE_VDD_TESTS") != "1")
            Skip = "Precisa do driver de monitor virtual instalado: defina SCREENSHARE_VDD_TESTS=1 para rodar.";
    }
}
```

`host/ScreenShare.Tests/Display/DisplayTopologyIntegrationTests.cs`:

```csharp
using ScreenShare.Display;

namespace ScreenShare.Tests.Display;

public sealed class DisplayTopologyIntegrationTests
{
    [VddFact]
    public void Attaches_changes_scale_and_detaches_the_real_vdd_output()
    {
        var topology = new DisplayTopology();
        var output = topology.FindVddOutput();
        Assert.NotNull(output);
        var mode = output.Modes[0];
        var wasAttached = output.Attached;
        try
        {
            if (output.Attached) Assert.True(topology.Detach(output.DeviceName));
            Assert.False(topology.FindVddOutput()!.Attached);

            Assert.True(topology.Attach(output.DeviceName, topology.DefaultPosition(), mode.Width, mode.Height));
            var attached = topology.FindVddOutput()!;
            Assert.True(attached.Attached);
            Assert.Equal(mode, (attached.Width, attached.Height));

            var scale = topology.GetScale(attached.DeviceName);
            Assert.NotNull(scale);
            Assert.True(topology.SetScale(attached.DeviceName, 125));
            Assert.Equal(125, topology.GetScale(attached.DeviceName));
            topology.SetScale(attached.DeviceName, scale.Value);
        }
        finally
        {
            if (!wasAttached && topology.FindVddOutput() is { Attached: true } now) topology.Detach(now.DeviceName);
        }
    }
}
```

Run: `dotnet test host/ScreenShare.slnx`
Expected: 0 avisos; todos passam e `Attaches_changes_scale_and_detaches_the_real_vdd_output` aparece como ignorado (sem a variável).

- [ ] **Step 4: Commit e push**

```bash
git add host/ScreenShare.Display host/ScreenShare.Tests/Display
git commit -m "feat(display): topologia de tela do monitor virtual (anexar, desanexar, resolução e escala)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

- [ ] **Step 5: Integração no PC do usuário (controlador)**

Com o driver instalado na Task 3, rode no PowerShell:

```powershell
$env:SCREENSHARE_VDD_TESTS = '1'; dotnet test host/ScreenShare.slnx --filter FullyQualifiedName~DisplayTopologyIntegrationTests; Remove-Item Env:SCREENSHARE_VDD_TESTS
```

Esperado: PASS. O monitor virtual aparece e some durante o teste. Registre no relatório.

---

### Task 5: `VirtualMonitorManager` (lease, contagem, desligamento em 10 s, resolução nova)

**Files:**
- Create: `host/ScreenShare.Display/VirtualMonitor.cs`
- Create: `host/ScreenShare.Display/VirtualMonitorManager.cs`
- Modify: `host/ScreenShare.Display/ScreenShare.Display.csproj` (`InternalsVisibleTo` para os testes)
- Modify: `host/ScreenShare.Tests/ScreenShare.Tests.csproj` (pacote `Microsoft.Extensions.TimeProvider.Testing`)
- Test: `host/ScreenShare.Tests/Display/FakeDisplayTopology.cs`, `host/ScreenShare.Tests/Display/VirtualMonitorManagerTests.cs`

**Interfaces:**
- Consumes: `IDisplayTopology`, `VddOutput` (Task 4); `DisplayStateStore`, `DisplayPosition`, `VddSettingsFile.EnsureMode`, `VddSettingsException`, `ScaleCalculator` (Task 2).
- Produces:
  - `ScreenShare.Display.VirtualMonitor`: `record(string DeviceName, int X, int Y, int Width, int Height, int ScalePercent)`.
  - `ScreenShare.Display.MonitorRequest`: `readonly record struct(int Width, int Height, int DensityDpi)` com `static MonitorRequest Normalize(int width, int height, int densityDpi)`.
  - `ScreenShare.Display.VirtualMonitorLease : IDisposable`:
    - `VirtualMonitorLease(MonitorRequest request, VirtualMonitor? monitor, Action? release)`;
    - `static VirtualMonitorLease Without(MonitorRequest request)`;
    - propriedades `Request`, `Monitor`, `Width`, `Height`.
  - `ScreenShare.Display.IVirtualMonitorManager`: `VirtualMonitorLease Acquire(int width, int height, int densityDpi)`.
  - `ScreenShare.Display.NullVirtualMonitorManager.Instance`.
  - `ScreenShare.Display.IDriverRestarter`: `Task<bool> RestartAsync()`.
  - `ScreenShare.Display.VirtualMonitorManager(IDisplayTopology, IDriverRestarter, DisplayStateStore, string settingsPath, TimeProvider, Action<string>? log = null) : IVirtualMonitorManager, IDisposable`; `TurnOffDelay` (10 s); `internal Task PendingRestart`.

- [ ] **Step 1: Pacote de teste e `InternalsVisibleTo`**

Em `host/ScreenShare.Tests/ScreenShare.Tests.csproj`, no `ItemGroup` dos `PackageReference`:

```xml
    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" Version="10.10.0" />
```

Em `host/ScreenShare.Display/ScreenShare.Display.csproj`:

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="ScreenShare.Tests" />
  </ItemGroup>
```

- [ ] **Step 2: Escrever a topologia falsa e os testes**

`host/ScreenShare.Tests/Display/FakeDisplayTopology.cs`:

```csharp
using ScreenShare.Display;

namespace ScreenShare.Tests.Display;

/// <summary>Topologia em memória: uma saída do VDD que liga, desliga e troca de resolução como o Windows fez no spike.</summary>
internal sealed class FakeDisplayTopology : IDisplayTopology
{
    public bool Present { get; set; } = true;
    public string DeviceName { get; set; } = @"\\.\DISPLAY5";
    public bool Attached { get; set; }
    public DisplayPosition Position { get; set; }
    public (int Width, int Height) Size { get; set; } = (1920, 1080);
    public List<(int Width, int Height)> Modes { get; } = [(1920, 1080)];
    public int Scale { get; set; } = 100;
    public DisplayPosition Default { get; set; } = new(3440, 0);
    public Func<DisplayPosition, bool> AcceptPosition { get; set; } = _ => true;
    public bool FailScale { get; set; }
    public Exception? ThrowOnFind { get; set; }
    public Exception? ThrowOnGetScale { get; set; }
    public int AttachCalls { get; private set; }
    public int DetachCalls { get; private set; }
    public int SetModeCalls { get; private set; }
    public int SetScaleCalls { get; private set; }
    public List<DisplayPosition> AttachedAt { get; } = [];

    public VddOutput? FindVddOutput()
    {
        if (ThrowOnFind is { } error) throw error;
        if (!Present) return null;
        return Attached
            ? new VddOutput(DeviceName, true, Position.X, Position.Y, Size.Width, Size.Height, [.. Modes])
            : new VddOutput(DeviceName, false, 0, 0, 0, 0, [.. Modes]);
    }

    public bool Attach(string deviceName, DisplayPosition position, int width, int height)
    {
        AttachCalls++;
        AttachedAt.Add(position);
        if (deviceName != DeviceName || !AcceptPosition(position) || !Modes.Contains((width, height))) return false;
        Attached = true;
        Position = position;
        Size = (width, height);
        return true;
    }

    public bool Detach(string deviceName)
    {
        DetachCalls++;
        if (deviceName != DeviceName) return false;
        Attached = false;
        return true;
    }

    public bool SetMode(string deviceName, int width, int height)
    {
        SetModeCalls++;
        if (deviceName != DeviceName || !Attached || !Modes.Contains((width, height))) return false;
        Size = (width, height);
        return true;
    }

    public int? GetScale(string deviceName)
    {
        if (ThrowOnGetScale is { } error) throw error;
        return Attached ? Scale : null;
    }

    public bool SetScale(string deviceName, int percent)
    {
        SetScaleCalls++;
        if (FailScale || deviceName != DeviceName || !Attached) return false;
        Scale = Math.Min(percent, 175); // o máximo que o Windows informou no spike
        return true;
    }

    public DisplayPosition DefaultPosition() => Default;
}
```

`host/ScreenShare.Tests/Display/VirtualMonitorManagerTests.cs`:

```csharp
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
```

- [ ] **Step 3: Rodar e ver falhar**

Run: `dotnet test host/ScreenShare.slnx --filter FullyQualifiedName~VirtualMonitorManagerTests`
Expected: erro de compilação, `VirtualMonitorManager` não existe.

- [ ] **Step 4: Implementar os tipos do lease e o gerenciador**

`host/ScreenShare.Display/VirtualMonitor.cs`:

```csharp
namespace ScreenShare.Display;

/// <summary>O monitor virtual como a captura (Parte 3) e o toque (Parte 5) o veem: saída, retângulo na área de trabalho e escala.</summary>
public sealed record VirtualMonitor(string DeviceName, int X, int Y, int Width, int Height, int ScalePercent);

/// <summary>O pedido do celular já dentro dos limites; largura e altura pares (o encoder da Parte 3 exige).</summary>
public readonly record struct MonitorRequest(int Width, int Height, int DensityDpi)
{
    public const int MinWidth = 640, MaxWidth = 7680, MinHeight = 360, MaxHeight = 4320, MinDpi = 72, MaxDpi = 1000;

    public static MonitorRequest Normalize(int width, int height, int densityDpi) => new(
        Math.Clamp(width, MinWidth, MaxWidth) & ~1,
        Math.Clamp(height, MinHeight, MaxHeight) & ~1,
        Math.Clamp(densityDpi, MinDpi, MaxDpi));
}

/// <summary>
/// O monitor que uma sessão está usando. Dispose libera (uma vez só); sem monitor, Width/Height são os do pedido.
/// </summary>
public sealed class VirtualMonitorLease(MonitorRequest request, VirtualMonitor? monitor, Action? release) : IDisposable
{
    private Action? _release = release;

    public static VirtualMonitorLease Without(MonitorRequest request) => new(request, null, null);

    public MonitorRequest Request { get; } = request;
    public VirtualMonitor? Monitor { get; } = monitor;
    public int Width => Monitor?.Width ?? Request.Width;
    public int Height => Monitor?.Height ?? Request.Height;

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

public interface IVirtualMonitorManager
{
    /// <summary>Monitor para uma sessão que acabou de mandar HELLO. Nunca lança: sem monitor, o lease vem vazio.</summary>
    VirtualMonitorLease Acquire(int width, int height, int densityDpi);
}

/// <summary>Sem monitor virtual (--sem-monitor ou driver ausente): o CONFIG leva a resolução pedida.</summary>
public sealed class NullVirtualMonitorManager : IVirtualMonitorManager
{
    public static NullVirtualMonitorManager Instance { get; } = new();

    public VirtualMonitorLease Acquire(int width, int height, int densityDpi) =>
        VirtualMonitorLease.Without(MonitorRequest.Normalize(width, height, densityDpi));
}

/// <summary>Reinicia o driver (exige administrador: UAC) para ele reler a lista de resoluções. true = reiniciou.</summary>
public interface IDriverRestarter
{
    Task<bool> RestartAsync();
}
```

`host/ScreenShare.Display/VirtualMonitorManager.cs`:

```csharp
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
        var request = MonitorRequest.Normalize(width, height, densityDpi);
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
            _leases++;
            CancelTurnOff();
            return new VirtualMonitorLease(request, monitor, Release);
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
            return;
        }
        if (!restarted)
        {
            _log($"O driver não foi reiniciado; {request.Width}×{request.Height} fica para a próxima execução do host.");
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
                                _log($"Monitor virtual agora em {monitor.Width}×{monitor.Height} (escala {monitor.ScalePercent}%).");
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
                    return;
                }
            }
            if (_time.GetUtcNow() >= deadline)
            {
                _log($"O driver reiniciou, mas {request.Width}×{request.Height} não apareceu.");
                return;
            }
            await Task.Delay(PollInterval, _time);
        }
    }

    private void Release()
    {
        lock (_gate)
        {
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
```

Run: `dotnet test host/ScreenShare.slnx --filter FullyQualifiedName~VirtualMonitorManagerTests`
Expected: PASS (26 casos).

- [ ] **Step 5: Rodar tudo, commit e push**

Run: `dotnet build host/ScreenShare.slnx` e `dotnet test host/ScreenShare.slnx`
Expected: 0 avisos; todos passam.

```bash
git add host/ScreenShare.Display host/ScreenShare.Tests
git commit -m "feat(display): gerenciador do monitor virtual com contagem de referências e resolução nova

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 6: DevHost com monitor virtual (instalação ao iniciar, `--sem-monitor`, `CONFIG` com a resolução aplicada)

**Files:**
- Create: `host/ScreenShare.Display/Driver/ElevatedDriverRestarter.cs`
- Create: `host/ScreenShare.DevHost/MonitorSetup.cs`
- Modify: `host/ScreenShare.DevHost/HostServer.cs` (construtor e `ServeSessionAsync`)
- Modify: `host/ScreenShare.DevHost/Program.cs` (criar o gerenciador e passar ao servidor)
- Test: `host/ScreenShare.Tests/DevHost/HostServerTests.cs` (gerenciador falso e testes novos), `host/ScreenShare.Tests/DevHost/MonitorSetupTests.cs`

**Interfaces:**
- Consumes:
  - `IVirtualMonitorManager`, `VirtualMonitorLease`, `MonitorRequest`, `VirtualMonitor`, `NullVirtualMonitorManager`, `VirtualMonitorManager`, `IDriverRestarter` (Task 5);
  - `DisplayTopology` (Task 4);
  - `WindowsDriverSystem`, `ElevatedCommand`, `DriverExitCode` (Task 3);
  - `DriverCommands.Describe` (Task 3);
  - `DisplayStateStore`, `VddSettingsFile.DefaultPath` (Task 2).
- Produces:
  - `HostServer(..., TimeSpan? idleTimeout = null, IVirtualMonitorManager? monitors = null)`;
  - `ScreenShare.Display.Driver.ElevatedDriverRestarter`;
  - `ScreenShare.DevHost.MonitorSetup.Create(Action<string> log)` (`VirtualMonitorManager?`) e `MonitorSetup.IsYes(string answer)`.

- [ ] **Step 1: Escrever os testes do `HostServer` com monitor**

Em `host/ScreenShare.Tests/DevHost/HostServerTests.cs`:

1. acrescente `using ScreenShare.Display;` aos `using`;
2. acrescente o campo `private readonly FakeMonitorManager _monitors = new();`;
3. na construção do `_server`, acrescente `, monitors: _monitors` depois de `idleTimeout: TimeSpan.FromSeconds(2)`;
4. acrescente os testes e a classe abaixo dentro de `HostServerTests`.

O `FakeMonitorManager` começa sem monitor (`Monitor = null`), então os testes que já existem continuam recebendo no `CONFIG` o tamanho do `HELLO`.

```csharp
    /// <summary>Espera uma condição que outro fio (o servidor) vai tornar verdadeira.</summary>
    private async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "a condição não ficou verdadeira em 5 s");
            await Task.Delay(20, _cts.Token);
        }
    }

    /// <summary>Wi-Fi + AUTH de um aparelho recém-adicionado + HELLO, até receber o CONFIG.</summary>
    private async Task<(TcpClient Client, SslStream Stream, MessageReader Reader, ConfigMessage Config)> ConnectWithHelloAsync()
    {
        var (_, token) = _devices.Add("Pixel 8");
        var (client, stream, reader) = await ConnectWifiAsync();
        await SendAsync(stream, new AuthMessage(token));
        await SendAsync(stream, Hello());
        var config = Assert.IsType<ConfigMessage>(await reader.ReadAsync(_cts.Token));
        return (client, stream, reader, config);
    }

    [Fact]
    public async Task Config_carries_the_virtual_monitor_resolution()
    {
        _monitors.Monitor = new VirtualMonitor(@"\\.\DISPLAY9", 3440, 0, 1920, 1080, 175);

        var (client, _, _, config) = await ConnectWithHelloAsync();
        using var _ = client;

        Assert.Equal((1920, 1080), (config.Width, config.Height));
        Assert.Equal((2400, 1080, 420), Assert.Single(_monitors.Requests));
    }

    [Fact]
    public async Task Monitor_is_released_when_the_phone_disconnects()
    {
        _monitors.Monitor = new VirtualMonitor(@"\\.\DISPLAY9", 3440, 0, 2400, 1080, 175);
        var (client, _, _, _) = await ConnectWithHelloAsync();

        client.Dispose();

        await WaitUntilAsync(() => _monitors.Released == 1);
    }

    [Fact]
    public async Task Monitor_is_released_when_the_session_dies_by_timeout()
    {
        _monitors.Monitor = new VirtualMonitor(@"\\.\DISPLAY9", 3440, 0, 2400, 1080, 175);
        var (client, _, _, _) = await ConnectWithHelloAsync();
        using var _ = client;

        // calado além do idleTimeout (2 s): o servidor derruba a sessão
        await WaitUntilAsync(() => _monitors.Released == 1);
    }

    [Fact]
    public async Task Session_that_ends_before_hello_takes_no_monitor()
    {
        var (_, token) = _devices.Add("Pixel 8");
        var (client, stream, _) = await ConnectWifiAsync();
        await SendAsync(stream, new AuthMessage(token));
        client.Dispose();

        // A porta atende um cliente por vez: quando o próximo recebe CONFIG, o anterior já acabou.
        var (next, _, _, _) = await ConnectWithHelloAsync();
        using var _ = next;

        Assert.Equal(1, _monitors.Acquired);
    }

    private sealed class FakeMonitorManager : IVirtualMonitorManager
    {
        private int _acquired;
        private int _released;

        public VirtualMonitor? Monitor { get; set; }
        public List<(int Width, int Height, int Dpi)> Requests { get; } = [];
        public int Acquired => _acquired;
        public int Released => _released;

        public VirtualMonitorLease Acquire(int width, int height, int densityDpi)
        {
            lock (Requests) Requests.Add((width, height, densityDpi));
            Interlocked.Increment(ref _acquired);
            return new VirtualMonitorLease(MonitorRequest.Normalize(width, height, densityDpi), Monitor,
                () => Interlocked.Increment(ref _released));
        }
    }
```

`host/ScreenShare.Tests/DevHost/MonitorSetupTests.cs`:

```csharp
using ScreenShare.DevHost;

namespace ScreenShare.Tests.DevHost;

public sealed class MonitorSetupTests
{
    [Theory]
    [InlineData("", true)]     // Enter = sim (o padrão é [S/n])
    [InlineData("s", true)]
    [InlineData(" S ", true)]
    [InlineData("sim", true)]
    [InlineData("y", true)]
    [InlineData("n", false)]
    [InlineData("não", false)]
    [InlineData("talvez", false)]
    public void IsYes_accepts_enter_and_yes(string answer, bool expected)
    {
        Assert.Equal(expected, MonitorSetup.IsYes(answer));
    }
}
```

Run: `dotnet test host/ScreenShare.slnx --filter "FullyQualifiedName~HostServerTests|FullyQualifiedName~MonitorSetupTests"`
Expected: erro de compilação (o `HostServer` não tem o parâmetro `monitors`, e `MonitorSetup` não existe).

- [ ] **Step 2: `HostServer` com o gerenciador**

Em `host/ScreenShare.DevHost/HostServer.cs`:

1. acrescente `using ScreenShare.Display;`;
2. acrescente o campo `private readonly IVirtualMonitorManager _monitors;`;
3. mude a assinatura do construtor para:

```csharp
    /// <param name="monitors">Monitor virtual por sessão (padrão: nenhum; o CONFIG leva a resolução do HELLO).</param>
    public HostServer(int wifiPort, int usbPort, HostIdentity identity, PairingSession pairing, DeviceRegistry devices,
        Action<string>? log = null, TimeSpan? handshakeTimeout = null, TimeSpan? idleTimeout = null,
        IVirtualMonitorManager? monitors = null)
```

e acrescente no corpo `_monitors = monitors ?? NullVirtualMonitorManager.Instance;`. Acrescente o `<param name="monitors">` ao lado dos outros `<param>` existentes.

4. Em `ServeSessionAsync`, troque a linha do `CONFIG`:

```csharp
        await SendAsync(stream, new ConfigMessage(hello.Width, hello.Height, VideoCodec.H264, StubBitrateKbps, []), cancellationToken);
```

por:

```csharp
        // O monitor vale enquanto a sessão durar; o using o libera em qualquer saída (fim, erro ou prazo).
        using var lease = _monitors.Acquire(hello.Width, hello.Height, hello.DensityDpi);
        if (lease.Monitor is { } monitor)
            _log?.Invoke($"Monitor virtual: {monitor.DeviceName} {monitor.Width}×{monitor.Height} em ({monitor.X},{monitor.Y}), escala {monitor.ScalePercent}%.");
        await SendAsync(stream, new ConfigMessage((ushort)lease.Width, (ushort)lease.Height, VideoCodec.H264, StubBitrateKbps, []), cancellationToken);
```

5. Atualize o resumo da classe: troque `/// Servidor de desenvolvimento (sem vídeo).` por `/// Servidor de desenvolvimento (sem vídeo; liga o monitor virtual por sessão).`

- [ ] **Step 3: Reinício elevado e configuração ao iniciar**

`host/ScreenShare.Display/Driver/ElevatedDriverRestarter.cs`:

```csharp
namespace ScreenShare.Display.Driver;

/// <summary>Reinicia o driver rodando este executável com "restart-driver" como administrador (UAC).</summary>
public sealed class ElevatedDriverRestarter : IDriverRestarter
{
    public Task<bool> RestartAsync() =>
        Task.Run(() => ElevatedCommand.Run("restart-driver") == (int)DriverExitCode.Ok);
}
```

`host/ScreenShare.DevHost/MonitorSetup.cs`:

```csharp
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
```

- [ ] **Step 4: `Program.cs`**

Em `host/ScreenShare.DevHost/Program.cs`:

1. acrescente `using ScreenShare.Display;` aos `using`;
2. logo depois de `var devices = new DeviceRegistry(...)`, acrescente:

```csharp
// Monitor virtual: liga quando um celular conecta (--sem-monitor desliga o recurso e não mexe no driver).
using var monitors = args.Contains("--sem-monitor") ? null : MonitorSetup.Create(Log);
```

3. troque a criação do servidor por:

```csharp
using var server = new HostServer(WifiPort, UsbPort, identity, pairing, devices, Log, monitors: monitors);
```

4. logo depois da linha `Console.WriteLine($"IPs anunciados: ...")`, acrescente:

```csharp
Console.WriteLine(monitors is null
    ? "Monitor virtual: desligado (sem driver ou --sem-monitor); o CONFIG leva a resolução do celular."
    : "Monitor virtual: pronto; liga quando um celular conecta e sai da área de trabalho 10 s depois que ele desconecta.");
```

Como o `using var monitors` vem antes do `using var server`, o servidor é descartado primeiro e o monitor é desligado por último, inclusive no Ctrl+C.

Run: `dotnet build host/ScreenShare.slnx` e `dotnet test host/ScreenShare.slnx`
Expected: 0 avisos; todos passam (os 4 testes novos do `HostServer` e os 8 do `MonitorSetupTests` incluídos).

- [ ] **Step 5: Commit e push**

```bash
git add host/ScreenShare.Display host/ScreenShare.DevHost host/ScreenShare.Tests
git commit -m "feat(devhost): monitor virtual por sessão, instalação do driver ao iniciar e --sem-monitor

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 7: documentação e verificação manual

**Files:**
- Modify: `README.md`
- Modify: `docs/guia-do-codigo.md`

**Interfaces:**
- Consumes: tudo das Tasks 2 a 6.
- Produces: documentação; a lista de verificação manual feita com o usuário.

- [ ] **Step 1: README**

Em `README.md`:

1. No roteiro, troque `- [ ] **Parte 2** — Monitor virtual (instalação do VDD e \`DisplayManager\`)` por:

```markdown
- [x] **Parte 2** — Monitor virtual: o DevHost instala o [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) e liga um monitor na resolução do celular quando ele conecta
```

2. Em "🚀 Começando", depois do bloco `dotnet run --project host/ScreenShare.DevHost` e antes de "Comandos do console do DevHost:", acrescente:

~~~markdown
**Monitor virtual.** Na primeira execução o DevHost pergunta se pode instalar o driver de monitor virtual (Virtual Display Driver, open source). Respondendo **S**:

1. O Windows pede permissão de administrador (UAC).
2. Em seguida, pergunta se pode instalar o software de dispositivo. Pode deixar marcado "Sempre confiar": o ScreenShare remove essa confiança logo depois, porque o driver instalado não precisa dela.

A partir daí:

- **Celular conecta:** aparece um monitor novo em Configurações › Tela, na resolução do celular, com escala ajustada pelo tamanho da tela dele.
- **Primeira vez de um celular:** o Windows pede permissão mais uma vez, para o driver aprender a resolução nova.
- **Celular desconecta:** o monitor sai da área de trabalho 10 s depois. Ele continua listado em Configurações, como "desconectado", enquanto o driver estiver instalado.
- **Escala e posição:** o que você mudar em Configurações › Tela continua valendo nas próximas conexões.

Para rodar sem monitor virtual: `dotnet run --project host/ScreenShare.DevHost -- --sem-monitor`. Para remover o driver:

```powershell
dotnet run --project host/ScreenShare.DevHost -- uninstall-driver
```
~~~

3. Na tabela "Como funciona", troque a linha `| Monitor virtual | [Virtual Display Driver](...) (open source, já assinado) |` por:

```markdown
| Monitor virtual | [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) (open source, já assinado), instalado pelo DevHost; liga e desliga pelo próprio Windows, sem administrador |
```

- [ ] **Step 2: Guia do código**

Em `docs/guia-do-codigo.md`, acrescente uma seção `## ScreenShare.Display (monitor virtual)` no mesmo tom das outras (explicando para quem não conhece C#), com estes pontos, nesta ordem:

1. **Para que serve:** fazer o Windows ganhar um monitor quando o celular conecta. O driver é o Virtual Display Driver; o projeto só o instala e o controla.
2. **`VddSettingsFile`:** o XML do driver, com a lista de resoluções. O driver só lê esse XML quando reinicia.
3. **`DisplayTopology` / `IDisplayTopology`:** liga (anexa à área de trabalho) e desliga (desanexa) a saída do driver pelo Windows, sem administrador; troca a resolução e a escala. Explica por que a saída é achada pelo ID `Root\MttVDD` e não pelo nome `\\.\DISPLAYn`.
4. **`VirtualMonitorManager`:**
   - contagem de referências;
   - espera de 10 s antes de desligar;
   - resolução nova = XML + reinício do driver com UAC, em segundo plano;
   - por que nunca derruba a sessão.
5. **`DisplayStateStore`:** o `display.json` (escala aplicada uma vez, última posição).
6. **`DriverInstaller` / `WindowsDriverSystem` / `ElevatedCommand`:**
   - os modos `install-driver`, `uninstall-driver` e `restart-driver`, únicos que rodam como administrador;
   - o SHA-256 fixado;
   - a remoção do certificado de `TrustedPublisher`.
7. **Por que não o pipe do driver:** o spike mostrou que os comandos dele derrubam o driver. Aponte para a seção "Resultados do spike" do spec.

Acrescente também, onde o guia lista os projetos da solução, a linha do `ScreenShare.Display`.

- [ ] **Step 3: Commit e push**

```bash
git add README.md docs/guia-do-codigo.md
git commit -m "docs: monitor virtual no README e no guia do código

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

- [ ] **Step 4: Verificação manual com o usuário (controlador)**

O controlador conduz, um passo por vez, a lista "Manuais" da seção "Testes" do spec. Os passos são:
1. desinstalar o driver de teste (`uninstall-driver`);
2. rodar o DevHost do zero;
3. responder S;
4. conectar o celular duas vezes;
5. desconectar e reconectar;
6. Ctrl+C;
7. escala e posição à mão;
8. `uninstall-driver` no fim, ou manter o driver se o usuário preferir.

Anote cada resultado no relatório final.
