# USB autenticado: a porta 38701 passa a usar o mesmo TLS + PAIR/AUTH do Wi-Fi

## Contexto
A revisão final do pareamento (PR #4) apontou que o `adb reverse tcp:38701 tcp:38701` abre `127.0.0.1:38701` **dentro do celular** para qualquer app com internet. Hoje essa porta aceita `HELLO` direto, sem TLS nem chave. Isso é inofensivo enquanto o DevHost é um stub, mas a partir da Parte 3 (vídeo) e da Parte 5 (toque) outro app do celular poderia ver a tela do PC e injetar toques.

Também há um segundo risco: um app malicioso pode ocupar a porta 38701 do celular **antes** do `adb reverse` e se passar pelo PC. Sem TLS, o ScreenShare entregaria a chave de acesso a ele, e essa chave valeria depois pelo Wi-Fi.

**Decisão do usuário:** o USB fica igual ao Wi-Fi. TLS com a digital fixa do PC, depois `PAIR` ou `AUTH`, depois `HELLO`. A porta 38701 continua escutando só em `127.0.0.1` do PC. O pareamento também passa a poder ser feito pelo cabo (o mesmo QR, conectando em `127.0.0.1:38701`). O custo de latência é desprezível: um handshake por conexão e depois AES-GCM por hardware.

## Mudanças

### PC (C#) — `host/ScreenShare.DevHost/HostServer.cs`
- `AcceptLoopAsync` deixa de ter caminho em texto puro: as duas portas chamam o fluxo seguro. Renomear `ServeWifiAsync` para `ServeSecureAsync`, que já faz TLS (1.2+) + `PAIR`/`AUTH` com prazo de handshake e depois `ServeSessionAsync`.
- O parâmetro `secure` some, e o log passa a mostrar só o rótulo da porta ("Wi-Fi"/"USB").
- `UsbEndPoint` continua em `IPAddress.Loopback`. Só a 38700 é anunciada no mDNS (nada muda no `Program.cs`, a não ser o texto de ajuda que menciona o USB, se houver).
- Testes (`host/ScreenShare.Tests/DevHost/HostServerTests.cs`):
  - `ConnectUsbAsync` passa a fazer TLS com a mesma validação por digital do `ConnectWifiAsync`. Extrair um helper `ConnectSecureAsync(int port)` usado pelos dois.
  - Testes de USB que mandavam `HELLO` direto passam a mandar `AUTH` com chave de um aparelho adicionado. São eles: `Usb_port_serves_hello_without_tls_or_pairing` (renomear para `Usb_port_requires_tls_and_auth_then_serves_hello`), `Old_app_hello_v1_on_usb...`, `Silent_client_after_config...` e `Server_accepts_a_new_usb_client...`.
  - Testes novos:
    - `Usb_port_rejects_plain_tcp_hello`: TCP puro na porta USB não tem sessão (null ou IOException), e a porta continua servindo.
    - `Usb_port_denies_hello_without_auth`: TLS + `HELLO` direto recebe `DENIED(2)`.
    - `Pairing_over_usb_works`: `PAIR` com segredo válido pela porta USB recebe `PAIRED`, e depois `AUTH` + `HELLO` recebe `CONFIG`.
  - `Usb_port_listens_only_on_loopback` permanece.

### Android (Kotlin)
- `net/Connection.kt`:
  - `ConnectTarget.Usb(val pc: PairedPc, val port: Int = USB_PORT)`: TLS com `pc.fingerprint` (usando o `upgradeToTls` existente) e depois `AUTH(pc.token)`. O branch `is ConnectTarget.Usb -> raw` sai.
  - `ConnectTarget.Pairing(info, deviceName, overUsb: Boolean = false)`: com `overUsb`, o endpoint é `127.0.0.1:USB_PORT`, com a mesma digital do QR. O `lastHost` salvo continua sendo `info.host`, o IP de LAN do QR.
- `net/ConnectionViewModel.kt`:
  - `connectUsb()` exige PC pareado. Sem pareamento, mostra "Pareie com o PC primeiro (pelo Wi-Fi ou pelo cabo)."
  - `pair(qrText, overUsb: Boolean = false)` repassa o modo.
- `ui/ScreenShareApp.kt` e `ui/HostListScreen.kt`:
  - Sem pareamento: botões **Parear pelo Wi-Fi (QR)** e **Parear pelo cabo USB (QR)**, e o texto do `adb reverse`.
  - Com pareamento: **Conectar por cabo USB** como hoje (agora autenticado), mais **Parear de novo (QR)**.
- Testes (`android/app/src/test/java/dev/screenshare/android/net/ConnectionTest.kt`):
  - `usb()` passa a usar `tlsServer` e o `PairedPc` de teste. Os testes de USB ganham a leitura do `AUTH` no lado servidor.
  - Testes novos:
    - `usbSendsAuthOverTlsThenHello`.
    - `usbWithDifferentCertificateFailsWithoutSendingTheToken` (servidor TLS com o `other.p12`).
    - `pairingOverUsbConnectsToLoopbackUsbPort`: com `overUsb`, conecta em `127.0.0.1` na porta do servidor de teste.
  - A porta de teste é injetada: `Usb(pc, port = tlsServer.localPort)`; para o pareamento por USB, adicionar `usbPort: Int = USB_PORT` ao `Pairing` ou um parâmetro de teste equivalente.

### Documentação
- `docs/protocol.md`:
  - Na tabela de portas, a 38701 passa a ser "TLS obrigatório, só `127.0.0.1`".
  - A sequência do USB passa a ser igual à do Wi-Fi.
  - Regra: na 38701 a primeira mensagem também tem de ser `PAIR` ou `AUTH`.
- `docs/superpowers/specs/2026-10-03-pareamento-autenticacao-design.md`: acrescentar uma seção "Revisão (USB autenticado)" com o motivo (túnel do adb aberto a qualquer app e risco de um app ocupar a porta antes do `adb reverse`) e a nova regra. A decisão antiga fica registrada como substituída.
- `README.md` (seção Segurança e Começando) e `docs/guia-do-codigo.md` (onde diz que o USB não tem TLS nem pareamento).

## Execução
- Branch nova `usb-autenticado` a partir de `origin/main`, numa worktree `.worktrees/usb-autenticado`.
- Execução por subagentes, como antes, com uma pausa ao fim de cada tarefa:
  1. PC (HostServer e testes).
  2. Android (Connection, ViewModel, telas e testes).
  3. Documentação.
  4. Revisão final, PR e, com o seu ok, merge.

## Verificação
- `dotnet test host`: tudo verde, 0 avisos, incluindo os testes novos de USB (TCP puro recusado, `HELLO` sem `AUTH` → `DENIED(2)`, pareamento pelo USB).
- `.\android\gradlew.bat -p android :app:testDebugUnitTest :app:assembleDebug`: tudo verde, incluindo USB com TLS + `AUTH` e certificado errado no USB sem enviar a chave.
- Manual no celular:
  1. Rodar `adb reverse tcp:38701 tcp:38701`. Com o app pareado, **Conectar por cabo USB** deve conectar.
  2. Com **Esquecer** + **Parear pelo cabo USB (QR)**, o pareamento deve funcionar sem Wi-Fi.
  3. Com o app sem pareamento, o USB deve pedir pareamento.
- CI do PR: `host-tests` e `android-tests` verdes.
