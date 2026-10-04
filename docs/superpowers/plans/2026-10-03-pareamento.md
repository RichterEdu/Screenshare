# Pareamento e autenticação — Plano de Implementação

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Só celulares pareados por QR conseguem usar o PC pelo Wi-Fi (TLS com certificado fixo + chave de acesso), e o USB continua direto, sem pareamento, numa porta que só escuta em loopback.

**Architecture:** O protocolo vai para a v2, com 4 mensagens novas (`PAIR`, `PAIRED`, `AUTH`, `DENIED`) antes do `HELLO`. No PC, um namespace novo `ScreenShare.Core.Security` cuida de certificado, sessão de pareamento e lista de aparelhos; o `DevHost` passa a ter dois listeners (TLS na 38700, TCP puro em `127.0.0.1:38701`). No Android, `PinnedTrustManager` + `PairingStore` (DataStore + Android Keystore) + `Connection` com três alvos (USB, Wi-Fi, Pareamento) e um leitor de QR do Google Play Services.

**Tech Stack:** .NET 10 (`SslStream`, `CertificateRequest`, `ProtectedData`, QRCoder), Kotlin/Android (JSSE `SSLSocket`, DataStore Preferences, Android Keystore, `play-services-code-scanner`), xUnit, JUnit 4.

**Spec:** `docs/superpowers/specs/2026-10-03-pareamento-autenticacao-design.md`

## Global Constraints

- Trabalho na worktree `C:\Users\eduar\OneDrive\Documentos\GitHub\Screenshare\.worktrees\pareamento` (branch `pareamento`). Nunca rodar git no checkout principal (outra sessão trabalha lá).
- Portas: **38700** = Wi-Fi, todas as interfaces, **TLS obrigatório**; **38701** = USB (`adb reverse tcp:38701 tcp:38701`), escuta **só em `127.0.0.1`**, TCP puro. Só a 38700 é anunciada por mDNS.
- Protocolo **v2** (`ProtocolVersion` = 2). O layout de 9 bytes do `HELLO` fica congelado em todas as versões.
- Mensagens novas: `PAIR` = 8 (celular → PC: `secret` 32 bytes + `deviceNameLength` u8 1..64 + `deviceName` UTF-8), `PAIRED` = 9 (PC → celular: `token` 32 bytes), `AUTH` = 10 (celular → PC: `token` 32 bytes), `DENIED` = 11 (PC → celular: `reason` u8: 1 = segredo inválido/expirado/usado; 2 = aparelho não pareado ou removido; 3 = versão incompatível).
- Na 38700 a primeira mensagem tem de ser `PAIR` ou `AUTH`; na 38701 tem de ser `HELLO`. `HELLO` com versão ≠ 2 → `DENIED(3)`. Depois de `DENIED` o PC fecha.
- Segredo de pareamento: 32 bytes aleatórios, válido por **2 minutos**, uso único, uma sessão ativa por vez (pedir de novo invalida a anterior). Chave de acesso: 32 bytes aleatórios; o PC guarda só o SHA-256 dela.
- Digital do PC: SHA-256 do certificado em DER, em **base64url sem padding** (no QR e no app). TXT do mDNS: `fp` = **16 primeiros caracteres hex minúsculos** do SHA-256.
- URI do QR: `screenshare://pair?h=<ip>&p=38700&fp=<digital base64url>&s=<segredo base64url>&n=<nome do PC, URL-encoded>`.
- Comparações de segredo, chave e digital em **tempo constante**.
- PC: `%APPDATA%\ScreenShare\host-cert.pfx` (ECDSA P-256, 20 anos), senha do PFX em `host-cert.key` protegida por DPAPI (usuário atual); `%APPDATA%\ScreenShare\paired-devices.json` com `{ id, name, tokenSha256, pairedAt }`, gravação atômica.
- Android: um único PC pareado `{ pcName, fingerprint, token, lastHost }` em DataStore; chave cifrada com AES-GCM via Android Keystore. **Não** usar EncryptedSharedPreferences.
- Identificadores em inglês; comentários e mensagens em pt-BR.
- Gradle no PowerShell: `$env:JAVA_HOME = 'C:\Program Files\Android\Android Studio\jbr'` antes do `gradlew`. O `android/local.properties` não vem no git: antes do primeiro gradle na worktree, copiar de `C:\Users\eduar\OneDrive\Documentos\GitHub\Screenshare\android\local.properties`.
- Commits terminam com uma linha `Co-Authored-By:` do Claude.

## Review Focus

1. **Peer que conecta na 38700 e fica calado** (ou não completa o TLS) → o PC desiste depois do timeout de handshake e continua atendendo outros. Teste na Tarefa 6.
2. **App antigo (v1)**: TCP puro na porta TLS → a conexão termina sem sessão e o servidor segue funcionando; `HELLO` v1 no USB → `DENIED(3)`. Testes na Tarefa 6.
3. **Chave do Android Keystore perdida** (reinstalação, backup restaurado em outro aparelho) → o pareamento salvo não decifra; o app não quebra, apaga o pareamento e pede para parear de novo. Teste na Tarefa 9.
4. **Outro PC (ou certificado regenerado) no mesmo IP** → o celular recusa o TLS e **não envia a chave**. Teste na Tarefa 10.
5. **QR de outro app ou malformado** → "QR inválido", nenhuma conexão. Testes nas Tarefas 8 e 11.

---

### Tarefa 1: Protocolo v2 em C# (mensagens, vetores, documento)

**Files:**
- Modify: `host/ScreenShare.Core/Protocol/Messages.cs`, `host/ScreenShare.Core/Protocol/MessageCodec.cs`
- Modify: `docs/protocol-vectors/hello.hex`; Create: `docs/protocol-vectors/{pair,paired,auth,denied}.hex`
- Modify: `docs/protocol.md`
- Test: `host/ScreenShare.Tests/Protocol/MessageCodecTests.cs`, `host/ScreenShare.Tests/Protocol/MessageReaderTests.cs`, `host/ScreenShare.Tests/DevHost/HostServerTests.cs`

**Interfaces:**
- Consumes: `MessageCodec`, `PayloadReader`, `PayloadWriter`, `Vectors.Load` (já existem).
- Produces: `MessageType.Pair = 8, Paired = 9, Auth = 10, Denied = 11`; `enum DeniedReason : byte { InvalidPairingSecret = 1, UnknownDevice = 2, IncompatibleVersion = 3 }`; records `PairMessage(byte[] Secret, string DeviceName)`, `PairedMessage(byte[] Token)`, `AuthMessage(byte[] Token)`, `DeniedMessage(DeniedReason Reason)`; constantes `MessageCodec.ProtocolVersion = 2`, `SecretLength = 32`, `TokenLength = 32`, `MaxDeviceNameBytes = 64`.

- [ ] **Step 1: Vetores**

`docs/protocol-vectors/hello.hex` — trocar só o cabeçalho de comentário e a linha da versão:
```text
# HELLO v2 — 2400x1080, 420 dpi, H.264 + H.265
01 09 00 00 00   # type=HELLO, length=9
02 00            # protocolVersion=2
60 09            # width=2400
38 04            # height=1080
A4 01            # densityDpi=420
03               # supportedCodecs=H264|H265
```

`docs/protocol-vectors/pair.hex`:
```text
# PAIR — segredo 00..1F, aparelho "Pixel 8"
08 28 00 00 00                                    # type=PAIR, length=40
00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F   # secret (1/2)
10 11 12 13 14 15 16 17 18 19 1A 1B 1C 1D 1E 1F   # secret (2/2)
07                                                # deviceNameLength=7
50 69 78 65 6C 20 38                              # deviceName="Pixel 8"
```

`docs/protocol-vectors/paired.hex`:
```text
# PAIRED — chave A0..BF
09 20 00 00 00                                    # type=PAIRED, length=32
A0 A1 A2 A3 A4 A5 A6 A7 A8 A9 AA AB AC AD AE AF   # token (1/2)
B0 B1 B2 B3 B4 B5 B6 B7 B8 B9 BA BB BC BD BE BF   # token (2/2)
```

`docs/protocol-vectors/auth.hex`:
```text
# AUTH — chave A0..BF
0A 20 00 00 00                                    # type=AUTH, length=32
A0 A1 A2 A3 A4 A5 A6 A7 A8 A9 AA AB AC AD AE AF   # token (1/2)
B0 B1 B2 B3 B4 B5 B6 B7 B8 B9 BA BB BC BD BE BF   # token (2/2)
```

`docs/protocol-vectors/denied.hex`:
```text
# DENIED — aparelho não pareado
0B 01 00 00 00   # type=DENIED, length=1
02               # reason=2 (aparelho não pareado ou removido)
```

- [ ] **Step 2: Testes que falham**

Nos testes existentes, trocar a versão do HELLO:
- `MessageCodecTests.cs`: em `Hello_matches_vector`, `new HelloMessage(1, 2400, ...)` → `new HelloMessage(2, 2400, ...)`.
- `MessageReaderTests.cs`: `new HelloMessage(1, 2400, ...)` → `new HelloMessage(2, 2400, ...)`.
- `HostServerTests.cs`: nas três chamadas `new HelloMessage(1, ...)` dos testes que esperam CONFIG, trocar `1` por `MessageCodec.ProtocolVersion`; no teste `Hello_with_incompatible_version_closes_the_connection`, trocar `new HelloMessage(2, ...)` por `new HelloMessage(1, ...)`. (A Tarefa 6 reescreve este arquivo.)

Acrescentar dentro da classe `MessageCodecTests`:
```csharp
    private static readonly byte[] Secret00To1F = Enumerable.Range(0x00, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] TokenA0ToBF = Enumerable.Range(0xA0, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void Pair_matches_vector()
    {
        var vector = Vectors.Load("pair.hex");
        var pair = new PairMessage(Secret00To1F, "Pixel 8");

        Assert.Equal(vector, MessageCodec.Encode(pair));
        var decoded = Assert.IsType<PairMessage>(DecodeVector(vector));
        Assert.Equal(pair with { Secret = decoded.Secret }, decoded);
        Assert.Equal(Secret00To1F, decoded.Secret);
    }

    [Fact]
    public void Paired_matches_vector()
    {
        var vector = Vectors.Load("paired.hex");

        Assert.Equal(vector, MessageCodec.Encode(new PairedMessage(TokenA0ToBF)));
        Assert.Equal(TokenA0ToBF, Assert.IsType<PairedMessage>(DecodeVector(vector)).Token);
    }

    [Fact]
    public void Auth_matches_vector()
    {
        var vector = Vectors.Load("auth.hex");

        Assert.Equal(vector, MessageCodec.Encode(new AuthMessage(TokenA0ToBF)));
        Assert.Equal(TokenA0ToBF, Assert.IsType<AuthMessage>(DecodeVector(vector)).Token);
    }

    [Fact]
    public void Denied_matches_vector()
    {
        var vector = Vectors.Load("denied.hex");
        var denied = new DeniedMessage(DeniedReason.UnknownDevice);

        Assert.Equal(vector, MessageCodec.Encode(denied));
        Assert.Equal(denied, DecodeVector(vector));
    }

    [Fact]
    public void Pair_with_empty_device_name_is_rejected()
    {
        var payload = new byte[32 + 1]; // segredo zerado + deviceNameLength=0
        Assert.Throws<ProtocolException>(() => MessageCodec.Decode((byte)MessageType.Pair, payload));
    }

    [Fact]
    public void Pair_with_invalid_utf8_device_name_is_rejected()
    {
        var payload = new byte[32 + 1 + 1];
        payload[32] = 1;    // deviceNameLength=1
        payload[33] = 0xFF; // byte que nunca aparece em UTF-8
        Assert.Throws<ProtocolException>(() => MessageCodec.Decode((byte)MessageType.Pair, payload));
    }

    [Fact]
    public void Encoding_device_name_over_64_bytes_is_rejected() =>
        Assert.Throws<ProtocolException>(() => MessageCodec.Encode(new PairMessage(Secret00To1F, new string('a', 65))));

    [Fact]
    public void Encoding_auth_with_wrong_token_length_is_rejected() =>
        Assert.Throws<ProtocolException>(() => MessageCodec.Encode(new AuthMessage(new byte[31])));

    [Fact]
    public void Auth_with_short_token_is_rejected() =>
        Assert.Throws<ProtocolException>(() => MessageCodec.Decode((byte)MessageType.Auth, new byte[31]));

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void Denied_with_unknown_reason_is_rejected(byte reason) =>
        Assert.Throws<ProtocolException>(() => MessageCodec.Decode((byte)MessageType.Denied, [reason]));
```

- [ ] **Step 3: Rodar e ver falhar**

Run: `dotnet test host`
Expected: FAIL na compilação — `PairMessage`, `PairedMessage`, `AuthMessage`, `DeniedMessage`, `DeniedReason` não encontrados.

- [ ] **Step 4: Implementar**

`Messages.cs` — no `enum MessageType`, depois de `KeyframeRequest = 7,`:
```csharp
    Pair = 8,
    Paired = 9,
    Auth = 10,
    Denied = 11,
```
e no fim do arquivo:
```csharp
/// <summary>Motivo de um DENIED.</summary>
public enum DeniedReason : byte
{
    /// <summary>Segredo de pareamento inválido, expirado ou já usado.</summary>
    InvalidPairingSecret = 1,
    /// <summary>Chave de acesso desconhecida: nunca pareado ou removido.</summary>
    UnknownDevice = 2,
    /// <summary>HELLO com protocolVersion diferente da do PC.</summary>
    IncompatibleVersion = 3,
}

/// <summary>Celular → PC, só na porta Wi-Fi: primeiro contato vindo do QR. Secret tem 32 bytes.</summary>
public sealed record PairMessage(byte[] Secret, string DeviceName) : Message;

/// <summary>PC → celular: chave de acesso (32 bytes) gerada no pareamento.</summary>
public sealed record PairedMessage(byte[] Token) : Message;

/// <summary>Celular → PC, só na porta Wi-Fi: apresenta a chave de acesso antes do HELLO.</summary>
public sealed record AuthMessage(byte[] Token) : Message;

/// <summary>PC → celular: recusa; o PC fecha a conexão em seguida.</summary>
public sealed record DeniedMessage(DeniedReason Reason) : Message;
```

