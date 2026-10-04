# Parte 3: vídeo ponta a ponta (design)

## Contexto

A Parte 2 liga um monitor virtual (VDD) no PC quando o celular conecta, mas o celular ainda mostra um placeholder. A Parte 3 faz o conteúdo desse monitor aparecer no celular, em tela cheia, pelo Wi-Fi e pelo cabo. A prioridade é **texto nítido** (uso de produtividade), e as metas do spec geral são **< 50 ms no USB** e **< 80 ms no Wi-Fi**.

Ponto de partida:
- `HostServer` manda um `CONFIG` fixo (H.264, 8000 kbps, `codecConfig` vazio) e escreve no `SslStream` sem trava. Só o laço da sessão escreve hoje.
- O app ignora `FRAME`, `CONFIG` no meio da sessão e `PING` vindo do PC. `ImmersiveScreen` é um placeholder com o RTT.
- O host não usa nenhuma biblioteca de DirectX nem de Media Foundation. A interop até aqui é `DllImport` escrita à mão.
- PC do usuário: Windows 11 26200, RTX 5060 Ti, monitor principal de 3440×1440, ligado ao roteador por cabo de rede. Celular: SM-F976B (pediu 2520×1080 na Parte 2) em Wi-Fi 5 GHz.

## Decisões

- **Captura:** DXGI Desktop Duplication da saída do monitor virtual. O plano B é Windows.Graphics.Capture, e o spike decide entre os dois.
- **Encode:**
  - H.265 por hardware via Media Foundation (NVENC na RTX), com H.264 quando o celular ou o encoder não tiver H.265;
  - encoder H.264 em software quando não houver encoder de hardware ou ele falhar (etapa B);
  - interop via **Vortice.Windows** (`Vortice.Direct3D11`, `Vortice.DXGI`, `Vortice.MediaFoundation`), mais `ICodecAPI` escrito à mão, porque o Vortice não mapeia o `codecapi.h`.
- **Taxa de quadros:** até **60 fps**, configurável por `--fps` no DevHost (a tela de ajuste fica para a Parte 6). Quadros só quando a tela muda.
- **Cursor:** desenhado no vídeo pelo PC; o protocolo não muda.
- **Robustez inteira nesta parte**, em duas etapas:
  - **A:** recriar a captura (UAC, bloqueio, troca de resolução), controle de fluxo, KEYFRAME_REQ, `CONFIG` no meio da sessão;
  - **B:** encoder em software e reconexão automática.
- **Reconexão:** até o usuário cancelar, com espera de 1 s, 2 s, 4 s e no máximo 10 s entre tentativas. Não reconecta depois de `DENIED`.
- **Overlay:** pequeno e sempre visível, `≈latência · fps · Mbps · RTT`.
- **App em segundo plano:** continua conectado e sem vídeo, no melhor esforço (sem serviço em primeiro plano; o Android pode cortar a rede com a tela apagada). Ao voltar, cria um decoder novo e pede keyframe. Se a conexão tiver caído, a reconexão cobre.
- **Bitrate inicial:**
  - USB: 50 Mbps de média e 100 de pico;
  - Wi-Fi: 25 Mbps de média e 50 de pico;
  - o spike calibra esses valores, e `--bitrate` sobrescreve.
- **O protocolo continua na v2.** Nenhum formato de mensagem muda; só regras de uso (seção "Protocolo").

## Achados que moldam o design

