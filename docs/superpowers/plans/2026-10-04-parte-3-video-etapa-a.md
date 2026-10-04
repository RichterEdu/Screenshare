# Parte 3, etapa A: vídeo funcionando — plano de implementação

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** O conteúdo do monitor virtual aparece no celular, em tela cheia e nítido, pelo Wi-Fi e pelo cabo, com latência medida no overlay.

**Architecture:**
- **Lógica pura** no `ScreenShare.Core` (Annex-B, relógio, fila e escritor da sessão) e na pasta `Pipeline/` do projeto novo `ScreenShare.Video` (ritmo, keyframes, recaptura). É testada com falsos.
- **Hardware** atrás de interfaces: DXGI Desktop Duplication, Media Foundation e `MediaCodec`, verificados à mão e com testes opcionais de GPU.
- **Rede:** o vídeo usa a sessão TLS que já existe, com um escritor único.

**Tech Stack:** C#/.NET 10, xUnit, Vortice.Windows 3.8.3 (`Vortice.Direct3D11`, `Vortice.DXGI`, `Vortice.MediaFoundation`); Kotlin, Compose, `MediaCodec`, JUnit 4.

**Spec:** `docs/superpowers/specs/2026-10-04-parte-3-video-design.md`

## Estrutura deste plano

O spike (Task 1) decide coisas que mudam o código de captura, encoder e decoder: a API de captura, os modos de bitrate aceitos e os decoders do celular. Por isso:
- **agora:** Task 1 (spike) e as Tasks 2 e 3 (lógica pura, que vale qualquer que seja o resultado);
- **depois do spike:** as Tasks 4 a 14 são escritas com os fatos reais e revisadas pelo usuário antes de executar. O escopo delas já está fixado em "Tasks posteriores ao spike", no fim.

## Global Constraints

- **Protocolo continua na v2.** Nenhum formato de mensagem muda; só regras de uso (`docs/protocol.md`).
- **Taxa:** até 60 fps por padrão (`--fps`, 1–120). Quadros só quando a tela muda.
- **Codec:** H.265 se o celular e o host tiverem; senão H.264. `CONFIG.codec` é exatamente 1 ou 2.
- **Bitrate inicial:** USB 50 Mbps (pico 100); Wi-Fi 25 Mbps (pico 50). O spike calibra, e `--bitrate` sobrescreve.
- **Fila de envio:** no máximo 2 FRAMEs pendentes. Passou disso, descarta P-frames até um IDR e pede IDR (`KeyframeNeeded`).
- **Escrita no stream da sessão só pelo `SessionWriter`.** Controle (`PONG`, `PING`, `CONFIG` de fallback) sai antes do vídeo e nunca é descartado.
- **Relógio do PC:** `PcClock` (QPC em µs). É o mesmo valor do `FRAME.timestampUs` e do `PING` do PC, e o `PING` é carimbado na hora de escrever.
- **Annex-B:** start codes de 3 ou 4 bytes. Keyframe: H.264 tipo 5; H.265 tipos 19, 20 e 21. Parâmetros: H.264 SPS 7 e PPS 8; H.265 VPS 32, SPS 33 e PPS 34. `codecConfig` = os parâmetros, cada um com start code de 4 bytes.
- **Projetos:**
  - `ScreenShare.Core` continua `net10.0`, puro, sem dependência de Windows;
  - `ScreenShare.Video` (novo) é `net10.0-windows`;
  - testes de GPU só com `SCREENSHARE_GPU_TESTS=1`, e testes de VDD só com `SCREENSHARE_VDD_TESTS=1`.
- **Texto e build:**
  - mensagens ao usuário e comentários de código em pt-BR;
  - `dotnet build host/ScreenShare.slnx` com 0 avisos, `dotnet test host/ScreenShare.slnx` verde, e no Android `.\android\gradlew.bat -p android :app:testDebugUnitTest` verde.
- **Commits:** cada commit termina com o `Co-Authored-By` do modelo que o escreveu, e cada task termina com `git push`.

## Review Focus

1. **Celular para de ler (Wi-Fi travou)** — o PC não pode acumular atraso nem memória. O esperado é ficar com no máximo 2 quadros na fila, descartar até um IDR e pedir IDR. *Testes:* Task 3 (`Too_many_pending_frames_drop_until_keyframe_and_ask_for_one`) e Task 4 (cliente que para de ler).
2. **Escritas simultâneas** da thread do encoder e do laço de leitura (`PONG`) no mesmo `SslStream` — o esperado é nunca sobrepor. *Teste:* Task 3 (`Concurrent_producers_never_overlap_writes_and_keep_the_stream_decodable`, com um stream que falha como o `SslStream`).
3. **Annex-B real do encoder** (start code de 3 bytes, AUD, SEI, zeros no fim) — o parser não pode errar o tipo nem os parâmetros. *Testes:* Task 2 (casos sintéticos + IDRs reais do spike) e Task 11 (mesmos vetores no Kotlin).
4. **Tela parada + pedido de keyframe** (decoder recriado, KEYFRAME_REQ) — o esperado é chegar um IDR mesmo sem mudança na tela. *Teste:* Task 6 (pipeline recodifica a última imagem).
5. **Resolução muda no meio da sessão** (reinício do driver, nome `\\.\DISPLAYn` novo) — o esperado é `CONFIG` novo + IDR, e o celular recria o decoder. *Testes:* Task 5 (`Changed` + `Refresh`), Task 6 (encoder novo + `CONFIG`) e Task 11 (`CONFIG` diferente recria).

---

### Task 1: spike de captura, encoder e decoder (código descartável)

**Quem executa:** o controlador, na sessão principal, com o usuário (UAC, Win+L, celular no cabo). Não vai para um subagente.

**Files:**
- Create, fora do repositório e descartável: `<scratchpad>/video-spike/VideoSpike.csproj` + `Program.cs`. Console `net10.0-windows`, `AllowUnsafeBlocks`, pacotes `Vortice.Direct3D11`, `Vortice.DXGI` e `Vortice.MediaFoundation` na versão 3.8.3.
- Create: `docs/protocol-vectors/annexb/h264-idr.hex` e `docs/protocol-vectors/annexb/h265-idr.hex`. São IDRs reais de 256×144 (pequenos), no formato dos outros vetores (pares hex, `#` comenta).
- Modify: `docs/superpowers/specs/2026-10-04-parte-3-video-design.md` (seção nova "Resultados do spike").

**Interfaces:**
- Consumes: o monitor virtual da Parte 2. O spike liga o monitor com `ChangeDisplaySettingsEx`, como o `DisplayTopology`, ou pelo próprio DevHost conectado.
- Produces: os vetores `annexb/*.hex` (a Task 2 usa) e as decisões registradas no spec (as Tasks 6 a 8 e 11 a 12 usam).

**Subcomandos do console:**