`MessageCodec.cs`:
1. Acrescentar `using System.Text;` no topo.
2. Trocar `public const ushort ProtocolVersion = 1;` por `public const ushort ProtocolVersion = 2;` e, logo abaixo de `MaxTouchPointers`, acrescentar:
```csharp
    public const int SecretLength = 32;
    public const int TokenLength = 32;
    public const int MaxDeviceNameBytes = 64;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
```
3. Em `Encode`, logo antes de `case PingMessage m:`:
```csharp
            case PairMessage m:
            {
                RequireLength(m.Secret, SecretLength, "segredo");
                var name = EncodeDeviceName(m.DeviceName);
                var w = Begin(MessageType.Pair, SecretLength + 1 + name.Length, out var bytes);
                w.WriteBytes(m.Secret);
                w.WriteByte((byte)name.Length);
                w.WriteBytes(name);
                return bytes;
            }
            case PairedMessage m:
            {
                RequireLength(m.Token, TokenLength, "chave");
                var w = Begin(MessageType.Paired, TokenLength, out var bytes);
                w.WriteBytes(m.Token);
                return bytes;
            }
            case AuthMessage m:
            {
                RequireLength(m.Token, TokenLength, "chave");
                var w = Begin(MessageType.Auth, TokenLength, out var bytes);
                w.WriteBytes(m.Token);
                return bytes;
            }
            case DeniedMessage m:
            {
                var w = Begin(MessageType.Denied, 1, out var bytes);
                w.WriteByte((byte)m.Reason);
                return bytes;
            }
```
4. No `switch` de `Decode`, logo antes de `MessageType.Ping =>`:
```csharp
            MessageType.Pair => DecodePair(ref r),
            MessageType.Paired => new PairedMessage(r.ReadBytes(TokenLength)),
            MessageType.Auth => new AuthMessage(r.ReadBytes(TokenLength)),
            MessageType.Denied => DecodeDenied(ref r),
```
5. No fim da classe:
```csharp
    private static void RequireLength(byte[] value, int length, string what)
    {
        if (value.Length != length)
            throw new ProtocolException($"O {what} precisa ter {length} bytes, tem {value.Length}.");
    }

    private static byte[] EncodeDeviceName(string name)
    {
        byte[] bytes;
        try
        {
            bytes = StrictUtf8.GetBytes(name);
        }
        catch (EncoderFallbackException)
        {
            throw new ProtocolException("Nome do aparelho não é texto válido.");
        }
        if (bytes.Length is < 1 or > MaxDeviceNameBytes)
            throw new ProtocolException($"Nome do aparelho precisa ter 1..{MaxDeviceNameBytes} bytes, tem {bytes.Length}.");
        return bytes;
    }

    private static PairMessage DecodePair(ref PayloadReader r)
    {
        var secret = r.ReadBytes(SecretLength);
        var nameLength = r.ReadByte();
        if (nameLength is < 1 or > MaxDeviceNameBytes)
            throw new ProtocolException($"Nome do aparelho precisa ter 1..{MaxDeviceNameBytes} bytes, tem {nameLength}.");
        var nameBytes = r.ReadBytes(nameLength);
        try
        {
            return new PairMessage(secret, StrictUtf8.GetString(nameBytes));
        }
        catch (DecoderFallbackException)
        {
            throw new ProtocolException("Nome do aparelho não é UTF-8 válido.");
        }
    }

    private static DeniedMessage DecodeDenied(ref PayloadReader r)
    {
        var reason = r.ReadByte();
        if (reason is < 1 or > 3)
            throw new ProtocolException($"Motivo de DENIED desconhecido: {reason}.");
        return new DeniedMessage((DeniedReason)reason);
    }
```

`docs/protocol.md`:
1. Título: `# Protocolo ScreenShare — versão 1` → `# Protocolo ScreenShare — versão 2`.
2. Substituir o primeiro parágrafo (o que começa com "Uma conexão TCP entre o app Android…") por:
```markdown
Conexões TCP entre o app Android (cliente) e o host Windows (servidor). Inteiros são **little-endian**; `f32` é IEEE 754 de 32 bits little-endian.

| Porta | Uso | Escuta em | Transporte |
|---|---|---|---|
| 38700 | Wi-Fi | todas as interfaces | TLS obrigatório, certificado autoassinado do PC fixado pelo celular no pareamento |
| 38701 | USB (`adb reverse tcp:38701 tcp:38701`) | só `127.0.0.1` | TCP puro |

As mensagens são as mesmas nas duas portas; muda só o que vem antes do `HELLO` (ver Sequência). Detalhes do pareamento: `docs/superpowers/specs/2026-10-03-pareamento-autenticacao-design.md`.
```
3. Substituir a seção `## Sequência` inteira por:
```markdown
## Sequência

**Wi-Fi (38700), primeiro uso — pareamento pelo QR:** TLS → `PAIR` → `PAIRED` → `AUTH` → `HELLO` → `CONFIG`.
**Wi-Fi (38700), já pareado:** TLS → `AUTH` → `HELLO` → `CONFIG`.
**USB (38701):** `HELLO` → `CONFIG`.

Depois do `CONFIG`: o PC envia `FRAME`s (o primeiro é keyframe) e, a qualquer momento, vêm `TOUCH`, `PING`/`PONG`, `KEYFRAME_REQ`.

Regras:
- Na 38700 a primeira mensagem tem de ser `PAIR` ou `AUTH`; depois de `PAIR`/`PAIRED` vem `AUTH`. Qualquer outra coisa → `DENIED` e fechamento.
- Na 38701 a primeira mensagem tem de ser `HELLO`.
- `HELLO` com `protocolVersion` ≠ 2 → `DENIED(3)` e fechamento.
- Depois de `DENIED` o PC fecha a conexão.
```
4. Na tabela `## Mensagens`, depois da linha de `KEYFRAME_REQ`, acrescentar:
```markdown
| 8 | PAIR | celular → PC | 33 + n |
| 9 | PAIRED | PC → celular | 32 |
| 10 | AUTH | celular → PC | 32 |
| 11 | DENIED | PC → celular | 1 |
```
5. Na tabela do `### HELLO`, a linha de `protocolVersion` passa a: `| protocolVersion | u16 | 2 (o layout de 9 bytes do HELLO é congelado em todas as versões) |`.
6. Depois da seção `### KEYFRAME_REQ`, acrescentar:
```markdown
### PAIR
| Campo | Tipo | Observação |
|---|---|---|
| secret | 32 bytes | segredo de pareamento lido do QR (válido por 2 minutos, uso único) |
| deviceNameLength | u8 | 1 a 64 |
| deviceName | UTF-8 | nome do celular, mostrado no PC |

### PAIRED
| Campo | Tipo | Observação |
|---|---|---|
| token | 32 bytes | chave de acesso; o celular guarda, o PC guarda só o SHA-256 |

### AUTH
| Campo | Tipo | Observação |
|---|---|---|
| token | 32 bytes | chave de acesso recebida no PAIRED |

### DENIED
| Campo | Tipo | Observação |
|---|---|---|
| reason | u8 | 1 = segredo inválido, expirado ou usado; 2 = aparelho não pareado ou removido; 3 = versão incompatível |
```
7. Na seção `## Descoberta (Wi-Fi)`, acrescentar ao fim do parágrafo: ` O TXT do anúncio traz \`fp\` = 16 primeiros caracteres hex (minúsculos) do SHA-256 do certificado do PC, para o celular reconhecer o PC pareado mesmo se o IP mudar. No USB a porta é a 38701.`
8. Na tabela de vetores: a linha do `hello.hex` passa a `| hello.hex | HELLO v2, 2400×1080, 420 dpi, H.264 + H.265 |` e acrescentar:
```markdown
| pair.hex | PAIR com segredo 00..1F e aparelho "Pixel 8" |
| paired.hex | PAIRED com chave A0..BF |
| auth.hex | AUTH com chave A0..BF |
| denied.hex | DENIED motivo 2 |
```

- [ ] **Step 5: Rodar e ver passar**

Run: `dotnet test host`
Expected: PASS — todos aprovados (35 anteriores + 11 novos = 46), 0 avisos de compilação.

- [ ] **Step 6: Commit**

```powershell
git add host docs/protocol.md docs/protocol-vectors
git commit -m "feat(core): protocolo v2 com PAIR, PAIRED, AUTH e DENIED" -m "Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Tarefa 2: Protocolo v2 em Kotlin

**Files:**
- Modify: `android/app/src/main/java/dev/screenshare/android/protocol/Messages.kt`, `.../protocol/MessageCodec.kt`
- Test: `android/app/src/test/java/dev/screenshare/android/protocol/MessageCodecTest.kt`, `.../protocol/MessageReaderTest.kt`

**Interfaces:**
- Consumes: vetores da Tarefa 1.
- Produces: `MessageType.PAIR = 8, PAIRED = 9, AUTH = 10, DENIED = 11`; `enum class DeniedReason(val code: Int) { INVALID_PAIRING_SECRET(1), UNKNOWN_DEVICE(2), INCOMPATIBLE_VERSION(3) }`; `PairMessage(secret: ByteArray, deviceName: String)`, `PairedMessage(token: ByteArray)`, `AuthMessage(token: ByteArray)`, `DeniedMessage(reason: DeniedReason)`; `MessageCodec.PROTOCOL_VERSION = 2`, `SECRET_LENGTH = 32`, `TOKEN_LENGTH = 32`, `MAX_DEVICE_NAME_BYTES = 64`.

- [ ] **Step 1: Testes que falham**

Antes do primeiro gradle na worktree: `Copy-Item C:\Users\eduar\OneDrive\Documentos\GitHub\Screenshare\android\local.properties android\local.properties`.

- `MessageCodecTest.kt`: em `hello()`, `HelloMessage(1, 2400, ...)` → `HelloMessage(2, 2400, ...)`.
- `MessageReaderTest.kt`: `HelloMessage(1, 2400, 1080, 420, VideoCodec.ALL)` → `HelloMessage(2, 2400, 1080, 420, VideoCodec.ALL)`.

Acrescentar dentro de `MessageCodecTest`:
```kotlin
    private val secret = ByteArray(32) { it.toByte() }
    private val token = ByteArray(32) { (0xA0 + it).toByte() }

    @Test
    fun pair() = assertMatchesVector("pair.hex", PairMessage(secret, "Pixel 8"))

    @Test
    fun paired() = assertMatchesVector("paired.hex", PairedMessage(token))

    @Test
    fun auth() = assertMatchesVector("auth.hex", AuthMessage(token))

    @Test
    fun denied() = assertMatchesVector("denied.hex", DeniedMessage(DeniedReason.UNKNOWN_DEVICE))

    @Test
    fun pairWithEmptyDeviceNameIsRejected() = assertRejected(MessageType.PAIR, ByteArray(32 + 1))

    @Test
    fun pairWithInvalidUtf8DeviceNameIsRejected() =
        assertRejected(MessageType.PAIR, ByteArray(32 + 2).also { it[32] = 1; it[33] = 0xFF.toByte() })

    @Test
    fun encodingDeviceNameOver64BytesIsRejected() {
        assertThrows(ProtocolException::class.java) { MessageCodec.encode(PairMessage(secret, "a".repeat(65))) }
    }

    @Test
    fun encodingAuthWithWrongTokenLengthIsRejected() {
        assertThrows(ProtocolException::class.java) { MessageCodec.encode(AuthMessage(ByteArray(31))) }
    }

    @Test
    fun authWithShortTokenIsRejected() = assertRejected(MessageType.AUTH, ByteArray(31))

    @Test
    fun deniedWithUnknownReasonIsRejected() {
        for (reason in listOf(0, 4)) assertRejected(MessageType.DENIED, byteArrayOf(reason.toByte()))
    }
```

- [ ] **Step 2: Rodar e ver falhar**

```powershell
$env:JAVA_HOME = 'C:\Program Files\Android\Android Studio\jbr'; .\android\gradlew.bat -p android :app:testDebugUnitTest
```
Expected: FAIL na compilação — `Unresolved reference 'PairMessage'` (e similares).

- [ ] **Step 3: Implementar**

`Messages.kt` — no `object MessageType`, depois de `KEYFRAME_REQUEST = 7`:
```kotlin
    const val PAIR = 8
    const val PAIRED = 9
    const val AUTH = 10
    const val DENIED = 11
```
e no fim do arquivo:
```kotlin
/** Motivo de um DENIED. */
enum class DeniedReason(val code: Int) {
    /** Segredo de pareamento inválido, expirado ou já usado. */
    INVALID_PAIRING_SECRET(1),

    /** Chave de acesso desconhecida: nunca pareado ou removido no PC. */
    UNKNOWN_DEVICE(2),

    /** HELLO com protocolVersion diferente da do PC. */
    INCOMPATIBLE_VERSION(3),
}

/** Celular → PC, só no Wi-Fi: primeiro contato vindo do QR. secret tem 32 bytes. */
data class PairMessage(val secret: ByteArray, val deviceName: String) : Message {
    override fun equals(other: Any?) =
        other is PairMessage && secret.contentEquals(other.secret) && deviceName == other.deviceName

    override fun hashCode() = Objects.hash(secret.contentHashCode(), deviceName)
}

/** PC → celular: chave de acesso (32 bytes) gerada no pareamento. */
data class PairedMessage(val token: ByteArray) : Message {
    override fun equals(other: Any?) = other is PairedMessage && token.contentEquals(other.token)
    override fun hashCode() = token.contentHashCode()
}

/** Celular → PC, só no Wi-Fi: apresenta a chave de acesso antes do HELLO. */
data class AuthMessage(val token: ByteArray) : Message {
    override fun equals(other: Any?) = other is AuthMessage && token.contentEquals(other.token)
    override fun hashCode() = token.contentHashCode()
}

/** PC → celular: recusa; o PC fecha a conexão em seguida. */
data class DeniedMessage(val reason: DeniedReason) : Message
```

`MessageCodec.kt`:
1. Imports: `java.nio.CharBuffer`, `java.nio.charset.CharacterCodingException`, `java.nio.charset.CodingErrorAction`.
2. `const val PROTOCOL_VERSION = 1` → `const val PROTOCOL_VERSION = 2`; abaixo de `MAX_TOUCH_POINTERS`:
```kotlin
    const val SECRET_LENGTH = 32
    const val TOKEN_LENGTH = 32
    const val MAX_DEVICE_NAME_BYTES = 64
```
3. No `when` de `encode`, logo antes de `is PingMessage ->`:
```kotlin
        is PairMessage -> {
            requireLength(message.secret, SECRET_LENGTH, "segredo")
            val name = encodeDeviceName(message.deviceName)
            frame(MessageType.PAIR, SECRET_LENGTH + 1 + name.size) {
                put(message.secret)
                put(name.size.toByte())
                put(name)
            }
        }
        is PairedMessage -> {
            requireLength(message.token, TOKEN_LENGTH, "chave")
            frame(MessageType.PAIRED, TOKEN_LENGTH) { put(message.token) }
        }
        is AuthMessage -> {
            requireLength(message.token, TOKEN_LENGTH, "chave")
            frame(MessageType.AUTH, TOKEN_LENGTH) { put(message.token) }
        }
        is DeniedMessage -> frame(MessageType.DENIED, 1) { put(message.reason.code.toByte()) }
```
4. No `when` de `decode`, logo antes de `MessageType.PING ->`:
```kotlin
            MessageType.PAIR -> decodePair(r)
            MessageType.PAIRED -> PairedMessage(r.bytes(TOKEN_LENGTH))
            MessageType.AUTH -> AuthMessage(r.bytes(TOKEN_LENGTH))
            MessageType.DENIED -> decodeDenied(r)
```
5. No fim do `object`:
```kotlin
    private fun requireLength(value: ByteArray, length: Int, what: String) {
        if (value.size != length) throw ProtocolException("o $what precisa ter $length bytes, tem ${value.size}")
    }

    private fun encodeDeviceName(name: String): ByteArray {
        val buffer = try {
            Charsets.UTF_8.newEncoder()
                .onMalformedInput(CodingErrorAction.REPORT)
                .onUnmappableCharacter(CodingErrorAction.REPORT)
                .encode(CharBuffer.wrap(name))
        } catch (e: CharacterCodingException) {
            throw ProtocolException("nome do aparelho não é texto válido")
        }
        val bytes = ByteArray(buffer.remaining()).also { buffer.get(it) }
        if (bytes.size !in 1..MAX_DEVICE_NAME_BYTES) {
            throw ProtocolException("nome do aparelho precisa ter 1..$MAX_DEVICE_NAME_BYTES bytes, tem ${bytes.size}")
        }
        return bytes
    }

    private fun decodePair(r: PayloadReader): PairMessage {
        val secret = r.bytes(SECRET_LENGTH)
        val nameLength = r.u8()
        if (nameLength !in 1..MAX_DEVICE_NAME_BYTES) {
            throw ProtocolException("nome do aparelho precisa ter 1..$MAX_DEVICE_NAME_BYTES bytes, tem $nameLength")
        }
        val name = try {
            Charsets.UTF_8.newDecoder()
                .onMalformedInput(CodingErrorAction.REPORT)
                .onUnmappableCharacter(CodingErrorAction.REPORT)
                .decode(ByteBuffer.wrap(r.bytes(nameLength)))
                .toString()
        } catch (e: CharacterCodingException) {
            throw ProtocolException("nome do aparelho não é UTF-8 válido")
        }
        return PairMessage(secret, name)
    }

    private fun decodeDenied(r: PayloadReader): DeniedMessage {
        val code = r.u8()
        val reason = DeniedReason.entries.firstOrNull { it.code == code }
            ?: throw ProtocolException("motivo de DENIED desconhecido: $code")
        return DeniedMessage(reason)
    }