1. **P-frames já codificados não podem ser descartados.** Sem B-frames, cada P-frame é referência do seguinte. O controle de fluxo principal fica **antes** do encode: a captura não pega quadro novo enquanto o escritor tiver um quadro pendente, e o DXGI acumula as mudanças nesse meio-tempo, então o próximo quadro já é a imagem mais nova. A rede de segurança: com mais de 2 quadros na fila de envio, descarta os não-keyframe até o próximo IDR e força um IDR.
2. **Escritor único por sessão.** O `SslStream` não aceita duas `WriteAsync` ao mesmo tempo. Uma tarefa faz todas as escritas: mensagens de controle (`PONG`, `PING`, `CONFIG` de fallback) saem antes do vídeo.
3. **Tela parada não gera quadro.** Um KEYFRAME_REQ, um decoder recriado ou um `CONFIG` novo exigem recodificar a última imagem, que a captura guarda numa cópia própria na GPU.
4. **Refinamentos.** Depois de uma mudança grande, o controle de bitrate pode deixar o quadro borrado, e com a tela parada nada o corrige. O PC recodifica a última imagem +100, +300 e +700 ms depois que a tela para, com um teto de QP opcional.
5. **Uma duplicação por saída por processo.** O vídeo é exclusivo: uma sessão nova (por exemplo, USB enquanto a do Wi-Fi ainda espera o prazo) **assume** o vídeo, e a antiga fica sem.
6. **Dispositivo D3D no adaptador da saída.** Primeiro acha a saída (casando o `DeviceName` do monitor virtual), depois cria o device nesse adaptador e escolhe o encoder pelo mesmo LUID. A thread de captura usa DPI por monitor (PMv2) só nela, para não mudar o que o código da Parte 2 lê.
7. **Pixels 1:1 para o texto ficar nítido.**
   - O HELLO passa a mandar o tamanho real do painel: `WindowManager.maximumWindowMetrics` no API 30+ e `Display.getRealMetrics` no API 29.
   - A `SurfaceView` cobre o recorte da câmera (`LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES`) e usa `holder.setFixedSize(w, h)`.
8. **Conversão para NV12 na GPU** com `ID3D11VideoProcessor` (BGRA full range → NV12 BT.709 limitado), com o processamento automático desligado para não mexer no texto. As amostras de entrada vêm de um pool (`MFCreateVideoSampleAllocatorEx`). O encoder é um **MFT assíncrono**, com uma thread própria de eventos.
9. **SPS, PPS e VPS do `CONFIG`** são tirados do primeiro IDR (`AnnexB.ExtractParameterSets`). Não confiar em `MF_MT_MPEG_SEQUENCE_HEADER`, que costuma vir vazio em encoders de hardware.
10. **GOP é contado em quadros, não em segundos.** `CODECAPI_AVEncMPVGOPSize` = 2 × fps. Como os quadros só vêm com mudança, o intervalo real entre IDRs varia; o spike registra o tamanho dos IDRs.

## Componentes — PC

### `ScreenShare.Core` (puro, testável)
- `Video/AnnexB`: divide um access unit em NALs (start codes de 3 e 4 bytes), dá o tipo do NAL, `IsKeyframe` (H.264: 5; H.265: 19, 20, 21) e `ExtractParameterSets` (H.265: VPS 32, SPS 33, PPS 34; H.264: SPS 7, PPS 8).
- `Video/PcClock`: relógio do PC em µs a partir do QPC (`Stopwatch.GetTimestamp`), o mesmo usado em `FRAME.timestampUs` e no `PING` do PC.
- `Protocol/VideoSendQueue` (com trava):
  - um `CONFIG` descarta os quadros pendentes do stream antigo e passa a esperar keyframe;
  - mais de 2 quadros pendentes → descarta os não-keyframe até um IDR e avisa que precisa de keyframe;
  - um keyframe que chega descarta os não-keyframe que estavam antes dele.
- `Protocol/SessionWriter`: o único que escreve no stream. `Send(controle)`, `SendPing()` (o valor é carimbado na hora de escrever), `SendVideoConfig`, `SendVideoFrame`, `PendingFrames`, `ConfigSent`, evento `KeyframeNeeded`, `RunAsync(ct)`. Um `IOException` encerra a sessão.
- `MessageCodec.WriteFrameHeader(Span<byte>, ts, isKeyframe, dataLength)`: escreve o cabeçalho de 14 bytes para o `FRAME` sair sem copiar os dados.

### `host/ScreenShare.Video` (projeto novo, `net10.0-windows`)
- **Entrada pública:**
  - `IVideoSource.Start(VideoRequest, IVideoOutput) → IVideoStream?` (null = sem vídeo);
  - `VideoRequest(IMonitorSource Monitor, VideoCodec PhoneCodecs, VideoOptions Options)`;
  - `IVideoOutput` (`OnConfig`, `OnFrame`, `PendingFrames`);
  - `IVideoStream : IAsyncDisposable` (`RequestKeyframe()`, `Stats`);
  - `VideoSourceFactory.CreateDefault(...)`.
