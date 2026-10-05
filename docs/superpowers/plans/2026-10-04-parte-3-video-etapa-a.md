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
- **depois do spike:** as Tasks 4 a 14, escritas com os fatos reais e revisadas pelo usuário antes de executar.

Sobre o código das Tasks 4 a 14:
- Foi escrito e verificado antes, numa cópia de rascunho do repositório, task por task: build com 0 avisos, testes verdes rodados 3 vezes e testes de GPU rodados na RTX do usuário.
- O APK do Android compilou.
- Na captura com cursor, o resultado foi conferido numa imagem salva.

Por isso cada task traz os arquivos inteiros (ou o diff, quando a mudança é pequena) para copiar como estão. Em relação à tabela de escopo do design, a ordem das Tasks 4 e 5 trocou: o lease com `Changed` vem antes do `HostServer`, porque o `VideoRequest` usa o `IMonitorSource` que ele cria.

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

1. **Celular para de ler (Wi-Fi travou)** — o PC não pode acumular atraso nem memória. O esperado é ficar com no máximo 2 quadros na fila, descartar até um IDR e pedir IDR. *Testes:* Task 3 (`Too_many_pending_frames_drop_until_keyframe_and_ask_for_one`) e Task 5 (`Phone_that_stops_reading_makes_the_queue_drop_and_ask_for_a_keyframe`).
2. **Escritas simultâneas** da thread do encoder e do laço de leitura (`PONG`) no mesmo `SslStream` — o esperado é nunca sobrepor. *Teste:* Task 3 (`Concurrent_producers_never_overlap_writes_and_keep_the_stream_decodable`, com um stream que falha como o `SslStream`).
3. **Annex-B real do encoder** (start code de 3 bytes, AUD, SEI, zeros no fim) — o parser não pode errar o tipo nem os parâmetros. *Testes:* Task 2 (casos sintéticos + IDRs reais do spike) e Task 11 (mesmos vetores no Kotlin).
4. **Tela parada + pedido de keyframe** (decoder recriado, KEYFRAME_REQ) — o esperado é chegar um IDR mesmo sem mudança na tela. *Teste:* Task 6 (pipeline recodifica a última imagem).
5. **Resolução muda no meio da sessão** (reinício do driver, nome `\\.\DISPLAYn` novo) — o esperado é `CONFIG` novo + IDR, e o celular recria o decoder. *Testes:* Task 4 (`Driver_restart_renames_the_output_and_fires_Changed_with_the_exact_mode`), Task 6 (`Capture_with_a_new_size_starts_a_new_stream_with_config_and_idr`, `Monitor_change_reopens_the_capture_on_the_new_output`) e Task 11 (`sameConfigKeepsTheDecoderAndADifferentOneRecreatesIt`).

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

### Task 4: lease com `Current`, `Changed` e `Refresh` (Parte 2)

**Por quê:** quando o driver reinicia para aplicar a resolução exata, a saída do monitor virtual volta com outro nome (`\\.\DISPLAY5` → `\\.\DISPLAY6`) e outro tamanho. A captura (Task 6) precisa saber disso para reabrir no nome novo e mandar um `CONFIG` novo. Hoje o lease guarda só o monitor do momento do `Acquire`.

**Files:**
- Modify: `host/ScreenShare.Display/VirtualMonitor.cs` (interface `IMonitorSource`; lease com `Current`, `Changed`, `Refresh`)
- Modify: `host/ScreenShare.Display/VirtualMonitorManager.cs` (guarda os leases ativos, atualiza-os e avisa fora da trava)
- Test: `host/ScreenShare.Tests/Display/VirtualMonitorManagerTests.cs` (6 testes novos)

**Interfaces:**
- Consumes: `VirtualMonitorManager` e `VirtualMonitorLease` da Parte 2 (`Acquire`, `Monitor`, `Width`, `Height`, `Dispose`); `FakeDisplayTopology` e `FakeRestarter` dos testes.
- Produces (as Tasks 5, 6 e 7 usam):
  - `public interface IMonitorSource { VirtualMonitor? Current { get; } event Action? Changed; VirtualMonitor? Refresh(); }` em `ScreenShare.Display`;
  - `VirtualMonitorLease : IMonitorSource, IDisposable`, com construtor `(MonitorRequest request, VirtualMonitor? monitor, Action? release, Func<VirtualMonitor?>? refresh = null)`;
  - `Monitor` continua sendo o do `Acquire`; `Current` é o de agora; `Width`/`Height` passam a vir de `Current`.

- [ ] **Step 1: Testes**

Substitua `host/ScreenShare.Tests/Display/VirtualMonitorManagerTests.cs` pelo conteúdo abaixo. As partes antigas não mudam. O que entra de novo é o helper `GatedRestartThatRenamesTo` e 6 testes, todos logo antes de `private sealed class FakeRestarter`.

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
```

Run: `dotnet test host/ScreenShare.slnx --filter FullyQualifiedName~VirtualMonitorManagerTests`
Expected: erro de compilação (`Current`, `Changed` e `Refresh` não existem).

- [ ] **Step 2: Lease e `IMonitorSource`**

`host/ScreenShare.Display/VirtualMonitor.cs` inteiro:

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
/// O monitor que a captura segue. Current muda quando o driver reinicia (o nome \\.\DISPLAYn muda) ou quando a
/// resolução exata é aplicada; Changed avisa depois da mudança. Refresh relê a saída agora (ex.: a captura não a achou).
/// </summary>
public interface IMonitorSource
{
    VirtualMonitor? Current { get; }

    event Action? Changed;

    VirtualMonitor? Refresh();
}

/// <summary>
/// O monitor que uma sessão está usando. Dispose libera (uma vez só); sem monitor, Width/Height são os do pedido.
/// O gerenciador atualiza Current e dispara Changed fora da trava dele.
/// </summary>
public sealed class VirtualMonitorLease(MonitorRequest request, VirtualMonitor? monitor, Action? release,
    Func<VirtualMonitor?>? refresh = null) : IMonitorSource, IDisposable
{
    private Action? _release = release;
    private VirtualMonitor? _current = monitor;

    public static VirtualMonitorLease Without(MonitorRequest request) => new(request, null, null);

    public MonitorRequest Request { get; } = request;

    /// <summary>O monitor no momento do Acquire.</summary>
    public VirtualMonitor? Monitor { get; } = monitor;

    /// <summary>O monitor agora: muda depois de um reinício do driver ou da resolução exata aplicada.</summary>
    public VirtualMonitor? Current => Volatile.Read(ref _current);

    public int Width => Current?.Width ?? Request.Width;
    public int Height => Current?.Height ?? Request.Height;

    public event Action? Changed;

    public VirtualMonitor? Refresh() => refresh is null ? Current : refresh();

    /// <summary>Troca o monitor atual; true se mudou (quem chama dispara Changed depois, fora da trava).</summary>
    internal bool Update(VirtualMonitor monitor)
    {
        if (Equals(Current, monitor)) return false;
        Volatile.Write(ref _current, monitor);
        return true;
    }

    internal void RaiseChanged() => Changed?.Invoke();

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

- [ ] **Step 3: Gerenciador**

`host/ScreenShare.Display/VirtualMonitorManager.cs` inteiro. O que muda em relação à Parte 2:
- `_active` (leases vivos) e `_toNotify` (os que mudaram);
- `Acquire` e `RestartThenApplyAsync` viram um invólucro com `finally { NotifyChanged(); }` em volta do código antigo. O aviso sai depois que a trava foi solta, então quem recebe pode chamar o gerenciador;
- todo `Apply` que devolve um monitor com sessões ativas chama `UpdateLeases`;
- `Refresh(lease)` relê a saída sob a trava;
- `Release` recebe o lease e o tira de `_active`.

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
```

Run: `dotnet test host/ScreenShare.slnx --filter FullyQualifiedName~VirtualMonitorManagerTests`
Expected: PASS (37 casos: os 31 de antes e os 6 novos).

- [ ] **Step 4: Rodar tudo, commit e push**

Run: `dotnet build host/ScreenShare.slnx` e `dotnet test host/ScreenShare.slnx`
Expected: 0 avisos; 274 aprovados e 1 ignorado.

```bash
git add host/ScreenShare.Display host/ScreenShare.Tests/Display/VirtualMonitorManagerTests.cs
git commit -m "feat(display): lease acompanha o monitor virtual (nome e tamanho novos depois do reinício do driver)"
git push
```

---

### Task 5: projeto `ScreenShare.Video` e `HostServer` com escritor único, PING do PC e vídeo

**Por quê:** a sessão passa a escrever tudo pelo `SessionWriter` (Task 3): os PONGs, o PING do PC a cada segundo, o `CONFIG` de fallback e o vídeo. Ela também pede o vídeo a um `IVideoSource`. Nesta task o vídeo de verdade ainda não existe; os testes usam um falso.

**Files:**
- Create: `host/ScreenShare.Video/ScreenShare.Video.csproj`, `host/ScreenShare.Video/VideoSource.cs` (tipos de entrada), `host/ScreenShare.Video/ExclusiveVideoSource.cs`
- Modify: `host/ScreenShare.slnx`, `host/ScreenShare.DevHost/ScreenShare.DevHost.csproj` e `host/ScreenShare.Tests/ScreenShare.Tests.csproj` (referência ao projeto novo)
- Modify: `host/ScreenShare.Core/Protocol/SessionWriter.cs` (`SendFallbackConfig`, comentários sobre o array do quadro e o `RunAsync` único)
- Modify: `host/ScreenShare.DevHost/HostServer.cs`
- Test: `host/ScreenShare.Tests/DevHost/FakeVideoSource.cs` (novo), `host/ScreenShare.Tests/DevHost/HostServerTests.cs`, `host/ScreenShare.Tests/Video/ExclusiveVideoSourceTests.cs` (novo), `host/ScreenShare.Tests/Protocol/SessionWriterTests.cs`

**Interfaces:**
- Consumes:
  - `SessionWriter` (Task 3): `Send`, `SendPing`, `SendVideoConfig`, `SendVideoFrame`, `KeyframeNeeded`, `ConfigSent`, `PendingFrames`, `RunAsync`;
  - `PcClock.NowUs` (Task 2);
  - `VirtualMonitorLease : IMonitorSource` (Task 4).
- Produces (as Tasks 6, 8 e 9 usam), no namespace `ScreenShare.Video`:
  - `enum VideoLink { Wifi, Usb }`;
  - `record VideoRequest(IMonitorSource Monitor, VideoCodec PhoneCodecs, VideoLink Link)`;
  - `interface IVideoOutput { void OnConfig(ConfigMessage); void OnFrame(FrameMessage); int PendingFrames { get; } }`;
  - `record VideoStats(int Width, int Height, VideoCodec Codec, long Frames, long Bytes, long Keyframes, double EncodeP95Ms)` com `VideoStats.Empty`;
  - `interface IVideoStream : IAsyncDisposable { void RequestKeyframe(); VideoStats Stats { get; } }`;
  - `interface IVideoSource { Task<IVideoStream?> StartAsync(VideoRequest, IVideoOutput, CancellationToken); }`;
  - `ExclusiveVideoSource(IVideoSource inner)`;
  - `SessionWriter.SendFallbackConfig(ConfigMessage) → bool`;
  - no `HostServer`, os parâmetros novos `IVideoSource? video = null`, `TimeSpan? pingInterval = null` (padrão 1 s) e `TimeSpan? videoConfigTimeout = null` (padrão 2 s). O `HostServer` embrulha o vídeo num `ExclusiveVideoSource` sozinho.

**Regras desta task (da revisão da Task 3):**
- o handler de `KeyframeNeeded` roda na thread do encoder e não pode lançar (`RequestKeyframe` com try/catch);
- `RunAsync` roda uma vez por sessão;
- o `CONFIG` de fallback vai pela fila de vídeo (`SendFallbackConfig`): um `CONFIG` de vídeo que chegar depois o substitui, e os dois nunca saem fora de ordem.

**Regra do spec:** o buffer de envio do socket fica fixo em 512 KiB (`client.Client.SendBufferSize`). O ajuste automático do Windows cresce até vários MB e esconde a rede travada; com o buffer fixo, a fila do `SessionWriter` percebe a travada e descarta até um IDR.

- [ ] **Step 1: Projeto novo**

`host/ScreenShare.Video/ScreenShare.Video.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\ScreenShare.Core\ScreenShare.Core.csproj" />
    <ProjectReference Include="..\ScreenShare.Display\ScreenShare.Display.csproj" />
  </ItemGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="ScreenShare.Tests" />
  </ItemGroup>

</Project>
```

Em `host/ScreenShare.slnx`, `host/ScreenShare.DevHost/ScreenShare.DevHost.csproj` e `host/ScreenShare.Tests/ScreenShare.Tests.csproj`:

```diff
@@ -3,4 +3,5 @@
   <Project Path="ScreenShare.DevHost/ScreenShare.DevHost.csproj" />
   <Project Path="ScreenShare.Display/ScreenShare.Display.csproj" />
   <Project Path="ScreenShare.Tests/ScreenShare.Tests.csproj" />
+  <Project Path="ScreenShare.Video/ScreenShare.Video.csproj" />
 </Solution>
```

```diff
@@ -1,8 +1,9 @@
-﻿<Project Sdk="Microsoft.NET.Sdk">
+<Project Sdk="Microsoft.NET.Sdk">
 
   <ItemGroup>
     <ProjectReference Include="..\ScreenShare.Core\ScreenShare.Core.csproj" />
     <ProjectReference Include="..\ScreenShare.Display\ScreenShare.Display.csproj" />
+    <ProjectReference Include="..\ScreenShare.Video\ScreenShare.Video.csproj" />
   </ItemGroup>
 
   <ItemGroup>
```

```diff
@@ -1,4 +1,4 @@
-﻿<Project Sdk="Microsoft.NET.Sdk">
+<Project Sdk="Microsoft.NET.Sdk">
 
   <PropertyGroup>
     <TargetFramework>net10.0-windows</TargetFramework>
@@ -23,6 +23,7 @@
     <ProjectReference Include="..\ScreenShare.Core\ScreenShare.Core.csproj" />
     <ProjectReference Include="..\ScreenShare.DevHost\ScreenShare.DevHost.csproj" />
     <ProjectReference Include="..\ScreenShare.Display\ScreenShare.Display.csproj" />
+    <ProjectReference Include="..\ScreenShare.Video\ScreenShare.Video.csproj" />
   </ItemGroup>
 
   <ItemGroup>
```

`host/ScreenShare.Video/VideoSource.cs`:

```csharp
using ScreenShare.Core.Protocol;
using ScreenShare.Display;

namespace ScreenShare.Video;

/// <summary>Por onde o celular está ligado: decide o bitrate padrão.</summary>
public enum VideoLink
{
    Wifi,
    Usb,
}

/// <summary>O vídeo de uma sessão: o monitor a capturar, os codecs que o celular decodifica e o caminho da conexão.</summary>
public sealed record VideoRequest(IMonitorSource Monitor, VideoCodec PhoneCodecs, VideoLink Link);

/// <summary>Para onde o vídeo vai (o SessionWriter da sessão). Chamado na thread do encoder; nunca bloqueia.</summary>
public interface IVideoOutput
{
    /// <summary>Começo de um stream: o próximo quadro é keyframe.</summary>
    void OnConfig(ConfigMessage config);

    /// <summary>Um access unit. O array passa a ser de quem recebe: quem chama não pode reutilizá-lo.</summary>
    void OnFrame(FrameMessage frame);

    /// <summary>Quadros esperando a rede: a captura só pega imagem nova com zero.</summary>
    int PendingFrames { get; }
}

/// <summary>Números acumulados do vídeo de uma sessão, para as estatísticas do console.</summary>
public sealed record VideoStats(int Width, int Height, VideoCodec Codec, long Frames, long Bytes, long Keyframes, double EncodeP95Ms)
{
    public static VideoStats Empty { get; } = new(0, 0, VideoCodec.None, 0, 0, 0, 0);
}

/// <summary>O vídeo em andamento de uma sessão. DisposeAsync para a captura e o encoder.</summary>
public interface IVideoStream : IAsyncDisposable
{
    /// <summary>Pede um keyframe (celular, descarte na fila). Nunca lança; pedidos próximos são juntados.</summary>
    void RequestKeyframe();

    VideoStats Stats { get; }
}

public interface IVideoSource
{
    /// <summary>Começa o vídeo de uma sessão; null = sem vídeo (por exemplo, sem encoder para os codecs do celular).</summary>
    Task<IVideoStream?> StartAsync(VideoRequest request, IVideoOutput output, CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Testes do `ExclusiveVideoSource` e do fallback no escritor**

`host/ScreenShare.Tests/Video/ExclusiveVideoSourceTests.cs`:

```csharp
using ScreenShare.Core.Protocol;
using ScreenShare.Display;
using ScreenShare.Video;

namespace ScreenShare.Tests.Video;

public sealed class ExclusiveVideoSourceTests
{
    private static readonly VideoRequest Request =
        new(VirtualMonitorLease.Without(MonitorRequest.Normalize(2400, 1080, 420)), VideoCodec.H264, VideoLink.Wifi);

    private readonly RecordingSource _inner = new();

    [Fact]
    public async Task New_session_stops_the_previous_stream_before_starting_its_own()
    {
        var source = new ExclusiveVideoSource(_inner);

        await source.StartAsync(Request, null!, CancellationToken.None);
        await source.StartAsync(Request, null!, CancellationToken.None);

        Assert.Equal(["start1", "stop1", "start2"], _inner.Events);
    }

    [Fact]
    public async Task Disposing_a_stream_that_was_taken_over_does_not_stop_the_new_one()
    {
        var source = new ExclusiveVideoSource(_inner);
        var first = await source.StartAsync(Request, null!, CancellationToken.None);
        await source.StartAsync(Request, null!, CancellationToken.None);

        await first!.DisposeAsync();

        Assert.Equal(["start1", "stop1", "start2"], _inner.Events);
    }

    [Fact]
    public async Task Keyframe_request_to_a_stopped_stream_is_ignored()
    {
        var source = new ExclusiveVideoSource(_inner);
        var first = await source.StartAsync(Request, null!, CancellationToken.None);
        await source.StartAsync(Request, null!, CancellationToken.None);

        first!.RequestKeyframe();

        Assert.Equal(0, _inner.Streams[0].KeyframeRequests);
    }

    private sealed class RecordingSource : IVideoSource
    {
        public List<string> Events { get; } = [];
        public List<RecordingStream> Streams { get; } = [];

        public Task<IVideoStream?> StartAsync(VideoRequest request, IVideoOutput output, CancellationToken cancellationToken)
        {
            var stream = new RecordingStream(Streams.Count + 1, Events);
            Streams.Add(stream);
            Events.Add($"start{stream.Id}");
            return Task.FromResult<IVideoStream?>(stream);
        }
    }

    private sealed class RecordingStream(int id, List<string> events) : IVideoStream
    {
        public int Id { get; } = id;
        public int KeyframeRequests { get; private set; }
        public VideoStats Stats => VideoStats.Empty;

        public void RequestKeyframe() => KeyframeRequests++;

        public ValueTask DisposeAsync()
        {
            events.Add($"stop{Id}");
            return ValueTask.CompletedTask;
        }
    }
}
```

Em `host/ScreenShare.Tests/Protocol/SessionWriterTests.cs`, antes do método `RunUntilAsync`:

```diff
@@ -151,6 +151,33 @@ public sealed class SessionWriterTests
         Assert.True(fallback.ConfigSent);
     }
 
+    [Fact]
+    public async Task Fallback_config_goes_out_only_when_no_config_was_queued_before()
+    {
+        var fallback = Config with { CodecConfig = [] };
+        var stream = new GuardedStream();
+        var writer = new SessionWriter(stream, () => 0);
+
+        Assert.True(writer.SendFallbackConfig(fallback));
+        Assert.False(writer.SendFallbackConfig(fallback));
+        writer.SendVideoConfig(Config); // o vídeo começou depois: o CONFIG dele substitui o de fallback na fila
+        writer.SendVideoFrame(Key(1));
+        var messages = await RunUntilAsync(writer, stream, m => m.Count == 2);
+
+        Assert.Equal(Config.CodecConfig, Assert.IsType<ConfigMessage>(messages[0]).CodecConfig);
+        Assert.IsType<FrameMessage>(messages[1]);
+    }
+
+    [Fact]
+    public void Fallback_config_after_a_video_config_is_not_sent()
+    {
+        var writer = new SessionWriter(new GuardedStream(), () => 0);
+        writer.SendVideoConfig(Config);
+
+        Assert.False(writer.SendFallbackConfig(Config with { CodecConfig = [] }));
+        Assert.Equal(0, writer.PendingFrames);
+    }
+
     /// <summary>Roda o escritor até as mensagens escritas satisfazerem a condição, para e devolve tudo o que saiu.</summary>
     private static async Task<List<Message>> RunUntilAsync(SessionWriter writer, GuardedStream stream, Func<List<Message>, bool> done)
     {
```

Run: `dotnet test host/ScreenShare.slnx --filter "FullyQualifiedName~ExclusiveVideoSourceTests|FullyQualifiedName~SessionWriterTests"`
Expected: erro de compilação (`ExclusiveVideoSource` e `SendFallbackConfig` não existem).

- [ ] **Step 3: `ExclusiveVideoSource` e `SendFallbackConfig`**

`host/ScreenShare.Video/ExclusiveVideoSource.cs`:

```csharp
namespace ScreenShare.Video;

/// <summary>
/// O Windows permite uma duplicação por saída por processo: só uma sessão tem vídeo por vez. Uma sessão nova (por
/// exemplo, o cabo enquanto a do Wi-Fi ainda não caiu) assume o vídeo; o stream da anterior é encerrado antes de o
/// novo começar, e ela fica sem vídeo.
/// </summary>
public sealed class ExclusiveVideoSource(IVideoSource inner) : IVideoSource
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Handle? _current;

    public async Task<IVideoStream?> StartAsync(VideoRequest request, IVideoOutput output, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_current is { } previous)
            {
                _current = null;
                await previous.StopAsync();
            }
            var stream = await inner.StartAsync(request, output, cancellationToken);
            if (stream is null) return null;
            _current = new Handle(this, stream);
            return _current;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask ReleaseAsync(Handle handle)
    {
        await _gate.WaitAsync();
        try
        {
            if (ReferenceEquals(_current, handle)) _current = null;
        }
        finally
        {
            _gate.Release();
        }
        await handle.StopAsync();
    }

    /// <summary>O stream entregue à sessão: depois de parado (pela própria sessão ou por uma nova), não faz mais nada.</summary>
    private sealed class Handle(ExclusiveVideoSource owner, IVideoStream inner) : IVideoStream
    {
        private int _stopped;

        public VideoStats Stats => inner.Stats;

        public void RequestKeyframe()
        {
            if (Volatile.Read(ref _stopped) == 0) inner.RequestKeyframe();
        }

        public ValueTask DisposeAsync() => owner.ReleaseAsync(this);

        public async ValueTask StopAsync()
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 0) await inner.DisposeAsync();
        }
    }
}
```

Em `host/ScreenShare.Core/Protocol/SessionWriter.cs`:

```diff
@@ -16,6 +16,7 @@ public sealed class SessionWriter
     private readonly ConcurrentQueue<Message?> _control = new(); // null = PING carimbado na hora de escrever
     private readonly SemaphoreSlim _signal = new(0);
     private readonly byte[] _frameHeader = new byte[MessageCodec.FrameHeaderSize];
+    private readonly Lock _configGate = new();
     private volatile bool _configSent;
 
     /// <param name="clockUs">Relógio do PC em µs (PcClock.NowUs), lido quando o PING é escrito.</param>
@@ -53,11 +54,34 @@ public sealed class SessionWriter
     /// <summary>CONFIG de um stream de vídeo novo: descarta o que ainda restava do stream anterior.</summary>
     public void SendVideoConfig(ConfigMessage config)
     {
-        _configSent = true;
-        _video.EnqueueConfig(config);
+        lock (_configGate)
+        {
+            _configSent = true;
+            _video.EnqueueConfig(config);
+        }
+        _signal.Release();
+    }
+
+    /// <summary>
+    /// CONFIG de fallback (sem vídeo, ou vídeo que não começou a tempo): só sai se nenhum CONFIG foi posto na fila
+    /// antes. Vai pela fila de vídeo, então um CONFIG de vídeo que chegar depois o substitui. true = foi para a fila.
+    /// </summary>
+    public bool SendFallbackConfig(ConfigMessage config)
+    {
+        lock (_configGate)
+        {
+            if (_configSent) return false;
+            _configSent = true;
+            _video.EnqueueConfig(config);
+        }
         _signal.Release();
+        return true;
     }
 
+    /// <summary>
+    /// Um quadro de vídeo. O array passa a ser do escritor até sair: quem chama não pode reutilizá-lo nem alterá-lo.
+    /// Se a fila passar do limite, dispara KeyframeNeeded nesta mesma thread (o handler não pode lançar).
+    /// </summary>
     public void SendVideoFrame(FrameMessage frame)
     {
         switch (_video.EnqueueFrame(frame))
@@ -71,6 +95,7 @@ public sealed class SessionWriter
         }
     }
 
+    /// <summary>Escreve até o cancelamento ou um erro de rede. Uma vez por sessão (o cabeçalho do FRAME é reaproveitado).</summary>
     public async Task RunAsync(CancellationToken cancellationToken)
     {
         while (true)
```

Run: `dotnet test host/ScreenShare.slnx --filter "FullyQualifiedName~ExclusiveVideoSourceTests|FullyQualifiedName~SessionWriterTests"`
Expected: PASS (3 + 9).

- [ ] **Step 4: Testes do `HostServer`**

`host/ScreenShare.Tests/DevHost/FakeVideoSource.cs`:

```csharp
using ScreenShare.Video;

namespace ScreenShare.Tests.DevHost;

/// <summary>Vídeo falso para o HostServer: o teste manda CONFIG e quadros pela saída que a sessão entregou.</summary>
internal sealed class FakeVideoSource : IVideoSource
{
    private readonly List<FakeVideoStream> _started = [];

    /// <summary>false = sem vídeo (StartAsync devolve null), como num PC sem encoder.</summary>
    public bool Enabled { get; set; }

    /// <summary>Chamado quando um stream é encerrado (para conferir a ordem do encerramento).</summary>
    public Action<string>? OnEvent { get; set; }

    /// <summary>Se definido, StartAsync só termina quando o teste completar (vídeo que demora a abrir).</summary>
    public TaskCompletionSource? StartGate { get; set; }

    public Exception? ThrowOnStart { get; set; }

    public bool ThrowOnDispose { get; set; }

    public IReadOnlyList<FakeVideoStream> Started
    {
        get
        {
            lock (_started) return [.. _started];
        }
    }

    public async Task<IVideoStream?> StartAsync(VideoRequest request, IVideoOutput output, CancellationToken cancellationToken)
    {
        if (ThrowOnStart is { } error) throw error;
        if (!Enabled) return null;
        var stream = new FakeVideoStream(request, output, OnEvent, ThrowOnDispose);
        lock (_started) _started.Add(stream);
        if (StartGate is { } gate) await gate.Task.WaitAsync(cancellationToken);
        return stream;
    }
}

internal sealed class FakeVideoStream(VideoRequest request, IVideoOutput output, Action<string>? onEvent, bool throwOnDispose) : IVideoStream
{
    private int _keyframeRequests;
    private int _disposed;

    public VideoRequest Request { get; } = request;
    public IVideoOutput Output { get; } = output;
    public int KeyframeRequests => Volatile.Read(ref _keyframeRequests);
    public bool Disposed => Volatile.Read(ref _disposed) == 1;
    public VideoStats Stats => VideoStats.Empty;

    public void RequestKeyframe() => Interlocked.Increment(ref _keyframeRequests);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) onEvent?.Invoke("vídeo encerrado");
        if (throwOnDispose) throw new InvalidOperationException("falha falsa ao encerrar");
        return ValueTask.CompletedTask;
    }
}
```

Substitua `host/ScreenShare.Tests/DevHost/HostServerTests.cs` pelo conteúdo abaixo. O que muda:
- o fixture passa o vídeo falso, PING do PC a cada 200 ms e fallback em 500 ms;
- `ReadSkippingPingsAsync` pula o PING do PC nos testes antigos que esperam um PONG ou o fechamento;
- `FakeMonitorManager.OnRelease`;
- 14 testes novos, antes de `FakeMonitorManager` (6 deles cobrem: vídeo demorando a abrir, KEYFRAME_REQ antes de o vídeo começar, celular que sai enquanto o vídeo abre, vídeo que falha ao abrir, vídeo que falha ao encerrar e intervalo de PING inválido).

```csharp
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using ScreenShare.Core.Protocol;
using ScreenShare.Core.Security;
using ScreenShare.Core.Video;
using ScreenShare.DevHost;
using ScreenShare.Display;
using ScreenShare.Video;

namespace ScreenShare.Tests.DevHost;

public sealed class HostServerTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screenshare-tests", Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(15));
    private readonly HostIdentity _identity;
    private readonly PairingSession _pairing = new(TimeProvider.System);
    private readonly DeviceRegistry _devices;
    private readonly FakeMonitorManager _monitors = new();
    private readonly FakeVideoSource _video = new();
    private readonly HostServer _server;
    private Task _serving = Task.CompletedTask;

    public HostServerTests()
    {
        _identity = HostIdentity.LoadOrCreate(_dir, "PC de Teste");
        _devices = new DeviceRegistry(Path.Combine(_dir, "paired-devices.json"));
        _server = new HostServer(0, 0, _identity, _pairing, _devices,
            handshakeTimeout: TimeSpan.FromSeconds(2), idleTimeout: TimeSpan.FromSeconds(2), monitors: _monitors,
            video: _video, pingInterval: TimeSpan.FromMilliseconds(200), videoConfigTimeout: TimeSpan.FromMilliseconds(500));
    }

    public Task InitializeAsync()
    {
        _server.Start();
        _serving = _server.RunAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        await _serving;
        _server.Dispose();
        _identity.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private static HelloMessage Hello(ushort version = MessageCodec.ProtocolVersion) =>
        new(version, 2400, 1080, 420, VideoCodec.H264 | VideoCodec.H265);

    /// <summary>TLS numa porta do servidor, aceitando só o certificado do PC de teste (como o celular faz).</summary>
    private async Task<(TcpClient Client, SslStream Stream, MessageReader Reader)> ConnectSecureAsync(int port)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, _cts.Token);
        var tls = new SslStream(client.GetStream());
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "screenshare",
            RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                certificate is not null && HostIdentity.ComputeFingerprint(certificate.GetRawCertData()) == _identity.Fingerprint,
        }, _cts.Token);
        return (client, tls, new MessageReader(tls));
    }

    private Task<(TcpClient Client, SslStream Stream, MessageReader Reader)> ConnectWifiAsync() =>
        ConnectSecureAsync(_server.WifiPort);

    private Task<(TcpClient Client, SslStream Stream, MessageReader Reader)> ConnectUsbAsync() =>
        ConnectSecureAsync(_server.UsbEndPoint.Port);

    /// <summary>Conecta na porta USB (TLS) e já manda o AUTH de um aparelho recém-adicionado.</summary>
    private async Task<(TcpClient Client, SslStream Stream, MessageReader Reader)> ConnectUsbAuthedAsync()
    {
        var (_, token) = _devices.Add("Pixel 8");
        var connection = await ConnectUsbAsync();
        await SendAsync(connection.Stream, new AuthMessage(token));
        return connection;
    }

    private Task SendAsync(Stream stream, Message message) =>
        stream.WriteAsync(MessageCodec.Encode(message), _cts.Token).AsTask();

    /// <summary>O PC fecha: a leitura termina (null) ou a conexão cai (IOException). Os PINGs do PC antes disso não contam.</summary>
    private async Task AssertClosedAsync(MessageReader reader)
    {
        Message? message = null;
        var error = await Record.ExceptionAsync(async () => message = await ReadSkippingPingsAsync(reader));
        Assert.Null(message);
        Assert.True(error is null or IOException, $"erro inesperado: {error}");
    }

    /// <summary>A próxima mensagem que não seja o PING periódico do PC.</summary>
    private async Task<Message?> ReadSkippingPingsAsync(MessageReader reader)
    {
        while (true)
        {
            var message = await reader.ReadAsync(_cts.Token);
            if (message is not PingMessage) return message;
        }
    }

    [Fact]
    public void SanitizeStripsControlCharacters()
    {
        Assert.Equal("Pixel X", HostServer.Sanitize("Pixel \u001bX\r\n\t"));
    }

    [Fact]
    public async Task Pairing_then_auth_then_hello_gets_config()
    {
        var secret = _pairing.Begin();
        var (client, stream, reader) = await ConnectWifiAsync();
        using var _ = client;

        await SendAsync(stream, new PairMessage(secret, "Pixel 8"));
        var token = Assert.IsType<PairedMessage>(await reader.ReadAsync(_cts.Token)).Token;
        await SendAsync(stream, new AuthMessage(token));
        await SendAsync(stream, Hello());

        var config = Assert.IsType<ConfigMessage>(await reader.ReadAsync(_cts.Token));
        Assert.Equal(2400, config.Width);
        Assert.Equal("Pixel 8", Assert.Single(_devices.Devices).Name);
        Assert.NotNull(_devices.Authenticate(token));
    }

    [Fact]
    public async Task Paired_device_reconnects_with_its_token_and_pings()
    {
        var (_, token) = _devices.Add("Pixel 8");
        var (client, stream, reader) = await ConnectWifiAsync();
        using var _ = client;

        await SendAsync(stream, new AuthMessage(token));
        await SendAsync(stream, Hello());
        Assert.IsType<ConfigMessage>(await reader.ReadAsync(_cts.Token));
        await SendAsync(stream, new PingMessage(123456789));

        Assert.Equal(new PongMessage(123456789), await ReadSkippingPingsAsync(reader));
    }

    [Fact]
    public async Task Wrong_pairing_secret_is_denied_and_nothing_is_registered()
    {
        _pairing.Begin();
        var (client, stream, reader) = await ConnectWifiAsync();
        using var _ = client;

        await SendAsync(stream, new PairMessage(RandomNumberGenerator.GetBytes(32), "Intruso"));

        Assert.Equal(new DeniedMessage(DeniedReason.InvalidPairingSecret), await reader.ReadAsync(_cts.Token));
        await AssertClosedAsync(reader);
        Assert.Empty(_devices.Devices);
    }

    [Fact]
    public async Task Unknown_token_is_denied()
    {
        var (client, stream, reader) = await ConnectWifiAsync();
        using var _ = client;

        await SendAsync(stream, new AuthMessage(RandomNumberGenerator.GetBytes(32)));

        Assert.Equal(new DeniedMessage(DeniedReason.UnknownDevice), await reader.ReadAsync(_cts.Token));
        await AssertClosedAsync(reader);
    }

    [Fact]
    public async Task Removed_device_is_denied()
    {
        var (device, token) = _devices.Add("Pixel 8");
        _devices.Remove(device.Id);
        var (client, stream, reader) = await ConnectWifiAsync();
        using var _ = client;

        await SendAsync(stream, new AuthMessage(token));

        Assert.Equal(new DeniedMessage(DeniedReason.UnknownDevice), await reader.ReadAsync(_cts.Token));
        await AssertClosedAsync(reader);
    }

    [Fact]
    public async Task Removed_device_sending_auth_and_hello_separately_still_gets_denied()
    {
        var (device, token) = _devices.Add("Pixel 8");
        _devices.Remove(device.Id);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var (client, stream, reader) = await ConnectWifiAsync();
            using (client)
            {
                // o app manda AUTH e HELLO em escritas separadas (dois registros TLS)
                await SendAsync(stream, new AuthMessage(token));
                await SendAsync(stream, Hello());

                Assert.Equal(new DeniedMessage(DeniedReason.UnknownDevice), await reader.ReadAsync(_cts.Token));
            }
        }
    }

    [Fact]
    public async Task Registry_write_failure_during_pairing_does_not_kill_the_wifi_listener()
    {
        var (_, token) = _devices.Add("Pixel 8");
        var registryFile = Path.Combine(_dir, "paired-devices.json");
        File.SetAttributes(registryFile, FileAttributes.ReadOnly); // Add vai falhar ao gravar
        try
        {
            var secret = _pairing.Begin();
            var (broken, brokenStream, brokenReader) = await ConnectWifiAsync();
            using (broken)
            {
                await SendAsync(brokenStream, new PairMessage(secret, "Pixel 9"));
                await AssertClosedAsync(brokenReader);
            }

            // a porta Wi-Fi continua atendendo
            var (client, stream, reader) = await ConnectWifiAsync();
            using var _ = client;
            await SendAsync(stream, new AuthMessage(token));
            await SendAsync(stream, Hello());
            Assert.IsType<ConfigMessage>(await reader.ReadAsync(_cts.Token));
        }
        finally
        {
            File.SetAttributes(registryFile, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task Hello_without_auth_on_wifi_is_denied()
    {
        var (client, stream, reader) = await ConnectWifiAsync();
        using var _ = client;

        await SendAsync(stream, Hello());

        Assert.Equal(new DeniedMessage(DeniedReason.UnknownDevice), await reader.ReadAsync(_cts.Token));
        await AssertClosedAsync(reader);
    }

    [Fact]
    public async Task Usb_port_requires_tls_and_auth_then_serves_hello()
    {
        var (client, stream, reader) = await ConnectUsbAuthedAsync();
        using var _ = client;

        await SendAsync(stream, Hello());
        Assert.IsType<ConfigMessage>(await reader.ReadAsync(_cts.Token));
        await SendAsync(stream, new PingMessage(42));

        Assert.Equal(new PongMessage(42), await ReadSkippingPingsAsync(reader));
    }

    [Fact]
    public async Task Usb_port_rejects_plain_tcp_hello()
    {
        using (var plain = new TcpClient())
        {
            await plain.ConnectAsync(IPAddress.Loopback, _server.UsbEndPoint.Port, _cts.Token);
            await plain.GetStream().WriteAsync(MessageCodec.Encode(Hello()), _cts.Token);
            Message? reply = null;
            var error = await Record.ExceptionAsync(async () => reply = await new MessageReader(plain.GetStream()).ReadAsync(_cts.Token));
            // o servidor só derruba depois do prazo de handshake (2 s no fixture); alerta TLS vira ProtocolException
            Assert.True(reply is not (ConfigMessage or DeniedMessage), $"resposta inesperada: {reply}");
            Assert.True(error is null or IOException or ProtocolException, $"erro inesperado: {error}");
        }

        // a porta continua servindo um cliente de verdade
        var (client, stream, reader) = await ConnectUsbAuthedAsync();
        using var _ = client;
        await SendAsync(stream, Hello());
        Assert.IsType<ConfigMessage>(await reader.ReadAsync(_cts.Token));
    }

    [Fact]
    public async Task Usb_port_denies_hello_without_auth()
    {
        var (client, stream, reader) = await ConnectUsbAsync();
        using var _ = client;

        await SendAsync(stream, Hello());

        Assert.Equal(new DeniedMessage(DeniedReason.UnknownDevice), await reader.ReadAsync(_cts.Token));
        await AssertClosedAsync(reader);
        Assert.Empty(_devices.Devices);
    }

    [Fact]
    public async Task Pairing_over_usb_works()
    {
        var secret = _pairing.Begin();
        var (client, stream, reader) = await ConnectUsbAsync();
        using var _ = client;

        await SendAsync(stream, new PairMessage(secret, "Pixel 8"));
        var token = Assert.IsType<PairedMessage>(await reader.ReadAsync(_cts.Token)).Token;
        await SendAsync(stream, new AuthMessage(token));
        await SendAsync(stream, Hello());

        Assert.IsType<ConfigMessage>(await reader.ReadAsync(_cts.Token));
        Assert.Equal("Pixel 8", Assert.Single(_devices.Devices).Name);
    }

    [Fact]
    public void Usb_port_listens_only_on_loopback() =>
        Assert.Equal(IPAddress.Loopback, _server.UsbEndPoint.Address);

    [Fact]
    public async Task Old_app_hello_v1_on_usb_is_denied_with_incompatible_version()
    {
        var (client, stream, reader) = await ConnectUsbAuthedAsync();
        using var _ = client;

        await SendAsync(stream, Hello(version: 1));

        Assert.Equal(new DeniedMessage(DeniedReason.IncompatibleVersion), await reader.ReadAsync(_cts.Token));
        await AssertClosedAsync(reader);
        Assert.Equal(0, _monitors.Acquired); // versão errada: nenhum monitor é ligado
    }

    [Fact]
    public async Task Old_app_plain_tcp_on_wifi_port_gets_no_session_and_server_keeps_working()
    {
        using (var plain = new TcpClient())
        {
            await plain.ConnectAsync(IPAddress.Loopback, _server.WifiPort, _cts.Token);
            await plain.GetStream().WriteAsync(MessageCodec.Encode(Hello(version: 1)), _cts.Token);
            Message? reply = null;
            var error = await Record.ExceptionAsync(async () => reply = await new MessageReader(plain.GetStream()).ReadAsync(_cts.Token));
            // o servidor só derruba depois do prazo de handshake (2 s no fixture); alerta TLS vira ProtocolException
            Assert.True(reply is not (ConfigMessage or DeniedMessage), $"resposta inesperada: {reply}");
            Assert.True(error is null or IOException or ProtocolException, $"erro inesperado: {error}");
        }

        var (_, token) = _devices.Add("Pixel 8");
        var (client, stream, reader) = await ConnectWifiAsync();
        using var _ = client;
        await SendAsync(stream, new AuthMessage(token));
        await SendAsync(stream, Hello());
        Assert.IsType<ConfigMessage>(await reader.ReadAsync(_cts.Token));
    }

    [Fact]
    public async Task Silent_peer_on_wifi_port_is_dropped_after_the_handshake_timeout()
    {
        using (var silent = new TcpClient())
        {
            await silent.ConnectAsync(IPAddress.Loopback, _server.WifiPort, _cts.Token);
            var read = await silent.GetStream().ReadAsync(new byte[1], _cts.Token); // o PC desiste e fecha

            Assert.Equal(0, read);
        }

        var (_, token) = _devices.Add("Pixel 8");
        var (client, stream, reader) = await ConnectWifiAsync();
        using var _ = client;
        await SendAsync(stream, new AuthMessage(token));
        await SendAsync(stream, Hello());
        Assert.IsType<ConfigMessage>(await reader.ReadAsync(_cts.Token));
    }

    [Fact]
    public async Task Silent_client_after_config_is_dropped_after_the_idle_timeout_and_a_new_one_is_served()
    {
        var (silent, silentStream, silentReader) = await ConnectUsbAuthedAsync();
        using (silent)
        {
            await SendAsync(silentStream, Hello());
            Assert.IsType<ConfigMessage>(await silentReader.ReadAsync(_cts.Token));

            await AssertClosedAsync(silentReader); // o aparelho sumiu: sem nada por 2 s, o PC derruba a conexão
        }

        var (client, stream, reader) = await ConnectUsbAuthedAsync();
        using var _ = client;
        await SendAsync(stream, Hello());

        Assert.IsType<ConfigMessage>(await reader.ReadAsync(_cts.Token));
    }

    [Fact]
    public async Task Server_accepts_a_new_usb_client_after_the_previous_one_disconnects()
    {
        var first = await ConnectUsbAuthedAsync();
        await SendAsync(first.Stream, Hello());
        Assert.IsType<ConfigMessage>(await first.Reader.ReadAsync(_cts.Token));
        first.Client.Dispose();

        var (client, stream, reader) = await ConnectUsbAuthedAsync();
        using var _ = client;
        await SendAsync(stream, Hello());

        Assert.IsType<ConfigMessage>(await reader.ReadAsync(_cts.Token));
    }

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

    /// <summary>Cabo + AUTH + HELLO com o vídeo falso ligado; devolve o stream que a sessão começou.</summary>
    private async Task<(TcpClient Client, SslStream Stream, MessageReader Reader, FakeVideoStream Video)> ConnectWithVideoAsync()
    {
        _video.Enabled = true;
        var (client, stream, reader) = await ConnectUsbAuthedAsync();
        var before = _video.Started.Count;
        await SendAsync(stream, Hello());
        await WaitUntilAsync(() => _video.Started.Count == before + 1);
        return (client, stream, reader, _video.Started[^1]);
    }

    [Fact]
    public async Task Pc_pings_with_its_own_clock_after_the_config()
    {
        var (client, _, reader, _) = await ConnectWithHelloAsync();
        using var _ = client;
        var afterConfig = PcClock.NowUs;

        var first = Assert.IsType<PingMessage>(await reader.ReadAsync(_cts.Token));
        var second = Assert.IsType<PingMessage>(await reader.ReadAsync(_cts.Token));

        Assert.InRange(first.TimestampUs, afterConfig, PcClock.NowUs);
        Assert.True(second.TimestampUs - first.TimestampUs >= 100_000, "o fixture pinga a cada 200 ms");
    }

    [Fact]
    public async Task Video_config_and_frames_reach_the_phone_in_order()
    {
        var (client, _, reader, video) = await ConnectWithVideoAsync();
        using var _ = client;
        var config = new ConfigMessage(2400, 1080, VideoCodec.H265, 25_000, [0, 0, 0, 1, 0x40, 0x01]);

        video.Output.OnConfig(config);
        video.Output.OnFrame(new FrameMessage(10, true, [0, 0, 0, 1, 0x26, 0x01]));
        video.Output.OnFrame(new FrameMessage(20, false, [0, 0, 0, 1, 0x02, 0x01]));

        var received = Assert.IsType<ConfigMessage>(await ReadSkippingPingsAsync(reader));
        Assert.Equal(VideoCodec.H265, received.Codec);
        Assert.Equal(config.CodecConfig, received.CodecConfig);
        var key = Assert.IsType<FrameMessage>(await ReadSkippingPingsAsync(reader));
        var p = Assert.IsType<FrameMessage>(await ReadSkippingPingsAsync(reader));
        Assert.Equal((10UL, true), (key.TimestampUs, key.IsKeyframe));
        Assert.Equal((20UL, false), (p.TimestampUs, p.IsKeyframe));
    }

    [Fact]
    public async Task Video_request_carries_the_monitor_the_phone_codecs_and_the_link()
    {
        var monitor = new VirtualMonitor(@"\\.\DISPLAY9", 3440, 0, 2400, 1080, 175);
        _monitors.Monitor = monitor;

        var (client, _, _, video) = await ConnectWithVideoAsync();
        using var _ = client;

        Assert.Equal(monitor, video.Request.Monitor.Current);
        Assert.Equal(VideoCodec.H264 | VideoCodec.H265, video.Request.PhoneCodecs);
        Assert.Equal(VideoLink.Usb, video.Request.Link);
    }

    [Fact]
    public async Task Keyframe_request_from_the_phone_reaches_the_video()
    {
        var (client, stream, _, video) = await ConnectWithVideoAsync();
        using var _ = client;

        await SendAsync(stream, new KeyframeRequestMessage());

        await WaitUntilAsync(() => video.KeyframeRequests == 1);
    }

    [Fact]
    public async Task Video_that_sends_nothing_gets_the_fallback_config()
    {
        var (client, _, reader, _) = await ConnectWithVideoAsync();
        using var _ = client;

        var config = Assert.IsType<ConfigMessage>(await ReadSkippingPingsAsync(reader)); // depois de 500 ms no fixture

        Assert.Equal((2400, 1080), (config.Width, config.Height));
        Assert.Empty(config.CodecConfig);
    }

    [Fact]
    public async Task Phone_that_stops_reading_makes_the_queue_drop_and_ask_for_a_keyframe()
    {
        var (client, _, _, video) = await ConnectWithVideoAsync();
        using var _ = client;
        video.Output.OnConfig(new ConfigMessage(2400, 1080, VideoCodec.H264, 25_000, []));
        var maxPending = 0;

        // O celular não lê nada: os buffers do TLS e do TCP enchem e o escritor para de andar.
        for (var i = 0; i < 300 && video.KeyframeRequests == 0; i++)
        {
            video.Output.OnFrame(new FrameMessage((ulong)i + 1, i == 0, new byte[1024 * 1024]));
            maxPending = Math.Max(maxPending, video.Output.PendingFrames);
            await Task.Delay(1, _cts.Token);
        }

        Assert.True(video.KeyframeRequests >= 1, "a fila não pediu keyframe");
        Assert.True(maxPending <= 2, $"a fila chegou a {maxPending} quadros");
    }

    [Fact]
    public async Task Video_stops_before_the_monitor_is_released()
    {
        var events = new List<string>();
        _video.OnEvent = e =>
        {
            lock (events) events.Add(e);
        };
        _monitors.OnRelease = () =>
        {
            lock (events) events.Add("monitor liberado");
        };
        _monitors.Monitor = new VirtualMonitor(@"\\.\DISPLAY9", 3440, 0, 2400, 1080, 175);
        var (client, _, _, _) = await ConnectWithVideoAsync();

        client.Dispose();
        await WaitUntilAsync(() => _monitors.Released == 1);

        lock (events) Assert.Equal(["vídeo encerrado", "monitor liberado"], events);
    }

    [Fact]
    public async Task New_session_takes_over_the_video()
    {
        _video.Enabled = true;
        var (_, token) = _devices.Add("Pixel 8");
        var (wifi, wifiStream, _) = await ConnectWifiAsync();
        using var _ = wifi;
        await SendAsync(wifiStream, new AuthMessage(token));
        await SendAsync(wifiStream, Hello());
        await WaitUntilAsync(() => _video.Started.Count == 1);

        var (usb, _, _, _) = await ConnectWithVideoAsync();
        using var __ = usb;

        Assert.True(_video.Started[0].Disposed);
        Assert.False(_video.Started[1].Disposed);
    }

    [Fact]
    public async Task Phone_is_answered_while_the_video_is_still_starting_and_gets_the_fallback_in_time()
    {
        _video.Enabled = true;
        _video.StartGate = new TaskCompletionSource();
        var (client, stream, reader) = await ConnectUsbAuthedAsync();
        using var _ = client;
        await SendAsync(stream, Hello());

        await SendAsync(stream, new PingMessage(42));

        Assert.Equal(new PongMessage(42), await ReadSkippingPingsAsync(reader)); // o vídeo ainda não abriu
        var config = Assert.IsType<ConfigMessage>(await ReadSkippingPingsAsync(reader)); // fallback em 500 ms desde o HELLO
        Assert.Empty(config.CodecConfig);
        _video.StartGate.SetResult();
    }

    [Fact]
    public async Task Keyframe_request_while_the_video_is_starting_reaches_it_once_it_starts()
    {
        _video.Enabled = true;
        _video.StartGate = new TaskCompletionSource();
        var (client, stream, reader) = await ConnectUsbAuthedAsync();
        using var _ = client;
        await SendAsync(stream, Hello());
        await SendAsync(stream, new KeyframeRequestMessage());
        await SendAsync(stream, new PingMessage(7));
        Assert.Equal(new PongMessage(7), await ReadSkippingPingsAsync(reader)); // o KEYFRAME_REQ já foi lido

        _video.StartGate.SetResult();

        await WaitUntilAsync(() => _video.Started.Count == 1 && _video.Started[0].KeyframeRequests == 1);
    }

    [Fact]
    public async Task Phone_that_leaves_while_the_video_is_starting_ends_the_session()
    {
        _video.Enabled = true;
        _video.StartGate = new TaskCompletionSource(); // a abertura nunca termina sozinha
        _monitors.Monitor = new VirtualMonitor(@"\\.\DISPLAY9", 3440, 0, 2400, 1080, 175);
        var (client, stream, _) = await ConnectUsbAuthedAsync();
        await SendAsync(stream, Hello());
        await WaitUntilAsync(() => _video.Started.Count == 1);

        client.Dispose();

        await WaitUntilAsync(() => _monitors.Released == 1);
    }

    [Fact]
    public void Ping_interval_must_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HostServer(0, 0, _identity, _pairing, _devices, pingInterval: TimeSpan.Zero));
    }

    [Fact]
    public async Task Video_that_fails_to_start_gets_the_fallback_config()
    {
        _video.ThrowOnStart = new InvalidOperationException("falha falsa ao abrir");
        var (client, stream, reader) = await ConnectUsbAuthedAsync();
        using var _ = client;

        await SendAsync(stream, Hello());

        Assert.Empty(Assert.IsType<ConfigMessage>(await ReadSkippingPingsAsync(reader)).CodecConfig);
    }

    [Fact]
    public async Task Video_that_fails_to_stop_still_ends_the_session_and_releases_the_monitor()
    {
        _video.ThrowOnDispose = true;
        _monitors.Monitor = new VirtualMonitor(@"\\.\DISPLAY9", 3440, 0, 2400, 1080, 175);
        var (client, _, _, video) = await ConnectWithVideoAsync();

        client.Dispose();

        await WaitUntilAsync(() => _monitors.Released == 1);
        Assert.True(video.Disposed);
    }

    private sealed class FakeMonitorManager : IVirtualMonitorManager
    {
        private int _acquired;
        private int _released;

        public VirtualMonitor? Monitor { get; set; }
        public List<(int Width, int Height, int Dpi)> Requests { get; } = [];
        public int Acquired => _acquired;
        public int Released => _released;
        public Action? OnRelease { get; set; }

        public VirtualMonitorLease Acquire(int width, int height, int densityDpi)
        {
            lock (Requests) Requests.Add((width, height, densityDpi));
            Interlocked.Increment(ref _acquired);
            return new VirtualMonitorLease(MonitorRequest.Normalize(width, height, densityDpi), Monitor, () =>
            {
                OnRelease?.Invoke();
                Interlocked.Increment(ref _released);
            });
        }
    }
}
```

Run: `dotnet test host/ScreenShare.slnx --filter FullyQualifiedName~HostServerTests`
Expected: erro de compilação (o `HostServer` não aceita `video`, `pingInterval` nem `videoConfigTimeout`).

- [ ] **Step 5: `HostServer`**

Substitua `host/ScreenShare.DevHost/HostServer.cs` pelo conteúdo abaixo. O que muda:
- o vídeo e os intervalos entram pelo construtor;
- `VideoLink` vem da porta;
- `ServeSessionAsync` cria o escritor e já começa a leitura, o PING e o prazo do fallback; só depois espera o vídeo abrir (o celular é atendido enquanto isso, e o prazo de 2 s conta desde o HELLO);
- a abertura do vídeo (`AttachVideoAsync`) corre junto com a leitura e a escrita, com cancelamento próprio: se o celular sai enquanto ela demora, ela é cancelada, e um vídeo que termina de abrir nessa hora ainda é encerrado;
- `SessionVideo` guarda um pedido de keyframe que chega antes de o vídeo começar e o entrega quando ele começa; o `KeyframeNeeded` é assinado antes do vídeo;
- vídeo que falha ao abrir vira sessão sem vídeo (CONFIG de fallback); falha ao encerrar o vídeo é registrada e não impede o resto do encerramento;
- o laço de leitura vira `ReadLoopAsync`;
- o que terminar primeiro, leitura ou escrita, encerra a sessão;
- a ordem de encerramento é vídeo → escritor → monitor (o `using var lease` vem antes de tudo e é o último a sair).

```csharp
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using ScreenShare.Core.Protocol;
using ScreenShare.Core.Security;
using ScreenShare.Core.Video;
using ScreenShare.Display;
using ScreenShare.Video;

namespace ScreenShare.DevHost;

/// <summary>
/// Servidor de desenvolvimento: liga o monitor virtual e o vídeo por sessão. As duas portas exigem TLS e depois PAIR/AUTH
/// antes do HELLO. A porta USB escuta só em loopback (o `adb reverse` chega por ali). Responde PING com PONG e manda o
/// próprio PING a cada segundo. Atende um cliente por vez em cada porta; um cliente mudo é derrubado por prazo
/// (handshake e ociosidade). O vídeo é de uma sessão por vez: a mais nova assume.
/// </summary>
public sealed class HostServer : IDisposable
{
    private const uint StubBitrateKbps = 8000;

    /// <summary>Depois de um DENIED o PC espera o cliente terminar de falar por no máximo isto antes de fechar.</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(1);

    private readonly TcpListener _wifi;
    private readonly TcpListener _usb;
    private readonly HostIdentity _identity;
    private readonly PairingSession _pairing;
    private readonly DeviceRegistry _devices;
    private readonly Action<string>? _log;
    private readonly TimeSpan _handshakeTimeout;
    private readonly TimeSpan _idleTimeout;
    private readonly IVirtualMonitorManager _monitors;
    private readonly IVideoSource? _video;
    private readonly TimeSpan _pingInterval;
    private readonly TimeSpan _videoConfigTimeout;

    /// <param name="handshakeTimeout">Prazo para TLS + PAIR/AUTH em qualquer porta (padrão 10 s).</param>
    /// <param name="idleTimeout">
    /// Silêncio máximo numa sessão, em qualquer porta (padrão 10 s). O app manda PING a cada segundo,
    /// então 10 s sem nada é conexão morta (celular sem Wi-Fi, fora de alcance) e não pode prender a porta.
    /// </param>
    /// <param name="monitors">Monitor virtual por sessão (padrão: nenhum; o CONFIG leva a resolução pedida pelo celular, já normalizada).</param>
    /// <param name="video">Vídeo por sessão (padrão: nenhum; o celular recebe só o CONFIG de fallback).</param>
    /// <param name="pingInterval">Intervalo do PING do PC depois do CONFIG (padrão 1 s).</param>
    /// <param name="videoConfigTimeout">Sem CONFIG do vídeo nesse prazo, sai o de fallback (padrão 2 s).</param>
    public HostServer(int wifiPort, int usbPort, HostIdentity identity, PairingSession pairing, DeviceRegistry devices,
        Action<string>? log = null, TimeSpan? handshakeTimeout = null, TimeSpan? idleTimeout = null,
        IVirtualMonitorManager? monitors = null, IVideoSource? video = null, TimeSpan? pingInterval = null,
        TimeSpan? videoConfigTimeout = null)
    {
        _wifi = new TcpListener(IPAddress.Any, wifiPort);
        _usb = new TcpListener(IPAddress.Loopback, usbPort);
        _identity = identity;
        _pairing = pairing;
        _devices = devices;
        _log = log;
        _handshakeTimeout = handshakeTimeout ?? TimeSpan.FromSeconds(10);
        _idleTimeout = idleTimeout ?? TimeSpan.FromSeconds(10);
        _monitors = monitors ?? NullVirtualMonitorManager.Instance;
        _video = video is null ? null : new ExclusiveVideoSource(video);
        _pingInterval = pingInterval ?? TimeSpan.FromSeconds(1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_pingInterval, TimeSpan.Zero, nameof(pingInterval));
        _videoConfigTimeout = videoConfigTimeout ?? TimeSpan.FromSeconds(2);
    }

    /// <summary>Porta Wi-Fi (TLS) em que está escutando.</summary>
    public int WifiPort => ((IPEndPoint)_wifi.LocalEndpoint).Port;

    /// <summary>Endereço da porta USB: sempre 127.0.0.1.</summary>
    public IPEndPoint UsbEndPoint => (IPEndPoint)_usb.LocalEndpoint;

    public void Start()
    {
        _wifi.Start();
        _usb.Start();
    }

    /// <summary>Atende as duas portas até o token ser cancelado.</summary>
    public Task RunAsync(CancellationToken cancellationToken) => Task.WhenAll(
        AcceptLoopAsync(_wifi, "Wi-Fi", VideoLink.Wifi, cancellationToken),
        AcceptLoopAsync(_usb, "USB", VideoLink.Usb, cancellationToken));

    private async Task AcceptLoopAsync(TcpListener listener, string portLabel, VideoLink link, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(cancellationToken);
                try
                {
                    client.NoDelay = true;
                    // Buffer de envio fixo: o ajuste automático do Windows cresce até vários MB e esconde a rede travada
                    // (os quadros envelheceriam lá dentro). Com 512 KiB, a fila do SessionWriter percebe e descarta até um IDR.
                    client.Client.SendBufferSize = 512 * 1024;
                    _log?.Invoke($"Cliente conectado ({portLabel}): {client.Client.RemoteEndPoint}");
                    await ServeSecureAsync(client.GetStream(), link, cancellationToken);
                }
                catch (Exception e) when (!(e is OperationCanceledException && cancellationToken.IsCancellationRequested))
                {
                    // Qualquer falha ao atender UM cliente (rede, TLS, protocolo, prazo estourado, disco ao gravar o
                    // registro...) encerra só essa conexão: a porta continua atendendo os próximos.
                    _log?.Invoke($"Conexão encerrada: {e.Message}");
                }
                _log?.Invoke("Cliente desconectado.");
            }
        }
        catch (OperationCanceledException)
        {
            // encerramento normal
        }
    }

    private async Task ServeSecureAsync(NetworkStream network, VideoLink link, CancellationToken cancellationToken)
    {
        await using var tls = new SslStream(network, leaveInnerStreamOpen: false);
        // TLS e PAIR/AUTH precisam terminar dentro do prazo: um cliente calado não pode prender a porta.
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshake.CancelAfter(_handshakeTimeout);

        await tls.AuthenticateAsServerAsync(
            new SslServerAuthenticationOptions
            {
                ServerCertificate = _identity.Certificate,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, // spec: TLS 1.2 ou superior
            }, handshake.Token);
        var reader = new MessageReader(tls);
        var first = await reader.ReadAsync(handshake.Token);

        if (first is PairMessage pair)
        {
            if (!_pairing.TryConsume(pair.Secret))
            {
                await DenyAsync(tls, DeniedReason.InvalidPairingSecret, cancellationToken);
                return;
            }
            var (device, token) = _devices.Add(pair.DeviceName);
            _log?.Invoke($"Aparelho pareado: {Sanitize(device.Name)} (id {device.Id})");
            await SendAsync(tls, new PairedMessage(token), cancellationToken);
            first = await reader.ReadAsync(handshake.Token);
        }

        if (first is null) return; // cliente fechou
        if (first is not AuthMessage auth || _devices.Authenticate(auth.Token) is not { } known)
        {
            await DenyAsync(tls, DeniedReason.UnknownDevice, cancellationToken);
            return;
        }

        _log?.Invoke($"Autenticado: {Sanitize(known.Name)} (id {known.Id})");
        await ServeSessionAsync(tls, reader, link, cancellationToken);
    }

    /// <summary>
    /// HELLO → monitor → vídeo (ou CONFIG de fallback), PING do PC a cada intervalo e o laço de leitura, até o cliente
    /// sair, ficar mudo além do prazo ocioso ou a rede falhar. Daqui em diante só o SessionWriter escreve no stream.
    /// Encerramento: vídeo, escritor, monitor.
    /// </summary>
    private async Task ServeSessionAsync(Stream stream, MessageReader reader, VideoLink link, CancellationToken cancellationToken)
    {
        if (await ReadWithIdleDeadlineAsync(reader, cancellationToken) is not HelloMessage hello)
            return; // primeira mensagem não é HELLO: fecha

        if (hello.ProtocolVersion != MessageCodec.ProtocolVersion)
        {
            await DenyAsync(stream, DeniedReason.IncompatibleVersion, cancellationToken);
            return;
        }

        // O monitor vale enquanto a sessão durar; o using o libera em qualquer saída (fim, erro ou prazo).
        using var lease = _monitors.Acquire(hello.Width, hello.Height, hello.DensityDpi);
        if (lease.Monitor is { } monitor)
            _log?.Invoke($"Monitor virtual: {monitor.DeviceName} {monitor.Width}×{monitor.Height} em ({monitor.X},{monitor.Y}), escala {monitor.ScalePercent}%.");
        var fallback = new ConfigMessage((ushort)lease.Width, (ushort)lease.Height, VideoCodec.H264, StubBitrateKbps, []);

        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var writer = new SessionWriter(stream, () => PcClock.NowUs);
        var video = new SessionVideo(this);
        writer.KeyframeNeeded += video.RequestKeyframe; // assinado antes de o vídeo começar: nenhum descarte fica sem pedido
        var writing = writer.RunAsync(session.Token);
        // A leitura, o PING e o prazo do fallback começam já: enquanto o vídeo abre, o celular é atendido.
        var reading = ReadLoopAsync(reader, writer, video, session.Token);
        _ = PingAsync(writer, session.Token);
        // A abertura do vídeo tem cancelamento próprio: se o celular sair enquanto ela demora, ela é cancelada antes de
        // o vídeo ser encerrado, e o escritor só para depois.
        using var opening = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
        var starting = Task.CompletedTask;
        try
        {
            if (_video is null)
            {
                writer.SendFallbackConfig(fallback);
            }
            else
            {
                _ = SendFallbackLaterAsync(writer, fallback, session.Token);
                starting = AttachVideoAsync(new VideoRequest(lease, hello.SupportedCodecs, link), writer, video, fallback, opening.Token);
            }
            // O que terminar primeiro encerra a sessão: o cliente (fim, prazo, protocolo) ou o escritor (rede). A abertura
            // do vídeo terminar não encerra nada; a sessão segue esperando os outros dois.
            var first = await Task.WhenAny(reading, writing, starting);
            if (first == starting)
            {
                await starting;
                first = await Task.WhenAny(reading, writing);
            }
            await first;
        }
        finally
        {
            await opening.CancelAsync();
            await IgnoreErrorsAsync(starting); // um vídeo que terminou de abrir agora entra em video e é encerrado abaixo
            await video.StopAsync(); // nunca lança: o resto do encerramento sempre acontece
            await session.CancelAsync();
            await IgnoreErrorsAsync(writing);
            await IgnoreErrorsAsync(reading);
        }
    }

    /// <summary>Abre o vídeo e o liga à sessão; sem vídeo (falha ou sem encoder), o celular recebe o CONFIG de fallback.</summary>
    private async Task AttachVideoAsync(VideoRequest request, SessionWriter writer, SessionVideo video, ConfigMessage fallback,
        CancellationToken cancellationToken)
    {
        var started = await StartVideoAsync(request, new WriterOutput(writer), cancellationToken);
        video.Start(started);
        if (started is null) writer.SendFallbackConfig(fallback);
    }

    /// <summary>Começa o vídeo da sessão; uma falha vira sessão sem vídeo (o celular recebe o CONFIG de fallback).</summary>
    private async Task<IVideoStream?> StartVideoAsync(VideoRequest request, IVideoOutput output, CancellationToken cancellationToken)
    {
        try
        {
            return await _video!.StartAsync(request, output, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log?.Invoke($"Vídeo indisponível nesta sessão: {e.Message}");
            return null;
        }
    }

    /// <summary>PING → PONG, KEYFRAME_REQ → vídeo. Termina quando o cliente fecha; prazo estourado lança.</summary>
    private async Task ReadLoopAsync(MessageReader reader, SessionWriter writer, SessionVideo video, CancellationToken cancellationToken)
    {
        while (await ReadWithIdleDeadlineAsync(reader, cancellationToken) is { } message)
        {
            switch (message)
            {
                case PingMessage ping:
                    writer.Send(new PongMessage(ping.TimestampUs));
                    break;
                case KeyframeRequestMessage:
                    video.RequestKeyframe();
                    break;
                // demais mensagens (PONG, TOUCH) são ignoradas: ainda não há toque
            }
        }
    }

    /// <summary>PING do PC a cada intervalo, depois do primeiro CONFIG: o celular acerta o relógio e mede a latência com ele.</summary>
    private async Task PingAsync(SessionWriter writer, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_pingInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (writer.ConfigSent) writer.SendPing();
            }
        }
        catch (OperationCanceledException)
        {
            // fim da sessão
        }
    }

    /// <summary>O vídeo não mandou CONFIG a tempo (captura ainda abrindo, encoder travado): o celular sai da espera.</summary>
    private async Task SendFallbackLaterAsync(SessionWriter writer, ConfigMessage fallback, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_videoConfigTimeout, cancellationToken);
            if (writer.SendFallbackConfig(fallback))
                _log?.Invoke($"O vídeo não começou em {_videoConfigTimeout.TotalSeconds:0.#} s; o celular recebeu um CONFIG sem vídeo.");
        }
        catch (OperationCanceledException)
        {
            // fim da sessão
        }
    }

    /// <summary>Chamado pelo laço de leitura e pelo escritor (thread do encoder): nunca lança.</summary>
    private void RequestKeyframe(IVideoStream video)
    {
        try
        {
            video.RequestKeyframe();
        }
        catch (Exception e)
        {
            _log?.Invoke($"Falha ao pedir keyframe: {e.Message}");
        }
    }

    /// <summary>Espera uma tarefa da sessão que já foi cancelada; o erro dela já foi tratado (ou não importa mais).</summary>
    private static async Task IgnoreErrorsAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception)
        {
            // cancelada ou já propagada pela sessão
        }
    }

    /// <summary>
    /// O vídeo da sessão, que começa com o laço de leitura já rodando. Um pedido de keyframe que chega antes
    /// (KEYFRAME_REQ do celular, descarte na fila) fica guardado e vai assim que o vídeo começa. Nada aqui lança.
    /// </summary>
    private sealed class SessionVideo(HostServer server)
    {
        private readonly Lock _gate = new();
        private IVideoStream? _stream;
        private bool _pending;

        public void Start(IVideoStream? stream)
        {
            bool pending;
            lock (_gate)
            {
                _stream = stream;
                pending = _pending;
                _pending = false;
            }
            if (pending && stream is not null) server.RequestKeyframe(stream);
        }

        public void RequestKeyframe()
        {
            IVideoStream? stream;
            lock (_gate)
            {
                stream = _stream;
                if (stream is null) _pending = true;
            }
            if (stream is not null) server.RequestKeyframe(stream);
        }

        /// <summary>Encerra o vídeo; uma falha ao encerrar é registrada e não impede o resto do encerramento.</summary>
        public async Task StopAsync()
        {
            IVideoStream? stream;
            lock (_gate)
            {
                stream = _stream;
                _stream = null;
            }
            if (stream is null) return;
            try
            {
                await stream.DisposeAsync();
            }
            catch (Exception e)
            {
                server._log?.Invoke($"Falha ao encerrar o vídeo: {e.Message}");
            }
        }
    }

    /// <summary>O vídeo escreve pelo escritor da sessão.</summary>
    private sealed class WriterOutput(SessionWriter writer) : IVideoOutput
    {
        public void OnConfig(ConfigMessage config) => writer.SendVideoConfig(config);
        public void OnFrame(FrameMessage frame) => writer.SendVideoFrame(frame);
        public int PendingFrames => writer.PendingFrames;
    }

    /// <summary>
    /// Lê uma mensagem com prazo próprio (recriado a cada leitura). Se estourar, lança OperationCanceledException
    /// com o token externo ainda ativo, que o laço de aceitação trata como conexão morta.
    /// </summary>
    private async Task<Message?> ReadWithIdleDeadlineAsync(MessageReader reader, CancellationToken cancellationToken)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(_idleTimeout);
        return await reader.ReadAsync(idle.Token);
    }

    private static Task SendAsync(Stream stream, Message message, CancellationToken cancellationToken) =>
        stream.WriteAsync(MessageCodec.Encode(message), cancellationToken).AsTask();

    /// <summary>
    /// Envia DENIED e fecha com educação. Fechar de imediato com entrada ainda pendente (ex.: o HELLO que vem logo
    /// depois do AUTH) faz o Windows mandar RST, e o cliente descarta o DENIED que ainda não leu. Por isso: encerra o
    /// nosso lado (close_notify no TLS, FIN no TCP puro) e descarta o que o cliente ainda mandar, até o fim da
    /// conexão ou por no máximo <see cref="DrainTimeout"/>.
    /// </summary>
    private static async Task DenyAsync(Stream stream, DeniedReason reason, CancellationToken cancellationToken)
    {
        await SendAsync(stream, new DeniedMessage(reason), cancellationToken);

        using var drain = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        drain.CancelAfter(DrainTimeout);
        try
        {
            if (stream is SslStream tls) await tls.ShutdownAsync();

            var buffer = new byte[256];
            while (await stream.ReadAsync(buffer, drain.Token) > 0)
            {
                // descarta
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException)
        {
            // o DENIED já foi enviado; o cliente sumir ou demorar a fechar não importa
        }
    }

    /// <summary>Remove caracteres de controle (ex.: sequências ESC) de um nome vindo do celular antes de ir para o console.</summary>
    public static string Sanitize(string text) => string.Concat(text.Where(c => !char.IsControl(c)));

    public void Dispose()
    {
        _wifi.Stop();
        _usb.Stop();
    }
}
```

Run: `dotnet test host/ScreenShare.slnx --filter FullyQualifiedName~HostServerTests`
Expected: PASS (37 casos). Rode 3 vezes seguidas: nenhum teste pode falhar (há tempos de 200 e 500 ms envolvidos).

- [ ] **Step 6: Rodar tudo, commit e push**

Run: `dotnet build host/ScreenShare.slnx` e `dotnet test host/ScreenShare.slnx`
Expected: 0 avisos; 293 aprovados e 1 ignorado.

```bash
git add host/ScreenShare.slnx host/ScreenShare.Video host/ScreenShare.Core/Protocol/SessionWriter.cs host/ScreenShare.DevHost host/ScreenShare.Tests
git commit -m "feat(host): sessão com escritor único, PING do PC e vídeo de uma sessão por vez"
git push
```

---

### Task 6: pipeline de vídeo (lógica pura, testada com falsos)

**Por quê:** esta task concentra todas as regras de quando capturar, quando codificar e quando mandar keyframe, sem hardware. As Tasks 7 e 8 só ligam a placa de vídeo nas interfaces daqui. Os falsos respondem na hora e o relógio é falso (`FakeTimeProvider`), então os testes são determinísticos.

**Files:**
- Create: `host/ScreenShare.Video/Pipeline/Hardware.cs` (interfaces do hardware e exceções)
- Create: `host/ScreenShare.Video/Pipeline/FramePacer.cs`, `host/ScreenShare.Video/Pipeline/KeyframeScheduler.cs`, `host/ScreenShare.Video/Pipeline/CodecChoice.cs`
- Create: `host/ScreenShare.Video/Pipeline/VideoPipeline.cs`, `host/ScreenShare.Video/Pipeline/PipelineVideoSource.cs`
- Create: `host/ScreenShare.Video/VideoOptions.cs`
- Test: `host/ScreenShare.Tests/Video/PipelineFakes.cs`, `VideoPipelineTests.cs`, `PacingTests.cs`, `PipelineVideoSourceTests.cs` (todos em `host/ScreenShare.Tests/Video/`)

**Interfaces:**
- Consumes:
  - `IMonitorSource` (Task 4);
  - `IVideoSource`, `IVideoOutput`, `IVideoStream`, `VideoRequest`, `VideoLink` e `VideoStats` (Task 5);
  - `AnnexB.ExtractParameterSets` e `PcClock.NowUs` (Task 2);
  - os vetores `annexb/*.hex` (Task 1).
- Produces (as Tasks 7, 8, 9 e 13 usam), no namespace `ScreenShare.Video.Pipeline`:
  - `IVideoImage { int Width; int Height; }`;
  - `enum AcquireResult { NewImage, PointerOnly, Timeout }`;
  - as exceções `CaptureLostException`, `DeviceLostException` e `EncoderUnavailableException` (mensagem e exceção interna opcional);
  - `IScreenCapture : IDisposable { int Width; int Height; IVideoImage Last; AcquireResult TryAcquire(TimeSpan timeout, out ulong presentUs); }`;
  - `record EncoderSettings(VideoCodec Codec, int Width, int Height, int Fps, int BitrateKbps, int PeakBitrateKbps)`;
  - `record EncodedFrame(ulong TimestampUs, bool IsKeyframe, byte[] Data)`;
  - `IVideoEncoder : IDisposable { VideoCodec Codec; int Width; int Height; bool CanAccept; void Submit(IVideoImage, ulong timestampUs, bool forceKeyframe); event Action<EncodedFrame>? Output; event Action<Exception>? Failed; }`;
  - `IVideoBackend : IDisposable { VideoCodec HardwareCodecs; IScreenCapture OpenCapture(string deviceName); IVideoEncoder CreateEncoder(EncoderSettings); }`;
  - `CodecChooser.Choose(phone, host, forced)` e `CodecChooser.Name(codec)`; `CodecHealth`;
  - `PipelineVideoSource(Func<IVideoBackend> createBackend, VideoCodec hostCodecs, VideoOptions options, TimeProvider time, Action<string> log, Func<IDisposable>? prepareThread = null) : IVideoSource`;
  - `ScreenShare.Video.VideoOptions(int Fps = 60, int? BitrateMbps = null, VideoCodec Codec = VideoCodec.None)` com `BitrateKbpsFor(VideoLink)` (50 Mbps no cabo e 25 no Wi-Fi; o pico é o dobro).

**Regras que os testes fixam:**
- o stream começa por IDR → `CONFIG` (parâmetros tirados do IDR) → quadros;
- uma saída não-IDR no começo do stream é descartada e pede IDR;
- o ritmo é de no máximo `fps`, com folga para o vsync (60 Hz sai a 60 fps);
- só captura com `PendingFrames == 0` e `CanAccept`;
- tela parada: refinamentos em +100, +300 e +700 ms, e o IDR pedido sai da última imagem;
- no mínimo 200 ms entre IDRs, mas o pedido nunca se perde; o IDR do stream novo não espera;
- ACCESS_LOST: reabre em 100 ms e mantém o encoder; tamanho novo = encoder novo + `CONFIG` + IDR;
- `Changed` do monitor reabre a captura no nome novo;
- saída indisponível: espera de 100 ms, dobrando até 2 s, com `Refresh` do monitor, e a mesma mensagem não se repete no log;
- H.265 que falha ao abrir fica de fora até o host reiniciar (`CodecHealth`), mas um codec que **já produziu vídeo** nunca é marcado como falho: se ele não abre mais, a placa reiniciou, e o pipeline recria tudo;
- o encoder que falha no meio (evento `Failed`, `Submit` lança ou para de pedir entrada por 1 s) é recriado com espera crescente; um codec que nunca produziu vídeo é deixado de lado depois de 3 falhas seguidas;
- um IDR pedido que sai como P-frame é pedido de novo;
- erro inesperado no `Step` recomeça a captura e o encoder em vez de parar o vídeo;
- `Changed` do monitor não espera uma espera crescente em andamento;
- `DeviceLostException` (da captura ou do encoder, pelo evento `Failed`) recria o backend inteiro;
- saída de um encoder antigo é ignorada;
- timestamps estritamente crescentes.

- [ ] **Step 1: Interfaces e opções**

`host/ScreenShare.Video/Pipeline/Hardware.cs`:

```csharp
using ScreenShare.Core.Protocol;

namespace ScreenShare.Video.Pipeline;

/// <summary>Uma imagem capturada (no hardware de verdade, uma textura na placa de vídeo).</summary>
public interface IVideoImage
{
    int Width { get; }
    int Height { get; }
}

public enum AcquireResult
{
    /// <summary>A tela mudou: Last tem a imagem nova.</summary>
    NewImage,

    /// <summary>Só o ponteiro do mouse mudou (sem cursor no vídeo, é o mesmo que Timeout).</summary>
    PointerOnly,

    Timeout,
}

/// <summary>A captura parou (ACCESS_LOST, área de trabalho segura, saída sumida): reabrir com espera.</summary>
public sealed class CaptureLostException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A placa de vídeo foi removida ou reiniciada (ou a saída mudou de placa): recriar o backend inteiro.</summary>
public sealed class DeviceLostException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Não há encoder para este codec, ou ele não abriu.</summary>
public sealed class EncoderUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A captura de uma saída. Só a thread de captura usa.</summary>
public interface IScreenCapture : IDisposable
{
    int Width { get; }
    int Height { get; }

    /// <summary>A última imagem nova, numa cópia própria: vale até a próxima NewImage.</summary>
    IVideoImage Last { get; }

    /// <summary>
    /// Espera até timeout por uma imagem nova. presentUs = quando ela foi apresentada, no relógio do PC (µs), ou 0.
    /// Lança CaptureLostException (reabrir) ou DeviceLostException (recriar tudo).
    /// </summary>
    AcquireResult TryAcquire(TimeSpan timeout, out ulong presentUs);
}

public sealed record EncoderSettings(VideoCodec Codec, int Width, int Height, int Fps, int BitrateKbps, int PeakBitrateKbps);

/// <summary>Um access unit em Annex-B. Data é uma cópia, de quem recebe.</summary>
public sealed record EncodedFrame(ulong TimestampUs, bool IsKeyframe, byte[] Data);

public interface IVideoEncoder : IDisposable
{
    VideoCodec Codec { get; }
    int Width { get; }
    int Height { get; }

    /// <summary>O encoder pediu entrada: Submit pode ser chamado.</summary>
    bool CanAccept { get; }

    void Submit(IVideoImage image, ulong timestampUs, bool forceKeyframe);

    /// <summary>Saída codificada, na thread do encoder.</summary>
    event Action<EncodedFrame>? Output;

    event Action<Exception>? Failed;
}

/// <summary>A placa de vídeo: captura e encoder no mesmo device.</summary>
public interface IVideoBackend : IDisposable
{
    /// <summary>Codecs que têm encoder de hardware.</summary>
    VideoCodec HardwareCodecs { get; }

    /// <summary>Abre a captura da saída; CaptureLostException se ela não está disponível agora.</summary>
    IScreenCapture OpenCapture(string deviceName);

    /// <summary>EncoderUnavailableException se não há encoder ou ele não abre.</summary>
    IVideoEncoder CreateEncoder(EncoderSettings settings);
}
```

`host/ScreenShare.Video/VideoOptions.cs`:

```csharp
using ScreenShare.Core.Protocol;

namespace ScreenShare.Video;

/// <summary>Ajustes do vídeo vindos da linha de comando do DevHost. Codec None = automático.</summary>
public sealed record VideoOptions(int Fps = 60, int? BitrateMbps = null, VideoCodec Codec = VideoCodec.None)
{
    /// <summary>Bitrate médio: o pedido, ou 50 Mbps no cabo e 25 no Wi-Fi. O pico é o dobro.</summary>
    public int BitrateKbpsFor(VideoLink link) => (BitrateMbps ?? (link == VideoLink.Usb ? 50 : 25)) * 1000;
}
```

- [ ] **Step 2: Falsos e testes**

`host/ScreenShare.Tests/Video/PipelineFakes.cs`:

```csharp
using ScreenShare.Core.Protocol;
using ScreenShare.Display;
using ScreenShare.Tests.Protocol;
using ScreenShare.Video;
using ScreenShare.Video.Pipeline;

namespace ScreenShare.Tests.Video;

internal sealed class FakeImage(int width, int height) : IVideoImage
{
    public int Width { get; } = width;
    public int Height { get; } = height;
}

/// <summary>Captura falsa: a primeira imagem já está pronta; depois, a tela muda a cada Period (null = nunca).</summary>
internal sealed class FakeCapture(string deviceName, int width, int height, Func<TimeSpan> now) : IScreenCapture
{
    private TimeSpan? _nextChange = TimeSpan.Zero;

    public string DeviceName { get; } = deviceName;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public IVideoImage Last { get; } = new FakeImage(width, height);
    public TimeSpan? Period { get; init; }
    public Exception? ThrowOnAcquire { get; set; }
    public int Acquires { get; private set; }
    public bool Disposed { get; private set; }

    /// <summary>A tela muda agora: a próxima TryAcquire devolve imagem nova.</summary>
    public void Change() => _nextChange = now();

    public AcquireResult TryAcquire(TimeSpan timeout, out ulong presentUs)
    {
        Acquires++;
        presentUs = 0;
        if (ThrowOnAcquire is { } error) throw error;
        var t = now();
        if (_nextChange is not { } next || t < next) return AcquireResult.Timeout;
        if (Period is { } period)
        {
            do next += period;
            while (next <= t);
            _nextChange = next;
        }
        else
        {
            _nextChange = null;
        }
        return AcquireResult.NewImage;
    }

    public void Dispose() => Disposed = true;
}

/// <summary>Encoder falso: responde na hora, na mesma thread, com um IDR real (vetor) ou um P-frame pequeno.</summary>
internal sealed class FakeEncoder(EncoderSettings settings) : IVideoEncoder
{
    private static readonly byte[] H264Idr = Vectors.Load("annexb/h264-idr.hex");
    private static readonly byte[] H265Idr = Vectors.Load("annexb/h265-idr.hex");

    public EncoderSettings Settings { get; } = settings;
    public VideoCodec Codec => Settings.Codec;
    public int Width => Settings.Width;
    public int Height => Settings.Height;
    public bool CanAccept { get; set; } = true;

    /// <summary>Simula um encoder cuja primeira saída não é IDR.</summary>
    public bool FirstOutputIsP { get; set; }

    /// <summary>Quantos pedidos de IDR o encoder ainda vai ignorar (sai P-frame no lugar).</summary>
    public int IgnoreForce { get; set; }

    /// <summary>Simula um encoder que abre mas recusa todo quadro.</summary>
    public bool ThrowOnSubmit { get; set; }

    public List<(ulong Timestamp, bool Forced)> Submitted { get; } = [];
    public bool Disposed { get; private set; }

    public event Action<EncodedFrame>? Output;
    public event Action<Exception>? Failed;

    public static byte[] Idr(VideoCodec codec) => codec == VideoCodec.H265 ? H265Idr : H264Idr;

    public void Submit(IVideoImage image, ulong timestampUs, bool forceKeyframe)
    {
        Submitted.Add((timestampUs, forceKeyframe));
        if (ThrowOnSubmit) throw new InvalidOperationException("quadro recusado (falso)");
        var key = forceKeyframe && !(FirstOutputIsP && Submitted.Count == 1);
        if (key && IgnoreForce > 0)
        {
            IgnoreForce--;
            key = false;
        }
        byte[] p = Codec == VideoCodec.H265 ? [0, 0, 0, 1, 0x02, 0x01, 0xD0] : [0, 0, 0, 1, 0x41, 0x9A];
        Output?.Invoke(new EncodedFrame(timestampUs, key, key ? Idr(Codec) : p));
    }

    public void Fail(Exception error) => Failed?.Invoke(error);

    public void Emit(EncodedFrame frame) => Output?.Invoke(frame);

    public void Dispose() => Disposed = true;
}

internal sealed class FakeBackend : IVideoBackend
{
    public VideoCodec HardwareCodecs { get; set; } = VideoCodec.H264 | VideoCodec.H265;
    public Func<string, FakeCapture> CaptureFactory { get; set; } = name => throw new InvalidOperationException("sem captura");
    public Queue<Exception> OpenFailures { get; } = new();
    public VideoCodec FailingCodecs { get; set; }
    public Action<FakeEncoder>? EncoderSetup { get; set; }
    public List<string> Opened { get; } = [];
    public List<FakeCapture> Captures { get; } = [];
    public List<FakeEncoder> Encoders { get; } = [];
    public bool Disposed { get; private set; }

    public IScreenCapture OpenCapture(string deviceName)
    {
        Opened.Add(deviceName);
        if (OpenFailures.TryDequeue(out var error)) throw error;
        var capture = CaptureFactory(deviceName);
        Captures.Add(capture);
        return capture;
    }

    public IVideoEncoder CreateEncoder(EncoderSettings settings)
    {
        if ((FailingCodecs & settings.Codec) != 0) throw new EncoderUnavailableException("falso");
        var encoder = new FakeEncoder(settings);
        EncoderSetup?.Invoke(encoder);
        Encoders.Add(encoder);
        return encoder;
    }

    public void Dispose() => Disposed = true;
}

internal sealed class FakeOutput : IVideoOutput
{
    private readonly List<Message> _sent = [];

    public int PendingFrames { get; set; }

    public List<Message> Sent
    {
        get
        {
            lock (_sent) return [.. _sent];
        }
    }

    public List<ConfigMessage> Configs => [.. Sent.OfType<ConfigMessage>()];
    public List<FrameMessage> Frames => [.. Sent.OfType<FrameMessage>()];

    public void OnConfig(ConfigMessage config)
    {
        lock (_sent) _sent.Add(config);
    }

    public void OnFrame(FrameMessage frame)
    {
        lock (_sent) _sent.Add(frame);
    }
}

internal sealed class FakeMonitor(VirtualMonitor? current) : IMonitorSource
{
    public VirtualMonitor? Current { get; private set; } = current;
    public int Refreshes { get; private set; }

    public event Action? Changed;

    public VirtualMonitor? Refresh()
    {
        Refreshes++;
        return Current;
    }

    public void Change(VirtualMonitor monitor)
    {
        Current = monitor;
        Changed?.Invoke();
    }
}
```

`host/ScreenShare.Tests/Video/PacingTests.cs`:

```csharp
using ScreenShare.Video.Pipeline;

namespace ScreenShare.Tests.Video;

public sealed class PacingTests
{
    [Fact]
    public void Jittery_60hz_frames_all_go_out_at_60fps()
    {
        var pacer = new FramePacer(60);
        var sent = 0;

        for (var i = 0; i < 60; i++)
        {
            var now = TimeSpan.FromMilliseconds(i * 1000.0 / 60 + (i % 2 == 0 ? 2 : -2));
            if (pacer.Delay(now) != TimeSpan.Zero) continue;
            pacer.MarkSent(now);
            sent++;
        }

        Assert.Equal(60, sent);
    }

    [Fact]
    public void After_a_pause_the_next_frame_goes_out_at_once_and_the_cadence_restarts()
    {
        var pacer = new FramePacer(60);
        pacer.MarkSent(TimeSpan.Zero);

        Assert.True(pacer.Delay(TimeSpan.FromMilliseconds(5)) > TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, pacer.Delay(TimeSpan.FromSeconds(2)));
        pacer.MarkSent(TimeSpan.FromSeconds(2));
        Assert.True(pacer.Delay(TimeSpan.FromMilliseconds(2005)) > TimeSpan.Zero);
    }

    [Fact]
    public void Nothing_is_due_without_a_request()
    {
        Assert.False(new KeyframeScheduler().TakeDue(TimeSpan.Zero));
    }

    [Fact]
    public void Keyframe_requests_are_200ms_apart_joined_and_never_lost()
    {
        var scheduler = new KeyframeScheduler();
        scheduler.Request();
        Assert.True(scheduler.TakeDue(TimeSpan.FromMilliseconds(10)));

        scheduler.Request();
        scheduler.Request();
        Assert.False(scheduler.TakeDue(TimeSpan.FromMilliseconds(100)));
        Assert.True(scheduler.IsDue(TimeSpan.FromMilliseconds(210)));
        Assert.True(scheduler.TakeDue(TimeSpan.FromMilliseconds(210)));
        Assert.False(scheduler.TakeDue(TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public void RequestNow_ignores_the_minimum_interval()
    {
        var scheduler = new KeyframeScheduler();
        scheduler.Request();
        scheduler.TakeDue(TimeSpan.Zero);

        scheduler.RequestNow();

        Assert.True(scheduler.TakeDue(TimeSpan.FromMilliseconds(1)));
    }
}
```

`host/ScreenShare.Tests/Video/VideoPipelineTests.cs`:

```csharp
using Microsoft.Extensions.Time.Testing;
using ScreenShare.Core.Protocol;
using ScreenShare.Core.Video;
using ScreenShare.Display;
using ScreenShare.Tests.Protocol;
using ScreenShare.Video;
using ScreenShare.Video.Pipeline;

namespace ScreenShare.Tests.Video;

public sealed class VideoPipelineTests : IDisposable
{
    private static readonly VirtualMonitor Display5 = new(@"\\.\DISPLAY5", 3440, 0, 2520, 1080, 175);
    private static readonly TimeSpan Ms = TimeSpan.FromMilliseconds(1);

    private readonly FakeTimeProvider _time = new();
    private readonly long _t0;
    private readonly FakeBackend _backend = new();
    private readonly FakeMonitor _monitor = new(Display5);
    private readonly FakeOutput _output = new();
    private readonly List<string> _log = [];
    private VideoPipeline? _pipeline;
    private int _backendsCreated;

    public VideoPipelineTests()
    {
        _t0 = _time.GetTimestamp();
        _backend.CaptureFactory = name => new FakeCapture(name, 2520, 1080, () => Now);
    }

    public void Dispose() => _pipeline?.Dispose();

    private TimeSpan Now => _time.GetElapsedTime(_t0);

    /// <summary>Relógio do PC falso: 1 s + o tempo do relógio falso, em µs.</summary>
    private ulong ClockUs() => 1_000_000UL + (ulong)(Now.Ticks / 10);

    private VideoPipeline Create(VideoCodec phone = VideoCodec.H264 | VideoCodec.H265, VideoOptions? options = null,
        CodecHealth? health = null, Func<ulong>? clock = null)
    {
        _pipeline = new VideoPipeline(() =>
        {
            _backendsCreated++;
            return _backend;
        }, _monitor, phone, options ?? new VideoOptions(), 25_000, health ?? new CodecHealth(), _output, _time,
            clock ?? ClockUs, _log.Add);
        return _pipeline;
    }

    /// <summary>Um Step por milissegundo: o relógio falso anda 1 ms depois de cada passo.</summary>
    private void Run(VideoPipeline pipeline, TimeSpan duration)
    {
        for (var t = TimeSpan.Zero; t < duration; t += Ms)
        {
            pipeline.Step();
            _time.Advance(Ms);
        }
    }

    [Fact]
    public void First_output_is_a_config_with_the_parameter_sets_then_the_idr()
    {
        var pipeline = Create();

        Run(pipeline, 5 * Ms);

        var config = Assert.IsType<ConfigMessage>(_output.Sent[0]);
        Assert.Equal(2520, config.Width);
        Assert.Equal(1080, config.Height);
        Assert.Equal(VideoCodec.H265, config.Codec);
        Assert.Equal(25_000u, config.BitrateKbps);
        Assert.Equal(AnnexB.ExtractParameterSets(Vectors.Load("annexb/h265-idr.hex"), VideoCodec.H265), config.CodecConfig);
        Assert.True(Assert.IsType<FrameMessage>(_output.Sent[1]).IsKeyframe);
        Assert.True(_backend.Encoders[0].Submitted[0].Forced);
    }

    [Theory]
    [InlineData(60, 60)]
    [InlineData(30, 30)]
    public void Frames_follow_a_60hz_screen_up_to_the_fps_limit(int fps, int expected)
    {
        _backend.CaptureFactory = name => new FakeCapture(name, 2520, 1080, () => Now)
        {
            Period = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60),
        };
        var pipeline = Create(options: new VideoOptions(Fps: fps));

        Run(pipeline, TimeSpan.FromSeconds(1));

        Assert.InRange(_output.Frames.Count, expected - 1, expected + 1);
    }

    [Fact]
    public void Nothing_is_captured_while_a_frame_waits_for_the_network_or_the_encoder_is_busy()
    {
        var pipeline = Create();
        Run(pipeline, 5 * Ms);
        var capture = _backend.Captures[0];
        capture.Change();
        var acquires = capture.Acquires;

        _output.PendingFrames = 1;
        Run(pipeline, 50 * Ms);
        Assert.Equal(acquires, capture.Acquires);

        _output.PendingFrames = 0;
        _backend.Encoders[0].CanAccept = false;
        Run(pipeline, 50 * Ms);
        Assert.Equal(acquires, capture.Acquires);

        _backend.Encoders[0].CanAccept = true;
        Run(pipeline, 5 * Ms);
        Assert.Equal(2, _output.Frames.Count); // o IDR do começo e a mudança, que esperou
    }

    [Fact]
    public void Still_screen_gets_refinements_at_100_300_and_700ms_and_then_nothing()
    {
        var pipeline = Create();

        Run(pipeline, TimeSpan.FromSeconds(3));

        var submitted = _backend.Encoders[0].Submitted;
        Assert.Equal([1_000_000UL, 1_100_000UL, 1_300_000UL, 1_700_000UL], submitted.Select(s => s.Timestamp));
        Assert.Equal([true, false, false, false], submitted.Select(s => s.Forced));
    }

    [Fact]
    public void Keyframe_request_with_a_still_screen_reencodes_the_last_image_as_an_idr()
    {
        var pipeline = Create();
        Run(pipeline, TimeSpan.FromSeconds(1));
        var before = _output.Frames.Count;

        pipeline.RequestKeyframe();
        Run(pipeline, 5 * Ms);

        Assert.Equal(before + 1, _output.Frames.Count);
        Assert.True(_output.Frames[^1].IsKeyframe);
    }

    [Fact]
    public void Keyframes_are_200ms_apart_and_a_request_in_between_is_only_delayed()
    {
        var pipeline = Create();
        Run(pipeline, 50 * Ms); // IDR do começo em t = 0

        pipeline.RequestKeyframe();
        Run(pipeline, 100 * Ms);
        Assert.Single(_output.Frames, f => f.IsKeyframe);

        Run(pipeline, 100 * Ms);
        Assert.Equal(2, _output.Frames.Count(f => f.IsKeyframe));
    }

    [Fact]
    public void Lost_capture_is_reopened_after_100ms_and_keeps_the_encoder()
    {
        var pipeline = Create();
        Run(pipeline, 5 * Ms);

        _backend.Captures[0].ThrowOnAcquire = new CaptureLostException("ACCESS_LOST");
        Run(pipeline, 150 * Ms);

        Assert.Equal(2, _backend.Opened.Count);
        Assert.True(_backend.Captures[0].Disposed);
        Assert.Single(_backend.Encoders);
        Assert.Single(_output.Configs);
    }

    [Fact]
    public void Capture_with_a_new_size_starts_a_new_stream_with_config_and_idr()
    {
        var pipeline = Create();
        Run(pipeline, 5 * Ms);

        _backend.CaptureFactory = name => new FakeCapture(name, 1920, 1080, () => Now);
        _backend.Captures[0].ThrowOnAcquire = new CaptureLostException("ACCESS_LOST");
        Run(pipeline, 150 * Ms);

        Assert.True(_backend.Encoders[0].Disposed);
        Assert.Equal(1920, _backend.Encoders[1].Width);
        Assert.Equal(1920, _output.Configs[1].Width);
        Assert.IsType<ConfigMessage>(_output.Sent[^2]);
        Assert.True(Assert.IsType<FrameMessage>(_output.Sent[^1]).IsKeyframe);
    }

    [Fact]
    public void Monitor_change_reopens_the_capture_on_the_new_output()
    {
        var pipeline = Create();
        Run(pipeline, 5 * Ms);

        _monitor.Change(Display5 with { DeviceName = @"\\.\DISPLAY6" });
        Run(pipeline, 5 * Ms);

        Assert.Equal([@"\\.\DISPLAY5", @"\\.\DISPLAY6"], _backend.Opened);
        Assert.True(_backend.Captures[0].Disposed);
    }

    [Fact]
    public void Unavailable_output_is_retried_with_growing_waits_and_refreshes_the_monitor()
    {
        for (var i = 0; i < 10; i++) _backend.OpenFailures.Enqueue(new CaptureLostException("E_ACCESSDENIED"));
        var pipeline = Create();

        Run(pipeline, TimeSpan.FromMilliseconds(3050)); // tentativas em 0, 100, 300, 700 e 1500 ms (a próxima, em 3100)

        Assert.Equal(5, _backend.Opened.Count);
        Assert.Equal(5, _monitor.Refreshes);
        Assert.Single(_log, line => line.Contains("indisponível")); // a mesma mensagem não se repete
    }

    [Fact]
    public void H265_that_fails_to_open_is_dropped_for_the_run_and_h264_is_used()
    {
        _backend.FailingCodecs = VideoCodec.H265;
        var health = new CodecHealth();
        var pipeline = Create(health: health);

        Run(pipeline, 5 * Ms);

        Assert.Equal(VideoCodec.H264, _output.Configs[0].Codec);
        Assert.Equal(VideoCodec.H265, health.Failed);
    }

    [Fact]
    public void Without_a_common_codec_nothing_is_encoded()
    {
        _backend.HardwareCodecs = VideoCodec.H265;
        var pipeline = Create(phone: VideoCodec.H264);

        Run(pipeline, 500 * Ms);

        Assert.Empty(_output.Sent);
        Assert.Empty(_backend.Encoders);
    }

    [Theory]
    [InlineData(VideoCodec.H264 | VideoCodec.H265, VideoCodec.H264 | VideoCodec.H265, VideoCodec.None, VideoCodec.H265)]
    [InlineData(VideoCodec.H264, VideoCodec.H264 | VideoCodec.H265, VideoCodec.None, VideoCodec.H264)]
    [InlineData(VideoCodec.H264 | VideoCodec.H265, VideoCodec.H264, VideoCodec.None, VideoCodec.H264)]
    [InlineData(VideoCodec.H264 | VideoCodec.H265, VideoCodec.H264 | VideoCodec.H265, VideoCodec.H264, VideoCodec.H264)]
    [InlineData(VideoCodec.H265, VideoCodec.H264, VideoCodec.None, VideoCodec.None)]
    [InlineData(VideoCodec.H264, VideoCodec.H264 | VideoCodec.H265, VideoCodec.H265, VideoCodec.None)]
    public void Codec_choice(VideoCodec phone, VideoCodec host, VideoCodec forced, VideoCodec expected) =>
        Assert.Equal(expected, CodecChooser.Choose(phone, host, forced));

    [Fact]
    public void Timestamps_strictly_increase_even_with_a_stuck_clock()
    {
        _backend.CaptureFactory = name => new FakeCapture(name, 2520, 1080, () => Now) { Period = 17 * Ms };
        var pipeline = Create(clock: () => 5);

        Run(pipeline, 200 * Ms);

        var timestamps = _output.Frames.Select(f => f.TimestampUs).ToList();
        Assert.True(timestamps.Count > 5);
        Assert.True(timestamps.Zip(timestamps.Skip(1)).All(pair => pair.Second > pair.First));
    }

    [Fact]
    public void Stream_that_does_not_start_with_an_idr_waits_for_one_before_the_config()
    {
        _backend.EncoderSetup = encoder => encoder.FirstOutputIsP = true;
        var pipeline = Create();

        Run(pipeline, 20 * Ms);

        Assert.Equal(2, _output.Sent.Count);
        Assert.IsType<ConfigMessage>(_output.Sent[0]);
        Assert.True(Assert.IsType<FrameMessage>(_output.Sent[1]).IsKeyframe);
        Assert.Equal(2, _backend.Encoders[0].Submitted.Count);
    }

    [Fact]
    public void Encoder_failure_recreates_it_as_a_new_stream()
    {
        var pipeline = Create();
        Run(pipeline, 5 * Ms);

        _backend.Encoders[0].Fail(new InvalidOperationException("falhou"));
        Run(pipeline, 150 * Ms); // recria depois da espera de 100 ms

        Assert.True(_backend.Encoders[0].Disposed);
        Assert.Equal(2, _output.Configs.Count);
        Assert.True(_output.Frames[^1].IsKeyframe);
    }

    [Fact]
    public void Output_from_an_old_encoder_is_ignored()
    {
        var pipeline = Create();
        Run(pipeline, 5 * Ms);
        var old = _backend.Encoders[0];
        old.Fail(new InvalidOperationException("falhou"));
        Run(pipeline, 150 * Ms);
        var count = _output.Sent.Count;

        old.Emit(new EncodedFrame(99, true, FakeEncoder.Idr(VideoCodec.H265)));

        Assert.Equal(count, _output.Sent.Count);
    }

    [Fact]
    public void Lost_device_recreates_the_whole_backend()
    {
        var pipeline = Create();
        Run(pipeline, 5 * Ms);

        _backend.Captures[0].ThrowOnAcquire = new DeviceLostException("DEVICE_REMOVED");
        Run(pipeline, 150 * Ms);

        Assert.True(_backend.Disposed);
        Assert.Equal(2, _backendsCreated);
        Assert.Equal(2, _output.Configs.Count);
    }

    [Fact]
    public void Encoder_that_rejects_every_frame_backs_off_and_its_codec_gives_way()
    {
        _backend.EncoderSetup = encoder => encoder.ThrowOnSubmit = encoder.Codec == VideoCodec.H265;
        var health = new CodecHealth();
        var pipeline = Create(health: health);

        Run(pipeline, TimeSpan.FromSeconds(2));

        Assert.Equal(VideoPipeline.MaxFailuresWithoutOutput, _backend.Encoders.Count(e => e.Codec == VideoCodec.H265));
        Assert.Equal(VideoCodec.H265, health.Failed);
        Assert.Equal(VideoCodec.H264, Assert.Single(_output.Configs).Codec);
        Assert.True(_log.Count < 10, $"{_log.Count} linhas de log");
    }

    [Fact]
    public void Codec_that_worked_and_then_fails_to_reopen_recreates_everything_and_is_kept()
    {
        var health = new CodecHealth();
        var pipeline = Create(health: health);
        Run(pipeline, 50 * Ms);

        // A placa reiniciou: o encoder avisa erro e nenhum encoder abre enquanto ela volta.
        _backend.FailingCodecs = VideoCodec.H264 | VideoCodec.H265;
        _backend.Encoders[0].Fail(new InvalidOperationException("erro do encoder"));
        Run(pipeline, 500 * Ms);
        _backend.FailingCodecs = VideoCodec.None;
        Run(pipeline, TimeSpan.FromSeconds(3));

        Assert.Equal(VideoCodec.None, health.Failed);
        Assert.True(_backendsCreated >= 2, "o backend não foi recriado");
        Assert.Equal(2, _output.Configs.Count);
        Assert.Equal(VideoCodec.H265, _output.Configs[1].Codec);
    }

    [Fact]
    public void Encoder_reporting_a_lost_device_recreates_the_backend()
    {
        var pipeline = Create();
        Run(pipeline, 5 * Ms);

        _backend.Encoders[0].Fail(new DeviceLostException("DEVICE_REMOVED"));
        Run(pipeline, 150 * Ms);

        Assert.True(_backend.Disposed);
        Assert.Equal(2, _backendsCreated);
        Assert.Equal(2, _output.Configs.Count);
    }

    [Fact]
    public void Requested_idr_that_comes_out_as_a_p_frame_is_requested_again()
    {
        var pipeline = Create();
        Run(pipeline, 300 * Ms);
        _backend.Encoders[0].IgnoreForce = 1;

        pipeline.RequestKeyframe();
        Run(pipeline, 500 * Ms);

        Assert.Equal(2, _output.Frames.Count(f => f.IsKeyframe)); // o do começo e o pedido de novo
    }

    [Fact]
    public void Encoder_that_stops_asking_for_input_is_recreated_after_a_second()
    {
        var pipeline = Create();
        Run(pipeline, 5 * Ms);

        _backend.Encoders[0].CanAccept = false;
        Run(pipeline, TimeSpan.FromMilliseconds(1200));

        Assert.True(_backend.Encoders[0].Disposed);
        Assert.Equal(2, _output.Configs.Count);
    }

    [Fact]
    public void Unexpected_error_restarts_the_video_instead_of_stopping_it()
    {
        var pipeline = Create();
        Run(pipeline, 5 * Ms);

        _backend.Captures[0].ThrowOnAcquire = new InvalidOperationException("erro inesperado do driver");
        Run(pipeline, 150 * Ms);

        Assert.Equal(2, _backendsCreated);
        Assert.Equal(2, _output.Configs.Count);
    }

    [Fact]
    public void Monitor_change_does_not_wait_for_a_pending_backoff()
    {
        for (var i = 0; i < 3; i++) _backend.OpenFailures.Enqueue(new CaptureLostException("E_ACCESSDENIED"));
        var pipeline = Create();
        Run(pipeline, 320 * Ms); // tentativas em 0, 100 e 300 ms; a próxima seria em 700
        Assert.Equal(3, _backend.Opened.Count);

        _monitor.Change(Display5 with { DeviceName = @"\\.\DISPLAY6" });
        Run(pipeline, 5 * Ms);

        Assert.Equal(4, _backend.Opened.Count);
    }

    [Fact]
    public void Stats_count_frames_bytes_and_keyframes()
    {
        var pipeline = Create();

        Run(pipeline, TimeSpan.FromSeconds(1));

        var stats = pipeline.Stats;
        Assert.Equal((2520, 1080, VideoCodec.H265), (stats.Width, stats.Height, stats.Codec));
        Assert.Equal(4, stats.Frames);
        Assert.Equal(1, stats.Keyframes);
        Assert.Equal(_output.Frames.Sum(f => (long)f.Data.Length), stats.Bytes);
    }
}
```

`host/ScreenShare.Tests/Video/PipelineVideoSourceTests.cs`:

```csharp
using System.Diagnostics;
using ScreenShare.Core.Protocol;
using ScreenShare.Display;
using ScreenShare.Video;
using ScreenShare.Video.Pipeline;

namespace ScreenShare.Tests.Video;

public sealed class PipelineVideoSourceTests
{
    private static readonly FakeMonitor Monitor = new(new VirtualMonitor(@"\\.\DISPLAY5", 0, 0, 1920, 1080, 100));

    [Fact]
    public async Task Start_runs_the_pipeline_on_its_own_thread_and_dispose_stops_everything()
    {
        var clock = Stopwatch.StartNew();
        var backend = new FakeBackend
        {
            CaptureFactory = name => new FakeCapture(name, 1920, 1080, () => clock.Elapsed) { Period = TimeSpan.FromMilliseconds(16) },
        };
        var source = new PipelineVideoSource(() => backend, VideoCodec.H264 | VideoCodec.H265, new VideoOptions(),
            TimeProvider.System, _ => { });
        var output = new FakeOutput();

        var stream = await source.StartAsync(new VideoRequest(Monitor, VideoCodec.H265, VideoLink.Usb), output, CancellationToken.None);
        Assert.NotNull(stream);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (output.Frames.Count < 3)
        {
            Assert.True(DateTime.UtcNow < deadline, "o vídeo não começou em 5 s");
            await Task.Delay(10);
        }
        await stream.DisposeAsync();

        Assert.True(backend.Disposed);
        Assert.All(backend.Captures, capture => Assert.True(capture.Disposed));
        Assert.All(backend.Encoders, encoder => Assert.True(encoder.Disposed));
        Assert.Equal(50_000u, output.Configs[0].BitrateKbps); // padrão do cabo
    }

    [Fact]
    public async Task Phone_without_a_codec_the_host_encodes_gets_no_video()
    {
        var source = new PipelineVideoSource(() => throw new InvalidOperationException("não deve criar"), VideoCodec.H265,
            new VideoOptions(), TimeProvider.System, _ => { });

        var stream = await source.StartAsync(new VideoRequest(Monitor, VideoCodec.H264, VideoLink.Wifi), new FakeOutput(),
            CancellationToken.None);

        Assert.Null(stream);
    }

    [Fact]
    public void Bitrate_defaults_to_50_mbps_on_usb_and_25_on_wifi_unless_set()
    {
        Assert.Equal(50_000, new VideoOptions().BitrateKbpsFor(VideoLink.Usb));
        Assert.Equal(25_000, new VideoOptions().BitrateKbpsFor(VideoLink.Wifi));
        Assert.Equal(8_000, new VideoOptions(BitrateMbps: 8).BitrateKbpsFor(VideoLink.Usb));
    }
}
```

Run: `dotnet test host/ScreenShare.slnx --filter FullyQualifiedName~ScreenShare.Tests.Video`
Expected: erro de compilação (`FramePacer`, `KeyframeScheduler`, `CodecChooser`, `CodecHealth`, `VideoPipeline` e `PipelineVideoSource` não existem).

- [ ] **Step 3: Ritmo, keyframes e codec**

`host/ScreenShare.Video/Pipeline/FramePacer.cs`:

```csharp
namespace ScreenShare.Video.Pipeline;

/// <summary>
/// Ritmo de até fps quadros por segundo. Tem uma folga de 1/4 de intervalo para o jitter do vsync: um quadro de 60 Hz
/// que chega 1 ms antes não espera um intervalo inteiro, e a tela a 60 Hz continua saindo a 60 fps.
/// </summary>
public sealed class FramePacer
{
    private readonly TimeSpan _interval;
    private readonly TimeSpan _slack;
    private TimeSpan? _next;

    public FramePacer(int fps)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(fps, 1);
        _interval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / fps);
        _slack = _interval / 4;
    }

    /// <summary>Quanto falta para o próximo quadro poder sair (zero = já pode).</summary>
    public TimeSpan Delay(TimeSpan now) =>
        _next is { } next && now < next - _slack ? next - _slack - now : TimeSpan.Zero;

    public void MarkSent(TimeSpan now)
    {
        // Dentro da folga, mantém a cadência; bem atrasado (tela parada), recomeça de agora.
        var basis = _next is { } next && (now - next).Duration() <= _slack ? next : now;
        _next = basis + _interval;
    }
}
```

`host/ScreenShare.Video/Pipeline/KeyframeScheduler.cs`:

```csharp
namespace ScreenShare.Video.Pipeline;

/// <summary>
/// Junta os pedidos de keyframe (celular, descarte na fila de envio) com no mínimo 200 ms entre IDRs: um IDR do
/// monitor inteiro tem centenas de KB. Um pedido só é adiado, nunca perdido. Seguro entre threads.
/// </summary>
public sealed class KeyframeScheduler
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(200);

    private readonly Lock _gate = new();
    private bool _pending;
    private TimeSpan? _last;

    public void Request()
    {
        lock (_gate) _pending = true;
    }

    /// <summary>Pedido que não espera o intervalo mínimo: stream novo, cujo primeiro quadro tem de ser IDR.</summary>
    public void RequestNow()
    {
        lock (_gate)
        {
            _pending = true;
            _last = null;
        }
    }

    public bool IsDue(TimeSpan now)
    {
        lock (_gate) return Due(now);
    }

    /// <summary>true = o próximo quadro deve ser IDR (e o pedido é consumido).</summary>
    public bool TakeDue(TimeSpan now)
    {
        lock (_gate)
        {
            if (!Due(now)) return false;
            _pending = false;
            _last = now;
            return true;
        }
    }

    private bool Due(TimeSpan now) => _pending && (_last is not { } last || now - last >= MinInterval);
}
```

`host/ScreenShare.Video/Pipeline/CodecChoice.cs`:

```csharp
using ScreenShare.Core.Protocol;

namespace ScreenShare.Video.Pipeline;

public static class CodecChooser
{
    /// <summary>H.265 se o celular e o host tiverem; senão H.264; None = sem vídeo. forced (--codec) restringe a um só.</summary>
    public static VideoCodec Choose(VideoCodec phone, VideoCodec host, VideoCodec forced = VideoCodec.None)
    {
        var both = phone & host;
        if (forced != VideoCodec.None) both &= forced;
        if (both.HasFlag(VideoCodec.H265)) return VideoCodec.H265;
        if (both.HasFlag(VideoCodec.H264)) return VideoCodec.H264;
        return VideoCodec.None;
    }

    public static string Name(VideoCodec codec) => codec == VideoCodec.H265 ? "H.265" : "H.264";
}

/// <summary>
/// Os codecs que já produziram vídeo nesta execução do host e os que falharam (estes ficam de fora até o host
/// reiniciar). Um codec que já funcionou nunca é marcado como falho: a falha dele é da placa ou passageira.
/// Compartilhado entre sessões.
/// </summary>
public sealed class CodecHealth
{
    private int _failed;
    private int _worked;

    public VideoCodec Failed => (VideoCodec)Volatile.Read(ref _failed);

    public void MarkFailed(VideoCodec codec) => Add(ref _failed, codec);

    public void MarkWorked(VideoCodec codec) => Add(ref _worked, codec);

    public bool HasWorked(VideoCodec codec) => ((VideoCodec)Volatile.Read(ref _worked) & codec) == codec;

    private static void Add(ref int field, VideoCodec codec)
    {
        int seen;
        do
        {
            seen = Volatile.Read(ref field);
        }
        while (Interlocked.CompareExchange(ref field, seen | (int)codec, seen) != seen);
    }
}
```

- [ ] **Step 4: `VideoPipeline`**

`host/ScreenShare.Video/Pipeline/VideoPipeline.cs`:

```csharp
using ScreenShare.Core.Protocol;
using ScreenShare.Core.Video;
using ScreenShare.Display;

namespace ScreenShare.Video.Pipeline;

/// <summary>
/// Captura → encoder → saída, um passo por vez (Step), sempre na mesma thread (a de captura). O hardware fica atrás de
/// IVideoBackend; os testes chamam Step com um relógio falso.
/// - Abre a captura com espera crescente (100 ms até 2 s); se a saída sumiu, relê o monitor.
/// - Tamanho novo da captura = encoder novo = stream novo: IDR, depois CONFIG (com os parâmetros tirados do IDR), depois quadros.
/// - Só captura com o ritmo de fps permitindo, o encoder pedindo entrada e nenhum quadro esperando a rede: assim a
///   captura junta as mudanças enquanto a rede está ocupada, e o quadro seguinte já é a imagem mais nova.
/// - Tela parada: refinamentos em +100, +300 e +700 ms e, se pedido, IDR da última imagem.
/// - Falhas: encoder que quebra é recriado com espera crescente; um codec que nunca funcionou é deixado de lado depois
///   de 3 falhas seguidas; um codec que já funcionou e não abre mais indica a placa reiniciada (recria tudo); qualquer
///   erro inesperado recomeça o vídeo em vez de pará-lo.
/// </summary>
public sealed class VideoPipeline : IDisposable
{
    public static readonly TimeSpan FirstRetry = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan MaxRetry = TimeSpan.FromSeconds(2);

    /// <summary>Espera máxima por imagem nova numa chamada: limita quanto um pedido de keyframe espera com a tela parada.</summary>
    public static readonly TimeSpan AcquireTimeout = TimeSpan.FromMilliseconds(20);

    /// <summary>Quanto o Step pede para esperar quando a rede ou o encoder ainda não liberaram.</summary>
    public static readonly TimeSpan BusyWait = TimeSpan.FromMilliseconds(1);

    /// <summary>O encoder sem pedir entrada por mais que isto está travado: é recriado.</summary>
    public static readonly TimeSpan EncoderStallTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Recodificações da última imagem depois que a tela para (o controle de bitrate deixa o primeiro quadro borrado).</summary>
    public static readonly TimeSpan[] Refinements =
        [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(700)];

    /// <summary>Falhas seguidas, sem nenhum quadro saindo, para desistir de um codec que nunca funcionou nesta execução.</summary>
    public const int MaxFailuresWithoutOutput = 3;

    private const int MaxTracked = 1000;

    private readonly Lock _gate = new();
    private readonly Func<IVideoBackend> _createBackend;
    private readonly IMonitorSource _monitor;
    private readonly VideoCodec _phoneCodecs;
    private readonly VideoOptions _options;
    private readonly int _bitrateKbps;
    private readonly CodecHealth _health;
    private readonly IVideoOutput _output;
    private readonly TimeProvider _time;
    private readonly Func<ulong> _clockUs;
    private readonly Action<string> _log;
    private readonly Action? _wake;
    private readonly long _start;
    private readonly FramePacer _pacer;
    private readonly KeyframeScheduler _keyframes = new();

    // Só a thread de captura (Step) mexe nestes.
    private IVideoBackend? _backend;
    private IScreenCapture? _capture;
    private TimeSpan _retryAt;
    private TimeSpan _retryDelay = FirstRetry;
    private bool _haveImage;
    private TimeSpan? _lastChange;
    private int _refinementsDone;
    private string? _lastProblem;
    private int _failuresWithoutOutput;
    private TimeSpan? _busySince;

    // Compartilhados com a thread do encoder e com a sessão: sob _gate.
    private readonly Dictionary<ulong, long> _submittedAt = [];
    private readonly HashSet<ulong> _forced = [];
    private readonly RecentDurations _encodeTimes = new(300);
    private IVideoEncoder? _encoder;
    private int _generation;
    private bool _needConfig;
    private int _failedGeneration = -1;
    private Exception? _failure;
    private bool _deviceLost;
    private bool _outputSeen;
    private bool _monitorChanged;
    private ulong _lastTimestamp;
    private long _frames;
    private long _bytes;
    private long _keyframesSent;
    private bool _disposed;

    /// <param name="clockUs">Relógio do PC em µs (PcClock.NowUs): timestamp dos quadros sem horário de apresentação.</param>
    /// <param name="wake">Acorda a thread de captura (pedido de keyframe, monitor mudou, encoder falhou).</param>
    public VideoPipeline(Func<IVideoBackend> createBackend, IMonitorSource monitor, VideoCodec phoneCodecs,
        VideoOptions options, int bitrateKbps, CodecHealth health, IVideoOutput output, TimeProvider time,
        Func<ulong> clockUs, Action<string> log, Action? wake = null)
    {
        _createBackend = createBackend;
        _monitor = monitor;
        _phoneCodecs = phoneCodecs;
        _options = options;
        _bitrateKbps = bitrateKbps;
        _health = health;
        _output = output;
        _time = time;
        _clockUs = clockUs;
        _log = log;
        _wake = wake;
        _start = time.GetTimestamp();
        _pacer = new FramePacer(options.Fps);
        monitor.Changed += OnMonitorChanged;
    }

    private TimeSpan Now => _time.GetElapsedTime(_start);

    public VideoStats Stats
    {
        get
        {
            lock (_gate)
            {
                return new VideoStats(_encoder?.Width ?? 0, _encoder?.Height ?? 0, _encoder?.Codec ?? VideoCodec.None,
                    _frames, _bytes, _keyframesSent, _encodeTimes.P95().TotalMilliseconds);
            }
        }
    }

    /// <summary>Pedido de keyframe (celular, fila de envio cheia). Qualquer thread; nunca lança.</summary>
    public void RequestKeyframe()
    {
        _keyframes.Request();
        _wake?.Invoke();
    }

    /// <summary>Um passo. Devolve quanto esperar antes do próximo (zero = chamar de novo já).</summary>
    public TimeSpan Step()
    {
        bool monitorChanged, outputSeen, deviceLost;
        lock (_gate)
        {
            if (_disposed) return MaxRetry;
            monitorChanged = _monitorChanged;
            outputSeen = _outputSeen;
            deviceLost = _deviceLost;
            _outputSeen = false;
            _deviceLost = false;
        }
        if (outputSeen)
        {
            _retryDelay = FirstRetry; // o vídeo voltou a sair: a próxima falha recomeça a espera do zero
            _failuresWithoutOutput = 0;
        }
        var now = Now;
        if (deviceLost)
        {
            Problem("A placa de vídeo foi reiniciada (o encoder avisou); recriando a captura e o encoder.");
            CloseAll();
            return Backoff(now);
        }
        if (now < _retryAt && !monitorChanged) return _retryAt - now; // monitor novo não espera a espera acabar

        try
        {
            if (!EnsureBackend() || !EnsureCapture() || !EnsureEncoder()) return Backoff(now);
            return Capture(now);
        }
        catch (CaptureLostException e)
        {
            Problem($"Captura perdida ({e.Message}); reabrindo.");
            CloseCapture();
            return Backoff(now);
        }
        catch (DeviceLostException e)
        {
            Problem($"A placa de vídeo foi reiniciada ({e.Message}); recriando a captura e o encoder.");
            CloseAll();
            return Backoff(now);
        }
        catch (Exception e)
        {
            // Erro que ninguém previu (driver, interop): recomeça tudo em vez de deixar a sessão com a imagem congelada.
            Problem($"Falha inesperada no vídeo ({e.Message}); recomeçando a captura e o encoder.");
            CloseAll();
            return Backoff(now);
        }
    }

    /// <summary>Chamado na thread de captura, depois do último Step.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _monitor.Changed -= OnMonitorChanged;
        CloseAll();
    }

    private TimeSpan Backoff(TimeSpan now)
    {
        var wait = _retryDelay;
        _retryAt = now + wait;
        _retryDelay = TimeSpan.FromTicks(Math.Min(_retryDelay.Ticks * 2, MaxRetry.Ticks));
        return wait;
    }

    private bool EnsureBackend()
    {
        if (_backend is not null) return true;
        try
        {
            _backend = _createBackend();
            return true;
        }
        catch (Exception e)
        {
            Problem($"Vídeo indisponível: {e.Message}");
            return false;
        }
    }

    private bool EnsureCapture()
    {
        bool changed;
        lock (_gate)
        {
            changed = _monitorChanged;
            _monitorChanged = false;
        }
        if (changed) CloseCapture(); // nome ou tamanho novo: a captura antiga não vale mais
        if (_capture is not null) return true;

        var monitor = _monitor.Current ?? _monitor.Refresh();
        if (monitor is null)
        {
            Problem("Monitor para capturar não encontrado; tentando de novo.");
            return false;
        }
        try
        {
            _capture = _backend!.OpenCapture(monitor.DeviceName);
            _retryDelay = FirstRetry;
            return true;
        }
        catch (CaptureLostException e)
        {
            // UAC ou Win+L (área de trabalho segura), ou driver reiniciando (nome novo): relê o monitor e tenta de novo.
            Problem($"Captura de {monitor.DeviceName} indisponível ({e.Message}); tentando de novo.");
            _monitor.Refresh();
            return false;
        }
    }

    private bool EnsureEncoder()
    {
        Exception? failure;
        lock (_gate)
        {
            failure = _failedGeneration == _generation ? _failure : null;
            _failedGeneration = -1;
            _failure = null;
        }
        if (failure is not null && _encoder is not null)
        {
            EncoderBroke($"O encoder falhou ({failure.Message}); recriando.");
            return false;
        }

        var capture = _capture!;
        if (_encoder is { } current && current.Width == capture.Width && current.Height == capture.Height) return true;
        CloseEncoder();

        while (true)
        {
            var codec = CodecChooser.Choose(_phoneCodecs, _backend!.HardwareCodecs & ~_health.Failed, _options.Codec);
            if (codec == VideoCodec.None)
            {
                Problem("Nenhum encoder disponível para os codecs que o celular decodifica; sem vídeo.");
                return false;
            }
            try
            {
                StartStream(_backend.CreateEncoder(new EncoderSettings(codec, capture.Width, capture.Height, _options.Fps,
                    _bitrateKbps, _bitrateKbps * 2)));
                return true;
            }
            catch (EncoderUnavailableException e)
            {
                if (_health.HasWorked(codec))
                {
                    // Já funcionou nesta execução: a placa reiniciou ou a falha é passageira. Recria tudo e tenta de novo.
                    Problem($"Encoder {CodecChooser.Name(codec)} não abriu ({e.Message}); recriando a captura e o encoder.");
                    CloseAll();
                    return false;
                }
                _log($"Encoder {CodecChooser.Name(codec)} indisponível ({e.Message}); ele fica de fora até o host reiniciar.");
                _health.MarkFailed(codec);
            }
        }
    }

    /// <summary>
    /// O encoder em uso quebrou: fecha (o próximo Step espera e recria). Um codec que nunca produziu vídeo nesta execução
    /// é deixado de lado depois de MaxFailuresWithoutOutput falhas seguidas, e o outro codec assume.
    /// </summary>
    private void EncoderBroke(string message)
    {
        var codec = _encoder?.Codec ?? VideoCodec.None;
        Problem(message);
        CloseEncoder();
        if (++_failuresWithoutOutput < MaxFailuresWithoutOutput || codec == VideoCodec.None || _health.HasWorked(codec)) return;
        _log($"Encoder {CodecChooser.Name(codec)} falhou {_failuresWithoutOutput} vezes sem produzir vídeo; ele fica de fora até o host reiniciar.");
        _health.MarkFailed(codec);
        _failuresWithoutOutput = 0;
    }

    private void StartStream(IVideoEncoder encoder)
    {
        lock (_gate)
        {
            var generation = ++_generation;
            encoder.Output += frame => OnOutput(generation, frame);
            encoder.Failed += error => OnFailed(generation, error);
            _encoder = encoder;
            _needConfig = true;
            _submittedAt.Clear();
            _forced.Clear();
        }
        _busySince = null;
        _keyframes.RequestNow();
        _log($"Vídeo: {CodecChooser.Name(encoder.Codec)} {encoder.Width}×{encoder.Height}, até {_options.Fps} fps, {_bitrateKbps / 1000} Mbps.");
    }

    private TimeSpan Capture(TimeSpan now)
    {
        var capture = _capture!;
        var encoder = _encoder!;
        if (_output.PendingFrames > 0)
        {
            _busySince = null; // esperando a rede, não o encoder
            return BusyWait;
        }
        if (!encoder.CanAccept)
        {
            _busySince ??= now;
            if (now - _busySince.Value < EncoderStallTimeout) return BusyWait;
            _busySince = null;
            EncoderBroke("O encoder parou de pedir quadros; recriando.");
            return Backoff(now);
        }
        _busySince = null;
        var pace = _pacer.Delay(now);
        if (pace > TimeSpan.Zero) return pace;

        var keyframeDue = _haveImage && _keyframes.IsDue(now);
        var refinementDue = _haveImage && RefinementDue(now);
        var result = capture.TryAcquire(keyframeDue || refinementDue ? TimeSpan.Zero : AcquireTimeout, out var presentUs);
        now = Now; // a espera do TryAcquire conta
        var submitted = true;
        if (result == AcquireResult.NewImage)
        {
            _haveImage = true;
            _lastChange = now;
            _refinementsDone = 0;
            submitted = Submit(encoder, capture.Last, presentUs, now);
        }
        else if (keyframeDue || refinementDue)
        {
            if (refinementDue) _refinementsDone++;
            submitted = Submit(encoder, capture.Last, 0, now);
        }
        return submitted ? TimeSpan.Zero : Backoff(now);
    }

    private bool RefinementDue(TimeSpan now) =>
        _lastChange is { } changed && _refinementsDone < Refinements.Length && now - changed >= Refinements[_refinementsDone];

    /// <summary>Manda a imagem ao encoder. false = o encoder recusou e foi fechado (o Step espera antes de recriar).</summary>
    private bool Submit(IVideoEncoder encoder, IVideoImage image, ulong presentUs, TimeSpan now)
    {
        var timestamp = presentUs != 0 ? presentUs : _clockUs();
        var force = _keyframes.TakeDue(now);
        lock (_gate)
        {
            if (timestamp <= _lastTimestamp) timestamp = _lastTimestamp + 1; // estritamente crescente dentro do stream
            _lastTimestamp = timestamp;
            if (_submittedAt.Count >= MaxTracked) _submittedAt.Clear();
            _submittedAt[timestamp] = _time.GetTimestamp();
            if (force)
            {
                if (_forced.Count >= MaxTracked) _forced.Clear();
                _forced.Add(timestamp);
            }
        }
        _pacer.MarkSent(now);
        _lastProblem = null;
        try
        {
            encoder.Submit(image, timestamp, force);
            return true;
        }
        catch (Exception e) when (e is not DeviceLostException)
        {
            if (force) _keyframes.RequestNow(); // o IDR pedido não saiu: vai no próximo encoder
            EncoderBroke($"O encoder recusou o quadro ({e.Message}); recriando.");
            return false;
        }
    }

    /// <summary>Saída do encoder, na thread dele. A emissão fica sob a trava para um stream novo nunca se misturar ao antigo.</summary>
    private void OnOutput(int generation, EncodedFrame frame)
    {
        var askAgain = false;
        lock (_gate)
        {
            if (_disposed || generation != _generation) return; // encoder antigo: o stream dele acabou
            if (_submittedAt.Remove(frame.TimestampUs, out var submitted)) _encodeTimes.Add(_time.GetElapsedTime(submitted));
            var forced = _forced.Remove(frame.TimestampUs);
            if (_needConfig)
            {
                if (!frame.IsKeyframe)
                {
                    _keyframes.RequestNow(); // o celular só começa a decodificar num IDR
                    return;
                }
                var encoder = _encoder!;
                _output.OnConfig(new ConfigMessage((ushort)encoder.Width, (ushort)encoder.Height, encoder.Codec,
                    (uint)_bitrateKbps, AnnexB.ExtractParameterSets(frame.Data, encoder.Codec) ?? []));
                _needConfig = false;
            }
            else if (forced && !frame.IsKeyframe)
            {
                // O IDR pedido saiu como P-frame (alguns encoders aplicam o pedido no quadro seguinte): pede de novo, senão a
                // fila de envio, que espera um keyframe depois de um descarte, ficaria descartando tudo.
                _keyframes.Request();
                askAgain = true;
            }
            _health.MarkWorked(_encoder!.Codec);
            _outputSeen = true;
            _frames++;
            _bytes += frame.Data.Length;
            if (frame.IsKeyframe) _keyframesSent++;
            _output.OnFrame(new FrameMessage(frame.TimestampUs, frame.IsKeyframe, frame.Data));
        }
        if (askAgain) _wake?.Invoke();
    }

    private void OnFailed(int generation, Exception error)
    {
        lock (_gate)
        {
            if (_disposed || generation != _generation) return;
            if (error is DeviceLostException)
            {
                _deviceLost = true;
            }
            else
            {
                _failedGeneration = generation;
                _failure = error;
            }
        }
        _wake?.Invoke();
    }

    private void OnMonitorChanged()
    {
        lock (_gate) _monitorChanged = true;
        _wake?.Invoke();
    }

    private void CloseEncoder()
    {
        IVideoEncoder? encoder;
        lock (_gate)
        {
            _generation++; // saídas e falhas atrasadas do encoder antigo passam a ser ignoradas
            encoder = _encoder;
            _encoder = null;
        }
        encoder?.Dispose();
    }

    private void CloseCapture()
    {
        _capture?.Dispose();
        _capture = null;
        _haveImage = false;
    }

    private void CloseAll()
    {
        CloseEncoder();
        CloseCapture();
        _backend?.Dispose();
        _backend = null;
    }

    /// <summary>Registra um problema uma vez só, até um quadro sair de novo (retentativas não enchem o console).</summary>
    private void Problem(string message)
    {
        if (message == _lastProblem) return;
        _lastProblem = message;
        _log(message);
    }
}

/// <summary>As últimas N durações (do encode), para o p95 das estatísticas.</summary>
internal sealed class RecentDurations(int capacity)
{
    private readonly Queue<TimeSpan> _items = new();

    public void Add(TimeSpan duration)
    {
        _items.Enqueue(duration);
        if (_items.Count > capacity) _items.Dequeue();
    }

    public TimeSpan P95()
    {
        if (_items.Count == 0) return TimeSpan.Zero;
        var sorted = _items.Order().ToArray();
        return sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1];
    }
}
```

- [ ] **Step 5: `PipelineVideoSource`**

`host/ScreenShare.Video/Pipeline/PipelineVideoSource.cs`:

```csharp
using ScreenShare.Core.Protocol;
using ScreenShare.Core.Video;

namespace ScreenShare.Video.Pipeline;

/// <summary>
/// O IVideoSource de verdade: cada sessão ganha um VideoPipeline numa thread própria (a thread de captura, com
/// prioridade acima do normal). O backend vem de createBackend, então os testes usam falsos. prepareThread roda no
/// começo da thread (DPI por monitor, timer de 1 ms) e o que devolve é descartado no fim dela.
/// </summary>
public sealed class PipelineVideoSource : IVideoSource
{
    private readonly Func<IVideoBackend> _createBackend;
    private readonly VideoCodec _hostCodecs;
    private readonly VideoOptions _options;
    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private readonly Func<IDisposable>? _prepareThread;
    private readonly CodecHealth _health = new();

    public PipelineVideoSource(Func<IVideoBackend> createBackend, VideoCodec hostCodecs, VideoOptions options,
        TimeProvider time, Action<string> log, Func<IDisposable>? prepareThread = null)
    {
        _createBackend = createBackend;
        _hostCodecs = hostCodecs;
        _options = options;
        _time = time;
        _log = log;
        _prepareThread = prepareThread;
    }

    public Task<IVideoStream?> StartAsync(VideoRequest request, IVideoOutput output, CancellationToken cancellationToken)
    {
        if (CodecChooser.Choose(request.PhoneCodecs, _hostCodecs & ~_health.Failed, _options.Codec) == VideoCodec.None)
        {
            _log("Nenhum encoder de hardware para os codecs que o celular decodifica: sessão sem vídeo.");
            return Task.FromResult<IVideoStream?>(null);
        }
        return Task.FromResult<IVideoStream?>(new PipelineStream(this, request, output));
    }

    private sealed class PipelineStream : IVideoStream
    {
        private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

        private readonly PipelineVideoSource _owner;
        private readonly VideoPipeline _pipeline;
        private readonly AutoResetEvent _wake = new(false);
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _stopping;

        public PipelineStream(PipelineVideoSource owner, VideoRequest request, IVideoOutput output)
        {
            _owner = owner;
            _pipeline = new VideoPipeline(owner._createBackend, request.Monitor, request.PhoneCodecs, owner._options,
                owner._options.BitrateKbpsFor(request.Link), owner._health, output, owner._time, () => PcClock.NowUs,
                owner._log, () => _wake.Set());
            new Thread(Run) { IsBackground = true, Name = "ScreenShare captura", Priority = ThreadPriority.AboveNormal }.Start();
        }

        public VideoStats Stats => _pipeline.Stats;

        public void RequestKeyframe() => _pipeline.RequestKeyframe();

        /// <summary>
        /// Para a thread e espera ela soltar a captura e o encoder, por até 5 s: um driver travado não pode prender o fim
        /// da sessão (nem a próxima sessão, que espera esta sair do vídeo).
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            _stopping = true;
            _wake.Set(); // o evento nunca é descartado: um pedido de keyframe atrasado não pode lançar
            try
            {
                await _stopped.Task.WaitAsync(StopTimeout);
            }
            catch (TimeoutException)
            {
                _owner._log($"O vídeo não parou em {StopTimeout.TotalSeconds:0} s; a sessão segue sem esperar.");
            }
        }

        private void Run()
        {
            IDisposable? prepared = null;
            try
            {
                prepared = _owner._prepareThread?.Invoke();
                while (!_stopping)
                {
                    var wait = _pipeline.Step();
                    if (wait > TimeSpan.Zero) _wake.WaitOne(wait);
                }
            }
            catch (Exception e)
            {
                _owner._log($"O vídeo parou por um erro inesperado: {e.Message}");
            }
            finally
            {
                _pipeline.Dispose();
                prepared?.Dispose();
                _stopped.TrySetResult();
            }
        }
    }
}
```

Run: `dotnet test host/ScreenShare.slnx --filter FullyQualifiedName~ScreenShare.Tests.Video`
Expected: PASS. São 40 casos novos: `VideoPipelineTests` 32, `PacingTests` 5 e `PipelineVideoSourceTests` 3. Rode 3 vezes: `PipelineVideoSourceTests` usa uma thread de verdade.

- [ ] **Step 6: Rodar tudo, commit e push**

Run: `dotnet build host/ScreenShare.slnx` e `dotnet test host/ScreenShare.slnx`
Expected: 0 avisos; 333 aprovados e 1 ignorado.

```bash
git add host/ScreenShare.Video host/ScreenShare.Tests/Video
git commit -m "feat(video): pipeline de captura e encode com ritmo, keyframes, refinamentos e recuperação"
git push
```

---

### Task 7: captura DXGI do monitor (hardware)

**Por quê:** é a primeira metade do hardware: achar a saída pelo nome, criar o device D3D11 na placa dela e duplicar a tela. Usa o que o spike validou (E1 a E3). O cursor fica para a Task 13.

**Files:**
- Modify: `host/ScreenShare.Video/ScreenShare.Video.csproj` (pacotes Vortice.Direct3D11 e Vortice.DXGI 3.8.3, `AllowUnsafeBlocks`)
- Create, todos em `host/ScreenShare.Video/Hardware/`:
  - `DxgiErrors.cs`, `GpuContext.cs`, `DxgiOutputLocator.cs`;
  - `DesktopDuplicationCapture.cs` (com `GpuImage`);
  - `CaptureThread.cs` e `PrimaryMonitorSource.cs`.
- Test: `host/ScreenShare.Tests/Video/GpuFactAttribute.cs`, `host/ScreenShare.Tests/Video/CaptureTests.cs`

**Interfaces:**
- Consumes: `IScreenCapture`, `IVideoImage`, `AcquireResult`, `CaptureLostException` e `DeviceLostException` (Task 6); `PcClock.ToMicroseconds` (Task 2); `IMonitorSource` e `VirtualMonitor` (Task 4).
- Produces (as Tasks 8, 9 e 13 usam), no namespace `ScreenShare.Video.Hardware`:
  - `internal GpuContext.Create(IDXGIAdapter1? adapter)`, com `Device`, `Context`, `VendorId`, `AdapterLuid` e `LuidOf(Luid)`;
  - `internal DxgiOutputLocator.Find(string deviceName)` e `FindPrimary()`, que devolvem `LocatedOutput?` (`Adapter`, `Output`, `AdapterLuid`);
  - `internal DesktopDuplicationCapture(GpuContext, IDXGIOutput) : IScreenCapture`;
  - `internal GpuImage(ID3D11Texture2D Texture, int Width, int Height) : IVideoImage`;
  - `internal CaptureThread.Prepare() → IDisposable` (DPI por monitor na thread e timer de 1 ms);
  - `public PrimaryMonitorSource : IMonitorSource`;
  - `internal DxgiErrors.Classify(int)` e `DxgiErrors.ToException(int hresult, string what, Exception? inner = null)`, com `enum DxgiErrorKind { Timeout, CaptureLost, DeviceLost }`.

**Fatos do spike:**
- a saída do VDD fica na própria RTX (adaptador 0);
- `DuplicateOutput1` funciona com DPI por monitor só na thread;
- Win+L e UAC dão ACCESS_LOST e depois `E_ACCESSDENIED` enquanto a área de trabalho segura está aberta, e tentar de novo com espera resolve;
- a imagem não fica na RAM (`DesktopImageInSystemMemory = false`).

**Testes de GPU:** `[GpuFact]` só roda com `SCREENSHARE_GPU_TESTS=1` (o CI não tem placa de vídeo). Nesta task, rode também com a variável no PC do usuário, que tem a RTX e o monitor.

- [ ] **Step 1: Pacotes**

Em `host/ScreenShare.Video/ScreenShare.Video.csproj`:

```diff
@@ -4,8 +4,14 @@
     <TargetFramework>net10.0-windows</TargetFramework>
     <ImplicitUsings>enable</ImplicitUsings>
     <Nullable>enable</Nullable>
+    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
   </PropertyGroup>
 
+  <ItemGroup>
+    <PackageReference Include="Vortice.Direct3D11" Version="3.8.3" />
+    <PackageReference Include="Vortice.DXGI" Version="3.8.3" />
+  </ItemGroup>
+
   <ItemGroup>
     <ProjectReference Include="..\ScreenShare.Core\ScreenShare.Core.csproj" />
     <ProjectReference Include="..\ScreenShare.Display\ScreenShare.Display.csproj" />
```

- [ ] **Step 2: Testes**

`host/ScreenShare.Tests/Video/GpuFactAttribute.cs`:

```csharp
namespace ScreenShare.Tests.Video;

/// <summary>Teste que usa a placa de vídeo e um monitor de verdade: só roda com SCREENSHARE_GPU_TESTS=1 (o CI não tem GPU).</summary>
public sealed class GpuFactAttribute : FactAttribute
{
    public GpuFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SCREENSHARE_GPU_TESTS") != "1")
            Skip = "Precisa de placa de vídeo e de um monitor: defina SCREENSHARE_GPU_TESTS=1 para rodar.";
    }
}
```

`host/ScreenShare.Tests/Video/CaptureTests.cs`:

```csharp
using ScreenShare.Core.Video;
using ScreenShare.Video.Hardware;
using ScreenShare.Video.Pipeline;

namespace ScreenShare.Tests.Video;

public sealed class CaptureTests
{
    [Theory]
    [InlineData(unchecked((int)0x887A0027), "Timeout")]
    [InlineData(unchecked((int)0x887A0026), "CaptureLost")] // ACCESS_LOST: UAC, Win+L, troca de modo
    [InlineData(unchecked((int)0x80070005), "CaptureLost")] // E_ACCESSDENIED: área de trabalho segura
    [InlineData(unchecked((int)0x887A0022), "CaptureLost")] // NOT_CURRENTLY_AVAILABLE
    [InlineData(unchecked((int)0x887A0005), "DeviceLost")]  // DEVICE_REMOVED
    [InlineData(unchecked((int)0x887A0007), "DeviceLost")]  // DEVICE_RESET
    public void Dxgi_errors_are_classified(int hresult, string expected) =>
        Assert.Equal(expected, DxgiErrors.Classify(hresult).ToString());

    [Fact]
    public void Dxgi_errors_become_the_exceptions_the_pipeline_handles()
    {
        Assert.IsType<CaptureLostException>(DxgiErrors.ToException(DxgiErrors.AccessLost, "teste"));
        Assert.IsType<DeviceLostException>(DxgiErrors.ToException(DxgiErrors.DeviceRemoved, "teste"));
    }

    [GpuFact]
    public void Primary_monitor_is_captured_at_its_size_within_a_second()
    {
        using var thread = CaptureThread.Prepare();
        var monitor = new PrimaryMonitorSource().Current!;
        using var located = DxgiOutputLocator.Find(monitor.DeviceName)!;
        using var gpu = GpuContext.Create(located.Adapter);
        using var capture = new DesktopDuplicationCapture(gpu, located.Output);

        var deadline = DateTime.UtcNow.AddSeconds(1);
        var result = AcquireResult.Timeout;
        ulong presentUs = 0;
        while (result != AcquireResult.NewImage && DateTime.UtcNow < deadline)
            result = capture.TryAcquire(TimeSpan.FromMilliseconds(100), out presentUs);

        Assert.Equal(AcquireResult.NewImage, result);
        Assert.Equal((monitor.Width, monitor.Height), (capture.Width, capture.Height));
        Assert.InRange(presentUs, 1UL, PcClock.NowUs);
    }

    [GpuFact]
    public void Output_that_does_not_exist_is_not_found()
    {
        Assert.Null(DxgiOutputLocator.Find(@"\\.\DISPLAY999"));
    }
}
```

Run: `dotnet test host/ScreenShare.slnx --filter FullyQualifiedName~CaptureTests`
Expected: erro de compilação (`DxgiErrors`, `CaptureThread` e as outras classes não existem).

- [ ] **Step 3: Erros, device e localizador**

`host/ScreenShare.Video/Hardware/DxgiErrors.cs`:

```csharp
using ScreenShare.Video.Pipeline;

namespace ScreenShare.Video.Hardware;

internal enum DxgiErrorKind
{
    Timeout,

    /// <summary>A captura parou mas volta: reabrir com espera (UAC, Win+L, troca de modo, driver reiniciando).</summary>
    CaptureLost,

    /// <summary>A placa de vídeo foi removida ou reiniciada: recriar o device e tudo o que depende dele.</summary>
    DeviceLost,
}

/// <summary>Os HRESULTs que a captura DXGI devolve, e o que o pipeline faz com cada um.</summary>
internal static class DxgiErrors
{
    public const int InvalidCall = unchecked((int)0x887A0001);
    public const int NotFound = unchecked((int)0x887A0002);
    public const int Unsupported = unchecked((int)0x887A0004);
    public const int DeviceRemoved = unchecked((int)0x887A0005);
    public const int DeviceHung = unchecked((int)0x887A0006);
    public const int DeviceReset = unchecked((int)0x887A0007);
    public const int DriverInternalError = unchecked((int)0x887A0020);
    public const int NotCurrentlyAvailable = unchecked((int)0x887A0022);
    public const int ModeChangeInProgress = unchecked((int)0x887A0025);
    public const int AccessLost = unchecked((int)0x887A0026);
    public const int WaitTimeout = unchecked((int)0x887A0027);
    public const int SessionDisconnected = unchecked((int)0x887A0028);
    public const int AccessDenied = unchecked((int)0x887A002B);

    /// <summary>E_ACCESSDENIED: a área de trabalho segura está aberta (UAC, Win+L); o spike viu este ao reabrir.</summary>
    public const int GeneralAccessDenied = unchecked((int)0x80070005);

    /// <summary>Qualquer erro que não seja de placa é tratado como captura perdida: tentar de novo não custa nada.</summary>
    public static DxgiErrorKind Classify(int hresult) => hresult switch
    {
        WaitTimeout => DxgiErrorKind.Timeout,
        DeviceRemoved or DeviceHung or DeviceReset or DriverInternalError => DxgiErrorKind.DeviceLost,
        _ => DxgiErrorKind.CaptureLost,
    };

    /// <summary>A exceção que o pipeline entende: CaptureLostException (reabrir) ou DeviceLostException (recriar tudo).</summary>
    public static Exception ToException(int hresult, string what, Exception? inner = null)
    {
        var message = $"{what}: 0x{hresult:X8}";
        return Classify(hresult) == DxgiErrorKind.DeviceLost
            ? new DeviceLostException(message, inner)
            : new CaptureLostException(message, inner);
    }
}
```

`host/ScreenShare.Video/Hardware/GpuContext.cs`:

```csharp
using ScreenShare.Video.Pipeline;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ScreenShare.Video.Hardware;

/// <summary>
/// O device D3D11 da placa onde está a saída capturada: captura, conversão para NV12 e encoder usam este, sem cópias
/// entre placas. Protegido para várias threads (a de captura e a de eventos do encoder).
/// </summary>
internal sealed class GpuContext : IDisposable
{
    private GpuContext(ID3D11Device device, ID3D11DeviceContext context, uint vendorId, long adapterLuid)
    {
        Device = device;
        Context = context;
        VendorId = vendorId;
        AdapterLuid = adapterLuid;
    }

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }

    /// <summary>Fabricante da placa (0x10DE = NVIDIA): o encoder de hardware é escolhido pelo mesmo fabricante.</summary>
    public uint VendorId { get; }

    public long AdapterLuid { get; }

    /// <summary>adapter null = a placa padrão (testes de GPU sem captura).</summary>
    public static GpuContext Create(IDXGIAdapter1? adapter)
    {
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
        var result = D3D11.D3D11CreateDevice(adapter, adapter is null ? DriverType.Hardware : DriverType.Unknown,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport, levels, out ID3D11Device device,
            out ID3D11DeviceContext context);
        if (result.Failure) throw new DeviceLostException($"D3D11CreateDevice: 0x{result.Code:X8}");

        using (var multithread = device.QueryInterface<ID3D11Multithread>()) multithread.SetMultithreadProtected(true);
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        using var actual = dxgiDevice.GetAdapter();
        var description = actual.Description;
        return new GpuContext(device, context, description.VendorId, LuidOf(description.Luid));
    }

    public static long LuidOf(Vortice.Luid luid) => ((long)luid.HighPart << 32) | luid.LowPart;

    public void Dispose()
    {
        Context.Dispose();
        Device.Dispose();
    }
}
```

`host/ScreenShare.Video/Hardware/DxgiOutputLocator.cs`:

```csharp
using Vortice.DXGI;

namespace ScreenShare.Video.Hardware;

/// <summary>Uma saída DXGI e a placa dela; Dispose solta as duas.</summary>
internal sealed class LocatedOutput(IDXGIAdapter1 adapter, IDXGIOutput output) : IDisposable
{
    public IDXGIAdapter1 Adapter { get; } = adapter;
    public IDXGIOutput Output { get; } = output;
    public long AdapterLuid => GpuContext.LuidOf(Adapter.Description1.Luid);

    public void Dispose()
    {
        Output.Dispose();
        Adapter.Dispose();
    }
}

/// <summary>
/// Acha uma saída pelo nome (\\.\DISPLAYn) entre todas as placas. Usa uma factory nova a cada busca: depois de um
/// reinício do driver a lista de uma factory antiga não tem a saída nova.
/// </summary>
internal static class DxgiOutputLocator
{
    public static LocatedOutput? Find(string deviceName) =>
        Find(description => string.Equals(description.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));

    /// <summary>O monitor principal: a saída na origem (0,0) da área de trabalho.</summary>
    public static LocatedOutput? FindPrimary() =>
        Find(description => description.AttachedToDesktop && description.DesktopCoordinates.Left == 0 && description.DesktopCoordinates.Top == 0);

    private static LocatedOutput? Find(Func<OutputDescription, bool> match)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
            {
                if (match(output.Description)) return new LocatedOutput(adapter, output);
                output.Dispose();
            }
            adapter.Dispose();
        }
        return null;
    }
}
```

- [ ] **Step 4: Captura, thread e monitor principal**

`host/ScreenShare.Video/Hardware/DesktopDuplicationCapture.cs`:

```csharp
using System.Diagnostics;
using ScreenShare.Core.Video;
using ScreenShare.Video.Pipeline;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ScreenShare.Video.Hardware;

/// <summary>Imagem BGRA na placa de vídeo (a cópia própria da captura).</summary>
internal sealed class GpuImage(ID3D11Texture2D texture, int width, int height) : IVideoImage
{
    public ID3D11Texture2D Texture { get; } = texture;
    public int Width { get; } = width;
    public int Height { get; } = height;
}

/// <summary>
/// Captura de uma saída com DXGI Desktop Duplication. Cada imagem nova é copiada para uma textura própria (Last) e o
/// quadro do Windows é liberado na hora, para o Windows continuar juntando as mudanças. Precisa de DPI por monitor na
/// thread que a cria e usa (CaptureThread.Prepare).
/// </summary>
internal sealed class DesktopDuplicationCapture : IScreenCapture
{
    private readonly GpuContext _gpu;
    private readonly IDXGIOutputDuplication _duplication;
    private readonly ID3D11Texture2D _copy;

    public DesktopDuplicationCapture(GpuContext gpu, IDXGIOutput output)
    {
        _gpu = gpu;
        _duplication = Duplicate(gpu, output);
        var mode = _duplication.Description.ModeDescription;
        Width = (int)mode.Width;
        Height = (int)mode.Height;
        _copy = gpu.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)Width, (uint)Height, 1, 1,
            BindFlags.RenderTarget | BindFlags.ShaderResource));
        Last = new GpuImage(_copy, Width, Height);
    }

    public int Width { get; }
    public int Height { get; }
    public IVideoImage Last { get; }

    public AcquireResult TryAcquire(TimeSpan timeout, out ulong presentUs)
    {
        presentUs = 0;
        var result = _duplication.AcquireNextFrame((uint)timeout.TotalMilliseconds, out var info, out var resource);
        if (result.Code == DxgiErrors.WaitTimeout) return AcquireResult.Timeout;
        if (result.Failure) throw DxgiErrors.ToException(result.Code, "AcquireNextFrame");
        try
        {
            if (info.LastPresentTime == 0) return AcquireResult.PointerOnly; // só o mouse mexeu
            using var texture = resource.QueryInterface<ID3D11Texture2D>();
            _gpu.Context.CopyResource(_copy, texture);
            presentUs = PcClock.ToMicroseconds(info.LastPresentTime, Stopwatch.Frequency);
            return AcquireResult.NewImage;
        }
        finally
        {
            resource.Dispose();
            _duplication.ReleaseFrame(); // se falhar (ACCESS_LOST), a próxima AcquireNextFrame avisa
        }
    }

    public void Dispose()
    {
        _duplication.Dispose();
        _copy.Dispose();
    }

    private static IDXGIOutputDuplication Duplicate(GpuContext gpu, IDXGIOutput output)
    {
        try
        {
            try
            {
                using var output5 = output.QueryInterface<IDXGIOutput5>();
                return output5.DuplicateOutput1(gpu.Device, [Format.B8G8R8A8_UNorm]);
            }
            catch (SharpGenException e) when (e.HResult == DxgiErrors.Unsupported)
            {
                using var output1 = output.QueryInterface<IDXGIOutput1>();
                return output1.DuplicateOutput(gpu.Device);
            }
        }
        catch (SharpGenException e)
        {
            throw DxgiErrors.ToException(e.HResult, "duplicar a saída", e);
        }
    }
}
```

`host/ScreenShare.Video/Hardware/CaptureThread.cs`:

```csharp
using System.Runtime.InteropServices;

namespace ScreenShare.Video.Hardware;

/// <summary>
/// Preparo da thread de captura. DPI por monitor: o DuplicateOutput1 exige, e fica só nesta thread para não mudar o que
/// o resto do host lê das telas. Timer do Windows de 1 ms: sem ele, as esperas curtas do pipeline viram 15 ms.
/// Dispose desfaz os dois (na mesma thread).
/// </summary>
internal static class CaptureThread
{
    private const nint PerMonitorAwareV2 = -4;

    public static IDisposable Prepare()
    {
        var previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        timeBeginPeriod(1);
        return new Restore(previous);
    }

    private sealed class Restore(nint previous) : IDisposable
    {
        public void Dispose()
        {
            timeEndPeriod(1);
            if (previous != 0) SetThreadDpiAwarenessContext(previous);
        }
    }

    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint dpiContext);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);
}
```

`host/ScreenShare.Video/Hardware/PrimaryMonitorSource.cs`:

```csharp
using ScreenShare.Display;

namespace ScreenShare.Video.Hardware;

/// <summary>Captura o monitor principal em vez do virtual (opção --capturar principal, para depurar sem o driver).</summary>
public sealed class PrimaryMonitorSource : IMonitorSource
{
    public PrimaryMonitorSource() => Refresh();

    public VirtualMonitor? Current { get; private set; }

    /// <summary>O monitor principal não muda de nome durante a sessão: nunca dispara.</summary>
    public event Action? Changed
    {
        add { }
        remove { }
    }

    public VirtualMonitor? Refresh()
    {
        using var located = DxgiOutputLocator.FindPrimary();
        if (located is null) return Current = null;
        var description = located.Output.Description;
        var area = description.DesktopCoordinates;
        return Current = new VirtualMonitor(description.DeviceName, area.Left, area.Top, area.Right - area.Left,
            area.Bottom - area.Top, 100);
    }
}
```

Run: `dotnet test host/ScreenShare.slnx --filter FullyQualifiedName~CaptureTests`
Expected: 7 aprovados e 2 ignorados (os de GPU).

Run (PowerShell, no PC do usuário): `$env:SCREENSHARE_GPU_TESTS=1; dotnet test host/ScreenShare.slnx --filter FullyQualifiedName~CaptureTests; Remove-Item Env:SCREENSHARE_GPU_TESTS`
Expected: 9 aprovados. O monitor principal é capturado no tamanho dele em menos de 1 s.

- [ ] **Step 5: Rodar tudo, commit e push**

Run: `dotnet build host/ScreenShare.slnx` e `dotnet test host/ScreenShare.slnx`
Expected: 0 avisos; 340 aprovados e 3 ignorados.

```bash
git add host/ScreenShare.Video host/ScreenShare.Tests/Video
git commit -m "feat(video): captura DXGI Desktop Duplication do monitor, com device na placa da saída"
git push
```

---

### Task 8: encoder Media Foundation, NV12 na GPU e backend de hardware

**Por quê:** é a outra metade do hardware: converter a imagem para NV12 na placa, codificar com o encoder de hardware (NVENC) e juntar captura e encoder num `IVideoBackend`. Usa o que o spike validou (E4 a E6):
- MFT assíncrono, `PeakConstrainedVBR`, sem B-frames;
- o IDR forçado sai no mesmo quadro;
- uma saída por entrada;
- **sem IDR periódico**: o GOP vai no maior valor, e o teste confere 600 quadros sem IDR espontâneo.

**Files:**
- Modify: `host/ScreenShare.Video/ScreenShare.Video.csproj` (pacote Vortice.MediaFoundation 3.8.3)
- Create, todos em `host/ScreenShare.Video/Hardware/`:
  - `CodecApi.cs`, `EncoderCatalog.cs`;
  - `Nv12Converter.cs`, `MediaFoundationEncoder.cs`;
  - `HardwareBackend.cs`.
- Create: `host/ScreenShare.Video/VideoSourceFactory.cs`
- Modify: `host/ScreenShare.Video/Hardware/DxgiErrors.cs` (`IsDeviceLost`)
- Test: `host/ScreenShare.Tests/Video/EncoderTests.cs` (só GPU)

**Interfaces:**
- Consumes: `IVideoEncoder`, `IVideoBackend`, `EncoderSettings`, `EncodedFrame`, `EncoderUnavailableException`, `PipelineVideoSource` e `CodecChooser` (Task 6); `GpuContext`, `GpuImage`, `DxgiOutputLocator`, `DesktopDuplicationCapture`, `DxgiErrors` e `CaptureThread` (Task 7); `AnnexB.IsKeyframe` (Task 2).
- Produces (a Task 9 usa):
  - `public static VideoSourceFactory.CreateDefault(VideoOptions options, Action<string> log) → IVideoSource?` (null sem encoder de hardware);
  - `internal EncoderCatalog.HardwareCodecs`, `Find(VideoCodec, uint vendorId, long adapterLuid)` (prefere o encoder da mesma placa) e `NameOf(IMFActivate)`;
  - `internal MediaFoundationEncoder(GpuContext, IMFActivate, EncoderSettings) : IVideoEncoder`;
  - `internal HardwareBackend : IVideoBackend`.

**Regras desta task (da revisão da própria Task 8):**
- a amostra de saída que o Vortice devolve é um objeto novo sem AddRef: se ela é a nossa (encoder que não fornece amostras), não pode ser liberada de novo (`ReleaseOutputSample`), senão o processo cai;
- o construtor do encoder solta tudo o que criou e chama `ShutdownObject` se algo falhar no meio; o `HardwareBackend` transforma qualquer falha do construtor (que não seja placa perdida) em `EncoderUnavailableException`;
- depois de um STREAM_CHANGE, a saída é pedida de novo, para não perder o quadro.

**Regra desta task (da revisão da Task 6):** os erros de placa removida ou reiniciada (`DxgiErrors.IsDeviceLost`: DEVICE_REMOVED, HUNG, RESET, DRIVER_INTERNAL_ERROR) que o encoder vê no `Submit`, no evento de erro do MFT ou ao ser criado viram `DeviceLostException`, para o pipeline recriar a placa e não só o encoder. `DxgiErrors.cs` (Task 7) ganha `IsDeviceLost`.

**Regra desta task (da revisão da Task 3):** o `EncodedFrame.Data` é sempre uma cópia nova (`CopyBytes`), porque o `SessionWriter` guarda o array até ele sair pela rede.

**Decisão:** o `MaxQP` não é configurado. O spike confirmou que ele existe, mas não chegou a um valor. Os refinamentos de +100, +300 e +700 ms (Task 6) já tiram o borrão da tela parada. Se a verificação da Task 14 mostrar texto borrado depois que a tela para, o `MaxQP` entra como correção.

- [ ] **Step 1: Pacote**

Em `host/ScreenShare.Video/ScreenShare.Video.csproj`:

```diff
@@ -10,6 +10,7 @@
   <ItemGroup>
     <PackageReference Include="Vortice.Direct3D11" Version="3.8.3" />
     <PackageReference Include="Vortice.DXGI" Version="3.8.3" />
+    <PackageReference Include="Vortice.MediaFoundation" Version="3.8.3" />
   </ItemGroup>
 
   <ItemGroup>
```

- [ ] **Step 2: Testes (GPU)**

`host/ScreenShare.Tests/Video/EncoderTests.cs`:

```csharp
using System.Collections.Concurrent;
using System.Diagnostics;
using ScreenShare.Core.Protocol;
using ScreenShare.Core.Video;
using ScreenShare.Video;
using ScreenShare.Video.Hardware;
using ScreenShare.Video.Pipeline;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.MediaFoundation;

namespace ScreenShare.Tests.Video;

public sealed class EncoderTests
{
    [GpuFact]
    public void H265_starts_with_an_idr_with_parameter_sets_honors_a_forced_idr_and_is_fast() =>
        AssertEncodes(VideoCodec.H265);

    [GpuFact]
    public void H264_starts_with_an_idr_with_parameter_sets_honors_a_forced_idr_and_is_fast() =>
        AssertEncodes(VideoCodec.H264);

    [GpuFact]
    public void No_idr_appears_on_its_own_in_600_frames()
    {
        var frames = Encode(VideoCodec.H265, count: 600, forceAt: -1);

        Assert.True(frames[0].Frame.IsKeyframe);
        Assert.DoesNotContain(frames.Skip(1), f => f.Frame.IsKeyframe);
    }

    [GpuFact]
    public async Task Default_source_captures_and_encodes_the_primary_monitor()
    {
        var source = VideoSourceFactory.CreateDefault(new VideoOptions(), _ => { });
        Assert.NotNull(source);
        var monitor = new PrimaryMonitorSource();
        var output = new FakeOutput();

        var stream = await source.StartAsync(new VideoRequest(monitor, VideoCodec.H264 | VideoCodec.H265, VideoLink.Usb),
            output, CancellationToken.None);
        Assert.NotNull(stream);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (output.Frames.Count == 0)
            {
                Assert.True(DateTime.UtcNow < deadline, "nenhum quadro em 3 s");
                await Task.Delay(20);
            }
        }
        finally
        {
            await stream.DisposeAsync();
        }

        var config = Assert.IsType<ConfigMessage>(output.Sent[0]);
        Assert.Equal(monitor.Current!.Width, config.Width);
        Assert.Equal(monitor.Current.Height, config.Height);
        Assert.NotEmpty(config.CodecConfig);
        Assert.True(output.Frames[0].IsKeyframe);
    }

    [GpuFact]
    public void Our_output_sample_returned_by_the_encoder_is_released_only_once()
    {
        var own = MediaFactory.MFCreateSample();
        var returned = new IMFSample(own.NativePointer); // como o Vortice devolve a saída: objeto novo, sem AddRef

        MediaFoundationEncoder.ReleaseOutputSample(returned, own);

        own.AddRef();
        Assert.Equal(1u, own.Release()); // a única referência continua com o own
        own.Dispose();
    }

    [GpuFact]
    public void Encoder_that_fails_to_configure_releases_everything_and_the_next_one_opens()
    {
        using var gpu = GpuContext.Create(null);
        var activate = EncoderCatalog.Find(VideoCodec.H264, gpu.VendorId, gpu.AdapterLuid);
        Assert.NotNull(activate);

        Assert.ThrowsAny<Exception>(() =>
            new MediaFoundationEncoder(gpu, activate, new EncoderSettings(VideoCodec.H264, 16384, 16384, 60, 25_000, 50_000)));
        using var encoder = new MediaFoundationEncoder(gpu, activate, new EncoderSettings(VideoCodec.H264, 1920, 1080, 60, 25_000, 50_000));

        var deadline = Stopwatch.StartNew();
        while (!encoder.CanAccept)
        {
            Assert.True(deadline.ElapsedMilliseconds < 1000, "o encoder novo não pediu entrada em 1 s");
            Thread.Sleep(1);
        }
    }

    private static void AssertEncodes(VideoCodec codec)
    {
        var frames = Encode(codec, count: 30, forceAt: 10);

        Assert.True(frames[0].Frame.IsKeyframe);
        Assert.NotNull(AnnexB.ExtractParameterSets(frames[0].Frame.Data, codec));
        Assert.True(frames[10].Frame.IsKeyframe); // o IDR forçado sai no mesmo quadro
        Assert.DoesNotContain(frames.Skip(1).Take(9), f => f.Frame.IsKeyframe);
        Assert.Equal(Enumerable.Range(1, 30).Select(i => (ulong)i * 16_667), frames.Select(f => f.Frame.TimestampUs));
        var sorted = frames.Select(f => f.Ms).Order().ToArray();
        var p95 = sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1];
        Assert.True(p95 < 10, $"p95 da entrada à saída: {p95:0.0} ms");
    }

    /// <summary>Codifica quadros 2520×1080 de cor diferente, um por vez (o encoder devolve uma saída por entrada).</summary>
    private static List<(EncodedFrame Frame, double Ms)> Encode(VideoCodec codec, int count, int forceAt)
    {
        using var gpu = GpuContext.Create(null);
        var activate = EncoderCatalog.Find(codec, gpu.VendorId, gpu.AdapterLuid);
        Assert.NotNull(activate);
        using var encoder = new MediaFoundationEncoder(gpu, activate, new EncoderSettings(codec, 2520, 1080, 60, 25_000, 50_000));
        using var texture = gpu.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, 2520, 1080, 1, 1,
            BindFlags.RenderTarget | BindFlags.ShaderResource));
        using var target = gpu.Device.CreateRenderTargetView(texture);
        var image = new GpuImage(texture, 2520, 1080);
        var outputs = new BlockingCollection<(EncodedFrame Frame, long At)>();
        Exception? failure = null;
        encoder.Output += frame => outputs.Add((frame, Stopwatch.GetTimestamp()));
        encoder.Failed += error => failure = error;

        var results = new List<(EncodedFrame Frame, double Ms)>();
        for (var i = 0; i < count; i++)
        {
            var deadline = Stopwatch.StartNew();
            while (!encoder.CanAccept)
            {
                Assert.True(deadline.ElapsedMilliseconds < 1000, "o encoder não pediu entrada em 1 s");
                Thread.Sleep(0);
            }
            gpu.Context.ClearRenderTargetView(target, new Color4(i % 2 == 0 ? 0.9f : 0.1f, 0.5f, i % 7 / 7f, 1f));
            var submitted = Stopwatch.GetTimestamp();
            encoder.Submit(image, (ulong)(i + 1) * 16_667, forceKeyframe: i == 0 || i == forceAt);
            Assert.True(outputs.TryTake(out var output, TimeSpan.FromSeconds(1)), $"sem saída para o quadro {i}: {failure}");
            results.Add((output.Frame, Stopwatch.GetElapsedTime(submitted, output.At).TotalMilliseconds));
        }
        Assert.Null(failure);
        return results;
    }
}
```

Run: `dotnet test host/ScreenShare.slnx --filter FullyQualifiedName~EncoderTests`
Expected: erro de compilação (`MediaFoundationEncoder`, `EncoderCatalog` e `VideoSourceFactory` não existem).

- [ ] **Step 3: `ICodecAPI`, catálogo e erro de placa**

Em `host/ScreenShare.Video/Hardware/DxgiErrors.cs`:

```diff
@@ -41,6 +41,9 @@ internal static class DxgiErrors
         _ => DxgiErrorKind.CaptureLost,
     };
 
+    /// <summary>A placa de vídeo foi removida ou reiniciada (vale também para os erros que o encoder repassa).</summary>
+    public static bool IsDeviceLost(int hresult) => Classify(hresult) == DxgiErrorKind.DeviceLost;
+
     /// <summary>A exceção que o pipeline entende: CaptureLostException (reabrir) ou DeviceLostException (recriar tudo).</summary>
     public static Exception ToException(int hresult, string what, Exception? inner = null)
     {
```

`host/ScreenShare.Video/Hardware/CodecApi.cs`:

```csharp
using System.Runtime.InteropServices;
using SharpGen.Runtime;

namespace ScreenShare.Video.Hardware;

/// <summary>
/// ICodecAPI por vtable: o Vortice não mapeia o codecapi.h. Só os dois métodos usados (IsSupported e SetValue), com um
/// VARIANT de 24 bytes (VT_UI4 ou VT_BOOL). GUIDs conferidos no codecapi.h do mingw-w64 durante o spike.
/// </summary>
internal sealed unsafe class CodecApi : IDisposable
{
    public static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static readonly Guid RateControl = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid MeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static readonly Guid MaxBitRate = new("9651eae4-39b9-4ebf-85ef-d7f444ec7465");
    public static readonly Guid BufferSize = new("0db96574-b6a4-4c8b-8106-3773de0310cd");
    public static readonly Guid ForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");

    /// <summary>eAVEncCommonRateControlMode_PeakConstrainedVBR: média com teto (o spike escolheu este).</summary>
    public const uint PeakConstrainedVbr = 1;

    private const ushort VtBool = 11;
    private const ushort VtUi4 = 19;
    private static readonly Guid Iid = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");

    private nint _api;

    public CodecApi(ComObject transform)
    {
        var iid = Iid;
        Marshal.QueryInterface(transform.NativePointer, in iid, out _api);
        if (_api == 0) throw new InvalidOperationException("o encoder não tem ICodecAPI");
    }

    private void** Vtable => *(void***)_api;

    public bool IsSupported(Guid property) =>
        ((delegate* unmanaged[Stdcall]<nint, Guid*, int>)Vtable[3])(_api, &property) == 0;

    /// <summary>Devolve o HRESULT (0 = aceitou).</summary>
    public int SetUInt32(Guid property, uint value) => SetValue(property, new Variant24 { Vt = VtUi4, Value = value });

    public int SetBool(Guid property, bool value) =>
        SetValue(property, new Variant24 { Vt = VtBool, Value = value ? 0xFFFFUL : 0UL });

    public void Dispose()
    {
        if (_api == 0) return;
        Marshal.Release(_api);
        _api = 0;
    }

    private int SetValue(Guid property, Variant24 value) =>
        ((delegate* unmanaged[Stdcall]<nint, Guid*, Variant24*, int>)Vtable[9])(_api, &property, &value);

    [StructLayout(LayoutKind.Sequential, Size = 24)]
    private struct Variant24
    {
        public ushort Vt;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public ulong Value;
        public ulong Padding;
    }
}
```

`host/ScreenShare.Video/Hardware/EncoderCatalog.cs`:

```csharp
using ScreenShare.Core.Protocol;
using Vortice.MediaFoundation;

namespace ScreenShare.Video.Hardware;

/// <summary>
/// Os encoders de vídeo de hardware (MFTs) do Windows. A coleção do MFTEnumEx solta os itens quando é descartada, então
/// as listas ficam vivas enquanto o host roda (um IMFActivate pode ser ativado de novo depois de ShutdownObject).
/// </summary>
internal static class EncoderCatalog
{
    private const uint Hardware = 0x4;
    private const uint SortAndFilter = 0x40;

    private static readonly Lock Gate = new();
    private static readonly Dictionary<VideoCodec, IMFActivate[]> Lists = [];
    private static readonly List<IDisposable> KeepAlive = [];
    private static bool _started;

    /// <summary>Codecs com pelo menos um encoder de hardware (de qualquer placa). None se o Media Foundation não existe.</summary>
    public static VideoCodec HardwareCodecs
    {
        get
        {
            try
            {
                return (List(VideoCodec.H264).Length > 0 ? VideoCodec.H264 : VideoCodec.None)
                    | (List(VideoCodec.H265).Length > 0 ? VideoCodec.H265 : VideoCodec.None);
            }
            catch (Exception)
            {
                return VideoCodec.None; // Windows sem Media Foundation (edições N sem o pacote de mídia)
            }
        }
    }

    /// <summary>MFT_ENUM_ADAPTER_LUID: a placa a que o encoder de hardware pertence.</summary>
    private static readonly Guid AdapterLuidKey = new("1d39518c-e220-4da8-a07f-ba172552d6b1");

    /// <summary>
    /// O encoder do codec na mesma placa (LUID) da captura; se o driver não informar a placa, o primeiro do mesmo
    /// fabricante (0x10DE = NVIDIA). null se não houver.
    /// </summary>
    public static IMFActivate? Find(VideoCodec codec, uint vendorId, long adapterLuid)
    {
        var vendor = $"VEN_{vendorId:X4}";
        var sameVendor = List(codec)
            .Where(activate => string.Equals(VendorOf(activate), vendor, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return sameVendor.FirstOrDefault(activate => LuidOf(activate) == adapterLuid) ?? sameVendor.FirstOrDefault();
    }

    private static long? LuidOf(IMFActivate activate)
    {
        try
        {
            return (long)activate.GetUInt64(AdapterLuidKey);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string NameOf(IMFActivate activate)
    {
        try
        {
            return activate.GetString(TransformAttributeKeys.MftFriendlyNameAttribute);
        }
        catch (Exception)
        {
            return "encoder sem nome";
        }
    }

    private static string? VendorOf(IMFActivate activate)
    {
        try
        {
            return activate.GetString(TransformAttributeKeys.MftEnumHardwareVendorIdAttribute);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IMFActivate[] List(VideoCodec codec)
    {
        lock (Gate)
        {
            if (Lists.TryGetValue(codec, out var cached)) return cached;
            if (!_started)
            {
                MediaFactory.MFStartup(true).CheckError();
                _started = true;
            }
            var output = new RegisterTypeInfo
            {
                GuidMajorType = MediaTypeGuids.Video,
                GuidSubtype = codec == VideoCodec.H265 ? VideoFormatGuids.Hevc : VideoFormatGuids.H264,
            };
            var collection = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, Hardware | SortAndFilter, null, output);
            KeepAlive.Add(collection);
            var items = collection.ToArray();
            Lists[codec] = items;
            return items;
        }
    }
}
```

- [ ] **Step 4: Conversor NV12 e encoder**

`host/ScreenShare.Video/Hardware/Nv12Converter.cs`:

```csharp
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.MediaFoundation;

namespace ScreenShare.Video.Hardware;

/// <summary>
/// BGRA (faixa cheia) → NV12 BT.709 (faixa limitada), na GPU, com o ID3D11VideoProcessor. O processamento automático
/// fica desligado para não mexer no texto. As views das texturas do pool do encoder são guardadas e soltas no Dispose.
/// </summary>
internal sealed class Nv12Converter : IDisposable
{
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly ID3D11VideoProcessorEnumerator _enumerator;
    private readonly ID3D11VideoProcessor _processor;
    private readonly Dictionary<(nint Texture, uint Slice), (ID3D11Texture2D Texture, ID3D11VideoProcessorOutputView View)> _outputs = [];
    private ID3D11Texture2D? _inputTexture;
    private ID3D11VideoProcessorInputView? _inputView;

    public Nv12Converter(GpuContext gpu, int width, int height, int fps)
    {
        _videoDevice = gpu.Device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = gpu.Context.QueryInterface<ID3D11VideoContext>();
        _enumerator = _videoDevice.CreateVideoProcessorEnumerator(new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)width,
            InputHeight = (uint)height,
            OutputWidth = (uint)width,
            OutputHeight = (uint)height,
            InputFrameRate = new Rational((uint)fps, 1),
            OutputFrameRate = new Rational((uint)fps, 1),
            Usage = VideoUsage.OptimalSpeed,
        });
        _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);
        using var videoContext1 = gpu.Context.QueryInterface<ID3D11VideoContext1>();
        videoContext1.VideoProcessorSetStreamColorSpace1(_processor, 0, ColorSpaceType.RgbFullG22NoneP709);
        videoContext1.VideoProcessorSetOutputColorSpace1(_processor, ColorSpaceType.YcbcrStudioG22LeftP709);
        _videoContext.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false);
        _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
    }

    /// <summary>Converte a imagem para a amostra NV12 (uma textura do pool do encoder).</summary>
    public void Convert(ID3D11Texture2D source, IMFSample sample)
    {
        if (!ReferenceEquals(source, _inputTexture))
        {
            _inputView?.Dispose();
            _inputView = _videoDevice.CreateVideoProcessorInputView(source, _enumerator, new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            });
            _inputTexture = source;
        }

        using var buffer = sample.GetBufferByIndex(0);
        using var dxgiBuffer = buffer.QueryInterface<IMFDXGIBuffer>();
        var pointer = dxgiBuffer.GetResource(typeof(ID3D11Texture2D).GUID); // já vem com uma referência nossa
        var slice = dxgiBuffer.SubresourceIndex;
        if (_outputs.TryGetValue((pointer, slice), out var output))
        {
            System.Runtime.InteropServices.Marshal.Release(pointer);
        }
        else
        {
            var texture = new ID3D11Texture2D(pointer);
            var view = _videoDevice.CreateVideoProcessorOutputView(texture, _enumerator, new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2DArray,
                Texture2DArray = new Texture2DArrayVideoProcessorOutputView { MipSlice = 0, FirstArraySlice = slice, ArraySize = 1 },
            });
            output = (texture, view);
            _outputs[(pointer, slice)] = output;
        }

        _videoContext.VideoProcessorBlt(_processor, output.View, 0, 1,
            [new VideoProcessorStream { Enable = true, InputSurface = _inputView }]).CheckError();
    }

    public void Dispose()
    {
        foreach (var (texture, view) in _outputs.Values)
        {
            view.Dispose();
            texture.Dispose();
        }
        _outputs.Clear();
        _inputView?.Dispose();
        _processor.Dispose();
        _enumerator.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
    }
}
```

`host/ScreenShare.Video/Hardware/MediaFoundationEncoder.cs`:

```csharp
using System.Runtime.InteropServices;
using ScreenShare.Core.Protocol;
using ScreenShare.Core.Video;
using ScreenShare.Video.Pipeline;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace ScreenShare.Video.Hardware;

/// <summary>
/// Encoder de hardware (MFT assíncrono do Media Foundation, NVENC na placa NVIDIA), como validado no spike:
/// - entrada NV12 vinda do Nv12Converter, em amostras de um pool na GPU;
/// - baixa latência, PeakConstrainedVBR com pico de 2×, sem B-frames (o NVENC já não usa) e sem IDR periódico
///   (GOP no maior valor: IDR só no começo, quando pedido ou depois de descarte);
/// - uma thread própria lê os eventos do MFT: pedido de entrada (CanAccept) e saída pronta (Output, cópia dos bytes).
/// O timestamp viaja no SampleTime (µs × 10 = unidades de 100 ns) e volta na saída.
/// </summary>
internal sealed class MediaFoundationEncoder : IVideoEncoder
{
    /// <summary>GOP "infinito": o maior valor que o NVENC aceita sem IDR sozinho em 600 quadros (conferido na GPU).</summary>
    public const uint MaxGop = int.MaxValue;

    private const int StreamChange = unchecked((int)0xC00D6D61); // MF_E_TRANSFORM_STREAM_CHANGE
    private const int ProvidesSamples = 0x100; // MFT_OUTPUT_STREAM_PROVIDES_SAMPLES
    private const int CanProvideSamples = 0x200; // MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES

    private readonly EncoderSettings _settings;
    private readonly IMFActivate _activate;
    private readonly IMFTransform _transform;
    private readonly IMFDXGIDeviceManager _manager;
    private readonly CodecApi _api;
    private readonly IMFMediaType _inputType;
    private readonly IMFVideoSampleAllocatorEx _allocator;
    private readonly Nv12Converter _converter;
    private readonly IMFMediaEventGenerator _events;
    private readonly bool _providesSamples;
    private readonly int _outputSize;
    private readonly Thread _thread;
    private int _inputRequests;
    private int _disposed;
    private volatile bool _stopping;

    /// <summary>
    /// Configura e começa o encoder. Se algo falhar no meio, solta tudo o que já criou e desativa o MFT
    /// (ShutdownObject): sem isso, o IMFActivate devolveria na próxima tentativa o mesmo MFT meio configurado.
    /// </summary>
    public MediaFoundationEncoder(GpuContext gpu, IMFActivate activate, EncoderSettings settings)
    {
        _settings = settings;
        _activate = activate;
        var created = new Stack<IDisposable>();
        try
        {
            _transform = Track(created, activate.ActivateObject<IMFTransform>());
            _manager = Track(created, MediaFactory.MFCreateDXGIDeviceManager());
            _api = Track(created, new CodecApi(_transform));
            _inputType = Track(created, InputType(settings));
            _allocator = Track(created,
                new IMFVideoSampleAllocatorEx(MediaFactory.MFCreateVideoSampleAllocatorEx(typeof(IMFVideoSampleAllocatorEx).GUID)));
            (_providesSamples, _outputSize) = Configure(gpu, settings);
            _converter = Track(created, new Nv12Converter(gpu, settings.Width, settings.Height, settings.Fps));
            _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
            _events = Track(created, _transform.QueryInterface<IMFMediaEventGenerator>());
        }
        catch
        {
            while (created.TryPop(out var item)) item.Dispose();
            activate.ShutdownObject();
            throw;
        }
        _thread = new Thread(RunEvents) { IsBackground = true, Name = "ScreenShare encoder", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private static T Track<T>(Stack<IDisposable> created, T item) where T : IDisposable
    {
        created.Push(item);
        return item;
    }

    /// <summary>Atributos, ajustes do ICodecAPI, tipos de saída e de entrada e o pool de amostras NV12.</summary>
    private (bool ProvidesSamples, int OutputSize) Configure(GpuContext gpu, EncoderSettings settings)
    {
        _transform.Attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
        _transform.Attributes.Set(CodecApi.LowLatency, 1u); // MF_LOW_LATENCY tem o mesmo GUID

        _manager.ResetDevice(gpu.Device).CheckError();
        _transform.ProcessMessage(TMessageType.MessageSetD3DManager, (UIntPtr)(nuint)_manager.NativePointer);

        _api.SetBool(CodecApi.LowLatency, true);
        _api.SetUInt32(CodecApi.GopSize, MaxGop);
        _api.SetUInt32(CodecApi.RateControl, CodecApi.PeakConstrainedVbr);
        var bitrate = (uint)settings.BitrateKbps * 1000;
        var peak = (uint)settings.PeakBitrateKbps * 1000;
        _api.SetUInt32(CodecApi.MeanBitRate, bitrate);
        _api.SetUInt32(CodecApi.MaxBitRate, peak);
        _api.SetUInt32(CodecApi.BufferSize, peak / (uint)settings.Fps * 3); // ~3 quadros no pico

        using (var outputType = OutputType(settings, bitrate)) _transform.SetOutputType(0, outputType, 0);
        _transform.SetInputType(0, _inputType, 0);
        var info = _transform.GetOutputStreamInfo(0);

        _allocator.SetDirectXManager(_manager);
        using (var attributes = MediaFactory.MFCreateAttributes(2))
        {
            attributes.Set(TransformAttributeKeys.D3D11Bindflags, (uint)(BindFlags.RenderTarget | BindFlags.VideoEncoder));
            attributes.Set(TransformAttributeKeys.D3D11Usage, (uint)ResourceUsage.Default);
            _allocator.InitializeSampleAllocatorEx(3, 6, attributes, _inputType);
        }
        return ((info.Flags & (ProvidesSamples | CanProvideSamples)) != 0, Math.Max(info.Size, settings.Width * settings.Height));
    }

    public VideoCodec Codec => _settings.Codec;
    public int Width => _settings.Width;
    public int Height => _settings.Height;
    public bool CanAccept => Volatile.Read(ref _inputRequests) > 0;

    public event Action<EncodedFrame>? Output;
    public event Action<Exception>? Failed;

    public void Submit(IVideoImage image, ulong timestampUs, bool forceKeyframe)
    {
        if (Interlocked.Decrement(ref _inputRequests) < 0)
        {
            Interlocked.Increment(ref _inputRequests);
            throw new InvalidOperationException("o encoder não pediu entrada");
        }
        try
        {
            using var sample = _allocator.AllocateSample();
            _converter.Convert(((GpuImage)image).Texture, sample);
            sample.SampleTime = (long)timestampUs * 10;
            sample.SampleDuration = 10_000_000 / _settings.Fps;
            if (forceKeyframe) _api.SetUInt32(CodecApi.ForceKeyFrame, 1); // vale para o próximo quadro (o spike conferiu)
            _transform.ProcessInput(0, sample, 0);
        }
        catch (SharpGenException e) when (DxgiErrors.IsDeviceLost(e.HResult))
        {
            throw new DeviceLostException($"encoder: 0x{e.HResult:X8}", e); // o pipeline recria a placa, não só o encoder
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _stopping = true;
        try
        {
            using var shutdown = _transform.QueryInterface<IMFShutdown>();
            shutdown.Shutdown(); // destrava o GetEvent da thread de eventos
        }
        catch (Exception)
        {
            // o MFT já parou
        }
        _thread.Join(TimeSpan.FromSeconds(2));
        _events.Dispose();
        _converter.Dispose();
        _allocator.Dispose();
        _inputType.Dispose();
        _api.Dispose();
        _transform.Dispose();
        _activate.ShutdownObject();
        _manager.Dispose();
    }

    private void RunEvents()
    {
        try
        {
            while (!_stopping)
            {
                using var mediaEvent = _events.GetEvent(0);
                if (mediaEvent.EventType == MediaEventTypes.TransformNeedInput) Interlocked.Increment(ref _inputRequests);
                else if (mediaEvent.EventType == MediaEventTypes.TransformHaveOutput) DrainOutput();
                else if (mediaEvent.EventType == MediaEventTypes.Error) throw new SharpGenException(mediaEvent.Status);
            }
        }
        catch (Exception e) when (!_stopping)
        {
            // Placa removida ou reiniciada vira DeviceLostException: o pipeline recria tudo, não só o encoder.
            Failed?.Invoke(e is SharpGenException sharpGen && DxgiErrors.IsDeviceLost(sharpGen.HResult)
                ? new DeviceLostException($"encoder: 0x{sharpGen.HResult:X8}", e)
                : e);
        }
        catch (Exception)
        {
            // encerrando: o Shutdown faz o GetEvent falhar
        }
    }

    /// <summary>Pega a saída pronta. Depois de uma troca de formato (STREAM_CHANGE) tenta uma vez mais, para não perder o quadro.</summary>
    private void DrainOutput()
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var buffer = new OutputDataBuffer { StreamID = 0 };
            IMFSample? own = null;
            if (!_providesSamples)
            {
                own = MediaFactory.MFCreateSample();
                using var memory = MediaFactory.MFCreateMemoryBuffer(_outputSize);
                own.AddBuffer(memory);
                buffer.Sample = own;
            }
            try
            {
                var result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref buffer, out _);
                if (result.Code == StreamChange)
                {
                    using var available = _transform.GetOutputAvailableType(0, 0);
                    _transform.SetOutputType(0, available, 0);
                    continue;
                }
                result.CheckError();
                var data = CopyBytes(buffer.Sample!);
                var timestamp = (ulong)(buffer.Sample!.SampleTime / 10);
                Output?.Invoke(new EncodedFrame(timestamp, AnnexB.IsKeyframe(data, _settings.Codec), data));
                return;
            }
            finally
            {
                buffer.Events?.Dispose();
                ReleaseOutputSample(buffer.Sample, own);
                own?.Dispose();
            }
        }
    }

    /// <summary>
    /// O Vortice devolve a amostra de saída num objeto novo que não fez AddRef. Se é a nossa (own), esse objeto não pode
    /// liberar nada: o own já libera a única referência, e liberar duas vezes derruba o processo. Se o MFT forneceu a
    /// amostra, o objeto novo é a referência que o MFT nos deu, e é liberado uma vez.
    /// </summary>
    internal static void ReleaseOutputSample(IMFSample? returned, IMFSample? own)
    {
        if (returned is null || ReferenceEquals(returned, own)) return;
        if (own is not null && returned.NativePointer == own.NativePointer)
        {
            returned.NativePointer = IntPtr.Zero;
            return;
        }
        returned.Dispose();
    }

    private static byte[] CopyBytes(IMFSample sample)
    {
        using var contiguous = sample.ConvertToContiguousBuffer();
        contiguous.Lock(out var pointer, out _, out var length);
        try
        {
            var data = new byte[length];
            Marshal.Copy(pointer, data, 0, length);
            return data;
        }
        finally
        {
            contiguous.Unlock();
        }
    }

    private static IMFMediaType OutputType(EncoderSettings settings, uint bitrate)
    {
        var type = MediaFactory.MFCreateMediaType();
        type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        type.Set(MediaTypeAttributeKeys.Subtype, settings.Codec == VideoCodec.H265 ? VideoFormatGuids.Hevc : VideoFormatGuids.H264);
        SetVideoBasics(type, settings);
        type.Set(MediaTypeAttributeKeys.AvgBitrate, bitrate);
        type.Set(MediaTypeAttributeKeys.Mpeg2Profile, settings.Codec == VideoCodec.H264 ? 100u : 1u); // High; Main
        type.Set(MediaTypeAttributeKeys.YuvMatrix, 1u); // BT.709
        type.Set(MediaTypeAttributeKeys.VideoNominalRange, 2u); // 16–235
        type.Set(MediaTypeAttributeKeys.VideoPrimaries, 2u); // BT.709
        type.Set(MediaTypeAttributeKeys.TransferFunction, 5u); // BT.709
        return type;
    }

    private static IMFMediaType InputType(EncoderSettings settings)
    {
        var type = MediaFactory.MFCreateMediaType();
        type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
        SetVideoBasics(type, settings);
        return type;
    }

    private static void SetVideoBasics(IMFMediaType type, EncoderSettings settings)
    {
        type.Set(MediaTypeAttributeKeys.FrameSize, ((ulong)settings.Width << 32) | (uint)settings.Height);
        type.Set(MediaTypeAttributeKeys.FrameRate, ((ulong)settings.Fps << 32) | 1u);
        type.Set(MediaTypeAttributeKeys.PixelAspectRatio, (1UL << 32) | 1u);
        type.Set(MediaTypeAttributeKeys.InterlaceMode, 2u); // progressivo
    }
}
```

- [ ] **Step 5: Backend e fábrica**

`host/ScreenShare.Video/Hardware/HardwareBackend.cs`:

```csharp
using ScreenShare.Core.Protocol;
using ScreenShare.Video.Pipeline;
using SharpGen.Runtime;

namespace ScreenShare.Video.Hardware;

/// <summary>
/// A placa de vídeo de verdade: captura DXGI e encoder Media Foundation no mesmo device D3D11, criado na placa da
/// primeira saída aberta. Se a saída aparecer em outra placa, pede para recriar tudo (DeviceLostException).
/// </summary>
internal sealed class HardwareBackend : IVideoBackend
{
    private GpuContext? _gpu;

    public VideoCodec HardwareCodecs => EncoderCatalog.HardwareCodecs;

    public IScreenCapture OpenCapture(string deviceName)
    {
        using var located = DxgiOutputLocator.Find(deviceName) ?? throw new CaptureLostException($"saída {deviceName} não encontrada");
        if (_gpu is null) _gpu = GpuContext.Create(located.Adapter);
        else if (_gpu.AdapterLuid != located.AdapterLuid) throw new DeviceLostException("a saída passou para outra placa de vídeo");
        try
        {
            return new DesktopDuplicationCapture(_gpu, located.Output);
        }
        catch (SharpGenException e)
        {
            throw DxgiErrors.ToException(e.HResult, "abrir a captura", e);
        }
    }

    public IVideoEncoder CreateEncoder(EncoderSettings settings)
    {
        var gpu = _gpu ?? throw new InvalidOperationException("a captura abre antes do encoder");
        var activate = EncoderCatalog.Find(settings.Codec, gpu.VendorId, gpu.AdapterLuid)
            ?? throw new EncoderUnavailableException($"a placa não tem encoder {CodecChooser.Name(settings.Codec)}");
        try
        {
            return new MediaFoundationEncoder(gpu, activate, settings);
        }
        catch (SharpGenException e) when (DxgiErrors.IsDeviceLost(e.HResult))
        {
            throw new DeviceLostException($"{EncoderCatalog.NameOf(activate)}: 0x{e.HResult:X8}", e);
        }
        catch (SharpGenException e)
        {
            throw new EncoderUnavailableException($"{EncoderCatalog.NameOf(activate)}: 0x{e.HResult:X8}", e);
        }
        catch (Exception e) when (e is not DeviceLostException)
        {
            throw new EncoderUnavailableException($"{EncoderCatalog.NameOf(activate)}: {e.Message}", e);
        }
    }

    public void Dispose()
    {
        _gpu?.Dispose();
        _gpu = null;
    }
}
```

`host/ScreenShare.Video/VideoSourceFactory.cs`:

```csharp
using ScreenShare.Core.Protocol;
using ScreenShare.Video.Hardware;
using ScreenShare.Video.Pipeline;

namespace ScreenShare.Video;

public static class VideoSourceFactory
{
    /// <summary>Vídeo com captura DXGI e encoder de hardware; null se o PC não tem encoder de hardware.</summary>
    public static IVideoSource? CreateDefault(VideoOptions options, Action<string> log)
    {
        var codecs = EncoderCatalog.HardwareCodecs;
        if (codecs == VideoCodec.None)
        {
            log("Nenhum encoder de vídeo de hardware encontrado: os celulares vão conectar sem vídeo.");
            return null;
        }
        var names = new List<string>();
        if (codecs.HasFlag(VideoCodec.H265)) names.Add("H.265");
        if (codecs.HasFlag(VideoCodec.H264)) names.Add("H.264");
        log($"Encoders de vídeo de hardware: {string.Join(" e ", names)}.");
        return new PipelineVideoSource(() => new HardwareBackend(), codecs, options, TimeProvider.System, log, CaptureThread.Prepare);
    }
}
```

Run: `dotnet test host/ScreenShare.slnx --filter FullyQualifiedName~EncoderTests`
Expected: 6 ignorados (sem a variável).

Run (PowerShell, no PC do usuário): `$env:SCREENSHARE_GPU_TESTS=1; dotnet test host/ScreenShare.slnx --filter "FullyQualifiedName~EncoderTests|FullyQualifiedName~CaptureTests"; Remove-Item Env:SCREENSHARE_GPU_TESTS`
Expected: 15 aprovados. O que eles conferem:
- H.265 e H.264 começam por IDR com os parâmetros;
- o IDR forçado no quadro 10 sai nesse quadro;
- o p95 da entrada à saída fica abaixo de 10 ms;
- 600 quadros saem sem IDR espontâneo;
- a amostra de saída devolvida pelo encoder é liberada uma vez só, e um encoder que falha ao configurar solta tudo e deixa o próximo abrir;
- a fábrica captura e codifica o monitor principal de ponta a ponta (`CONFIG` com os parâmetros + IDR).

Se o teste dos 600 quadros falhar (aparecer um IDR sozinho), o encoder não aceitou `MaxGop`. Nesse caso, troque `MaxGop` por `65535` em `MediaFoundationEncoder.cs`, rode de novo e anote no relatório.

- [ ] **Step 6: Rodar tudo, commit e push**

Run: `dotnet build host/ScreenShare.slnx` e `dotnet test host/ScreenShare.slnx`
Expected: 0 avisos; 340 aprovados e 9 ignorados.

```bash
git add host/ScreenShare.Video host/ScreenShare.Tests/Video
git commit -m "feat(video): encoder de hardware Media Foundation com NV12 na GPU e fábrica do vídeo"
git push
```

---

### Task 9: DevHost com vídeo, opções, `--gravar` e estatísticas

**Por quê:** liga o vídeo de verdade no DevHost e dá as ferramentas de conferência:
- `--capturar principal`, para testar sem o monitor virtual;
- `--gravar`, um arquivo Annex-B que toca no ffplay;
- uma linha de estatísticas a cada 5 s.

Com esta task o PC já manda vídeo de verdade. O app ainda ignora os quadros (as Tasks 10 a 12 fazem o celular mostrar).

**Files:**
- Create: `host/ScreenShare.DevHost/DevHostOptions.cs`, `host/ScreenShare.DevHost/VideoStatsLine.cs`
- Create: `host/ScreenShare.Video/VideoSourceDecorators.cs` (`MonitorOverrideVideoSource`, `RecordingVideoSource`)
- Modify: `host/ScreenShare.DevHost/HostServer.cs` (estatísticas: RTT pelo PONG, descartes pelo `KeyframeNeeded`, `statsInterval`)
- Modify: `host/ScreenShare.DevHost/Program.cs` (opções e vídeo)
- Test: `host/ScreenShare.Tests/DevHost/DevHostOptionsTests.cs`, `host/ScreenShare.Tests/Video/VideoSourceDecoratorsTests.cs`

**Interfaces:**
- Consumes:
  - `VideoSourceFactory.CreateDefault` (Task 8) e `PrimaryMonitorSource` (Task 7);
  - `VideoOptions` (Task 6);
  - `IVideoSource`, `VideoStats` e o `HostServer` (Task 5);
  - os falsos `FakeMonitor` e `FakeOutput` (Task 6).
- Produces:
  - `DevHostOptions.Parse(IReadOnlyList<string>) → DevHostOptions(bool NoMonitor, bool NoVideo, bool CapturePrimary, string? RecordPath, VideoOptions Video)`, que lança `OptionsException` com a mensagem para o usuário;
  - `DevHostOptions.Usage`;
  - `VideoStatsLine.Format(VideoStats previous, VideoStats current, TimeSpan elapsed, int drops, double? rttMs) → string`;
  - `MonitorOverrideVideoSource(IVideoSource inner, Func<IMonitorSource> monitor)` e `RecordingVideoSource(IVideoSource inner, string path, Action<string> log)` (o arquivo fecha em qualquer saída, inclusive quando o vídeo falha ao abrir: a próxima sessão grava nele de novo);
  - no `HostServer`, o parâmetro novo `TimeSpan? statsInterval = null` (padrão 5 s).

- [ ] **Step 1: Testes**

`host/ScreenShare.Tests/DevHost/DevHostOptionsTests.cs`:

```csharp
using ScreenShare.Core.Protocol;
using ScreenShare.DevHost;
using ScreenShare.Video;

namespace ScreenShare.Tests.DevHost;

public sealed class DevHostOptionsTests
{
    [Fact]
    public void No_arguments_give_the_defaults()
    {
        var options = DevHostOptions.Parse([]);

        Assert.Equal(new DevHostOptions(false, false, false, null, new VideoOptions()), options);
        Assert.Equal(60, options.Video.Fps);
        Assert.Null(options.Video.BitrateMbps);
        Assert.Equal(VideoCodec.None, options.Video.Codec);
    }

    [Fact]
    public void Every_option_is_read()
    {
        var options = DevHostOptions.Parse(["--sem-monitor", "--capturar", "principal", "--gravar", "teste.h265",
            "--fps", "30", "--bitrate", "12", "--codec", "h264"]);

        Assert.True(options.NoMonitor);
        Assert.True(options.CapturePrimary);
        Assert.Equal("teste.h265", options.RecordPath);
        Assert.Equal(new VideoOptions(30, 12, VideoCodec.H264), options.Video);
    }

    [Fact]
    public void No_video_is_read()
    {
        Assert.True(DevHostOptions.Parse(["--sem-video"]).NoVideo);
    }

    [Theory]
    [InlineData("--fps", "0")]
    [InlineData("--fps", "121")]
    [InlineData("--fps", "sessenta")]
    [InlineData("--bitrate", "0")]
    [InlineData("--codec", "av1")]
    [InlineData("--capturar", "segundo")]
    [InlineData("--monitor", "1")]
    public void Invalid_options_are_rejected_with_a_message(string option, string value)
    {
        var error = Assert.Throws<OptionsException>(() => DevHostOptions.Parse([option, value]));

        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public void Option_without_its_value_is_rejected()
    {
        Assert.Throws<OptionsException>(() => DevHostOptions.Parse(["--gravar"]));
        Assert.Throws<OptionsException>(() => DevHostOptions.Parse(["--gravar", "--fps", "30"]));
    }

    [Fact]
    public void Stats_line_shows_rates_for_the_period_in_portuguese()
    {
        var before = new VideoStats(2520, 1080, VideoCodec.H265, 100, 1_000_000, 1, 2.5);
        var after = new VideoStats(2520, 1080, VideoCodec.H265, 400, 8_500_000, 3, 3.14);

        var line = VideoStatsLine.Format(before, after, TimeSpan.FromSeconds(5), drops: 1, rttMs: 4.25);

        Assert.Equal("Vídeo: H.265 2520×1080 · 60,0 fps · 12,0 Mbps · 2 IDR · 1 descartes · encode p95 3,1 ms · RTT 4,3 ms", line);
    }

    [Fact]
    public void Stats_line_without_rtt_shows_a_dash()
    {
        var line = VideoStatsLine.Format(VideoStats.Empty, VideoStats.Empty, TimeSpan.FromSeconds(5), 0, null);

        Assert.EndsWith("RTT —", line);
    }
}
```

`host/ScreenShare.Tests/Video/VideoSourceDecoratorsTests.cs`:

```csharp
using ScreenShare.Core.Protocol;
using ScreenShare.Display;
using ScreenShare.Video;

namespace ScreenShare.Tests.Video;

public sealed class VideoSourceDecoratorsTests : IDisposable
{
    private static readonly VirtualMonitor Primary = new(@"\\.\DISPLAY1", 0, 0, 3440, 1440, 100);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screenshare-tests", Guid.NewGuid().ToString("N"));
    private readonly CapturingSource _inner = new();

    public VideoSourceDecoratorsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static VideoRequest Request() =>
        new(VirtualMonitorLease.Without(MonitorRequest.Normalize(2400, 1080, 420)), VideoCodec.H265, VideoLink.Usb);

    [Fact]
    public async Task Override_captures_the_given_monitor_and_keeps_the_rest_of_the_request()
    {
        var source = new MonitorOverrideVideoSource(_inner, () => new FakeMonitor(Primary));

        await source.StartAsync(Request(), new FakeOutput(), CancellationToken.None);

        Assert.Equal(Primary, _inner.Request!.Monitor.Current);
        Assert.Equal((VideoCodec.H265, VideoLink.Usb), (_inner.Request.PhoneCodecs, _inner.Request.Link));
    }

    [Fact]
    public async Task Recording_writes_every_frame_to_the_file_and_passes_everything_on()
    {
        var path = Path.Combine(_dir, "video.h265");
        var output = new FakeOutput();
        var source = new RecordingVideoSource(_inner, path, _ => { });

        var stream = await source.StartAsync(Request(), output, CancellationToken.None);
        _inner.Output!.OnConfig(new ConfigMessage(2400, 1080, VideoCodec.H265, 50_000, []));
        _inner.Output.OnFrame(new FrameMessage(1, true, [0, 0, 0, 1, 0x26]));
        _inner.Output.OnFrame(new FrameMessage(2, false, [0, 0, 0, 1, 0x02]));
        await stream!.DisposeAsync();

        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x26, 0, 0, 0, 1, 0x02 }, File.ReadAllBytes(path));
        Assert.Equal(3, output.Sent.Count);
    }

    [Fact]
    public async Task Recording_file_is_closed_when_the_video_fails_to_start()
    {
        var path = Path.Combine(_dir, "video.h265");
        var source = new RecordingVideoSource(new FailingSource(), path, _ => { });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            source.StartAsync(Request(), new FakeOutput(), CancellationToken.None));

        File.Delete(path); // aberto ainda, isto falharia no Windows
    }

    private sealed class FailingSource : IVideoSource
    {
        public Task<IVideoStream?> StartAsync(VideoRequest request, IVideoOutput output, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("falha falsa ao abrir");
    }

    private sealed class CapturingSource : IVideoSource
    {
        public VideoRequest? Request { get; private set; }
        public IVideoOutput? Output { get; private set; }

        public Task<IVideoStream?> StartAsync(VideoRequest request, IVideoOutput output, CancellationToken cancellationToken)
        {
            Request = request;
            Output = output;
            return Task.FromResult<IVideoStream?>(new NothingStream());
        }
    }

    private sealed class NothingStream : IVideoStream
    {
        public VideoStats Stats => VideoStats.Empty;
        public void RequestKeyframe() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
```

Run: `dotnet test host/ScreenShare.slnx --filter "FullyQualifiedName~DevHostOptionsTests|FullyQualifiedName~VideoSourceDecoratorsTests"`
Expected: erro de compilação.

- [ ] **Step 2: Opções, linha de estatísticas e decoradores**

`host/ScreenShare.DevHost/DevHostOptions.cs`:

```csharp
using System.Globalization;
using ScreenShare.Core.Protocol;
using ScreenShare.Video;

namespace ScreenShare.DevHost;

/// <summary>Opção de linha de comando inválida; a mensagem vai para o usuário.</summary>
public sealed class OptionsException(string message) : Exception(message);

/// <summary>As opções do DevHost (fora os comandos do driver).</summary>
public sealed record DevHostOptions(bool NoMonitor, bool NoVideo, bool CapturePrimary, string? RecordPath, VideoOptions Video)
{
    public const string Usage =
        "Opções: --sem-monitor, --sem-video, --capturar principal, --gravar <arquivo>, --fps <1-120>, --bitrate <Mbps>, --codec h264|h265|auto";

    public static DevHostOptions Parse(IReadOnlyList<string> args)
    {
        bool noMonitor = false, noVideo = false, capturePrimary = false;
        string? record = null;
        int fps = 60;
        int? bitrate = null;
        var codec = VideoCodec.None;

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--sem-monitor":
                    noMonitor = true;
                    break;
                case "--sem-video":
                    noVideo = true;
                    break;
                case "--capturar":
                    if (Value(args, ref i, "--capturar") != "principal")
                        throw new OptionsException("--capturar só aceita \"principal\" (o monitor principal, para depurar sem o driver).");
                    capturePrimary = true;
                    break;
                case "--gravar":
                    record = Value(args, ref i, "--gravar");
                    break;
                case "--fps":
                    fps = Number(Value(args, ref i, "--fps"), "--fps", 1, 120);
                    break;
                case "--bitrate":
                    bitrate = Number(Value(args, ref i, "--bitrate"), "--bitrate", 1, 500);
                    break;
                case "--codec":
                    codec = Value(args, ref i, "--codec") switch
                    {
                        "h264" => VideoCodec.H264,
                        "h265" => VideoCodec.H265,
                        "auto" => VideoCodec.None,
                        _ => throw new OptionsException("--codec aceita h264, h265 ou auto."),
                    };
                    break;
                default:
                    throw new OptionsException($"Opção desconhecida: {args[i]}");
            }
        }
        return new DevHostOptions(noMonitor, noVideo, capturePrimary, record, new VideoOptions(fps, bitrate, codec));
    }

    private static string Value(IReadOnlyList<string> args, ref int i, string option)
    {
        if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            throw new OptionsException($"{option} precisa de um valor.");
        return args[++i];
    }

    private static int Number(string text, string option, int min, int max)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            throw new OptionsException($"{option} precisa de um número de {min} a {max}.");
        return value;
    }
}
```

`host/ScreenShare.DevHost/VideoStatsLine.cs`:

```csharp
using System.Globalization;
using ScreenShare.Core.Protocol;
using ScreenShare.Video;

namespace ScreenShare.DevHost;

/// <summary>A linha de estatísticas do vídeo que o console mostra a cada 5 s.</summary>
public static class VideoStatsLine
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    /// <param name="drops">Quantas vezes a fila de envio descartou quadros no período.</param>
    public static string Format(VideoStats previous, VideoStats current, TimeSpan elapsed, int drops, double? rttMs)
    {
        var seconds = Math.Max(elapsed.TotalSeconds, 0.001);
        var fps = (current.Frames - previous.Frames) / seconds;
        var mbps = (current.Bytes - previous.Bytes) * 8 / seconds / 1_000_000;
        var keyframes = current.Keyframes - previous.Keyframes;
        var codec = current.Codec switch
        {
            VideoCodec.H265 => "H.265",
            VideoCodec.H264 => "H.264",
            _ => "sem encoder",
        };
        var rtt = rttMs is { } value ? string.Format(PtBr, "{0:0.0} ms", value) : "—";
        return string.Format(PtBr,
            "Vídeo: {0} {1}×{2} · {3:0.0} fps · {4:0.0} Mbps · {5} IDR · {6} descartes · encode p95 {7:0.0} ms · RTT {8}",
            codec, current.Width, current.Height, fps, mbps, keyframes, drops, current.EncodeP95Ms, rtt);
    }
}
```

`host/ScreenShare.Video/VideoSourceDecorators.cs`:

```csharp
using ScreenShare.Core.Protocol;
using ScreenShare.Display;

namespace ScreenShare.Video;

/// <summary>Captura outro monitor no lugar do da sessão (--capturar principal: depurar sem o monitor virtual).</summary>
public sealed class MonitorOverrideVideoSource(IVideoSource inner, Func<IMonitorSource> monitor) : IVideoSource
{
    public Task<IVideoStream?> StartAsync(VideoRequest request, IVideoOutput output, CancellationToken cancellationToken) =>
        inner.StartAsync(request with { Monitor = monitor() }, output, cancellationToken);
}

/// <summary>
/// Grava o vídeo de cada sessão num arquivo Annex-B (--gravar), para conferir com o ffplay. Cada sessão recomeça o
/// arquivo. Os keyframes trazem os parâmetros, então o arquivo toca mesmo com CONFIG no meio.
/// </summary>
public sealed class RecordingVideoSource(IVideoSource inner, string path, Action<string> log) : IVideoSource
{
    public async Task<IVideoStream?> StartAsync(VideoRequest request, IVideoOutput output, CancellationToken cancellationToken)
    {
        var recorder = new RecordingOutput(output, path, log);
        IVideoStream? stream;
        try
        {
            stream = await inner.StartAsync(request, recorder, cancellationToken);
        }
        catch
        {
            recorder.Close(); // o arquivo não pode ficar aberto: a próxima sessão grava nele de novo
            throw;
        }
        if (stream is null)
        {
            recorder.Close();
            return null;
        }
        log($"Gravando o vídeo em {Path.GetFullPath(path)}.");
        return new RecordingStream(stream, recorder);
    }

    private sealed class RecordingOutput(IVideoOutput inner, string path, Action<string> log) : IVideoOutput
    {
        private readonly Lock _gate = new();
        private FileStream? _file = File.Create(path);

        public int PendingFrames => inner.PendingFrames;

        public void OnConfig(ConfigMessage config) => inner.OnConfig(config);

        public void OnFrame(FrameMessage frame)
        {
            lock (_gate)
            {
                try
                {
                    _file?.Write(frame.Data);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    log($"Gravação interrompida: {e.Message}");
                    _file?.Dispose();
                    _file = null;
                }
            }
            inner.OnFrame(frame);
        }

        public void Close()
        {
            lock (_gate)
            {
                _file?.Dispose();
                _file = null;
            }
        }
    }

    private sealed class RecordingStream(IVideoStream inner, RecordingOutput recorder) : IVideoStream
    {
        public VideoStats Stats => inner.Stats;

        public void RequestKeyframe() => inner.RequestKeyframe();

        public async ValueTask DisposeAsync()
        {
            try
            {
                await inner.DisposeAsync();
            }
            finally
            {
                recorder.Close();
            }
        }
    }
}
```

Run: `dotnet test host/ScreenShare.slnx --filter "FullyQualifiedName~DevHostOptionsTests|FullyQualifiedName~VideoSourceDecoratorsTests"`
Expected: PASS (13 + 3).

- [ ] **Step 3: Estatísticas no `HostServer`**

Em `host/ScreenShare.DevHost/HostServer.cs`:

```diff
@@ -35,6 +35,7 @@ public sealed class HostServer : IDisposable
     private readonly IVideoSource? _video;
     private readonly TimeSpan _pingInterval;
     private readonly TimeSpan _videoConfigTimeout;
+    private readonly TimeSpan _statsInterval;
 
     /// <param name="handshakeTimeout">Prazo para TLS + PAIR/AUTH em qualquer porta (padrão 10 s).</param>
     /// <param name="idleTimeout">
@@ -45,10 +46,11 @@ public sealed class HostServer : IDisposable
     /// <param name="video">Vídeo por sessão (padrão: nenhum; o celular recebe só o CONFIG de fallback).</param>
     /// <param name="pingInterval">Intervalo do PING do PC depois do CONFIG (padrão 1 s).</param>
     /// <param name="videoConfigTimeout">Sem CONFIG do vídeo nesse prazo, sai o de fallback (padrão 2 s).</param>
+    /// <param name="statsInterval">Intervalo das estatísticas do vídeo no console (padrão 5 s).</param>
     public HostServer(int wifiPort, int usbPort, HostIdentity identity, PairingSession pairing, DeviceRegistry devices,
         Action<string>? log = null, TimeSpan? handshakeTimeout = null, TimeSpan? idleTimeout = null,
         IVirtualMonitorManager? monitors = null, IVideoSource? video = null, TimeSpan? pingInterval = null,
-        TimeSpan? videoConfigTimeout = null)
+        TimeSpan? videoConfigTimeout = null, TimeSpan? statsInterval = null)
     {
         _wifi = new TcpListener(IPAddress.Any, wifiPort);
         _usb = new TcpListener(IPAddress.Loopback, usbPort);
@@ -63,6 +65,7 @@ public sealed class HostServer : IDisposable
         _pingInterval = pingInterval ?? TimeSpan.FromSeconds(1);
         ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_pingInterval, TimeSpan.Zero, nameof(pingInterval));
         _videoConfigTimeout = videoConfigTimeout ?? TimeSpan.FromSeconds(2);
+        _statsInterval = statsInterval ?? TimeSpan.FromSeconds(5);
     }
 
     /// <summary>Porta Wi-Fi (TLS) em que está escutando.</summary>
@@ -178,10 +181,15 @@ public sealed class HostServer : IDisposable
         using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
         var writer = new SessionWriter(stream, () => PcClock.NowUs);
         var video = new SessionVideo(this);
-        writer.KeyframeNeeded += video.RequestKeyframe; // assinado antes de o vídeo começar: nenhum descarte fica sem pedido
+        var stats = new SessionStats();
+        writer.KeyframeNeeded += () => // assinado antes de o vídeo começar: nenhum descarte fica sem pedido
+        {
+            stats.CountDrop();
+            video.RequestKeyframe();
+        };
         var writing = writer.RunAsync(session.Token);
         // A leitura, o PING e o prazo do fallback começam já: enquanto o vídeo abre, o celular é atendido.
-        var reading = ReadLoopAsync(reader, writer, video, session.Token);
+        var reading = ReadLoopAsync(reader, writer, video, stats, session.Token);
         _ = PingAsync(writer, session.Token);
         // A abertura do vídeo tem cancelamento próprio: se o celular sair enquanto ela demora, ela é cancelada antes de
         // o vídeo ser encerrado, e o escritor só para depois.
@@ -196,7 +204,8 @@ public sealed class HostServer : IDisposable
             else
             {
                 _ = SendFallbackLaterAsync(writer, fallback, session.Token);
-                starting = AttachVideoAsync(new VideoRequest(lease, hello.SupportedCodecs, link), writer, video, fallback, opening.Token);
+                starting = AttachVideoAsync(new VideoRequest(lease, hello.SupportedCodecs, link), writer, video, fallback, stats,
+                    opening.Token);
             }
             // O que terminar primeiro encerra a sessão: o cliente (fim, prazo, protocolo) ou o escritor (rede). A abertura
             // do vídeo terminar não encerra nada; a sessão segue esperando os outros dois.
@@ -219,13 +228,17 @@ public sealed class HostServer : IDisposable
         }
     }
 
-    /// <summary>Abre o vídeo e o liga à sessão; sem vídeo (falha ou sem encoder), o celular recebe o CONFIG de fallback.</summary>
+    /// <summary>
+    /// Abre o vídeo e o liga à sessão, com as estatísticas no console; sem vídeo (falha ou sem encoder), o celular recebe
+    /// o CONFIG de fallback.
+    /// </summary>
     private async Task AttachVideoAsync(VideoRequest request, SessionWriter writer, SessionVideo video, ConfigMessage fallback,
-        CancellationToken cancellationToken)
+        SessionStats stats, CancellationToken cancellationToken)
     {
         var started = await StartVideoAsync(request, new WriterOutput(writer), cancellationToken);
         video.Start(started);
         if (started is null) writer.SendFallbackConfig(fallback);
+        else _ = LogStatsAsync(started, stats, cancellationToken);
     }
 
     /// <summary>Começa o vídeo da sessão; uma falha vira sessão sem vídeo (o celular recebe o CONFIG de fallback).</summary>
@@ -242,8 +255,9 @@ public sealed class HostServer : IDisposable
         }
     }
 
-    /// <summary>PING → PONG, KEYFRAME_REQ → vídeo. Termina quando o cliente fecha; prazo estourado lança.</summary>
-    private async Task ReadLoopAsync(MessageReader reader, SessionWriter writer, SessionVideo video, CancellationToken cancellationToken)
+    /// <summary>PING → PONG, KEYFRAME_REQ → vídeo, PONG → RTT. Termina quando o cliente fecha; prazo estourado lança.</summary>
+    private async Task ReadLoopAsync(MessageReader reader, SessionWriter writer, SessionVideo video, SessionStats stats,
+        CancellationToken cancellationToken)
     {
         while (await ReadWithIdleDeadlineAsync(reader, cancellationToken) is { } message)
         {
@@ -255,7 +269,10 @@ public sealed class HostServer : IDisposable
                 case KeyframeRequestMessage:
                     video.RequestKeyframe();
                     break;
-                // demais mensagens (PONG, TOUCH) são ignoradas: ainda não há toque
+                case PongMessage pong:
+                    stats.SetRtt(PcClock.NowUs, pong.TimestampUs);
+                    break;
+                // TOUCH é ignorado: ainda não há toque
             }
         }
     }
@@ -292,6 +309,30 @@ public sealed class HostServer : IDisposable
         }
     }
 
+    /// <summary>Uma linha de estatísticas do vídeo a cada intervalo, enquanto a sessão durar.</summary>
+    private async Task LogStatsAsync(IVideoStream video, SessionStats stats, CancellationToken cancellationToken)
+    {
+        using var timer = new PeriodicTimer(_statsInterval);
+        var previous = video.Stats;
+        var since = TimeProvider.System.GetTimestamp();
+        try
+        {
+            while (await timer.WaitForNextTickAsync(cancellationToken))
+            {
+                var current = video.Stats;
+                var now = TimeProvider.System.GetTimestamp();
+                _log?.Invoke(VideoStatsLine.Format(previous, current, TimeProvider.System.GetElapsedTime(since, now),
+                    stats.TakeDrops(), stats.RttMs));
+                previous = current;
+                since = now;
+            }
+        }
+        catch (OperationCanceledException)
+        {
+            // fim da sessão
+        }
+    }
+
     /// <summary>Chamado pelo laço de leitura e pelo escritor (thread do encoder): nunca lança.</summary>
     private void RequestKeyframe(IVideoStream video)
     {
@@ -372,6 +413,24 @@ public sealed class HostServer : IDisposable
         }
     }
 
+    /// <summary>Contadores da sessão para as estatísticas: descartes na fila e o RTT do último PONG.</summary>
+    private sealed class SessionStats
+    {
+        private int _drops;
+        private long _rttUs = -1;
+
+        public double? RttMs => Interlocked.Read(ref _rttUs) is var rtt and >= 0 ? rtt / 1000.0 : null;
+
+        public void CountDrop() => Interlocked.Increment(ref _drops);
+
+        public int TakeDrops() => Interlocked.Exchange(ref _drops, 0);
+
+        public void SetRtt(ulong nowUs, ulong pingUs)
+        {
+            if (nowUs >= pingUs) Interlocked.Exchange(ref _rttUs, (long)(nowUs - pingUs));
+        }
+    }
+
     /// <summary>O vídeo escreve pelo escritor da sessão.</summary>
     private sealed class WriterOutput(SessionWriter writer) : IVideoOutput
     {
```

- [ ] **Step 4: `Program.cs`**

Em `host/ScreenShare.DevHost/Program.cs`:

```diff
@@ -3,13 +3,29 @@ using Makaretu.Dns;
 using QRCoder;
 using ScreenShare.Core.Security;
 using ScreenShare.DevHost;
+using ScreenShare.Video;
+using ScreenShare.Video.Hardware;
 
 // Host de desenvolvimento: Wi-Fi com TLS + pareamento por QR (porta 38700) e USB com TLS + pareamento, só em loopback (porta 38701).
 // Comandos no console: p = parear celular (mostra o QR), l = listar pareados, r <id> = remover, Ctrl+C = sair.
 // Modos de linha de comando (pedem administrador): install-driver, uninstall-driver, restart-driver.
-// Opção: --sem-monitor (não liga o monitor virtual nem mexe no driver).
+// Opções: --sem-monitor (não liga o monitor virtual nem mexe no driver), --sem-video, --capturar principal (vídeo do
+// monitor principal, para depurar sem o driver), --gravar <arquivo> (o vídeo em Annex-B, para o ffplay),
+// --fps <1-120> (padrão 60), --bitrate <Mbps> (padrão 50 no cabo e 25 no Wi-Fi), --codec h264|h265|auto.
 if (DriverCommands.IsDriverCommand(args)) return await DriverCommands.RunAsync(args);
 
+DevHostOptions options;
+try
+{
+    options = DevHostOptions.Parse(args);
+}
+catch (OptionsException ex)
+{
+    Console.Error.WriteLine(ex.Message);
+    Console.Error.WriteLine(DevHostOptions.Usage);
+    return 2;
+}
+
 const int WifiPort = 38700;
 const int UsbPort = 38701;
 
@@ -22,7 +38,12 @@ var pairing = new PairingSession(TimeProvider.System);
 var devices = new DeviceRegistry(Path.Combine(dataDirectory, "paired-devices.json"), log: Log);
 
 // Monitor virtual: liga quando um celular conecta (--sem-monitor desliga o recurso e não mexe no driver).
-using var monitors = args.Contains("--sem-monitor") ? null : MonitorSetup.Create(Log);
+using var monitors = options.NoMonitor ? null : MonitorSetup.Create(Log);
+
+// Vídeo: captura + encoder de hardware por sessão (--sem-video desliga).
+var video = options.NoVideo ? null : VideoSourceFactory.CreateDefault(options.Video, Log);
+if (video is not null && options.CapturePrimary) video = new MonitorOverrideVideoSource(video, () => new PrimaryMonitorSource());
+if (video is not null && options.RecordPath is { } recordPath) video = new RecordingVideoSource(video, recordPath, Log);
 
 using var cts = new CancellationTokenSource();
 Console.CancelKeyPress += (_, e) =>
@@ -31,7 +52,7 @@ Console.CancelKeyPress += (_, e) =>
     cts.Cancel();
 };
 
-using var server = new HostServer(WifiPort, UsbPort, identity, pairing, devices, Log, monitors: monitors);
+using var server = new HostServer(WifiPort, UsbPort, identity, pairing, devices, Log, monitors: monitors, video: video);
 server.Start();
 
 // Anuncia só IPs da LAN real; sem nenhum (ex.: sem gateway), cai no padrão da biblioteca (todos os IPs).
@@ -47,6 +68,9 @@ Console.WriteLine($"IPs anunciados: {(lanAddresses.Count > 0 ? string.Join(", ",
 Console.WriteLine(monitors is null
     ? "Monitor virtual: desligado (sem driver ou --sem-monitor); o CONFIG leva a resolução do celular."
     : "Monitor virtual: pronto; liga quando um celular conecta e sai da área de trabalho 10 s depois que ele desconecta.");
+Console.WriteLine(video is null
+    ? "Vídeo: desligado (sem encoder de hardware ou --sem-video)."
+    : $"Vídeo: até {options.Video.Fps} fps{(options.CapturePrimary ? ", capturando o monitor principal" : "")}.");
 Console.WriteLine("Comandos: p = parear celular, l = listar pareados, r <id> = remover, Ctrl+C = sair.");
 
 // Uma falha num comando (ex.: erro de disco ao remover um celular) não pode encerrar o laço: p/l/r continuam respondendo.
```

- [ ] **Step 5: Rodar tudo, commit e push**

Run: `dotnet build host/ScreenShare.slnx` e `dotnet test host/ScreenShare.slnx`
Expected: 0 avisos; 356 aprovados e 9 ignorados.

```bash
git add host/ScreenShare.DevHost host/ScreenShare.Video host/ScreenShare.Tests
git commit -m "feat(devhost): vídeo na sessão, opções --fps/--bitrate/--codec/--sem-video/--capturar/--gravar e estatísticas"
git push
```

- [ ] **Step 6: Verificação manual (o controlador faz com o usuário, depois da revisão)**

Precisa do ffmpeg (o `ffplay` e o `ffprobe` já foram usados no spike) e do celular com o app atual no cabo USB. Esse app ainda não mostra vídeo, mas a conexão já faz o PC codificar.

1. Na pasta do worktree, com o celular ligado no cabo e o `adb reverse tcp:38701 tcp:38701` feito:
   ```
   dotnet run --project host/ScreenShare.DevHost -- --capturar principal --gravar teste.h265
   ```
2. No app, conectar pelo cabo. No console devem aparecer "Gravando o vídeo em ...", "Vídeo: H.265 3440×1440, até 60 fps, 50 Mbps." e, a cada 5 s, a linha "Vídeo: ... fps · ... Mbps · ...".
3. Mexer janelas e rolar uma página no monitor principal por uns 10 s. Os fps sobem com o movimento e caem perto de zero com a tela parada.
4. Desconectar no app, Ctrl+C no DevHost e rodar `ffplay teste.h265`. A imagem tem de ser nítida e sem quadros quebrados. O cursor ainda não aparece; ele entra na Task 13. `ffprobe -show_frames teste.h265` mostra só um quadro `key_frame=1` no começo.
5. Repetir sem `--capturar principal` (é o monitor virtual): `--gravar vdd.h265`. Com o celular conectado, mandar uma janela para o monitor virtual com Win+Shift+→ e mexer nela. Depois, conferir `vdd.h265` no ffplay.

---

### Task 10: Android — conexão com vídeo, PONG ao PC, relógio do PC e HELLO com a tela real

**Por quê:** o app passa a responder o PING do PC e acerta o relógio com ele (`ClockSync`, para a latência do overlay). Ele também entrega `CONFIG` e `FRAME` a um `VideoSink`, derruba a conexão quando o PC fica mudo e manda no HELLO o tamanho real do painel (spike E8).

**Files:**
- Create: `android/app/src/main/java/dev/screenshare/android/video/ClockSync.kt`, `android/app/src/main/java/dev/screenshare/android/video/VideoSink.kt`
- Modify: `android/app/src/main/java/dev/screenshare/android/net/Connection.kt`
- Modify: `android/app/src/main/java/dev/screenshare/android/net/ConnectionViewModel.kt` (tela pelo `maximumWindowMetrics`, lida a cada conexão)
- Test: `android/app/src/test/java/dev/screenshare/android/video/ClockSyncTest.kt` (novo), `android/app/src/test/java/dev/screenshare/android/net/ConnectionTest.kt`

**Interfaces:**
- Consumes: o protocolo Kotlin (`Messages.kt`, `MessageCodec`, `MessageReader`) e a `Connection` atual.
- Produces (as Tasks 11 e 12 usam):
  - `ScreenInfo(width, height, densityDpi, codecs: Int = VideoCodec.ALL)`;
  - `Connection(scope, screen: () -> ScreenInfo, pingIntervalMs = 1_000, handshakeTimeoutMs = 5_000, idleTimeoutMs = 5_000, sink: VideoSink = VideoSink.NONE, onPaired = {})`;
  - `Connection.requestKeyframe()` e `Connection.clock: ClockSync`, que recomeça a cada `connect`;
  - `interface VideoSink { fun onConfig(config: ConfigMessage); fun onFrame(frame: FrameMessage) }`, com `VideoSink.NONE`;
  - `ClockSync(windowUs = 20_000_000)`, com `onPcPing(pcUs, receivedUs)`, `onRtt(rttUs, atUs)`, `offsetUs(): Long?` e `toLocalUs(pcUs): Long?`.

**Comando dos testes Android:** `.\android\gradlew.bat -p android :app:testDebugUnitTest`. Nesta máquina, **2 dos 4 testes de `PairingStoreTest` já falham antes de qualquer mudança** ("Unable to rename ... pairing.preferences_pb.tmp", DataStore no Windows; no CI, em Ubuntu, passam). Quais 2 falham varia de uma execução para outra. Ignore só essas falhas; qualquer outra é problema desta task.

- [ ] **Step 1: Testes**

`android/app/src/test/java/dev/screenshare/android/video/ClockSyncTest.kt`:

```kotlin
package dev.screenshare.android.video

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class ClockSyncTest {
    /** O celular está 5 s à frente do PC; ida = volta = 2 ms quando a rede está livre. */
    private val offset = 5_000_000L
    private val oneWay = 2_000L

    private fun ClockSync.ping(pcUs: Long, extraDelayUs: Long = 0) = onPcPing(pcUs, pcUs + offset + oneWay + extraDelayUs)

    @Test
    fun noEstimateUntilThereIsAPcPingAndAnRtt() {
        val sync = ClockSync()
        assertNull(sync.offsetUs())

        sync.ping(1_000_000)
        assertNull(sync.offsetUs())

        sync.onRtt(2 * oneWay, 6_000_000)
        assertEquals(offset, sync.offsetUs())
    }

    @Test
    fun jitterAndQueuedPingsDoNotMoveTheEstimate() {
        val sync = ClockSync()
        for (i in 0 until 20) {
            val pcUs = 1_000_000L * (i + 1)
            sync.ping(pcUs, extraDelayUs = if (i == 7) 0 else 3_000L + i * 1_500L) // só um PING passou sem fila
            sync.onRtt(2 * oneWay + (i % 3) * 4_000L, pcUs + offset)
        }

        assertEquals(offset, sync.offsetUs())
        assertEquals(10_000_000L + offset, sync.toLocalUs(10_000_000L))
    }

    @Test
    fun invalidRttIsIgnored() {
        val sync = ClockSync()
        sync.ping(1_000_000)
        sync.onRtt(2 * oneWay, 1_000_000 + offset)

        sync.onRtt(-5_000, 1_100_000 + offset)
        sync.onRtt(0, 1_200_000 + offset)

        assertEquals(offset, sync.offsetUs())
    }

    @Test
    fun oldSamplesLeaveAfterTwentySecondsSoDriftIsFollowed() {
        val sync = ClockSync()
        sync.ping(1_000_000)
        sync.onRtt(2 * oneWay, 1_000_000 + offset)

        // 25 s depois os relógios andaram 3 ms um em relação ao outro
        val pcUs = 26_000_000L
        sync.onPcPing(pcUs, pcUs + offset + 3_000 + oneWay)
        sync.onRtt(2 * oneWay, pcUs + offset)

        assertEquals(offset + 3_000, sync.offsetUs())
    }
}
```

Substitua `android/app/src/test/java/dev/screenshare/android/net/ConnectionTest.kt` pelo conteúdo abaixo. O que muda:
- `newConnection` aceita `idleTimeoutMs` e a tela atual;
- o vídeo vai para um `RecordingSink`;
- 5 testes novos antes do `companion object`.

```kotlin
package dev.screenshare.android.net

import dev.screenshare.android.pairing.PairingInfo
import dev.screenshare.android.protocol.AuthMessage
import dev.screenshare.android.protocol.ConfigMessage
import dev.screenshare.android.protocol.DeniedMessage
import dev.screenshare.android.protocol.DeniedReason
import dev.screenshare.android.protocol.FrameMessage
import dev.screenshare.android.protocol.HelloMessage
import dev.screenshare.android.protocol.KeyframeRequestMessage
import dev.screenshare.android.protocol.Message
import dev.screenshare.android.protocol.MessageCodec
import dev.screenshare.android.protocol.MessageReader
import dev.screenshare.android.protocol.PairMessage
import dev.screenshare.android.protocol.PairedMessage
import dev.screenshare.android.protocol.PingMessage
import dev.screenshare.android.protocol.PongMessage
import dev.screenshare.android.protocol.VideoCodec
import dev.screenshare.android.security.PairedPc
import dev.screenshare.android.security.TestTls
import dev.screenshare.android.video.VideoSink
import java.net.Socket
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Testa a Connection contra servidores TLS reais em loopback, com certificado de teste (Wi-Fi e USB usam o mesmo fluxo). */
class ConnectionTest {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val tlsServer = TestTls.serverSocket("host")
    private val screen = ScreenInfo(width = 2400, height = 1080, densityDpi = 420)
    private val config = ConfigMessage(2400, 1080, VideoCodec.H264, 8000, ByteArray(0))
    private val hostFingerprint = TestTls.fingerprint("host")
    private val paired = mutableListOf<PairedPc>()
    private val video = RecordingSink()

    @After
    fun tearDown() {
        scope.cancel()
        tlsServer.close()
    }

    private fun newConnection(idleTimeoutMs: Int = 5_000, currentScreen: () -> ScreenInfo = { screen }) = Connection(
        scope, currentScreen, pingIntervalMs = 20, handshakeTimeoutMs = 2_000, idleTimeoutMs = idleTimeoutMs, sink = video,
        onPaired = { synchronized(paired) { paired.add(it) } },
    )

    /** Guarda o que a conexão entregou ao vídeo. */
    private class RecordingSink : VideoSink {
        val received = mutableListOf<Message>()

        override fun onConfig(config: ConfigMessage) {
            synchronized(received) { received.add(config) }
        }

        override fun onFrame(frame: FrameMessage) {
            synchronized(received) { received.add(frame) }
        }

        fun snapshot(): List<Message> = synchronized(received) { received.toList() }
    }

    /** Espera o vídeo receber [count] mensagens (até 5 s). */
    private suspend fun RecordingSink.awaitCount(count: Int): List<Message> = withTimeout(5_000) {
        while (snapshot().size < count) kotlinx.coroutines.delay(10)
        snapshot()
    }

    /** Lê do cliente até chegar uma mensagem que não seja o PING periódico dele. */
    private fun MessageReader.readSkippingPings(): Message? {
        while (true) {
            val message = read()
            if (message !is PingMessage) return message
        }
    }

    private fun usb() = ConnectTarget.Usb(PairedPc("PC de teste", hostFingerprint, TOKEN, null), port = tlsServer.localPort)

    private fun wifi(fingerprint: String = hostFingerprint) = ConnectTarget.Wifi(
        HostAddress("127.0.0.1", tlsServer.localPort), PairedPc("PC de teste", fingerprint, TOKEN, null),
    )

    private fun pairing() = ConnectTarget.Pairing(
        PairingInfo("127.0.0.1", tlsServer.localPort, hostFingerprint, SECRET, "PC de teste"), "Pixel 8",
    )

    private fun Socket.send(message: Message) = getOutputStream().apply { write(MessageCodec.encode(message)); flush() }

    private suspend fun Connection.await(predicate: (ConnectionState) -> Boolean): ConnectionState =
        withTimeout(5_000) { state.first(predicate) }

    /** Lado PC (Wi-Fi e USB): TLS com o certificado "host". */
    private fun servingTls(block: (Socket, MessageReader) -> Unit) = scope.async {
        tlsServer.accept().use { socket -> block(socket, MessageReader(socket.getInputStream())) }
    }

    private fun hello() = HelloMessage(MessageCodec.PROTOCOL_VERSION, 2400, 1080, 420, VideoCodec.ALL)

    @Test
    fun usbHandshakeSendsHelloWithScreenInfoAndEndsConnected() = runBlocking {
        var received: Message? = null
        val serverSide = servingTls { socket, reader ->
            assertEquals(AuthMessage(TOKEN), reader.read())
            received = reader.read()
            socket.send(config)
            reader.read() // mantém a conexão aberta até o cliente pingar
        }
        val connection = newConnection()

        connection.connect(usb())
        val state = connection.await { it is ConnectionState.Connected }

        assertEquals(config, (state as ConnectionState.Connected).config)
        assertEquals(hello(), received)
        connection.disconnect()
        serverSide.await()
    }

    @Test
    fun pongsProduceARoundTripTime() = runBlocking {
        val serverSide = servingTls { socket, reader ->
            reader.read() // AUTH
            reader.read() // HELLO
            socket.send(config)
            while (true) {
                val ping = reader.read() as? PingMessage ?: break
                socket.send(PongMessage(ping.timestampUs))
            }
        }
        val connection = newConnection()

        connection.connect(usb())
        val state = connection.await { it is ConnectionState.Connected && it.rttMs != null }

        val rtt = (state as ConnectionState.Connected).rttMs
        assertNotNull(rtt)
        assertTrue("rtt inesperado: $rtt", rtt!! >= 0.0 && rtt < 1_000.0)
        connection.disconnect()
        serverSide.await()
    }

    @Test
    fun serverClosingBeforeConfigFails() = runBlocking {
        val serverSide = servingTls { _, reader -> reader.read(); reader.read() } // lê AUTH e HELLO e fecha
        val connection = newConnection()

        connection.connect(usb())
        val state = connection.await { it is ConnectionState.Failed }

        assertTrue(state is ConnectionState.Failed)
        serverSide.await()
    }

    @Test
    fun serverDroppingAfterConnectedFails() = runBlocking {
        val serverSide = servingTls { socket, reader ->
            reader.read() // AUTH
            reader.read() // HELLO
            socket.send(config)
        } // sai do bloco e fecha o socket
        val connection = newConnection()

        connection.connect(usb())
        val state = connection.await { it is ConnectionState.Failed }

        assertEquals("Conexão perdida", (state as ConnectionState.Failed).reason)
        serverSide.await()
    }

    @Test
    fun disconnectEndsInDisconnectedAndServerSeesTheClose() = runBlocking {
        var serverSawClose = false
        val serverSide = servingTls { socket, reader ->
            reader.read() // AUTH
            reader.read() // HELLO
            socket.send(config)
            while (reader.read() != null) { /* ignora PINGs até o cliente fechar */ }
            serverSawClose = true
        }
        val connection = newConnection()
        connection.connect(usb())
        connection.await { it is ConnectionState.Connected }

        connection.disconnect()

        assertEquals(ConnectionState.Disconnected, connection.state.value)
        serverSide.await()
        assertTrue(serverSawClose)
    }

    @Test
    fun connectionRefusedFails() = runBlocking {
        val port = tlsServer.localPort
        tlsServer.close() // nada escutando nessa porta
        val connection = newConnection()

        connection.connect(ConnectTarget.Usb(PairedPc("PC de teste", hostFingerprint, TOKEN, null), port))
        val state = connection.await { it is ConnectionState.Failed }

        assertTrue((state as ConnectionState.Failed).reason.startsWith("Não foi possível conectar ao PC."))
    }

    @Test
    fun wifiSendsAuthWithTheTokenThenHello() = runBlocking {
        val received = mutableListOf<Message?>()
        val serverSide = servingTls { socket, reader ->
            received += reader.read()
            received += reader.read()
            socket.send(config)
            reader.read()
        }
        val connection = newConnection()

        connection.connect(wifi())
        connection.await { it is ConnectionState.Connected }

        assertEquals(listOf(AuthMessage(TOKEN), hello()), received)
        connection.disconnect()
        serverSide.await()
    }

    @Test
    fun pairingSendsTheSecretReportsThePairedPcAndAuthenticates() = runBlocking {
        val serverSide = servingTls { socket, reader ->
            assertEquals(PairMessage(SECRET, "Pixel 8"), reader.read())
            socket.send(PairedMessage(TOKEN))
            assertEquals(AuthMessage(TOKEN), reader.read())
            assertEquals(hello(), reader.read())
            socket.send(config)
            reader.read()
        }
        val connection = newConnection()

        connection.connect(pairing())
        connection.await { it is ConnectionState.Connected }

        assertEquals(listOf(PairedPc("PC de teste", hostFingerprint, TOKEN, "127.0.0.1")), synchronized(paired) { paired.toList() })
        connection.disconnect()
        serverSide.await()
    }

    @Test
    fun unknownDeviceIsReportedWithItsReason() = runBlocking {
        val serverSide = servingTls { socket, reader ->
            reader.read() // AUTH
            socket.send(DeniedMessage(DeniedReason.UNKNOWN_DEVICE))
        }
        val connection = newConnection()

        connection.connect(wifi())
        val state = connection.await { it is ConnectionState.Failed } as ConnectionState.Failed

        assertEquals(DeniedReason.UNKNOWN_DEVICE, state.denied)
        serverSide.await()
    }

    @Test
    fun invalidPairingSecretIsReportedAndNothingIsStored() = runBlocking {
        val serverSide = servingTls { socket, reader ->
            reader.read() // PAIR
            socket.send(DeniedMessage(DeniedReason.INVALID_PAIRING_SECRET))
        }
        val connection = newConnection()

        connection.connect(pairing())
        val state = connection.await { it is ConnectionState.Failed } as ConnectionState.Failed

        assertEquals(DeniedReason.INVALID_PAIRING_SECRET, state.denied)
        assertTrue(synchronized(paired) { paired.isEmpty() })
        serverSide.await()
    }

    @Test
    fun usbDeniedForIncompatibleVersionIsReported() = runBlocking {
        val serverSide = servingTls { socket, reader ->
            reader.read() // AUTH
            reader.read() // HELLO
            socket.send(DeniedMessage(DeniedReason.INCOMPATIBLE_VERSION))
        }
        val connection = newConnection()

        connection.connect(usb())
        val state = connection.await { it is ConnectionState.Failed } as ConnectionState.Failed

        assertEquals(DeniedReason.INCOMPATIBLE_VERSION, state.denied)
        serverSide.await()
    }

    @Test
    fun differentCertificateFailsWithoutSendingTheToken() = runBlocking {
        var received: Result<Message?>? = null
        val serverSide = servingTls { _, reader -> received = runCatching { reader.read() } }
        val connection = newConnection()

        connection.connect(wifi(fingerprint = TestTls.fingerprint("other")))
        val state = connection.await { it is ConnectionState.Failed } as ConnectionState.Failed

        assertTrue(state.reason, state.reason.contains("não é o PC pareado"))
        assertNull(state.denied)
        serverSide.await()
        assertTrue("o PC recebeu dados: $received", received!!.isFailure || received!!.getOrNull() == null)
    }

    @Test
    fun serverThatNeverAnswersHelloFailsWithATimeoutMessageInPortuguese() = runBlocking {
        val serverSide = servingTls { _, reader ->
            reader.read() // AUTH
            reader.read() // HELLO
            Thread.sleep(3_000) // nunca manda o CONFIG (> handshakeTimeoutMs)
        }
        val connection = newConnection()

        connection.connect(usb())
        val state = connection.await { it is ConnectionState.Failed } as ConnectionState.Failed

        assertEquals(
            "O PC não respondeu a tempo. Se a conexão anterior caiu agora, espere uns 10 segundos e tente de novo.",
            state.reason,
        )
        serverSide.await()
    }

    @Test
    fun usbSendsAuthOverTlsThenHello() = runBlocking {
        val received = mutableListOf<Message?>()
        val serverSide = servingTls { socket, reader ->
            received += reader.read()
            received += reader.read()
            socket.send(config)
            reader.read()
        }
        val connection = newConnection()

        connection.connect(usb())
        connection.await { it is ConnectionState.Connected }

        assertEquals(listOf(AuthMessage(TOKEN), hello()), received)
        connection.disconnect()
        serverSide.await()
    }

    @Test
    fun usbWithDifferentCertificateFailsWithoutSendingTheToken() = runBlocking {
        val otherServer = TestTls.serverSocket("other")
        try {
            var received: Result<Message?>? = null
            val serverSide = scope.async {
                otherServer.accept().use { socket -> received = runCatching { MessageReader(socket.getInputStream()).read() } }
            }
            val connection = newConnection()

            connection.connect(ConnectTarget.Usb(PairedPc("PC de teste", hostFingerprint, TOKEN, null), port = otherServer.localPort))
            val state = connection.await { it is ConnectionState.Failed } as ConnectionState.Failed

            assertTrue(state.reason, state.reason.contains("não é o PC pareado"))
            assertNull(state.denied)
            serverSide.await()
            assertTrue("o PC recebeu dados: $received", received!!.isFailure || received!!.getOrNull() == null)
        } finally {
            otherServer.close()
        }
    }

    @Test
    fun usbToPlainTcpSquatterFailsWithoutSendingTheToken() = runBlocking {
        val squatter = java.net.ServerSocket(0, 1, java.net.InetAddress.getLoopbackAddress())
        try {
            val received = java.io.ByteArrayOutputStream()
            val serverSide = scope.async {
                squatter.accept().use { socket ->
                    socket.soTimeout = 500
                    val buffer = ByteArray(4096)
                    try {
                        while (true) {
                            val n = socket.getInputStream().read(buffer)
                            if (n < 0) break
                            received.write(buffer, 0, n)
                        }
                    } catch (_: java.net.SocketTimeoutException) {
                    } catch (_: java.io.IOException) {
                    }
                }
            }
            val connection = newConnection()

            connection.connect(ConnectTarget.Usb(PairedPc("PC de teste", hostFingerprint, TOKEN, null), port = squatter.localPort))
            connection.await { it is ConnectionState.Failed }
            serverSide.await()

            val bytes = received.toByteArray()
            val leaked = (0..bytes.size - TOKEN.size).any { i -> TOKEN.indices.all { bytes[i + it] == TOKEN[it] } }
            assertTrue("a chave vazou para um servidor sem TLS", !leaked)
        } finally {
            squatter.close()
        }
    }

    @Test
    fun serverClosingDuringTlsHandshakeShowsCannotConnectMessage() = runBlocking {
        // adb reverse ativo, mas o ScreenShare do PC fechado: o adbd aceita e fecha na hora.
        val closer = java.net.ServerSocket(0, 1, java.net.InetAddress.getLoopbackAddress())
        try {
            val serverSide = scope.async { closer.accept().close() }
            val connection = newConnection()

            connection.connect(ConnectTarget.Usb(PairedPc("PC de teste", hostFingerprint, TOKEN, null), port = closer.localPort))
            val state = connection.await { it is ConnectionState.Failed }
            serverSide.await()

            val reason = (state as ConnectionState.Failed).reason
            assertTrue(reason, reason.startsWith("Não foi possível conectar ao PC."))
        } finally {
            closer.close()
        }
    }

    @Test
    fun pairingOverUsbConnectsToLoopbackUsbPort() = runBlocking {
        val serverSide = servingTls { socket, reader ->
            assertEquals(PairMessage(SECRET, "Pixel 8"), reader.read())
            socket.send(PairedMessage(TOKEN))
            assertEquals(AuthMessage(TOKEN), reader.read())
            assertEquals(hello(), reader.read())
            socket.send(config)
            reader.read()
        }
        val connection = newConnection()
        val target = ConnectTarget.Pairing(
            PairingInfo("192.168.0.10", DEFAULT_PORT, hostFingerprint, SECRET, "PC de teste"), "Pixel 8",
            overUsb = true, usbPort = tlsServer.localPort,
        )

        connection.connect(target)
        connection.await { it is ConnectionState.Connected }

        assertEquals(listOf(PairedPc("PC de teste", hostFingerprint, TOKEN, "192.168.0.10")), synchronized(paired) { paired.toList() })
        connection.disconnect()
        serverSide.await()
    }

    @Test
    fun pcPingIsAnsweredWithAPongOfTheSameValue() = runBlocking {
        var answer: Message? = null
        val serverSide = servingTls { socket, reader ->
            reader.read() // AUTH
            reader.read() // HELLO
            socket.send(config)
            socket.send(PingMessage(123_456_789))
            while (true) {
                val message = reader.read() ?: break
                if (message is PongMessage && message.timestampUs == 123_456_789L) {
                    answer = message
                    break
                }
            }
        }
        val connection = newConnection()

        connection.connect(usb())
        serverSide.await()

        assertEquals(PongMessage(123_456_789), answer)
        connection.disconnect()
    }

    @Test
    fun configAndFramesReachTheVideoInOrderAndANewConfigUpdatesTheState() = runBlocking {
        val newConfig = ConfigMessage(1920, 1080, VideoCodec.H265, 25_000, byteArrayOf(0, 0, 0, 1, 0x40, 0x01))
        val key = FrameMessage(10, true, byteArrayOf(0, 0, 0, 1, 0x26, 0x01))
        val p = FrameMessage(20, false, byteArrayOf(0, 0, 0, 1, 0x02, 0x01))
        val serverSide = servingTls { socket, reader ->
            reader.read() // AUTH
            reader.read() // HELLO
            socket.send(config)
            socket.send(key)
            socket.send(p)
            socket.send(newConfig)
            while (reader.read() != null) { /* até o cliente fechar */ }
        }
        val connection = newConnection()

        connection.connect(usb())
        val received = video.awaitCount(4)
        val state = connection.await { it is ConnectionState.Connected && it.config == newConfig }

        assertEquals(listOf(config, key, p, newConfig), received)
        assertEquals(newConfig, (state as ConnectionState.Connected).config)
        connection.disconnect()
        serverSide.await()
    }

    @Test
    fun requestKeyframeSendsTheMessage() = runBlocking {
        var request: Message? = null
        val serverSide = servingTls { socket, reader ->
            reader.read() // AUTH
            reader.read() // HELLO
            socket.send(config)
            request = reader.readSkippingPings()
        }
        val connection = newConnection()
        connection.connect(usb())
        connection.await { it is ConnectionState.Connected }

        connection.requestKeyframe()
        serverSide.await()

        assertEquals(KeyframeRequestMessage, request)
        connection.disconnect()
    }

    @Test
    fun silentPcAfterTheConfigIsDroppedAfterTheIdleTimeout() = runBlocking {
        val serverSide = servingTls { socket, reader ->
            reader.read() // AUTH
            reader.read() // HELLO
            socket.send(config)
            Thread.sleep(1_500) // não responde PING nem manda nada
        }
        val connection = newConnection(idleTimeoutMs = 300)

        connection.connect(usb())
        val state = connection.await { it is ConnectionState.Failed } as ConnectionState.Failed

        assertTrue(state.reason, state.reason.startsWith("O PC parou de responder"))
        serverSide.await()
    }

    @Test
    fun helloCarriesTheScreenAndCodecsReadAtConnectTime() = runBlocking {
        var received: Message? = null
        val serverSide = servingTls { socket, reader ->
            reader.read() // AUTH
            received = reader.read()
            socket.send(config)
            reader.read()
        }
        var current = ScreenInfo(2520, 1080, 432, VideoCodec.H264)
        val connection = newConnection(currentScreen = { current })
        current = ScreenInfo(2504, 2256, 432, VideoCodec.H264) // a tela mudou antes de conectar (dobrável aberto)

        connection.connect(usb())
        connection.await { it is ConnectionState.Connected }

        assertEquals(HelloMessage(MessageCodec.PROTOCOL_VERSION, 2504, 2256, 432, VideoCodec.H264), received)
        connection.disconnect()
        serverSide.await()
    }

    private companion object {
        val SECRET = ByteArray(32) { it.toByte() }
        val TOKEN = ByteArray(32) { (0xA0 + it).toByte() }
    }
}
```

Run: `.\android\gradlew.bat -p android :app:testDebugUnitTest`
Expected: erro de compilação (`ClockSync`, `VideoSink` e os parâmetros novos da `Connection` não existem).

- [ ] **Step 2: `ClockSync` e `VideoSink`**

`android/app/src/main/java/dev/screenshare/android/video/ClockSync.kt`:

```kotlin
package dev.screenshare.android.video

/**
 * Acerta o relógio do PC com o do celular, para medir a latência de ponta a ponta sem mudar o protocolo.
 * deslocamento = mín(recebido no celular − valor do PING do PC) − mín(RTT)/2, nos últimos 20 s: o PING que chegou mais
 * rápido é o que menos esperou na rede. A janela de 20 s deixa entrar uma rota nova ou a deriva dos relógios.
 * Seguro entre threads (a leitura da conexão grava, o vídeo lê).
 */
class ClockSync(private val windowUs: Long = 20_000_000) {
    private class Sample(val atUs: Long, val value: Long)

    private val pings = ArrayDeque<Sample>()
    private val rtts = ArrayDeque<Sample>()

    /** [pcUs] = valor do PING do PC (relógio dele); [receivedUs] = quando chegou (relógio do celular). */
    @Synchronized
    fun onPcPing(pcUs: Long, receivedUs: Long) = add(pings, receivedUs, receivedUs - pcUs)

    /** Um RTT medido pelos PINGs do próprio celular. Zero ou negativo (PONG inválido) é ignorado. */
    @Synchronized
    fun onRtt(rttUs: Long, atUs: Long) {
        if (rttUs > 0) add(rtts, atUs, rttUs)
    }

    /** Relógio do celular − relógio do PC, em µs; null até ter um PING do PC e um RTT. */
    @Synchronized
    fun offsetUs(): Long? {
        val ping = pings.minOfOrNull { it.value } ?: return null
        val rtt = rtts.minOfOrNull { it.value } ?: return null
        return ping - rtt / 2
    }

    /** O instante, no relógio do celular, em que o PC marcou [pcUs]; null sem estimativa. */
    fun toLocalUs(pcUs: Long): Long? = offsetUs()?.let { pcUs + it }

    private fun add(samples: ArrayDeque<Sample>, atUs: Long, value: Long) {
        samples.addLast(Sample(atUs, value))
        while (atUs - samples.first().atUs > windowUs) samples.removeFirst()
    }
}
```

`android/app/src/main/java/dev/screenshare/android/video/VideoSink.kt`:

```kotlin
package dev.screenshare.android.video

import dev.screenshare.android.protocol.ConfigMessage
import dev.screenshare.android.protocol.FrameMessage

/** Para onde vai o vídeo que chega do PC. Chamado na thread de leitura da conexão: não pode bloquear. */
interface VideoSink {
    fun onConfig(config: ConfigMessage)

    fun onFrame(frame: FrameMessage)

    companion object {
        /** Sem vídeo (testes, ou antes de o player existir). */
        val NONE = object : VideoSink {
            override fun onConfig(config: ConfigMessage) = Unit
            override fun onFrame(frame: FrameMessage) = Unit
        }
    }
}
```

- [ ] **Step 3: `Connection` e `ConnectionViewModel`**

Substitua `android/app/src/main/java/dev/screenshare/android/net/Connection.kt` pelo conteúdo abaixo. O que muda:
- `screen` virou uma função;
- `idleTimeoutMs` e `sink` entram pelo construtor;
- o laço de leitura responde PING com PONG, acerta o `ClockSync`, entrega `CONFIG` e `FRAME` e atualiza o estado com o `CONFIG` novo;
- `requestKeyframe()`;
- `soTimeout = idleTimeoutMs` depois do `CONFIG`, com mensagem própria quando o PC fica mudo no meio da sessão;
- a saída fica pronta antes de publicar `Connected` (quem reage a `Connected` já pode pedir keyframe), e uma conexão antiga que ainda esteja terminando não mexe no relógio nem na saída da nova.

```kotlin
package dev.screenshare.android.net

import dev.screenshare.android.pairing.PairingInfo
import dev.screenshare.android.protocol.AuthMessage
import dev.screenshare.android.protocol.ConfigMessage
import dev.screenshare.android.protocol.DeniedMessage
import dev.screenshare.android.protocol.DeniedReason
import dev.screenshare.android.protocol.FrameMessage
import dev.screenshare.android.protocol.HelloMessage
import dev.screenshare.android.protocol.KeyframeRequestMessage
import dev.screenshare.android.protocol.Message
import dev.screenshare.android.protocol.MessageCodec
import dev.screenshare.android.protocol.MessageReader
import dev.screenshare.android.protocol.PairMessage
import dev.screenshare.android.protocol.PairedMessage
import dev.screenshare.android.protocol.PingMessage
import dev.screenshare.android.protocol.PongMessage
import dev.screenshare.android.protocol.ProtocolException
import dev.screenshare.android.protocol.VideoCodec
import dev.screenshare.android.security.PairedPc
import dev.screenshare.android.security.PinnedTrustManager
import dev.screenshare.android.video.ClockSync
import dev.screenshare.android.video.VideoSink
import java.io.EOFException
import java.io.IOException
import java.io.OutputStream
import java.net.ConnectException
import java.net.InetSocketAddress
import java.net.Socket
import java.net.SocketException
import java.net.SocketTimeoutException
import java.security.cert.CertificateException
import javax.net.ssl.SSLException
import javax.net.ssl.SSLSocket
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

/** Tamanho e densidade da tela do celular e os codecs que ele decodifica, enviados ao PC no HELLO. */
data class ScreenInfo(val width: Int, val height: Int, val densityDpi: Int, val codecs: Int = VideoCodec.ALL)

sealed interface ConnectionState {
    data object Disconnected : ConnectionState
    data object Connecting : ConnectionState

    /** Handshake concluído. [rttMs] é o último tempo de ida e volta medido por PING/PONG (null até o primeiro PONG). */
    data class Connected(val config: ConfigMessage, val rttMs: Double?) : ConnectionState

    /** [denied] vem preenchido quando o PC recusou com DENIED. */
    data class Failed(val reason: String, val denied: DeniedReason? = null) : ConnectionState
}

/** Para onde e como conectar. */
sealed interface ConnectTarget {
    /** Cabo USB com o PC já pareado: 127.0.0.1 depois do `adb reverse`, e então o mesmo fluxo do Wi-Fi (TLS com a digital fixa, AUTH). */
    data class Usb(val pc: PairedPc, val port: Int = USB_PORT) : ConnectTarget

    /** Wi-Fi com o PC já pareado: TLS com a digital fixa, depois AUTH. */
    data class Wifi(val address: HostAddress, val pc: PairedPc) : ConnectTarget

    /** Primeiro contato vindo do QR: TLS com a digital do QR, PAIR → PAIRED, depois AUTH. */
    data class Pairing(
        val info: PairingInfo,
        val deviceName: String,
        /** Pareia pelo cabo: conecta em 127.0.0.1:[usbPort] em vez do IP do QR (o IP continua sendo o `lastHost` salvo). */
        val overUsb: Boolean = false,
        val usbPort: Int = USB_PORT,
    ) : ConnectTarget
}

/**
 * Conexão com o host: (TLS + PAIR/AUTH, no Wi-Fi e no USB) → HELLO → CONFIG, depois PING periódico para medir a latência.
 * Depois do CONFIG: responde o PING do PC com PONG (e acerta o relógio por ele), entrega CONFIGs novos e FRAMEs ao
 * [sink] e derruba a conexão se o PC ficar mudo por [idleTimeoutMs] (ele pinga a cada segundo).
 * Uma conexão por vez; chamar [connect] de novo encerra a anterior.
 * [screen] é lido a cada conexão (a tela em uso pode mudar num dobrável).
 * [onPaired] é chamado (na thread de IO) quando um pareamento termina, com os dados a salvar.
 */
class Connection(
    private val scope: CoroutineScope,
    private val screen: () -> ScreenInfo,
    private val pingIntervalMs: Long = 1_000,
    private val handshakeTimeoutMs: Int = 5_000,
    private val idleTimeoutMs: Int = 5_000,
    private val sink: VideoSink = VideoSink.NONE,
    private val onPaired: (PairedPc) -> Unit = {},
) {
    private val _state = MutableStateFlow<ConnectionState>(ConnectionState.Disconnected)
    val state: StateFlow<ConnectionState> = _state.asStateFlow()

    private var job: Job? = null
    private var socket: Socket? = null

    @Volatile
    private var output: OutputStream? = null

    /** O relógio do PC visto do celular, para a latência do vídeo. Recomeça a cada conexão. */
    @Volatile
    var clock = ClockSync()
        private set

    fun connect(target: ConnectTarget) {
        disconnect()
        // Cada conexão tem o seu relógio: uma conexão antiga que ainda esteja terminando não mexe no da nova.
        val sync = ClockSync().also { clock = it }
        _state.value = ConnectionState.Connecting
        val s = Socket().also { socket = it } // guardado já aqui para disconnect() poder interromper o connect
        job = scope.launch(Dispatchers.IO) { run(s, target, sync) }
    }

    /** Pede ao PC um quadro completo (decoder novo ou com erro). Não bloqueia; sem conexão, não faz nada. */
    fun requestKeyframe() {
        val out = output ?: return
        scope.launch(Dispatchers.IO) {
            try {
                out.send(KeyframeRequestMessage)
            } catch (_: IOException) {
                // a leitura percebe a queda e trata
            }
        }
    }

    fun disconnect() {
        job?.cancel()
        job = null
        socket?.closeQuietly() // destrava a leitura bloqueante (fechar o socket de baixo também derruba o TLS)
        socket = null
        _state.value = ConnectionState.Disconnected
    }

    private suspend fun run(raw: Socket, target: ConnectTarget, clock: ClockSync) {
        var connected = false
        try {
            val (host, port) = target.endpoint()
            raw.tcpNoDelay = true
            raw.connect(InetSocketAddress(host, port), handshakeTimeoutMs)
            val fingerprint = when (target) {
                is ConnectTarget.Usb -> target.pc.fingerprint
                is ConnectTarget.Wifi -> target.pc.fingerprint
                is ConnectTarget.Pairing -> target.info.fingerprint
            }
            val s = try {
                raw.upgradeToTls(host, port, fingerprint)
            } catch (e: IOException) {
                // Com o adb reverse ativo e o ScreenShare fechado no PC, o adbd aceita e fecha na hora: o TLS acaba
                // em EOF/reset, o mesmo que "ninguém escutando". O erro de certificado não passa por aqui.
                if (e.closedByPeerDuringHandshake()) throw ConnectException(e.message) else throw e
            }
            s.soTimeout = handshakeTimeoutMs
            val out = s.getOutputStream()
            val reader = MessageReader(s.getInputStream())

            when (target) {
                is ConnectTarget.Usb -> out.send(AuthMessage(target.pc.token))
                is ConnectTarget.Wifi -> out.send(AuthMessage(target.pc.token))
                is ConnectTarget.Pairing -> {
                    out.send(PairMessage(target.info.secret, target.deviceName))
                    val token = when (val reply = reader.read()) {
                        is PairedMessage -> reply.token
                        is DeniedMessage -> return denied(reply.reason)
                        else -> return fail("Resposta inesperada do PC durante o pareamento")
                    }
                    onPaired(PairedPc(target.info.pcName, target.info.fingerprint, token, target.info.host))
                    out.send(AuthMessage(token))
                }
            }

            val info = screen()
            out.send(HelloMessage(MessageCodec.PROTOCOL_VERSION, info.width, info.height, info.densityDpi, info.codecs))
            val config = when (val reply = reader.read()) {
                is ConfigMessage -> reply
                is DeniedMessage -> return denied(reply.reason)
                else -> return fail("O PC recusou a conexão")
            }

            s.soTimeout = idleTimeoutMs // o PC pinga a cada segundo: mudo por mais que isso é conexão morta
            // disconnect() pode ter corrido com a leitura do CONFIG: não publicar Connected depois de Disconnected
            if (!currentCoroutineContext().isActive) return
            output = out // antes de Connected: quem reage a Connected já pode pedir keyframe
            connected = true
            _state.value = ConnectionState.Connected(config, rttMs = null)
            sink.onConfig(config)
            val pinger = scope.launch(Dispatchers.IO) {
                while (isActive) {
                    delay(pingIntervalMs)
                    try {
                        out.send(PingMessage(nowMicros()))
                    } catch (_: IOException) {
                        raw.closeQuietly() // a leitura abaixo falha e trata o erro
                        return@launch
                    }
                }
            }
            try {
                while (true) {
                    when (val message = reader.read() ?: break) {
                        is PingMessage -> {
                            val received = nowMicros()
                            out.send(PongMessage(message.timestampUs))
                            clock.onPcPing(message.timestampUs, received)
                        }
                        is PongMessage -> {
                            val now = nowMicros()
                            clock.onRtt(now - message.timestampUs, now)
                            val rtt = (now - message.timestampUs) / 1_000.0
                            _state.update { if (it is ConnectionState.Connected) it.copy(rttMs = rtt) else it }
                        }
                        is ConfigMessage -> { // o PC recomeçou o vídeo (resolução nova, encoder novo)
                            _state.update { if (it is ConnectionState.Connected) it.copy(config = message) else it }
                            sink.onConfig(message)
                        }
                        is FrameMessage -> sink.onFrame(message)
                        else -> Unit // nada mais vem do PC depois do CONFIG
                    }
                }
            } finally {
                pinger.cancel()
                if (output === out) output = null // um connect() novo pode já ter posto a saída dele
            }
            fail("Conexão perdida")
        } catch (e: SocketTimeoutException) {
            fail(
                if (connected) "O PC parou de responder (nada chegou em ${idleTimeoutMs / 1_000} s). Confira a rede e conecte de novo."
                else e.describe(),
            )
        } catch (e: IOException) { // inclui ProtocolException, EOFException e erros de TLS
            fail(e.describe())
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) { // nada deve escapar da corrotina e derrubar o app
            fail("Erro inesperado na conexão: ${e.message ?: e.javaClass.simpleName}")
        } finally {
            raw.closeQuietly()
        }
    }

    private fun ConnectTarget.endpoint(): kotlin.Pair<String, Int> = when (this) {
        is ConnectTarget.Usb -> LOOPBACK to port
        is ConnectTarget.Wifi -> address.host to address.port
        is ConnectTarget.Pairing -> if (overUsb) LOOPBACK to usbPort else info.host to info.port
    }

    /** TLS por cima do socket já conectado, aceitando só o certificado com a digital esperada. */
    private fun Socket.upgradeToTls(host: String, port: Int, fingerprint: String): SSLSocket =
        (PinnedTrustManager(fingerprint).socketFactory().createSocket(this, host, port, true) as SSLSocket).apply {
            soTimeout = handshakeTimeoutMs
            startHandshake()
        }

    /** Só publica a falha se esta conexão ainda é a atual: disconnect() e um novo connect() cancelam a corrotina antiga. */
    private suspend fun fail(reason: String) {
        if (currentCoroutineContext().isActive) _state.value = ConnectionState.Failed(reason)
    }

    private suspend fun denied(reason: DeniedReason) {
        if (currentCoroutineContext().isActive) _state.value = ConnectionState.Failed(reason.describe(), reason)
    }

    private fun DeniedReason.describe() = when (this) {
        DeniedReason.INVALID_PAIRING_SECRET -> "QR de pareamento inválido ou expirado. Gere um novo no PC."
        DeniedReason.UNKNOWN_DEVICE -> "Este celular não está mais pareado com o PC. Pareie de novo."
        DeniedReason.INCOMPATIBLE_VERSION -> "Versão incompatível com o PC. Atualize o app e o ScreenShare do PC."
    }

    private fun IOException.describe() = when {
        this is ConnectException ->
            "Não foi possível conectar ao PC. No cabo, rode adb reverse tcp:$USB_PORT tcp:$USB_PORT; " +
                "no Wi-Fi, confira se o ScreenShare está aberto no PC."
        this is SocketTimeoutException ->
            "O PC não respondeu a tempo. Se a conexão anterior caiu agora, espere uns 10 segundos e tente de novo."
        this is ProtocolException -> "Resposta inválida do PC: $message"
        this is SSLException && causes().any { it is CertificateException } ->
            "Este não é o PC pareado (certificado diferente). Pareie de novo."
        this is SSLException -> "Falha na conexão segura: ${message ?: "erro de TLS"}"
        else -> message?.takeIf { it.isNotBlank() } ?: "Erro de rede"
    }

    private companion object {
        const val LOOPBACK = "127.0.0.1"
    }

    private fun IOException.closedByPeerDuringHandshake(): Boolean {
        if (causes().any { it is CertificateException || it is SocketTimeoutException }) return false
        return causes().any { it is EOFException || it is SocketException } ||
            (this is SSLException && message?.contains("terminated the handshake", ignoreCase = true) == true)
    }

    private fun Throwable.causes() = generateSequence(this) { it.cause }

    private fun OutputStream.send(message: Message) = synchronized(this) {
        write(MessageCodec.encode(message))
        flush()
    }

    private fun nowMicros() = System.nanoTime() / 1_000

    private fun Socket.closeQuietly() = try { close() } catch (_: IOException) {}
}
```

Em `android/app/src/main/java/dev/screenshare/android/net/ConnectionViewModel.kt`:

```diff
@@ -2,6 +2,8 @@ package dev.screenshare.android.net
 
 import android.app.Application
 import android.os.Build
+import android.util.DisplayMetrics
+import android.view.WindowManager
 import androidx.lifecycle.AndroidViewModel
 import androidx.lifecycle.viewModelScope
 import dev.screenshare.android.pairing.PairingUri
@@ -34,7 +36,7 @@ class ConnectionViewModel(application: Application) : AndroidViewModel(applicati
     val message: StateFlow<String?> = _message.asStateFlow()
 
     private val connection = Connection(
-        viewModelScope, landscapeScreenInfo(application),
+        viewModelScope, screen = { landscapeScreenInfo(application) },
         // Roda na thread de IO dentro da conexão: não pode lançar (derrubaria o app), então só muda estado e dispara o salvamento.
         onPaired = { pc ->
             persist("Não foi possível salvar o pareamento") { store.save(pc) }
@@ -122,13 +124,24 @@ class ConnectionViewModel(application: Application) : AndroidViewModel(applicati
     }
 
     private companion object {
-        /** A tela do celular é usada como monitor na horizontal: largura é sempre o lado maior. */
+        /**
+         * O painel inteiro da tela em uso, na horizontal (largura = lado maior), para o PC criar o monitor com um pixel
+         * por pixel: inclui a área do recorte da câmera e das barras, que a tela imersiva também cobre.
+         */
         fun landscapeScreenInfo(application: Application): ScreenInfo {
-            val metrics = application.resources.displayMetrics
+            val windowManager = application.getSystemService(WindowManager::class.java)
+            val (width, height) = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
+                windowManager.maximumWindowMetrics.bounds.let { it.width() to it.height() }
+            } else {
+                val metrics = DisplayMetrics()
+                @Suppress("DEPRECATION")
+                windowManager.defaultDisplay.getRealMetrics(metrics)
+                metrics.widthPixels to metrics.heightPixels
+            }
             return ScreenInfo(
-                width = maxOf(metrics.widthPixels, metrics.heightPixels),
-                height = minOf(metrics.widthPixels, metrics.heightPixels),
-                densityDpi = metrics.densityDpi,
+                width = maxOf(width, height),
+                height = minOf(width, height),
+                densityDpi = application.resources.displayMetrics.densityDpi,
             )
         }
     }
```

Run: `.\android\gradlew.bat -p android :app:testDebugUnitTest`
Expected: 91 testes, todos aprovados, exceto as 2 falhas conhecidas do `PairingStoreTest`. Rode 3 vezes: `ConnectionTest` usa sockets de verdade.

- [ ] **Step 4: Commit e push**

```bash
git add android/app/src
git commit -m "feat(android): PONG ao PC, relógio do PC, CONFIG e quadros para o vídeo e HELLO com a tela real"
git push
```

---

### Task 11: Android — Annex-B, csd, `DecoderCore`, estatísticas e escolha de decoder (lógica pura)

**Por quê:** todas as regras do decoder ficam testáveis na JVM, sem Android. O `MediaCodec` entra na Task 12 por trás da interface `CodecPort`.

**Files:**
- Create, todos em `android/app/src/main/java/dev/screenshare/android/video/`:
  - `AnnexB.kt` (com `Csd` e `CsdBuilder`);
  - `DecoderCore.kt` (com `CodecPort`);
  - `VideoStats.kt` (com `StatsSnapshot` e `overlayText`);
  - `CodecSupport.kt` (com `DecoderInfo`).
- Test: `android/app/src/test/java/dev/screenshare/android/video/AnnexBTest.kt`, `DecoderCoreTest.kt`, `VideoStatsTest.kt`

**Interfaces:**
- Consumes: `ConfigMessage`, `FrameMessage` e `VideoCodec` (protocolo Kotlin); `Vectors.load` dos testes, com os vetores `annexb/*.hex` da Task 1.
- Produces (a Task 12 usa):
  - `AnnexB.split`, `nalType`, `isKeyframe`, `extractParameterSets` e `withStartCode`, com as constantes `H264_*` e `H265_*`;
  - `Csd(csd0, csd1?)` e `CsdBuilder.build(codec, data): Csd?`;
  - `interface CodecPort { start(generation, codec, width, height, csd): Boolean; queue(index, data, ptsUs); render(index); release() }`;
  - `DecoderCore(port, requestKeyframe: () -> Unit, nowMs: () -> Long)`:
    - entradas: `onConfig`, `onFrame`, `onSurface(Boolean)`;
    - callbacks do decoder: `onInputAvailable(gen, index, capacity)`, `onOutputAvailable(gen, index)`, `onError(gen)`;
    - controle: `onTick()` (o player chama a cada 100 ms: manda o pedido de keyframe que o limite adiou), `release()`, `reset()`, `generation`, `isRunning`;
  - `VideoStats(windowUs = 1_000_000)`, com `onReceived(bytes, atUs)`, `onRendered(atUs, latencyUs?)`, `snapshot(nowUs): StatsSnapshot` e `VideoStats.overlayText(StatsSnapshot, rttMs?)`;
  - `DecoderInfo(name, mime, hardware, lowLatency, sizeSupported)`;
  - `CodecSupport.choose(decoders, codec, width, height)`, `supported(decoders, width, height)`, `mimeOf(codec)`, `MIME_H264` e `MIME_H265`.

**Regras da revisão desta task:**
- um pedido de keyframe barrado pelo limite de 1 a cada 500 ms fica guardado e sai no `onTick` seguinte: com a tela do PC parada não viria outro quadro para pedir de novo, e o celular ficaria numa imagem velha;
- um CONFIG que só muda o bitrate mantém o decoder;
- um keyframe que chega antes da superfície já guarda os parâmetros (CONFIG vazio);
- o HELLO anuncia só os codecs com decoder de hardware, se houver algum: o PC prefere H.265, e um H.265 só em software não aguenta 60 fps na tela inteira.

**Regra do spike (E8):** o decoder preferido é o terminado em `.low_latency` (no celular de teste, `c2.qti.hevc.decoder.low_latency` e `c2.qti.avc.decoder.low_latency`). Depois vem o de hardware com `FEATURE_LowLatency`, depois o primeiro de hardware.

- [ ] **Step 1: Testes**

`android/app/src/test/java/dev/screenshare/android/video/AnnexBTest.kt`:

```kotlin
package dev.screenshare.android.video

import dev.screenshare.android.protocol.Vectors
import dev.screenshare.android.protocol.VideoCodec
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Os mesmos casos do AnnexBTests.cs, com os IDRs reais do encoder do PC. */
class AnnexBTest {
    private val h264Idr = Vectors.load("annexb/h264-idr.hex")
    private val h265Idr = Vectors.load("annexb/h265-idr.hex")

    private fun bytes(vararg values: Int) = ByteArray(values.size) { values[it].toByte() }

    @Test
    fun realH264IdrHasAudSpsPpsAndIdr() {
        assertEquals(listOf(9, 7, 8, 5), AnnexB.split(h264Idr, VideoCodec.H264).map { it.type }.distinct())
        assertTrue(AnnexB.isKeyframe(h264Idr, VideoCodec.H264))
    }

    @Test
    fun realH265IdrHasVpsSpsPpsAndAKeyframeSlice() {
        val types = AnnexB.split(h265Idr, VideoCodec.H265).map { it.type }
        assertTrue(types.containsAll(listOf(32, 33, 34)))
        assertTrue(AnnexB.isKeyframe(h265Idr, VideoCodec.H265))
    }

    @Test
    fun parameterSetsComeOutWithFourByteStartCodesInOrder() {
        val params = AnnexB.extractParameterSets(h265Idr, VideoCodec.H265)!!
        val units = AnnexB.split(params, VideoCodec.H265)

        assertEquals(listOf(32, 33, 34), units.map { it.type })
        assertArrayEquals(bytes(0, 0, 0, 1), params.copyOfRange(0, 4))
    }

    @Test
    fun threeByteStartCodesAndTrailingZerosAreHandled() {
        val data = bytes(0, 0, 1, 0x67, 0x42, 0, 0, 0, 1, 0x68, 0xCE, 0, 0, 1, 0x65, 0x88, 0, 0)

        val units = AnnexB.split(data, VideoCodec.H264)

        assertEquals(listOf(7, 8, 5), units.map { it.type })
        assertEquals(listOf(2, 2, 2), units.map { it.length })
    }

    @Test
    fun pFrameIsNotAKeyframe() {
        assertFalse(AnnexB.isKeyframe(bytes(0, 0, 0, 1, 0x41, 0x9A), VideoCodec.H264))
        assertFalse(AnnexB.isKeyframe(bytes(0, 0, 0, 1, 0x02, 0x01, 0xD0), VideoCodec.H265))
    }

    @Test
    fun missingPpsGivesNoParameterSets() {
        assertNull(AnnexB.extractParameterSets(bytes(0, 0, 0, 1, 0x67, 0x42, 0, 0, 0, 1, 0x65, 0x88), VideoCodec.H264))
    }

    @Test
    fun h264CsdIsSpsAndPpsSeparately() {
        val csd = CsdBuilder.build(VideoCodec.H264, h264Idr)!!

        assertEquals(listOf(7), AnnexB.split(csd.csd0, VideoCodec.H264).map { it.type })
        assertEquals(listOf(8), AnnexB.split(csd.csd1!!, VideoCodec.H264).map { it.type })
    }

    @Test
    fun h265CsdIsAllParameterSetsTogether() {
        val csd = CsdBuilder.build(VideoCodec.H265, h265Idr)!!

        assertArrayEquals(AnnexB.extractParameterSets(h265Idr, VideoCodec.H265), csd.csd0)
        assertNull(csd.csd1)
    }

    @Test
    fun csdFromTheConfigEqualsCsdFromTheKeyframe() {
        val config = AnnexB.extractParameterSets(h264Idr, VideoCodec.H264)!!

        assertEquals(CsdBuilder.build(VideoCodec.H264, h264Idr), CsdBuilder.build(VideoCodec.H264, config))
    }
}
```

`android/app/src/test/java/dev/screenshare/android/video/DecoderCoreTest.kt`:

```kotlin
package dev.screenshare.android.video

import dev.screenshare.android.protocol.ConfigMessage
import dev.screenshare.android.protocol.FrameMessage
import dev.screenshare.android.protocol.Vectors
import dev.screenshare.android.protocol.VideoCodec
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class DecoderCoreTest {
    private val idr = Vectors.load("annexb/h264-idr.hex")
    private val config = ConfigMessage(2520, 1080, VideoCodec.H264, 50_000, AnnexB.extractParameterSets(idr, VideoCodec.H264)!!)
    private val port = FakePort()
    private var now = 0L
    private var keyframeRequests = 0
    private val core = DecoderCore(port, requestKeyframe = { keyframeRequests++ }, nowMs = { now })

    private fun key(ts: Long) = FrameMessage(ts, true, idr)
    private fun p(ts: Long) = FrameMessage(ts, false, byteArrayOf(0, 0, 0, 1, 0x41, 0x9A.toByte()))

    /** Decoder falso: guarda o que recebeu. */
    private class FakePort : CodecPort {
        val started = mutableListOf<Int>()
        val queued = mutableListOf<Long>()
        val rendered = mutableListOf<Int>()
        var releases = 0
        var accept = true

        override fun start(generation: Int, codec: Int, width: Int, height: Int, csd: Csd): Boolean {
            if (accept) started += generation
            return accept
        }

        override fun queue(index: Int, data: ByteArray, ptsUs: Long) {
            queued += ptsUs
        }

        override fun render(index: Int) {
            rendered += index
        }

        override fun release() {
            releases++
        }
    }

    private fun running(): DecoderCore {
        core.onSurface(true)
        core.onConfig(config)
        keyframeRequests = 0
        return core
    }

    private fun DecoderCore.input(index: Int, capacity: Int = 1 shl 20) = onInputAvailable(generation, index, capacity)

    @Test
    fun decoderStartsOnlyWithConfigAndSurface() {
        core.onConfig(config)
        assertTrue(port.started.isEmpty())

        core.onSurface(true)

        assertEquals(1, port.started.size)
        assertEquals(1, keyframeRequests) // superfície nova: pede um quadro completo
    }

    @Test
    fun framesBeforeTheKeyframeAreDroppedThenFramesGoInOrder() {
        val core = running()
        core.input(0)
        core.input(1)
        core.input(2)

        core.onFrame(p(1))
        core.onFrame(key(2))
        core.onFrame(p(3))

        assertEquals(listOf(2L, 3L), port.queued)
    }

    @Test
    fun framesWaitForInputBuffers() {
        val core = running()
        core.onFrame(key(1))
        core.onFrame(p(2))
        assertTrue(port.queued.isEmpty())

        core.input(7)
        core.input(8)

        assertEquals(listOf(1L, 2L), port.queued)
    }

    @Test
    fun outputIsRenderedAtOnce() {
        val core = running()

        core.onOutputAvailable(core.generation, 4)

        assertEquals(listOf(4), port.rendered)
    }

    @Test
    fun sameConfigKeepsTheDecoderAndADifferentOneRecreatesIt() {
        val core = running()

        core.onConfig(config.copy())
        assertEquals(1, port.started.size)

        core.onConfig(config.copy(width = 1920))
        assertEquals(2, port.started.size)
        assertEquals(1, port.releases)
        assertEquals(0, keyframeRequests) // o PC já manda o IDR depois do CONFIG
    }

    @Test
    fun tooManyFramesWaitingAreDroppedAndAKeyframeIsRequested() {
        val core = running()
        core.onFrame(key(1))
        repeat(DecoderCore.MAX_WAITING) { core.onFrame(p(2L + it)) }

        core.input(0)

        assertTrue(port.queued.isEmpty())
        assertEquals(1, keyframeRequests)
        core.onFrame(p(10))
        core.onFrame(key(11))
        assertEquals(listOf(11L), port.queued)
    }

    @Test
    fun frameBiggerThanTheInputBufferIsDroppedAndAKeyframeIsRequested() {
        val core = running()
        core.input(0, capacity = 10)

        core.onFrame(key(1))

        assertTrue(port.queued.isEmpty())
        assertEquals(1, keyframeRequests)
    }

    @Test
    fun keyframeRequestsAreLimitedToOneEvery500ms() {
        val core = running()
        core.onFrame(p(1))
        core.onFrame(p(2))
        assertEquals(1, keyframeRequests)

        now = 600
        core.onFrame(p(3))
        assertEquals(2, keyframeRequests)
    }

    @Test
    fun requestBlockedByTheLimitIsSentByALaterTick() {
        val core = running()
        core.onFrame(p(1)) // pede (t = 0)
        now = 100
        core.onFrame(p(2)) // barrado pelo limite; a tela do PC para e não vem mais nada
        assertEquals(1, keyframeRequests)

        now = 300
        core.onTick()
        assertEquals(1, keyframeRequests)
        now = 600
        core.onTick()
        assertEquals(2, keyframeRequests)
        now = 1_200
        core.onTick()
        assertEquals(2, keyframeRequests) // o pedido guardado sai uma vez só
    }

    @Test
    fun configThatOnlyChangesTheBitrateKeepsTheDecoder() {
        val core = running()

        core.onConfig(config.copy(bitrateKbps = 25_000))

        assertEquals(1, port.started.size)
        assertEquals(0, port.releases)
    }

    @Test
    fun keyframeThatArrivesBeforeTheSurfaceStartsTheDecoderWhenItAppears() {
        core.onConfig(config.copy(codecConfig = ByteArray(0)))
        core.onFrame(key(1)) // sem superfície ainda

        core.onSurface(true)

        assertEquals(1, port.started.size)
        assertEquals(1, keyframeRequests)
    }

    @Test
    fun lostSurfaceReleasesAndItsReturnRecreatesAndAsksForAKeyframe() {
        val core = running()

        core.onSurface(false)
        assertEquals(1, port.releases)
        assertFalse(core.isRunning)

        now = 1_000
        core.onSurface(true)
        assertEquals(2, port.started.size)
        assertEquals(1, keyframeRequests)
    }

    @Test
    fun decoderErrorRecreatesItAndAsksForAKeyframe() {
        val core = running()

        core.onError(core.generation)

        assertEquals(2, port.started.size)
        assertEquals(1, keyframeRequests)
    }

    @Test
    fun callbacksFromAnOldDecoderAreIgnored() {
        val core = running()
        val old = core.generation
        core.onConfig(config.copy(width = 1920))

        core.onInputAvailable(old, 0, 1 shl 20)
        core.onOutputAvailable(old, 1)
        core.onError(old)
        core.onFrame(key(1))

        assertTrue(port.queued.isEmpty())
        assertTrue(port.rendered.isEmpty())
        assertEquals(2, port.started.size)
    }

    @Test
    fun resetForgetsTheConfigSoTheSameOneStartsAgain() {
        val core = running()

        core.reset()
        assertEquals(1, port.releases)
        core.onConfig(config)

        assertEquals(2, port.started.size)
    }

    @Test
    fun configWithoutParameterSetsStartsOnTheFirstKeyframe() {
        core.onSurface(true)
        core.onConfig(config.copy(codecConfig = ByteArray(0)))
        assertTrue(port.started.isEmpty())

        core.onFrame(key(1))
        core.input(0)

        assertEquals(1, port.started.size)
        assertEquals(listOf(1L), port.queued)
    }
}
```

`android/app/src/test/java/dev/screenshare/android/video/VideoStatsTest.kt`:

```kotlin
package dev.screenshare.android.video

import dev.screenshare.android.protocol.VideoCodec
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class VideoStatsTest {
    @Test
    fun lastSecondGivesFpsMbpsAndMedianLatency() {
        val stats = VideoStats()
        for (i in 0 until 60) {
            val at = 1_000_000L + i * 16_667L
            stats.onReceived(25_000, at)
            stats.onRendered(at, latencyUs = 30_000L + (i % 3) * 5_000L)
        }

        val snapshot = stats.snapshot(nowUs = 2_000_000)

        assertEquals(60.0, snapshot.fps, 0.01)
        assertEquals(12.0, snapshot.mbps, 0.01)
        assertEquals(35.0, snapshot.latencyMs!!, 0.01)
    }

    @Test
    fun oldEventsLeaveTheWindowAndLatencyWithoutClockIsUnknown() {
        val stats = VideoStats()
        stats.onReceived(1_000_000, 0)
        stats.onRendered(500_000, latencyUs = null)

        val snapshot = stats.snapshot(nowUs = 1_200_000)

        assertEquals(1.0, snapshot.fps, 0.01) // o recebido em t = 0 já saiu; o exibido em 0,5 s ainda conta
        assertEquals(0.0, snapshot.mbps, 0.01)
        assertNull(snapshot.latencyMs)
    }

    @Test
    fun overlayTextIsInPortuguese() {
        assertEquals("≈38 ms · 60 fps · 12,4 Mbps · RTT 3,1 ms", VideoStats.overlayText(StatsSnapshot(60.0, 12.43, 38.2), 3.14))
        assertEquals("≈— ms · 0 fps · 0,0 Mbps · RTT —", VideoStats.overlayText(StatsSnapshot(0.0, 0.0, null), null))
    }

    @Test
    fun lowLatencyDecoderIsPreferredAndCodecsDependOnTheSize() {
        val any: (Int, Int) -> Boolean = { _, _ -> true }
        val upTo1080p: (Int, Int) -> Boolean = { w, h -> w <= 1920 && h <= 1088 }
        val decoders = listOf(
            DecoderInfo("c2.qti.avc.decoder", CodecSupport.MIME_H264, hardware = true, lowLatency = false, any),
            DecoderInfo("c2.qti.avc.decoder.low_latency", CodecSupport.MIME_H264, hardware = true, lowLatency = true, any),
            DecoderInfo("c2.android.hevc.decoder", CodecSupport.MIME_H265, hardware = false, lowLatency = false, upTo1080p),
        )

        assertEquals("c2.qti.avc.decoder.low_latency", CodecSupport.choose(decoders, VideoCodec.H264, 2520, 1080)?.name)
        assertEquals(VideoCodec.H264, CodecSupport.supported(decoders, 2520, 1080))
        // H.265 só em software não entra no HELLO quando há decoder de hardware: o PC escolheria H.265
        assertEquals(VideoCodec.H264, CodecSupport.supported(decoders, 1920, 1080))
        // sem nenhum decoder de hardware, vale o software
        assertEquals(VideoCodec.H265, CodecSupport.supported(decoders.filter { !it.hardware }, 1920, 1080))
    }
}
```

Run: `.\android\gradlew.bat -p android :app:testDebugUnitTest`
Expected: erro de compilação.

- [ ] **Step 2: Implementação**

`android/app/src/main/java/dev/screenshare/android/video/AnnexB.kt`:

```kotlin
package dev.screenshare.android.video

import dev.screenshare.android.protocol.VideoCodec
import java.io.ByteArrayOutputStream

/** Uma NAL unit dentro de um access unit: posição e tamanho sem o start code, e o tipo. */
data class NalUnit(val offset: Int, val length: Int, val type: Int)

/** Leitura de vídeo em Annex-B (start codes de 3 ou 4 bytes), igual à do PC (host/ScreenShare.Core/Video/AnnexB.cs). */
object AnnexB {
    const val H264_IDR = 5
    const val H264_SPS = 7
    const val H264_PPS = 8
    const val H265_IDR_W_RADL = 19
    const val H265_IDR_N_LP = 20
    const val H265_CRA = 21
    const val H265_VPS = 32
    const val H265_SPS = 33
    const val H265_PPS = 34

    private val START_CODE = byteArrayOf(0, 0, 0, 1)

    /** As NAL units, na ordem. Bytes antes do primeiro start code são ignorados. */
    fun split(accessUnit: ByteArray, codec: Int): List<NalUnit> {
        requireSingleCodec(codec)
        val units = mutableListOf<NalUnit>()
        var start = -1
        var i = 0
        while (i + 2 < accessUnit.size) {
            if (accessUnit[i].toInt() == 0 && accessUnit[i + 1].toInt() == 0 && accessUnit[i + 2].toInt() == 1) {
                if (start >= 0) add(units, accessUnit, start, i, codec)
                start = i + 3
                i += 3
            } else {
                i++
            }
        }
        if (start >= 0) add(units, accessUnit, start, accessUnit.size, codec)
        return units
    }

    /** O tipo da NAL pelo primeiro byte do cabeçalho (H.264: 5 bits baixos; H.265: bits 1 a 6). */
    fun nalType(header: Byte, codec: Int): Int {
        val value = header.toInt() and 0xFF
        return if (requireSingleCodec(codec) == VideoCodec.H264) value and 0x1F else (value shr 1) and 0x3F
    }

    /** O access unit tem um IDR (H.264) ou um IDR/CRA (H.265), por onde o decoder pode começar. */
    fun isKeyframe(accessUnit: ByteArray, codec: Int): Boolean = split(accessUnit, codec).any {
        if (codec == VideoCodec.H264) it.type == H264_IDR else it.type in H265_IDR_W_RADL..H265_CRA
    }

    /** Os parâmetros (H.264: SPS e PPS; H.265: VPS, SPS e PPS), cada um com start code de 4 bytes. null se faltar algum. */
    fun extractParameterSets(accessUnit: ByteArray, codec: Int): ByteArray? {
        val required = if (codec == VideoCodec.H264) setOf(H264_SPS, H264_PPS) else setOf(H265_VPS, H265_SPS, H265_PPS)
        val found = mutableSetOf<Int>()
        val output = ByteArrayOutputStream()
        for (nal in split(accessUnit, codec)) {
            if (nal.type !in required) continue
            found += nal.type
            output.write(START_CODE)
            output.write(accessUnit, nal.offset, nal.length)
        }
        return if (found == required) output.toByteArray() else null
    }

    /** A NAL com start code de 4 bytes na frente. */
    fun withStartCode(accessUnit: ByteArray, nal: NalUnit): ByteArray =
        START_CODE + accessUnit.copyOfRange(nal.offset, nal.offset + nal.length)

    private fun add(units: MutableList<NalUnit>, data: ByteArray, start: Int, end: Int, codec: Int) {
        // Uma NAL nunca termina em 0x00: os zeros antes de um start code são do start code de 4 bytes ou de preenchimento.
        var last = end
        while (last > start && data[last - 1].toInt() == 0) last--
        if (last > start) units += NalUnit(start, last - start, nalType(data[start], codec))
    }

    private fun requireSingleCodec(codec: Int): Int {
        require(codec == VideoCodec.H264 || codec == VideoCodec.H265) { "O codec precisa ser H.264 ou H.265, recebeu $codec." }
        return codec
    }
}

/** csd-0 e csd-1 do MediaFormat. H.264: csd-0 = SPS e csd-1 = PPS; H.265: csd-0 = VPS + SPS + PPS e sem csd-1. */
class Csd(val csd0: ByteArray, val csd1: ByteArray?) {
    override fun equals(other: Any?) =
        other is Csd && csd0.contentEquals(other.csd0) && (csd1?.contentEquals(other.csd1) ?: (other.csd1 == null))

    override fun hashCode() = 31 * csd0.contentHashCode() + (csd1?.contentHashCode() ?: 0)
}

object CsdBuilder {
    /** Do codecConfig do CONFIG ou de um keyframe (os parâmetros vêm dentro dele). null se faltar algum. */
    fun build(codec: Int, data: ByteArray): Csd? {
        if (codec == VideoCodec.H265) return AnnexB.extractParameterSets(data, codec)?.let { Csd(it, null) }
        val units = AnnexB.split(data, codec)
        val sps = units.firstOrNull { it.type == AnnexB.H264_SPS } ?: return null
        val pps = units.firstOrNull { it.type == AnnexB.H264_PPS } ?: return null
        return Csd(AnnexB.withStartCode(data, sps), AnnexB.withStartCode(data, pps))
    }
}
```

`android/app/src/main/java/dev/screenshare/android/video/DecoderCore.kt`:

```kotlin
package dev.screenshare.android.video

import dev.screenshare.android.protocol.ConfigMessage
import dev.screenshare.android.protocol.FrameMessage

/** O decoder de verdade (MediaCodec) visto pelo [DecoderCore]. Os callbacks do decoder levam a geração recebida em [start]. */
interface CodecPort {
    /** Cria e começa um decoder para este formato. false = não deu (formato não suportado ou superfície inválida). */
    fun start(generation: Int, codec: Int, width: Int, height: Int, csd: Csd): Boolean

    /** Entrega um access unit no buffer de entrada [index]. */
    fun queue(index: Int, data: ByteArray, ptsUs: Long)

    /** Mostra o quadro de saída [index] já. */
    fun render(index: Int)

    fun release()
}

/**
 * O decoder como máquina de estados, sem Android, numa thread só (a do decoder). Regras:
 * - só cria o decoder com CONFIG, parâmetros e superfície; os parâmetros vêm do CONFIG ou, se ele vier vazio, do primeiro keyframe;
 * - depois de (re)criar, descarta tudo até um keyframe;
 * - CONFIG igual não faz nada; diferente recria;
 * - mais de [MAX_WAITING] quadros esperando buffer de entrada: descarta-os, espera keyframe e pede um;
 * - quadro maior que o buffer de entrada: descarta, espera keyframe e pede;
 * - P-frame chegando enquanto espera keyframe: pede de novo (o pedido anterior pode ter sido o limitado);
 * - superfície perdida: solta o decoder; de volta: cria e pede keyframe;
 * - erro do decoder: recria e pede keyframe;
 * - callbacks de um decoder antigo (geração anterior) são ignorados;
 * - no máximo um pedido de keyframe a cada [KEYFRAME_REQUEST_INTERVAL_MS]; um pedido barrado pelo limite fica guardado e
 *   sai no [onTick] seguinte depois do intervalo (com a tela do PC parada, não viria outro quadro para pedir de novo);
 * - CONFIG que só muda o bitrate mantém o decoder.
 */
class DecoderCore(
    private val port: CodecPort,
    private val requestKeyframe: () -> Unit,
    private val nowMs: () -> Long,
) {
    private var config: ConfigMessage? = null
    private var csd: Csd? = null
    private var hasSurface = false
    private var running = false
    private var awaitingKeyframe = true
    private var lastRequestMs: Long? = null
    private var pendingRequest = false
    private val waiting = ArrayDeque<FrameMessage>()
    private val inputs = ArrayDeque<Pair<Int, Int>>() // (índice, capacidade)

    /** Muda a cada decoder criado ou solto; callbacks com outra geração são de um decoder que já foi. */
    var generation = 0
        private set

    val isRunning: Boolean get() = running

    fun onConfig(config: ConfigMessage) {
        val current = this.config
        this.config = config
        if (current != null && sameStream(current, config)) return // só o bitrate mudou: o decoder serve
        csd = if (config.codecConfig.isEmpty()) null else CsdBuilder.build(config.codec, config.codecConfig)
        stop()
        startIfReady(askKeyframe = false) // o PC manda o IDR logo depois do CONFIG
    }

    fun onFrame(frame: FrameMessage) {
        val config = config ?: return
        if (!running && csd == null && frame.isKeyframe) {
            csd = CsdBuilder.build(config.codec, frame.data) // CONFIG sem parâmetros: eles vêm no keyframe
            startIfReady(askKeyframe = false)
        }
        if (!running) return
        if (awaitingKeyframe && !frame.isKeyframe) {
            askKeyframe()
            return
        }
        awaitingKeyframe = false
        pendingRequest = false
        if (frame.isKeyframe) waiting.clear() // o keyframe torna inúteis os quadros que ainda esperavam
        waiting.addLast(frame)
        if (waiting.size > MAX_WAITING) {
            dropUntilKeyframe()
            return
        }
        feed()
    }

    fun onSurface(available: Boolean) {
        hasSurface = available
        if (!available) {
            stop()
            return
        }
        if (config != null && csd == null) askKeyframe() // sem parâmetros ainda: só um keyframe destrava o decoder
        startIfReady(askKeyframe = true)
    }

    /** Chamado periodicamente (o player chama a cada 100 ms): manda o pedido de keyframe que o limite tinha barrado. */
    fun onTick() {
        if (pendingRequest) askKeyframe()
    }

    fun onInputAvailable(generation: Int, index: Int, capacity: Int) {
        if (generation != this.generation || !running) return
        inputs.addLast(index to capacity)
        feed()
    }

    fun onOutputAvailable(generation: Int, index: Int) {
        if (generation != this.generation || !running) return
        port.render(index)
    }

    fun onError(generation: Int) {
        if (generation != this.generation || !running) return
        stop()
        startIfReady(askKeyframe = true)
    }

    fun release() = stop()

    /** Conexão nova: solta o decoder e esquece o CONFIG (o próximo, mesmo igual, recria). */
    fun reset() {
        stop()
        config = null
        csd = null
        pendingRequest = false
    }

    private fun feed() {
        while (waiting.isNotEmpty() && inputs.isNotEmpty()) {
            val frame = waiting.removeFirst()
            val (index, capacity) = inputs.removeFirst()
            if (frame.data.size > capacity) {
                inputs.addFirst(index to capacity) // o buffer continua livre para o próximo keyframe
                dropUntilKeyframe()
                return
            }
            port.queue(index, frame.data, frame.timestampUs)
        }
    }

    private fun dropUntilKeyframe() {
        waiting.clear()
        awaitingKeyframe = true
        askKeyframe()
    }

    private fun startIfReady(askKeyframe: Boolean) {
        val config = config ?: return
        val csd = csd ?: return
        if (!hasSurface || running) return
        generation++
        if (!port.start(generation, config.codec, config.width, config.height, csd)) return
        running = true
        awaitingKeyframe = true
        if (askKeyframe) askKeyframe()
    }

    private fun stop() {
        if (running) {
            port.release()
            running = false
            generation++
        }
        waiting.clear()
        inputs.clear()
        awaitingKeyframe = true
    }

    private fun askKeyframe() {
        val now = nowMs()
        val last = lastRequestMs
        if (last != null && now - last < KEYFRAME_REQUEST_INTERVAL_MS) {
            pendingRequest = true // adiado, não perdido
            return
        }
        lastRequestMs = now
        pendingRequest = false
        requestKeyframe()
    }

    /** O mesmo stream para o decoder: o bitrate não importa para ele. */
    private fun sameStream(a: ConfigMessage, b: ConfigMessage) =
        a.codec == b.codec && a.width == b.width && a.height == b.height && a.codecConfig.contentEquals(b.codecConfig)

    companion object {
        const val MAX_WAITING = 3
        const val KEYFRAME_REQUEST_INTERVAL_MS = 500L
    }
}
```

`android/app/src/main/java/dev/screenshare/android/video/VideoStats.kt`:

```kotlin
package dev.screenshare.android.video

import java.util.Locale

/** O que o overlay mostra: quadros exibidos por segundo, Mbps recebidos e a latência mediana (null sem relógio acertado). */
data class StatsSnapshot(val fps: Double, val mbps: Double, val latencyMs: Double?)

/**
 * Números do vídeo na última janela (1 s): bytes recebidos, quadros exibidos e a latência de cada um (do instante em
 * que o PC capturou até aparecer na tela). Seguro entre threads.
 */
class VideoStats(private val windowUs: Long = 1_000_000) {
    private class Event(val atUs: Long, val value: Long)

    private val received = ArrayDeque<Event>()
    private val rendered = ArrayDeque<Event>()

    @Synchronized
    fun onReceived(bytes: Int, atUs: Long) = add(received, atUs, bytes.toLong())

    /** [latencyUs] null = ainda sem relógio do PC acertado. */
    @Synchronized
    fun onRendered(atUs: Long, latencyUs: Long?) = add(rendered, atUs, latencyUs ?: -1)

    @Synchronized
    fun snapshot(nowUs: Long): StatsSnapshot {
        evict(received, nowUs)
        evict(rendered, nowUs)
        val seconds = windowUs / 1_000_000.0
        val latencies = rendered.map { it.value }.filter { it >= 0 }.sorted()
        return StatsSnapshot(
            fps = rendered.size / seconds,
            mbps = received.sumOf { it.value } * 8 / seconds / 1_000_000,
            latencyMs = if (latencies.isEmpty()) null else latencies[latencies.size / 2] / 1_000.0,
        )
    }

    private fun add(events: ArrayDeque<Event>, atUs: Long, value: Long) {
        events.addLast(Event(atUs, value))
        evict(events, atUs)
    }

    private fun evict(events: ArrayDeque<Event>, nowUs: Long) {
        while (events.isNotEmpty() && nowUs - events.first().atUs > windowUs) events.removeFirst()
    }

    companion object {
        private val PT_BR = Locale.forLanguageTag("pt-BR")

        /** "≈38 ms · 60 fps · 12,4 Mbps · RTT 3,1 ms" ("—" no que ainda não se sabe). */
        fun overlayText(stats: StatsSnapshot, rttMs: Double?): String {
            val latency = stats.latencyMs?.let { String.format(PT_BR, "≈%.0f ms", it) } ?: "≈— ms"
            val rtt = rttMs?.let { String.format(PT_BR, "%.1f ms", it) } ?: "—"
            return String.format(PT_BR, "%s · %.0f fps · %.1f Mbps · RTT %s", latency, stats.fps, stats.mbps, rtt)
        }
    }
}
```

`android/app/src/main/java/dev/screenshare/android/video/CodecSupport.kt`:

```kotlin
package dev.screenshare.android.video

import dev.screenshare.android.protocol.VideoCodec

/** Um decoder do aparelho, como o MediaCodecList o descreve. */
class DecoderInfo(
    val name: String,
    val mime: String,
    val hardware: Boolean,
    val lowLatency: Boolean,
    private val sizeSupported: (width: Int, height: Int) -> Boolean,
) {
    fun supports(width: Int, height: Int) = sizeSupported(width, height)
}

/** Que codecs o celular decodifica de fato e qual decoder usar (regra do spike da Parte 3). */
object CodecSupport {
    const val MIME_H264 = "video/avc"
    const val MIME_H265 = "video/hevc"

    fun mimeOf(codec: Int) = if (codec == VideoCodec.H265) MIME_H265 else MIME_H264

    /**
     * O decoder para o codec no tamanho pedido: o do fabricante terminado em ".low_latency"; senão o primeiro de
     * hardware com baixa latência; senão o primeiro de hardware; senão qualquer um. null se nenhum abre esse tamanho.
     */
    fun choose(decoders: List<DecoderInfo>, codec: Int, width: Int, height: Int): DecoderInfo? {
        val candidates = decoders.filter { it.mime == mimeOf(codec) && it.supports(width, height) }
        return candidates.firstOrNull { it.name.endsWith(".low_latency") }
            ?: candidates.firstOrNull { it.hardware && it.lowLatency }
            ?: candidates.firstOrNull { it.hardware }
            ?: candidates.firstOrNull()
    }

    /**
     * Os codecs (flags do HELLO) que o celular decodifica no tamanho da tela. Se houver decoder de hardware para algum
     * codec, só os de hardware entram: o PC prefere H.265, e um H.265 só em software não aguenta 60 fps na tela inteira.
     */
    fun supported(decoders: List<DecoderInfo>, width: Int, height: Int): Int {
        val hardware = flags(decoders.filter { it.hardware }, width, height)
        return if (hardware != 0) hardware else flags(decoders, width, height)
    }

    private fun flags(decoders: List<DecoderInfo>, width: Int, height: Int): Int {
        var codecs = 0
        if (choose(decoders, VideoCodec.H264, width, height) != null) codecs = codecs or VideoCodec.H264
        if (choose(decoders, VideoCodec.H265, width, height) != null) codecs = codecs or VideoCodec.H265
        return codecs
    }
}
```

Run: `.\android\gradlew.bat -p android :app:testDebugUnitTest`
Expected: 120 testes, todos aprovados, exceto as 2 falhas conhecidas do `PairingStoreTest` (ver Task 10). São 29 novos: `AnnexBTest` 9, `DecoderCoreTest` 16 e `VideoStatsTest` 4.

- [ ] **Step 3: Commit e push**

```bash
git add android/app/src
git commit -m "feat(android): leitura Annex-B, csd do decoder, máquina de estados do decoder e estatísticas do vídeo"
git push
```

---

### Task 12: Android — MediaCodec, tela de vídeo 1:1 e overlay

**Por quê:** é o adaptador Android do `DecoderCore`: `MediaCodec` em modo assíncrono numa thread de prioridade de display, com as chaves de baixa latência. A tela imersiva passa a mostrar o vídeo numa `SurfaceView` com `setFixedSize` no tamanho do `CONFIG` (um pixel do PC por pixel do painel). O overlay `≈latência · fps · Mbps · RTT` é atualizado 2 vezes por segundo. Não há teste automático de Android aqui: o CI compila, e a verificação é manual no celular.

**Files:**
- Create: `android/app/src/main/java/dev/screenshare/android/video/MediaCodecPort.kt` (com `MediaCodecDecoders`), `android/app/src/main/java/dev/screenshare/android/video/VideoPlayer.kt`
- Modify: `android/app/src/main/java/dev/screenshare/android/net/ConnectionViewModel.kt` (`player`, codecs reais no HELLO, `reset` a cada conexão, `release`)
- Modify: `android/app/src/main/java/dev/screenshare/android/ui/ImmersiveScreen.kt` (vídeo, overlay, recorte da câmera)
- Modify: `android/app/src/main/java/dev/screenshare/android/ui/ScreenShareApp.kt` (passa o `player`)

**Interfaces:**
- Consumes:
  - `DecoderCore`, `CodecPort`, `Csd`, `VideoStats`, `DecoderInfo` e `CodecSupport` (Task 11);
  - `VideoSink`, `ClockSync`, `Connection.requestKeyframe`, `Connection.clock` e `ScreenInfo.codecs` (Task 10).
- Produces:
  - `VideoPlayer(decoders: () -> List<DecoderInfo>, clock: () -> ClockSync, requestKeyframe: () -> Unit) : VideoSink`, com `stats`, `attach(Surface)`, `detach()`, `reset()` e `release()`;
  - `ConnectionViewModel.player`;
  - `ImmersiveScreen(state, player, onDisconnect)`.

**Decisões:**
- `releaseOutputBuffer(index, System.nanoTime())`: cada quadro aparece assim que é decodificado;
- o `VideoPlayer` chama `DecoderCore.onTick()` a cada 100 ms, na thread do vídeo (pedido de keyframe adiado pelo limite);
- a latência é medida por quadro: `renderNs/1000 − clock.toLocalUs(pts)`, pelo `setOnFrameRenderedListener`. O pts é o timestamp do PC; o `System.nanoTime` é o mesmo relógio do `nowMicros` da `Connection`;
- app em segundo plano: a superfície some, o `detach` solta o decoder e a conexão continua (no melhor esforço). Ao voltar, o `attach` recria o decoder e pede keyframe;
- o botão "Desconectar" virou um "Sair" discreto (60% de opacidade) no canto. O voltar do sistema também desconecta.

- [ ] **Step 1: `MediaCodecPort` e `VideoPlayer`**

`android/app/src/main/java/dev/screenshare/android/video/MediaCodecPort.kt`:

```kotlin
package dev.screenshare.android.video

import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaCodecList
import android.media.MediaFormat
import android.os.Build
import android.os.Handler
import android.util.Log
import android.view.Surface
import java.nio.ByteBuffer

/** Os decoders H.264/H.265 do aparelho, para escolher o de menor latência e dizer ao PC o que o celular decodifica. */
object MediaCodecDecoders {
    fun list(): List<DecoderInfo> = MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos
        .filter { !it.isEncoder }
        .flatMap { info ->
            info.supportedTypes
                .filter { it == CodecSupport.MIME_H264 || it == CodecSupport.MIME_H265 }
                .map { mime ->
                    val capabilities = info.getCapabilitiesForType(mime)
                    DecoderInfo(
                        name = info.name,
                        mime = mime,
                        hardware = info.isHardwareAccelerated,
                        lowLatency = Build.VERSION.SDK_INT >= Build.VERSION_CODES.R &&
                            capabilities.isFeatureSupported(MediaCodecInfo.CodecCapabilities.FEATURE_LowLatency),
                    ) { width, height ->
                        val video = capabilities.videoCapabilities
                        video != null && (video.isSizeSupported(width, height) || video.isSizeSupported(height, width))
                    }
                }
        }
}

/**
 * O [CodecPort] com MediaCodec em modo assíncrono: os callbacks chegam no [handler] (a thread do vídeo, a mesma do
 * DecoderCore). Baixa latência: KEY_LOW_LATENCY (API 30+), prioridade de tempo real e as chaves do fabricante; cada
 * quadro é mostrado assim que sai do decoder.
 */
class MediaCodecPort(
    private val handler: Handler,
    private val surface: () -> Surface?,
    private val decoders: () -> List<DecoderInfo>,
    private val listener: Listener,
) : CodecPort {
    interface Listener {
        fun onInput(generation: Int, index: Int, capacity: Int)
        fun onOutput(generation: Int, index: Int)
        fun onError(generation: Int)

        /** [ptsUs] = timestamp do quadro (relógio do PC); [renderNs] = quando apareceu (System.nanoTime). */
        fun onRendered(ptsUs: Long, renderNs: Long)
    }

    private var codec: MediaCodec? = null
    private var generation = 0

    override fun start(generation: Int, codec: Int, width: Int, height: Int, csd: Csd): Boolean {
        val target = surface() ?: return false
        val info = CodecSupport.choose(decoders(), codec, width, height) ?: return false
        var decoder: MediaCodec? = null
        return try {
            decoder = MediaCodec.createByCodecName(info.name)
            decoder.setCallback(callback(generation), handler)
            decoder.setOnFrameRenderedListener({ _, ptsUs, renderNs -> listener.onRendered(ptsUs, renderNs) }, handler)
            decoder.configure(format(info, codec, width, height, csd), target, null, 0)
            decoder.start()
            this.codec = decoder
            this.generation = generation
            true
        } catch (e: Exception) { // formato recusado, superfície inválida, decoder ocupado
            Log.w(TAG, "Não foi possível abrir ${info.name}", e)
            decoder?.release()
            false
        }
    }

    override fun queue(index: Int, data: ByteArray, ptsUs: Long) {
        val decoder = codec ?: return
        try {
            val buffer = decoder.getInputBuffer(index) ?: return
            buffer.clear()
            buffer.put(data)
            decoder.queueInputBuffer(index, 0, data.size, ptsUs, 0)
        } catch (e: IllegalStateException) {
            fail(e)
        }
    }

    override fun render(index: Int) {
        try {
            codec?.releaseOutputBuffer(index, System.nanoTime())
        } catch (e: IllegalStateException) {
            fail(e)
        }
    }

    override fun release() {
        val decoder = codec ?: return
        codec = null
        try {
            decoder.stop()
        } catch (_: IllegalStateException) {
            // já em erro: o release abaixo resolve
        }
        decoder.release()
    }

    /** O decoder entrou em erro numa chamada nossa: avisa o DecoderCore depois, como um callback de erro. */
    private fun fail(error: Exception) {
        Log.w(TAG, "Erro no decoder", error)
        val failed = generation
        handler.post { listener.onError(failed) }
    }

    private fun callback(generation: Int) = object : MediaCodec.Callback() {
        override fun onInputBufferAvailable(codec: MediaCodec, index: Int) {
            val capacity = try {
                codec.getInputBuffer(index)?.capacity() ?: 0
            } catch (_: IllegalStateException) {
                return
            }
            listener.onInput(generation, index, capacity)
        }

        override fun onOutputBufferAvailable(codec: MediaCodec, index: Int, info: MediaCodec.BufferInfo) =
            listener.onOutput(generation, index)

        override fun onError(codec: MediaCodec, e: MediaCodec.CodecException) {
            Log.w(TAG, "Erro no decoder", e)
            listener.onError(generation)
        }

        override fun onOutputFormatChanged(codec: MediaCodec, format: MediaFormat) = Unit
    }

    private fun format(info: DecoderInfo, codec: Int, width: Int, height: Int, csd: Csd) =
        MediaFormat.createVideoFormat(CodecSupport.mimeOf(codec), width, height).apply {
            setByteBuffer("csd-0", ByteBuffer.wrap(csd.csd0))
            csd.csd1?.let { setByteBuffer("csd-1", ByteBuffer.wrap(it)) }
            setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, maxOf(1 shl 20, width * height))
            setInteger(MediaFormat.KEY_PRIORITY, 0) // tempo real
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R && info.lowLatency) setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
            // Chaves de baixa latência dos fabricantes (spike: o celular de teste é Qualcomm).
            if (info.name.startsWith("c2.qti") || info.name.startsWith("OMX.qcom")) setInteger("vendor.qti-ext-dec-low-latency.enable", 1)
            if (info.name.contains("exynos", ignoreCase = true)) setInteger("vendor.rtc-ext-dec-low-latency.enable", 1)
        }

    private companion object {
        const val TAG = "ScreenShare"
    }
}
```

`android/app/src/main/java/dev/screenshare/android/video/VideoPlayer.kt`:

```kotlin
package dev.screenshare.android.video

import android.os.Handler
import android.os.HandlerThread
import android.os.Process
import android.os.SystemClock
import android.view.Surface
import dev.screenshare.android.protocol.ConfigMessage
import dev.screenshare.android.protocol.FrameMessage
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

/**
 * O vídeo da conexão: recebe CONFIG e FRAMEs (como [VideoSink]) e a superfície da tela, e roda o [DecoderCore] numa
 * thread com prioridade de display. Vive no ViewModel, então sobrevive a mudanças de configuração.
 */
class VideoPlayer(
    decoders: () -> List<DecoderInfo>,
    private val clock: () -> ClockSync,
    requestKeyframe: () -> Unit,
) : VideoSink {
    private val thread = HandlerThread("ScreenShare vídeo", Process.THREAD_PRIORITY_URGENT_DISPLAY).apply { start() }
    private val handler = Handler(thread.looper)

    @Volatile
    private var surface: Surface? = null

    val stats = VideoStats()

    private val core: DecoderCore = DecoderCore(
        MediaCodecPort(handler, { surface }, decoders, object : MediaCodecPort.Listener {
            override fun onInput(generation: Int, index: Int, capacity: Int) = core.onInputAvailable(generation, index, capacity)
            override fun onOutput(generation: Int, index: Int) = core.onOutputAvailable(generation, index)
            override fun onError(generation: Int) = core.onError(generation)
            override fun onRendered(ptsUs: Long, renderNs: Long) {
                val renderUs = renderNs / 1_000
                stats.onRendered(renderUs, clock().toLocalUs(ptsUs)?.let { renderUs - it })
            }
        }),
        requestKeyframe,
        nowMs = { SystemClock.elapsedRealtime() },
    )

    /** A cada 100 ms: o DecoderCore manda o pedido de keyframe que o limite de 1 a cada 500 ms tinha adiado. */
    private val tick = object : Runnable {
        override fun run() {
            core.onTick()
            handler.postDelayed(this, TICK_MS)
        }
    }

    init {
        handler.postDelayed(tick, TICK_MS)
    }

    override fun onConfig(config: ConfigMessage) {
        handler.post { core.onConfig(config) }
    }

    override fun onFrame(frame: FrameMessage) {
        stats.onReceived(frame.data.size, System.nanoTime() / 1_000)
        handler.post { core.onFrame(frame) }
    }

    /** A superfície da tela ficou pronta. */
    fun attach(surface: Surface) {
        handler.post {
            this.surface = surface
            core.onSurface(true)
        }
    }

    /** A superfície vai sumir (tela fechada, app em segundo plano): solta o decoder antes, esperando até 500 ms. */
    fun detach() {
        val done = CountDownLatch(1)
        handler.post {
            core.onSurface(false)
            surface = null
            done.countDown()
        }
        done.await(500, TimeUnit.MILLISECONDS)
    }

    /** Conexão nova: esquece o CONFIG anterior. */
    fun reset() {
        handler.post { core.reset() }
    }

    fun release() {
        handler.removeCallbacks(tick)
        handler.post { core.release() }
        thread.quitSafely()
    }

    private companion object {
        const val TICK_MS = 100L
    }
}
```

- [ ] **Step 2: ViewModel e telas**

Em `android/app/src/main/java/dev/screenshare/android/net/ConnectionViewModel.kt`. As declarações de `player` e `connection` levam o tipo explícito; sem isso, o Kotlin acusa "Type checking has run into a recursive problem", porque uma referencia a outra.

```diff
@@ -9,10 +9,15 @@ import androidx.lifecycle.viewModelScope
 import dev.screenshare.android.pairing.PairingUri
 import dev.screenshare.android.pairing.deviceNameOf
 import dev.screenshare.android.protocol.DeniedReason
+import dev.screenshare.android.protocol.VideoCodec
 import dev.screenshare.android.security.KeystoreTokenCipher
 import dev.screenshare.android.security.PairedPc
 import dev.screenshare.android.security.PairingStore
 import dev.screenshare.android.security.pairingDataStore
+import dev.screenshare.android.video.CodecSupport
+import dev.screenshare.android.video.DecoderInfo
+import dev.screenshare.android.video.MediaCodecDecoders
+import dev.screenshare.android.video.VideoPlayer
 import kotlinx.coroutines.CancellationException
 import kotlinx.coroutines.flow.MutableStateFlow
 import kotlinx.coroutines.flow.SharingStarted
@@ -35,8 +40,24 @@ class ConnectionViewModel(application: Application) : AndroidViewModel(applicati
     /** Aviso para o usuário fora do estado da conexão (QR inválido, leitor indisponível…). */
     val message: StateFlow<String?> = _message.asStateFlow()
 
-    private val connection = Connection(
-        viewModelScope, screen = { landscapeScreenInfo(application) },
+    /** Os decoders do aparelho (lidos uma vez): escolhem o decoder e os codecs do HELLO. */
+    private val decoders: List<DecoderInfo> by lazy { MediaCodecDecoders.list() }
+
+    /** O vídeo da conexão; a tela imersiva liga a superfície dela a ele. */
+    val player: VideoPlayer = VideoPlayer(
+        decoders = { decoders },
+        clock = { connection.clock },
+        requestKeyframe = { connection.requestKeyframe() },
+    )
+
+    private val connection: Connection = Connection(
+        viewModelScope,
+        screen = {
+            val screen = landscapeScreenInfo(application)
+            // Sem decoder que abra esse tamanho (raro): manda os dois e deixa o PC decidir, como antes.
+            screen.copy(codecs = CodecSupport.supported(decoders, screen.width, screen.height).takeIf { it != 0 } ?: VideoCodec.ALL)
+        },
+        sink = player,
         // Roda na thread de IO dentro da conexão: não pode lançar (derrubaria o app), então só muda estado e dispara o salvamento.
         onPaired = { pc ->
             persist("Não foi possível salvar o pareamento") { store.save(pc) }
@@ -70,6 +91,7 @@ class ConnectionViewModel(application: Application) : AndroidViewModel(applicati
             return
         }
         _message.value = null
+        player.reset()
         connection.connect(ConnectTarget.Pairing(info, deviceNameOf(Build.MODEL), overUsb))
     }
 
@@ -84,6 +106,7 @@ class ConnectionViewModel(application: Application) : AndroidViewModel(applicati
             return
         }
         _message.value = null
+        player.reset()
         connection.connect(ConnectTarget.Wifi(address, pc))
         persist("Não foi possível salvar o último endereço") { store.updateLastHost(address.host) }
     }
@@ -95,6 +118,7 @@ class ConnectionViewModel(application: Application) : AndroidViewModel(applicati
             return
         }
         _message.value = null
+        player.reset()
         connection.connect(ConnectTarget.Usb(pc))
     }
 
@@ -105,7 +129,10 @@ class ConnectionViewModel(application: Application) : AndroidViewModel(applicati
 
     fun disconnect() = connection.disconnect()
 
-    override fun onCleared() = connection.disconnect()
+    override fun onCleared() {
+        connection.disconnect()
+        player.release()
+    }
 
     /**
      * Roda uma gravação do [store] sem deixar a falha escapar: o viewModelScope não tem handler e uma exceção
```

`android/app/src/main/java/dev/screenshare/android/ui/ImmersiveScreen.kt` inteiro:

```kotlin
package dev.screenshare.android.ui

import android.app.Activity
import android.content.pm.ActivityInfo
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.WindowManager
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import dev.screenshare.android.net.ConnectionState
import dev.screenshare.android.video.VideoPlayer
import dev.screenshare.android.video.VideoStats
import kotlinx.coroutines.delay

/**
 * A tela do celular como monitor: o vídeo do PC em tela cheia, na horizontal, um pixel do PC por pixel do painel
 * (setFixedSize no tamanho do CONFIG), sobre fundo preto. Em cima, o overlay de latência e um botão discreto de sair.
 */
@Composable
fun ImmersiveScreen(state: ConnectionState.Connected, player: VideoPlayer, onDisconnect: () -> Unit) {
    ImmersiveWindowEffect()
    BackHandler(onBack = onDisconnect)
    val width = state.config.width
    val height = state.config.height

    Box(modifier = Modifier.fillMaxSize().background(Color.Black), contentAlignment = Alignment.Center) {
        AndroidView(
            factory = { context ->
                SurfaceView(context).apply {
                    holder.addCallback(object : SurfaceHolder.Callback {
                        override fun surfaceCreated(holder: SurfaceHolder) = player.attach(holder.surface)
                        override fun surfaceChanged(holder: SurfaceHolder, format: Int, w: Int, h: Int) = Unit
                        override fun surfaceDestroyed(holder: SurfaceHolder) = player.detach()
                    })
                }
            },
            update = { view -> view.holder.setFixedSize(width, height) },
            modifier = Modifier.aspectRatio(width.toFloat() / height),
        )

        VideoOverlay(
            stats = player.stats,
            rttMs = state.rttMs,
            modifier = Modifier.align(Alignment.TopStart).safeDrawingPadding().padding(8.dp),
        )

        TextButton(
            onClick = onDisconnect,
            modifier = Modifier.align(Alignment.TopEnd).safeDrawingPadding().alpha(0.6f),
        ) {
            Text("Sair", color = Color.White)
        }
    }
}

/** "≈latência · fps · Mbps · RTT", atualizado duas vezes por segundo. */
@Composable
private fun VideoOverlay(stats: VideoStats, rttMs: Double?, modifier: Modifier = Modifier) {
    var text by remember { mutableStateOf("") }
    LaunchedEffect(stats, rttMs) {
        while (true) {
            text = VideoStats.overlayText(stats.snapshot(System.nanoTime() / 1_000), rttMs)
            delay(500)
        }
    }
    Text(
        text = text,
        color = Color.Green,
        fontSize = 12.sp,
        modifier = modifier
            .background(Color(0x99000000), RoundedCornerShape(4.dp))
            .padding(horizontal = 6.dp, vertical = 2.dp),
    )
}

/**
 * Esconde as barras do sistema, trava a horizontal, cobre o recorte da câmera (o vídeo usa o painel inteiro) e mantém
 * a tela acesa enquanto a tela imersiva está visível.
 */
@Composable
private fun ImmersiveWindowEffect() {
    val view = LocalView.current
    DisposableEffect(view) {
        val activity = view.context as? Activity
        val window = activity?.window
        val previousOrientation = activity?.requestedOrientation
        val previousCutout = window?.attributes?.layoutInDisplayCutoutMode
        val controller = window?.let { WindowCompat.getInsetsController(it, view) }

        activity?.requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
        window?.attributes = window?.attributes?.also {
            it.layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES
        }
        controller?.systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        controller?.hide(WindowInsetsCompat.Type.systemBars())
        view.keepScreenOn = true

        onDispose {
            view.keepScreenOn = false
            controller?.show(WindowInsetsCompat.Type.systemBars())
            if (previousCutout != null) window.attributes = window.attributes.also { it.layoutInDisplayCutoutMode = previousCutout }
            previousOrientation?.let { activity.requestedOrientation = it }
        }
    }
}
```

Em `android/app/src/main/java/dev/screenshare/android/ui/ScreenShareApp.kt`:

```diff
@@ -18,7 +18,7 @@ fun ScreenShareApp(viewModel: ConnectionViewModel) {
     val context = LocalContext.current
 
     when (val current = state) {
-        is ConnectionState.Connected -> ImmersiveScreen(current, onDisconnect = viewModel::disconnect)
+        is ConnectionState.Connected -> ImmersiveScreen(current, viewModel.player, onDisconnect = viewModel::disconnect)
         else -> {
             val hosts by viewModel.hosts.collectAsState()
             val pairedPc by viewModel.pairedPc.collectAsState()
```

- [ ] **Step 3: Compilar e testar**

Run: `.\android\gradlew.bat -p android :app:assembleDebug :app:testDebugUnitTest --continue`
Expected: o APK compila sem avisos novos do Kotlin (linhas `w:`). São 120 testes, todos aprovados, exceto as 2 falhas conhecidas do `PairingStoreTest`.

- [ ] **Step 4: Commit e push**

```bash
git add android/app/src
git commit -m "feat(android): vídeo do PC em tela cheia com MediaCodec de baixa latência e overlay de latência"
git push
```

- [ ] **Step 5: Verificação manual (o controlador faz com o usuário, depois da revisão)**

1. Instalar o app: `.\android\gradlew.bat -p android :app:installDebug` (celular no cabo, depuração USB ligada).
2. No PC, na pasta do worktree: `adb reverse tcp:38701 tcp:38701` e `dotnet run --project host/ScreenShare.DevHost`.
3. No app, conectar pelo cabo. Em até 2 s o celular mostra o monitor virtual em tela cheia. O overlay no canto mostra latência, fps, Mbps e RTT.
4. Mandar uma janela para o monitor virtual (Win+Shift+→) e conferir:
   - texto nítido, sem borrão, depois que a tela para;
   - rolar uma página: fluido, e o overlay fica perto de 60 fps.
5. Apertar o botão de início no celular, esperar 5 s e voltar ao app: o vídeo volta sozinho em até 1 s.
6. Repetir pelo Wi-Fi.
7. O cursor ainda não aparece no celular; ele entra na Task 13.

---

### Task 13: cursor desenhado no vídeo

**Por quê:** a Desktop Duplication entrega a tela sem o cursor. O cursor vem à parte (posição, visibilidade e formato) e é desenhado na imagem antes do encode. Mexer só o mouse também gera um quadro novo, dentro do limite de fps. O cursor é misturado na CPU: só a região dele vai e volta da placa, sem shaders. Isso foi conferido salvando uma captura com o cursor desenhado na posição certa.

**Files:**
- Create: `host/ScreenShare.Video/Hardware/PointerShapes.cs` (conversor e mistura, puros)
- Modify: `host/ScreenShare.Video/Hardware/DesktopDuplicationCapture.cs` (`_desktop` + `_composed`, `UpdatePointer` e `Compose`)
- Test: `host/ScreenShare.Tests/Video/PointerShapesTests.cs`

**Interfaces:**
- Consumes: `DesktopDuplicationCapture` e `GpuContext` (Task 7).
- Produces:
  - `internal record PointerImage(int Width, int Height, byte[] Bgra)`;
  - `internal PointerShapes.ToBgra(int type, int width, int height, int pitch, ReadOnlySpan<byte> shape)`, com as constantes `Monochrome = 1`, `Color = 2` e `MaskedColor = 4`;
  - `internal PointerShapes.Blend(Span<byte> region, int width, int height, PointerImage pointer, int offsetX, int offsetY)`.
- O contrato do `IScreenCapture` não muda. Só que agora `NewImage` também vem quando só o mouse mexeu. Nesse caso o `presentUs` é o `LastMouseUpdateTime`.

**Convenção dos pixels do ponteiro** (a do ponteiro colorido com máscara do DXGI):
- alfa 255: opaco;
- alfa 0 com cor 0: transparente;
- alfa 0 com cor diferente de 0: XOR com a tela. O I-beam de texto inverte o fundo; por isso ele aparece sobre o claro e sobre o escuro;
- outro alfa: mistura.

- [ ] **Step 1: Testes**

`host/ScreenShare.Tests/Video/PointerShapesTests.cs`:

```csharp
using ScreenShare.Video.Hardware;

namespace ScreenShare.Tests.Video;

public sealed class PointerShapesTests
{
    [Fact]
    public void Monochrome_pointer_becomes_black_white_transparent_and_invert()
    {
        // 4 pixels: (AND, XOR) = (0,0), (0,1), (1,0), (1,1); 1 bit por pixel, o mais alto primeiro.
        byte[] shape = [0b0011_0000, 0b0101_0000];

        var image = PointerShapes.ToBgra(PointerShapes.Monochrome, width: 4, height: 2, pitch: 1, shape);

        Assert.Equal((4, 1), (image.Width, image.Height));
        Assert.Equal(new byte[] { 0, 0, 0, 255, 255, 255, 255, 255, 0, 0, 0, 0, 255, 255, 255, 0 }, image.Bgra);
    }

    [Fact]
    public void Color_pointer_keeps_its_alpha_and_clears_fully_transparent_pixels()
    {
        byte[] shape = [10, 20, 30, 128, 40, 50, 60, 0];

        var image = PointerShapes.ToBgra(PointerShapes.Color, width: 2, height: 1, pitch: 8, shape);

        Assert.Equal(new byte[] { 10, 20, 30, 128, 0, 0, 0, 0 }, image.Bgra);
    }

    [Fact]
    public void Masked_color_pointer_turns_the_mask_into_opaque_or_invert()
    {
        byte[] shape = [10, 20, 30, 0x00, 255, 255, 255, 0xFF, 0, 0, 0, 0xFF, 0, 0];

        var image = PointerShapes.ToBgra(PointerShapes.MaskedColor, width: 3, height: 1, pitch: 14, shape);

        Assert.Equal(new byte[] { 10, 20, 30, 255, 255, 255, 255, 0, 0, 0, 0, 0 }, image.Bgra);
    }

    [Fact]
    public void Blend_replaces_inverts_keeps_and_mixes()
    {
        var pointer = new PointerImage(4, 1, [1, 2, 3, 255, 255, 255, 255, 0, 0, 0, 0, 0, 200, 200, 200, 128]);
        byte[] region = [100, 100, 100, 255, 100, 100, 100, 255, 100, 100, 100, 255, 0, 0, 0, 255];

        PointerShapes.Blend(region, 4, 1, pointer, 0, 0);

        Assert.Equal(new byte[] { 1, 2, 3, 255, 155, 155, 155, 255, 100, 100, 100, 255, 100, 100, 100, 255 }, region);
    }

    [Fact]
    public void Blend_clips_a_pointer_that_leaves_the_region()
    {
        var pointer = new PointerImage(2, 2, Enumerable.Repeat(new byte[] { 9, 9, 9, 255 }, 4).SelectMany(p => p).ToArray());
        var region = new byte[2 * 2 * 4];

        PointerShapes.Blend(region, 2, 2, pointer, offsetX: 1, offsetY: -1); // metade para fora pela direita e pelo topo

        Assert.Equal(new byte[] { 0, 0, 0, 0, 9, 9, 9, 255, 0, 0, 0, 0, 0, 0, 0, 0 }, region);
    }
}
```

Run: `dotnet test host/ScreenShare.slnx --filter FullyQualifiedName~PointerShapesTests`
Expected: erro de compilação.

- [ ] **Step 2: Conversor e mistura**

`host/ScreenShare.Video/Hardware/PointerShapes.cs`:

```csharp
namespace ScreenShare.Video.Hardware;

/// <summary>
/// O ponteiro do mouse em BGRA, 4 bytes por pixel, linhas sem folga. Convenção (a do ponteiro colorido com máscara do
/// DXGI): alfa 255 = pixel opaco; alfa 0 com cor 0 = transparente; alfa 0 com cor ≠ 0 = inverter a tela (XOR) com essa
/// cor; outro alfa = mistura.
/// </summary>
internal sealed record PointerImage(int Width, int Height, byte[] Bgra);

/// <summary>Converte os três formatos de ponteiro do DXGI e desenha o ponteiro sobre a imagem da tela. Puro.</summary>
internal static class PointerShapes
{
    public const int Monochrome = 1;
    public const int Color = 2;
    public const int MaskedColor = 4;

    /// <param name="height">Como o DXGI informa: no monocromático é o dobro (máscara AND em cima, XOR embaixo).</param>
    public static PointerImage ToBgra(int type, int width, int height, int pitch, ReadOnlySpan<byte> shape) => type switch
    {
        Monochrome => FromMonochrome(width, height / 2, pitch, shape),
        Color => FromColor(width, height, pitch, shape, masked: false),
        MaskedColor => FromColor(width, height, pitch, shape, masked: true),
        _ => throw new ArgumentException($"formato de ponteiro desconhecido: {type}", nameof(type)),
    };

    /// <summary>
    /// Desenha o ponteiro numa região BGRA de width×height (linhas sem folga). offsetX/offsetY = onde o canto do
    /// ponteiro cai na região (negativo quando o ponteiro sai da tela pela esquerda ou pelo topo).
    /// </summary>
    public static void Blend(Span<byte> region, int width, int height, PointerImage pointer, int offsetX, int offsetY)
    {
        for (var py = 0; py < pointer.Height; py++)
        {
            var ry = py + offsetY;
            if (ry < 0 || ry >= height) continue;
            for (var px = 0; px < pointer.Width; px++)
            {
                var rx = px + offsetX;
                if (rx < 0 || rx >= width) continue;
                var source = pointer.Bgra.AsSpan((py * pointer.Width + px) * 4, 4);
                var target = region.Slice((ry * width + rx) * 4, 4);
                var alpha = source[3];
                if (alpha == 255)
                {
                    source[..3].CopyTo(target);
                }
                else if (alpha == 0)
                {
                    for (var c = 0; c < 3; c++) target[c] ^= source[c]; // cor 0 = transparente (XOR com zero)
                }
                else
                {
                    for (var c = 0; c < 3; c++) target[c] = (byte)((source[c] * alpha + target[c] * (255 - alpha) + 127) / 255);
                }
                target[3] = 255;
            }
        }
    }

    private static PointerImage FromMonochrome(int width, int height, int pitch, ReadOnlySpan<byte> shape)
    {
        var bgra = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var bit = 0x80 >> (x % 8);
                var and = (shape[y * pitch + x / 8] & bit) != 0;
                var xor = (shape[(y + height) * pitch + x / 8] & bit) != 0;
                // AND 0: pixel opaco (preto ou branco); AND 1: transparente, ou inverter a tela se XOR 1.
                var (color, alpha) = (and, xor) switch
                {
                    (false, false) => ((byte)0, (byte)255),
                    (false, true) => ((byte)255, (byte)255),
                    (true, false) => ((byte)0, (byte)0),
                    (true, true) => ((byte)255, (byte)0),
                };
                var i = (y * width + x) * 4;
                bgra[i] = bgra[i + 1] = bgra[i + 2] = color;
                bgra[i + 3] = alpha;
            }
        }
        return new PointerImage(width, height, bgra);
    }

    private static PointerImage FromColor(int width, int height, int pitch, ReadOnlySpan<byte> shape, bool masked)
    {
        var bgra = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            shape.Slice(y * pitch, width * 4).CopyTo(bgra.AsSpan(y * width * 4));
        }
        for (var i = 0; i < bgra.Length; i += 4)
        {
            if (masked)
            {
                // Máscara 0: a cor substitui a tela; 0xFF: a cor faz XOR com a tela.
                bgra[i + 3] = bgra[i + 3] == 0 ? (byte)255 : (byte)0;
            }
            else if (bgra[i + 3] == 0)
            {
                bgra[i] = bgra[i + 1] = bgra[i + 2] = 0; // totalmente transparente (não confundir com XOR)
            }
        }
        return new PointerImage(width, height, bgra);
    }
}
```

Run: `dotnet test host/ScreenShare.slnx --filter FullyQualifiedName~PointerShapesTests`
Expected: PASS (5 casos).

- [ ] **Step 3: Captura com cursor**

Substitua `host/ScreenShare.Video/Hardware/DesktopDuplicationCapture.cs` pelo conteúdo abaixo:

```csharp
using System.Diagnostics;
using System.Runtime.InteropServices;
using ScreenShare.Core.Video;
using ScreenShare.Video.Pipeline;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ScreenShare.Video.Hardware;

/// <summary>Imagem BGRA na placa de vídeo (a cópia própria da captura).</summary>
internal sealed class GpuImage(ID3D11Texture2D texture, int width, int height) : IVideoImage
{
    public ID3D11Texture2D Texture { get; } = texture;
    public int Width { get; } = width;
    public int Height { get; } = height;
}

/// <summary>
/// Captura de uma saída com DXGI Desktop Duplication, com o cursor do mouse desenhado na imagem. A tela é copiada para
/// uma textura própria (_desktop) e o quadro do Windows é liberado na hora; Last (_composed) = tela + cursor. Mexer só o
/// mouse também dá imagem nova. Precisa de DPI por monitor na thread que a cria e usa (CaptureThread.Prepare).
/// </summary>
internal sealed class DesktopDuplicationCapture : IScreenCapture
{
    private readonly GpuContext _gpu;
    private readonly IDXGIOutputDuplication _duplication;
    private readonly ID3D11Texture2D _desktop;
    private readonly ID3D11Texture2D _composed;
    private ID3D11Texture2D? _staging;
    private int _stagingSize;
    private PointerImage? _pointer;
    private (int X, int Y) _pointerPosition;
    private bool _pointerVisible;

    public DesktopDuplicationCapture(GpuContext gpu, IDXGIOutput output)
    {
        _gpu = gpu;
        _duplication = Duplicate(gpu, output);
        var mode = _duplication.Description.ModeDescription;
        Width = (int)mode.Width;
        Height = (int)mode.Height;
        var description = new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)Width, (uint)Height, 1, 1,
            BindFlags.RenderTarget | BindFlags.ShaderResource);
        _desktop = gpu.Device.CreateTexture2D(description);
        _composed = gpu.Device.CreateTexture2D(description);
        Last = new GpuImage(_composed, Width, Height);
    }

    public int Width { get; }
    public int Height { get; }
    public IVideoImage Last { get; }

    public AcquireResult TryAcquire(TimeSpan timeout, out ulong presentUs)
    {
        presentUs = 0;
        var result = _duplication.AcquireNextFrame((uint)timeout.TotalMilliseconds, out var info, out var resource);
        if (result.Code == DxgiErrors.WaitTimeout) return AcquireResult.Timeout;
        if (result.Failure) throw DxgiErrors.ToException(result.Code, "AcquireNextFrame");
        try
        {
            var desktopChanged = info.LastPresentTime != 0;
            var pointerChanged = UpdatePointer(info);
            if (!desktopChanged && !pointerChanged) return AcquireResult.PointerOnly;
            if (desktopChanged)
            {
                using var texture = resource.QueryInterface<ID3D11Texture2D>();
                _gpu.Context.CopyResource(_desktop, texture);
            }
            Compose();
            var when = desktopChanged ? info.LastPresentTime : info.LastMouseUpdateTime;
            presentUs = PcClock.ToMicroseconds(when, Stopwatch.Frequency);
            return AcquireResult.NewImage;
        }
        finally
        {
            resource.Dispose();
            _duplication.ReleaseFrame(); // se falhar (ACCESS_LOST), a próxima AcquireNextFrame avisa
        }
    }

    public void Dispose()
    {
        _duplication.Dispose();
        _staging?.Dispose();
        _composed.Dispose();
        _desktop.Dispose();
    }

    /// <summary>Lê posição, visibilidade e formato novos do ponteiro (antes do ReleaseFrame). true = algo mudou.</summary>
    private unsafe bool UpdatePointer(OutduplFrameInfo info)
    {
        var changed = false;
        if (info.LastMouseUpdateTime != 0)
        {
            var position = (info.PointerPosition.Position.X, info.PointerPosition.Position.Y);
            bool visible = info.PointerPosition.Visible;
            changed = position != _pointerPosition || visible != _pointerVisible;
            _pointerPosition = position;
            _pointerVisible = visible;
        }
        if (info.PointerShapeBufferSize > 0)
        {
            var buffer = new byte[info.PointerShapeBufferSize];
            fixed (byte* pointer = buffer)
            {
                var result = _duplication.GetFramePointerShape((uint)buffer.Length, (nint)pointer, out _, out var shape);
                if (result.Success)
                {
                    _pointer = PointerShapes.ToBgra((int)shape.Type, (int)shape.Width, (int)shape.Height, (int)shape.Pitch, buffer);
                    changed = true;
                }
            }
        }
        return changed;
    }

    /// <summary>
    /// Last = tela + cursor. O cursor é misturado na CPU: só a região dele vai e volta da placa (uma cópia pequena), sem
    /// shaders.
    /// </summary>
    private void Compose()
    {
        _gpu.Context.CopyResource(_composed, _desktop);
        if (!_pointerVisible || _pointer is not { } pointer) return;

        var (x, y) = _pointerPosition;
        int left = Math.Max(x, 0), top = Math.Max(y, 0);
        int right = Math.Min(x + pointer.Width, Width), bottom = Math.Min(y + pointer.Height, Height);
        if (right <= left || bottom <= top) return;
        int width = right - left, height = bottom - top;

        var staging = Staging(Math.Max(width, height));
        var area = new Box(left, top, 0, right, bottom, 1);
        _gpu.Context.CopySubresourceRegion(staging, 0, 0, 0, 0, _desktop, 0, area);
        var region = new byte[width * height * 4];
        var mapped = _gpu.Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            for (var row = 0; row < height; row++)
                Marshal.Copy(mapped.DataPointer + row * (int)mapped.RowPitch, region, row * width * 4, width * 4);
        }
        finally
        {
            _gpu.Context.Unmap(staging, 0);
        }
        PointerShapes.Blend(region, width, height, pointer, x - left, y - top);
        _gpu.Context.UpdateSubresource(region, _composed, 0, (uint)(width * 4), 0, area);
    }

    /// <summary>Textura de leitura pela CPU do tamanho do cursor (recriada só se o cursor crescer).</summary>
    private ID3D11Texture2D Staging(int size)
    {
        if (_staging is not null && _stagingSize >= size) return _staging;
        _staging?.Dispose();
        _stagingSize = Math.Max(size, 64);
        _staging = _gpu.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)_stagingSize,
            (uint)_stagingSize, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        return _staging;
    }

    private static IDXGIOutputDuplication Duplicate(GpuContext gpu, IDXGIOutput output)
    {
        try
        {
            try
            {
                using var output5 = output.QueryInterface<IDXGIOutput5>();
                return output5.DuplicateOutput1(gpu.Device, [Format.B8G8R8A8_UNorm]);
            }
            catch (SharpGenException e) when (e.HResult == DxgiErrors.Unsupported)
            {
                using var output1 = output.QueryInterface<IDXGIOutput1>();
                return output1.DuplicateOutput(gpu.Device);
            }
        }
        catch (SharpGenException e)
        {
            throw DxgiErrors.ToException(e.HResult, "duplicar a saída", e);
        }
    }
}
```

Run (PowerShell, no PC do usuário): `$env:SCREENSHARE_GPU_TESTS=1; dotnet test host/ScreenShare.slnx; Remove-Item Env:SCREENSHARE_GPU_TESTS`
Expected: 369 aprovados e 1 ignorado (o teste do driver de monitor virtual).

- [ ] **Step 4: Rodar tudo, commit e push**

Run: `dotnet build host/ScreenShare.slnx` e `dotnet test host/ScreenShare.slnx`
Expected: 0 avisos; 361 aprovados e 9 ignorados.

```bash
git add host/ScreenShare.Video host/ScreenShare.Tests/Video
git commit -m "feat(video): cursor do mouse desenhado no vídeo (monocromático, colorido e com máscara)"
git push
```

- [ ] **Step 5: Verificação manual (o controlador faz com o usuário, depois da revisão)**

Com o DevHost e o app conectados (como no Step 5 da Task 12):
- passar o mouse para o monitor virtual: o cursor aparece no celular e acompanha o movimento sem atraso visível;
- num editor de texto, o cursor vira o I-beam e aparece sobre o fundo claro e sobre o escuro;
- arrastar uma janela pela barra de título e ver o cursor junto.

---

### Task 14: verificação ponta a ponta e documentação

**Quem executa:** dois passos.
1. **Verificação manual** (Step 1): o controlador, com o usuário, no PC e no celular. O controlador anota os resultados.
2. **Documentação** (Steps 2 a 5): um subagente, com as notas do Step 1 em mãos.

**Files:**
- Modify: `docs/superpowers/specs/2026-10-04-parte-3-video-design.md` (seção nova "Resultados da verificação (etapa A)")
- Modify: `README.md` (vídeo funcionando, opções do DevHost e como rodar os testes de GPU)
- Modify: `docs/guia-do-codigo.md` (projeto `ScreenShare.Video`, arquivos novos do host e do app, contagem de testes)

- [ ] **Step 1: Verificação manual (controlador + usuário)**

Antes: `dotnet test host/ScreenShare.slnx` com e sem `SCREENSHARE_GPU_TESTS=1`, e `.\android\gradlew.bat -p android :app:testDebugUnitTest`. Depois, com o app instalado (`:app:installDebug`) e o DevHost rodando, a lista do spec:

1. **Imagem nítida e 1:1:** texto pequeno legível no celular; arrastar uma janela para o monitor do celular com o cursor visível.
2. **Latência:** < 50 ms no cabo e < 80 ms no Wi-Fi.
   - Abrir um cronômetro em milissegundos no monitor virtual (por exemplo, numa página de cronômetro online).
   - Filmar com outro celular, ao mesmo tempo, a tela do PC espelhada e o celular.
   - A diferença entre os dois números no vídeo é a latência; ela deve bater com o "≈" do overlay (±10 ms).
3. **Resolução nova:** o celular pede uma resolução que o driver ainda não tem → UAC → o monitor muda no meio da sessão. O celular recebe o `CONFIG` novo e o vídeo volta sem reconectar.
4. **Win+L e UAC:** o vídeo para durante a área de trabalho segura e volta sozinho depois.
5. **Sessão nova assume:** conectado pelo Wi-Fi, conectar pelo cabo. O vídeo passa para a conexão do cabo, e a do Wi-Fi fica sem vídeo até cair.
6. **`--fps 30`:** o overlay mostra até 30 fps.
7. **Tela parada e rede ruim:**
   - parar de mexer: em menos de 1 s o texto fica nítido (refinamentos);
   - travada do Wi-Fi (afastar o celular do roteador por uns segundos): o vídeo se recupera sozinho, com descartes e um IDR nas estatísticas do console.
8. **Segundo plano:** apertar o início, esperar 10 s e voltar ao app. O vídeo volta, ou, se o Android cortou a rede, o app volta para a lista com a mensagem.

Anote para cada item: ok ou não, os números (latência filmada × overlay, fps, Mbps) e qualquer comportamento estranho. Um item que falhar vira uma correção (fix) antes dos Steps 2 a 5.

- [ ] **Step 2: Spec**

Em `docs/superpowers/specs/2026-10-04-parte-3-video-design.md`, acrescente no fim a seção `## Resultados da verificação (etapa A)`. Ela leva uma tabela com os 8 itens do Step 1 e uma frase de resultado com os números anotados, no estilo da tabela "Resultados do spike". Também entra o que ficou para a etapa B: encoder em software e reconexão automática.

- [ ] **Step 3: README**

Em `README.md`:
- o roadmap marca a Parte 3 (vídeo) como feita na etapa A;
- "Começando" ganha as opções do DevHost: `--fps`, `--bitrate`, `--codec`, `--sem-video`, `--capturar principal` e `--gravar`, uma linha cada, como no comentário do `Program.cs`;
- explicar a linha de estatísticas do console e o overlay do celular (`≈latência · fps · Mbps · RTT`);
- os testes de GPU: `$env:SCREENSHARE_GPU_TESTS=1; dotnet test host/ScreenShare.slnx`.

Mantenha o tom e o formato do README atual (pt-BR, emojis nos títulos que já existem).

- [ ] **Step 4: Guia do código**

Em `docs/guia-do-codigo.md`:
- uma seção para o projeto `host/ScreenShare.Video`, com uma linha por arquivo de `Pipeline/` e de `Hardware/` dizendo o que faz;
- as linhas dos arquivos novos e alterados do Core (`Video/AnnexB.cs`, `Video/PcClock.cs`, `Protocol/VideoSendQueue.cs`, `Protocol/SessionWriter.cs`), do DevHost (`DevHostOptions.cs`, `VideoStatsLine.cs`, `HostServer.cs`) e do app (`video/*.kt`, `net/Connection.kt`, `ui/ImmersiveScreen.kt`);
- as contagens de testes: 361 aprovados e 9 ignorados no host sem GPU, 369 e 1 com GPU, e 120 no app.

Siga o formato de tabela "Arquivo | O que faz" que o guia já usa.

- [ ] **Step 5: Commit e push**

```bash
git add README.md docs/guia-do-codigo.md docs/superpowers/specs/2026-10-04-parte-3-video-design.md
git commit -m "docs: vídeo da Parte 3 no README, no guia do código e no spec (resultados da verificação)"
git push
```