```

- [ ] **Step 4: Rodar e ver passar**

Mesmo comando do Step 2. Expected: `BUILD SUCCESSFUL`, todos os testes passando (MessageCodecTest 29, MessageReaderTest 5, ConnectionTest 6, HostAddressTest 8).

- [ ] **Step 5: Commit**

```powershell
git add android/app/src
git commit -m "feat(android): protocolo v2 com PAIR, PAIRED, AUTH e DENIED" -m "Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Tarefa 3: `HostIdentity` — certificado e digital do PC (C#)

**Files:**
- Modify: `host/ScreenShare.Core/ScreenShare.Core.csproj` (pacote `System.Security.Cryptography.ProtectedData` 10.0.0)
- Create: `host/ScreenShare.Core/Security/HostIdentity.cs`
- Test: `host/ScreenShare.Tests/Security/HostIdentityTests.cs`

**Interfaces:**
- Produces: `sealed class HostIdentity : IDisposable` com `static HostIdentity LoadOrCreate(string directory, string commonName)`, `X509Certificate2 Certificate`, `string Fingerprint` (base64url do SHA-256 do DER), `string MdnsId` (16 hex minúsculos), `static string ComputeFingerprint(ReadOnlySpan<byte> der)`, `static string MdnsIdOf(string fingerprint)`.

- [ ] **Step 1: Teste que falha**

`host/ScreenShare.Tests/Security/HostIdentityTests.cs`:
```csharp
using ScreenShare.Core.Security;

namespace ScreenShare.Tests.Security;

public sealed class HostIdentityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screenshare-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Creates_a_certificate_once_and_reloads_the_same_fingerprint()
    {
        string first;
        using (var identity = HostIdentity.LoadOrCreate(_dir, "PC de Teste"))
        {
            first = identity.Fingerprint;
            Assert.True(identity.Certificate.HasPrivateKey);
            Assert.Equal("CN=PC de Teste", identity.Certificate.Subject);
        }

        using var reloaded = HostIdentity.LoadOrCreate(_dir, "PC de Teste");

        Assert.Equal(first, reloaded.Fingerprint);
        Assert.True(File.Exists(Path.Combine(_dir, "host-cert.pfx")));
        Assert.True(File.Exists(Path.Combine(_dir, "host-cert.key")));
    }

    [Fact]
    public void Deleting_the_certificate_creates_a_new_identity()
    {
        string first;
        using (var identity = HostIdentity.LoadOrCreate(_dir, "PC")) first = identity.Fingerprint;
        File.Delete(Path.Combine(_dir, "host-cert.pfx"));

        using var regenerated = HostIdentity.LoadOrCreate(_dir, "PC");

        Assert.NotEqual(first, regenerated.Fingerprint);
    }

    [Fact]
    public void Fingerprint_is_base64url_of_the_sha256_of_the_certificate()
    {
        using var identity = HostIdentity.LoadOrCreate(_dir, "PC");

        Assert.Equal(43, identity.Fingerprint.Length); // 32 bytes em base64url sem padding
        Assert.Equal(identity.Fingerprint, HostIdentity.ComputeFingerprint(identity.Certificate.RawData));
        Assert.DoesNotContain('+', identity.Fingerprint);
        Assert.DoesNotContain('/', identity.Fingerprint);
        Assert.DoesNotContain('=', identity.Fingerprint);
    }

    [Fact]
    public void Mdns_id_is_the_lowercase_hex_of_the_first_eight_bytes()
    {
        // bytes 0x20..0x3F em base64url (mesmo exemplo do teste Kotlin)
        Assert.Equal("2021222324252627", HostIdentity.MdnsIdOf("ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8"));

        using var identity = HostIdentity.LoadOrCreate(_dir, "PC");
        Assert.Equal(HostIdentity.MdnsIdOf(identity.Fingerprint), identity.MdnsId);
    }
}
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet test host --filter "FullyQualifiedName~HostIdentityTests"`
Expected: FAIL na compilação — namespace `ScreenShare.Core.Security` não existe.

- [ ] **Step 3: Implementar**

Em `ScreenShare.Core.csproj`, acrescentar:
```xml
  <ItemGroup>
    <PackageReference Include="System.Security.Cryptography.ProtectedData" Version="10.0.0" />
  </ItemGroup>
```

`host/ScreenShare.Core/Security/HostIdentity.cs`:
```csharp
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ScreenShare.Core.Security;

/// <summary>
/// Certificado autoassinado do PC (ECDSA P-256), criado na primeira execução e reaproveitado depois.
/// O celular fixa a digital (SHA-256 do certificado) no pareamento e recusa qualquer outro certificado.
/// </summary>
public sealed class HostIdentity : IDisposable
{
    private const string CertFileName = "host-cert.pfx";
    private const string KeyFileName = "host-cert.key";

    private HostIdentity(X509Certificate2 certificate)
    {
        Certificate = certificate;
        Fingerprint = ComputeFingerprint(certificate.RawData);
        MdnsId = MdnsIdOf(Fingerprint);
    }

    public X509Certificate2 Certificate { get; }

    /// <summary>SHA-256 do certificado em DER, em base64url sem padding (vai no QR).</summary>
    public string Fingerprint { get; }

    /// <summary>16 primeiros caracteres hex do SHA-256 (vai no TXT `fp` do mDNS).</summary>
    public string MdnsId { get; }

    /// <summary>Carrega o certificado de <paramref name="directory"/> ou cria um novo se não existir.</summary>
    public static HostIdentity LoadOrCreate(string directory, string commonName)
    {
        Directory.CreateDirectory(directory);
        var certPath = Path.Combine(directory, CertFileName);
        var keyPath = Path.Combine(directory, KeyFileName);
        if (!File.Exists(certPath) || !File.Exists(keyPath))
            Create(certPath, keyPath, commonName);

        var password = Encoding.UTF8.GetString(Unprotect(File.ReadAllBytes(keyPath)));
        return new HostIdentity(X509CertificateLoader.LoadPkcs12FromFile(certPath, password));
    }

    public static string ComputeFingerprint(ReadOnlySpan<byte> der) => Base64Url.EncodeToString(SHA256.HashData(der));

    public static string MdnsIdOf(string fingerprint) =>
        Convert.ToHexStringLower(Base64Url.DecodeFromChars(fingerprint).AsSpan(0, 8));

    public void Dispose() => Certificate.Dispose();

    private static void Create(string certPath, string keyPath, string commonName)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var name = new X500DistinguishedNameBuilder();
        name.AddCommonName(commonName);
        var request = new CertificateRequest(name.Build(), key, HashAlgorithmName.SHA256);
        var now = DateTimeOffset.UtcNow;
        using var certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(20));

        // Senha aleatória para o PFX, guardada protegida pelo DPAPI do usuário atual.
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        File.WriteAllBytes(certPath, certificate.Export(X509ContentType.Pfx, password));
        File.WriteAllBytes(keyPath, Protect(Encoding.UTF8.GetBytes(password)));
    }

    private static byte[] Protect(byte[] data)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("O host do ScreenShare só roda no Windows.");
        return ProtectedData.Protect(data, optionalEntropy: null, DataProtectionScope.CurrentUser);
    }

    private static byte[] Unprotect(byte[] data)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("O host do ScreenShare só roda no Windows.");
        return ProtectedData.Unprotect(data, optionalEntropy: null, DataProtectionScope.CurrentUser);
    }
}
```

- [ ] **Step 4: Rodar e ver passar**

Run: `dotnet test host` — Expected: PASS, 0 avisos (o guard `OperatingSystem.IsWindows()` evita o CA1416).

- [ ] **Step 5: Commit**

```powershell
git add host
git commit -m "feat(core): identidade do PC com certificado autoassinado e digital" -m "Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Tarefa 4: `PairingSession` e `PairingUri` (C#)

**Files:**
- Create: `host/ScreenShare.Core/Security/PairingSession.cs`, `host/ScreenShare.Core/Security/PairingUri.cs`
- Create: `docs/protocol-vectors/pairing-uri.txt`
- Modify: `host/ScreenShare.Tests/ScreenShare.Tests.csproj`, `host/ScreenShare.Tests/Protocol/Vectors.cs`
- Test: `host/ScreenShare.Tests/Security/PairingSessionTests.cs`, `host/ScreenShare.Tests/Security/PairingUriTests.cs`, `host/ScreenShare.Tests/Security/ManualClock.cs`

**Interfaces:**
- Consumes: `MessageCodec.SecretLength`.
- Produces: `sealed class PairingSession(TimeProvider clock)` com `static readonly TimeSpan Lifetime` (2 min), `byte[] Begin()`, `bool TryConsume(ReadOnlySpan<byte> secret)`; `static class PairingUri` com `string Build(string host, int port, string fingerprint, ReadOnlySpan<byte> secret, string pcName)`; teste: `ManualClock : TimeProvider` com `DateTimeOffset Now`; `Vectors.Text(string name)`.

- [ ] **Step 1: Vetor e testes que falham**

`docs/protocol-vectors/pairing-uri.txt` (uma linha só):
```text
screenshare://pair?h=192.168.0.10&p=38700&fp=ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8&s=AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8&n=PC%20da%20Sala
```

Em `ScreenShare.Tests.csproj`, no `ItemGroup` dos vetores, acrescentar abaixo da linha dos `*.hex`:
```xml
    <None Include="..\..\docs\protocol-vectors\*.txt" LinkBase="protocol-vectors" CopyToOutputDirectory="PreserveNewest" />
```

Em `Vectors.cs`, acrescentar dentro da classe:
```csharp
    public static string Text(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "protocol-vectors", name)).Trim();
```

`host/ScreenShare.Tests/Security/ManualClock.cs`:
```csharp
namespace ScreenShare.Tests.Security;

/// <summary>Relógio que só anda quando o teste manda.</summary>
internal sealed class ManualClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}
```

`host/ScreenShare.Tests/Security/PairingSessionTests.cs`:
```csharp
using ScreenShare.Core.Security;

namespace ScreenShare.Tests.Security;

public class PairingSessionTests
{
    private readonly ManualClock _clock = new();

    [Fact]
    public void Begin_returns_a_32_byte_secret_that_is_accepted_once()
    {
        var session = new PairingSession(_clock);
        var secret = session.Begin();

        Assert.Equal(32, secret.Length);
        Assert.True(session.TryConsume(secret));
        Assert.False(session.TryConsume(secret)); // uso único
    }

    [Fact]
    public void Wrong_secret_is_rejected_and_the_right_one_still_works()
    {
        var session = new PairingSession(_clock);
        var secret = session.Begin();

        Assert.False(session.TryConsume(new byte[32]));
        Assert.True(session.TryConsume(secret));
    }

    [Fact]
    public void Secret_expires_after_two_minutes()
    {
        var session = new PairingSession(_clock);
        var secret = session.Begin();

        _clock.Now += PairingSession.Lifetime;

        Assert.False(session.TryConsume(secret));
    }

    [Fact]
    public void Secret_is_still_valid_just_before_expiring()
    {
        var session = new PairingSession(_clock);
        var secret = session.Begin();

        _clock.Now += PairingSession.Lifetime - TimeSpan.FromSeconds(1);

        Assert.True(session.TryConsume(secret));
    }

    [Fact]
    public void Beginning_again_invalidates_the_previous_secret()
    {
        var session = new PairingSession(_clock);
        var old = session.Begin();
        var current = session.Begin();

        Assert.False(session.TryConsume(old));
        Assert.True(session.TryConsume(current));
    }

    [Fact]
    public void Nothing_is_accepted_before_begin() =>
        Assert.False(new PairingSession(_clock).TryConsume(new byte[32]));

    [Fact]
    public void Secret_of_wrong_length_is_rejected()
    {
        var session = new PairingSession(_clock);
        var secret = session.Begin();

        Assert.False(session.TryConsume(secret.AsSpan(0, 31)));
        Assert.True(session.TryConsume(secret));
    }
}
```

`host/ScreenShare.Tests/Security/PairingUriTests.cs`:
```csharp
using ScreenShare.Core.Security;
using ScreenShare.Tests.Protocol;

namespace ScreenShare.Tests.Security;

public class PairingUriTests
{
    [Fact]
    public void Builds_the_shared_example()
    {
        var secret = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

        var uri = PairingUri.Build("192.168.0.10", 38700, "ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8", secret, "PC da Sala");

        Assert.Equal(Vectors.Text("pairing-uri.txt"), uri);
    }

    [Fact]
    public void Escapes_special_characters_in_the_pc_name() =>
        Assert.EndsWith("&n=PC%26Casa%3DSala",
            PairingUri.Build("10.0.0.2", 38700, "fp", new byte[32], "PC&Casa=Sala"));
}
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet test host --filter "FullyQualifiedName~Security"`
Expected: FAIL na compilação — `PairingSession` e `PairingUri` não existem.

- [ ] **Step 3: Implementar**

`host/ScreenShare.Core/Security/PairingSession.cs`:
```csharp
using System.Security.Cryptography;
using ScreenShare.Core.Protocol;

namespace ScreenShare.Core.Security;

/// <summary>
/// Segredo de pareamento mostrado no QR: 32 bytes aleatórios, válido por <see cref="Lifetime"/>, uso único.
/// Só existe uma sessão por vez: <see cref="Begin"/> de novo invalida a anterior.
/// </summary>
public sealed class PairingSession(TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    private readonly Lock _lock = new();
    private byte[]? _secret;
    private DateTimeOffset _expiresAt;

    /// <summary>Gera um segredo novo (invalidando o anterior) e devolve uma cópia para pôr no QR.</summary>
    public byte[] Begin()
    {
        lock (_lock)
        {
            _secret = RandomNumberGenerator.GetBytes(MessageCodec.SecretLength);
            _expiresAt = clock.GetUtcNow() + Lifetime;
            return (byte[])_secret.Clone();
        }
    }

    /// <summary>True se <paramref name="secret"/> é o segredo atual e ainda vale; nesse caso a sessão é consumida.</summary>
    public bool TryConsume(ReadOnlySpan<byte> secret)
    {
        lock (_lock)
        {
            if (_secret is null) return false;
            if (clock.GetUtcNow() >= _expiresAt)
            {
                _secret = null;
                return false;
            }
            if (!CryptographicOperations.FixedTimeEquals(_secret, secret)) return false;
            _secret = null;
            return true;
        }
    }
}
```

`host/ScreenShare.Core/Security/PairingUri.cs`:
```csharp
using System.Buffers.Text;

namespace ScreenShare.Core.Security;

/// <summary>Monta a URI que vai no QR de pareamento (formato em docs/protocol-vectors/pairing-uri.txt).</summary>
public static class PairingUri
{
    public static string Build(string host, int port, string fingerprint, ReadOnlySpan<byte> secret, string pcName) =>
        $"screenshare://pair?h={Uri.EscapeDataString(host)}&p={port}&fp={fingerprint}" +
        $"&s={Base64Url.EncodeToString(secret)}&n={Uri.EscapeDataString(pcName)}";
}
```

- [ ] **Step 4: Rodar e ver passar**

Run: `dotnet test host` — Expected: PASS, 0 avisos.

- [ ] **Step 5: Commit**

```powershell
git add host docs/protocol-vectors/pairing-uri.txt
git commit -m "feat(core): sessão de pareamento e URI do QR" -m "Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Tarefa 5: `DeviceRegistry` — aparelhos pareados (C#)

**Files:**
- Create: `host/ScreenShare.Core/Security/DeviceRegistry.cs`
- Test: `host/ScreenShare.Tests/Security/DeviceRegistryTests.cs`