- **`Pipeline/`** (puro, testado com falsos e `FakeTimeProvider`):
  - **`VideoPipeline`**, com um `Step()` que os testes chamam:
    1. abre a captura com espera crescente (100 ms a 2 s); se a saída não for achada, chama `IMonitorSource.Refresh()`;
    2. recria o encoder quando o tamanho da captura muda (stream novo: IDR, `CONFIG`, quadros);
    3. só segue se o ritmo de fps permitir, o encoder aceitar entrada e o escritor não tiver quadro pendente;
    4. faz `TryAcquire` e codifica a imagem nova, ou, sem imagem nova, recodifica a última se houver pedido de keyframe ou refinamento.
  - **`FramePacer`**: ritmo de até `fps` quadros por segundo.
  - **`KeyframeScheduler`**: junta os pedidos (celular, descarte no escritor, stream novo), com no mínimo 200 ms entre IDRs. Um pedido só é adiado, nunca perdido.
  - **Escolha de codec:** H.265 se o celular e o host tiverem; senão H.264; senão sem vídeo. `--codec` força. Se o H.265 falhar ao criar, ele fica indisponível nesta execução e o pipeline usa H.264.
- **Interfaces de hardware:**
  - `IVideoBackend` (device D3D11, `IMFDXGIDeviceManager`, conversor NV12; `HardwareCodecs`, `OpenCapture(deviceName)`, `CreateEncoder(EncoderSettings)`);
  - `IScreenCapture` (`Width`, `Height`, `TryAcquire(timeout)` → `NewImage(ts)`, `PointerOnly` ou `Timeout`; `CaptureLostException`; `Last`);
  - `IVideoEncoder` (`CanAccept`, `Submit(img, ts, forceKeyframe)`, eventos `Output` e `Failed`).
- **Implementações de hardware:**
  - `GpuContext`: device D3D11 com `BgraSupport | VideoSupport`, multithread protegido.
  - `DxgiOutputLocator`: factory nova a cada reabertura; casa `OutputDescription.DeviceName`.
  - `DesktopDuplicationCapture`:
    - `IDXGIOutput5.DuplicateOutput1` em BGRA, com `DuplicateOutput` como alternativa;
    - copia o quadro para uma textura própria e libera o quadro na hora;
    - trata ACCESS_LOST, `E_ACCESSDENIED` (área de trabalho segura) e saída sumida.
  - `Nv12Converter`.
  - `MediaFoundationEncoder`:
    - `MFTEnumEx` de hardware, filtrado pelo LUID ou pelo fornecedor;
    - `MF_TRANSFORM_ASYNC_UNLOCK` e `MF_LOW_LATENCY`;
    - `CODECAPI_AVLowLatencyMode`, B-frames = 0, GOP = 2 × fps;
    - modo de bitrate `PeakConstrainedVBR` ou `LowDelayVBR` (o spike escolhe), com `MaxQP` se suportado;
    - tipo de saída antes do de entrada; VUI BT.709 limitado;
    - IDR forçado com `CODECAPI_AVEncVideoForceKeyFrame`;
    - saída Annex-B; keyframe pelo `CleanPoint`, conferido pelos tipos de NAL.
  - `CodecApi`: interop de `ICodecAPI` por vtable, com um `VARIANT` blittable.
  - `EncoderCatalog`: lista os encoders e decide quais codecs o host oferece.
  - `CursorCompositor`: lê o formato do ponteiro com `GetFramePointerShape` (monocromático, colorido e com máscara), converte num conversor puro e testado, e desenha numa cópia da imagem. Mexer só o mouse gera quadro novo, respeitando o limite de fps.
  - **Etapa B:** `SoftwareH264Encoder` (MFT H.264 da Microsoft, síncrono, com o NV12 copiado para a CPU).
- **Threads:**
  - captura: thread dedicada, prioridade acima do normal, DPI PMv2, espera de alta resolução, acordada por evento;
  - eventos do MFT: uma por encoder;
  - sessão: no pool de threads (leitura, escritor, `PING`).
- **Controle de fluxo:** o buffer de envio do socket fica explícito (começa em 512 KiB; o spike ajusta), para o buffer do kernel não esconder o congestionamento.

### Display (Parte 2)
- O `VirtualMonitorLease` ganha `Current` (atualizado pelo gerenciador), o evento `Changed` e `Refresh()`. `Refresh()` relê a saída sob a trava do gerenciador e devolve o nome `\\.\DISPLAYn` atual.
- O gerenciador guarda os leases ativos, atualiza-os quando o driver reinicia ou a resolução exata é aplicada, e dispara `Changed` **fora** da trava.
- `FixedMonitorSource` atende a opção de depuração `--capturar principal` (espelha o monitor principal, sem VDD).

