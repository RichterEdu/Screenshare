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

- [x] **Parte 1 — Fundação**: solução .NET e protocolo v1 (`HELLO`, `PING`, `PONG`, `KEYFRAME_REQ`) com testes
- [x] Parte 1 — mensagens `CONFIG`, `FRAME`, `TOUCH` e lado Kotlin (`android/`) com os mesmos vetores
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

App Android (usa o JDK do Android Studio):

```powershell
$env:JAVA_HOME = 'C:\Program Files\Android\Android Studio\jbr'
.\android\gradlew.bat -p android :app:testDebugUnitTest
```

## 📚 Documentação

| Documento | Conteúdo |
|---|---|
| [`docs/protocol.md`](docs/protocol.md) | Especificação do protocolo v1 |
| [`docs/guia-do-codigo.md`](docs/guia-do-codigo.md) | Guia do código para quem não conhece C# |
| [`docs/superpowers/specs`](docs/superpowers/specs) | Spec de design do projeto |
| [`docs/superpowers/plans`](docs/superpowers/plans) | Planos de implementação |

## 🚫 Fora do escopo (por enquanto)

Modo internet (WebRTC/relay), iOS, macOS/Linux, driver próprio assinado, cor 4:4:4, áudio e múltiplos celulares.

## 📄 Licença

[MIT](LICENSE) © 2026 RichterEdu