| Subcomando | O que faz | Responde |
|---|---|---|
| `outputs` | `CreateDXGIFactory1` → `EnumAdapters1` × `EnumOutputs`: descrição do adaptador, LUID, `DeviceName`, `AttachedToDesktop` e área | S1 |
| `dupl <\\.\DISPLAYn> [--dpi thread\|process] [--segundos N]` | Device D3D11 no adaptador da saída (`BgraSupport \| VideoSupport`), `IDXGIOutput5.DuplicateOutput1(BGRA)` (alternativa: `DuplicateOutput`). Por N segundos: `AcquireNextFrame(100 ms)`, `CopyResource` para uma textura própria, `ReleaseFrame`. Imprime quadros/s, p50/p95 de acquire+copy, `DesktopImageInSystemMemory`, `LastPresentTime` vs QPC, e todo HRESULT de erro, recriando a duplicação depois de ACCESS_LOST | S1, S2 |
| `wgc <\\.\DISPLAYn>` (só se S2 falhar) | Windows.Graphics.Capture para o HMONITOR: `IsBorderRequired = false`, `IsCursorCaptureEnabled` e quadros/s | S3 |
| `encoders` | `MFTEnumEx(VideoEncoder, HARDWARE \| SORTANDFILTER)` para HEVC e H.264: nome, fornecedor e LUID, tipos de entrada aceitos e `ICodecAPI.IsSupported` de: `AVLowLatencyMode`, `AVEncMPVDefaultBPictureCount`, `AVEncMPVGOPSize`, `AVEncCommonRateControlMode` (0, 1, 3, 4), `AVEncCommonMeanBitRate`, `AVEncCommonMaxBitRate`, `AVEncCommonBufferSize`, `AVEncCommonQualityVsSpeed`, `AVEncVideoMaxQP`, `AVEncVideoForceKeyFrame` | S4 |
| `encode <h264\|h265> <w> <h> <quadros> [--modo cbr\|pcvbr\|ldvbr\|quality] [--mbps N]` | Quadros sintéticos com texto e rolagem (`ClearRenderTargetView` + faixas de cor), `ID3D11VideoProcessor` BGRA → NV12, pool `MFCreateVideoSampleAllocatorEx` e MFT assíncrono (`MF_TRANSFORM_ASYNC_UNLOCK`, `MF_LOW_LATENCY`). IDR forçado no quadro 10. Grava `saida.h26x` em Annex-B e imprime: tipos de NAL de cada saída, `CleanPoint`, se o IDR forçado saiu no mesmo quadro, p50/p95 da entrada até a saída, tamanho do IDR, de um P de rolagem e de um P sem mudança | S5, S6 |
| `capture-encode <\\.\DISPLAYn> <codec> <segundos>` | `dupl` + `encode` juntos no monitor virtual real, com p50/p95 da captura até a saída | S6 |
| `vetores` | `encode` de 256×144 com um quadro em H.264 e em H.265 → `annexb/h264-idr.hex` e `h265-idr.hex` | vetores |

**Celular (S7)**, com o aparelho no cabo e o `adb` do Android Studio:
- `adb shell wm size`;
- `adb shell dumpsys display | findstr mBaseDisplayInfo`;
- `adb shell dumpsys window displays | findstr "cutout"`;
- os decoders de `/vendor/etc/media_codecs*.xml` (nomes H.264/H.265 e `<Feature name="low-latency"/>`);
- `adb shell getprop ro.build.version.sdk`.

- [ ] **Step 1: Escrever e compilar o console** com os subcomandos acima. Rodar `outputs` com o monitor virtual desligado (estado atual).

- [ ] **Step 2: Ligar o monitor virtual e rodar os experimentos.** Os passos com interação são pedidos ao usuário um de cada vez:

| # | Experimento | O que anotar |
|---|---|---|
| E1 | `outputs` com o monitor virtual ligado (DevHost rodando + celular conectado, ou anexando à mão) | Adaptador e LUID da saída do VDD; se é a NVIDIA |
| E2 | `dupl` no VDD, 10 s, com uma janela rolando nele (`--dpi thread` e depois `--dpi process`) | Funciona? Quadros/s, p50/p95, `DesktopImageInSystemMemory` |
| E3 | `dupl` por 60 s enquanto o usuário faz Win+L e volta, abre um UAC e troca a resolução do monitor virtual em Configurações | Os HRESULTs e se a recriação volta a dar quadros |
| E4 | `encoders` | Lista de MFTs e de ajustes suportados |
| E5 | `encode h265 2520 1080 300` em cada modo de bitrate, a 25 e a 50 Mbps; repetir em H.264 | Latência, tamanhos, NALs, `CleanPoint`, IDR forçado; o arquivo toca no ffplay (`ffplay -f hevc saida.h265`) |
| E6 | `capture-encode` no VDD por 30 s com rolagem de texto | p50/p95 de ponta a ponta no PC |
| E7 | `vetores` | Os dois arquivos `.hex` |
| E8 | S7 no celular | Tamanho do painel, recorte, decoders, `low-latency`, versão do Android |
| E9 | Só se E2 falhar: `wgc` | Borda, cursor, quadros/s |

- [ ] **Step 3: Decidir e registrar** em "Resultados do spike" no spec:
  - **API de captura:** DXGI ou WGC, e DPI só na thread ou no processo inteiro;
  - **adaptador do encoder:** e se é preciso fixar a GPU no `vdd_settings.xml`;
  - **modo de bitrate padrão e números padrão:** USB e Wi-Fi;
  - se `MaxQP` existe;
  - se o IDR forçado vale para o mesmo quadro;
  - se o encoder entrega 1 saída por entrada (ou guarda quadros);
  - **o que o HELLO deve mandar:** largura × altura do painel;
  - **nomes dos decoders e chaves de baixa latência** que o celular aceita.

- [ ] **Step 4: Commit e push** dos vetores e do spec.

```bash
git add docs/protocol-vectors/annexb docs/superpowers/specs/2026-10-04-parte-3-video-design.md
git commit -m "docs: resultados do spike de vídeo e IDRs reais para os testes"
git push
```

---

### Task 2: protocolo de vídeo, `AnnexB` e `PcClock`

**Files:**
- Modify: `docs/protocol.md`
- Create: `host/ScreenShare.Core/Video/AnnexB.cs`
- Create: `host/ScreenShare.Core/Video/PcClock.cs`
- Modify: `host/ScreenShare.Tests/ScreenShare.Tests.csproj` (copiar `docs/protocol-vectors/annexb/*.hex`)
- Test: `host/ScreenShare.Tests/Video/AnnexBTests.cs`, `host/ScreenShare.Tests/Video/PcClockTests.cs`

**Interfaces:**
- Consumes: `ScreenShare.Core.Protocol.VideoCodec` (`H264 = 1`, `H265 = 2`); `ScreenShare.Tests.Protocol.Vectors.Load(string name)` (lê `bin/.../protocol-vectors/<name>`); os vetores `annexb/*.hex` da Task 1.
- Produces:
  - `ScreenShare.Core.Video.NalUnit(int Offset, int Length, int Type)` (`readonly record struct`).
  - `ScreenShare.Core.Video.AnnexB`:
    - constantes `H264Idr`, `H264Sps`, `H264Pps`, `H265IdrWRadl`, `H265IdrNLp`, `H265Cra`, `H265Vps`, `H265Sps`, `H265Pps`;
    - `IReadOnlyList<NalUnit> Split(ReadOnlySpan<byte> accessUnit, VideoCodec codec)`;
    - `int NalType(byte header, VideoCodec codec)`;
    - `bool IsKeyframe(ReadOnlySpan<byte> accessUnit, VideoCodec codec)`;
    - `byte[]? ExtractParameterSets(ReadOnlySpan<byte> accessUnit, VideoCodec codec)`.
  - `ScreenShare.Core.Video.PcClock`: `ulong NowUs { get; }` e `ulong ToMicroseconds(long ticks, long frequency)`.

- [ ] **Step 1: Regras de vídeo no `docs/protocol.md`**

1. Troque a linha 29 (`Depois do `AUTH`, 10 s sem nenhuma mensagem...`) por:

```markdown
Depois do `AUTH`, 10 s sem nenhuma mensagem do celular fecham a conexão (o app envia `PING` a cada segundo). Depois do `CONFIG`, o PC também envia um `PING` a cada segundo (ver "Vídeo").
```

2. Troque a linha 31 (`Depois do `CONFIG`: o PC envia `FRAME`s...`) por:

```markdown
Depois do `CONFIG`: o PC envia `FRAME`s (o primeiro é keyframe) e, a qualquer momento, vêm `TOUCH`, `PING`/`PONG`, `KEYFRAME_REQ` e, se o vídeo mudar (resolução, encoder, codec), um `CONFIG` novo. As regras estão em "Vídeo".
```

3. Na seção `### PING / PONG`, acrescente depois da linha que já existe:

```markdown
O PING que o **PC** envia (a cada segundo depois do `CONFIG`) leva o relógio do PC em µs: monotônico e o mesmo relógio do `FRAME.timestampUs`, lido no instante em que o PING é escrito. O celular **deve** responder cada PING do PC com um PONG de mesmo valor, sem demora; com isso e com o próprio RTT, ele converte o horário de captura dos quadros para o próprio relógio e mede a latência de ponta a ponta.
```