**Interfaces:**
- Consumes: `MessageCodec.TokenLength`, `ManualClock` (teste, Tarefa 4).
- Produces: `sealed record PairedDevice(string Id, string Name, string TokenSha256, DateTimeOffset PairedAt)`; `sealed class DeviceRegistry(string path, TimeProvider? clock = null, Action<string>? log = null)` com `IReadOnlyList<PairedDevice> Devices`, `(PairedDevice Device, byte[] Token) Add(string name)`, `PairedDevice? Authenticate(ReadOnlySpan<byte> token)`, `bool Remove(string id)`.

- [ ] **Step 1: Teste que falha**

`host/ScreenShare.Tests/Security/DeviceRegistryTests.cs`:
```csharp
using ScreenShare.Core.Security;

namespace ScreenShare.Tests.Security;

public sealed class DeviceRegistryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screenshare-tests", Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "paired-devices.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Added_device_authenticates_with_its_token()
    {
        var clock = new ManualClock();
        var registry = new DeviceRegistry(FilePath, clock);

        var (device, token) = registry.Add("Pixel 8");

        Assert.Equal(32, token.Length);
        Assert.Equal(device, registry.Authenticate(token));
        Assert.Equal("Pixel 8", device.Name);
        Assert.Equal(clock.Now, device.PairedAt);
    }

    [Fact]
    public void Unknown_or_malformed_token_does_not_authenticate()
    {
        var registry = new DeviceRegistry(FilePath);
        registry.Add("Pixel 8");

        Assert.Null(registry.Authenticate(new byte[32]));
        Assert.Null(registry.Authenticate(new byte[31]));
    }

    [Fact]
    public void Removed_device_no_longer_authenticates()
    {
        var registry = new DeviceRegistry(FilePath);
        var (device, token) = registry.Add("Pixel 8");

        Assert.True(registry.Remove(device.Id));

        Assert.Null(registry.Authenticate(token));
        Assert.Empty(registry.Devices);
        Assert.False(registry.Remove(device.Id));
    }

    [Fact]
    public void Devices_survive_a_restart()
    {
        var (device, token) = new DeviceRegistry(FilePath).Add("Pixel 8");

        var reloaded = new DeviceRegistry(FilePath);

        Assert.Equal(device, reloaded.Authenticate(token));
        Assert.Equal(new[] { device }, reloaded.Devices);
    }

    [Fact]
    public void File_stores_only_the_token_hash()
    {
        var (_, token) = new DeviceRegistry(FilePath).Add("Pixel 8");

        var json = File.ReadAllText(FilePath);

        Assert.DoesNotContain(Convert.ToHexString(token), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToBase64String(token), json);
        Assert.Contains("tokenSha256", json);
    }

    [Fact]
    public void Corrupted_file_starts_empty_keeps_a_backup_and_logs()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ isso não é json");
        var logs = new List<string>();

        var registry = new DeviceRegistry(FilePath, log: logs.Add);

        Assert.Empty(registry.Devices);
        Assert.Equal("{ isso não é json", File.ReadAllText(FilePath + ".bak"));
        Assert.Single(logs);
        var (device, token) = registry.Add("Pixel 8"); // continua funcionando depois
        Assert.Equal(device, new DeviceRegistry(FilePath).Authenticate(token));
    }
}
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet test host --filter "FullyQualifiedName~DeviceRegistryTests"`
Expected: FAIL na compilação — `DeviceRegistry` não existe.

- [ ] **Step 3: Implementar**

`host/ScreenShare.Core/Security/DeviceRegistry.cs`:
```csharp
using System.Security.Cryptography;
using System.Text.Json;
using ScreenShare.Core.Protocol;

namespace ScreenShare.Core.Security;

/// <summary>Um celular pareado. Só o SHA-256 (hex) da chave de acesso é guardado.</summary>
public sealed record PairedDevice(string Id, string Name, string TokenSha256, DateTimeOffset PairedAt);

/// <summary>
/// Lista de celulares pareados, persistida em JSON. Thread-safe.
/// Arquivo corrompido: registra no log, guarda uma cópia em ".bak" e começa vazio.
/// </summary>
public sealed class DeviceRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;
    private readonly TimeProvider _clock;
    private readonly Lock _lock = new();
    private readonly List<PairedDevice> _devices;

    public DeviceRegistry(string path, TimeProvider? clock = null, Action<string>? log = null)
    {
        _path = path;
        _clock = clock ?? TimeProvider.System;
        _devices = Load(log);
    }

    public IReadOnlyList<PairedDevice> Devices
    {
        get
        {
            lock (_lock) return _devices.ToArray();
        }
    }

    /// <summary>Registra um celular novo e devolve a chave de acesso (a única vez em que ela existe em claro no PC).</summary>
    public (PairedDevice Device, byte[] Token) Add(string name)
    {
        var token = RandomNumberGenerator.GetBytes(MessageCodec.TokenLength);
        var device = new PairedDevice(
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)), name,
            Convert.ToHexStringLower(SHA256.HashData(token)), _clock.GetUtcNow());
        lock (_lock)
        {
            _devices.Add(device);
            Save();
        }
        return (device, token);
    }

    /// <summary>O aparelho dono da chave, ou null. Compara os hashes em tempo constante.</summary>
    public PairedDevice? Authenticate(ReadOnlySpan<byte> token)
    {
        if (token.Length != MessageCodec.TokenLength) return null;
        var hash = SHA256.HashData(token);
        lock (_lock)
        {
            PairedDevice? match = null;
            foreach (var device in _devices)
            {
                if (CryptographicOperations.FixedTimeEquals(Convert.FromHexString(device.TokenSha256), hash))
                    match = device;
            }
            return match;
        }
    }

    public bool Remove(string id)
    {
        lock (_lock)
        {
            if (_devices.RemoveAll(d => d.Id == id) == 0) return false;
            Save();
            return true;
        }
    }

    private List<PairedDevice> Load(Action<string>? log)
    {
        if (!File.Exists(_path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<PairedDevice>>(File.ReadAllText(_path), JsonOptions) ?? [];
        }
        catch (JsonException e)
        {
            File.Copy(_path, _path + ".bak", overwrite: true);
            log?.Invoke($"Lista de aparelhos pareados corrompida ({e.Message}). Começando vazia; cópia em {_path}.bak");
            return [];
        }
    }

    /// <summary>Grava num arquivo temporário e renomeia, para nunca deixar o JSON pela metade.</summary>
    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_devices, JsonOptions));
        File.Move(temporary, _path, overwrite: true);
    }
}
```

- [ ] **Step 4: Rodar e ver passar**

Run: `dotnet test host` — Expected: PASS, 0 avisos.

- [ ] **Step 5: Commit**

```powershell
git add host
git commit -m "feat(core): registro de aparelhos pareados com hash da chave" -m "Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Tarefa 6: `HostServer` v2 — TLS, pareamento e autenticação (C#)

**Files:**
- Modify: `host/ScreenShare.DevHost/HostServer.cs` (reescrever)
- Test: `host/ScreenShare.Tests/DevHost/HostServerTests.cs` (reescrever)

**Interfaces:**
- Consumes: `HostIdentity` (Tarefa 3), `PairingSession` (Tarefa 4), `DeviceRegistry`/`PairedDevice` (Tarefa 5), mensagens v2 (Tarefa 1).
- Produces: `HostServer(int wifiPort, int usbPort, HostIdentity identity, PairingSession pairing, DeviceRegistry devices, Action<string>? log = null, TimeSpan? handshakeTimeout = null)` com `int WifiPort`, `IPEndPoint UsbEndPoint`, `void Start()`, `Task RunAsync(CancellationToken)`, `Dispose()`. Usado pelo `Program.cs` na Tarefa 7.

- [ ] **Step 1: Reescrever os testes (falham)**

`host/ScreenShare.Tests/DevHost/HostServerTests.cs`:
```csharp
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using ScreenShare.Core.Protocol;
using ScreenShare.Core.Security;
using ScreenShare.DevHost;

namespace ScreenShare.Tests.DevHost;