### DevHost
- **`HostServer.ServeSessionAsync`:**
  - cria o `SessionWriter`, um `PING` a cada 1 s e `video.Start(...)`;
  - sem vídeo, ou sem `CONFIG` em 2 s → `CONFIG` de fallback (tamanho do lease, codec escolhido, `codecConfig` vazio);
  - laço de leitura: `PING` → `PONG`; `KEYFRAME_REQ` → `RequestKeyframe()`; `PONG` → RTT no log;
  - `KeyframeNeeded` do escritor → `RequestKeyframe()`;
  - encerramento: vídeo, escritor, lease.
- **`ExclusiveVideoSource`:** `Start` encerra o stream da sessão anterior antes de começar o novo.
- **Construtor:** `HostServer` ganha `IVideoSource? video = null`, para os testes atuais não mudarem.
- **Opções:**
  - `--fps N` (padrão 60, 1–120);
  - `--bitrate Mbps`;
  - `--codec h264|h265|auto`;
  - `--encoder hardware|software|auto` (etapa B);
  - `--sem-video`;
  - `--capturar principal`;
  - `--gravar arquivo` (dump Annex-B para conferir com ffplay).
- **Estatísticas no console** a cada 5 s: fps, Mbps, quadros descartados, IDRs, p95 do encode.

## Componentes — celular

- **`Connection`:**
  - `PING` do PC → `PONG` com o mesmo valor, na hora, e `ClockSync.onPcPing`;
  - `CONFIG` (o do handshake e os do meio da sessão) → atualiza o estado `Connected` e vai para o `VideoSink`;
  - `FRAME` → `VideoSink`, sem bloquear;
  - `PONG` → RTT e `ClockSync.onRtt`;
  - `requestKeyframe()` envia `KEYFRAME_REQ` pelo `Dispatchers.IO`;
  - depois do `CONFIG`, `soTimeout` = 5 s (o PC pinga a cada 1 s).
- **HELLO:** o tamanho real do painel em paisagem (achado 7) e os codecs que o aparelho decodifica de fato no tamanho pedido (`CodecSupport`).
- **Lógica pura, testada na JVM** (`dev.screenshare.android.video`):
  - `AnnexB`, com os mesmos vetores do C#;
  - `CsdBuilder`: H.264 → `csd-0` = SPS e `csd-1` = PPS; H.265 → `csd-0` = VPS+SPS+PPS; a partir do `CONFIG` ou, se ele vier vazio, do primeiro keyframe;
  - `ClockSync`: deslocamento = mínimo em 20 s de (recebido no celular − valor do PING do PC) − RTT mínimo/2;
  - `VideoStats` e `CodecSupport`;
  - **`DecoderCore`**, uma máquina de estados de uma thread só, que fala com uma porta `CodecPort` (`create`, `inputCapacity`, `queue`, `render`, `release`):
    - configura só quando há `CONFIG` e superfície;
    - depois de (re)configurar, descarta não-keyframes até um keyframe;
    - `CONFIG` igual não faz nada; diferente recria o decoder;
    - mais de 3 quadros esperando entrada → descarta os que esperam, espera keyframe e manda KEYFRAME_REQ (no máximo 1 a cada 500 ms);
    - quadro maior que o buffer de entrada → descarta e pede keyframe;
    - superfície perdida → libera o decoder; superfície de volta → cria e pede keyframe;
    - erro do decoder → recria e pede keyframe;
    - callbacks de uma geração antiga são ignorados.