4. Logo antes de `## Descoberta (Wi-Fi)`, acrescente:

```markdown
## Vídeo
- **CONFIG:** o primeiro pode chegar até cerca de 2 s depois do `HELLO` (o PC espera o primeiro keyframe para tirar os parâmetros dele). Pode ser reenviado no meio da sessão, quando a resolução, o encoder ou o codec mudam. **Depois de qualquer `CONFIG`, o próximo `FRAME` é keyframe**, e nenhum quadro do stream anterior é enviado depois dele. Um `CONFIG` com o mesmo conteúdo do anterior permite manter o decoder.
- **codecConfig:** quando não vazio, contém só os parâmetros (H.264: SPS e PPS; H.265: VPS, SPS e PPS), cada um com start code de 4 bytes. Eles também se repetem dentro de cada keyframe.
- **FRAME:** um access unit por mensagem. Keyframe = IDR (H.264: NAL tipo 5; H.265: tipos 19, 20 ou 21) precedido dos parâmetros. Sem B-frames: a ordem de decodificação é a de exibição. Timestamps estritamente crescentes dentro de um stream. NALs de AUD e SEI podem aparecer e são ignoráveis.
- **Fluxo:** quadros só quando a tela muda, mais alguns refinamentos logo depois que ela para; ficar sem quadros é normal. Com congestionamento o PC pode pular quadros, mas o próximo que chega depois de um pulo é keyframe. `KEYFRAME_REQ` é atendido o quanto antes, mesmo com a tela parada; pedidos próximos são juntados.
```

5. Na tabela de "Vetores de teste", acrescente as linhas:

```markdown
| annexb/h264-idr.hex | access unit IDR real de H.264, 256×144, gerado pelo encoder do PC (só os dados do vídeo, sem cabeçalho de mensagem) |
| annexb/h265-idr.hex | access unit IDR real de H.265, 256×144 (idem) |
```

- [ ] **Step 2: Copiar os vetores novos para os testes**

Em `host/ScreenShare.Tests/ScreenShare.Tests.csproj`, logo depois da linha `<None Include="..\..\docs\protocol-vectors\*.txt" ...>`, acrescente:

```xml
    <None Include="..\..\docs\protocol-vectors\annexb\*.hex" LinkBase="protocol-vectors\annexb" CopyToOutputDirectory="PreserveNewest" />
```

- [ ] **Step 3: Escrever os testes do `AnnexB`**

`host/ScreenShare.Tests/Video/AnnexBTests.cs`:

```csharp
using ScreenShare.Core.Protocol;
using ScreenShare.Core.Video;
using ScreenShare.Tests.Protocol;

namespace ScreenShare.Tests.Video;

public sealed class AnnexBTests
{
    // H.265: VPS, SPS e PPS com start code de 4 bytes; o IDR com start code de 3 bytes.
    private static readonly byte[] H265Idr =
    [
        0, 0, 0, 1, 0x40, 0x01, 0x0C,     // VPS (32)
        0, 0, 0, 1, 0x42, 0x01, 0x01,     // SPS (33)
        0, 0, 0, 1, 0x44, 0x01, 0xC0,     // PPS (34)
        0, 0, 1, 0x26, 0x01, 0xAF, 0x05,  // IDR_W_RADL (19)
    ];

    // H.264 como os encoders costumam entregar: AUD, SPS, PPS, SEI e o IDR.
    private static readonly byte[] H264IdrWithAudAndSei =
    [
        0, 0, 0, 1, 0x09, 0xF0,              // AUD (9)
        0, 0, 0, 1, 0x67, 0x42, 0x00, 0x1F,  // SPS (7)
        0, 0, 0, 1, 0x68, 0xCE, 0x3C, 0x80,  // PPS (8)
        0, 0, 0, 1, 0x06, 0x05, 0x01,        // SEI (6)
        0, 0, 0, 1, 0x65, 0x88, 0x84,        // IDR (5)
    ];

    [Fact]
    public void Split_finds_nal_units_with_3_and_4_byte_start_codes()
    {
        var units = AnnexB.Split(H265Idr, VideoCodec.H265);

        Assert.Equal(new[] { 32, 33, 34, 19 }, units.Select(u => u.Type));
        Assert.Equal(new NalUnit(4, 3, 32), units[0]);
        Assert.Equal(new NalUnit(11, 3, 33), units[1]);
        Assert.Equal(new NalUnit(18, 3, 34), units[2]);
        Assert.Equal(new NalUnit(24, 4, 19), units[3]);
    }

    [Fact]
    public void Split_ignores_bytes_before_the_first_start_code()
    {
        var units = AnnexB.Split(new byte[] { 0xFF, 0xEE, 0, 0, 1, 0x65, 0x88 }, VideoCodec.H264);

        Assert.Equal(new[] { new NalUnit(5, 2, 5) }, units);
    }

    [Fact]
    public void Split_drops_the_zeros_that_precede_a_start_code()
    {
        // O zero extra do start code de 4 bytes e os trailing_zero_8bits não fazem parte da NAL.
        byte[] data = [0, 0, 1, 0x09, 0xF0, 0x00, 0x00, 0, 0, 0, 1, 0x65, 0x88, 0x00];

        var units = AnnexB.Split(data, VideoCodec.H264);

        Assert.Equal(new[] { new NalUnit(3, 2, 9), new NalUnit(11, 2, 5) }, units);
    }

    [Fact]
    public void Emulation_prevention_bytes_do_not_split_a_nal_unit()
    {
        byte[] data = [0, 0, 0, 1, 0x65, 0x00, 0x00, 0x03, 0x01, 0x42];

        var units = AnnexB.Split(data, VideoCodec.H264);

        Assert.Equal(new[] { new NalUnit(4, 6, 5) }, units);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x65, 0x88, 0x84 })]
    public void Data_without_start_code_has_no_nal_units(byte[] data)
    {
        Assert.Empty(AnnexB.Split(data, VideoCodec.H264));
    }

    [Theory]
    [InlineData(VideoCodec.None)]
    [InlineData(VideoCodec.H264 | VideoCodec.H265)]
    public void Codec_must_be_exactly_one(VideoCodec codec)
    {
        Assert.Throws<ArgumentException>(() => AnnexB.Split(H265Idr, codec));
    }

    [Theory]
    [InlineData(VideoCodec.H264, (byte)0x65, true)]   // IDR
    [InlineData(VideoCodec.H264, (byte)0x41, false)]  // P (tipo 1)
    [InlineData(VideoCodec.H265, (byte)0x26, true)]   // IDR_W_RADL (19)
    [InlineData(VideoCodec.H265, (byte)0x28, true)]   // IDR_N_LP (20)
    [InlineData(VideoCodec.H265, (byte)0x2A, true)]   // CRA (21)
    [InlineData(VideoCodec.H265, (byte)0x02, false)]  // TRAIL_R (1)
    [InlineData(VideoCodec.H265, (byte)0x40, false)]  // VPS sozinho
    public void IsKeyframe_looks_at_the_nal_types(VideoCodec codec, byte header, bool expected)
    {
        byte[] accessUnit = [0, 0, 0, 1, header, 0x01, 0x80];

        Assert.Equal(expected, AnnexB.IsKeyframe(accessUnit, codec));
    }

    [Fact]
    public void Frame_vector_data_is_an_h265_keyframe()
    {
        var data = Vectors.Load("frame.hex")[14..]; // cabeçalho do FRAME (vira MessageCodec.FrameHeaderSize na Task 3)

        Assert.True(AnnexB.IsKeyframe(data, VideoCodec.H265));
    }

    [Fact]
    public void Config_vector_codec_config_is_an_h265_vps()
    {
        var payload = Vectors.Load("config.hex")[MessageCodec.HeaderSize..];
        var codecConfig = payload[11..];

        Assert.Equal(new[] { AnnexB.H265Vps }, AnnexB.Split(codecConfig, VideoCodec.H265).Select(u => u.Type));
    }

    [Fact]
    public void ExtractParameterSets_returns_vps_sps_pps_with_4_byte_start_codes()
    {
        var sets = AnnexB.ExtractParameterSets(H265Idr, VideoCodec.H265);

        Assert.Equal(new byte[]
        {
            0, 0, 0, 1, 0x40, 0x01, 0x0C,
            0, 0, 0, 1, 0x42, 0x01, 0x01,
            0, 0, 0, 1, 0x44, 0x01, 0xC0,
        }, sets);
    }

    [Fact]
    public void ExtractParameterSets_is_null_when_a_set_is_missing()
    {
        var withoutPps = H265Idr[..14].Concat(H265Idr[21..]).ToArray();

        Assert.Null(AnnexB.ExtractParameterSets(withoutPps, VideoCodec.H265));
    }

    [Fact]
    public void H264_parameter_sets_ignore_aud_and_sei()
    {
        Assert.True(AnnexB.IsKeyframe(H264IdrWithAudAndSei, VideoCodec.H264));
        Assert.Equal(new byte[]
        {
            0, 0, 0, 1, 0x67, 0x42, 0x00, 0x1F,
            0, 0, 0, 1, 0x68, 0xCE, 0x3C, 0x80,
        }, AnnexB.ExtractParameterSets(H264IdrWithAudAndSei, VideoCodec.H264));
    }

    [Theory]
    [InlineData("annexb/h264-idr.hex", VideoCodec.H264)]
    [InlineData("annexb/h265-idr.hex", VideoCodec.H265)]
    public void Real_encoder_idr_from_the_spike_is_a_keyframe_with_parameter_sets(string vector, VideoCodec codec)
    {
        var accessUnit = Vectors.Load(vector);

        Assert.True(AnnexB.IsKeyframe(accessUnit, codec));
        var sets = AnnexB.ExtractParameterSets(accessUnit, codec);
        Assert.NotNull(sets);
        Assert.Equal(new byte[] { 0, 0, 0, 1 }, sets[..4]);
    }
}
```

