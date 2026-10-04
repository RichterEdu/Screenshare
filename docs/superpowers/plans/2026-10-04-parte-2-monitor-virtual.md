# Parte 2: monitor virtual, plano de implementação

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Quando um celular conecta, o Windows ganha um monitor de verdade (VDD) na resolução do celular, com escala automática. Quando a última sessão termina, o monitor some 10 s depois. O host instala o driver sozinho, com UAC, se ele faltar.

**Architecture:** Projeto novo `host/ScreenShare.Display` (`net10.0-windows`), com a lógica pura (XML do VDD, escala, registro de resoluções) separada das chamadas ao Windows (SetupAPI, APIs de tela, pipe do VDD). O `HostServer` do DevHost recebe um `IVirtualMonitorManager` e manda no `CONFIG` a resolução realmente aplicada.

**Tech Stack:** C# / .NET 10, xUnit, P/Invoke (setupapi, newdev, user32), System.Text.Json, System.Xml.Linq, `Microsoft.Extensions.TimeProvider.Testing` (testes).

**Spec:** `docs/superpowers/specs/2026-10-04-parte-2-monitor-virtual-design.md`

## Estrutura deste plano

O código do driver empacotado (24.12.24) põe em dúvida o controle em tempo de uso pelo pipe (veja "Achado ao preparar o plano" no spec). Por isso o plano tem duas etapas:

- **Agora:** Task 1 (spike) e Task 2 (lógica pura, que vale qualquer que seja o resultado do spike).
- **Depois do spike:** as Tasks 3 a 8 são escritas com o resultado dele, num commit que acrescenta este arquivo, e revisadas pelo usuário antes de executar. O escopo delas já está fixado em "Tasks posteriores ao spike", no fim.

## Global Constraints

- Driver: release **25.7.23**, asset `VirtualDisplayDriver-x86.Driver.Only.zip` (driver 24.12.24, DLL x64), SHA-256 `e24210692b442b39af763536330ce78b423f19342b7a7792c26de3944e418b3a`. URL: `https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/25.7.23/VirtualDisplayDriver-x86.Driver.Only.zip`.
- ID de hardware `Root\MttVDD`; classe Display `{4d36e968-e325-11ce-bfc1-08002be10318}`.
- Configuração do VDD: `C:\VirtualDisplayDriver\vdd_settings.xml`.
- Pipe `\\.\pipe\MTTVirtualDisplayPipe`, comandos em UTF-16, prazo de 2 s para conectar.
- Esperar a saída do monitor aparecer: até 5 s. Desligar depois da última sessão: 10 s.
- Limites do pedido: largura 640–7680, altura 360–4320, DPI 72–1000.
- Escala: `DPI ÷ 160 × 100`, arredondada (meio para cima) ao múltiplo de 25 mais próximo, limitada a 100–200.
- Resoluções que já receberam escala automática: `%LOCALAPPDATA%\ScreenShare\display.json`.
- O host nunca roda como administrador, exceto nos modos `install-driver` / `uninstall-driver`. Códigos de saída: 0 ok, 2 SHA-256 diferente, 3 download, 4 SetupAPI, 5 usuário recusou a confirmação do Windows.
- O certificado do driver **não** entra em `TrustedPublisher` (salvo se o spike mostrar que é impossível; aí a decisão volta ao usuário).
- O protocolo não muda.
- Mensagens ao usuário e comentários de código em pt-BR, como no resto do repositório.
- `ScreenShare.Display` e `ScreenShare.Tests` (e, na Task 7, `ScreenShare.DevHost`) usam `net10.0-windows`.
- `dotnet build host/ScreenShare.slnx` com 0 avisos e `dotnet test host/ScreenShare.slnx` verde em cada commit.
- Cada commit termina com `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`, e cada task termina com `git push`.

## Review Focus

