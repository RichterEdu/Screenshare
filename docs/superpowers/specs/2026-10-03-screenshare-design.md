# ScreenShare — celular Android como segunda tela real do PC Windows

## Contexto
Apps existentes de "segunda tela" exigem configurações chatas ou não oferecem conexão por cabo. O objetivo é um app pessoal em que o **Windows reconhece o celular como um monitor de verdade** (estender área de trabalho, arrastar janelas), com conexão por **Wi-Fi local** ou **cabo USB (ADB)** para menor latência, e **toque multitouch nativo** (o celular age como monitor touchscreen). Uso principal: produtividade (texto nítido > fps). Modo "qualquer rede" fica fora do escopo por enquanto.

Pasta do projeto está vazia (projeto novo). Uso pessoal → sem assinatura própria de driver.

## Decisões
- PC: Windows 10/11, **C# .NET 10**, app de bandeja. Android: **Kotlin**, app nativo.
- Monitor virtual: **Virtual Display Driver** open source já assinado (github.com/VirtualDrivers/Virtual-Display-Driver) — instalado uma vez; resolução configurada via seu XML de config.
- Captura: **DXGI Desktop Duplication** só da saída do monitor virtual (`Vortice.Windows`).
- Encode: **H.265 (fallback H.264)** em hardware via Media Foundation (NVENC/QSV/AMF), low-latency, sem B-frames, GOP ~2 s, bitrate alto (LAN/USB). Fallback para MFT de software.
- Transporte: **TCP único** com `TCP_NODELAY`. Wi-Fi: descoberta mDNS (`Makaretu.Dns.Multicast` / Android NSD). USB: `adb reverse tcp:PORT tcp:PORT` (adb.exe empacotado), celular conecta em `127.0.0.1`.
- Toque: Android `MotionEvent` → pacote `TOUCH` normalizado → PC `InjectSyntheticPointerInput(PT_TOUCH)` mapeado para os bounds do monitor virtual.

## Estrutura
```
host/   ScreenShare.sln
  ScreenShare.Core/   Protocol (mensagens, framing), interfaces ICaptureSource, IVideoEncoder, ITransport, ITouchInjector
  ScreenShare.Host/   Tray app: DisplayManager, CaptureService, EncoderService, TcpTransport, Discovery, UsbBridge, TouchInjector
  ScreenShare.Tests/  xUnit
android/  app/ — ConnectionManager, Discovery(NSD), VideoDecoder(MediaCodec→SurfaceView), TouchCapture, UI (lista de PCs + tela imersiva)
docs/   protocol.md, protocol-vectors/ (vetores binários compartilhados C#/Kotlin), superpowers/specs/2026-10-03-screenshare-design.md
```

## Protocolo
`[tipo:u8][tamanho:u32 LE][payload]`
- `HELLO` (A→PC): versão, largura/altura/DPI, codecs suportados
- `CONFIG` (PC→A): resolução, codec, bitrate, SPS/PPS(VPS)
- `FRAME` (PC→A): timestamp µs, flag keyframe, NAL units
- `TOUCH` (A→PC): N ponteiros `{id:u8, ação:u8, x:f32, y:f32 (0–1), pressão:f32}`
- `PING`/`PONG`: latência (overlay debug) · `KEYFRAME_REQ` (A→PC)

## Fluxo
1. Host escuta; celular conecta (Wi-Fi via mDNS ou USB via 127.0.0.1) e envia `HELLO`.
2. DisplayManager ativa monitor virtual na resolução do celular → `CONFIG`.
3. Loop vídeo: DXGI (frame só quando muda) → encoder GPU (texture direto, sem cópia CPU) → socket. Fila > 2 frames ⇒ descarta não-keyframes.
4. Loop toque: `TOUCH` → pixels do monitor virtual → injeção.
5. Desconexão: monitor mantido ~10 s (reconexão), depois desativado.

## Erros
- Reconexão automática com backoff no Android.
- `DXGI_ERROR_ACCESS_LOST` (UAC, lock, troca de resolução) → recria duplicação.
- Sem HW encoder → MFT software + aviso.
- ADB sem dispositivo/não autorizado → aviso na bandeja com instruções.
- Erro de decode no Android → `KEYFRAME_REQ`.

## Fases de implementação (cada uma entregável e testável)
1. **Fundação**: repo git, soluções .NET e Gradle, `ScreenShare.Core` com protocolo + testes de serialização (C# e Kotlin com os mesmos vetores).
2. **Monitor virtual**: instalar VDD, `DisplayManager` que ajusta resolução e localiza a saída DXGI/bounds do monitor.
3. **Vídeo ponta a ponta no Wi-Fi**: captura → encode → TCP → MediaCodec na SurfaceView; descoberta mDNS; overlay de latência.
4. **USB**: `UsbBridge` detecta dispositivo via `adb devices`, aplica `adb reverse`; Android escolhe USB/Wi-Fi.
5. **Toque multitouch**: TouchCapture + TouchInjector com mapeamento de coordenadas.
6. **Robustez e UX**: reconexão, ACCESS_LOST, descarte de frames, fallback de encoder, bandeja, seleção de resolução/qualidade.

## Verificação
- `dotnet test` (protocolo, mapeamento de toque, descarte de frames) e `./gradlew test` (protocolo com vetores compartilhados).
- Integração PC: captura falsa → encoder → decoder MF em loopback, compara dimensões/frames.
- Manual E2E: Windows mostra "Display 2" em Configurações > Tela; arrastar janela para o celular; medir latência via PING (meta < 50 ms USB, < 80 ms Wi-Fi); pinça para zoom num navegador; desplugar cabo/derrubar Wi-Fi e verificar reconexão.

## Fora do escopo (futuro)
Modo internet (WebRTC/relay), iOS, macOS/Linux, driver próprio assinado, cor 4:4:4, áudio, múltiplos celulares.