public sealed class HostServerTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screenshare-tests", Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(15));
    private readonly HostIdentity _identity;
    private readonly PairingSession _pairing = new(TimeProvider.System);
    private readonly DeviceRegistry _devices;
    private readonly HostServer _server;
    private Task _serving = Task.CompletedTask;

    public HostServerTests()
    {
        _identity = HostIdentity.LoadOrCreate(_dir, "PC de Teste");
        _devices = new DeviceRegistry(Path.Combine(_dir, "paired-devices.json"));
        _server = new HostServer(0, 0, _identity, _pairing, _devices, handshakeTimeout: TimeSpan.FromSeconds(2));
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

    /// <summary>TLS na porta Wi-Fi, aceitando só o certificado do PC de teste (como o celular faz).</summary>
    private async Task<(TcpClient Client, SslStream Stream, MessageReader Reader)> ConnectWifiAsync()
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _server.WifiPort, _cts.Token);
        var tls = new SslStream(client.GetStream());
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "screenshare",
            RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                certificate is not null && HostIdentity.ComputeFingerprint(certificate.GetRawCertData()) == _identity.Fingerprint,
        }, _cts.Token);
        return (client, tls, new MessageReader(tls));
    }

    private async Task<(TcpClient Client, NetworkStream Stream, MessageReader Reader)> ConnectUsbAsync()
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _server.UsbEndPoint.Port, _cts.Token);
        var stream = client.GetStream();
        return (client, stream, new MessageReader(stream));
    }

    private Task SendAsync(Stream stream, Message message) =>
        stream.WriteAsync(MessageCodec.Encode(message), _cts.Token).AsTask();

    /// <summary>Depois de DENIED o PC fecha: a leitura termina (null) ou a conexão cai (IOException).</summary>
    private async Task AssertClosedAsync(MessageReader reader)
    {
        Message? message = null;
        var error = await Record.ExceptionAsync(async () => message = await reader.ReadAsync(_cts.Token));
        Assert.Null(message);
        Assert.True(error is null or IOException, $"erro inesperado: {error}");
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

        Assert.Equal(new PongMessage(123456789), await reader.ReadAsync(_cts.Token));
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
    public async Task Usb_port_serves_hello_without_tls_or_pairing()
    {
        var (client, stream, reader) = await ConnectUsbAsync();
        using var _ = client;

        await SendAsync(stream, Hello());
        Assert.IsType<ConfigMessage>(await reader.ReadAsync(_cts.Token));
        await SendAsync(stream, new PingMessage(42));

        Assert.Equal(new PongMessage(42), await reader.ReadAsync(_cts.Token));
    }

    [Fact]
    public void Usb_port_listens_only_on_loopback() =>
        Assert.Equal(IPAddress.Loopback, _server.UsbEndPoint.Address);

    [Fact]
    public async Task Old_app_hello_v1_on_usb_is_denied_with_incompatible_version()
    {
        var (client, stream, reader) = await ConnectUsbAsync();
        using var _ = client;

        await SendAsync(stream, Hello(version: 1));

        Assert.Equal(new DeniedMessage(DeniedReason.IncompatibleVersion), await reader.ReadAsync(_cts.Token));
        await AssertClosedAsync(reader);
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
            Assert.Null(reply);
            Assert.True(error is null or IOException, $"erro inesperado: {error}");
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
    public async Task Server_accepts_a_new_usb_client_after_the_previous_one_disconnects()
    {
        var first = await ConnectUsbAsync();
        await SendAsync(first.Stream, Hello());
        Assert.IsType<ConfigMessage>(await first.Reader.ReadAsync(_cts.Token));
        first.Client.Dispose();

        var (client, stream, reader) = await ConnectUsbAsync();
        using var _ = client;
        await SendAsync(stream, Hello());

        Assert.IsType<ConfigMessage>(await reader.ReadAsync(_cts.Token));
    }
}
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet test host --filter "FullyQualifiedName~HostServerTests"`
Expected: FAIL na compilação — o construtor de `HostServer` não aceita esses argumentos; `WifiPort`/`UsbEndPoint` não existem.

- [ ] **Step 3: Implementar — reescrever `host/ScreenShare.DevHost/HostServer.cs`**

```csharp
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using ScreenShare.Core.Protocol;
using ScreenShare.Core.Security;

namespace ScreenShare.DevHost;

/// <summary>
/// Servidor de desenvolvimento (sem vídeo). Porta Wi-Fi: TLS obrigatório, depois PAIR/AUTH antes do HELLO.
/// Porta USB: só em loopback (o `adb reverse` chega por ali), HELLO direto. Responde PING com PONG.
/// Atende um cliente por vez em cada porta.
/// </summary>
public sealed class HostServer : IDisposable
{
    private const uint StubBitrateKbps = 8000;

    private readonly TcpListener _wifi;
    private readonly TcpListener _usb;
    private readonly HostIdentity _identity;
    private readonly PairingSession _pairing;
    private readonly DeviceRegistry _devices;
    private readonly Action<string>? _log;
    private readonly TimeSpan _handshakeTimeout;

    public HostServer(int wifiPort, int usbPort, HostIdentity identity, PairingSession pairing, DeviceRegistry devices,
        Action<string>? log = null, TimeSpan? handshakeTimeout = null)
    {
        _wifi = new TcpListener(IPAddress.Any, wifiPort);
        _usb = new TcpListener(IPAddress.Loopback, usbPort);
        _identity = identity;
        _pairing = pairing;
        _devices = devices;
        _log = log;
        _handshakeTimeout = handshakeTimeout ?? TimeSpan.FromSeconds(10);
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
        AcceptLoopAsync(_wifi, secure: true, cancellationToken),
        AcceptLoopAsync(_usb, secure: false, cancellationToken));

    private async Task AcceptLoopAsync(TcpListener listener, bool secure, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(cancellationToken);
                client.NoDelay = true;
                _log?.Invoke($"Cliente conectado ({(secure ? "Wi-Fi" : "USB")}): {client.Client.RemoteEndPoint}");
                try
                {
                    if (secure)
                        await ServeWifiAsync(client.GetStream(), cancellationToken);
                    else
                        await ServeSessionAsync(client.GetStream(), new MessageReader(client.GetStream()), cancellationToken);
                }
                catch (Exception e) when (e is IOException or AuthenticationException
                                          || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    // IOException inclui ProtocolException; OperationCanceledException aqui é o timeout do handshake
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

    private async Task ServeWifiAsync(NetworkStream network, CancellationToken cancellationToken)
    {
        await using var tls = new SslStream(network, leaveInnerStreamOpen: false);
        // TLS e PAIR/AUTH precisam terminar dentro do prazo: um cliente calado não pode prender a porta.
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshake.CancelAfter(_handshakeTimeout);

        await tls.AuthenticateAsServerAsync(
            new SslServerAuthenticationOptions { ServerCertificate = _identity.Certificate }, handshake.Token);
        var reader = new MessageReader(tls);
        var first = await reader.ReadAsync(handshake.Token);

        if (first is PairMessage pair)
        {
            if (!_pairing.TryConsume(pair.Secret))
            {
                await SendAsync(tls, new DeniedMessage(DeniedReason.InvalidPairingSecret), cancellationToken);
                return;
            }
            var (device, token) = _devices.Add(pair.DeviceName);
            _log?.Invoke($"Aparelho pareado: {device.Name} (id {device.Id})");
            await SendAsync(tls, new PairedMessage(token), cancellationToken);
            first = await reader.ReadAsync(handshake.Token);
        }

        if (first is null) return; // cliente fechou
        if (first is not AuthMessage auth || _devices.Authenticate(auth.Token) is not { } known)
        {
            await SendAsync(tls, new DeniedMessage(DeniedReason.UnknownDevice), cancellationToken);
            return;
        }

        _log?.Invoke($"Autenticado: {known.Name} (id {known.Id})");
        await ServeSessionAsync(tls, reader, cancellationToken);
    }

    /// <summary>HELLO → CONFIG, depois PING → PONG até o cliente sair.</summary>
    private static async Task ServeSessionAsync(Stream stream, MessageReader reader, CancellationToken cancellationToken)
    {
        if (await reader.ReadAsync(cancellationToken) is not HelloMessage hello)
            return; // primeira mensagem não é HELLO: fecha

        if (hello.ProtocolVersion != MessageCodec.ProtocolVersion)
        {
            await SendAsync(stream, new DeniedMessage(DeniedReason.IncompatibleVersion), cancellationToken);
            return;
        }

        await SendAsync(stream, new ConfigMessage(hello.Width, hello.Height, VideoCodec.H264, StubBitrateKbps, []), cancellationToken);

        while (await reader.ReadAsync(cancellationToken) is { } message)
        {
            if (message is PingMessage ping)
                await SendAsync(stream, new PongMessage(ping.TimestampUs), cancellationToken);
            // demais mensagens (TOUCH, KEYFRAME_REQ) são ignoradas: este stub não tem vídeo nem toque
        }
    }

    private static Task SendAsync(Stream stream, Message message, CancellationToken cancellationToken) =>
        stream.WriteAsync(MessageCodec.Encode(message), cancellationToken).AsTask();

    public void Dispose()
    {
        _wifi.Stop();
        _usb.Stop();
    }
}
```

`Program.cs` ainda usa o construtor antigo e não compila até a Tarefa 7. Para manter a solução compilando nesta tarefa, trocar no `Program.cs` as linhas que criam o servidor pelo bloco abaixo (a Tarefa 7 reescreve o arquivo inteiro):
```csharp
var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenShare");
using var identity = ScreenShare.Core.Security.HostIdentity.LoadOrCreate(dataDirectory, Environment.MachineName);
var devices = new ScreenShare.Core.Security.DeviceRegistry(Path.Combine(dataDirectory, "paired-devices.json"));
using var server = new HostServer(port, 38701, identity, new ScreenShare.Core.Security.PairingSession(TimeProvider.System), devices,
    message => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}"));
```
e trocar `server.Port` por `server.WifiPort` nas duas linhas que o usam.

- [ ] **Step 4: Rodar e ver passar**

Run: `dotnet test host` — Expected: PASS (todos), 0 avisos. O teste do peer calado leva cerca de 2 s.

- [ ] **Step 5: Commit**

```powershell
git add host
git commit -m "feat(devhost): TLS na porta Wi-Fi com pareamento e AUTH; USB só em loopback" -m "Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Tarefa 7: DevHost — comandos de console, QR e `fp` no mDNS

**Files:**
- Modify: `host/ScreenShare.DevHost/ScreenShare.DevHost.csproj` (pacote `QRCoder` 1.6.0)
- Modify: `host/ScreenShare.DevHost/Program.cs` (reescrever)

**Interfaces:**
- Consumes: `HostServer` (Tarefa 6), `HostIdentity.MdnsId`/`Fingerprint` (Tarefa 3), `PairingSession.Begin` e `PairingUri.Build` (Tarefa 4), `DeviceRegistry.Devices`/`Remove` (Tarefa 5), `LanAddressSelector.Select` e `AdapterInfo.FromSystem` (já existem).
- Produces: executável que o usuário roda no PC; sem API nova.

- [ ] **Step 1: Pacote**

Em `ScreenShare.DevHost.csproj`, no `ItemGroup` dos pacotes:
```xml
    <PackageReference Include="QRCoder" Version="1.6.0" />
```

- [ ] **Step 2: Reescrever `host/ScreenShare.DevHost/Program.cs`**

```csharp
using Makaretu.Dns;
using QRCoder;
using ScreenShare.Core.Security;
using ScreenShare.DevHost;

// Host de desenvolvimento: Wi-Fi com TLS + pareamento por QR (porta 38700) e USB sem TLS em loopback (porta 38701).
// Comandos no console: p = parear celular (mostra o QR), l = listar pareados, r <id> = remover, Ctrl+C = sair.
const int WifiPort = 38700;
const int UsbPort = 38701;

void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenShare");
using var identity = HostIdentity.LoadOrCreate(dataDirectory, Environment.MachineName);
var pairing = new PairingSession(TimeProvider.System);
var devices = new DeviceRegistry(Path.Combine(dataDirectory, "paired-devices.json"), log: Log);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

using var server = new HostServer(WifiPort, UsbPort, identity, pairing, devices, Log);
server.Start();

// Anuncia só IPs da LAN real; sem nenhum (ex.: sem gateway), cai no padrão da biblioteca (todos os IPs).
var lanAddresses = LanAddressSelector.Select(AdapterInfo.FromSystem());
var profile = new ServiceProfile(
    Environment.MachineName, "_screenshare._tcp", (ushort)server.WifiPort, lanAddresses.Count > 0 ? lanAddresses : null);
profile.AddProperty("fp", identity.MdnsId);
using var discovery = new ServiceDiscovery();
discovery.Advertise(profile);

Console.WriteLine($"ScreenShare DevHost \"{Environment.MachineName}\": Wi-Fi (TLS) na porta {server.WifiPort}, USB na {server.UsbEndPoint}.");
Console.WriteLine($"IPs anunciados: {(lanAddresses.Count > 0 ? string.Join(", ", lanAddresses) : "todos")}. Digital: {identity.Fingerprint}");
Console.WriteLine("Comandos: p = parear celular, l = listar pareados, r <id> = remover, Ctrl+C = sair.");

_ = Task.Run(() =>
{
    while (Console.ReadLine() is { } line)
        HandleCommand(line.Trim());
});

await server.RunAsync(cts.Token);

void HandleCommand(string line)
{
    if (line == "p")
    {
        if (lanAddresses.Count == 0)
        {
            Console.WriteLine("Nenhum IP de rede local encontrado: conecte o PC ao Wi-Fi/rede para parear.");
            return;
        }
        var uri = PairingUri.Build(lanAddresses[0].ToString(), server.WifiPort, identity.Fingerprint, pairing.Begin(), Environment.MachineName);
        using var qr = new QRCodeGenerator().CreateQrCode(uri, QRCodeGenerator.ECCLevel.L);
        Console.WriteLine(new AsciiQRCode(qr).GetGraphicSmall());
        Console.WriteLine($"Escaneie no app (vale {PairingSession.Lifetime.TotalMinutes:0} minutos, uma vez): {uri}");
    }
    else if (line == "l")
    {
        var list = devices.Devices;
        Console.WriteLine(list.Count == 0
            ? "Nenhum celular pareado."
            : string.Join(Environment.NewLine, list.Select(d => $"  {d.Id}  {d.Name}  (pareado em {d.PairedAt.ToLocalTime():g})")));
    }
    else if (line.StartsWith("r ", StringComparison.Ordinal))
    {
        var id = line[2..].Trim();
        Console.WriteLine(devices.Remove(id) ? $"Removido: {id}" : $"Nenhum celular com id {id}.");
    }
    else if (line.Length > 0)
    {
        Console.WriteLine("Comandos: p = parear celular, l = listar pareados, r <id> = remover, Ctrl+C = sair.");
    }
}
```

- [ ] **Step 3: Compilar e testar**

Run: `dotnet build host` — Expected: 0 avisos, 0 erros. Run: `dotnet test host` — Expected: PASS.

- [ ] **Step 4: Verificação manual**

Run: `dotnet run --project host/ScreenShare.DevHost`. Digitar `p` → aparece um QR em texto e a URI `screenshare://pair?...` com o IP da LAN; `l` → "Nenhum celular pareado."; `r xyz` → "Nenhum celular com id xyz."; Ctrl+C encerra. Conferir que `%APPDATA%\ScreenShare\host-cert.pfx` e `host-cert.key` foram criados e que, rodando de novo, a digital impressa é a mesma.

- [ ] **Step 5: Commit**

```powershell
git add host
git commit -m "feat(devhost): QR de pareamento no console, lista/remoção de aparelhos e fp no mDNS" -m "Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Tarefa 8: Android — digital, `PinnedTrustManager`, URI do QR e nome do aparelho

**Files:**
- Create: `android/app/src/main/java/dev/screenshare/android/security/Fingerprint.kt`, `.../security/PinnedTrustManager.kt`, `.../pairing/PairingUri.kt`
- Create (gerados): `android/app/src/test/resources/tls/host.p12`, `android/app/src/test/resources/tls/other.p12`
- Modify: `.gitattributes` (`*.p12 binary`), `android/app/src/test/java/dev/screenshare/android/protocol/Vectors.kt`
- Test: `android/app/src/test/java/dev/screenshare/android/security/TestTls.kt`, `.../security/PinnedTrustManagerTest.kt`, `.../pairing/PairingUriTest.kt`

**Interfaces:**
- Consumes: `MessageCodec.SECRET_LENGTH`, `MAX_DEVICE_NAME_BYTES` (Tarefa 2); `docs/protocol-vectors/pairing-uri.txt` (Tarefa 4).
- Produces: `object Fingerprint { fun of(der: ByteArray): String; fun mdnsId(fingerprint: String): String }`; `class PinnedTrustManager(expectedFingerprint: String) : X509TrustManager { fun socketFactory(): SSLSocketFactory }`; `data class PairingInfo(host: String, port: Int, fingerprint: String, secret: ByteArray, pcName: String)`; `object PairingUri { fun parse(text: String): PairingInfo? }`; `fun deviceNameOf(model: String): String`; teste: `object TestTls { fun keyStore(name); fun certificate(name): X509Certificate; fun fingerprint(name): String; fun serverSocket(name): SSLServerSocket }`, `Vectors.text(name)`.

- [ ] **Step 1: Certificados de teste**

No Git Bash, a partir da raiz da worktree:
```bash
mkdir -p android/app/src/test/resources/tls && cd android/app/src/test/resources/tls
for name in host other; do
  MSYS_NO_PATHCONV=1 openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -nodes \
    -keyout $name.key -out $name.crt -days 36500 -subj "/CN=ScreenShare Test $name"
  openssl pkcs12 -export -inkey $name.key -in $name.crt -out $name.p12 -passout pass:test -name $name
  rm $name.key $name.crt
done
```
Em `.gitattributes`, acrescentar a linha `*.p12 binary`.

- [ ] **Step 2: Testes que falham**

Em `Vectors.kt` (teste), acrescentar dentro do `object Vectors`:
```kotlin
    /** Conteúdo de um vetor de texto (ex.: pairing-uri.txt), sem espaços nas pontas. */
    fun text(name: String): String {
        val file = File(dir, name)
        require(file.isFile) { "vetor não encontrado: ${file.canonicalPath}" }
        return file.readText().trim()
    }
```

`android/app/src/test/java/dev/screenshare/android/security/TestTls.kt`:
```kotlin
package dev.screenshare.android.security

import java.net.InetAddress
import java.security.KeyStore
import java.security.cert.X509Certificate
import javax.net.ssl.KeyManagerFactory
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLServerSocket

/** Certificados de teste em src/test/resources/tls (gerados com openssl, senha "test"). */
object TestTls {
    private val password = "test".toCharArray()

    fun keyStore(name: String): KeyStore = KeyStore.getInstance("PKCS12").apply {
        val stream = TestTls::class.java.getResourceAsStream("/tls/$name.p12") ?: error("certificado de teste não encontrado: $name")
        stream.use { load(it, password) }
    }

    fun certificate(name: String): X509Certificate =
        keyStore(name).let { it.getCertificate(it.aliases().nextElement()) as X509Certificate }

    fun fingerprint(name: String): String = Fingerprint.of(certificate(name).encoded)

    /** Servidor TLS em loopback com o certificado [name], como o PC. */
    fun serverSocket(name: String): SSLServerSocket {
        val keyManagers = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm())
            .apply { init(keyStore(name), password) }.keyManagers
        val context = SSLContext.getInstance("TLS").apply { init(keyManagers, null, null) }
        return context.serverSocketFactory.createServerSocket(0, 1, InetAddress.getLoopbackAddress()) as SSLServerSocket
    }
}
```

`android/app/src/test/java/dev/screenshare/android/security/PinnedTrustManagerTest.kt`:
```kotlin
package dev.screenshare.android.security

import java.security.cert.CertificateException
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class PinnedTrustManagerTest {
    private val host = TestTls.certificate("host")
    private val other = TestTls.certificate("other")
    private val manager = PinnedTrustManager(Fingerprint.of(host.encoded))

    @Test
    fun acceptsTheExpectedCertificate() = manager.checkServerTrusted(arrayOf(host), "ECDHE_ECDSA")

    @Test
    fun rejectsADifferentCertificate() {
        assertThrows(CertificateException::class.java) { manager.checkServerTrusted(arrayOf(other), "ECDHE_ECDSA") }
    }

    @Test
    fun rejectsAnEmptyChain() {
        assertThrows(CertificateException::class.java) { manager.checkServerTrusted(arrayOf(), "ECDHE_ECDSA") }
    }

    @Test
    fun fingerprintIsBase64UrlWithoutPadding() {
        val fingerprint = Fingerprint.of(host.encoded)
        assertEquals(43, fingerprint.length)
        assertEquals(false, fingerprint.any { it == '+' || it == '/' || it == '=' })
    }

    @Test
    fun mdnsIdIsTheLowercaseHexOfTheFirstEightBytes() =
        assertEquals("2021222324252627", Fingerprint.mdnsId("ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8"))
}
```

`android/app/src/test/java/dev/screenshare/android/pairing/PairingUriTest.kt`:
```kotlin
package dev.screenshare.android.pairing

import dev.screenshare.android.protocol.Vectors
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class PairingUriTest {
    private val example = Vectors.text("pairing-uri.txt")

    @Test
    fun parsesTheSharedExample() {
        val info = PairingUri.parse(example)!!

        assertEquals("192.168.0.10", info.host)
        assertEquals(38700, info.port)
        assertEquals("ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8", info.fingerprint)
        assertArrayEquals(ByteArray(32) { it.toByte() }, info.secret)
        assertEquals("PC da Sala", info.pcName)
    }

    @Test
    fun rejectsOtherSchemesAndHosts() {
        assertNull(PairingUri.parse(example.replace("screenshare://", "https://")))
        assertNull(PairingUri.parse(example.replace("://pair?", "://outra?")))
    }

    @Test
    fun rejectsMissingFields() {
        for (field in listOf("h", "p", "fp", "s", "n")) {
            val without = example.split('?', '&').filterNot { it.startsWith("$field=") }
            val uri = without.first() + "?" + without.drop(1).joinToString("&")
            assertNull("sem $field", PairingUri.parse(uri))
        }
    }

    @Test
    fun rejectsBadValues() {
        assertNull(PairingUri.parse(example.replace("p=38700", "p=99999")))
        assertNull(PairingUri.parse(example.replace("s=AAEC", "s=!!!!")))
        assertNull(PairingUri.parse(example.replace("s=AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8", "s=AAEC")))
        assertNull(PairingUri.parse(example.replace("fp=ICEi", "fp=")))
    }

    @Test
    fun rejectsGarbage() {
        assertNull(PairingUri.parse("isso não é uma URI"))
        assertNull(PairingUri.parse("https://example.com"))
        assertNull(PairingUri.parse(""))
    }

    @Test
    fun deviceNameIsTruncatedTo64Utf8Bytes() {
        val name = deviceNameOf("é".repeat(40)) // 2 bytes por caractere
        assertEquals("é".repeat(32), name)
        assertTrue(name.toByteArray(Charsets.UTF_8).size <= 64)
    }

    @Test
    fun blankDeviceNameFallsBackToAndroid() = assertEquals("Android", deviceNameOf("   "))
}
```

- [ ] **Step 3: Rodar e ver falhar**

```powershell
$env:JAVA_HOME = 'C:\Program Files\Android\Android Studio\jbr'; .\android\gradlew.bat -p android :app:testDebugUnitTest
```
Expected: FAIL na compilação — `Fingerprint`, `PinnedTrustManager`, `PairingUri` não existem.

- [ ] **Step 4: Implementar**

`android/app/src/main/java/dev/screenshare/android/security/Fingerprint.kt`:
```kotlin
package dev.screenshare.android.security

import java.security.MessageDigest
import java.util.Base64

/** Digital do certificado do PC: SHA-256 do DER em base64url sem padding (mesmo formato do QR). */
object Fingerprint {
    fun of(der: ByteArray): String =
        Base64.getUrlEncoder().withoutPadding().encodeToString(MessageDigest.getInstance("SHA-256").digest(der))

    /** 16 primeiros caracteres hex da digital: é o que o PC anuncia no TXT `fp` do mDNS. */
    fun mdnsId(fingerprint: String): String =
        Base64.getUrlDecoder().decode(fingerprint).take(8).joinToString("") { "%02x".format(it) }
}
```

`android/app/src/main/java/dev/screenshare/android/security/PinnedTrustManager.kt`:
```kotlin
package dev.screenshare.android.security

import java.security.MessageDigest
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocketFactory
import javax.net.ssl.X509TrustManager

/**
 * Confia só no certificado cuja digital é [expectedFingerprint] (fixada no pareamento).
 * Não usa autoridades certificadoras nem confere nome de host: o PC tem certificado autoassinado.
 */
class PinnedTrustManager(private val expectedFingerprint: String) : X509TrustManager {
    override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        val leaf = chain?.firstOrNull() ?: throw CertificateException("O PC não enviou certificado")
        val actual = Fingerprint.of(leaf.encoded)
        if (!MessageDigest.isEqual(actual.toByteArray(), expectedFingerprint.toByteArray())) {
            throw CertificateException("Este não é o PC pareado")
        }
    }

    override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) =
        throw CertificateException("Certificado de cliente não é usado")

    override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()

    fun socketFactory(): SSLSocketFactory =
        SSLContext.getInstance("TLS").apply { init(null, arrayOf(this@PinnedTrustManager), null) }.socketFactory
}
```

`android/app/src/main/java/dev/screenshare/android/pairing/PairingUri.kt`:
```kotlin
package dev.screenshare.android.pairing