1. **Liga/desliga repetido:** se o mecanismo escolhido derrubar e reiniciar o processo do driver (UMDF), depois de algumas vezes o Windows pode desabilitar o dispositivo. O esperado é poder conectar e desconectar o dia inteiro. *Teste:* E4 do spike repete o ciclo 10 vezes. Na Task 3, o teste de integração manual também repete.
2. **PC reiniciado com o host fechado:** o monitor fantasma não pode ficar ativo no login, porque as janelas iriam para uma tela invisível. *Teste:* E6 do spike (reiniciar com o monitor desligado). Na Task 6, a limpeza ao iniciar.
3. **Largura ou altura ímpar no `HELLO`** (ex.: 2273×1080, porque o Android desconta a barra de navegação): o modo precisa ser par para o encoder da Parte 3. *Teste:* na Task 6, o gerenciador arredonda para baixo para número par antes de qualquer coisa. Fica fixado aqui para não se perder.
4. **XML do VDD editado por outro programa** (o app oficial do VDD ou o próprio driver gravando o `count`): editar não pode apagar o que não conhecemos, e uma gravação interrompida não pode deixar um XML pela metade. *Teste:* Task 2 (preserva elementos e comentários; gravação atômica).
5. **`display.json` corrompido ou de outra versão:** não pode impedir a conexão. *Teste:* Task 2 (corrompido conta como vazio e é regravado).

---

### Task 1: spike do VDD no PC do usuário (código descartável)

**Quem executa:** o controlador, na sessão principal, junto com o usuário. O usuário aceita o UAC e a confirmação do Windows, e às vezes olha Configurações › Tela. Não é despachada a um subagente, porque precisa dessa interação.

**Files:**
- Create (fora do repositório, descartável): `<scratchpad>/vdd-spike/VddSpike.csproj` e `Program.cs`. É um console `net10.0-windows` com os subcomandos abaixo.
- Modify: `docs/superpowers/specs/2026-10-04-parte-2-monitor-virtual-design.md` (nova seção "Resultados do spike").

**Interfaces:**
- Consumes: nada.
- Produces: a seção "Resultados do spike" no spec, com uma resposta por experimento, e a decisão de mecanismo usada nas Tasks 3 a 8.

Subcomandos do console de spike (P/Invoke direto, sem tratamento elaborado de erro):

| Subcomando | Precisa de admin | O que faz |
|---|---|---|
| `install <pasta-do-inf>` | sim | Cria `Root\MttVDD` (SetupAPI) e instala o `.inf` (`UpdateDriverForPlugAndPlayDevicesW`); imprime `reboot` e `GetLastError` |
| `uninstall` | sim | Para cada `Root\MttVDD`: lê `DEVPKEY_Device_DriverInfPath`, `DIF_REMOVE`, `SetupUninstallOEMInfW(SUOI_FORCEDELETE)` |
| `pipe <comando>` | não | Conecta em `MTTVirtualDisplayPipe` (2 s), escreve o comando em UTF-16, lê a resposta até fechar e imprime |
| `list` | não | `EnumDisplayDevicesW` (adaptadores e monitores: `DeviceName`, `DeviceString`, `DeviceID`, `StateFlags`) e, para cada saída ativa, `EnumDisplaySettingsExW(ENUM_CURRENT_SETTINGS)` (posição e tamanho) e todos os modos disponíveis |
| `mode <\\.\DISPLAYn> <w> <h>` | não | `ChangeDisplaySettingsExW` com `DM_PELSWIDTH \| DM_PELSHEIGHT` e `CDS_UPDATEREGISTRY` |
| `detach <\\.\DISPLAYn>` / `attach <\\.\DISPLAYn> <x> <y> <w> <h>` | não | Desliga e religa a saída pelo método do Windows: largura e altura 0 com `DM_POSITION` + `CDS_UPDATEREGISTRY \| CDS_NORESET`, depois `ChangeDisplaySettingsExW(null, …)` |
| `scale <\\.\DISPLAYn> <percent>` | não | `QueryDisplayConfig` + `DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME` para achar a fonte, depois `DisplayConfigGetDeviceInfo(-3)` e `DisplayConfigSetDeviceInfo(-4)` com o passo relativo ao recomendado. Escalas: 100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500 |

Referência de P/Invoke da instalação (será reaproveitada na Task 4):