- **Adaptadores Android** (verificação manual):
  - `MediaCodecPort`:
    - modo assíncrono numa `HandlerThread` com `THREAD_PRIORITY_URGENT_DISPLAY`;
    - `KEY_LOW_LATENCY` (API 30+, se `FEATURE_LowLatency`), `KEY_PRIORITY` = 0 e as chaves de baixa latência do fabricante (`vendor.qti-ext-dec-low-latency.enable`, `vendor.rtc-ext-dec-low-latency.enable`, conforme o decoder);
    - `KEY_MAX_INPUT_SIZE` = max(1 MiB, w·h);
    - `releaseOutputBuffer(i, System.nanoTime())` e `setOnFrameRenderedListener` para as estatísticas;
    - decoder de hardware escolhido pelo `MediaCodecList`.
  - `VideoPlayer`: no `ConnectionViewModel`, um por conexão, sobrevive a mudanças de configuração. `surfaceDestroyed` espera a liberação por até 500 ms.
  - **UI:** `AndroidView { SurfaceView }` com `setFixedSize(w, h)` e `aspectRatio(w/h)` sobre fundo preto. `ImmersiveWindowEffect` acrescenta `SHORT_EDGES`.
  - **Overlay:** atualizado 2×/s num composable próprio.
- **Etapa B:**
  - `Reconnector` (puro): backoff 1, 2, 4, 8, 10, 10 s…, cancelável, sem tentar depois de `DENIED`; reconecta pelo mesmo caminho (Wi-Fi ou cabo);
  - UI "Reconectando…" sobre o último quadro, com o botão Cancelar;
  - uma mudança do tamanho da tela do celular (dobrar ou desdobrar) reconecta com um HELLO novo.

## Fluxo de uma sessão

1. HELLO → lease do monitor (Parte 2) → `video.Start`.
2. O pipeline abre a captura e o encoder. A primeira saída é um IDR: o PC extrai SPS/PPS/VPS, envia o `CONFIG` e, em seguida, o `FRAME` do IDR. Sem vídeo em 2 s, sai o `CONFIG` de fallback.
3. A tela muda → captura → NV12 → encode → `FRAME`, no máximo `fps` por segundo e só com o escritor livre.
4. A tela para → refinamentos em +100, +300 e +700 ms.
5. KEYFRAME_REQ, descarte no escritor ou decoder recriado → IDR da última imagem, mesmo com a tela parada.
6. O monitor muda de tamanho (driver reiniciado com a resolução exata) → `Changed` → captura reaberta → encoder novo → `CONFIG` novo + IDR. No celular, `CONFIG` diferente → decoder recriado.
7. UAC, Win+L ou troca de modo → ACCESS_LOST → duplicação recriada, mesmo encoder (mesmo tamanho = mesmo stream).
8. Sessão termina → vídeo encerrado, escritor parado, lease liberado (o monitor sai em 10 s, como na Parte 2).

## Protocolo (`docs/protocol.md`, continua v2)

- **PING do PC:** depois do `CONFIG`, o PC manda um `PING` a cada 1 s. O valor é o relógio do PC em µs, monotônico, o mesmo do `FRAME.timestampUs`. O celular **deve** responder cada `PING` com um `PONG` de mesmo valor, sem demora.
- **CONFIG:**
  - o primeiro pode chegar até cerca de 2 s depois do `HELLO`;
  - pode ser reenviado no meio da sessão (mudança de resolução, encoder reiniciado, troca de codec);
  - **depois de qualquer `CONFIG`, o próximo `FRAME` é keyframe**, e nenhum quadro do stream anterior é enviado depois dele;
  - `CONFIG` com o mesmo conteúdo permite manter o decoder;
  - um `codecConfig` não vazio contém só os parâmetros (start codes de 4 bytes), que também se repetem dentro de cada keyframe.
- **FRAME:**
  - 1 access unit por mensagem;
  - keyframe = IDR precedido dos parâmetros;
  - sem B-frames (ordem de decodificação = ordem de exibição);
  - timestamps estritamente crescentes dentro de um stream;
  - AUD e SEI podem aparecer.
- **Fluxo de vídeo:**
  - quadros só quando a tela muda, mais alguns refinamentos; não receber quadros é normal;
  - com congestionamento o PC pode pular quadros, mas o próximo que chega é keyframe;
  - KEYFRAME_REQ é atendido o quanto antes, mesmo com a tela parada, e pedidos próximos são juntados.
- **Vetores novos:** `docs/protocol-vectors/annexb/h264-idr.hex` e `h265-idr.hex`, IDRs reais pequenos capturados no spike, usados pelos testes C# e Kotlin.

## Erros