`Frame_vector_data_is_an_h265_keyframe` usa `14` porque a constante `MessageCodec.FrameHeaderSize` só nasce na Task 3, que troca o número por ela.

- [ ] **Step 4: Rodar e ver falhar**

Run: `dotnet test host/ScreenShare.slnx --filter FullyQualifiedName~AnnexBTests`
Expected: erro de compilação, `ScreenShare.Core.Video` não existe.

- [ ] **Step 5: Implementar o `AnnexB`**

`host/ScreenShare.Core/Video/AnnexB.cs`:

```csharp
using ScreenShare.Core.Protocol;

namespace ScreenShare.Core.Video;

/// <summary>Uma NAL unit dentro de um access unit: onde começa o conteúdo (sem o start code), o tamanho e o tipo.</summary>
public readonly record struct NalUnit(int Offset, int Length, int Type);

/// <summary>
/// Leitura de vídeo H.264/H.265 em Annex-B (NAL units separadas por 00 00 01 ou 00 00 00 01), o formato que o
/// encoder entrega e que o FRAME carrega.
/// </summary>
public static class AnnexB
{
    public const int H264Idr = 5;
    public const int H264Sps = 7;
    public const int H264Pps = 8;
    public const int H265IdrWRadl = 19;
    public const int H265IdrNLp = 20;
    public const int H265Cra = 21;
    public const int H265Vps = 32;
    public const int H265Sps = 33;
    public const int H265Pps = 34;

    private static readonly byte[] StartCode = [0, 0, 0, 1];

    /// <summary>As NAL units do access unit, na ordem. Bytes antes do primeiro start code são ignorados.</summary>
    public static IReadOnlyList<NalUnit> Split(ReadOnlySpan<byte> accessUnit, VideoCodec codec)
    {
        RequireSingleCodec(codec);
        var units = new List<NalUnit>();
        var start = -1;
        var i = 0;
        while (i + 2 < accessUnit.Length)
        {
            if (accessUnit[i] == 0 && accessUnit[i + 1] == 0 && accessUnit[i + 2] == 1)
            {
                if (start >= 0) Add(units, accessUnit, start, i, codec);
                start = i + 3;
                i += 3;
            }
            else
            {
                i++;
            }
        }
        if (start >= 0) Add(units, accessUnit, start, accessUnit.Length, codec);
        return units;
    }

    /// <summary>O tipo da NAL a partir do primeiro byte do cabeçalho (H.264: 5 bits baixos; H.265: bits 1 a 6).</summary>
    public static int NalType(byte header, VideoCodec codec) =>
        RequireSingleCodec(codec) == VideoCodec.H264 ? header & 0x1F : (header >> 1) & 0x3F;

    /// <summary>O access unit tem um IDR (H.264) ou um IDR/CRA (H.265), por onde um decoder pode começar.</summary>
    public static bool IsKeyframe(ReadOnlySpan<byte> accessUnit, VideoCodec codec)
    {
        foreach (var nal in Split(accessUnit, codec))
        {
            if (codec == VideoCodec.H264 ? nal.Type == H264Idr : nal.Type is H265IdrWRadl or H265IdrNLp or H265Cra)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Os parâmetros do access unit (H.264: SPS e PPS; H.265: VPS, SPS e PPS), cada um com start code de 4 bytes,
    /// na ordem em que aparecem — o que vai no codecConfig do CONFIG. Null se faltar algum.
    /// </summary>
    public static byte[]? ExtractParameterSets(ReadOnlySpan<byte> accessUnit, VideoCodec codec)
    {
        int[] required = codec == VideoCodec.H264 ? [H264Sps, H264Pps] : [H265Vps, H265Sps, H265Pps];
        var found = new HashSet<int>();
        using var output = new MemoryStream();
        foreach (var nal in Split(accessUnit, codec))
        {
            if (Array.IndexOf(required, nal.Type) < 0) continue;
            found.Add(nal.Type);
            output.Write(StartCode);
            output.Write(accessUnit.Slice(nal.Offset, nal.Length));
        }
        return found.Count == required.Length ? output.ToArray() : null;
    }

    private static void Add(List<NalUnit> units, ReadOnlySpan<byte> data, int start, int end, VideoCodec codec)
    {
        // Uma NAL nunca termina em 0x00: os zeros antes de um start code são o zero extra do start code de 4 bytes
        // ou trailing_zero_8bits.
        while (end > start && data[end - 1] == 0) end--;
        if (end > start) units.Add(new NalUnit(start, end - start, NalType(data[start], codec)));
    }

    private static VideoCodec RequireSingleCodec(VideoCodec codec) =>
        codec is VideoCodec.H264 or VideoCodec.H265
            ? codec
            : throw new ArgumentException($"O codec precisa ser H264 ou H265, recebeu {codec}.", nameof(codec));
}
```

Run: `dotnet test host/ScreenShare.slnx --filter FullyQualifiedName~AnnexBTests`
Expected: PASS (22 casos).

- [ ] **Step 6: Escrever os testes do `PcClock`**

`host/ScreenShare.Tests/Video/PcClockTests.cs`:

```csharp
using ScreenShare.Core.Video;

namespace ScreenShare.Tests.Video;

public sealed class PcClockTests
{
    [Theory]
    [InlineData(10_000_000L, 10_000_000L, 1_000_000UL)]
    [InlineData(15L, 10_000_000L, 1UL)]                       // 1,5 µs arredonda para baixo
    [InlineData(0L, 10_000_000L, 0UL)]
    [InlineData(3_000_000_000_000_000L, 10_000_000L, 300_000_000_000_000UL)] // anos de QPC sem estourar
    public void ToMicroseconds_converts_qpc_ticks(long ticks, long frequency, ulong expected)
    {
        Assert.Equal(expected, PcClock.ToMicroseconds(ticks, frequency));
    }

    [Theory]
    [InlineData(-1L, 10_000_000L)]
    [InlineData(10L, 0L)]
    public void ToMicroseconds_rejects_invalid_input(long ticks, long frequency)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PcClock.ToMicroseconds(ticks, frequency));
    }

    [Fact]
    public void NowUs_never_goes_backwards()
    {
        var previous = PcClock.NowUs;
        for (var i = 0; i < 10_000; i++)
        {
            var now = PcClock.NowUs;
            Assert.True(now >= previous);
            previous = now;
        }
    }
}
```