```csharp
[StructLayout(LayoutKind.Sequential)]
struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }

[DllImport("setupapi.dll", SetLastError = true)]
static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid classGuid, IntPtr hwndParent);
[DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
static extern bool SetupDiCreateDeviceInfoW(IntPtr set, string deviceName, ref Guid classGuid, string? description,
    IntPtr hwndParent, int creationFlags /* DICD_GENERATE_ID = 1 */, ref SP_DEVINFO_DATA data);
[DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA data,
    int property /* SPDRP_HARDWAREID = 1 */, byte[] buffer, int size);
[DllImport("setupapi.dll", SetLastError = true)]
static extern bool SetupDiCallClassInstaller(int function /* DIF_REGISTERDEVICE = 0x19, DIF_REMOVE = 0x05 */,
    IntPtr set, ref SP_DEVINFO_DATA data);
[DllImport("setupapi.dll", SetLastError = true)]
static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
[DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr hwnd, string hardwareId, string fullInfPath,
    int installFlags, out bool rebootRequired);

// ID de hardware: multi-sz UTF-16 "Root\MttVDD\0\0"
var hardwareId = System.Text.Encoding.Unicode.GetBytes("Root\\MttVDD\0\0");
```

- [ ] **Step 1: Conferir o pacote.** Baixe o zip da URL das Global Constraints para `<scratchpad>/vdd-spike/pkg`, confira o SHA-256 e extraia. Esperado: `VirtualDisplayDriver/{MttVDD.dll, MttVDD.inf, mttvdd.cat, vdd_settings.xml}`.

- [ ] **Step 2: Escrever e compilar o console de spike** com os subcomandos da tabela. Compile com `dotnet build` e rode `list` para ver o estado de antes (só a RTX 5060 Ti).

- [ ] **Step 3: Preparar o XML mínimo (como administrador, junto com a instalação).** Crie `C:\VirtualDisplayDriver\vdd_settings.xml` com exatamente o conteúdo abaixo, que é o mesmo que a Task 2 vai gerar:

```xml
<?xml version="1.0" encoding="utf-8"?>
<vdd_settings>
  <monitors>
    <count>1</count>
  </monitors>
  <gpu>
    <friendlyname>default</friendlyname>
  </gpu>
  <global>
    <g_refresh_rate>60</g_refresh_rate>
  </global>
  <resolutions>
    <resolution>
      <width>1920</width>
      <height>1080</height>
      <refresh_rate>60</refresh_rate>
    </resolution>
  </resolutions>
</vdd_settings>
```

Dê permissão de modificação na pasta ao usuário (`icacls C:\VirtualDisplayDriver /grant "%USERNAME%":(OI)(CI)M`) e a `LOCAL SERVICE` (`/grant *S-1-5-19:(OI)(CI)M`).

- [ ] **Step 4: Executar os experimentos** e anotar o resultado de cada um. Os comandos com administrador rodam num PowerShell elevado aberto pelo controlador (`Start-Process -Verb RunAs`), e o usuário aceita o UAC.

| # | Experimento | O que anotar |
|---|---|---|
| E1 | `install` sem `TrustedPublisher` | Apareceu a confirmação do Windows? A instalação funcionou? O dispositivo ficou com status OK (`Get-PnpDevice`)? O monitor apareceu (count 1)? Ele virou o principal? Mexeu nas janelas? |
| E2 | `pipe PING` sem admin | A resposta (`PONG`?) |
| E3 | `list` | Como identificar o VDD: `DeviceString` do adaptador, `DeviceID` do monitor e o `\\.\DISPLAYn` |
| E4 | `pipe SETDISPLAYCOUNT 0`, depois `SETDISPLAYCOUNT 1`, 10 vezes | O monitor some e volta? Em quanto tempo? O processo do driver reinicia (Visualizador de Eventos › Aplicativos e Serviços › Microsoft › Windows › DriverFrameworks-UserMode)? O dispositivo continua OK depois de 10 ciclos? O XML foi gravado pelo driver (permissão)? |
| E5 | Sem admin: acrescentar 2400×1080 no XML e `pipe RELOAD_DRIVER` | O modo aparece em `list`? Se não aparecer, repita com `SETDISPLAYCOUNT 0` e `1`. Se ainda não aparecer, rode `pnputil /restart-device "ROOT\DISPLAY\0000"` (ID real do `list`) como administrador |
| E6 | `detach` / `attach` sem admin | O monitor sai e volta em Configurações › Tela? As janelas que estavam nele voltam para o principal? Com `detach` feito e o PC reiniciado, ele continua desligado no login? |
| E7 | `mode \\.\DISPLAYn 2400 1080` sem admin | Funcionou? O que `list` mostra depois? |
| E8 | `scale \\.\DISPLAYn 200` sem admin e leitura de volta; depois mudar à mão para 150% em Configurações e ler de novo | A escala aplicada e a escala lida |
| E9 | `uninstall` (admin) | Saiu do Gerenciador de Dispositivos? `pnputil /enum-drivers` ainda lista o `mttvdd.inf`? O monitor sumiu? |