import dev.screenshare.android.protocol.MessageCodec
import java.net.URI
import java.net.URISyntaxException
import java.net.URLDecoder
import java.util.Base64
import java.util.Objects

/** Dados lidos do QR de pareamento. [fingerprint] em base64url; [secret] tem 32 bytes. */
data class PairingInfo(
    val host: String,
    val port: Int,
    val fingerprint: String,
    val secret: ByteArray,
    val pcName: String,
) {
    override fun equals(other: Any?) = other is PairingInfo && host == other.host && port == other.port &&
        fingerprint == other.fingerprint && secret.contentEquals(other.secret) && pcName == other.pcName

    override fun hashCode() = Objects.hash(host, port, fingerprint, secret.contentHashCode(), pcName)
}

/** Lê `screenshare://pair?h=…&p=…&fp=…&s=…&n=…` (formato em docs/protocol-vectors/pairing-uri.txt). */
object PairingUri {
    /** null se o texto não for um QR de pareamento válido. */
    fun parse(text: String): PairingInfo? {
        val uri = try {
            URI(text.trim())
        } catch (_: URISyntaxException) {
            return null
        }
        if (uri.scheme != "screenshare" || uri.host != "pair") return null
        val params = uri.rawQuery?.split('&')
            ?.mapNotNull { part -> part.split('=', limit = 2).takeIf { it.size == 2 } }
            ?.associate { (key, value) -> key to URLDecoder.decode(value, "UTF-8") } // decode(String, Charset) só existe a partir do API 33
            ?: return null

        val host = params["h"]?.takeIf { it.isNotBlank() } ?: return null
        val port = params["p"]?.toIntOrNull()?.takeIf { it in 1..65535 } ?: return null
        val fingerprint = params["fp"]?.takeIf { decodeBase64Url(it)?.size == 32 } ?: return null
        val secret = params["s"]?.let(::decodeBase64Url)?.takeIf { it.size == MessageCodec.SECRET_LENGTH } ?: return null
        val pcName = params["n"]?.takeIf { it.isNotBlank() } ?: return null
        return PairingInfo(host, port, fingerprint, secret, pcName)
    }

    private fun decodeBase64Url(text: String): ByteArray? = try {
        Base64.getUrlDecoder().decode(text)
    } catch (_: IllegalArgumentException) {
        null
    }
}

/** Nome do aparelho para o PAIR: o modelo, cortado em até 64 bytes UTF-8 sem partir caracteres. */
fun deviceNameOf(model: String): String {
    val name = model.trim().ifEmpty { "Android" }
    val result = StringBuilder()
    var bytes = 0
    for (codePoint in name.codePoints().toArray()) {
        val char = String(Character.toChars(codePoint))
        val size = char.toByteArray(Charsets.UTF_8).size
        if (bytes + size > MessageCodec.MAX_DEVICE_NAME_BYTES) break
        result.append(char)
        bytes += size
    }
    return result.toString()
}
```

- [ ] **Step 5: Rodar e ver passar**

Mesmo comando do Step 3. Expected: `BUILD SUCCESSFUL`, PinnedTrustManagerTest 5/5 e PairingUriTest 7/7 entre os aprovados.

- [ ] **Step 6: Commit**

```powershell
git add .gitattributes android/app/src
git commit -m "feat(android): digital fixa do PC, leitura do QR de pareamento e nome do aparelho" -m "Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Tarefa 9: Android — `PairingStore` (DataStore + Keystore)

**Files:**
- Modify: `android/gradle/libs.versions.toml`, `android/app/build.gradle.kts` (DataStore Preferences 1.1.7)
- Create: `android/app/src/main/java/dev/screenshare/android/security/PairingStore.kt`, `.../security/KeystoreTokenCipher.kt`
- Test: `android/app/src/test/java/dev/screenshare/android/security/PairingStoreTest.kt`

**Interfaces:**
- Produces: `data class PairedPc(name: String, fingerprint: String, token: ByteArray, lastHost: String?)`; `interface TokenCipher { fun encrypt(plain: ByteArray): ByteArray; fun decrypt(sealed: ByteArray): ByteArray }`; `class PairingStore(dataStore: DataStore<Preferences>, cipher: TokenCipher)` com `suspend fun load(): PairedPc?`, `suspend fun save(pc: PairedPc)`, `suspend fun updateLastHost(host: String)`, `suspend fun clear()`; `val Context.pairingDataStore: DataStore<Preferences>`; `class KeystoreTokenCipher : TokenCipher`.

- [ ] **Step 1: Dependência**

`libs.versions.toml`: em `[versions]` acrescentar `datastore = "1.1.7"`; em `[libraries]` acrescentar:
```toml
androidx-datastore-preferences = { group = "androidx.datastore", name = "datastore-preferences", version.ref = "datastore" }
```
`app/build.gradle.kts`, em `dependencies`, depois de `implementation(libs.kotlinx.coroutines.android)`:
```kotlin
    implementation(libs.androidx.datastore.preferences)
```

- [ ] **Step 2: Teste que falha**

`android/app/src/test/java/dev/screenshare/android/security/PairingStoreTest.kt`:
```kotlin
package dev.screenshare.android.security

import androidx.datastore.preferences.core.PreferenceDataStoreFactory
import java.io.File
import javax.crypto.AEADBadTagException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class PairingStoreTest {
    @get:Rule
    val tmp = TemporaryFolder()

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val file by lazy { File(tmp.root, "pairing.preferences_pb") }
    private val dataStore by lazy { PreferenceDataStoreFactory.create(scope = scope, produceFile = { file }) }
    private val pc = PairedPc("PC da Sala", "ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8", ByteArray(32) { (0xA0 + it).toByte() }, "192.168.0.10")

    /** Cifra de mentira para o JVM (o Android Keystore só existe no aparelho). */
    private object XorCipher : TokenCipher {
        override fun encrypt(plain: ByteArray) = ByteArray(plain.size) { (plain[it].toInt() xor 0x5A).toByte() }
        override fun decrypt(sealed: ByteArray) = encrypt(sealed)
    }

    /** Simula a chave do Keystore perdida (app reinstalado, backup restaurado em outro aparelho). */
    private object LostKeyCipher : TokenCipher {
        override fun encrypt(plain: ByteArray) = XorCipher.encrypt(plain)
        override fun decrypt(sealed: ByteArray): ByteArray = throw AEADBadTagException("chave do Keystore perdida")
    }

    @After
    fun tearDown() = scope.cancel()

    @Test
    fun savedPcIsLoadedBack() = runBlocking {
        val store = PairingStore(dataStore, XorCipher)
        store.save(pc)

        assertEquals(pc, store.load())
    }

    @Test
    fun nothingSavedLoadsNull() = runBlocking {
        assertNull(PairingStore(dataStore, XorCipher).load())
    }

    @Test
    fun tokenIsNotStoredInPlainText() = runBlocking {
        PairingStore(dataStore, XorCipher).save(pc)

        val bytes = file.readBytes()
        assertFalse(bytes.toList().windowed(pc.token.size).any { it == pc.token.toList() })
    }

    @Test
    fun undecryptableTokenForgetsThePairingInsteadOfCrashing() = runBlocking {
        PairingStore(dataStore, XorCipher).save(pc)
        val store = PairingStore(dataStore, LostKeyCipher)

        assertNull(store.load())
        assertNull(PairingStore(dataStore, XorCipher).load()) // foi apagado
    }

    @Test
    fun clearForgetsThePc() = runBlocking {
        val store = PairingStore(dataStore, XorCipher)
        store.save(pc)

        store.clear()

        assertNull(store.load())
    }

    @Test
    fun updateLastHostKeepsTheRest() = runBlocking {
        val store = PairingStore(dataStore, XorCipher)
        store.save(pc)

        store.updateLastHost("10.0.0.7")

        assertEquals(pc.copy(lastHost = "10.0.0.7"), store.load())
    }

    @Test
    fun updateLastHostWithoutPairingDoesNothing() = runBlocking {
        val store = PairingStore(dataStore, XorCipher)

        store.updateLastHost("10.0.0.7")

        assertNull(store.load())
    }
}
```

- [ ] **Step 3: Rodar e ver falhar**

```powershell
$env:JAVA_HOME = 'C:\Program Files\Android\Android Studio\jbr'; .\android\gradlew.bat -p android :app:testDebugUnitTest
```
Expected: FAIL na compilação — `PairedPc`, `TokenCipher`, `PairingStore` não existem.

- [ ] **Step 4: Implementar**

`android/app/src/main/java/dev/screenshare/android/security/PairingStore.kt`:
```kotlin
package dev.screenshare.android.security

import android.content.Context
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import java.security.GeneralSecurityException
import java.util.Base64
import java.util.Objects
import kotlinx.coroutines.flow.first

/** O PC pareado com este celular. [token] é a chave de acesso em claro (só em memória). */
data class PairedPc(val name: String, val fingerprint: String, val token: ByteArray, val lastHost: String?) {
    override fun equals(other: Any?) = other is PairedPc && name == other.name && fingerprint == other.fingerprint &&
        token.contentEquals(other.token) && lastHost == other.lastHost

    override fun hashCode() = Objects.hash(name, fingerprint, token.contentHashCode(), lastHost)
}

/** Cifra a chave de acesso antes de ir para o disco. */
interface TokenCipher {
    fun encrypt(plain: ByteArray): ByteArray
    fun decrypt(sealed: ByteArray): ByteArray
}

val Context.pairingDataStore: DataStore<Preferences> by preferencesDataStore(name = "pairing")

/**
 * Guarda um único PC pareado. A chave vai cifrada ([TokenCipher]); se não der para decifrar
 * (chave do Keystore perdida após reinstalação ou backup), o pareamento é apagado e [load] devolve null.
 */
class PairingStore(private val dataStore: DataStore<Preferences>, private val cipher: TokenCipher) {
    suspend fun load(): PairedPc? {
        val prefs = dataStore.data.first()
        val name = prefs[NAME] ?: return null
        val fingerprint = prefs[FINGERPRINT] ?: return null
        val sealed = prefs[TOKEN] ?: return null
        val token = try {
            cipher.decrypt(Base64.getDecoder().decode(sealed))
        } catch (_: GeneralSecurityException) {
            clear()
            return null
        } catch (_: IllegalArgumentException) {
            clear()
            return null
        }
        return PairedPc(name, fingerprint, token, prefs[LAST_HOST])
    }

    suspend fun save(pc: PairedPc) {
        val sealed = Base64.getEncoder().encodeToString(cipher.encrypt(pc.token))
        dataStore.edit { prefs ->
            prefs[NAME] = pc.name
            prefs[FINGERPRINT] = pc.fingerprint
            prefs[TOKEN] = sealed
            if (pc.lastHost != null) prefs[LAST_HOST] = pc.lastHost else prefs.remove(LAST_HOST)
        }
    }

    suspend fun updateLastHost(host: String) {
        dataStore.edit { prefs -> if (prefs[NAME] != null) prefs[LAST_HOST] = host }
    }

    suspend fun clear() {
        dataStore.edit { it.clear() }
    }

    private companion object {
        val NAME = stringPreferencesKey("pc_name")
        val FINGERPRINT = stringPreferencesKey("pc_fingerprint")
        val TOKEN = stringPreferencesKey("token_sealed")
        val LAST_HOST = stringPreferencesKey("last_host")
    }
}
```

`android/app/src/main/java/dev/screenshare/android/security/KeystoreTokenCipher.kt`:
```kotlin
package dev.screenshare.android.security

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import java.security.KeyStore
import javax.crypto.AEADBadTagException
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/** AES-256-GCM com chave guardada no Android Keystore (não sai do aparelho). Formato: IV (12 bytes) + texto cifrado. */
class KeystoreTokenCipher(private val alias: String = "screenshare-pairing") : TokenCipher {
    override fun encrypt(plain: ByteArray): ByteArray {
        val cipher = Cipher.getInstance(TRANSFORMATION).apply { init(Cipher.ENCRYPT_MODE, key()) }
        return cipher.iv + cipher.doFinal(plain)
    }

    override fun decrypt(sealed: ByteArray): ByteArray {
        if (sealed.size <= IV_SIZE) throw AEADBadTagException("dado cifrado curto demais")
        val cipher = Cipher.getInstance(TRANSFORMATION).apply {
            init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(TAG_BITS, sealed, 0, IV_SIZE))
        }
        return cipher.doFinal(sealed, IV_SIZE, sealed.size - IV_SIZE)
    }

    private fun key(): SecretKey {
        val keyStore = KeyStore.getInstance(KEYSTORE).apply { load(null) }
        (keyStore.getKey(alias, null) as? SecretKey)?.let { return it }
        return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, KEYSTORE).apply {
            init(
                KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                    .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                    .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                    .setKeySize(256)
                    .build(),
            )
        }.generateKey()
    }

    private companion object {
        const val KEYSTORE = "AndroidKeyStore"
        const val TRANSFORMATION = "AES/GCM/NoPadding"
        const val IV_SIZE = 12
        const val TAG_BITS = 128
    }
}
```

- [ ] **Step 5: Rodar e ver passar**

Mesmo comando do Step 3. Expected: `BUILD SUCCESSFUL`, PairingStoreTest 7/7 entre os aprovados. (O `KeystoreTokenCipher` só roda no aparelho; é verificado manualmente na Tarefa 11.)

- [ ] **Step 6: Commit**