Run: `dotnet test host/ScreenShare.slnx --filter FullyQualifiedName~PcClockTests`
Expected: erro de compilação, `PcClock` não existe.

- [ ] **Step 7: Implementar o `PcClock`**

`host/ScreenShare.Core/Video/PcClock.cs`:

```csharp
using System.Diagnostics;

namespace ScreenShare.Core.Video;

/// <summary>
/// O relógio do PC em microssegundos, a partir do QPC (o mesmo relógio do LastPresentTime do DXGI). É o valor do
/// FRAME.timestampUs e do PING que o PC envia; o celular usa os dois para medir a latência de ponta a ponta.
/// </summary>
public static class PcClock
{
    public static ulong NowUs => ToMicroseconds(Stopwatch.GetTimestamp(), Stopwatch.Frequency);

    /// <summary>Converte ticks do QPC em µs sem estourar 64 bits (ticks × 1 000 000 estoura depois de uns 10 dias).</summary>
    public static ulong ToMicroseconds(long ticks, long frequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ticks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        var seconds = ticks / frequency;
        var remainder = ticks % frequency;
        return (ulong)seconds * 1_000_000UL + (ulong)(remainder * 1_000_000 / frequency);
    }
}
```

Run: `dotnet test host/ScreenShare.slnx --filter FullyQualifiedName~PcClockTests`
Expected: PASS (7 casos).

- [ ] **Step 8: Rodar tudo, commit e push**

Run: `dotnet build host/ScreenShare.slnx` e `dotnet test host/ScreenShare.slnx`
Expected: 0 avisos; 223 + 22 + 7 = 252 aprovados e 1 ignorado.

```bash
git add docs/protocol.md host/ScreenShare.Core/Video host/ScreenShare.Tests/Video host/ScreenShare.Tests/ScreenShare.Tests.csproj
git commit -m "feat(core): regras de vídeo no protocolo, leitura Annex-B e relógio do PC"
git push
```

---

### Task 3: `VideoSendQueue`, `SessionWriter` e cabeçalho de FRAME sem cópia

**Files:**
- Modify: `host/ScreenShare.Core/Protocol/MessageCodec.cs` (`FrameHeaderSize`, `WriteFrameHeader`)
- Create: `host/ScreenShare.Core/Protocol/VideoSendQueue.cs`
- Create: `host/ScreenShare.Core/Protocol/SessionWriter.cs`
- Modify: `host/ScreenShare.Tests/Video/AnnexBTests.cs` (trocar o `14` por `MessageCodec.FrameHeaderSize`)
- Test: `host/ScreenShare.Tests/Protocol/FrameHeaderTests.cs`, `host/ScreenShare.Tests/Protocol/VideoSendQueueTests.cs`, `host/ScreenShare.Tests/Protocol/SessionWriterTests.cs`

**Interfaces:**
- Consumes: `MessageCodec.Encode(Message)`, `MessageCodec.HeaderSize`, `MessageCodec.MaxPayloadLength`, `MessageReader(Stream).ReadAsync(CancellationToken)`, os records de `Messages.cs`, `ProtocolException`.
- Produces (a Task 4 usa estes nomes):
  - `MessageCodec.FrameHeaderSize` (= 14) e `MessageCodec.WriteFrameHeader(Span<byte> destination, ulong timestampUs, bool isKeyframe, int dataLength)`;
  - `enum FrameEnqueue { Queued, Dropped, DroppedNeedKeyframe }`;
  - `VideoSendQueue(int maxPendingFrames = 2)`:
    - `EnqueueConfig(ConfigMessage)`;
    - `FrameEnqueue EnqueueFrame(FrameMessage)`;
    - `bool TryDequeue(out Message? message)`;
    - `int PendingFrames`;
  - `SessionWriter(Stream stream, Func<ulong> clockUs, int maxPendingFrames = 2)`:
    - envio: `Send(Message)`, `SendPing()`, `SendVideoConfig(ConfigMessage)`, `SendVideoFrame(FrameMessage)`;
    - `event Action? KeyframeNeeded`;
    - `bool ConfigSent`, `int PendingFrames`;
    - `Task RunAsync(CancellationToken)`.

- [ ] **Step 1: Testes do cabeçalho de FRAME**

`host/ScreenShare.Tests/Protocol/FrameHeaderTests.cs`:

```csharp
using ScreenShare.Core.Protocol;

namespace ScreenShare.Tests.Protocol;

public sealed class FrameHeaderTests
{
    [Fact]
    public void Header_plus_data_is_the_frame_vector()
    {
        byte[] data = [0x00, 0x00, 0x00, 0x01, 0x26, 0x01, 0xAF];
        var header = new byte[MessageCodec.FrameHeaderSize];

        MessageCodec.WriteFrameHeader(header, 1_000_000, isKeyframe: true, data.Length);

        Assert.Equal(Vectors.Load("frame.hex"), header.Concat(data).ToArray());
    }

    [Fact]
    public void Payload_over_the_limit_is_rejected()
    {
        var header = new byte[MessageCodec.FrameHeaderSize];

        Assert.Throws<ProtocolException>(() =>
            MessageCodec.WriteFrameHeader(header, 0, false, MessageCodec.MaxPayloadLength - 8));
    }

    [Fact]
    public void Destination_too_small_is_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            MessageCodec.WriteFrameHeader(new byte[MessageCodec.FrameHeaderSize - 1], 0, false, 0));
    }
}
```

Run: `dotnet test host/ScreenShare.slnx --filter FullyQualifiedName~FrameHeaderTests`
Expected: erro de compilação, `FrameHeaderSize` não existe.

- [ ] **Step 2: Implementar `FrameHeaderSize` e `WriteFrameHeader`**

Em `host/ScreenShare.Core/Protocol/MessageCodec.cs`, depois de `public const int MaxDeviceNameBytes = 64;`, acrescente:

```csharp
    /// <summary>Cabeçalho completo de um FRAME: 5 do quadro + 8 do timestamp + 1 de flags.</summary>
    public const int FrameHeaderSize = HeaderSize + 9;
```

E, logo depois do método `Encode`, acrescente:

```csharp
    /// <summary>
    /// Escreve só o cabeçalho de um FRAME (tipo, tamanho, timestamp e flags), para os dados do vídeo irem direto para
    /// o stream sem serem copiados para um array novo.
    /// </summary>
    public static void WriteFrameHeader(Span<byte> destination, ulong timestampUs, bool isKeyframe, int dataLength)
    {
        if (destination.Length < FrameHeaderSize)
            throw new ArgumentException($"O cabeçalho do FRAME precisa de {FrameHeaderSize} bytes.", nameof(destination));
        ArgumentOutOfRangeException.ThrowIfNegative(dataLength);
        var payloadLength = 9L + dataLength;
        if (payloadLength > MaxPayloadLength)
            throw new ProtocolException($"Payload de {payloadLength} bytes excede o limite de {MaxPayloadLength}.");
        destination[0] = (byte)MessageType.Frame;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[1..], (uint)payloadLength);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[5..], timestampUs);
        destination[13] = isKeyframe ? (byte)1 : (byte)0;
    }
```

Em `host/ScreenShare.Tests/Video/AnnexBTests.cs`, troque `Vectors.Load("frame.hex")[14..]` (e o comentário da linha) por `Vectors.Load("frame.hex")[MessageCodec.FrameHeaderSize..]`.

Run: `dotnet test host/ScreenShare.slnx --filter "FullyQualifiedName~FrameHeaderTests|FullyQualifiedName~AnnexBTests"`
Expected: PASS.

- [ ] **Step 3: Testes da `VideoSendQueue`**

`host/ScreenShare.Tests/Protocol/VideoSendQueueTests.cs`:

