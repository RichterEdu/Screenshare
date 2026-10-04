<div align="center">

# 📱 ScreenShare

**Transforme o celular Android numa segunda tela de verdade para o seu PC Windows.**

![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![Kotlin](https://img.shields.io/badge/Android-Kotlin-7F52FF?logo=kotlin&logoColor=white)
![Plataforma](https://img.shields.io/badge/host-Windows%2010%2F11-0078D4?logo=windows&logoColor=white)
![Status](https://img.shields.io/badge/status-em%20desenvolvimento-orange)
![Licença](https://img.shields.io/badge/licença-MIT-green)

</div>

---

## ✨ O que é

Os apps de "segunda tela" existentes pedem configuração chata ou não aceitam cabo. O ScreenShare faz o Windows enxergar o celular como um **monitor de verdade**: estenda a área de trabalho, arraste janelas e use **toque multitouch nativo**.

- 🖥️ **Monitor virtual real**, reconhecido em Configurações › Tela
- 🔌 **Wi-Fi local** (descoberta automática) ou **cabo USB** via ADB para menor latência
- ✋ **Multitouch**: o celular age como um monitor touchscreen
- 🔤 **Texto nítido** em primeiro lugar: foco em produtividade, não em fps
- ⚡ Metas de latência: **< 50 ms** no USB e **< 80 ms** no Wi-Fi

## 🧩 Como funciona

```
 PC (host, C#)                                   Celular (Android, Kotlin)
┌───────────────────────────┐   TCP único        ┌──────────────────────────┐
│ Monitor virtual (VDD)     │  (Wi-Fi ou USB)    │ Decodifica H.265/H.264   │
│ Captura DXGI              │ ─── vídeo ───────▶ │ (MediaCodec → Surface)   │
│ Encode por hardware       │                    │                          │
│ Injeção de toque          │ ◀─── toques ────── │ Captura de MotionEvent   │
└───────────────────────────┘                    └──────────────────────────┘
```

| Etapa | Tecnologia |
|---|---|
| Monitor virtual | [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) (open source, já assinado) |
| Captura | DXGI Desktop Duplication (`Vortice.Windows`) |
| Encode | H.265 (fallback H.264) em hardware via Media Foundation |
| Transporte | TCP com `TCP_NODELAY`; mDNS no Wi-Fi, `adb reverse` no USB |
| Toque | `MotionEvent` → pacote `TOUCH` → `InjectSyntheticPointerInput` |

O formato dos bytes trocados entre os dois lados está em [`docs/protocol.md`](docs/protocol.md).

## 🗺️ Roadmap

- [x] **Parte 1 — Fundação**: protocolo v1 completo — as 7 mensagens (`HELLO`, `CONFIG`, `FRAME`, `TOUCH`, `PING`, `PONG`, `KEYFRAME_REQ`) e o `MessageReader` — em C# e em Kotlin (`android/`), testados contra os mesmos vetores
- [x] **App Android e host de desenvolvimento** — app Compose descobre o PC (mDNS) ou aceita IP, faz o handshake e mostra a latência real (PING/PONG); `ScreenShare.DevHost` é o stub do lado do PC (sem vídeo)
- [x] **Pareamento e autenticação** — QR + TLS com certificado fixo, no Wi-Fi e no cabo USB (protocolo v2)
- [ ] **Parte 2** — Monitor virtual (instalação do VDD e `DisplayManager`)
- [ ] **Parte 3** — Vídeo ponta a ponta no Wi-Fi + descoberta mDNS + overlay de latência
- [ ] **Parte 4** — Conexão por cabo USB (`adb reverse`)
- [ ] **Parte 5** — Toque multitouch
- [ ] **Parte 6** — Robustez e UX (reconexão, bandeja, qualidade)

## 🚀 Começando

Pré-requisitos: [.NET 10 SDK](https://dotnet.microsoft.com/download) (host) e [Android Studio](https://developer.android.com/studio) (app).

```bash
git clone https://github.com/RichterEdu/Screenshare.git
cd Screenshare/host
dotnet test
```

Para testar o app sem o host real, rode o host de desenvolvimento (libere a porta 38700 no Firewall do Windows se ele pedir):

```bash
dotnet run --project host/ScreenShare.DevHost
```

No console, digite `p` para mostrar o QR de pareamento e escaneie com o app (botão **Parear pelo Wi-Fi (QR)** ou **Parear pelo cabo USB (QR)**). `l` lista os celulares pareados e `r <id>` remove um. Dá para parear de duas formas: pelo Wi-Fi (botão **Parear pelo Wi-Fi (QR)**) ou pelo cabo (rode `adb reverse tcp:38701 tcp:38701` e use **Parear pelo cabo USB (QR)**, com o mesmo QR). Depois de pareado, **Conectar por cabo USB** usa o cabo (com o `adb reverse` ativo) e **Parear de novo (QR)** refaz o pareamento. Sem pareamento, o botão do cabo não aparece.

App Android (usa o JDK do Android Studio):

```powershell
$env:JAVA_HOME = 'C:\Program Files\Android\Android Studio\jbr'
.\android\gradlew.bat -p android :app:testDebugUnitTest
```

## 📚 Documentação

| Documento | Conteúdo |
|---|---|
| [`docs/protocol.md`](docs/protocol.md) | Especificação do protocolo v2 |
| [`docs/protocol-vectors`](docs/protocol-vectors) | Bytes de referência que os testes C# e Kotlin precisam reproduzir |
| [`docs/guia-do-codigo.md`](docs/guia-do-codigo.md) | Guia do código para quem não conhece C# |
| [`docs/superpowers/specs`](docs/superpowers/specs) | Spec de design do projeto |
| [`docs/superpowers/plans`](docs/superpowers/plans) | Planos de implementação |

## 🚫 Fora do escopo (por enquanto)

Modo internet (WebRTC/relay), iOS, macOS/Linux, driver próprio assinado, cor 4:4:4, áudio e múltiplos celulares.

## 🔒 Segurança

- **Wi-Fi:** o celular precisa ser **pareado** uma vez escaneando o QR que o PC mostra. Depois disso a conexão é criptografada (TLS) e o celular só aceita o certificado daquele PC; o PC só aceita celulares pareados e permite remover qualquer um.
- **Cabo USB:** exige o mesmo pareamento e o mesmo TLS do Wi-Fi. O `adb reverse` abre `127.0.0.1:38701` dentro do celular para qualquer app, e um app pode ocupar essa porta antes dele para se passar pelo PC; por isso a porta 38701 (que escuta só em `127.0.0.1` do PC) recusa conexão sem TLS e sem chave de um celular pareado, e o celular só aceita o certificado do PC pareado.

## 📄 Licença

[MIT](LICENSE) © 2026 RichterEdu