| Situação | Comportamento |
|---|---|
| Sem encoder de hardware para nenhum codec que o celular aceita | Etapa A: sem vídeo, com aviso no console e `CONFIG` de fallback. Etapa B: encoder H.264 em software |
| H.265 falha ao criar | Indisponível nesta execução do host; usa H.264 |
| Encoder falha no meio | Recria; stream novo (`CONFIG` + IDR) |
| ACCESS_LOST (UAC, Win+L, troca de modo) | Recria a duplicação; mesmo encoder |
| `E_ACCESSDENIED` (área de trabalho segura), saída sumida | Tenta de novo com espera crescente e relê a saída (`Refresh`) |
| `DEVICE_REMOVED` / `RESET` | Recria o backend inteiro |
| Escritor com mais de 2 quadros pendentes | Descarta não-keyframes até um IDR e pede IDR |
| Celular: erro do decoder, quadro grande demais, fila cheia | Recria ou descarta e manda KEYFRAME_REQ (no máximo 1 a cada 500 ms) |
| Segunda sessão pede vídeo | A nova assume; a antiga fica sem vídeo |
| Celular: conexão cai | Etapa A: volta para a lista com a mensagem. Etapa B: reconexão automática |

## Spike (primeira tarefa, código descartável)

O controlador executa no PC e no celular do usuário. Os resultados entram neste documento, numa seção "Resultados do spike", e as tarefas de captura e encoder podem ser reescritas a partir deles.

| # | Pergunta | Plano B |
|---|---|---|
| S1 | Em qual adaptador (e LUID) aparece a saída do VDD? `DesktopImageInSystemMemory`? | Fixar a GPU de renderização no `vdd_settings.xml` |
| S2 | `DuplicateOutput1` funciona na saída do VDD (PMv2 só na thread)? O que acontece com UAC, Win+L, troca de modo e reinício do driver (nome novo)? | Windows.Graphics.Capture (S3) |
| S3 | (Só se S2 falhar) WGC na saída do VDD: a borda amarela pode ser desligada num app sem pacote? O cursor vem junto? | Pacote esparso para a permissão de captura sem borda |
| S4 | Quais MFTs de hardware existem por LUID? Quais tipos de entrada aceitam? `ICodecAPI.IsSupported` de cada ajuste? | Ajustar a lista de configurações |
| S5 | VideoProcessor → allocator → MFT assíncrono funciona? Saída Annex-B com parâmetros em cada IDR? `CleanPoint`? O IDR forçado vale para o mesmo quadro? A largura 2520 funciona? | Ajustar o encoder |
| S6 | Latência p50/p95 da captura até a entrada e da entrada até a saída; tamanho do IDR, de um quadro de rolagem e de um refinamento, em 3 modos de bitrate, a 25 e 50 Mbps | Escolher o modo e os números padrão |
| S7 | Celular: `wm size`, `displayMetrics` vs `maximumWindowMetrics`, decoders H.264/H.265 e `FEATURE_LowLatency` | Ajustar o HELLO e o decoder |

## Testes

**Automáticos.** Host: `dotnet test host/ScreenShare.slnx`, no CI `windows-latest` sem GPU. Celular: `gradlew :app:testDebugUnitTest`, no CI ubuntu, JUnit puro.