```csharp
using ScreenShare.Core.Protocol;

namespace ScreenShare.Tests.Protocol;

public sealed class VideoSendQueueTests
{
    private static readonly ConfigMessage Config = new(2520, 1080, VideoCodec.H265, 50_000, [0, 0, 0, 1, 0x40]);

    private static FrameMessage Key(ulong ts) => new(ts, true, [0, 0, 0, 1, 0x26, 0x01]);

    private static FrameMessage P(ulong ts) => new(ts, false, [0, 0, 0, 1, 0x02, 0x01]);

    private static List<Message> Drain(VideoSendQueue queue)
    {
        var messages = new List<Message>();
        while (queue.TryDequeue(out var message)) messages.Add(message);
        return messages;
    }

    [Fact]
    public void Config_then_keyframe_then_frames_come_out_in_order()
    {
        var queue = new VideoSendQueue();
        queue.EnqueueConfig(Config);

        Assert.Equal(FrameEnqueue.Queued, queue.EnqueueFrame(Key(1)));
        Assert.Equal(FrameEnqueue.Queued, queue.EnqueueFrame(P(2)));

        Assert.Equal(new Message[] { Config, Key(1), P(2) }, Drain(queue), new MessageComparer());
    }

    [Fact]
    public void Frames_before_the_first_keyframe_of_a_stream_are_dropped()
    {
        var queue = new VideoSendQueue();
        queue.EnqueueConfig(Config);

        Assert.Equal(FrameEnqueue.Dropped, queue.EnqueueFrame(P(1)));
        Assert.Equal(0, queue.PendingFrames);
    }

    [Fact]
    public void Third_pending_frame_is_dropped_and_asks_for_a_keyframe_then_waits_for_one()
    {
        var queue = new VideoSendQueue(maxPendingFrames: 2);
        queue.EnqueueConfig(Config);
        queue.EnqueueFrame(Key(1));
        queue.EnqueueFrame(P(2));

        Assert.Equal(FrameEnqueue.DroppedNeedKeyframe, queue.EnqueueFrame(P(3)));
        Assert.Equal(FrameEnqueue.Dropped, queue.EnqueueFrame(P(4)));
        Assert.Equal(2, queue.PendingFrames);
        Assert.Equal(FrameEnqueue.Queued, queue.EnqueueFrame(Key(5)));
    }

    [Fact]
    public void Keyframe_replaces_the_frames_still_waiting()
    {
        var queue = new VideoSendQueue();
        queue.EnqueueConfig(Config);
        queue.EnqueueFrame(Key(1));
        queue.EnqueueFrame(P(2));

        queue.EnqueueFrame(Key(3));

        Assert.Equal(1, queue.PendingFrames);
        Assert.Equal(new Message[] { Config, Key(3) }, Drain(queue), new MessageComparer());
    }

    [Fact]
    public void New_config_discards_the_old_stream_and_its_config()
    {
        var queue = new VideoSendQueue();
        queue.EnqueueConfig(Config);
        queue.EnqueueFrame(Key(1));
        queue.EnqueueFrame(P(2));
        var newConfig = Config with { Width = 1920 };

        queue.EnqueueConfig(newConfig);

        Assert.Equal(FrameEnqueue.Dropped, queue.EnqueueFrame(P(3)));
        Assert.Equal(FrameEnqueue.Queued, queue.EnqueueFrame(Key(4)));
        Assert.Equal(new Message[] { newConfig, Key(4) }, Drain(queue), new MessageComparer());
    }

    [Fact]
    public void PendingFrames_counts_only_frames()
    {
        var queue = new VideoSendQueue();
        queue.EnqueueConfig(Config);
        queue.EnqueueFrame(Key(1));
        Assert.Equal(1, queue.PendingFrames);

        Assert.True(queue.TryDequeue(out var first));
        Assert.IsType<ConfigMessage>(first);
        Assert.Equal(1, queue.PendingFrames);

        Assert.True(queue.TryDequeue(out _));
        Assert.Equal(0, queue.PendingFrames);
        Assert.False(queue.TryDequeue(out _));
    }

    /// <summary>Records com arrays não comparam o conteúdo: compara pelos bytes codificados.</summary>
    private sealed class MessageComparer : IEqualityComparer<Message>
    {
        public bool Equals(Message? x, Message? y) =>
            x is not null && y is not null && MessageCodec.Encode(x).AsSpan().SequenceEqual(MessageCodec.Encode(y));

        public int GetHashCode(Message obj) => obj.GetType().GetHashCode();
    }
}
```

Run: `dotnet test host/ScreenShare.slnx --filter FullyQualifiedName~VideoSendQueueTests`
Expected: erro de compilação, `VideoSendQueue` não existe.

- [ ] **Step 4: Implementar a `VideoSendQueue`**

`host/ScreenShare.Core/Protocol/VideoSendQueue.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;

namespace ScreenShare.Core.Protocol;

/// <summary>O que aconteceu com um FRAME posto na fila.</summary>
public enum FrameEnqueue
{
    Queued,
    /// <summary>Descartado sem pedir nada: o stream já espera um keyframe.</summary>
    Dropped,
    /// <summary>Descartado porque a fila passou do limite: quem produz o vídeo precisa mandar um keyframe.</summary>
    DroppedNeedKeyframe,
}

/// <summary>
/// A fila de vídeo de uma sessão (CONFIG e FRAMEs), segura entre threads. Um P-frame não pode ser descartado sozinho
/// (o seguinte depende dele): ao passar do limite, a fila descarta os P-frames até o próximo keyframe e pede um.
/// Um keyframe torna obsoletos os quadros que ainda esperam; um CONFIG começa um stream novo.
/// </summary>
public sealed class VideoSendQueue(int maxPendingFrames = 2)
{
    private readonly Lock _gate = new();
    private readonly LinkedList<Message> _items = new();
    private int _pendingFrames;
    private bool _awaitingKeyframe;

    public int PendingFrames
    {
        get
        {
            lock (_gate) return _pendingFrames;
        }
    }

    /// <summary>Começa um stream novo: os quadros e o CONFIG do stream anterior que ainda não saíram são descartados.</summary>
    public void EnqueueConfig(ConfigMessage config)
    {
        lock (_gate)
        {
            _items.Clear();
            _pendingFrames = 0;
            _items.AddLast(config);
            _awaitingKeyframe = true;
        }
    }

    public FrameEnqueue EnqueueFrame(FrameMessage frame)
    {
        lock (_gate)
        {
            if (frame.IsKeyframe)
            {
                RemoveFrames();
                _awaitingKeyframe = false;
                AddFrame(frame);
                return FrameEnqueue.Queued;
            }
            if (_awaitingKeyframe) return FrameEnqueue.Dropped;
            if (_pendingFrames >= maxPendingFrames)
            {
                _awaitingKeyframe = true;
                return FrameEnqueue.DroppedNeedKeyframe;
            }
            AddFrame(frame);
            return FrameEnqueue.Queued;
        }
    }

    public bool TryDequeue([NotNullWhen(true)] out Message? message)
    {
        lock (_gate)
        {
            if (_items.First is not { } first)
            {
                message = null;
                return false;
            }
            _items.RemoveFirst();
            if (first.Value is FrameMessage) _pendingFrames--;
            message = first.Value;
            return true;
        }
    }

    private void AddFrame(FrameMessage frame)
    {
        _items.AddLast(frame);
        _pendingFrames++;
    }

    private void RemoveFrames()
    {
        for (var node = _items.First; node is not null;)
        {
            var next = node.Next;
            if (node.Value is FrameMessage)
            {
                _items.Remove(node);
                _pendingFrames--;
            }
            node = next;
        }
    }
}
```

Run: `dotnet test host/ScreenShare.slnx --filter FullyQualifiedName~VideoSendQueueTests`
Expected: PASS (6 casos).

- [ ] **Step 5: Testes do `SessionWriter`**

`host/ScreenShare.Tests/Protocol/SessionWriterTests.cs`:

```csharp
using ScreenShare.Core.Protocol;

namespace ScreenShare.Tests.Protocol;

public sealed class SessionWriterTests
{
    private static readonly ConfigMessage Config = new(2520, 1080, VideoCodec.H265, 50_000, [0, 0, 0, 1, 0x40, 0x01, 0x0C]);
    private static readonly byte[] KeyData = [0, 0, 0, 1, 0x26, 0x01, 0xAF];

    private static FrameMessage Key(ulong ts) => new(ts, true, KeyData);

    private static FrameMessage P(ulong ts) => new(ts, false, [0, 0, 0, 1, 0x02, 0x01, 0x80]);

    [Fact]
    public async Task Frame_is_written_as_header_plus_data_exactly_like_the_vector()
    {
        var stream = new GuardedStream();
        var writer = new SessionWriter(stream, () => 0);
        writer.SendVideoConfig(Config);
        writer.SendVideoFrame(new FrameMessage(1_000_000, true, KeyData));

        await RunUntilAsync(writer, stream, messages => messages.Count == 2);

        Assert.Equal(MessageCodec.Encode(Config).Concat(Vectors.Load("frame.hex")).ToArray(), stream.Written);
    }

    [Fact]
    public async Task Control_messages_go_before_pending_video()
    {
        var stream = new GuardedStream();
        var writer = new SessionWriter(stream, () => 0);
        writer.SendVideoConfig(Config);
        writer.SendVideoFrame(Key(1));
        writer.Send(new PongMessage(7));

        var messages = await RunUntilAsync(writer, stream, m => m.Count == 3);

        Assert.Equal(new PongMessage(7), messages[0]);
        Assert.IsType<ConfigMessage>(messages[1]);
        Assert.IsType<FrameMessage>(messages[2]);
    }

    [Fact]
    public async Task Ping_value_is_read_when_written_not_when_queued()
    {
        ulong clock = 5;
        var stream = new GuardedStream();
        var writer = new SessionWriter(stream, () => clock);
        writer.SendPing();
        clock = 9;

        var messages = await RunUntilAsync(writer, stream, m => m.Count == 1);

        Assert.Equal(new PingMessage(9), messages[0]);
    }

    [Fact]
    public async Task Too_many_pending_frames_drop_until_keyframe_and_ask_for_one()
    {
        var stream = new GuardedStream();
        var writer = new SessionWriter(stream, () => 0, maxPendingFrames: 2);
        var keyframeRequests = 0;
        writer.KeyframeNeeded += () => keyframeRequests++;
        writer.SendVideoConfig(Config);
        writer.SendVideoFrame(Key(1));
        writer.SendVideoFrame(P(2));
        writer.SendVideoFrame(P(3)); // terceiro pendente: descartado, pede keyframe
        writer.SendVideoFrame(P(4)); // ainda esperando keyframe: descartado sem pedir de novo
        writer.SendVideoFrame(Key(5)); // substitui o que ainda esperava

        var messages = await RunUntilAsync(writer, stream, m => m.Count == 2);

        Assert.Equal(1, keyframeRequests);
        Assert.IsType<ConfigMessage>(messages[0]);
        var frame = Assert.IsType<FrameMessage>(messages[1]);
        Assert.Equal(5UL, frame.TimestampUs);
    }

    [Fact]
    public async Task Concurrent_producers_never_overlap_writes_and_keep_the_stream_decodable()
    {
        var stream = new GuardedStream();
        var writer = new SessionWriter(stream, () => 0);
        writer.KeyframeNeeded += () => { };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = writer.RunAsync(cts.Token);

        var video = Task.Run(async () =>
        {
            writer.SendVideoConfig(Config);
            for (var i = 0; i < 1000; i++)
            {
                writer.SendVideoFrame(new FrameMessage((ulong)i, i % 50 == 0, KeyData));
                if (i % 7 == 0) await Task.Yield();
            }
        });
        var control = Task.Run(async () =>
        {
            for (var i = 0; i < 1000; i++)
            {
                writer.Send(new PongMessage((ulong)i));
                if (i % 5 == 0) await Task.Yield();
            }
        });
        await Task.WhenAll(video, control);
        writer.Send(new PongMessage(ulong.MaxValue)); // marcador: quando ele sai, todos os PONGs saíram

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!(DecodeComplete(stream.Written).Contains(new PongMessage(ulong.MaxValue)) && writer.PendingFrames == 0))
        {
            Assert.False(run.IsFaulted, run.Exception?.ToString());
            Assert.True(DateTime.UtcNow < deadline, "o escritor não terminou de escrever");
            await Task.Delay(10);
        }
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        var messages = DecodeComplete(stream.Written);
        var pongs = messages.OfType<PongMessage>().Select(p => p.TimestampUs).ToList();
        Assert.Equal(Enumerable.Range(0, 1000).Select(i => (ulong)i).Append(ulong.MaxValue), pongs);
        var frames = messages.OfType<FrameMessage>().ToList();
        Assert.True(frames[0].IsKeyframe);
        for (var i = 1; i < frames.Count; i++)
        {
            Assert.True(frames[i].TimestampUs > frames[i - 1].TimestampUs);
            if (frames[i].TimestampUs != frames[i - 1].TimestampUs + 1)
                Assert.True(frames[i].IsKeyframe, $"depois de um pulo (t={frames[i].TimestampUs}) o quadro tem de ser keyframe");
        }
    }

    [Fact]
    public async Task Write_error_ends_the_writer()
    {
        var writer = new SessionWriter(new FailingStream(), () => 0);
        writer.Send(new PongMessage(1));

        await Assert.ThrowsAsync<IOException>(() => writer.RunAsync(CancellationToken.None));
    }

    [Fact]
    public void ConfigSent_after_a_video_or_a_fallback_config()
    {
        var video = new SessionWriter(new GuardedStream(), () => 0);
        var fallback = new SessionWriter(new GuardedStream(), () => 0);
        Assert.False(video.ConfigSent);

        video.SendVideoConfig(Config);
        fallback.Send(Config);

        Assert.True(video.ConfigSent);
        Assert.True(fallback.ConfigSent);
    }

    /// <summary>Roda o escritor até as mensagens escritas satisfazerem a condição, para e devolve tudo o que saiu.</summary>
    private static async Task<List<Message>> RunUntilAsync(SessionWriter writer, GuardedStream stream, Func<List<Message>, bool> done)
    {
        using var cts = new CancellationTokenSource();
        var run = writer.RunAsync(cts.Token);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!done(DecodeComplete(stream.Written)))
        {
            Assert.False(run.IsFaulted, run.Exception?.ToString());
            Assert.True(DateTime.UtcNow < deadline, "o escritor não escreveu o esperado em 5 s");
            await Task.Delay(5);
        }
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        return DecodeComplete(stream.Written);
    }

    /// <summary>Decodifica as mensagens completas; uma mensagem cortada no fim (ainda sendo escrita) fica de fora.</summary>
    private static List<Message> DecodeComplete(byte[] bytes)
    {
        var messages = new List<Message>();
        var offset = 0;
        while (bytes.Length - offset >= MessageCodec.HeaderSize)
        {
            var length = (int)BitConverter.ToUInt32(bytes, offset + 1);
            if (bytes.Length - offset - MessageCodec.HeaderSize < length) break;
            messages.Add(MessageCodec.Decode(bytes[offset], bytes.AsSpan(offset + MessageCodec.HeaderSize, length)));
            offset += MessageCodec.HeaderSize + length;
        }
        return messages;
    }

    /// <summary>Guarda o que foi escrito e falha se duas escritas se sobrepõem, como o SslStream.</summary>
    private sealed class GuardedStream : Stream
    {
        private readonly MemoryStream _data = new();
        private int _writing;

        public byte[] Written
        {
            get
            {
                lock (_data) return _data.ToArray();
            }
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _writing, 1) == 1) throw new NotSupportedException("Duas escritas ao mesmo tempo.");
            try
            {
                await Task.Yield();
                lock (_data) _data.Write(buffer.Span);
            }
            finally
            {
                Volatile.Write(ref _writing, 0);
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Flush() { }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FailingStream : Stream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("conexão caiu"));

        public override void Flush() { }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
```