```powershell
git add android/gradle/libs.versions.toml android/app
git commit -m "feat(android): PC pareado salvo em DataStore com a chave cifrada pelo Keystore" -m "Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Tarefa 10: Android — `Connection` com USB, Wi-Fi (TLS + AUTH) e pareamento

**Files:**
- Modify: `android/app/src/main/java/dev/screenshare/android/net/Connection.kt` (reescrever), `.../net/HostAddress.kt`
- Test: `android/app/src/test/java/dev/screenshare/android/net/ConnectionTest.kt` (reescrever)

**Interfaces:**
- Consumes: mensagens v2 (Tarefa 2); `PinnedTrustManager`, `PairingInfo`, `TestTls` (Tarefa 8); `PairedPc` (Tarefa 9).
- Produces: `const val USB_PORT = 38701`; `sealed interface ConnectTarget { data class Usb(port: Int = USB_PORT); data class Wifi(address: HostAddress, pc: PairedPc); data class Pairing(info: PairingInfo, deviceName: String) }`; `ConnectionState.Failed(reason: String, denied: DeniedReason? = null)`; `Connection(scope, screen, pingIntervalMs = 1_000, handshakeTimeoutMs = 5_000, onPaired: (PairedPc) -> Unit = {})` com `fun connect(target: ConnectTarget)` e `fun disconnect()`.

- [ ] **Step 1: Reescrever os testes (falham)**

Em `HostAddress.kt`, logo abaixo de `const val DEFAULT_PORT = 38700`:
```kotlin
/** Porta do USB: o app conecta em 127.0.0.1 depois do `adb reverse tcp:38701 tcp:38701`. */
const val USB_PORT = 38701
```

`android/app/src/test/java/dev/screenshare/android/net/ConnectionTest.kt`:
```kotlin
package dev.screenshare.android.net

import dev.screenshare.android.pairing.PairingInfo
import dev.screenshare.android.protocol.AuthMessage
import dev.screenshare.android.protocol.ConfigMessage
import dev.screenshare.android.protocol.DeniedMessage
import dev.screenshare.android.protocol.DeniedReason
import dev.screenshare.android.protocol.HelloMessage
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
import java.net.InetAddress
import java.net.ServerSocket
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

/** Testa a Connection contra servidores reais em loopback: TCP puro (USB) e TLS com certificado de teste (Wi-Fi). */
class ConnectionTest {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val server = ServerSocket(0, 1, InetAddress.getLoopbackAddress())
    private val tlsServer = TestTls.serverSocket("host")
    private val screen = ScreenInfo(width = 2400, height = 1080, densityDpi = 420)
    private val config = ConfigMessage(2400, 1080, VideoCodec.H264, 8000, ByteArray(0))
    private val hostFingerprint = TestTls.fingerprint("host")
    private val paired = mutableListOf<PairedPc>()

    @After
    fun tearDown() {
        scope.cancel()
        server.close()
        tlsServer.close()
    }

    private fun newConnection() = Connection(
        scope, screen, pingIntervalMs = 20, handshakeTimeoutMs = 2_000,
        onPaired = { synchronized(paired) { paired.add(it) } },
    )

    private fun usb() = ConnectTarget.Usb(port = server.localPort)

    private fun wifi(fingerprint: String = hostFingerprint) = ConnectTarget.Wifi(
        HostAddress("127.0.0.1", tlsServer.localPort), PairedPc("PC de teste", fingerprint, TOKEN, null),
    )

    private fun pairing() = ConnectTarget.Pairing(
        PairingInfo("127.0.0.1", tlsServer.localPort, hostFingerprint, SECRET, "PC de teste"), "Pixel 8",
    )

    private fun Socket.send(message: Message) = getOutputStream().apply { write(MessageCodec.encode(message)); flush() }

    private suspend fun Connection.await(predicate: (ConnectionState) -> Boolean): ConnectionState =
        withTimeout(5_000) { state.first(predicate) }

    /** Lado PC no USB (TCP puro). */
    private fun serving(block: (Socket, MessageReader) -> Unit) = scope.async {
        server.accept().use { socket -> block(socket, MessageReader(socket.getInputStream())) }
    }

    /** Lado PC no Wi-Fi (TLS com o certificado "host"). */
    private fun servingTls(block: (Socket, MessageReader) -> Unit) = scope.async {
        tlsServer.accept().use { socket -> block(socket, MessageReader(socket.getInputStream())) }
    }

    private fun hello() = HelloMessage(MessageCodec.PROTOCOL_VERSION, 2400, 1080, 420, VideoCodec.ALL)

