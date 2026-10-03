# App Android com conexão real — design

Complementa o [spec geral](2026-10-03-screenshare-design.md). Branch `android-app`.

## Objetivo
O protocolo v1 já existe em C# e Kotlin. O host real (monitor virtual, captura, encode) ainda não existe, então o app não tem vídeo.
Em vez de telas com dados falsos, este ciclo liga o app à **rede de verdade**: descobre o PC por mDNS, conecta por TCP, troca
`HELLO`/`CONFIG` e mede latência com `PING`/`PONG`. O host é um **stub** em C#. Valida mDNS, TCP e firewall no celular real.

## Escopo
- Dentro: descoberta, conexão, handshake, overlay de latência, entrada manual de IP, stub do host.
- Fora: vídeo/MediaCodec, toque, USB (`adb reverse`), tela de configurações, reconexão automática, tray app.

## Host stub (`host/ScreenShare.DevHost`)
- Console, referencia `ScreenShare.Core`. `TcpListener` na porta 38700, um cliente por vez.
- Anuncia `_screenshare._tcp` via mDNS (`Makaretu.Dns.Multicast`); nome do serviço = nome da máquina.
- `HELLO` com versão ≠ 1 → fecha a conexão. Senão responde `CONFIG` fixo (resolução do HELLO, H.264, codecConfig vazio) e não envia FRAMEs.
- `PING` → `PONG` com o mesmo valor.

## App Android (`dev.screenshare.android`)
- `net/HostDiscovery`: `NsdManager` → `Flow<List<DiscoveredHost>>` (nome, IP, porta).
- `net/Connection`: `Socket` com `TCP_NODELAY`; envia `HELLO` (tamanho/DPI reais), espera `CONFIG`, manda `PING` a cada 1 s e
  publica o RTT. Estados: `Connecting`, `Connected(config, rttMs)`, `Failed(motivo)`, `Disconnected`.
- `ConnectionViewModel`: junta descoberta + conexão para a UI.
- Compose: `HostListScreen` (PCs descobertos + IP manual) e `ImmersiveScreen` (tela cheia, placeholder, overlay de RTT, desconectar).
- Manifest: `INTERNET`, `ACCESS_NETWORK_STATE`.

## Testes
- C#: handshake do stub sobre loopback. Kotlin: `Connection` contra um `ServerSocket` local (sockets reais, sem mocks); ViewModel com `runTest`.
- Manual: `DevHost` no PC (liberar porta 38700 no Firewall), celular no mesmo Wi-Fi; PC aparece na lista, conecta, RTT real no overlay.

## Riscos
mDNS depende da rede/Firewall (por isso o IP manual); Gradle dentro do OneDrive pode travar arquivos.