- [ ] **Step 5: Escolher o mecanismo** e registrar a escolha em "Resultados do spike":
  - **Ligar e desligar:** pelo pipe (se E4 funcionou sem derrubar o dispositivo) ou pelo `detach`/`attach` (E6).
  - **Resolução nova:** pelo pipe (E5 direto), por count 0→1, ou reiniciando o dispositivo como administrador (UAC uma vez por resolução nova).
  - **Valor de count no XML instalado:** 1 se o liga/desliga for pelo `detach`/`attach`; 0 se for pelo pipe.
  - **`TrustedPublisher`:** desnecessário (E1 funcionou com a confirmação) ou necessário. Se for necessário, perguntar ao usuário antes de seguir.
  - **Identificação do VDD:** o critério exato de E3.

- [ ] **Step 6: Deixar o PC limpo.** O E9 já desinstala. Apague `C:\VirtualDisplayDriver` e a pasta do spike.

- [ ] **Step 7: Commit e push** do spec com os resultados.

```bash
git add docs/superpowers/specs/2026-10-04-parte-2-monitor-virtual-design.md
git commit -m "docs: resultados do spike do VDD na Parte 2

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 2: projeto ScreenShare.Display com a lógica pura (XML do VDD, escala, resoluções já escaladas)

**Files:**
- Create: `host/ScreenShare.Display/ScreenShare.Display.csproj`
- Create: `host/ScreenShare.Display/VddSettingsFile.cs`
- Create: `host/ScreenShare.Display/ScaleCalculator.cs`
- Create: `host/ScreenShare.Display/ScaledResolutionsStore.cs`
- Create: `host/ScreenShare.Display/AtomicFile.cs`
- Modify: `host/ScreenShare.slnx` (acrescentar o projeto)
- Modify: `host/ScreenShare.Tests/ScreenShare.Tests.csproj` (`net10.0-windows` + referência ao `ScreenShare.Display`)
- Test: `host/ScreenShare.Tests/Display/VddSettingsFileTests.cs`, `ScaleCalculatorTests.cs`, `ScaledResolutionsStoreTests.cs`

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
  - `ScreenShare.Display.ScaledResolutionsStore(string path)`: `bool Contains(int width, int height)`, `void Add(int width, int height)`, `static string DefaultPath`.
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

- [ ] **Step 8: Escrever os testes do `ScaledResolutionsStore`**

`host/ScreenShare.Tests/Display/ScaledResolutionsStoreTests.cs`:

```csharp
using ScreenShare.Display;

namespace ScreenShare.Tests.Display;

public sealed class ScaledResolutionsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screenshare-tests", Guid.NewGuid().ToString("N"));
    private string StorePath => Path.Combine(_dir, "ScreenShare", "display.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Missing_file_contains_nothing()
    {
        Assert.False(new ScaledResolutionsStore(StorePath).Contains(2400, 1080));
    }

    [Fact]
    public void Added_resolution_survives_a_new_instance()
    {
        new ScaledResolutionsStore(StorePath).Add(2400, 1080);

        var reopened = new ScaledResolutionsStore(StorePath);
        Assert.True(reopened.Contains(2400, 1080));
        Assert.False(reopened.Contains(1080, 2400));
    }

    [Fact]
    public void Adding_twice_keeps_a_single_entry()
    {
        var store = new ScaledResolutionsStore(StorePath);
        store.Add(2400, 1080);
        store.Add(2400, 1080);

        Assert.Equal(1, File.ReadAllText(StorePath).Split("2400x1080").Length - 1);
    }

    [Theory]
    [InlineData("isto não é json")]
    [InlineData("{\"scaled\": 42}")]
    [InlineData("null")]
    [InlineData("")]
    public void Corrupted_file_counts_as_empty_and_is_rewritten_on_add(string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, contents);
        var store = new ScaledResolutionsStore(StorePath);

        Assert.False(store.Contains(2400, 1080));
        store.Add(2400, 1080);

        Assert.True(new ScaledResolutionsStore(StorePath).Contains(2400, 1080));
    }

    [Fact]
    public void Default_path_is_under_local_app_data()
    {
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenShare", "display.json"),
            ScaledResolutionsStore.DefaultPath);
    }
}
```

- [ ] **Step 9: Rodar e ver falhar**

Run: `dotnet test host/ScreenShare.slnx --filter FullyQualifiedName~ScaledResolutionsStoreTests`
Expected: erro de compilação, `ScaledResolutionsStore` não existe.

- [ ] **Step 10: Implementar o `ScaledResolutionsStore`**

`host/ScreenShare.Display/ScaledResolutionsStore.cs`:

```csharp
using System.Text.Json;