| Alvo | Casos |
|---|---|
| `AnnexB` (C# e Kotlin) | Vetores reais; start codes de 3 e 4 bytes; bytes de emulação; AUD/SEI; PPS ausente |
| `VideoSendQueue` / `SessionWriter` | Cabeçalho igual ao `frame.hex`; controle antes do vídeo; `PING` carimbado na hora de escrever; descarte até IDR + `KeyframeNeeded`; `CONFIG` descarta quadros antigos; IDR substitui P-frames na fila; stream que falha com escritas simultâneas sob 1 000 quadros + PONGs; `IOException` encerra o escritor |
| `HostServer` | Testes atuais verdes; `PING` do PC a cada ~1 s; vídeo falso → `CONFIG` e quadros na ordem; KEYFRAME_REQ repassado; fallback em 2 s; cliente que para de ler → descarte + pedido de keyframe; vídeo encerrado antes do lease; sessão nova assume o vídeo |
| Lease (Parte 2) | Reinício do driver (DISPLAY5→6) dispara `Changed` com o modo exato; `Refresh` acha o nome novo; sem deadlock se o handler chama o gerenciador |
| `VideoPipeline` | Primeira saída IDR, depois `CONFIG`; ritmo a 60 e 30 fps; espera escritor livre e encoder com entrada; tela parada + pedido → recodifica a última; refinamentos; ACCESS_LOST mantém o encoder; tamanho novo → encoder + `CONFIG` + IDR; `Changed` reabre; H.265 falha → H.264; timestamps crescentes; primeira saída não-IDR descartada |
| Encoder / captura (só com `SCREENSHARE_GPU_TESTS=1`) | Duplicar o monitor principal: quadro em 1 s e tamanho certo. 30 quadros sintéticos em H.264 e H.265: IDR com parâmetros primeiro, IDR forçado no quadro 10, p95 da entrada à saída < 10 ms |
| Conversor do ponteiro | Formatos monocromático, colorido e com máscara |
| Celular: `Connection` | `PONG` ecoa o `PING` do PC; `CONFIG` novo atualiza o estado; ordem dos quadros; bytes do KEYFRAME_REQ |
| Celular: `ClockSync`, `DecoderCore`, `CsdBuilder`, `Reconnector` | Jitter, fila assimétrica e deriva; todas as regras do `DecoderCore`, incluindo gerações antigas; csd por codec; backoff, cancelamento e `DENIED` |

**Manuais, no PC do usuário com o celular** (fim de cada etapa):
1. Imagem nítida e 1:1; arrastar uma janela para o monitor do celular com o cursor visível.
2. Latência < 50 ms no USB e < 80 ms no Wi-Fi, conferida filmando as duas telas com um cronômetro em ms; o overlay deve bater com o filme.
3. Primeira vez de uma resolução (UAC) → `CONFIG` no meio da sessão sem reconectar.
4. Win+L e UAC: o vídeo volta sozinho.
5. Sessão nova pelo USB assume a do Wi-Fi.
6. `--fps 30`.
7. Tela parada sem borrão; uma travada do Wi-Fi recupera sozinha.
8. App em segundo plano e de volta.
9. Etapa B: `--encoder software`; queda da conexão → "Reconectando…" e volta sozinha; dobrar ou desdobrar o celular reconecta.

## Etapas e entrega

- **Etapa A** (vídeo funcionando, PR próprio):
  1. spike;
  2. protocolo + `AnnexB` + `PcClock`;
  3. fila e escritor;
  4. `HostServer`;
  5. lease com `Changed`;
  6. pipeline;
  7. captura;
  8. encoder;
  9. opções do DevHost;
  10. `Connection` + `ClockSync`;
  11. lógica pura do decoder;
  12. MediaCodec + UI + overlay;
  13. cursor;
  14. verificação ponta a ponta + documentação.
- **Etapa B** (robustez extra, PR próprio):
  - encoder em software;
  - reconexão automática e reconexão ao mudar a tela;
  - verificação manual + documentação.

## Fora do escopo

- Toque (Parte 5).
- Bandeja, tela de configurações e controle adaptativo de bitrate (Parte 6).
- Prioridade de GPU sob carga de jogos, modos acima de 60 Hz, HDR, cor 4:4:4, áudio e vários celulares.
- Serviço em primeiro plano no Android para manter a conexão com a tela apagada.

## Resultados do spike (2026-10-04, PC: Windows 11 26200, RTX 5060 Ti; celular SM-F976B, Android API 37)

| # | Experimento | Resultado |
|---|---|---|
| E1 | Saídas DXGI com o monitor virtual ligado | A saída do VDD (`\.\DISPLAY10`, 2520×1080 em (3440,0)) aparece no **adaptador 0, a própria RTX 5060 Ti** (LUID 0001D004), o mesmo do monitor principal e do encoder: captura e encode na mesma GPU, sem cópia entre adaptadores. Existe um segundo adaptador com o mesmo nome (LUID D18781F5) **sem saídas**: é o adaptador IddCx do VDD. Não é preciso fixar a GPU no `vdd_settings.xml`. |
| E2 | `DuplicateOutput1` na saída do VDD | ✅ Funciona com DPI por monitor **só na thread** (no processo inteiro também, sem diferença). BGRA, rotação identidade, `DesktopImageInSystemMemory = false`. O quadro chega ~0,6 ms depois do present. |
| E3 | Win+L, UAC e troca de resolução durante a captura | Win+L/UAC: `ACCESS_LOST`, depois `E_ACCESSDENIED` ao reabrir enquanto a área de trabalho segura está aberta, e a reabertura volta a funcionar sozinha quando ela fecha. Troca de resolução: `ACCESS_LOST` → reabre em 1920×1080 → `ACCESS_LOST` no "Reverter" → reabre em 2520×1080. **Tentar de novo com espera** basta. |
| E4 | Encoders de hardware | "NVIDIA HEVC Encoder MFT" e "NVIDIA H.264 Encoder MFT" (`VEN_10DE`), **assíncronos** e `D3D11_AWARE`. Entradas: NV12 (e outras). `ICodecAPI.IsSupported`: LowLatency, GOP, RateControl, MeanBitRate, MaxBitRate, BufferSize, Quality, QualityVsSpeed, MaxQP, MinQP e ForceKeyFrame **sim**; `AVEncMPVDefaultBPictureCount` **não** (`E_INVALIDARG` ao definir) — não há B-frames em baixa latência. |
| E5 | Encode sintético 2520×1080 a 60 fps (texto denso rolando) | Entrada→saída p50 ≈ 3,0 ms, p95 ≈ 6 ms; **1 saída por entrada** (não guarda quadros). Annex-B com start codes de 4 bytes; keyframe = AUD + VPS/SPS/PPS + IDR (H.265 tipo 19; H.264 AUD + SPS + PPS + IDR 5); P = AUD + slice. **O IDR forçado sai no mesmo quadro.** A largura 2520 funciona. ffprobe: `yuv420p`, BT.709, faixa limitada, todos os quadros decodificam. `PeakConstrainedVBR` e `LowDelayVBR` dão o mesmo resultado. |
| E6 | Captura + encode da área de trabalho real (rolagem de uma página web) a 50 Mbps | Present→saída codificada p50 **3,5 ms**, p95 **7 ms**. P-frames da rolagem: p50 35 KB, máx. 168 KB. **IDRs periódicos de 0,6 a 1 MB** (a cada 120 quadros). Texto nítido nos quadros extraídos. |
| E7 | Vetores | `annexb/h264-idr.hex` (2 512 bytes) e `annexb/h265-idr.hex` (6 067 bytes), 256×144. |
| E8 | Celular | Dobrável com duas telas: externa **1080×2520** (a ativa no teste; recorte da câmera de 108 px no topo) e interna **2256×2504**. O HELLO de hoje já mandava 2520×1080, o tamanho real da tela externa. Densidade 480 (override 432). Decoders: `c2.qti.hevc.decoder.low_latency` e `c2.qti.avc.decoder.low_latency` (com `Feature low-latency`), além dos normais. |

### Decisões a partir do spike

- **Captura:** DXGI Desktop Duplication (o plano B, WGC, não é necessário). DPI por monitor só na thread de captura. `ACCESS_LOST` e `E_ACCESSDENIED` → reabrir com espera crescente; tamanho novo na reabertura → encoder novo + `CONFIG` + IDR.
- **Device:** criado no adaptador da saída achada pelo `DeviceName`; o encoder é escolhido entre os MFTs de hardware do mesmo fornecedor (aqui, um só por codec).
- **Encoder:** MFT assíncrono, NV12 vindo do `ID3D11VideoProcessor`, `PeakConstrainedVBR`. B-frames não são configurados (não suportado; já são 0). `MaxQP` disponível.
- **Sem IDR periódico:** com 0,6–1 MB por IDR a cada 2 s, o GOP fixo daria um tranco de dezenas de ms a cada 2 s com a tela em movimento. Como o TCP não perde dados, keyframes só saem no começo do stream, num `KEYFRAME_REQ`, depois de um descarte na fila ou numa reabertura. O GOP é configurado no maior valor aceito (a Task 8 confirma que nenhum IDR aparece sozinho em 600 quadros).
- **Bitrate padrão:** USB 50 Mbps (pico 100); Wi-Fi 25 Mbps (pico 50). Na rolagem real, a média ficou bem abaixo do teto (~17 Mbps a 60 fps).
- **Celular:** o HELLO manda o tamanho real da tela em uso (o cálculo atual já dá 1:1 na tela externa; a Task 10 passa a usar `maximumWindowMetrics`, que cobre a interna também). Decoder: preferir o nome terminado em `.low_latency` do codec; senão, o primeiro de hardware com `FEATURE_LowLatency`; senão, o primeiro de hardware.
