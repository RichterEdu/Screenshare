# Guia do código — ScreenShare (host Windows, C#)

Este guia explica o código que já existe em `host/`, pensado para quem nunca viu C#. Estado descrito: commit `3c9da16` (protocolo v1 com HELLO, PING, PONG e KEYFRAME_REQ). Quando o código avançar, este guia precisa ser atualizado.

## 1. Visão geral em 1 minuto

O projeto transforma o celular Android numa segunda tela do PC:

- O **PC (host, C#)** captura a tela, comprime em vídeo e manda para o celular.
- O **celular (Android, Kotlin)** mostra o vídeo e devolve os toques.

Os dois conversam por **uma conexão TCP**, que é só um "cano" por onde passam bytes. Os bytes precisam ter um formato combinado, senão um lado não entende o outro. Esse formato é o **protocolo** (descrito em `docs/protocol.md`).

O código atual faz uma coisa só: **traduzir entre objetos C# (`HelloMessage`, `PingMessage`...) e bytes**. Ainda não abre rede, não captura tela, não tem vídeo. Isso vem nas próximas partes do plano.

```
objeto C#  ──Encode──▶  bytes  ──(rede, futuro)──▶  bytes  ──Decode──▶  objeto C#
PingMessage(123)                                                       PingMessage(123)
```

## 2. Mapa dos arquivos

| Arquivo | Para que serve |
|---|---|
| `host/ScreenShare.slnx` | "Solução": lista os projetos para o `dotnet`/Visual Studio abrirem juntos. |
| `host/ScreenShare.Core/ScreenShare.Core.csproj` | Projeto da biblioteca principal (a lógica). Define .NET 10 e liga `ImplicitUsings` e `Nullable` (ver glossário). |
| `host/ScreenShare.Core/Protocol/Messages.cs` | Define **quais mensagens existem** e quais dados cada uma carrega. |
| `host/ScreenShare.Core/Protocol/MessageCodec.cs` | O tradutor: `Encode` (objeto → bytes) e `Decode` (bytes → objeto). |
| `host/ScreenShare.Core/Protocol/PayloadWriter.cs` | Ferramenta que **escreve** números em sequência dentro de um array de bytes. |
| `host/ScreenShare.Core/Protocol/PayloadReader.cs` | Ferramenta que **lê** números em sequência de um array de bytes. |
| `host/ScreenShare.Core/Protocol/ProtocolException.cs` | O tipo de erro lançado quando os bytes violam o protocolo. |
| `host/ScreenShare.Tests/...` | Projeto de testes automáticos (xUnit). |
| `docs/protocol.md` | Especificação do protocolo, a "fonte da verdade". |
| `docs/protocol-vectors/*.hex` | Bytes de referência, usados pelo C# e pelo Kotlin para provarem que falam igual. |

## 3. Glossário de C# (com exemplos do próprio código)

**`namespace ScreenShare.Core.Protocol;`**
Uma "pasta lógica" para organizar nomes. Evita dois arquivos com classes de mesmo nome brigarem. Quem quer usar essas classes escreve `using ScreenShare.Core.Protocol;`.

**`using System.Buffers.Binary;`**
Importa um namespace da biblioteca padrão do .NET. Este em especial tem funções para converter números em bytes (`BinaryPrimitives`).

**`ImplicitUsings` (no `.csproj`)**
Faz o compilador adicionar sozinho `using` comuns (`System`, `System.IO`, `System.Linq`...). Por isso arquivos usam `IOException` ou `File` sem importar nada.

**`Nullable` (no `.csproj`)**
Liga um recurso de segurança: o compilador avisa quando algo que pode ser `null` é usado sem checagem. Um tipo com `?` (ex.: `char[]?`) significa "pode ser nulo"; sem `?`, não pode.

**Tipos numéricos**

| C# | Tamanho | No protocolo |
|---|---|---|
| `byte` | 1 byte, 0..255 | `u8` |
| `ushort` | 2 bytes, sem sinal | `u16` |
| `uint` | 4 bytes, sem sinal | `u32` |
| `ulong` | 8 bytes, sem sinal | `u64` |
| `float` | 4 bytes, decimal | `f32` |
| `byte[]` | array (lista fixa) de bytes | |

"Sem sinal" = só números ≥ 0 (o "u" vem de *unsigned*).

**`const`**
Valor fixo, decidido na compilação. Ex.: `public const int HeaderSize = 5;`.

**`public` / `private` / `internal`**
Quem pode usar: `public` = qualquer código; `private` = só dentro da própria classe; `internal` = só dentro do mesmo projeto. `PayloadReader` e `PayloadWriter` são `internal`: são ferramentas de bastidor, ninguém de fora precisa delas.

**`static class`**
Classe que não vira objeto: só agrupa funções. `MessageCodec.Encode(...)` é chamado direto, sem `new`.

**`enum`**
Lista de nomes para números. `MessageType.Ping` vale `5`. O `: byte` em `enum MessageType : byte` diz que cabe em 1 byte.

**`[Flags]` e o operador `|`**
Um `enum` com `[Flags]` guarda **combinações** de opções, cada uma num bit diferente:

```
H264 = 1  = 0b01
H265 = 2  = 0b10
H264 | H265 = 0b11 = 3     ("OU" bit a bit: suporta os dois)
```

No `Decode`, `codecs & ~KnownCodecs` testa se sobrou algum bit desconhecido: `~` inverte os bits, `&` mantém só os bits em comum. Se o resultado não for 0, veio um codec que não existe.

**`record`**
Forma curta de declarar uma classe que só **guarda dados**:

```csharp
public sealed record PingMessage(ulong TimestampUs) : Message;
```

Isso já cria o campo `TimestampUs`, um construtor (`new PingMessage(123)`) e a comparação por valor: dois `PingMessage` com o mesmo número são considerados iguais (é isso que os testes usam em `Assert.Equal`).

- `abstract record Message;` = molde-base que não pode ser criado sozinho.
- `: Message` = "é um tipo de Message" (herança). Por isso o `Encode` aceita qualquer mensagem como parâmetro `Message`.
- `sealed` = ninguém pode herdar desta classe.
- `readonly record struct TouchPointer(...)` = um record leve (struct) e imutável, para dados pequenos.

**Construtor primário**
Em `ProtocolException(string message) : IOException(message)`, os parâmetros ficam logo depois do nome da classe e são repassados à classe-pai. A classe inteira cabe numa linha.

**`throw`**
Lança um erro. Quem chamou pode capturar com `try/catch`; se ninguém capturar, o programa para.

**`switch` com `case HelloMessage m:` (pattern matching)**
```csharp
switch (message)
{
    case HelloMessage m:   // "se message for um HelloMessage, chame-o de m"
        ...
}
```
Testa o **tipo real** do objeto e já te dá uma variável tipada (`m`) para usar.

**`switch` como expressão e `=>`**
```csharp
Message message = kind switch
{
    MessageType.Ping => new PingMessage(r.ReadUInt64()),
    _ => throw ...,        // "_" = qualquer outro caso
};
```
Aqui o `switch` **devolve um valor**. O `=>` também aparece em métodos de uma linha (`public byte ReadByte() => Take(1)[0];`), que é só uma forma curta de `{ return ...; }`.

**`Span<byte>` e `ReadOnlySpan<byte>`**
Uma "janela" sobre bytes que já existem na memória, **sem copiar**. `Span` permite escrever; `ReadOnlySpan` só ler. `bytes.AsSpan(5)` = janela que começa no byte 5. `_span[_position..]` = "do `_position` até o fim". É por isso que o código é rápido e quase não aloca memória.

**`ref struct`**
Um tipo que só pode viver na pilha (stack), exigência de quem guarda um `Span` dentro de si. Consequência prática: no `Decode`, o reader é passado como `ref PayloadReader r`, que significa "passe o **original**, não uma cópia", para que o avanço de `_position` seja visto por quem chamou.

**`out var bytes`**
Parâmetro de saída: a função devolve um segundo valor por esse parâmetro. `Begin(..., out var bytes)` devolve o `PayloadWriter` (retorno normal) **e** o array de bytes (via `out`).

**`readonly`**
Campo que só recebe valor no construtor; método `readonly` promete não alterar o objeto.

**`$"texto {variavel}"`**
String com valores embutidos. `{type:X2}` formata o número em hexadecimal com 2 dígitos (5 → `05`).

**`nameof(message)`**
Vira o texto `"message"` na compilação, sem risco de erro de digitação.

## 4. Conceitos de bytes

**Little-endian.** Um número maior que 1 byte precisa de uma ordem. No protocolo, o byte **menos** significativo vem primeiro. Ex.: `2400` = `0x0960` → bytes `60 09`.

**O quadro (frame).** Toda mensagem, de qualquer tipo, tem este formato:

```
 byte 0      bytes 1-4                 bytes 5 ...
┌────────┬──────────────────────┬─────────────────────┐
│  type  │ length (u32, LE)     │ payload (length B)  │
└────────┴──────────────────────┴─────────────────────┘
 1 byte         4 bytes            conteúdo da mensagem
└──── cabeçalho: HeaderSize = 5 ─┘
```

O `length` existe porque o TCP é um fluxo contínuo de bytes, sem divisão entre mensagens. O receptor lê 5 bytes, descobre o tamanho e então lê exatamente aquela quantidade.

**Limite de 16 MiB.** `MaxPayloadLength` impede alguém (ou um bug) de mandar `length` gigante e fazer o programa reservar memória demais.

**Annex-B / NAL units.** Formato usado pelo vídeo H.264/H.265 para separar pedaços da imagem comprimida. Só importa nas mensagens `CONFIG` e `FRAME` (ainda não implementadas no codec); por ora trate como "bytes de vídeo".

## 5. Passo a passo por arquivo

### 5.1 `Messages.cs`

Declara os vocabulários do protocolo:

- `MessageType`: o número que vai no byte `type` (Hello=1, Config=2, Frame=3, Touch=4, Ping=5, Pong=6, KeyframeRequest=7).
- `VideoCodec`: H264=1, H265=2 (flags combináveis).
- `TouchAction`: Down, Move, Up, Cancel (dedo encostou, moveu, soltou, gesto cancelado).
- As mensagens, todas `record`s que herdam de `Message`:

| Mensagem | Quem envia | O que carrega |
|---|---|---|
| `HelloMessage` | celular → PC (primeira) | versão do protocolo, largura/altura/dpi da tela, codecs suportados |
| `ConfigMessage` | PC → celular | resolução, codec escolhido, bitrate, config do codec |
| `FrameMessage` | PC → celular | um quadro de vídeo + se é keyframe |
| `TouchMessage` | celular → PC | lista de dedos (`TouchPointer`) com posição 0..1 e pressão |
| `PingMessage` / `PongMessage` | qualquer lado / resposta | um timestamp (para medir latência) |
| `KeyframeRequestMessage` | celular → PC | nada (só "me mande um quadro completo") |

**Keyframe** = quadro de vídeo completo, que pode ser decodificado sozinho. Os outros quadros guardam só as diferenças; se algo se perde, o celular pede um keyframe para "recomeçar".

### 5.2 `PayloadWriter.cs`

Escreve campos um depois do outro num `Span<byte>` já dimensionado.

- `_position` é o "cursor": quantos bytes já foram escritos.
- `WriteUInt16(value)` chama `BinaryPrimitives.WriteUInt16LittleEndian` para gravar 2 bytes na ordem little-endian na posição atual e depois faz `_position += 2`.
- `WriteByte`, `WriteUInt32`, `WriteUInt64`, `WriteSingle`, `WriteBytes` seguem o mesmo padrão.
- Não valida tamanho: se o span for pequeno, o .NET lança erro. Quem garante o tamanho certo é o `Begin` do codec.

### 5.3 `PayloadReader.cs`

O espelho do writer: lê campos em sequência de um `ReadOnlySpan<byte>`.

- `Take(count)` é o coração: confere se ainda há `count` bytes (`count > _span.Length - _position` → lança `ProtocolException("Payload truncado.")`), devolve a fatia e avança o cursor.
- `ReadByte`, `ReadUInt16` etc. são `Take` + conversão little-endian.
- `ReadBytes(n)` e `ReadRemaining()` devolvem **cópia** (`.ToArray()`), porque o span original pode deixar de existir depois.
- `EnsureEnd()` verifica que o cursor chegou exatamente ao fim. Sobrou byte → erro. Assim a mensagem precisa ter o tamanho **exato**.

### 5.4 `ProtocolException.cs`

Um tipo de erro próprio. Herda de `IOException`, então código de rede que já captura erros de I/O captura esse também. Use-o para "os bytes não seguem o protocolo", e não para falhas de conexão.

### 5.5 `MessageCodec.cs`

**Constantes:** `ProtocolVersion = 1`, `HeaderSize = 5`, `MaxPayloadLength = 16 MiB`, `MaxTouchPointers = 10`. `KnownCodecs` (privada) é a máscara `H264 | H265` usada para validar.

**`Encode(Message)`** — objeto → bytes:
1. O `switch` descobre qual mensagem é.
2. `Begin(tipo, tamanhoDoPayload, out bytes)` cria o array já com tamanho total (5 + payload), grava o `type` no byte 0 e o `length` nos bytes 1-4, e devolve um `PayloadWriter` posicionado logo depois do cabeçalho.
3. O `case` escreve os campos na ordem do protocolo (para HELLO: versão, largura, altura, dpi, codecs = 2+2+2+2+1 = **9** bytes).
4. Devolve `bytes`.
5. Tipo ainda não suportado → `ArgumentException`. (Hoje isso inclui Config, Frame e Touch.)

**`Decode(byte type, ReadOnlySpan<byte> payload)`** — bytes → objeto:
1. Cria um `PayloadReader` sobre o payload.
2. `kind switch` escolhe como ler cada tipo; tipo desconhecido → `ProtocolException`.
3. `r.EnsureEnd()` garante que não sobrou nem faltou byte.
4. Observe que o `Decode` recebe **só o payload**: quem lê da rede (futuro) vai ler o cabeçalho antes e entregar `type` e payload separados.

**`DecodeHello`** lê os 5 campos na ordem e valida os codecs: `None` (0) ou bits desconhecidos → erro.

### 5.6 Ordem das ações (resumo do ciclo)

```
Encode:  Message ─▶ switch ─▶ Begin (cabeçalho) ─▶ Write* (payload) ─▶ byte[]
Decode:  (type, payload) ─▶ PayloadReader ─▶ Read* ─▶ EnsureEnd ─▶ Message
```

## 6. Exemplo trabalhado: `PingMessage(123456789)`

1. `Encode` cai no `case PingMessage m`, que chama `Begin(MessageType.Ping, 8, ...)`.
2. `Begin` cria um array de `5 + 8 = 13` bytes e preenche o cabeçalho:
   - `bytes[0] = 5` (Ping)
   - bytes 1-4 = `8` em u32 little-endian = `08 00 00 00`
3. `WriteUInt64(123456789)`: `123456789` = `0x075BCD15`. Em 8 bytes little-endian (menos significativo primeiro): `15 CD 5B 07 00 00 00 00`.
4. Resultado completo, que é exatamente o conteúdo de `docs/protocol-vectors/ping.hex`:

```
05 08 00 00 00   15 CD 5B 07 00 00 00 00
└── cabeçalho ─┘ └──── payload (u64) ────┘
```

O teste `Ping_matches_vector` confere os dois sentidos: o `Encode` gera esses bytes e o `Decode` desses bytes devolve um `PingMessage(123456789)`.

## 7. Os testes

**xUnit** é o framework de testes. O `.csproj` de testes referencia o projeto `Core` e os pacotes do xUnit.

- `[Fact]` = um teste simples.
- `[Theory]` + `[InlineData(...)]` = o mesmo teste rodado várias vezes com valores diferentes. Em `Hello_with_invalid_codec_flags_is_rejected`, roda com `0` (nenhum codec) e `4` (bit desconhecido).
- `Assert.Equal(esperado, atual)` falha o teste se forem diferentes; `Assert.Throws<ProtocolException>(...)` falha se o código **não** lançar aquele erro.
- Os testes de erro cobrem: tipo desconhecido (`0x63`), payload truncado (8 bytes num HELLO que pede 9), bytes sobrando (9 bytes num PING que pede 8), flags de codec inválidas.

**Vetores compartilhados.** A pasta `docs/protocol-vectors` tem os bytes exatos de cada mensagem. O `.csproj` de testes copia esses `.hex` para a pasta de saída (a linha `<None Include="..\..\docs\protocol-vectors\*.hex" ...>`), e `Vectors.Load("ping.hex")` os lê: ignora tudo depois de `#` (comentário), separa os pares hexadecimais e converte cada um em `byte` com `Convert.ToByte(token, 16)`. O app Android (Kotlin) testa contra os **mesmos arquivos**, então os dois lados provam que falam o mesmo "idioma" sem precisarem rodar juntos.

## 8. Como rodar e o que falta

Dentro da pasta `host`:

```bash
dotnet test
```

**Ainda não implementado no codec:** `CONFIG`, `FRAME` e `TOUCH`. Os tipos já existem em `Messages.cs` e há vetores `.hex` para eles, mas `Encode` lança `ArgumentException` e `Decode` lança "tipo desconhecido" para esses três. Isso e o resto do sistema (rede, captura, vídeo, toque) vêm nas próximas partes do plano em `docs/superpowers/plans/`.