    @Test
    fun usbHandshakeSendsHelloWithScreenInfoAndEndsConnected() = runBlocking {
        var received: Message? = null
        val serverSide = serving { socket, reader ->
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
        val serverSide = serving { socket, reader ->
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
        val serverSide = serving { _, reader -> reader.read() } // lê o HELLO e fecha
        val connection = newConnection()

        connection.connect(usb())
        val state = connection.await { it is ConnectionState.Failed }

        assertTrue(state is ConnectionState.Failed)
        serverSide.await()
    }

    @Test
    fun serverDroppingAfterConnectedFails() = runBlocking {
        val serverSide = serving { socket, reader ->
            reader.read()
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
        val serverSide = serving { socket, reader ->
            reader.read()
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
        val port = server.localPort
        server.close() // nada escutando nessa porta
        val connection = newConnection()

        connection.connect(ConnectTarget.Usb(port))
        val state = connection.await { it is ConnectionState.Failed }

        assertTrue((state as ConnectionState.Failed).reason.isNotBlank())
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
        val serverSide = serving { socket, reader ->
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

    private companion object {
        val SECRET = ByteArray(32) { it.toByte() }
        val TOKEN = ByteArray(32) { (0xA0 + it).toByte() }
    }
}
```

- [ ] **Step 2: Rodar e ver falhar**

```powershell
$env:JAVA_HOME = 'C:\Program Files\Android\Android Studio\jbr'; .\android\gradlew.bat -p android :app:testDebugUnitTest
```
Expected: FAIL na compilação — `ConnectTarget` não existe e `Connection` não aceita `onPaired`. (O `ConnectionViewModel` também deixa de compilar até a Tarefa 11; para esta tarefa, trocar nele `fun connect(address: HostAddress) = connection.connect(address.host, address.port)` por `fun connect(address: HostAddress) = connection.connect(ConnectTarget.Usb())` provisoriamente — a Tarefa 11 reescreve o arquivo.)

- [ ] **Step 3: Implementar — reescrever `android/app/src/main/java/dev/screenshare/android/net/Connection.kt`**

```kotlin
package dev.screenshare.android.net

import dev.screenshare.android.pairing.PairingInfo
import dev.screenshare.android.protocol.AuthMessage
import dev.screenshare.android.protocol.ConfigMessage
import dev.screenshare.android.protocol.DeniedMessage
import dev.screenshare.android.protocol.DeniedReason
import dev.screenshare.android.protocol.HelloMessage
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
import java.io.IOException
import java.io.OutputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.security.cert.CertificateException
import javax.net.ssl.SSLException
import javax.net.ssl.SSLSocket
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

/** Tamanho e densidade da tela do celular, enviados ao PC no HELLO. */
data class ScreenInfo(val width: Int, val height: Int, val densityDpi: Int)

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
    /** Cabo USB: 127.0.0.1 depois do `adb reverse tcp:38701 tcp:38701`, sem TLS nem pareamento. */
    data class Usb(val port: Int = USB_PORT) : ConnectTarget

    /** Wi-Fi com o PC já pareado: TLS com a digital fixa, depois AUTH. */
    data class Wifi(val address: HostAddress, val pc: PairedPc) : ConnectTarget

    /** Primeiro contato vindo do QR: TLS com a digital do QR, PAIR → PAIRED, depois AUTH. */
    data class Pairing(val info: PairingInfo, val deviceName: String) : ConnectTarget
}

/**
 * Conexão com o host: (TLS + PAIR/AUTH no Wi-Fi) → HELLO → CONFIG, depois PING periódico para medir a latência.
 * Uma conexão por vez; chamar [connect] de novo encerra a anterior.
 * [onPaired] é chamado (na thread de IO) quando um pareamento termina, com os dados a salvar.
 */
class Connection(
    private val scope: CoroutineScope,
    private val screen: ScreenInfo,
    private val pingIntervalMs: Long = 1_000,
    private val handshakeTimeoutMs: Int = 5_000,
    private val onPaired: (PairedPc) -> Unit = {},
) {
    private val _state = MutableStateFlow<ConnectionState>(ConnectionState.Disconnected)
    val state: StateFlow<ConnectionState> = _state.asStateFlow()

    private var job: Job? = null
    private var socket: Socket? = null

    fun connect(target: ConnectTarget) {
        disconnect()
        _state.value = ConnectionState.Connecting
        val s = Socket().also { socket = it } // guardado já aqui para disconnect() poder interromper o connect
        job = scope.launch(Dispatchers.IO) { run(s, target) }
    }

    fun disconnect() {
        job?.cancel()
        job = null
        socket?.closeQuietly() // destrava a leitura bloqueante (fechar o socket de baixo também derruba o TLS)
        socket = null
        _state.value = ConnectionState.Disconnected
    }

    private suspend fun run(raw: Socket, target: ConnectTarget) {
        try {
            val (host, port) = target.endpoint()
            raw.tcpNoDelay = true
            raw.connect(InetSocketAddress(host, port), handshakeTimeoutMs)
            val s = when (target) {
                is ConnectTarget.Usb -> raw
                is ConnectTarget.Wifi -> raw.upgradeToTls(host, port, target.pc.fingerprint)
                is ConnectTarget.Pairing -> raw.upgradeToTls(host, port, target.info.fingerprint)
            }
            s.soTimeout = handshakeTimeoutMs
            val out = s.getOutputStream()
            val reader = MessageReader(s.getInputStream())

            when (target) {
                is ConnectTarget.Usb -> Unit
                is ConnectTarget.Wifi -> out.send(AuthMessage(target.pc.token))
                is ConnectTarget.Pairing -> {
                    out.send(PairMessage(target.info.secret, target.deviceName))
                    val token = when (val reply = reader.read()) {
                        is PairedMessage -> reply.token
                        is DeniedMessage -> return denied(reply.reason)
                        else -> return fail("Resposta inesperada do PC durante o pareamento")
                    }
                    onPaired(PairedPc(target.info.pcName, target.info.fingerprint, token, host))
                    out.send(AuthMessage(token))
                }
            }

            out.send(HelloMessage(MessageCodec.PROTOCOL_VERSION, screen.width, screen.height, screen.densityDpi, VideoCodec.ALL))
            val config = when (val reply = reader.read()) {
                is ConfigMessage -> reply
                is DeniedMessage -> return denied(reply.reason)
                else -> return fail("O PC recusou a conexão")
            }

            s.soTimeout = 0 // daqui em diante a leitura bloqueia até chegar um PONG ou a conexão cair
            _state.value = ConnectionState.Connected(config, rttMs = null)
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
                    val message = reader.read() ?: break
                    if (message is PongMessage) {
                        val rtt = (nowMicros() - message.timestampUs) / 1_000.0
                        _state.update { if (it is ConnectionState.Connected) it.copy(rttMs = rtt) else it }
                    }
                }
            } finally {
                pinger.cancel()
            }
            fail("Conexão perdida")
        } catch (e: IOException) { // inclui ProtocolException, EOFException e erros de TLS
            fail(e.describe())
        } finally {
            raw.closeQuietly()
        }
    }

    private fun ConnectTarget.endpoint(): kotlin.Pair<String, Int> = when (this) {
        is ConnectTarget.Usb -> "127.0.0.1" to port
        is ConnectTarget.Wifi -> address.host to address.port
        is ConnectTarget.Pairing -> info.host to info.port
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
        this is ProtocolException -> "Resposta inválida do PC: $message"
        this is SSLException && causes().any { it is CertificateException } ->
            "Este não é o PC pareado (certificado diferente). Pareie de novo."
        this is SSLException -> "Falha na conexão segura: ${message ?: "erro de TLS"}"
        else -> message?.takeIf { it.isNotBlank() } ?: "Erro de rede"
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

- [ ] **Step 4: Rodar e ver passar**

Mesmo comando do Step 2. Expected: `BUILD SUCCESSFUL`, ConnectionTest 12/12 entre os aprovados.

- [ ] **Step 5: Commit**

```powershell
git add android/app/src
git commit -m "feat(android): conexão por USB, Wi-Fi com TLS fixo + AUTH e pareamento pelo QR" -m "Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Tarefa 11: Android — leitor de QR, descoberta filtrada e telas

**Files:**
- Modify: `android/gradle/libs.versions.toml`, `android/app/build.gradle.kts` (`play-services-code-scanner` 16.1.0)
- Modify: `android/app/src/main/AndroidManifest.xml`
- Modify: `android/app/src/main/java/dev/screenshare/android/net/HostDiscovery.kt`
- Modify (reescrever): `.../net/ConnectionViewModel.kt`, `.../ui/ScreenShareApp.kt`, `.../ui/HostListScreen.kt`
- Test: `android/app/src/test/java/dev/screenshare/android/net/HostFilterTest.kt`

**Interfaces:**
- Consumes: `Connection`/`ConnectTarget`/`ConnectionState.Failed.denied` (Tarefa 10); `PairingStore`, `PairedPc`, `KeystoreTokenCipher`, `pairingDataStore` (Tarefa 9); `PairingUri`, `deviceNameOf`, `Fingerprint.mdnsId` (Tarefa 8).
- Produces: `DiscoveredHost(name, address, mdnsId: String? = null)`; `fun List<DiscoveredHost>.ofPairedPc(pc: PairedPc?): List<DiscoveredHost>`; `ConnectionViewModel` com `pairedPc`, `message`, `hosts`, `connectionState`, `pair(qrText)`, `showMessage(text)`, `connectWifi(address)`, `connectUsb()`, `forgetPc()`, `disconnect()`.

- [ ] **Step 1: Teste que falha**

`android/app/src/test/java/dev/screenshare/android/net/HostFilterTest.kt`:
```kotlin
package dev.screenshare.android.net

import dev.screenshare.android.security.PairedPc
import org.junit.Assert.assertEquals
import org.junit.Test

class HostFilterTest {
    // digital com os bytes 0x20..0x3F → mdnsId "2021222324252627"
    private val pc = PairedPc("PC da Sala", "ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8", ByteArray(32), null)
    private val mine = DiscoveredHost("SALA", HostAddress("192.168.0.10", 38700), "2021222324252627")
    private val other = DiscoveredHost("VIZINHO", HostAddress("192.168.0.20", 38700), "ffffffffffffffff")
    private val old = DiscoveredHost("ANTIGO", HostAddress("192.168.0.30", 38700), null)

    @Test
    fun withAPairedPcOnlyItsAnnouncementsAreShown() =
        assertEquals(listOf(mine), listOf(mine, other, old).ofPairedPc(pc))

    @Test
    fun withoutPairingNothingIsShown() =
        assertEquals(emptyList<DiscoveredHost>(), listOf(mine, other, old).ofPairedPc(null))
}
```

- [ ] **Step 2: Rodar e ver falhar**

```powershell
$env:JAVA_HOME = 'C:\Program Files\Android\Android Studio\jbr'; .\android\gradlew.bat -p android :app:testDebugUnitTest
```
Expected: FAIL na compilação — `DiscoveredHost` não tem `mdnsId` e `ofPairedPc` não existe.

- [ ] **Step 3: Implementar**

`libs.versions.toml`: em `[versions]` `codeScanner = "16.1.0"`; em `[libraries]`:
```toml
play-services-code-scanner = { group = "com.google.android.gms", name = "play-services-code-scanner", version.ref = "codeScanner" }
```
`app/build.gradle.kts`, em `dependencies`: `implementation(libs.play.services.code.scanner)`.

`AndroidManifest.xml`, dentro de `<application>` (antes de `<activity>`):
```xml
        <!-- Baixa o leitor de QR do Google Play Services junto com o app -->
        <meta-data
            android:name="com.google.mlkit.vision.DEPENDENCIES"
            android:value="barcode_ui" />
```

`HostDiscovery.kt`:
1. Imports: `dev.screenshare.android.security.Fingerprint`, `dev.screenshare.android.security.PairedPc`.
2. Trocar `data class DiscoveredHost(val name: String, val address: HostAddress)` por:
```kotlin
/** Um PC anunciando o serviço ScreenShare na rede local. [mdnsId] é o TXT `fp` (16 hex da digital do PC). */
data class DiscoveredHost(val name: String, val address: HostAddress, val mdnsId: String? = null)

/** Com um PC pareado, só os anúncios dele (TXT `fp` igual); sem pareamento, nenhum (é preciso parear primeiro). */
fun List<DiscoveredHost>.ofPairedPc(pc: PairedPc?): List<DiscoveredHost> {
    val id = pc?.let { Fingerprint.mdnsId(it.fingerprint) } ?: return emptyList()
    return filter { it.mdnsId == id }
}
```
3. Em `onServiceResolved`, trocar `found[info.serviceName] = DiscoveredHost(info.serviceName, HostAddress(ip, info.port))` por:
```kotlin
                        if (ip != null) {
                            val mdnsId = info.attributes["fp"]?.let { String(it, Charsets.UTF_8) }
                            found[info.serviceName] = DiscoveredHost(info.serviceName, HostAddress(ip, info.port), mdnsId)
                        }
```
(removendo o `if (ip != null)` original dessa linha).

Reescrever `android/app/src/main/java/dev/screenshare/android/net/ConnectionViewModel.kt`:
```kotlin
package dev.screenshare.android.net

import android.app.Application
import android.os.Build
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import dev.screenshare.android.pairing.PairingUri
import dev.screenshare.android.pairing.deviceNameOf
import dev.screenshare.android.protocol.DeniedReason
import dev.screenshare.android.security.KeystoreTokenCipher
import dev.screenshare.android.security.PairedPc
import dev.screenshare.android.security.PairingStore
import dev.screenshare.android.security.pairingDataStore
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.catch
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch

/** Liga pareamento, descoberta e conexão à interface. */
class ConnectionViewModel(application: Application) : AndroidViewModel(application) {
    private val store = PairingStore(application.pairingDataStore, KeystoreTokenCipher())

    private val _pairedPc = MutableStateFlow<PairedPc?>(null)
    val pairedPc: StateFlow<PairedPc?> = _pairedPc.asStateFlow()

    private val _message = MutableStateFlow<String?>(null)

    /** Aviso para o usuário fora do estado da conexão (QR inválido, leitor indisponível…). */
    val message: StateFlow<String?> = _message.asStateFlow()

    private val connection = Connection(
        viewModelScope, landscapeScreenInfo(application),
        onPaired = { pc -> viewModelScope.launch { store.save(pc) }; _pairedPc.value = pc },
    )

    val connectionState: StateFlow<ConnectionState> = connection.state

    /** PCs encontrados por mDNS, só os do PC pareado; a busca só roda enquanto a lista está na tela. */
    val hosts: StateFlow<List<DiscoveredHost>> = HostDiscovery(application).hosts()
        .catch { emit(emptyList()) } // sem permissão/serviço NSD: a entrada manual continua funcionando
        .combine(pairedPc) { found, pc -> found.ofPairedPc(pc) }
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(stopTimeoutMillis = 5_000), emptyList())

    init {
        viewModelScope.launch { _pairedPc.value = store.load() }
        viewModelScope.launch {
            connection.state.collect { state ->
                // O PC removeu este celular: esquece o pareamento para o usuário parear de novo.
                if (state is ConnectionState.Failed && state.denied == DeniedReason.UNKNOWN_DEVICE) forgetPc()
            }
        }
    }

    /** Texto lido do QR. */
    fun pair(qrText: String) {
        val info = PairingUri.parse(qrText)
        if (info == null) {
            _message.value = "Este QR não é de pareamento do ScreenShare."
            return
        }
        _message.value = null
        connection.connect(ConnectTarget.Pairing(info, deviceNameOf(Build.MODEL)))
    }

    fun showMessage(text: String) {
        _message.value = text
    }

    fun connectWifi(address: HostAddress) {
        val pc = _pairedPc.value
        if (pc == null) {
            _message.value = "Pareie com o PC primeiro."
            return
        }
        _message.value = null
        connection.connect(ConnectTarget.Wifi(address, pc))
        viewModelScope.launch { store.updateLastHost(address.host) }
    }

    fun connectUsb() {
        _message.value = null
        connection.connect(ConnectTarget.Usb())
    }

    fun forgetPc() {
        _pairedPc.value = null
        viewModelScope.launch { store.clear() }
    }

    fun disconnect() = connection.disconnect()

    override fun onCleared() = connection.disconnect()

    private companion object {
        /** A tela do celular é usada como monitor na horizontal: largura é sempre o lado maior. */
        fun landscapeScreenInfo(application: Application): ScreenInfo {
            val metrics = application.resources.displayMetrics
            return ScreenInfo(
                width = maxOf(metrics.widthPixels, metrics.heightPixels),
                height = minOf(metrics.widthPixels, metrics.heightPixels),
                densityDpi = metrics.densityDpi,
            )
        }
    }
}
```

Reescrever `android/app/src/main/java/dev/screenshare/android/ui/ScreenShareApp.kt`:
```kotlin
package dev.screenshare.android.ui

import android.content.Context
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.platform.LocalContext
import com.google.mlkit.vision.barcode.common.Barcode
import com.google.mlkit.vision.codescanner.GmsBarcodeScannerOptions
import com.google.mlkit.vision.codescanner.GmsBarcodeScanning
import dev.screenshare.android.net.ConnectionState
import dev.screenshare.android.net.ConnectionViewModel

/** Escolhe a tela pelo estado da conexão: conectado → tela imersiva; qualquer outro estado → lista de PCs. */
@Composable
fun ScreenShareApp(viewModel: ConnectionViewModel) {
    val state by viewModel.connectionState.collectAsState()
    val context = LocalContext.current

    when (val current = state) {
        is ConnectionState.Connected -> ImmersiveScreen(current, onDisconnect = viewModel::disconnect)
        else -> {
            val hosts by viewModel.hosts.collectAsState()
            val pairedPc by viewModel.pairedPc.collectAsState()
            val message by viewModel.message.collectAsState()
            HostListScreen(
                hosts = hosts,
                state = current,
                pairedPc = pairedPc,
                message = message,
                onPair = { scanPairingQr(context, onResult = viewModel::pair, onError = viewModel::showMessage) },
                onConnect = viewModel::connectWifi,
                onConnectUsb = viewModel::connectUsb,
                onForget = viewModel::forgetPc,
            )
        }
    }
}

/** Abre o leitor de QR do Google Play Services (traz a própria tela de câmera; o app não pede permissão). */
private fun scanPairingQr(context: Context, onResult: (String) -> Unit, onError: (String) -> Unit) {
    val options = GmsBarcodeScannerOptions.Builder().setBarcodeFormats(Barcode.FORMAT_QR_CODE).build()
    GmsBarcodeScanning.getClient(context, options).startScan()
        .addOnSuccessListener { barcode -> barcode.rawValue?.let(onResult) ?: onError("O QR está vazio.") }
        .addOnFailureListener { onError("Não foi possível abrir o leitor de QR: ${it.message}") }
}
```

Reescrever `android/app/src/main/java/dev/screenshare/android/ui/HostListScreen.kt`:
```kotlin
package dev.screenshare.android.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import dev.screenshare.android.net.ConnectionState
import dev.screenshare.android.net.DEFAULT_PORT
import dev.screenshare.android.net.DiscoveredHost
import dev.screenshare.android.net.HostAddress
import dev.screenshare.android.net.USB_PORT
import dev.screenshare.android.net.parseHostAddress
import dev.screenshare.android.security.PairedPc

@Composable
fun HostListScreen(
    hosts: List<DiscoveredHost>,
    state: ConnectionState,
    pairedPc: PairedPc?,
    message: String?,
    onPair: () -> Unit,
    onConnect: (HostAddress) -> Unit,
    onConnectUsb: () -> Unit,
    onForget: () -> Unit,
) {
    val connecting = state is ConnectionState.Connecting

    Surface(modifier = Modifier.fillMaxSize()) {
        Column(
            modifier = Modifier.safeDrawingPadding().padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            Text("ScreenShare", style = MaterialTheme.typography.headlineMedium)

            when {
                connecting -> LinearProgressIndicator(modifier = Modifier.fillMaxWidth())
                state is ConnectionState.Failed -> Text(state.reason, color = MaterialTheme.colorScheme.error)
            }
            message?.let { Text(it, color = MaterialTheme.colorScheme.error) }

            if (pairedPc == null) {
                Text(
                    "Para usar pelo Wi-Fi, pareie com o PC: no ScreenShare do PC, peça para parear e escaneie o QR.",
                    style = MaterialTheme.typography.bodyMedium,
                )
                Button(onClick = onPair, enabled = !connecting) { Text("Parear com PC (QR)") }
            } else {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text("PC pareado: ${pairedPc.name}", style = MaterialTheme.typography.titleMedium, modifier = Modifier.weight(1f))
                    TextButton(onClick = onForget, enabled = !connecting) { Text("Esquecer") }
                }
                Text("Na rede", style = MaterialTheme.typography.titleSmall)
                if (hosts.isEmpty()) {
                    Text(
                        "Procurando o PC… Abra o ScreenShare nele (mesma rede Wi-Fi) ou digite o endereço abaixo.",
                        style = MaterialTheme.typography.bodyMedium,
                    )
                }
                LazyColumn(
                    modifier = Modifier.weight(1f, fill = false),
                    verticalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    items(hosts, key = { it.name }) { host ->
                        Card(modifier = Modifier.fillMaxWidth().clickable(enabled = !connecting) { onConnect(host.address) }) {
                            Column(modifier = Modifier.padding(16.dp)) {
                                Text(host.name, style = MaterialTheme.typography.titleMedium)
                                Text("${host.address.host}:${host.address.port}", style = MaterialTheme.typography.bodySmall)
                            }
                        }
                    }
                }
                ManualAddress(initial = pairedPc.lastHost.orEmpty(), enabled = !connecting, onConnect = onConnect)
            }

            OutlinedButton(onClick = onConnectUsb, enabled = !connecting) { Text("Conectar por cabo USB") }
            Text(
                "No cabo: ative a depuração USB e rode no PC: adb reverse tcp:$USB_PORT tcp:$USB_PORT",
                style = MaterialTheme.typography.bodySmall,
            )
        }
    }
}

/** Plano B quando o mDNS não funciona na rede: digitar o IP do PC pareado. */
@Composable
private fun ManualAddress(initial: String, enabled: Boolean, onConnect: (HostAddress) -> Unit) {
    var text by rememberSaveable { mutableStateOf(initial) }
    val address = remember(text) { parseHostAddress(text) }

    Row(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
        OutlinedTextField(
            value = text,
            onValueChange = { text = it },
            modifier = Modifier.weight(1f),
            label = { Text("Endereço do PC") },
            placeholder = { Text("192.168.0.10 (porta $DEFAULT_PORT)") },
            singleLine = true,
            isError = text.isNotBlank() && address == null,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
        )
        Button(onClick = { address?.let(onConnect) }, enabled = enabled && address != null) {
            Text("Conectar")
        }
    }
}
```

- [ ] **Step 4: Rodar e ver passar**

```powershell
$env:JAVA_HOME = 'C:\Program Files\Android\Android Studio\jbr'; .\android\gradlew.bat -p android :app:testDebugUnitTest :app:assembleDebug
```
Expected: `BUILD SUCCESSFUL`, HostFilterTest 2/2 entre os aprovados, APK gerado.

- [ ] **Step 5: Verificação manual (celular real)**

1. PC: `dotnet run --project host/ScreenShare.DevHost`, digitar `p`.
2. Celular (mesmo Wi-Fi): instalar o APK, tocar em **Parear com PC (QR)**, escanear → a tela imersiva abre com a latência; no console do PC aparece "Aparelho pareado" e "Autenticado".
3. Voltar, fechar e reabrir o app → "PC pareado: …", o PC aparece em "Na rede"; tocar → conecta sem QR.
4. PC: `l` e `r <id>`; no celular, conectar de novo → "Este celular não está mais pareado…" e o botão **Parear com PC (QR)** volta.
5. Escanear um QR qualquer (ex.: de um site) → "Este QR não é de pareamento do ScreenShare."
6. USB: `adb reverse tcp:38701 tcp:38701` e **Conectar por cabo USB** → conecta sem pareamento.

- [ ] **Step 6: Commit**

```powershell
git add android
git commit -m "feat(android): pareamento por QR, descoberta só do PC pareado e conexão por USB na interface" -m "Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Tarefa 12: README e guia do código

**Files:**
- Modify: `README.md`, `docs/guia-do-codigo.md`

**Interfaces:**
- Consumes: tudo das Tarefas 1–11. Sem código.

- [ ] **Step 1: README**

1. Substituir a seção `## 🔒 Segurança` inteira por:
```markdown
## 🔒 Segurança

- **Wi-Fi:** o celular precisa ser **pareado** uma vez escaneando o QR que o PC mostra. Depois disso a conexão é criptografada (TLS) e o celular só aceita o certificado daquele PC; o PC só aceita celulares pareados e permite remover qualquer um.
- **Cabo USB:** sem pareamento nem criptografia: o tráfego não passa pela rede e a porta do USB (38701) só aceita conexões do próprio PC.
```
2. No roadmap, logo depois da linha do **App Android e host de desenvolvimento**, acrescentar:
```markdown
- [x] **Pareamento e autenticação** — QR + TLS com certificado fixo no Wi-Fi (protocolo v2); USB direto em loopback
```
3. Na seção `## 🚀 Começando`, substituir o parágrafo e o bloco que explicam como rodar o host de desenvolvimento por:
````markdown
Para testar o app sem o host real, rode o host de desenvolvimento (libere a porta 38700 no Firewall do Windows se ele pedir):

```bash
dotnet run --project host/ScreenShare.DevHost
```

No console, digite `p` para mostrar o QR de pareamento e escaneie com o app (botão **Parear com PC**). `l` lista os celulares pareados e `r <id>` remove um. Pelo cabo USB, rode `adb reverse tcp:38701 tcp:38701` e use **Conectar por cabo USB** no app.
````

- [ ] **Step 2: Guia do código**

Em `docs/guia-do-codigo.md`:
1. Toda menção ao valor da versão do protocolo (`ProtocolVersion = 1`, "versão 1", `HELLO v1`) passa a 2, explicando numa frase que a v2 acrescentou o pareamento.
2. No mapa de arquivos, acrescentar `host/ScreenShare.Core/Security/` (`HostIdentity.cs`, `PairingSession.cs`, `PairingUri.cs`, `DeviceRegistry.cs`) com uma linha de explicação cada.
3. Acrescentar uma subseção curta "Pareamento e autenticação" no passo a passo, explicando para iniciante: o que é a digital do certificado, por que o QR leva um segredo de uso único, por que o PC guarda só o hash da chave e por que o USB não precisa disso. Copiar trechos de código do código real, não reescrever de memória.
4. Atualizar a contagem de testes citada na seção de testes para o resultado de `dotnet test host` ao fim da Tarefa 11.

- [ ] **Step 3: Conferência**

`dotnet test host` e `.\android\gradlew.bat -p android :app:testDebugUnitTest` continuam verdes; reler os trechos alterados contra o código.

- [ ] **Step 4: Commit**

```powershell
git add README.md docs/guia-do-codigo.md
git commit -m "docs: README e guia com pareamento por QR e protocolo v2" -m "Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## Verificação final

1. `dotnet test host` — todos aprovados, 0 avisos.
2. `$env:JAVA_HOME = 'C:\Program Files\Android\Android Studio\jbr'; .\android\gradlew.bat -p android :app:testDebugUnitTest :app:assembleDebug` — `BUILD SUCCESSFUL`.
3. Teste manual da Tarefa 11, Step 5, num celular real.
4. PR da branch `pareamento` para a `main` (o CI exige `host-tests` e `android-tests`).