Run: `dotnet test host/ScreenShare.slnx --filter FullyQualifiedName~SessionWriterTests`
Expected: erro de compilação, `SessionWriter` não existe.

- [ ] **Step 6: Implementar o `SessionWriter`**

`host/ScreenShare.Core/Protocol/SessionWriter.cs`:

```csharp
using System.Collections.Concurrent;

namespace ScreenShare.Core.Protocol;

/// <summary>
/// O único que escreve no stream de uma sessão: o SslStream não aceita duas escritas ao mesmo tempo, e o vídeo (thread
/// do encoder), os PONGs (laço de leitura) e os PINGs (timer) chegam de lugares diferentes. Mensagens de controle saem
/// antes do vídeo e nunca são descartadas; o vídeo passa pela VideoSendQueue. Um erro de escrita encerra RunAsync com
/// a exceção, o que encerra a sessão.
/// </summary>
public sealed class SessionWriter
{
    private readonly Stream _stream;
    private readonly Func<ulong> _clockUs;
    private readonly VideoSendQueue _video;
    private readonly ConcurrentQueue<Message?> _control = new(); // null = PING carimbado na hora de escrever
    private readonly SemaphoreSlim _signal = new(0);
    private readonly byte[] _frameHeader = new byte[MessageCodec.FrameHeaderSize];
    private volatile bool _configSent;

    /// <param name="clockUs">Relógio do PC em µs (PcClock.NowUs), lido quando o PING é escrito.</param>
    public SessionWriter(Stream stream, Func<ulong> clockUs, int maxPendingFrames = 2)
    {
        _stream = stream;
        _clockUs = clockUs;
        _video = new VideoSendQueue(maxPendingFrames);
    }

    /// <summary>A fila descartou quadros por excesso: quem produz o vídeo precisa mandar um keyframe.</summary>
    public event Action? KeyframeNeeded;

    /// <summary>Já foi posto na fila algum CONFIG (de vídeo ou de fallback).</summary>
    public bool ConfigSent => _configSent;

    public int PendingFrames => _video.PendingFrames;

    /// <summary>Mensagem de controle (PONG, CONFIG de fallback...): sai antes do vídeo e nunca é descartada.</summary>
    public void Send(Message control)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (control is ConfigMessage) _configSent = true;
        _control.Enqueue(control);
        _signal.Release();
    }

    /// <summary>PING do PC: o valor (relógio do PC) é lido na hora de escrever, para a fila não atrasar a medida.</summary>
    public void SendPing()
    {
        _control.Enqueue(null);
        _signal.Release();
    }

    /// <summary>CONFIG de um stream de vídeo novo: descarta o que ainda restava do stream anterior.</summary>
    public void SendVideoConfig(ConfigMessage config)
    {
        _configSent = true;
        _video.EnqueueConfig(config);
        _signal.Release();
    }

    public void SendVideoFrame(FrameMessage frame)
    {
        switch (_video.EnqueueFrame(frame))
        {
            case FrameEnqueue.Queued:
                _signal.Release();
                break;
            case FrameEnqueue.DroppedNeedKeyframe:
                KeyframeNeeded?.Invoke();
                break;
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await _signal.WaitAsync(cancellationToken);
            while (TryNext(out var message))
                await WriteAsync(message, cancellationToken);
            await _stream.FlushAsync(cancellationToken);
        }
    }

    private bool TryNext(out Message message)
    {
        if (_control.TryDequeue(out var control))
        {
            message = control ?? new PingMessage(_clockUs());
            return true;
        }
        if (_video.TryDequeue(out var video))
        {
            message = video;
            return true;
        }
        message = null!;
        return false;
    }

    private async Task WriteAsync(Message message, CancellationToken cancellationToken)
    {
        if (message is FrameMessage frame)
        {
            MessageCodec.WriteFrameHeader(_frameHeader, frame.TimestampUs, frame.IsKeyframe, frame.Data.Length);
            await _stream.WriteAsync(_frameHeader, cancellationToken);
            await _stream.WriteAsync(frame.Data, cancellationToken);
        }
        else
        {
            await _stream.WriteAsync(MessageCodec.Encode(message), cancellationToken);
        }
    }
}
```

Run: `dotnet test host/ScreenShare.slnx --filter FullyQualifiedName~SessionWriterTests`
Expected: PASS (7 casos). Rode a classe 3 vezes seguidas: o teste de concorrência não pode falhar nenhuma vez.

- [ ] **Step 7: Rodar tudo, commit e push**

Run: `dotnet build host/ScreenShare.slnx` e `dotnet test host/ScreenShare.slnx`
Expected: 0 avisos; 252 + 3 + 6 + 7 = 268 aprovados e 1 ignorado.

```bash
git add host/ScreenShare.Core/Protocol host/ScreenShare.Tests/Protocol host/ScreenShare.Tests/Video/AnnexBTests.cs
git commit -m "feat(core): escritor único da sessão com fila de vídeo que descarta até o keyframe"
git push
```

---

## Tasks posteriores ao spike (escritas depois da Task 1)

O escopo está fixado aqui; o código depende do que o spike mostrar.

| Task | Escopo | Testes |
|---|---|---|
| 4 | `HostServer`: `SessionWriter`, `PING` do PC a cada 1 s, parâmetro `IVideoSource? video = null`, `CONFIG` de fallback em 2 s, `KEYFRAME_REQ` e `KeyframeNeeded` → `RequestKeyframe`, encerramento vídeo → escritor → lease, `ExclusiveVideoSource` | Testes atuais verdes; `PING` do PC a cada ~1 s; vídeo falso → `CONFIG` e quadros na ordem; KEYFRAME_REQ repassado; fallback; cliente que para de ler → descarte + pedido; sessão nova assume o vídeo |
| 5 | Lease com `Current`, `Changed` e `Refresh` (Parte 2) | Reinício do driver renomeia a saída e dispara `Changed` com o modo exato; `Refresh`; sem deadlock |
| 6 | Projeto `ScreenShare.Video`: interfaces, `VideoPipeline`, `FramePacer` e `KeyframeScheduler` (puros) | Com falsos e `FakeTimeProvider`: IDR → `CONFIG` → quadros; ritmo a 60 e 30 fps; espera escritor e encoder; tela parada + pedido; refinamentos; ACCESS_LOST; tamanho novo; H.265 → H.264; timestamps crescentes |
| 7 | Captura (DXGI ou WGC, conforme o spike) + localizador de saída | Puros (escolha da saída, classificação de HRESULT); GPU opcional (`[GpuFact]`); manual no VDD com UAC e Win+L |
| 8 | Encoder Media Foundation + NV12 + `ICodecAPI` + catálogo | Puros (ajustes → tipos e CODECAPI); GPU opcional (30 quadros, IDR primeiro, IDR forçado, p95); manual com ffplay |
| 9 | DevHost: `--fps`, `--bitrate`, `--codec`, `--sem-video`, `--capturar principal`, `--gravar`, estatísticas a cada 5 s | Leitura das opções e padrões por porta; manual com ffplay |
| 10 | Android `Connection` (`PING` do PC → `PONG`, `CONFIG` no meio, `FRAME` → sink, `requestKeyframe`, `soTimeout`), `ClockSync`, HELLO com painel e codecs | TLS loopback; `ClockSync` com jitter, fila assimétrica e deriva |
| 11 | Android `AnnexB`, `CsdBuilder`, `DecoderCore`, `VideoStats` e `CodecSupport` | Mesmos vetores do C#; todas as regras do `DecoderCore` |
| 12 | `MediaCodecPort`, `VideoPlayer`, `SurfaceView` com recorte e `setFixedSize`, overlay | Compilação no CI; manual no celular (imagem 1:1, segundo plano) |
| 13 | Cursor desenhado no vídeo | Conversor de formato do ponteiro (3 tipos); manual |
| 14 | Verificação ponta a ponta + README, guia e spec | Lista manual do spec (latência filmada, UAC, Win+L, troca de sessão, `--fps 30`, tela parada) |
