# Parte 1 — Fundação (protocolo) — Plano de Implementação

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

## Contexto
O spec aprovado (`docs/superpowers/specs/2026-10-03-screenshare-design.md`) divide o ScreenShare (celular Android como segunda tela real do PC Windows, via Wi-Fi local ou cabo USB/ADB, com toque multitouch) em 6 partes. O usuário pediu para **executar uma parte por vez**. Este plano detalha só a **Parte 1 — Fundação**: o protocolo binário que PC (C#) e celular (Kotlin) vão falar, implementado e testado nos dois lados contra os mesmos vetores de bytes. As demais partes ganham plano próprio quando chegarmos nelas.

Ambiente verificado: .NET SDK **10.0.401** (único instalado), Java do sistema **1.8** (velho demais para o Android), **sem** Android Studio/SDK/Gradle, GPU RTX 5060 Ti, repo git em `master` com 1 commit (o spec), pasta dentro do OneDrive.

**Goal:** Protocolo v1 (framing + 7 mensagens) implementado em C# (`ScreenShare.Core`) e Kotlin (app Android), ambos validados byte a byte contra `docs/protocol-vectors/*.hex`.

**Architecture:** Quadros `[type:u8][length:u32 LE][payload]` sobre TCP. Cada lado tem um `MessageCodec` (mensagem ↔ bytes, com validação estrita) e um `MessageReader` (remonta quadros de um stream). Os vetores `.hex` em `docs/` são a fonte da verdade e são lidos pelos testes das duas linguagens.

**Tech Stack:** C# / .NET 10 (`net10.0`), xUnit; Kotlin, Android Studio (Gradle Kotlin DSL), JUnit 4 — sem dependências de runtime de terceiros.

**Spec:** `docs/superpowers/specs/2026-10-03-screenshare-design.md`

## Roteiro das partes
| Parte | Entrega | Plano |
|---|---|---|
| **1 — Fundação** | protocolo C# + Kotlin com vetores compartilhados | **este** |
| 2 — Monitor virtual | VDD instalado + `DisplayManager` ativa/ajusta o monitor | depois |
| 3 — Vídeo no Wi-Fi | captura → encoder → TCP → MediaCodec, mDNS, overlay de latência | depois |
| 4 — USB | `UsbBridge` com `adb reverse` | depois |
| 5 — Toque | `TouchCapture` + `TouchInjector` | depois |
| 6 — Robustez/UX | reconexão, ACCESS_LOST, fallback, bandeja | depois |

Interfaces previstas no spec para o Core (`ICaptureSource`, `IVideoEncoder`, `ITransport`, `ITouchInjector`) entram nas partes que as usam (YAGNI).

## Global Constraints
- .NET: SDK 10, `net10.0` (o spec dizia .NET 8; a Tarefa 1 corrige para .NET 10 — único SDK instalado e LTS atual).
- Android: Kotlin, Kotlin DSL, package `dev.screenshare.android`, minSdk 29.
- Protocolo v1: tudo **little-endian**; cabeçalho de 5 bytes `[type:u8][length:u32]`; payload máx. **16 777 216** bytes (16 MiB); TOUCH com **1..10** ponteiros de 14 bytes; tipos 1..7 conforme `docs/protocol.md`.
- `docs/protocol-vectors/*.hex` é a fonte da verdade: C# e Kotlin precisam produzir e aceitar exatamente esses bytes.
- Erros: `ProtocolException` (herda de `IOException`) nos dois lados; stream acabando no meio de uma mensagem → `EndOfStreamException` (C#) / `EOFException` (Kotlin); stream acabando entre mensagens → `null`.
- Identificadores em inglês; comentários e mensagens de erro em pt-BR.
- Gradle no PowerShell: rodar `$env:JAVA_HOME = 'C:\Program Files\Android\Android Studio\jbr'` antes do `gradlew` (Java do sistema é 1.8).
- Commits terminam com `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`; trabalho na branch `parte-1-fundacao`.
- Risco conhecido: repo dentro do OneDrive. Se o Gradle falhar com arquivo bloqueado, pausar a sincronização do OneDrive (ou mover o repo para fora dele, ex.: `C:\dev\Screenshare`).

## Review Focus
1. **TCP entregando a mensagem em pedaços** (cabeçalho ou payload partidos) → o leitor remonta e devolve a mensagem inteira. Testado nas Tarefas 3 e 6 (stream que entrega 1 byte por leitura).
2. **Campo `length` corrompido ou hostil** (ex.: `FF FF FF FF`) → `ProtocolException` imediata, sem tentar alocar 4 GB. Tarefas 3 e 6.
3. **Conexão caindo no meio de uma mensagem** → `EndOfStreamException`/`EOFException`, distinguível de desconexão limpa (`null`). Tarefas 3 e 6.
4. **TOUCH com NaN/infinito, ação desconhecida, 0 ou mais de 10 ponteiros** → rejeitado na decodificação, nunca chega à injeção de toque. Tarefas 2 e 5.
5. **Codec config com mais de 65 535 bytes** → o encode lança erro em vez de truncar silenciosamente o tamanho u16. Tarefas 2 e 5.

---

### Tarefa 1: Solução .NET, documento do protocolo e mensagens de tamanho fixo (C#)

**Files:**
- Create: `.gitignore`, `.gitattributes`
- Create (templates): `host/ScreenShare.slnx`, `host/ScreenShare.Core/ScreenShare.Core.csproj`, `host/ScreenShare.Tests/ScreenShare.Tests.csproj`
- Modify: `host/ScreenShare.Tests/ScreenShare.Tests.csproj` (copiar vetores)
- Create: `docs/protocol.md`, `docs/protocol-vectors/{hello,config,frame,touch,ping,pong,keyframe_req}.hex`
- Create: `host/ScreenShare.Core/Protocol/{Messages,ProtocolException,PayloadReader,PayloadWriter,MessageCodec}.cs`
- Test: `host/ScreenShare.Tests/Protocol/Vectors.cs`, `host/ScreenShare.Tests/Protocol/MessageCodecTests.cs`
- Modify: `docs/superpowers/specs/2026-10-03-screenshare-design.md:9` (.NET 8 → .NET 10)

**Interfaces:**
- Consumes: nada.
- Produces: namespace `ScreenShare.Core.Protocol` com `enum MessageType : byte`, `[Flags] enum VideoCodec : byte { None, H264 = 1, H265 = 2 }`, `enum TouchAction : byte { Down, Move, Up, Cancel }`, `abstract record Message` e os records `HelloMessage(ushort ProtocolVersion, ushort Width, ushort Height, ushort DensityDpi, VideoCodec SupportedCodecs)`, `ConfigMessage(ushort Width, ushort Height, VideoCodec Codec, uint BitrateKbps, byte[] CodecConfig)`, `FrameMessage(ulong TimestampUs, bool IsKeyframe, byte[] Data)`, `TouchPointer(byte Id, TouchAction Action, float X, float Y, float Pressure)` (record struct), `TouchMessage(IReadOnlyList<TouchPointer> Pointers)`, `PingMessage(ulong TimestampUs)`, `PongMessage(ulong TimestampUs)`, `KeyframeRequestMessage`; `ProtocolException : IOException`; `static class MessageCodec { const ushort ProtocolVersion = 1; const int HeaderSize = 5; const int MaxPayloadLength = 16 * 1024 * 1024; const int MaxTouchPointers = 10; static byte[] Encode(Message); static Message Decode(byte type, ReadOnlySpan<byte> payload) }`. Teste: `Vectors.Load(string name) : byte[]`.

- [ ] **Step 1: Branch e esqueleto da solução**

```powershell
git switch -c parte-1-fundacao
dotnet new sln -n ScreenShare -o host
dotnet new classlib -n ScreenShare.Core -o host/ScreenShare.Core
dotnet new xunit -n ScreenShare.Tests -o host/ScreenShare.Tests
dotnet sln host add host/ScreenShare.Core/ScreenShare.Core.csproj host/ScreenShare.Tests/ScreenShare.Tests.csproj
dotnet add host/ScreenShare.Tests/ScreenShare.Tests.csproj reference host/ScreenShare.Core/ScreenShare.Core.csproj
Remove-Item host/ScreenShare.Core/Class1.cs, host/ScreenShare.Tests/UnitTest1.cs
```

`.gitignore` (raiz):
```text
# .NET
bin/
obj/
.vs/
*.user
TestResults/

# Android / Gradle
.gradle/
.kotlin/
build/
local.properties
*.iml

# IDEs
.idea/
.vscode/
```

`.gitattributes` (raiz):
```text
* text=auto
*.bat text eol=crlf
gradlew text eol=lf
*.jar binary
```

Em `host/ScreenShare.Tests/ScreenShare.Tests.csproj`, antes de `</Project>`:
```xml
  <ItemGroup>
    <!-- Vetores compartilhados com o Kotlin: copiados para bin/.../protocol-vectors -->
    <None Include="..\..\docs\protocol-vectors\*.hex" LinkBase="protocol-vectors" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

Run: `dotnet build host` — Expected: compilação OK.

- [ ] **Step 2: Escrever o documento do protocolo e os vetores**

`docs/protocol.md`:
````markdown
# Protocolo ScreenShare — versão 1

Uma conexão TCP entre o app Android (cliente) e o host Windows (servidor), porta padrão **38700**. Wi-Fi e USB (`adb reverse`) usam exatamente o mesmo protocolo. Inteiros são **little-endian**; `f32` é IEEE 754 de 32 bits little-endian.

## Quadro

| Campo | Tipo | Descrição |
|---|---|---|
| type | u8 | tipo da mensagem |
| length | u32 | tamanho do payload em bytes, no máximo 16 777 216 (16 MiB) |
| payload | `length` bytes | conteúdo, conforme o tipo |

`length` acima do limite é erro de protocolo: o receptor encerra a conexão sem alocar o payload.

## Sequência

1. O celular conecta e envia `HELLO`.
2. O PC responde `CONFIG`, ou fecha a conexão se `protocolVersion` for incompatível.
3. O PC envia `FRAME`s; o primeiro é keyframe.
4. Depois do `CONFIG`, a qualquer momento: `TOUCH`, `PING`/`PONG`, `KEYFRAME_REQ`.

## Mensagens

| Código | Nome | Direção | Tamanho do payload |
|---|---|---|---|
| 1 | HELLO | celular → PC | 9 |
| 2 | CONFIG | PC → celular | 11 + n |
| 3 | FRAME | PC → celular | 9 + n |
| 4 | TOUCH | celular → PC | 1 + 14·n |
| 5 | PING | qualquer lado | 8 |
| 6 | PONG | resposta ao PING | 8 |
| 7 | KEYFRAME_REQ | celular → PC | 0 |

### HELLO
| Campo | Tipo | Observação |
|---|---|---|
| protocolVersion | u16 | 1 |
| width | u16 | largura da tela do celular, em pixels |
| height | u16 | altura, em pixels |
| densityDpi | u16 | |
| supportedCodecs | u8 | flags: 1 = H.264, 2 = H.265; diferente de 0 e sem outros bits |

### CONFIG
| Campo | Tipo | Observação |
|---|---|---|
| width | u16 | resolução do monitor virtual |
| height | u16 | |
| codec | u8 | exatamente 1 (H.264) ou 2 (H.265) |
| bitrateKbps | u32 | |
| codecConfigLength | u16 | |
| codecConfig | bytes | SPS/PPS (+VPS no H.265) em Annex-B; vazio se vierem dentro dos FRAMEs |

### FRAME
| Campo | Tipo | Observação |
|---|---|---|
| timestampUs | u64 | instante da captura, relógio do PC |
| flags | u8 | bit 0 = keyframe; bits 1–7 reservados (enviar 0, ignorar ao receber) |
| data | resto do payload | NAL units em Annex-B |

### TOUCH
| Campo | Tipo | Observação |
|---|---|---|
| count | u8 | 1 a 10 |
| ponteiros | count × 14 bytes | ver abaixo |

Cada ponteiro: `id` u8, `action` u8 (0 DOWN, 1 MOVE, 2 UP, 3 CANCEL), `x` f32, `y` f32, `pressure` f32. `x`/`y` são normalizados ao monitor virtual (0 = esquerda/topo, 1 = direita/base); valores fora de 0..1 são aceitos (o host faz clamp), NaN e infinito são erro. Cada TOUCH traz todos os ponteiros ativos naquele instante: o que mudou leva DOWN/UP, os outros MOVE.

### PING / PONG
`timestampUs` u64: valor opaco de quem enviou o PING. O PONG devolve o mesmo valor e o remetente calcula o RTT com o próprio relógio.

### KEYFRAME_REQ
Sem payload. O celular pede um keyframe depois de um erro de decodificação.

## Validação
- Payload com bytes faltando ou sobrando é erro; tipo desconhecido é erro.
- Erros de protocolo: `ProtocolException` (subclasse de `IOException`) em C# e Kotlin.
- Stream terminando no meio de uma mensagem: `EndOfStreamException` (C#) / `EOFException` (Kotlin). Terminando entre mensagens: desconexão normal (`null`).

## Vetores de teste
`docs/protocol-vectors/*.hex` são quadros completos (cabeçalho + payload) em pares hexadecimais; `#` inicia comentário. As duas implementações devem gerar e aceitar exatamente esses bytes.

| Arquivo | Conteúdo |
|---|---|
| hello.hex | HELLO v1, 2400×1080, 420 dpi, H.264 + H.265 |
| config.hex | CONFIG 2400×1080, H.265, 20 000 kbps, 8 bytes de codec config |
| frame.hex | FRAME keyframe, t = 1 000 000 µs, 7 bytes de dados |
| touch.hex | TOUCH com 2 ponteiros (MOVE e DOWN) |
| ping.hex | PING t = 123 456 789 |
| pong.hex | PONG t = 123 456 789 |
| keyframe_req.hex | KEYFRAME_REQ |
````

`docs/protocol-vectors/hello.hex`:
```text
# HELLO v1 — 2400x1080, 420 dpi, H.264 + H.265
01 09 00 00 00   # type=HELLO, length=9
01 00            # protocolVersion=1
60 09            # width=2400
38 04            # height=1080
A4 01            # densityDpi=420
03               # supportedCodecs=H264|H265
```

`docs/protocol-vectors/config.hex`:
```text
# CONFIG — 2400x1080, H.265, 20000 kbps, 8 bytes de codec config
02 13 00 00 00            # type=CONFIG, length=19
60 09                     # width=2400
38 04                     # height=1080
02                        # codec=H265
20 4E 00 00               # bitrateKbps=20000
08 00                     # codecConfigLength=8
00 00 00 01 40 01 0C 01   # codecConfig
```

`docs/protocol-vectors/frame.hex`:
```text
# FRAME — keyframe, t=1000000 us, 7 bytes de dados
03 10 00 00 00            # type=FRAME, length=16
40 42 0F 00 00 00 00 00   # timestampUs=1000000
01                        # flags=keyframe
00 00 00 01 26 01 AF      # data
```

`docs/protocol-vectors/touch.hex`:
```text
# TOUCH — 2 ponteiros
04 1D 00 00 00                              # type=TOUCH, length=29
02                                          # count=2
00 01 00 00 80 3E 00 00 00 3F 00 00 80 3F   # id=0 MOVE x=0.25 y=0.5 pressure=1.0
01 00 00 00 40 3F 00 00 00 3E 00 00 00 3F   # id=1 DOWN x=0.75 y=0.125 pressure=0.5
```

`docs/protocol-vectors/ping.hex`:
```text
# PING — timestampUs=123456789
05 08 00 00 00            # type=PING, length=8
15 CD 5B 07 00 00 00 00   # timestampUs=123456789
```

`docs/protocol-vectors/pong.hex`:
```text
# PONG — timestampUs=123456789
06 08 00 00 00            # type=PONG, length=8
15 CD 5B 07 00 00 00 00   # timestampUs=123456789
```

`docs/protocol-vectors/keyframe_req.hex`:
```text
# KEYFRAME_REQ — sem payload
07 00 00 00 00   # type=KEYFRAME_REQ, length=0
```

- [ ] **Step 3: Escrever os testes que falham**

`host/ScreenShare.Tests/Protocol/Vectors.cs`:
```csharp
namespace ScreenShare.Tests.Protocol;

/// <summary>Carrega os vetores compartilhados de docs/protocol-vectors (pares hex; '#' inicia comentário).</summary>
internal static class Vectors
{
    public static byte[] Load(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "protocol-vectors", name);
        return File.ReadLines(path)
            .Select(line => line.Split('#')[0])
            .SelectMany(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Select(token => Convert.ToByte(token, 16))
            .ToArray();
    }
}
```

`host/ScreenShare.Tests/Protocol/MessageCodecTests.cs`:
```csharp
using System.Buffers.Binary;
using ScreenShare.Core.Protocol;

namespace ScreenShare.Tests.Protocol;

public class MessageCodecTests
{
    private static Message DecodeVector(byte[] vector) =>
        MessageCodec.Decode(vector[0], vector.AsSpan(MessageCodec.HeaderSize));

    [Fact]
    public void Hello_matches_vector()
    {
        var vector = Vectors.Load("hello.hex");
        var hello = new HelloMessage(1, 2400, 1080, 420, VideoCodec.H264 | VideoCodec.H265);

        Assert.Equal(vector, MessageCodec.Encode(hello));
        Assert.Equal(hello, DecodeVector(vector));
    }

    [Fact]
    public void Ping_matches_vector()
    {
        var vector = Vectors.Load("ping.hex");
        var ping = new PingMessage(123456789);

        Assert.Equal(vector, MessageCodec.Encode(ping));
        Assert.Equal(ping, DecodeVector(vector));
    }

    [Fact]
    public void Pong_matches_vector()
    {
        var vector = Vectors.Load("pong.hex");
        var pong = new PongMessage(123456789);

        Assert.Equal(vector, MessageCodec.Encode(pong));
        Assert.Equal(pong, DecodeVector(vector));
    }

    [Fact]
    public void KeyframeRequest_matches_vector()
    {
        var vector = Vectors.Load("keyframe_req.hex");

        Assert.Equal(vector, MessageCodec.Encode(new KeyframeRequestMessage()));
        Assert.Equal(new KeyframeRequestMessage(), DecodeVector(vector));
    }

    [Fact]
    public void Unknown_type_is_rejected() =>
        Assert.Throws<ProtocolException>(() => MessageCodec.Decode(0x63, ReadOnlySpan<byte>.Empty));

    [Fact]
    public void Truncated_payload_is_rejected() =>
        Assert.Throws<ProtocolException>(() => MessageCodec.Decode((byte)MessageType.Hello, new byte[8]));

    [Fact]
    public void Trailing_bytes_are_rejected() =>
        Assert.Throws<ProtocolException>(() => MessageCodec.Decode((byte)MessageType.Ping, new byte[9]));

    [Theory]
    [InlineData(0)] // nenhum codec
    [InlineData(4)] // bit desconhecido
    public void Hello_with_invalid_codec_flags_is_rejected(byte flags)
    {
        var payload = new byte[] { 1, 0, 0x60, 0x09, 0x38, 0x04, 0xA4, 0x01, flags };
        Assert.Throws<ProtocolException>(() => MessageCodec.Decode((byte)MessageType.Hello, payload));
    }
}
```

- [ ] **Step 4: Rodar e ver falhar**

Run: `dotnet test host`
Expected: FAIL na compilação — `CS0246: O nome do tipo ou do namespace "HelloMessage" não pode ser encontrado` (e similares).

- [ ] **Step 5: Implementar modelo, leitor/escritor de payload e codec**

`host/ScreenShare.Core/Protocol/Messages.cs`:
```csharp
namespace ScreenShare.Core.Protocol;

/// <summary>Código do tipo de mensagem no cabeçalho do quadro.</summary>
public enum MessageType : byte
{
    Hello = 1,
    Config = 2,
    Frame = 3,
    Touch = 4,
    Ping = 5,
    Pong = 6,
    KeyframeRequest = 7,
}

/// <summary>Flags de codec: o HELLO carrega uma combinação; o CONFIG, exatamente um.</summary>
[Flags]
public enum VideoCodec : byte
{
    None = 0,
    H264 = 1,
    H265 = 2,
}

public enum TouchAction : byte
{
    Down = 0,
    Move = 1,
    Up = 2,
    Cancel = 3,
}

public abstract record Message;

/// <summary>Celular → PC, primeira mensagem da conexão.</summary>
public sealed record HelloMessage(
    ushort ProtocolVersion, ushort Width, ushort Height, ushort DensityDpi, VideoCodec SupportedCodecs) : Message;

/// <summary>PC → celular. CodecConfig: SPS/PPS (+VPS no H.265) em Annex-B; vazio se vierem dentro dos FRAMEs.</summary>
public sealed record ConfigMessage(
    ushort Width, ushort Height, VideoCodec Codec, uint BitrateKbps, byte[] CodecConfig) : Message;

/// <summary>PC → celular: um quadro de vídeo codificado (NAL units em Annex-B).</summary>
public sealed record FrameMessage(ulong TimestampUs, bool IsKeyframe, byte[] Data) : Message;

/// <summary>Um dedo na tela. X e Y normalizados ao monitor virtual (0 = esquerda/topo, 1 = direita/base).</summary>
public readonly record struct TouchPointer(byte Id, TouchAction Action, float X, float Y, float Pressure);

/// <summary>Celular → PC: estado de todos os ponteiros ativos num instante.</summary>
public sealed record TouchMessage(IReadOnlyList<TouchPointer> Pointers) : Message;

/// <summary>Qualquer lado; quem recebe responde PONG com o mesmo valor.</summary>
public sealed record PingMessage(ulong TimestampUs) : Message;

public sealed record PongMessage(ulong TimestampUs) : Message;

/// <summary>Celular → PC: pede um keyframe após erro de decodificação.</summary>
public sealed record KeyframeRequestMessage : Message;
```

`host/ScreenShare.Core/Protocol/ProtocolException.cs`:
```csharp
namespace ScreenShare.Core.Protocol;

/// <summary>Bytes recebidos (ou uma mensagem a enviar) violam o protocolo — ver docs/protocol.md.</summary>
public sealed class ProtocolException(string message) : IOException(message);
```

`host/ScreenShare.Core/Protocol/PayloadReader.cs`:
```csharp
using System.Buffers.Binary;

namespace ScreenShare.Core.Protocol;

/// <summary>Lê campos little-endian em sequência; falta ou sobra de bytes vira ProtocolException.</summary>
internal ref struct PayloadReader
{
    private readonly ReadOnlySpan<byte> _span;
    private int _position;

    public PayloadReader(ReadOnlySpan<byte> span) => _span = span;

    public byte ReadByte() => Take(1)[0];
    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
    public float ReadSingle() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
    public byte[] ReadBytes(int count) => Take(count).ToArray();
    public byte[] ReadRemaining() => Take(_span.Length - _position).ToArray();

    public readonly void EnsureEnd()
    {
        if (_position != _span.Length)
            throw new ProtocolException($"{_span.Length - _position} bytes inesperados no fim do payload.");
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count > _span.Length - _position)
            throw new ProtocolException("Payload truncado.");
        var slice = _span.Slice(_position, count);
        _position += count;
        return slice;
    }
}
```

`host/ScreenShare.Core/Protocol/PayloadWriter.cs`:
```csharp
using System.Buffers.Binary;

namespace ScreenShare.Core.Protocol;

/// <summary>Escreve campos little-endian em sequência num span já dimensionado.</summary>
internal ref struct PayloadWriter
{
    private readonly Span<byte> _span;
    private int _position;

    public PayloadWriter(Span<byte> span) => _span = span;

    public void WriteByte(byte value) => _span[_position++] = value;

    public void WriteUInt16(ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(_span[_position..], value);
        _position += 2;
    }

    public void WriteUInt32(uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(_span[_position..], value);
        _position += 4;
    }

    public void WriteUInt64(ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(_span[_position..], value);
        _position += 8;
    }

    public void WriteSingle(float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(_span[_position..], value);
        _position += 4;
    }

    public void WriteBytes(ReadOnlySpan<byte> value)
    {
        value.CopyTo(_span[_position..]);
        _position += value.Length;
    }
}
```

`host/ScreenShare.Core/Protocol/MessageCodec.cs` (versão da Tarefa 1 — a Tarefa 2 substitui o arquivo inteiro):
```csharp
using System.Buffers.Binary;

namespace ScreenShare.Core.Protocol;

/// <summary>
/// Converte mensagens de/para quadros [type:u8][payloadLength:u32 LE][payload].
/// Layouts em docs/protocol.md; bytes de referência em docs/protocol-vectors.
/// </summary>
public static class MessageCodec
{
    public const ushort ProtocolVersion = 1;
    public const int HeaderSize = 5;
    public const int MaxPayloadLength = 16 * 1024 * 1024;
    public const int MaxTouchPointers = 10;

    private const VideoCodec KnownCodecs = VideoCodec.H264 | VideoCodec.H265;

    public static byte[] Encode(Message message)
    {
        switch (message)
        {
            case HelloMessage m:
            {
                var w = Begin(MessageType.Hello, 9, out var bytes);
                w.WriteUInt16(m.ProtocolVersion);
                w.WriteUInt16(m.Width);
                w.WriteUInt16(m.Height);
                w.WriteUInt16(m.DensityDpi);
                w.WriteByte((byte)m.SupportedCodecs);
                return bytes;
            }
            case PingMessage m:
            {
                var w = Begin(MessageType.Ping, 8, out var bytes);
                w.WriteUInt64(m.TimestampUs);
                return bytes;
            }
            case PongMessage m:
            {
                var w = Begin(MessageType.Pong, 8, out var bytes);
                w.WriteUInt64(m.TimestampUs);
                return bytes;
            }
            case KeyframeRequestMessage:
            {
                Begin(MessageType.KeyframeRequest, 0, out var bytes);
                return bytes;
            }
            default:
                throw new ArgumentException($"Mensagem não suportada: {message.GetType().Name}.", nameof(message));
        }
    }

    public static Message Decode(byte type, ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        var kind = (MessageType)type;
        Message message = kind switch
        {
            MessageType.Hello => DecodeHello(ref r),
            MessageType.Ping => new PingMessage(r.ReadUInt64()),
            MessageType.Pong => new PongMessage(r.ReadUInt64()),
            MessageType.KeyframeRequest => new KeyframeRequestMessage(),
            _ => throw new ProtocolException($"Tipo de mensagem desconhecido: 0x{type:X2}."),
        };
        r.EnsureEnd();
        return message;
    }

    private static PayloadWriter Begin(MessageType type, int payloadLength, out byte[] bytes)
    {
        if (payloadLength > MaxPayloadLength)
            throw new ProtocolException($"Payload de {payloadLength} bytes excede o limite de {MaxPayloadLength}.");
        bytes = new byte[HeaderSize + payloadLength];
        bytes[0] = (byte)type;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1), (uint)payloadLength);
        return new PayloadWriter(bytes.AsSpan(HeaderSize));
    }

    private static HelloMessage DecodeHello(ref PayloadReader r)
    {
        var version = r.ReadUInt16();
        var width = r.ReadUInt16();
        var height = r.ReadUInt16();
        var dpi = r.ReadUInt16();
        var codecs = (VideoCodec)r.ReadByte();
        if (codecs == VideoCodec.None || (codecs & ~KnownCodecs) != 0)
            throw new ProtocolException($"Flags de codec inválidas: {(byte)codecs}.");
        return new HelloMessage(version, width, height, dpi, codecs);
    }
}
```

- [ ] **Step 6: Rodar e ver passar**

Run: `dotnet test host`
Expected: PASS — 9 aprovados, 0 com falha.

- [ ] **Step 7: Corrigir a versão do .NET no spec**

Em `docs/superpowers/specs/2026-10-03-screenshare-design.md`, linha 9: trocar `**C# .NET 8**` por `**C# .NET 10**`.

- [ ] **Step 8: Commit**

```powershell
git add .gitignore .gitattributes host docs
git commit -m "feat(core): protocolo v1 com HELLO, PING, PONG e KEYFRAME_REQ" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Tarefa 2: CONFIG, FRAME e TOUCH (C#)

**Files:**
- Modify: `host/ScreenShare.Core/Protocol/MessageCodec.cs` (substituir o arquivo inteiro)
- Test: `host/ScreenShare.Tests/Protocol/MessageCodecTests.cs` (acrescentar testes)

**Interfaces:**
- Consumes: tudo que a Tarefa 1 produziu (`PayloadReader`, `PayloadWriter`, records, `Vectors.Load`).
- Produces: `MessageCodec.Encode/Decode` cobrindo os 7 tipos; constante privada `TouchPointerSize = 14`.

- [ ] **Step 1: Escrever os testes que falham**

Acrescentar dentro da classe `MessageCodecTests`:
```csharp
    [Fact]
    public void Config_matches_vector()
    {
        var vector = Vectors.Load("config.hex");
        byte[] codecConfig = [0x00, 0x00, 0x00, 0x01, 0x40, 0x01, 0x0C, 0x01];
        var config = new ConfigMessage(2400, 1080, VideoCodec.H265, 20000, codecConfig);

        Assert.Equal(vector, MessageCodec.Encode(config));
        var decoded = Assert.IsType<ConfigMessage>(DecodeVector(vector));
        // Records comparam arrays por referência: compara o resto pelo record e o array pelo conteúdo.
        Assert.Equal(config with { CodecConfig = decoded.CodecConfig }, decoded);
        Assert.Equal(codecConfig, decoded.CodecConfig);
    }

    [Fact]
    public void Frame_matches_vector()
    {
        var vector = Vectors.Load("frame.hex");
        byte[] data = [0x00, 0x00, 0x00, 0x01, 0x26, 0x01, 0xAF];
        var frame = new FrameMessage(1_000_000, true, data);

        Assert.Equal(vector, MessageCodec.Encode(frame));
        var decoded = Assert.IsType<FrameMessage>(DecodeVector(vector));
        Assert.Equal(frame with { Data = decoded.Data }, decoded);
        Assert.Equal(data, decoded.Data);
    }

    [Fact]
    public void Touch_matches_vector()
    {
        var vector = Vectors.Load("touch.hex");
        var touch = new TouchMessage([
            new TouchPointer(0, TouchAction.Move, 0.25f, 0.5f, 1.0f),
            new TouchPointer(1, TouchAction.Down, 0.75f, 0.125f, 0.5f),
        ]);

        Assert.Equal(vector, MessageCodec.Encode(touch));
        var decoded = Assert.IsType<TouchMessage>(DecodeVector(vector));
        Assert.Equal(touch.Pointers, decoded.Pointers);
    }

    [Fact]
    public void Config_with_unknown_codec_is_rejected()
    {
        // codec=3 (H264|H265) não é um codec único
        var payload = new byte[] { 0x60, 0x09, 0x38, 0x04, 3, 0x20, 0x4E, 0, 0, 0, 0 };
        Assert.Throws<ProtocolException>(() => MessageCodec.Decode((byte)MessageType.Config, payload));
    }

    [Fact]
    public void Config_with_codec_config_longer_than_payload_is_rejected()
    {
        // declara 8 bytes de codec config mas traz só 2
        var payload = new byte[] { 0x60, 0x09, 0x38, 0x04, 2, 0x20, 0x4E, 0, 0, 8, 0, 0xAA, 0xBB };
        Assert.Throws<ProtocolException>(() => MessageCodec.Decode((byte)MessageType.Config, payload));
    }

    [Fact]
    public void Frame_shorter_than_its_fixed_fields_is_rejected() =>
        Assert.Throws<ProtocolException>(() => MessageCodec.Decode((byte)MessageType.Frame, new byte[8]));

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void Touch_with_invalid_pointer_count_is_rejected(int count)
    {
        var payload = new byte[1 + 14 * count];
        payload[0] = (byte)count;
        Assert.Throws<ProtocolException>(() => MessageCodec.Decode((byte)MessageType.Touch, payload));
    }

    [Fact]
    public void Touch_with_unknown_action_is_rejected()
    {
        var payload = new byte[1 + 14];
        payload[0] = 1; // count
        payload[2] = 9; // action do primeiro ponteiro
        Assert.Throws<ProtocolException>(() => MessageCodec.Decode((byte)MessageType.Touch, payload));
    }

    [Fact]
    public void Touch_with_non_finite_value_is_rejected()
    {
        var payload = new byte[1 + 14];
        payload[0] = 1;
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(3), float.NaN); // x do primeiro ponteiro
        Assert.Throws<ProtocolException>(() => MessageCodec.Decode((byte)MessageType.Touch, payload));
    }

    [Fact]
    public void Encoding_touch_without_pointers_is_rejected() =>
        Assert.Throws<ProtocolException>(() => MessageCodec.Encode(new TouchMessage([])));

    [Fact]
    public void Encoding_codec_config_over_65535_bytes_is_rejected() =>
        Assert.Throws<ProtocolException>(() => MessageCodec.Encode(
            new ConfigMessage(1920, 1080, VideoCodec.H264, 8000, new byte[70_000])));
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet test host --filter "FullyQualifiedName~MessageCodecTests"`
Expected: FAIL — 5 falham (`Config_matches_vector`, `Frame_matches_vector`, `Touch_matches_vector`, `Encoding_touch_without_pointers_is_rejected`, `Encoding_codec_config_over_65535_bytes_is_rejected`) com `ArgumentException: Mensagem não suportada`; 16 passam. Os 7 testes novos de rejeição na decodificação já passam aqui porque o codec da Tarefa 1 trata CONFIG/FRAME/TOUCH como tipo desconhecido — eles só passam a testar a regra certa depois do Step 3.

- [ ] **Step 3: Implementar — substituir `host/ScreenShare.Core/Protocol/MessageCodec.cs` inteiro**

```csharp
using System.Buffers.Binary;

namespace ScreenShare.Core.Protocol;

/// <summary>
/// Converte mensagens de/para quadros [type:u8][payloadLength:u32 LE][payload].
/// Layouts em docs/protocol.md; bytes de referência em docs/protocol-vectors.
/// </summary>
public static class MessageCodec
{
    public const ushort ProtocolVersion = 1;
    public const int HeaderSize = 5;
    public const int MaxPayloadLength = 16 * 1024 * 1024;
    public const int MaxTouchPointers = 10;

    private const int TouchPointerSize = 14;
    private const VideoCodec KnownCodecs = VideoCodec.H264 | VideoCodec.H265;

    public static byte[] Encode(Message message)
    {
        switch (message)
        {
            case HelloMessage m:
            {
                var w = Begin(MessageType.Hello, 9, out var bytes);
                w.WriteUInt16(m.ProtocolVersion);
                w.WriteUInt16(m.Width);
                w.WriteUInt16(m.Height);
                w.WriteUInt16(m.DensityDpi);
                w.WriteByte((byte)m.SupportedCodecs);
                return bytes;
            }
            case ConfigMessage m:
            {
                if (m.CodecConfig.Length > ushort.MaxValue)
                    throw new ProtocolException($"Codec config de {m.CodecConfig.Length} bytes não cabe em u16.");
                var w = Begin(MessageType.Config, 11 + m.CodecConfig.Length, out var bytes);
                w.WriteUInt16(m.Width);
                w.WriteUInt16(m.Height);
                w.WriteByte((byte)m.Codec);
                w.WriteUInt32(m.BitrateKbps);
                w.WriteUInt16((ushort)m.CodecConfig.Length);
                w.WriteBytes(m.CodecConfig);
                return bytes;
            }
            case FrameMessage m:
            {
                var w = Begin(MessageType.Frame, 9 + m.Data.Length, out var bytes);
                w.WriteUInt64(m.TimestampUs);
                w.WriteByte(m.IsKeyframe ? (byte)1 : (byte)0);
                w.WriteBytes(m.Data);
                return bytes;
            }
            case TouchMessage m:
            {
                if (m.Pointers.Count is < 1 or > MaxTouchPointers)
                    throw new ProtocolException($"TOUCH precisa de 1..{MaxTouchPointers} ponteiros, recebeu {m.Pointers.Count}.");
                var w = Begin(MessageType.Touch, 1 + TouchPointerSize * m.Pointers.Count, out var bytes);
                w.WriteByte((byte)m.Pointers.Count);
                foreach (var p in m.Pointers)
                {
                    w.WriteByte(p.Id);
                    w.WriteByte((byte)p.Action);
                    w.WriteSingle(p.X);
                    w.WriteSingle(p.Y);
                    w.WriteSingle(p.Pressure);
                }
                return bytes;
            }
            case PingMessage m:
            {
                var w = Begin(MessageType.Ping, 8, out var bytes);
                w.WriteUInt64(m.TimestampUs);
                return bytes;
            }
            case PongMessage m:
            {
                var w = Begin(MessageType.Pong, 8, out var bytes);
                w.WriteUInt64(m.TimestampUs);
                return bytes;
            }
            case KeyframeRequestMessage:
            {
                Begin(MessageType.KeyframeRequest, 0, out var bytes);
                return bytes;
            }
            default:
                throw new ArgumentException($"Mensagem não suportada: {message.GetType().Name}.", nameof(message));
        }
    }

    public static Message Decode(byte type, ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        var kind = (MessageType)type;
        Message message = kind switch
        {
            MessageType.Hello => DecodeHello(ref r),
            MessageType.Config => DecodeConfig(ref r),
            MessageType.Frame => DecodeFrame(ref r),
            MessageType.Touch => DecodeTouch(ref r),
            MessageType.Ping => new PingMessage(r.ReadUInt64()),
            MessageType.Pong => new PongMessage(r.ReadUInt64()),
            MessageType.KeyframeRequest => new KeyframeRequestMessage(),
            _ => throw new ProtocolException($"Tipo de mensagem desconhecido: 0x{type:X2}."),
        };
        r.EnsureEnd();
        return message;
    }

    private static PayloadWriter Begin(MessageType type, int payloadLength, out byte[] bytes)
    {
        if (payloadLength > MaxPayloadLength)
            throw new ProtocolException($"Payload de {payloadLength} bytes excede o limite de {MaxPayloadLength}.");
        bytes = new byte[HeaderSize + payloadLength];
        bytes[0] = (byte)type;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1), (uint)payloadLength);
        return new PayloadWriter(bytes.AsSpan(HeaderSize));
    }

    private static HelloMessage DecodeHello(ref PayloadReader r)
    {
        var version = r.ReadUInt16();
        var width = r.ReadUInt16();
        var height = r.ReadUInt16();
        var dpi = r.ReadUInt16();
        var codecs = (VideoCodec)r.ReadByte();
        if (codecs == VideoCodec.None || (codecs & ~KnownCodecs) != 0)
            throw new ProtocolException($"Flags de codec inválidas: {(byte)codecs}.");
        return new HelloMessage(version, width, height, dpi, codecs);
    }

    private static ConfigMessage DecodeConfig(ref PayloadReader r)
    {
        var width = r.ReadUInt16();
        var height = r.ReadUInt16();
        var codec = (VideoCodec)r.ReadByte();
        if (codec is not (VideoCodec.H264 or VideoCodec.H265))
            throw new ProtocolException($"Codec inválido: {(byte)codec}.");
        var bitrate = r.ReadUInt32();
        var codecConfigLength = r.ReadUInt16();
        var codecConfig = r.ReadBytes(codecConfigLength);
        return new ConfigMessage(width, height, codec, bitrate, codecConfig);
    }

    private static FrameMessage DecodeFrame(ref PayloadReader r)
    {
        var timestamp = r.ReadUInt64();
        var flags = r.ReadByte();
        return new FrameMessage(timestamp, (flags & 1) != 0, r.ReadRemaining());
    }

    private static TouchMessage DecodeTouch(ref PayloadReader r)
    {
        var count = r.ReadByte();
        if (count is < 1 or > MaxTouchPointers)
            throw new ProtocolException($"TOUCH precisa de 1..{MaxTouchPointers} ponteiros, recebeu {count}.");
        var pointers = new TouchPointer[count];
        for (var i = 0; i < count; i++)
        {
            var id = r.ReadByte();
            var action = r.ReadByte();
            if (action > (byte)TouchAction.Cancel)
                throw new ProtocolException($"Ação de toque desconhecida: {action}.");
            var x = r.ReadSingle();
            var y = r.ReadSingle();
            var pressure = r.ReadSingle();
            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(pressure))
                throw new ProtocolException("Coordenada ou pressão de toque não finita.");
            pointers[i] = new TouchPointer(id, (TouchAction)action, x, y, pressure);
        }
        return new TouchMessage(pointers);
    }
}
```

- [ ] **Step 4: Rodar e ver passar**

Run: `dotnet test host`
Expected: PASS — 21 aprovados, 0 com falha.

- [ ] **Step 5: Commit**

```powershell
git add host
git commit -m "feat(core): mensagens CONFIG, FRAME e TOUCH com validação" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Tarefa 3: `MessageReader` — leitura de quadros de um stream (C#)

**Files:**
- Create: `host/ScreenShare.Core/Protocol/MessageReader.cs`
- Test: `host/ScreenShare.Tests/Protocol/MessageReaderTests.cs`

**Interfaces:**
- Consumes: `MessageCodec.Decode`, `MessageCodec.HeaderSize`, `MessageCodec.MaxPayloadLength`, `ProtocolException`, `Vectors.Load`.
- Produces: `sealed class MessageReader(Stream stream) { Task<Message?> ReadAsync(CancellationToken cancellationToken = default) }` — `null` = fim limpo; `EndOfStreamException` = corte no meio; `ProtocolException` = bytes inválidos. Usado pelo host TCP na Parte 3.

- [ ] **Step 1: Escrever os testes que falham**

`host/ScreenShare.Tests/Protocol/MessageReaderTests.cs`:
```csharp
using ScreenShare.Core.Protocol;

namespace ScreenShare.Tests.Protocol;

public class MessageReaderTests
{
    [Fact]
    public async Task Reads_consecutive_messages_then_null_at_clean_end()
    {
        var bytes = Vectors.Load("hello.hex")
            .Concat(Vectors.Load("ping.hex"))
            .Concat(Vectors.Load("keyframe_req.hex"))
            .ToArray();
        var reader = new MessageReader(new MemoryStream(bytes));

        Assert.Equal(new HelloMessage(1, 2400, 1080, 420, VideoCodec.H264 | VideoCodec.H265), await reader.ReadAsync());
        Assert.Equal(new PingMessage(123456789), await reader.ReadAsync());
        Assert.Equal(new KeyframeRequestMessage(), await reader.ReadAsync());
        Assert.Null(await reader.ReadAsync());
    }

    [Fact]
    public async Task Reassembles_messages_delivered_one_byte_at_a_time()
    {
        var reader = new MessageReader(new OneByteAtATimeStream(Vectors.Load("touch.hex")));

        var touch = Assert.IsType<TouchMessage>(await reader.ReadAsync());
        Assert.Equal(2, touch.Pointers.Count);
        Assert.Null(await reader.ReadAsync());
    }

    [Fact]
    public async Task Stream_ending_inside_header_throws_EndOfStream()
    {
        var reader = new MessageReader(new MemoryStream([0x05, 0x08]));
        await Assert.ThrowsAsync<EndOfStreamException>(() => reader.ReadAsync());
    }

    [Fact]
    public async Task Stream_ending_inside_payload_throws_EndOfStream()
    {
        var ping = Vectors.Load("ping.hex");
        var reader = new MessageReader(new MemoryStream(ping[..^3]));
        await Assert.ThrowsAsync<EndOfStreamException>(() => reader.ReadAsync());
    }

    [Fact]
    public async Task Oversized_length_is_rejected_before_reading_payload()
    {
        // FRAME declarando 4 GB de payload
        var reader = new MessageReader(new MemoryStream([0x03, 0xFF, 0xFF, 0xFF, 0xFF]));
        await Assert.ThrowsAsync<ProtocolException>(() => reader.ReadAsync());
    }

    /// <summary>Simula o TCP entregando um byte por leitura.</summary>
    private sealed class OneByteAtATimeStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, Math.Min(count, 1));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet test host --filter "FullyQualifiedName~MessageReaderTests"`
Expected: FAIL na compilação — `CS0246: ... "MessageReader" não pode ser encontrado`.

- [ ] **Step 3: Implementar**

`host/ScreenShare.Core/Protocol/MessageReader.cs`:
```csharp
using System.Buffers.Binary;

namespace ScreenShare.Core.Protocol;

/// <summary>Lê mensagens completas de um stream (ex.: NetworkStream), remontando quadros que o TCP entregou em pedaços.</summary>
public sealed class MessageReader(Stream stream)
{
    private readonly byte[] _header = new byte[MessageCodec.HeaderSize];

    /// <summary>Próxima mensagem, ou null se o stream terminou exatamente entre mensagens.</summary>
    /// <exception cref="EndOfStreamException">O stream terminou no meio de uma mensagem.</exception>
    /// <exception cref="ProtocolException">Os bytes recebidos violam o protocolo.</exception>
    public async Task<Message?> ReadAsync(CancellationToken cancellationToken = default)
    {
        var read = await stream.ReadAtLeastAsync(_header, _header.Length, throwOnEndOfStream: false, cancellationToken);
        if (read == 0)
            return null;
        if (read < _header.Length)
            throw new EndOfStreamException("Stream terminou dentro do cabeçalho de uma mensagem.");

        var length = BinaryPrimitives.ReadUInt32LittleEndian(_header.AsSpan(1));
        if (length > MessageCodec.MaxPayloadLength)
            throw new ProtocolException($"Payload de {length} bytes excede o limite de {MessageCodec.MaxPayloadLength}.");

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return MessageCodec.Decode(_header[0], payload);
    }
}
```

- [ ] **Step 4: Rodar e ver passar**

Run: `dotnet test host`
Expected: PASS — 26 aprovados, 0 com falha.

- [ ] **Step 5: Commit**

```powershell
git add host
git commit -m "feat(core): MessageReader remonta quadros de um stream" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Tarefa 4: Projeto Android

**Files:**
- Create (wizard do Android Studio): `android/**` — `settings.gradle.kts`, `build.gradle.kts`, `gradle/libs.versions.toml`, `gradlew(.bat)`, `gradle/wrapper/*`, `app/build.gradle.kts`, `app/src/main/java/dev/screenshare/android/MainActivity.kt` etc.
- Delete: `android/app/src/test/java/dev/screenshare/android/ExampleUnitTest.kt`, `android/app/src/androidTest/java/dev/screenshare/android/ExampleInstrumentedTest.kt`

**Interfaces:**
- Consumes: nada.
- Produces: projeto Gradle em `android/` que compila (`:app:assembleDebug`) e roda testes JVM (`:app:testDebugUnitTest`) com JUnit 4 (`testImplementation(libs.junit)` do template). Raiz do código: `android/app/src/main/java/dev/screenshare/android/` — se o wizard tiver criado `src/main/kotlin`, use `kotlin` no lugar de `java` em todos os caminhos das Tarefas 5 e 6.

- [ ] **Step 1 (você): Instalar o Android Studio**

Baixar em https://developer.android.com/studio (ou `winget install -e --id Google.AndroidStudio`), abrir e concluir o Setup Wizard em modo **Standard** — ele instala o Android SDK em `%LOCALAPPDATA%\Android\Sdk`.

- [ ] **Step 2 (você): Criar o projeto pelo wizard**

*New Project → Phone and Tablet → Empty Activity*, com:
- Name: `ScreenShare`
- Package name: `dev.screenshare.android`
- Save location: `C:\Users\eduar\OneDrive\Documentos\GitHub\Screenshare\android`
- Minimum SDK: `API 29 ("Q"; Android 10.0)`
- Build configuration language: `Kotlin DSL (build.gradle.kts)`

Esperar o Gradle Sync terminar. Se o Android Studio perguntar se deve adicionar arquivos ao Git, recusar — o commit é feito no Step 5.

- [ ] **Step 3: Remover os testes de exemplo do template**

```powershell
Remove-Item android/app/src/test/java/dev/screenshare/android/ExampleUnitTest.kt, android/app/src/androidTest/java/dev/screenshare/android/ExampleInstrumentedTest.kt
```

- [ ] **Step 4: Compilar pela linha de comando**

```powershell
$env:JAVA_HOME = 'C:\Program Files\Android\Android Studio\jbr'; .\android\gradlew.bat -p android :app:assembleDebug
```
Expected: `BUILD SUCCESSFUL`. Conferir que `git status --short android` não lista `local.properties` nem pastas `build/` (o `.gitignore` gerado pelo wizard cobre).

- [ ] **Step 5: Commit**

```powershell
git add android
git commit -m "chore(android): projeto Android (Compose, minSdk 29)" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Tarefa 5: Codec do protocolo em Kotlin

**Files:**
- Create: `android/app/src/main/java/dev/screenshare/android/protocol/{ProtocolException,Messages,PayloadReader,MessageCodec}.kt`
- Test: `android/app/src/test/java/dev/screenshare/android/protocol/{Vectors,MessageCodecTest}.kt`
- Modify: `android/app/build.gradle.kts` (vetores como entrada dos testes)

**Interfaces:**
- Consumes: vetores `docs/protocol-vectors/*.hex` e o layout de `docs/protocol.md` (Tarefa 1); projeto Android (Tarefa 4).
- Produces: package `dev.screenshare.android.protocol` com `object MessageType` (consts `HELLO=1 … KEYFRAME_REQUEST=7`), `object VideoCodec { H264=1, H265=2, ALL=3 }`, `enum class TouchAction(val code: Int)`, `sealed interface Message` e `HelloMessage(protocolVersion: Int, width: Int, height: Int, densityDpi: Int, supportedCodecs: Int)`, `ConfigMessage(width: Int, height: Int, codec: Int, bitrateKbps: Long, codecConfig: ByteArray)`, `FrameMessage(timestampUs: Long, isKeyframe: Boolean, data: ByteArray)`, `TouchPointer(id: Int, action: TouchAction, x: Float, y: Float, pressure: Float)`, `TouchMessage(pointers: List<TouchPointer>)`, `PingMessage(timestampUs: Long)`, `PongMessage(timestampUs: Long)`, `data object KeyframeRequestMessage`; `class ProtocolException : IOException`; `object MessageCodec { PROTOCOL_VERSION, HEADER_SIZE, MAX_PAYLOAD_LENGTH, MAX_TOUCH_POINTERS; fun encode(message: Message): ByteArray; fun decode(type: Int, payload: ByteArray): Message }`. Teste: `Vectors.load(name: String): ByteArray`.

- [ ] **Step 1: Escrever os testes que falham**

No fim de `android/app/build.gradle.kts` (sem isso o Gradle considera os testes "UP-TO-DATE" e não roda de novo quando só um vetor muda):
```kotlin
// Os testes leem docs/protocol-vectors: mudar um vetor precisa invalidar o cache do Gradle.
val protocolVectors = layout.projectDirectory.dir("../../docs/protocol-vectors")
tasks.withType<Test>().configureEach {
    inputs.dir(protocolVectors)
}
```

`android/app/src/test/java/dev/screenshare/android/protocol/Vectors.kt`:
```kotlin
package dev.screenshare.android.protocol

import java.io.File

/** Carrega os vetores compartilhados de docs/protocol-vectors (pares hex; '#' inicia comentário). */
object Vectors {
    // O Gradle roda os testes com o diretório do módulo (android/app) como diretório de trabalho.
    private val dir = File(System.getProperty("user.dir"), "../../docs/protocol-vectors")

    fun load(name: String): ByteArray {
        val file = File(dir, name)
        require(file.isFile) { "vetor não encontrado: ${file.canonicalPath}" }
        return file.readLines()
            .map { it.substringBefore('#') }
            .flatMap { it.trim().split(Regex("\\s+")) }
            .filter { it.isNotEmpty() }
            .map { it.toInt(16).toByte() }
            .toByteArray()
    }
}
```

`android/app/src/test/java/dev/screenshare/android/protocol/MessageCodecTest.kt`:
```kotlin
package dev.screenshare.android.protocol

import java.nio.ByteBuffer
import java.nio.ByteOrder
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class MessageCodecTest {
    private fun decodeVector(vector: ByteArray): Message =
        MessageCodec.decode(vector[0].toInt() and 0xFF, vector.copyOfRange(MessageCodec.HEADER_SIZE, vector.size))

    private fun assertMatchesVector(name: String, message: Message) {
        val vector = Vectors.load(name)
        assertArrayEquals(vector, MessageCodec.encode(message))
        assertEquals(message, decodeVector(vector))
    }

    private fun assertRejected(type: Int, payload: ByteArray) {
        assertThrows(ProtocolException::class.java) { MessageCodec.decode(type, payload) }
    }

    @Test
    fun hello() = assertMatchesVector("hello.hex", HelloMessage(1, 2400, 1080, 420, VideoCodec.H264 or VideoCodec.H265))

    @Test
    fun config() = assertMatchesVector(
        "config.hex",
        ConfigMessage(2400, 1080, VideoCodec.H265, 20000, byteArrayOf(0x00, 0x00, 0x00, 0x01, 0x40, 0x01, 0x0C, 0x01)),
    )

    @Test
    fun frame() = assertMatchesVector(
        "frame.hex",
        FrameMessage(1_000_000, true, byteArrayOf(0x00, 0x00, 0x00, 0x01, 0x26, 0x01, 0xAF.toByte())),
    )

    @Test
    fun touch() = assertMatchesVector(
        "touch.hex",
        TouchMessage(
            listOf(
                TouchPointer(0, TouchAction.MOVE, 0.25f, 0.5f, 1.0f),
                TouchPointer(1, TouchAction.DOWN, 0.75f, 0.125f, 0.5f),
            ),
        ),
    )

    @Test
    fun ping() = assertMatchesVector("ping.hex", PingMessage(123456789))

    @Test
    fun pong() = assertMatchesVector("pong.hex", PongMessage(123456789))

    @Test
    fun keyframeRequest() = assertMatchesVector("keyframe_req.hex", KeyframeRequestMessage)

    @Test
    fun unknownTypeIsRejected() = assertRejected(0x63, ByteArray(0))

    @Test
    fun truncatedPayloadIsRejected() = assertRejected(MessageType.HELLO, ByteArray(8))

    @Test
    fun trailingBytesAreRejected() = assertRejected(MessageType.PING, ByteArray(9))

    @Test
    fun helloWithInvalidCodecFlagsIsRejected() {
        for (flags in listOf(0, 4)) { // nenhum codec; bit desconhecido
            assertRejected(
                MessageType.HELLO,
                byteArrayOf(1, 0, 0x60, 0x09, 0x38, 0x04, 0xA4.toByte(), 0x01, flags.toByte()),
            )
        }
    }

    @Test
    fun configWithUnknownCodecIsRejected() = // codec=3 (H264|H265) não é um codec único
        assertRejected(MessageType.CONFIG, byteArrayOf(0x60, 0x09, 0x38, 0x04, 3, 0x20, 0x4E, 0, 0, 0, 0))

    @Test
    fun configWithCodecConfigLongerThanPayloadIsRejected() = // declara 8 bytes, traz 2
        assertRejected(
            MessageType.CONFIG,
            byteArrayOf(0x60, 0x09, 0x38, 0x04, 2, 0x20, 0x4E, 0, 0, 8, 0, 0xAA.toByte(), 0xBB.toByte()),
        )

    @Test
    fun frameShorterThanItsFixedFieldsIsRejected() = assertRejected(MessageType.FRAME, ByteArray(8))

    @Test
    fun touchWithInvalidPointerCountIsRejected() {
        for (count in listOf(0, 11)) {
            assertRejected(MessageType.TOUCH, ByteArray(1 + 14 * count).also { it[0] = count.toByte() })
        }
    }

    @Test
    fun touchWithUnknownActionIsRejected() =
        assertRejected(MessageType.TOUCH, ByteArray(15).also { it[0] = 1; it[2] = 9 })

    @Test
    fun touchWithNonFiniteValueIsRejected() {
        val payload = ByteBuffer.allocate(15).order(ByteOrder.LITTLE_ENDIAN)
            .put(1.toByte()).put(0.toByte()).put(0.toByte()) // count, id, action
            .putFloat(Float.NaN).putFloat(0f).putFloat(1f) // x, y, pressure
            .array()
        assertRejected(MessageType.TOUCH, payload)
    }

    @Test
    fun encodingTouchWithoutPointersIsRejected() {
        assertThrows(ProtocolException::class.java) { MessageCodec.encode(TouchMessage(emptyList())) }
    }

    @Test
    fun encodingCodecConfigOver65535BytesIsRejected() {
        assertThrows(ProtocolException::class.java) {
            MessageCodec.encode(ConfigMessage(1920, 1080, VideoCodec.H264, 8000, ByteArray(70_000)))
        }
    }
}
```

- [ ] **Step 2: Rodar e ver falhar**

```powershell
$env:JAVA_HOME = 'C:\Program Files\Android\Android Studio\jbr'; .\android\gradlew.bat -p android :app:testDebugUnitTest
```
Expected: FAIL na compilação — `Unresolved reference 'MessageCodec'` (e similares).

- [ ] **Step 3: Implementar**

`android/app/src/main/java/dev/screenshare/android/protocol/ProtocolException.kt`:
```kotlin
package dev.screenshare.android.protocol

import java.io.IOException

/** Bytes recebidos (ou uma mensagem a enviar) violam o protocolo — ver docs/protocol.md. */
class ProtocolException(message: String) : IOException(message)
```

`android/app/src/main/java/dev/screenshare/android/protocol/Messages.kt`:
```kotlin
package dev.screenshare.android.protocol

import java.util.Objects

/** Código do tipo de mensagem no cabeçalho do quadro. */
object MessageType {
    const val HELLO = 1
    const val CONFIG = 2
    const val FRAME = 3
    const val TOUCH = 4
    const val PING = 5
    const val PONG = 6
    const val KEYFRAME_REQUEST = 7
}

/** Flags de codec: o HELLO carrega uma combinação; o CONFIG, exatamente um. */
object VideoCodec {
    const val H264 = 1
    const val H265 = 2
    const val ALL = H264 or H265
}

enum class TouchAction(val code: Int) { DOWN(0), MOVE(1), UP(2), CANCEL(3) }

sealed interface Message

/** Celular → PC, primeira mensagem da conexão. */
data class HelloMessage(
    val protocolVersion: Int,
    val width: Int,
    val height: Int,
    val densityDpi: Int,
    val supportedCodecs: Int,
) : Message

/** PC → celular. codecConfig: SPS/PPS (+VPS no H.265) em Annex-B; vazio se vierem dentro dos FRAMEs. */
data class ConfigMessage(
    val width: Int,
    val height: Int,
    val codec: Int,
    val bitrateKbps: Long,
    val codecConfig: ByteArray,
) : Message {
    // ByteArray compara por referência; aqui comparamos o conteúdo.
    override fun equals(other: Any?) = other is ConfigMessage &&
        width == other.width && height == other.height && codec == other.codec &&
        bitrateKbps == other.bitrateKbps && codecConfig.contentEquals(other.codecConfig)

    override fun hashCode() = Objects.hash(width, height, codec, bitrateKbps, codecConfig.contentHashCode())
}

/** PC → celular: um quadro de vídeo codificado (NAL units em Annex-B). */
data class FrameMessage(
    val timestampUs: Long,
    val isKeyframe: Boolean,
    val data: ByteArray,
) : Message {
    override fun equals(other: Any?) = other is FrameMessage &&
        timestampUs == other.timestampUs && isKeyframe == other.isKeyframe && data.contentEquals(other.data)

    override fun hashCode() = Objects.hash(timestampUs, isKeyframe, data.contentHashCode())
}

/** Um dedo na tela. x e y normalizados ao monitor virtual (0 = esquerda/topo, 1 = direita/base). */
data class TouchPointer(val id: Int, val action: TouchAction, val x: Float, val y: Float, val pressure: Float)

/** Celular → PC: estado de todos os ponteiros ativos num instante. */
data class TouchMessage(val pointers: List<TouchPointer>) : Message

/** Qualquer lado; quem recebe responde PONG com o mesmo valor. */
data class PingMessage(val timestampUs: Long) : Message

data class PongMessage(val timestampUs: Long) : Message

/** Celular → PC: pede um keyframe após erro de decodificação. */
data object KeyframeRequestMessage : Message
```

`android/app/src/main/java/dev/screenshare/android/protocol/PayloadReader.kt`:
```kotlin
package dev.screenshare.android.protocol

import java.nio.ByteBuffer
import java.nio.ByteOrder

/** Lê campos little-endian em sequência; falta ou sobra de bytes vira ProtocolException. */
internal class PayloadReader(payload: ByteArray) {
    private val buffer = ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN)

    fun u8(): Int { need(1); return buffer.get().toInt() and 0xFF }
    fun u16(): Int { need(2); return buffer.short.toInt() and 0xFFFF }
    fun u32(): Long { need(4); return buffer.int.toLong() and 0xFFFFFFFFL }
    fun u64(): Long { need(8); return buffer.long }
    fun f32(): Float { need(4); return buffer.float }
    fun bytes(count: Int): ByteArray { need(count); return ByteArray(count).also { buffer.get(it) } }
    fun remaining(): ByteArray = bytes(buffer.remaining())

    fun ensureEnd() {
        if (buffer.hasRemaining()) throw ProtocolException("${buffer.remaining()} bytes inesperados no fim do payload")
    }

    private fun need(count: Int) {
        if (buffer.remaining() < count) throw ProtocolException("payload truncado")
    }
}
```

`android/app/src/main/java/dev/screenshare/android/protocol/MessageCodec.kt`:
```kotlin
package dev.screenshare.android.protocol

import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * Converte mensagens de/para quadros [type:u8][payloadLength:u32 LE][payload].
 * Layouts em docs/protocol.md; bytes de referência em docs/protocol-vectors.
 */
object MessageCodec {
    const val PROTOCOL_VERSION = 1
    const val HEADER_SIZE = 5
    const val MAX_PAYLOAD_LENGTH = 16 * 1024 * 1024
    const val MAX_TOUCH_POINTERS = 10

    private const val TOUCH_POINTER_SIZE = 14

    fun encode(message: Message): ByteArray = when (message) {
        is HelloMessage -> frame(MessageType.HELLO, 9) {
            putShort(message.protocolVersion.toShort())
            putShort(message.width.toShort())
            putShort(message.height.toShort())
            putShort(message.densityDpi.toShort())
            put(message.supportedCodecs.toByte())
        }
        is ConfigMessage -> {
            val size = message.codecConfig.size
            if (size > 0xFFFF) throw ProtocolException("codec config de $size bytes não cabe em u16")
            frame(MessageType.CONFIG, 11 + size) {
                putShort(message.width.toShort())
                putShort(message.height.toShort())
                put(message.codec.toByte())
                putInt(message.bitrateKbps.toInt())
                putShort(size.toShort())
                put(message.codecConfig)
            }
        }
        is FrameMessage -> frame(MessageType.FRAME, 9 + message.data.size) {
            putLong(message.timestampUs)
            put((if (message.isKeyframe) 1 else 0).toByte())
            put(message.data)
        }
        is TouchMessage -> {
            val count = message.pointers.size
            if (count !in 1..MAX_TOUCH_POINTERS) {
                throw ProtocolException("TOUCH precisa de 1..$MAX_TOUCH_POINTERS ponteiros, recebeu $count")
            }
            frame(MessageType.TOUCH, 1 + TOUCH_POINTER_SIZE * count) {
                put(count.toByte())
                for (p in message.pointers) {
                    put(p.id.toByte())
                    put(p.action.code.toByte())
                    putFloat(p.x)
                    putFloat(p.y)
                    putFloat(p.pressure)
                }
            }
        }
        is PingMessage -> frame(MessageType.PING, 8) { putLong(message.timestampUs) }
        is PongMessage -> frame(MessageType.PONG, 8) { putLong(message.timestampUs) }
        KeyframeRequestMessage -> frame(MessageType.KEYFRAME_REQUEST, 0) {}
    }

    fun decode(type: Int, payload: ByteArray): Message {
        val r = PayloadReader(payload)
        val message = when (type) {
            MessageType.HELLO -> decodeHello(r)
            MessageType.CONFIG -> decodeConfig(r)
            MessageType.FRAME -> decodeFrame(r)
            MessageType.TOUCH -> decodeTouch(r)
            MessageType.PING -> PingMessage(r.u64())
            MessageType.PONG -> PongMessage(r.u64())
            MessageType.KEYFRAME_REQUEST -> KeyframeRequestMessage
            else -> throw ProtocolException("tipo de mensagem desconhecido: 0x%02X".format(type))
        }
        r.ensureEnd()
        return message
    }

    private inline fun frame(type: Int, payloadLength: Int, write: ByteBuffer.() -> Unit): ByteArray {
        if (payloadLength > MAX_PAYLOAD_LENGTH) {
            throw ProtocolException("payload de $payloadLength bytes excede o limite de $MAX_PAYLOAD_LENGTH")
        }
        val buffer = ByteBuffer.allocate(HEADER_SIZE + payloadLength).order(ByteOrder.LITTLE_ENDIAN)
        buffer.put(type.toByte())
        buffer.putInt(payloadLength)
        buffer.write()
        return buffer.array()
    }

    private fun decodeHello(r: PayloadReader): HelloMessage {
        val version = r.u16()
        val width = r.u16()
        val height = r.u16()
        val dpi = r.u16()
        val codecs = r.u8()
        if (codecs == 0 || (codecs and VideoCodec.ALL.inv()) != 0) {
            throw ProtocolException("flags de codec inválidas: $codecs")
        }
        return HelloMessage(version, width, height, dpi, codecs)
    }

    private fun decodeConfig(r: PayloadReader): ConfigMessage {
        val width = r.u16()
        val height = r.u16()
        val codec = r.u8()
        if (codec != VideoCodec.H264 && codec != VideoCodec.H265) throw ProtocolException("codec inválido: $codec")
        val bitrate = r.u32()
        val codecConfigLength = r.u16()
        val codecConfig = r.bytes(codecConfigLength)
        return ConfigMessage(width, height, codec, bitrate, codecConfig)
    }

    private fun decodeFrame(r: PayloadReader): FrameMessage {
        val timestamp = r.u64()
        val flags = r.u8()
        return FrameMessage(timestamp, (flags and 1) != 0, r.remaining())
    }

    private fun decodeTouch(r: PayloadReader): TouchMessage {
        val count = r.u8()
        if (count !in 1..MAX_TOUCH_POINTERS) {
            throw ProtocolException("TOUCH precisa de 1..$MAX_TOUCH_POINTERS ponteiros, recebeu $count")
        }
        val pointers = List(count) {
            val id = r.u8()
            val actionCode = r.u8()
            val action = TouchAction.entries.firstOrNull { it.code == actionCode }
                ?: throw ProtocolException("ação de toque desconhecida: $actionCode")
            val x = r.f32()
            val y = r.f32()
            val pressure = r.f32()
            if (!x.isFinite() || !y.isFinite() || !pressure.isFinite()) {
                throw ProtocolException("coordenada ou pressão de toque não finita")
            }
            TouchPointer(id, action, x, y, pressure)
        }
        return TouchMessage(pointers)
    }
}
```

- [ ] **Step 4: Rodar e ver passar**

```powershell
$env:JAVA_HOME = 'C:\Program Files\Android\Android Studio\jbr'; .\android\gradlew.bat -p android :app:testDebugUnitTest
```
Expected: `BUILD SUCCESSFUL` — 19 testes, 0 falhas (relatório em `android/app/build/reports/tests/testDebugUnitTest/index.html`).

- [ ] **Step 5: Commit**

```powershell
git add android
git commit -m "feat(android): codec do protocolo v1 validado pelos vetores compartilhados" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Tarefa 6: `MessageReader` em Kotlin

**Files:**
- Create: `android/app/src/main/java/dev/screenshare/android/protocol/MessageReader.kt`
- Test: `android/app/src/test/java/dev/screenshare/android/protocol/MessageReaderTest.kt`

**Interfaces:**
- Consumes: `MessageCodec.decode`, `MessageCodec.HEADER_SIZE`, `MessageCodec.MAX_PAYLOAD_LENGTH`, `ProtocolException`, `Vectors.load` (Tarefa 5).
- Produces: `class MessageReader(input: InputStream) { fun read(): Message? }` — bloqueante; `null` = fim limpo; `EOFException` = corte no meio; `ProtocolException` = bytes inválidos. Usado pela conexão do app na Parte 3.

- [ ] **Step 1: Escrever os testes que falham**

`android/app/src/test/java/dev/screenshare/android/protocol/MessageReaderTest.kt`:
```kotlin
package dev.screenshare.android.protocol

import java.io.ByteArrayInputStream
import java.io.EOFException
import java.io.InputStream
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Test

class MessageReaderTest {
    @Test
    fun readsConsecutiveMessagesThenNullAtCleanEnd() {
        val bytes = Vectors.load("hello.hex") + Vectors.load("ping.hex") + Vectors.load("keyframe_req.hex")
        val reader = MessageReader(ByteArrayInputStream(bytes))

        assertEquals(HelloMessage(1, 2400, 1080, 420, VideoCodec.ALL), reader.read())
        assertEquals(PingMessage(123456789), reader.read())
        assertEquals(KeyframeRequestMessage, reader.read())
        assertNull(reader.read())
    }

    @Test
    fun reassemblesMessagesDeliveredOneByteAtATime() {
        val reader = MessageReader(OneByteAtATime(ByteArrayInputStream(Vectors.load("touch.hex"))))

        val touch = reader.read() as TouchMessage
        assertEquals(2, touch.pointers.size)
        assertNull(reader.read())
    }

    @Test
    fun streamEndingInsideHeaderThrowsEof() {
        val reader = MessageReader(ByteArrayInputStream(byteArrayOf(0x05, 0x08)))
        assertThrows(EOFException::class.java) { reader.read() }
    }

    @Test
    fun streamEndingInsidePayloadThrowsEof() {
        val ping = Vectors.load("ping.hex")
        val reader = MessageReader(ByteArrayInputStream(ping.copyOf(ping.size - 3)))
        assertThrows(EOFException::class.java) { reader.read() }
    }

    @Test
    fun oversizedLengthIsRejectedBeforeReadingPayload() {
        // FRAME declarando 4 GB de payload
        val reader = MessageReader(ByteArrayInputStream(byteArrayOf(0x03, -1, -1, -1, -1)))
        assertThrows(ProtocolException::class.java) { reader.read() }
    }

    /** Simula o TCP entregando um byte por leitura. */
    private class OneByteAtATime(private val inner: InputStream) : InputStream() {
        override fun read(): Int = inner.read()
        override fun read(b: ByteArray, off: Int, len: Int): Int = if (len == 0) 0 else inner.read(b, off, 1)
    }
}
```

- [ ] **Step 2: Rodar e ver falhar**

```powershell
$env:JAVA_HOME = 'C:\Program Files\Android\Android Studio\jbr'; .\android\gradlew.bat -p android :app:testDebugUnitTest
```
Expected: FAIL na compilação — `Unresolved reference 'MessageReader'`.

- [ ] **Step 3: Implementar**

`android/app/src/main/java/dev/screenshare/android/protocol/MessageReader.kt`:
```kotlin
package dev.screenshare.android.protocol

import java.io.EOFException
import java.io.InputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder

/** Lê mensagens completas de um stream (ex.: socket TCP), remontando quadros entregues em pedaços. Bloqueante. */
class MessageReader(private val input: InputStream) {
    private val header = ByteArray(MessageCodec.HEADER_SIZE)

    /**
     * Próxima mensagem, ou null se o stream terminou exatamente entre mensagens.
     * @throws EOFException o stream terminou no meio de uma mensagem.
     * @throws ProtocolException os bytes recebidos violam o protocolo.
     */
    fun read(): Message? {
        val headerRead = readFully(header)
        if (headerRead == 0) return null
        if (headerRead < header.size) throw EOFException("stream terminou dentro do cabeçalho de uma mensagem")

        val length = ByteBuffer.wrap(header, 1, 4).order(ByteOrder.LITTLE_ENDIAN).int.toLong() and 0xFFFFFFFFL
        if (length > MessageCodec.MAX_PAYLOAD_LENGTH) {
            throw ProtocolException("payload de $length bytes excede o limite de ${MessageCodec.MAX_PAYLOAD_LENGTH}")
        }

        val payload = ByteArray(length.toInt())
        if (readFully(payload) < payload.size) throw EOFException("stream terminou dentro do payload de uma mensagem")
        return MessageCodec.decode(header[0].toInt() and 0xFF, payload)
    }

    /** Lê até encher [buffer] ou o stream acabar; devolve quantos bytes leu. */
    private fun readFully(buffer: ByteArray): Int {
        var total = 0
        while (total < buffer.size) {
            val n = input.read(buffer, total, buffer.size - total)
            if (n < 0) break
            total += n
        }
        return total
    }
}
```

- [ ] **Step 4: Rodar e ver passar**

```powershell
$env:JAVA_HOME = 'C:\Program Files\Android\Android Studio\jbr'; .\android\gradlew.bat -p android :app:testDebugUnitTest
```
Expected: `BUILD SUCCESSFUL` — 24 testes, 0 falhas.

- [ ] **Step 5: Commit**

```powershell
git add android
git commit -m "feat(android): MessageReader remonta quadros de um InputStream" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Verificação da Parte 1
1. `dotnet test host` → 26 aprovados, 0 falhas.
2. `$env:JAVA_HOME = 'C:\Program Files\Android\Android Studio\jbr'; .\android\gradlew.bat -p android :app:testDebugUnitTest` → 24 testes, 0 falhas.
3. Prova de que os dois lados leem os mesmos vetores: trocar o último `03` de `docs/protocol-vectors/hello.hex` por `01`, rodar as duas suítes — o teste de HELLO falha nas duas — e desfazer com `git checkout docs/protocol-vectors/hello.hex`.
4. Revisão final da branch `parte-1-fundacao` e, com seu ok, merge em `master` (skill finishing-a-development-branch).

## Execução
- Depois da aprovação, este plano é salvo em `docs/superpowers/plans/2026-10-03-parte-1-fundacao.md` e commitado.
- Recomendação: **Native** — eu implemento cada tarefa nesta sessão e **paro ao fim de cada uma** para você ver o resultado antes de seguir (uma a uma, como pediu); no fim da parte, um revisor independente olha a branch inteira. As 6 tarefas são sequenciais e compartilham os mesmos nomes e tipos, e os vetores compartilhados já pegam erros de protocolo, então revisão por subagente a cada tarefa custaria mais sem ganho proporcional. Confirmo o método com você logo após a aprovação.
- As Tarefas 1–3 (C#) não dependem do Android Studio: você pode instalá-lo enquanto elas rodam (só a Tarefa 4 precisa dele).