namespace ScreenShare.Display;

/// <summary>
/// Lembra as resoluções que já receberam a escala automática, para não sobrescrever a escala que o usuário escolher
/// depois em Configurações › Tela. Arquivo ilegível conta como vazio: no pior caso a escala automática é aplicada
/// de novo uma vez, o que nunca impede a conexão.
/// </summary>
public sealed class ScaledResolutionsStore(string path)
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenShare", "display.json");

    private sealed record Contents(List<string>? Scaled);

    public bool Contains(int width, int height) => Load().Contains(Key(width, height));

    public void Add(int width, int height)
    {
        var scaled = Load();
        if (!scaled.Add(Key(width, height))) return;
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(new Contents([.. scaled.Order(StringComparer.Ordinal)]),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
    }

    private static string Key(int width, int height) => $"{width}x{height}";

    private HashSet<string> Load()
    {
        try
        {
            var contents = JsonSerializer.Deserialize<Contents>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return new HashSet<string>(contents?.Scaled ?? [], StringComparer.Ordinal);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // FileNotFound e DirectoryNotFound são IOException: sem arquivo, nada foi escalado ainda.
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }
}
```

Run: `dotnet test host/ScreenShare.slnx --filter FullyQualifiedName~ScaledResolutionsStoreTests`
Expected: PASS (8 casos).

- [ ] **Step 11: Rodar tudo**

Run: `dotnet build host/ScreenShare.slnx` e depois `dotnet test host/ScreenShare.slnx`
Expected: 0 avisos; 99 + 11 + 10 + 8 = 128 testes passando.

- [ ] **Step 12: Commit e push**

```bash
git add host/ScreenShare.Display host/ScreenShare.slnx host/ScreenShare.Tests/ScreenShare.Tests.csproj host/ScreenShare.Tests/Display
git commit -m "feat(display): projeto ScreenShare.Display com XML do VDD, escala e resoluções já escaladas

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

## Tasks posteriores ao spike (escritas depois da Task 1)

O escopo está fixado aqui. O código de cada uma depende do mecanismo que o spike escolher.

| Task | Escopo | Testes |
|---|---|---|
| 3 | **Controle do driver** (`IVirtualDisplayDriver`): ligar, desligar e garantir modo pelo mecanismo escolhido (pipe ou `detach`/`attach`), incluindo o `VddPipeClient` se o pipe for usado | Unitários com pipe falso ou API falsa; integração manual (10 ciclos) com o driver instalado |
| 4 | **Instalação** (`DriverDetector`, `DriverInstaller`, modos `install-driver`/`uninstall-driver` no `Program.cs`): download, SHA-256, SetupAPI, XML com permissões e, se o spike exigir, a reinicialização do dispositivo para resoluções novas | Unitários do SHA-256 e da idempotência; manual: instalar e desinstalar com UAC |
| 5 | **Topologia** (`IDisplayTopology`/`DisplayTopology`): achar a saída do VDD, esperar até 5 s, aplicar modo e escala, ler nome, posição, tamanho e escala; `VirtualMonitor` | Integração manual com o driver instalado |
| 6 | **`VirtualMonitorManager`**: limites e arredondamento para par, contagem de referências, desligamento em 10 s, reconexão, troca de resolução, escala só na primeira vez, falhas resultam em "sem monitor", limpeza ao iniciar, `Dispose` | Unitários com falsos e `FakeTimeProvider` (`Microsoft.Extensions.TimeProvider.Testing` 10.10.0) |
| 7 | **DevHost**: `net10.0-windows`; pergunta e instalação ao iniciar; `--sem-monitor`; `HostServer` com `IVirtualMonitorManager` (`CONFIG` com a resolução aplicada, lease liberado no `finally`); Ctrl+C desliga | `HostServerTests` com gerenciador falso |
| 8 | **Documentação e verificação manual**: README, `guia-do-codigo.md`, Parte 2 marcada no roteiro; a lista de verificação manual do spec | Manual no PC do usuário |
