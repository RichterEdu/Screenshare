# Guia do código — ScreenShare (host Windows, C#)

Este guia explica o código que já existe em `host/`, pensado para quem nunca viu C#. Estado descrito: protocolo **v2** (as 7 mensagens originais HELLO, CONFIG, FRAME, TOUCH, PING, PONG e KEYFRAME_REQ, mais PAIR, PAIRED, AUTH e DENIED), o `MessageReader`, a pasta `Security/` e o host de desenvolvimento com pareamento por QR. A v1 era só as 7 primeiras; a **v2 acrescentou o pareamento**, e por isso o número da versão no HELLO passou de 1 para 2. Quando o código avançar, este guia precisa ser atualizado.

## 1. Visão geral em 1 minuto

O projeto transforma o celular Android numa segunda tela do PC:

- O **PC (host, C#)** captura a tela, comprime em vídeo e manda para o celular.
- O **celular (Android, Kotlin)** mostra o vídeo e devolve os toques.

Os dois conversam por **uma conexão TCP**, que é só um "cano" por onde passam bytes. Os bytes precisam ter um formato combinado, senão um lado não entende o outro. Esse formato é o **protocolo** (descrito em `docs/protocol.md`).

O código atual faz duas coisas: **traduzir entre objetos C# (`HelloMessage`, `PingMessage`...) e bytes** (o `MessageCodec`) e **ler mensagens inteiras de um fluxo de bytes** (o `MessageReader`). A rede e o pareamento ficam em volta dele (seção 5.8, `Security/` e o `DevHost`); ainda não há captura de tela nem vídeo. Isso vem nas próximas partes do plano.

```
objeto C#  ──Encode──▶  bytes  ──(rede, futuro)──▶  bytes  ──Decode──▶  objeto C#
PingMessage(123)                                                       PingMessage(123)
```

No caminho de volta, o `MessageReader` lê o fluxo de bytes, junta os pedaços de uma mensagem inteira e chama o `Decode` por você (seção 5.6).

## 2. Mapa dos arquivos

| Arquivo | Para que serve |
|---|---|
| `host/ScreenShare.slnx` | "Solução": lista os projetos para o `dotnet`/Visual Studio abrirem juntos. |
| `host/ScreenShare.Core/ScreenShare.Core.csproj` | Projeto da biblioteca principal (a lógica). Define .NET 10 e liga `ImplicitUsings` e `Nullable` (ver glossário). |
| `host/ScreenShare.Core/Protocol/Messages.cs` | Define **quais mensagens existem** e quais dados cada uma carrega. |
| `host/ScreenShare.Core/Protocol/MessageCodec.cs` | O tradutor: `Encode` (objeto → bytes) e `Decode` (bytes → objeto). |
| `host/ScreenShare.Core/Protocol/MessageReader.cs` | O leitor: lê mensagens **completas** de um fluxo de bytes (`Stream`), juntando os pedaços que o TCP entrega. |
| `host/ScreenShare.Core/Protocol/PayloadWriter.cs` | Ferramenta que **escreve** números em sequência dentro de um array de bytes. |
| `host/ScreenShare.Core/Protocol/PayloadReader.cs` | Ferramenta que **lê** números em sequência de um array de bytes. |
| `host/ScreenShare.Core/Protocol/ProtocolException.cs` | O tipo de erro lançado quando os bytes violam o protocolo. |
| `host/ScreenShare.Core/Security/HostIdentity.cs` | O certificado do PC: cria (uma vez) e carrega o certificado autoassinado, e calcula a **digital** dele. |
| `host/ScreenShare.Core/Security/PairingSession.cs` | O segredo de uso único do QR: gera, vale 2 minutos e é consumido na primeira vez que alguém o apresenta. |
| `host/ScreenShare.Core/Security/PairingUri.cs` | Monta o texto `screenshare://pair?...` que vira o QR. |
| `host/ScreenShare.Core/Security/DeviceRegistry.cs` | A lista de celulares pareados (arquivo JSON): guarda só o **hash** da chave de cada um. |
| `host/ScreenShare.DevHost/Program.cs` | O programa de console do host de desenvolvimento: comandos `p`, `l`, `r <id>` e anúncio mDNS; modos de driver e `--sem-monitor`. |
| `host/ScreenShare.DevHost/HostServer.cs` | O servidor TCP: porta 38700 (Wi-Fi) e porta 38701 (USB, só loopback), as duas com TLS + pareamento. |
| `host/ScreenShare.DevHost/LanAddressSelector.cs` | Escolhe quais IPs do PC anunciar e qual vai no QR. |
| `host/ScreenShare.DevHost/MonitorSetup.cs` | Ao iniciar, oferece instalar o driver de monitor virtual e cria o `VirtualMonitorManager`. |
| `host/ScreenShare.DevHost/DriverCommands.cs` | Os modos `install-driver`, `uninstall-driver` e `restart-driver`, reabertos como administrador. |
| `host/ScreenShare.Display/` | Biblioteca do monitor virtual: instala o driver, liga/desliga o monitor e guarda escala e posição (ver a seção "ScreenShare.Display"). |
| `host/ScreenShare.Tests/ScreenShare.Tests.csproj` | Projeto de testes automáticos (xUnit). Referencia o `Core`, o `Display` e o `DevHost` e copia os vetores `.hex` para a pasta de saída. |
| `host/ScreenShare.Tests/Protocol/Vectors.cs` | Lê um arquivo `.hex` de `docs/protocol-vectors` e o transforma em `byte[]`. |
| `host/ScreenShare.Tests/Protocol/MessageCodecTests.cs` | 32 casos de teste do `MessageCodec`: confere os vetores nos dois sentidos e rejeita mensagens inválidas (inclui PAIR, PAIRED, AUTH e DENIED). |
| `host/ScreenShare.Tests/Protocol/MessageReaderTests.cs` | 5 testes do `MessageReader`: mensagens em sequência, entrega de 1 byte por vez, conexão cortada no meio e tamanho gigante. |
| `host/ScreenShare.Tests/Security/` e `host/ScreenShare.Tests/DevHost/` | Testes de `Security/` (25 casos) e do host de desenvolvimento (63 casos). |
| `host/ScreenShare.Tests/Display/` | Testes da biblioteca do monitor virtual (99 casos, 1 deles de integração com o driver real, ignorado por padrão). |
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
| `bool` | `true` ou `false` (verdadeiro ou falso) | bit 0 do `flags` (`u8`) do FRAME |
| `byte[]` | array (lista fixa) de bytes | |

"Sem sinal" = só números ≥ 0 (o "u" vem de *unsigned*).

**Literais numéricos (`0.25f`, `1_000_000`, `0x3E`)**
Jeitos de escrever um número fixo no código. O `f` no fim marca um `float` (sem ele, `0.25` seria um `double`, um decimal de 8 bytes). Os `_` só facilitam a leitura: `1_000_000` vale 1000000. O prefixo `0x` indica hexadecimal: `0x3E` vale 62. Exemplos dos testes: `new TouchPointer(0, TouchAction.Move, 0.25f, 0.5f, 1.0f)` e `new FrameMessage(1_000_000, true, data)`.

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

**Expressão `with`**
Cria uma **cópia** de um record trocando só alguns campos. Os testes do CONFIG e do FRAME precisam disso por causa de um detalhe da comparação por valor (ver `record`): ela compara campo a campo, mas um `byte[]` só é "igual" se for **o mesmo array**; ter o mesmo conteúdo não basta. O teste resolve isso copiando a mensagem original com o array trocado pelo que veio do `Decode` (assim o record compara todo o resto) e conferindo o conteúdo do array à parte:

```csharp
Assert.Equal(config with { CodecConfig = decoded.CodecConfig }, decoded);
Assert.Equal(codecConfig, decoded.CodecConfig);
```

**Construtor primário**
Em `ProtocolException(string message) : IOException(message)`, os parâmetros ficam logo depois do nome da classe e são repassados à classe-pai. A classe inteira cabe numa linha. Em `MessageReader(Stream stream)` nada é repassado: o parâmetro `stream` fica disponível dentro da classe toda, como se fosse um campo (`stream.ReadAtLeastAsync(...)`).

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

**Padrões com `is`, `or` e `not`**
`is` testa se um valor se encaixa num padrão, e os padrões podem ser combinados com `or` ("ou") e `not` ("não"). É uma forma curta de escrever comparações repetidas:

```csharp
if (m.Pointers.Count is < 1 or > MaxTouchPointers)       // "menor que 1 OU maior que o máximo"
    throw new ProtocolException(...);

if (codec is not (VideoCodec.H264 or VideoCodec.H265))   // "NÃO é (H264 ou H265)"
    throw new ProtocolException(...);
```

A primeira condição diz "a quantidade é menor que 1 ou maior que o máximo", sem repetir `Count` duas vezes. A segunda vale para qualquer codec que não seja nem H264 nem H265 (os parênteses importam: o `not` vale para o grupo todo).

**`condição ? a : b` (operador ternário)**
Escolhe entre dois valores: se a condição for verdadeira, vale `a`; senão, vale `b`. No `Encode` do FRAME, `w.WriteByte(m.IsKeyframe ? (byte)1 : (byte)0);` grava `1` se o quadro for keyframe e `0` se não for.

**Conversão explícita `(tipo)valor` (cast)**
Pede ao compilador para tratar o valor como outro tipo. `w.WriteByte((byte)m.Pointers.Count)` converte o `Count` (um `int`) em `byte`; `(MessageType)type` transforma o byte `type` no `enum` correspondente, e `(byte)m.SupportedCodecs` faz o caminho inverso. Atenção: se o número não couber no tipo de destino, os bits que sobram são **cortados** em silêncio. Por isso o código confere os limites antes de converter (ex.: `ushort.MaxValue` no CONFIG).

**`foreach` e `for`**
Repetem um trecho de código. `foreach (var p in m.Pointers)` = "para cada dedo `p` da lista" (usado no `Encode` do TOUCH). `for (var i = 0; i < count; i++)` = "comece com `i = 0` e repita enquanto `i < count`, somando 1 a `i` a cada volta" (usado no `DecodeTouch`, para ler `count` dedos).

**Coleções `[...]`**
Forma curta de criar um array ou uma lista com os elementos entre colchetes; o tipo final vem do contexto. Nos testes, `byte[] codecConfig = [0x00, 0x00, 0x00, 0x01, 0x40, 0x01, 0x0C, 0x01];` cria um array de 8 bytes, e `new TouchMessage([ ... ])` passa a lista de dedos (o parâmetro é um `IReadOnlyList<TouchPointer>`: uma lista de `TouchPointer` que só pode ser lida, não alterada). `[]` é a coleção vazia: `new TouchMessage([])` é um TOUCH sem nenhum dedo, que o `Encode` precisa recusar.

**`Span<byte>` e `ReadOnlySpan<byte>`**
Uma "janela" sobre bytes que já existem na memória, **sem copiar**. `Span` permite escrever; `ReadOnlySpan` só ler. `bytes.AsSpan(5)` = janela que começa no byte 5. `_span[_position..]` = "do `_position` até o fim". É por isso que o código é rápido e quase não aloca memória.

**Intervalos `..` e `^`**
Dentro de colchetes, `..` quer dizer "até" e `^n` quer dizer "o n-ésimo a partir do fim" (é o mesmo recurso do `_span[_position..]`, visto acima). Nos testes, `ping[..^3]` é o vetor do PING **sem os 3 últimos bytes**, usado para simular uma conexão que cai no meio de uma mensagem.

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

**`Stream`**
Um fluxo de bytes que se lê aos poucos e em ordem, sem importar de onde vem: um arquivo, uma conexão de rede (`NetworkStream`) ou a memória (`MemoryStream`). O `MessageReader` aceita qualquer `Stream`; nos testes, `new MessageReader(new MemoryStream(bytes))` lê bytes que já estão na memória, como se tivessem chegado pela rede. (`Stream`, `MemoryStream` e `EndOfStreamException` vêm de `System.IO`, importado sozinho pelo `ImplicitUsings`.)

**`async`, `await` e `Task<T>`**
Esperar bytes da rede pode demorar. Em vez de travar o programa inteiro, o método é marcado `async` e devolve um `Task<T>`: uma "promessa" de que, mais tarde, haverá um resultado do tipo `T` (o que vai entre `<` e `>`). O `await` significa "espere aqui até a promessa se cumprir, mas deixe o resto do programa seguir enquanto isso".

```csharp
public async Task<Message?> ReadAsync(CancellationToken cancellationToken = default)
...
    await stream.ReadExactlyAsync(payload, cancellationToken);
```

`Task<Message?>` = "a promessa de uma `Message` (ou de `null`, ver `Nullable`)". Quem chama também usa `await`, como nos testes: `await reader.ReadAsync()`. Um `Task` sem `<T>` é uma promessa que não devolve valor: é o tipo dos testes `async`, como `public async Task Reads_consecutive_messages_then_null_at_clean_end()`. O nome de um método assíncrono costuma terminar em `Async` (`ReadAsync`).

**`CancellationToken`**
Um "botão de cancelar" entregue a uma operação demorada: se alguém o aciona, a espera é interrompida. Em `CancellationToken cancellationToken = default`, o `= default` torna o parâmetro **opcional**; sem argumento, vale um token que nunca é cancelado. Por isso os testes chamam `reader.ReadAsync()` com os parênteses vazios.

**Argumento nomeado (`nome: valor`)**
Escreve-se o nome do parâmetro antes do valor, para deixar claro o que ele significa. Em `stream.ReadAtLeastAsync(_header, _header.Length, throwOnEndOfStream: false, cancellationToken)`, o `false` é o valor de `throwOnEndOfStream` ("não lance erro se o fluxo acabar antes do esperado").

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

O `length` existe porque o TCP é um fluxo contínuo de bytes, sem divisão entre mensagens. O receptor lê 5 bytes, descobre o tamanho e então lê exatamente aquela quantidade (é o trabalho do `MessageReader`, seção 5.6). Cuidado com a palavra "quadro": aqui ela é o envelope que embrulha **qualquer** mensagem; o *quadro de vídeo* é só o conteúdo da mensagem `FRAME`.

**Limite de 16 MiB.** `MaxPayloadLength` (`16 * 1024 * 1024` = 16 777 216 bytes) impede alguém (ou um bug) de mandar `length` gigante e fazer o programa reservar memória demais. O `Begin` recusa escrever um payload maior que isso, e o `MessageReader` recusa ler um, **antes** de reservar a memória.

**Annex-B / NAL units.** Formato usado pelo vídeo H.264/H.265 para separar pedaços da imagem comprimida. Só importa nas mensagens `CONFIG` e `FRAME`, onde o codec apenas carrega esses bytes (`CodecConfig` e `Data`), sem interpretá-los; por ora trate como "bytes de vídeo".

## 5. Passo a passo por arquivo

### 5.1 `Messages.cs`

Declara os vocabulários do protocolo:

- `MessageType`: o número que vai no byte `type` (Hello=1, Config=2, Frame=3, Touch=4, Ping=5, Pong=6, KeyframeRequest=7, e da v2 Pair=8, Paired=9, Auth=10, Denied=11).
- `VideoCodec`: H264=1, H265=2 (flags combináveis: o HELLO carrega uma combinação; o CONFIG, exatamente um).
- `TouchAction`: Down=0, Move=1, Up=2, Cancel=3 (dedo encostou, moveu, soltou, gesto cancelado).
- As mensagens, todas `record`s que herdam de `Message`:

| Mensagem | Quem envia | O que carrega |
|---|---|---|
| `HelloMessage` | celular → PC (primeira) | versão do protocolo, largura/altura/dpi da tela, codecs suportados |
| `ConfigMessage` | PC → celular | resolução, codec escolhido, bitrate, config do codec |
| `FrameMessage` | PC → celular | um quadro de vídeo (`Data`), o instante da captura (`TimestampUs`) e se é keyframe |
| `TouchMessage` | celular → PC | lista de dedos (`TouchPointer`), cada um com id, ação, posição 0..1 e pressão |
| `PingMessage` / `PongMessage` | qualquer lado / resposta | um timestamp (para medir latência) |
| `KeyframeRequestMessage` | celular → PC | nada (só "me mande um quadro completo") |
| `PairMessage` | celular → PC | o segredo de 32 bytes lido do QR e o nome do celular |
| `PairedMessage` | PC → celular | a chave de acesso de 32 bytes, entregue uma única vez |
| `AuthMessage` | celular → PC | a chave de acesso, antes do HELLO |
| `DeniedMessage` | PC → celular | o motivo da recusa (`DeniedReason`: segredo inválido, aparelho desconhecido, versão incompatível) |

**Keyframe** = quadro de vídeo completo, que pode ser decodificado sozinho. Os outros quadros guardam só as diferenças; se algo se perde, o celular pede um keyframe para "recomeçar".

### 5.2 `PayloadWriter.cs`

Escreve campos um depois do outro num `Span<byte>` já dimensionado.

- `_position` é o "cursor": quantos bytes já foram escritos.
- `WriteUInt16(value)` chama `BinaryPrimitives.WriteUInt16LittleEndian` para gravar 2 bytes na ordem little-endian na posição atual e depois faz `_position += 2`.
- `WriteByte`, `WriteUInt32`, `WriteUInt64`, `WriteSingle`, `WriteBytes` seguem o mesmo padrão. (`WriteSingle` grava um `float`: `Single` é o nome do `float` no .NET.)
- Não valida tamanho: se o span for pequeno, o .NET lança erro. Quem garante o tamanho certo é o `Begin` do codec.

### 5.3 `PayloadReader.cs`

O espelho do writer: lê campos em sequência de um `ReadOnlySpan<byte>`.

- `Take(count)` é o coração: confere se ainda há `count` bytes (`count > _span.Length - _position` → lança `ProtocolException("Payload truncado.")`), devolve a fatia e avança o cursor.
- `ReadByte`, `ReadUInt16` etc. são `Take` + conversão little-endian (`ReadSingle` devolve um `float`).
- `ReadBytes(n)` e `ReadRemaining()` devolvem **cópia** (`.ToArray()`), porque o span original pode deixar de existir depois. O CONFIG usa `ReadBytes` para o codec config; o FRAME usa `ReadRemaining` para o vídeo.
- `EnsureEnd()` verifica que o cursor chegou exatamente ao fim. Sobrou byte → erro. Assim a mensagem precisa ter o tamanho **exato**.

### 5.4 `ProtocolException.cs`

Um tipo de erro próprio. Herda de `IOException`, então código de rede que já captura erros de I/O captura esse também. Use-o para "os bytes não seguem o protocolo" (ou, no `Encode`, "esta mensagem não cabe no protocolo"), e não para falhas de conexão: quando a conexão cai no meio de uma mensagem, o erro é a `EndOfStreamException` do próprio .NET (seção 5.6).

### 5.5 `MessageCodec.cs`

**Constantes:** `ProtocolVersion = 2` (a v2 acrescentou o pareamento; o HELLO com outra versão é recusado), `SecretLength` e `TokenLength` (32 bytes cada), `HeaderSize = 5`, `MaxPayloadLength = 16 MiB`, `MaxTouchPointers = 10`. Duas constantes são privadas: `TouchPointerSize = 14` (os bytes de cada dedo no TOUCH: id 1 + ação 1 + x 4 + y 4 + pressão 4) e `KnownCodecs`, a máscara `H264 | H265` usada para validar.

**`Encode(Message)`** — objeto → bytes:
1. O `switch` descobre qual mensagem é.
2. `Begin(tipo, tamanhoDoPayload, out bytes)` cria o array já com tamanho total (5 + payload), grava o `type` no byte 0 e o `length` nos bytes 1-4, e devolve um `PayloadWriter` posicionado logo depois do cabeçalho. Se o payload passar de `MaxPayloadLength` (16 MiB), o `Begin` lança `ProtocolException` antes de criar o array.
3. O `case` escreve os campos na ordem do protocolo (para HELLO: versão, largura, altura, dpi, codecs = 2+2+2+2+1 = **9** bytes).
4. Devolve `bytes`.
5. Qualquer outra subclasse de `Message` → `ArgumentException`. Com as 11 mensagens do protocolo cobertas, isso só aconteceria com uma mensagem nova, que o `Encode` não conhece.

O tamanho de payload que cada `case` passa ao `Begin` (`n` = quantidade de bytes de dados; no TOUCH, quantidade de dedos):

| Mensagem | Payload | De onde vem a conta |
|---|---|---|
| HELLO | 9 | 2+2+2+2+1 |
| CONFIG | `11 + n` | 2+2+1+4+2 = 11 de campos fixos, mais os `n` bytes do codec config |
| FRAME | `9 + n` | 8 (timestamp) + 1 (flags) = 9, mais os `n` bytes do vídeo |
| TOUCH | `1 + 14·n` | 1 (quantidade) + 14 (`TouchPointerSize`) por dedo |
| PING / PONG | 8 | 8 (timestamp) |
| KEYFRAME_REQ | 0 | nada |
| PAIR | `33 + n` | 32 (segredo) + 1 (tamanho do nome) = 33, mais os `n` bytes do nome do celular em UTF-8 |
| PAIRED | 32 | a chave de acesso (`TokenLength`) |
| AUTH | 32 | a chave de acesso (`TokenLength`) |
| DENIED | 1 | o motivo (`DeniedReason`) |

**`Decode(byte type, ReadOnlySpan<byte> payload)`** — bytes → objeto:
1. Cria um `PayloadReader` sobre o payload.
2. `kind switch` escolhe como ler cada tipo; tipo desconhecido → `ProtocolException`.
3. `r.EnsureEnd()` garante que não sobrou nem faltou byte.
4. Observe que o `Decode` recebe **só o payload**: quem lê o cabeçalho e entrega `type` e payload separados é o `MessageReader` (seção 5.6).

**Mensagem por mensagem.** HELLO, CONFIG, FRAME e TOUCH têm, cada uma, o seu `DecodeXxx`, que lê os campos na ordem e valida o que dá para validar na hora (o `Take` do `PayloadReader` já garante que não falte byte). Todos os erros abaixo são `ProtocolException`.

- **HELLO** (`DecodeHello`) lê os 5 campos na ordem e valida os codecs: `None` (0) ou bits desconhecidos → erro.
- **CONFIG** (PC → celular):
  - `Encode`: antes de tudo confere `m.CodecConfig.Length > ushort.MaxValue` (65 535). O motivo: o tamanho do codec config é gravado num `u16`, e o `(ushort)` cortaria o tamanho de um array maior (70 000 viraria 4 464), gravando um valor errado. Depois grava largura (u16), altura (u16), codec (u8), bitrate (u32), o tamanho do codec config (u16) e os bytes do codec config.
  - `DecodeConfig`: lê largura, altura e o byte do codec, e **já confere o codec**: `codec is not (VideoCodec.H264 or VideoCodec.H265)`. Só vale **exatamente** 1 ou 2: recusa 0 (nenhum) e 3 (os dois juntos; o HELLO pode oferecer os dois, mas o CONFIG tem de escolher um). Em seguida lê o bitrate (u32), o tamanho do codec config (u16) e `ReadBytes(tamanho)`: se o payload tiver menos bytes do que o tamanho declarado, o `Take` lança "Payload truncado.".
- **FRAME** (PC → celular):
  - `Encode`: grava o timestamp (u64), 1 byte de flags e os bytes do vídeo (`Data`). O byte de flags é `m.IsKeyframe ? (byte)1 : (byte)0`: o bit 0 diz "é keyframe" e os bits 1 a 7 ficam em 0.
  - `DecodeFrame`: lê o timestamp (u64) e o byte `flags`. `IsKeyframe` é `(flags & 1) != 0`: o `& 1` apaga todos os bits menos o bit 0, e se sobrou algo diferente de 0, ele estava ligado; os bits 1 a 7 são ignorados. **Todo o resto do payload** (`ReadRemaining()`) é o vídeo, entregue sem o codec olhar dentro. O único erro possível é um payload com menos de 9 bytes (faltando timestamp ou flags).
- **TOUCH** (celular → PC):
  - `Encode`: primeiro confere `m.Pointers.Count is < 1 or > MaxTouchPointers` (precisa de 1 a 10 dedos). Depois grava 1 byte com a quantidade e, num `foreach`, 14 bytes por dedo: `Id` (u8), `Action` (u8), `X`, `Y` e `Pressure` (3 × f32). Só a quantidade é conferida aqui; ação e números finitos só são conferidos no `Decode`.
  - `DecodeTouch`: lê a quantidade e confere 1 a 10 **antes de ler qualquer dedo**. Depois, para cada dedo (num `for`): lê o `id`; lê a ação e recusa se `action > (byte)TouchAction.Cancel` (só 0 a 3 valem); lê `x`, `y` e `pressure` e recusa se algum não for finito (`float.IsFinite` é falso para `NaN`, "não é um número", e para infinito). Um `x` ou `y` fora de 0..1 é aceito pelo protocolo.
- **PING, PONG e KEYFRAME_REQ** não têm regras extras: o `Decode` lê direto o `u64` do timestamp (ou nada, no KEYFRAME_REQ) e o `EnsureEnd` confere o tamanho.

### 5.6 `MessageReader.cs`

O `Decode` só sabe traduzir um payload que já está inteiro na memória. Na rede, as coisas chegam de outro jeito: o TCP não preserva as fronteiras entre mensagens, ele entrega os bytes **em pedaços de tamanho imprevisível**. Uma leitura pode trazer só metade de um cabeçalho, ou o fim de uma mensagem junto com o começo da seguinte. Por isso alguém precisa ficar lendo até completar uma mensagem. Esse alguém é o `MessageReader`.

Ao contrário do `MessageCodec` (`static`), o leitor guarda estado: o `Stream` que recebeu no construtor e o array `_header`, de 5 bytes (`HeaderSize`), criado junto com ele e reaproveitado a cada mensagem. Por isso se cria com `new MessageReader(stream)`. Ele tem um único método, `ReadAsync`:

```csharp
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
```

Passo a passo:
1. **Cabeçalho.** `ReadAtLeastAsync` fica lendo do `Stream` até juntar os 5 bytes do cabeçalho (`_header.Length`), por mais picados que cheguem, e devolve quantos bytes conseguiu. O `throwOnEndOfStream: false` pede para **não** lançar erro se o fluxo acabar antes; assim é o próprio leitor que decide o que fazer, nos dois passos seguintes.
2. **Fim limpo.** `read == 0`: não chegou nenhum byte, ou seja, o fluxo terminou **exatamente entre duas mensagens**. Devolve `null`. Não é um erro: é a desconexão normal.
3. **Corte no cabeçalho.** `read < _header.Length`: chegaram de 1 a 4 bytes e o fluxo acabou. Lança `EndOfStreamException`.
4. **Tamanho.** O `length` (u32 little-endian) está nos bytes 1 a 4 do cabeçalho (`_header.AsSpan(1)`). Se passar de `MaxPayloadLength`, lança `ProtocolException` **antes** do `new byte[length]`. A ordem é a defesa: com um cabeçalho como `03 FF FF FF FF`, o programa tentaria reservar cerca de 4 GB só porque alguém mandou um número grande.
5. **Payload.** `ReadExactlyAsync` lê exatamente `length` bytes (de novo, juntando os pedaços). Se o fluxo acabar antes de completar, o .NET lança `EndOfStreamException`: é o corte no meio do payload.
6. **Decodificar.** `MessageCodec.Decode(_header[0], payload)` recebe o `type` (byte 0 do cabeçalho) e o payload. É o `Decode` que lança `ProtocolException` para tipo desconhecido, campo inválido, bytes faltando ou sobrando.

Como o leitor lê exatamente 5 bytes e depois exatamente `length` bytes, ele nunca pega bytes da mensagem seguinte: a próxima chamada de `ReadAsync` recomeça de onde esta parou. É assim que várias mensagens coladas no mesmo fluxo saem uma de cada vez.

Quando tudo dá certo, o resultado é a `Message` decodificada. Fora isso, o `ReadAsync` termina de **três jeitos**, que dizem coisas bem diferentes:

| Resultado | O que significa |
|---|---|
| `null` | O fluxo acabou **exatamente entre duas mensagens**: fim limpo, desconexão normal. |
| `EndOfStreamException` | A conexão caiu **no meio** de uma mensagem (dentro do cabeçalho ou do payload). |
| `ProtocolException` | O `length` passa de 16 MiB (recusado **antes** de reservar memória) ou os bytes são inválidos (tipo desconhecido, campo inválido, bytes faltando ou sobrando). |

O leitor não sabe nada de TCP: qualquer `Stream` serve. Nos testes é um `MemoryStream`; na Parte 3, será o fluxo da conexão com o celular.

### 5.7 Ordem das ações (resumo do ciclo)

```
Encode:  Message ─▶ switch ─▶ Begin (cabeçalho) ─▶ Write* (payload) ─▶ byte[]
Decode:  (type, payload) ─▶ PayloadReader ─▶ Read* ─▶ EnsureEnd ─▶ Message
Reader:  Stream ─▶ 5 bytes de cabeçalho ─▶ confere length ─▶ payload ─▶ Decode ─▶ Message
```

No `Reader`, o fluxo acabar antes do primeiro byte de uma mensagem dá `null`; acabar no meio dela dá `EndOfStreamException`.

### 5.8 Pareamento e autenticação

Na porta Wi-Fi qualquer aparelho da rede consegue abrir uma conexão (e, no USB, qualquer app do celular consegue abrir o `127.0.0.1:38701` que o `adb reverse` cria lá), então o PC precisa de duas garantias: o celular fala **com o PC certo** (e não com um impostor) e o PC só atende **celulares que o dono autorizou**. Cada peça de `Security/` cuida de uma parte.

**A digital do certificado (`HostIdentity`).** Na primeira execução o PC cria um certificado TLS autoassinado e o reaproveita depois. A **digital** (*fingerprint*) é o SHA-256 dos bytes do certificado: um resumo curto, que muda por completo se o certificado mudar. O QR leva essa digital; o celular a guarda e, nas conexões seguintes, **recusa qualquer certificado cuja digital seja diferente**. Assim ninguém consegue se passar pelo PC, mesmo sem uma autoridade certificadora:

```csharp
/// <summary>SHA-256 do certificado em DER, em base64url sem padding (vai no QR).</summary>
public string Fingerprint { get; }
...
public static string ComputeFingerprint(ReadOnlySpan<byte> der) => Base64Url.EncodeToString(SHA256.HashData(der));
```

**Por que o QR leva um segredo de uso único (`PairingSession`).** Quem enxerga o QR (uma foto, por cima do ombro) poderia se parear. Por isso o QR carrega um segredo aleatório de 32 bytes que vale só **2 minutos** e é **consumido** na primeira vez que alguém o apresenta; gerar um QR novo invalida o anterior. Depois de usado, a foto do QR não serve mais para nada:

```csharp
public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
...
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
```

(`FixedTimeEquals` compara em tempo constante, para que o tempo da resposta não revele quantos bytes estavam certos. `lock` impede que duas conexões usem o mesmo segredo ao mesmo tempo.) O que vai no QR é a URI montada por `PairingUri.Build`: endereço e porta do PC (`h`, `p`), a digital (`fp`), o segredo (`s`) e o nome do PC (`n`).

**Por que o PC guarda só o hash da chave (`DeviceRegistry`).** No pareamento o PC gera uma **chave de acesso** de 32 bytes e a entrega ao celular uma única vez (`PAIRED`). Nas conexões seguintes o celular a apresenta no `AUTH`. O PC não guarda a chave, só o SHA-256 dela: se alguém copiar o arquivo `paired-devices.json`, vê apenas hashes, que não permitem se passar por um celular (um hash não volta a ser a chave). Para conferir um `AUTH`, o PC calcula o hash da chave recebida e procura o mesmo hash na lista:

```csharp
var token = RandomNumberGenerator.GetBytes(MessageCodec.TokenLength);
var device = new PairedDevice(
    Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)), name,
    Convert.ToHexStringLower(SHA256.HashData(token)), _clock.GetUtcNow());
```

Remover um celular (`Remove`, comando `r <id>` do console) apaga a linha dele: a chave dele deixa de funcionar na hora.

**Como as peças se encaixam (`HostServer`).** Nas duas portas (Wi-Fi e USB) o servidor chama o mesmo método, `ServeSecureAsync`: abre o TLS e olha a primeira mensagem: `PAIR` (primeiro uso) ou `AUTH` (já pareado). Só depois de autenticado é que o fluxo normal, `HELLO` → `CONFIG`, começa:

```csharp
if (first is PairMessage pair)
{
    if (!_pairing.TryConsume(pair.Secret))
    {
        await DenyAsync(tls, DeniedReason.InvalidPairingSecret, cancellationToken);
        return;
    }
    var (device, token) = _devices.Add(pair.DeviceName);
    ...
    await SendAsync(tls, new PairedMessage(token), cancellationToken);
    first = await reader.ReadAsync(handshake.Token);
}
```

Qualquer coisa fora do esperado recebe um `DENIED` com o motivo. Prazos evitam que um cliente mudo prenda a porta: 10 s para o TLS e o pareamento, e 10 s sem mensagem alguma depois disso (o app manda `PING` a cada segundo).

**O USB segue o mesmo caminho.** O `AcceptLoopAsync` recebe o listener e só um rótulo para o log, e as duas portas terminam em `ServeSecureAsync`:

```csharp
AcceptLoopAsync(_wifi, "Wi-Fi", cancellationToken),
AcceptLoopAsync(_usb, "USB", cancellationToken));
...  await ServeSecureAsync(client.GetStream(), cancellationToken);
```

A porta 38701 continua escutando só em `127.0.0.1` (`new TcpListener(IPAddress.Loopback, usbPort)`), mas isso não basta: o `adb reverse` abre `127.0.0.1:38701` **dentro do celular**, onde qualquer app com internet pode se conectar, e um app também pode ocupar essa porta antes do `adb reverse` para se passar pelo PC. Por isso o USB exige o mesmo TLS (o celular só aceita o certificado de digital fixada) e o mesmo `PAIR`/`AUTH` do Wi-Fi; `HELLO` direto, ou TCP puro, não abre sessão. No app, `Connection.kt` trata o cabo assim:

```kotlin
is ConnectTarget.Usb -> raw.upgradeToTls(host, port, target.pc.fingerprint)
...
is ConnectTarget.Usb -> out.send(AuthMessage(target.pc.token))
is ConnectTarget.Usb -> LOOPBACK to port
is ConnectTarget.Pairing -> if (overUsb) LOOPBACK to usbPort else info.host to info.port
```

Ou seja, o `Usb` conecta em `127.0.0.1`, faz TLS com a digital do PC pareado e manda `AUTH`; e o pareamento também funciona pelo cabo (`overUsb`), com o mesmo QR. Na tela (`HostListScreen.kt`), sem pareamento aparecem **Parear pelo Wi-Fi (QR)** e **Parear pelo cabo USB (QR)**; pareado, **Conectar por cabo USB** e **Parear de novo (QR)**.

**O console (`Program.cs`) e o `LanAddressSelector`.** Digitar `p` chama `pairing.Begin()` para gerar o segredo, monta a URI com `PairingUri.Build` e a desenha como QR no console. O IP que vai no QR vem do `LanAddressSelector.PickForPairing`, que prefere um IP de rede privada (10.x, 172.16-31.x, 192.168.x): o PC pode ter adaptadores de VPN ou de máquina virtual cujos IPs o celular não alcança.

## 6. Exemplos trabalhados

### 6.1 `PingMessage(123456789)`

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

### 6.2 `TouchMessage` com dois dedos

O PING só tinha um número inteiro. O TOUCH traz `float`s, os números com casas decimais, como `0.25` ("um quarto da largura do monitor"). Como um `float` vira bytes? O exemplo é o do teste `Touch_matches_vector`:

```csharp
var touch = new TouchMessage([
    new TouchPointer(0, TouchAction.Move, 0.25f, 0.5f, 1.0f),
    new TouchPointer(1, TouchAction.Down, 0.75f, 0.125f, 0.5f),
]);
```

(O `f` depois dos números marca um `float`; veja "Literais numéricos" no glossário.)

1. `Encode` cai no `case TouchMessage m`, confere que há de 1 a 10 dedos (são 2) e chama `Begin(MessageType.Touch, 1 + TouchPointerSize * m.Pointers.Count, ...)`, ou seja, `1 + 14 * 2 = 29` bytes de payload.
2. `Begin` cria um array de `5 + 29 = 34` bytes e preenche o cabeçalho:
   - `bytes[0] = 4` (Touch)
   - bytes 1-4 = `29` em u32 little-endian = `1D 00 00 00`
3. `w.WriteByte((byte)m.Pointers.Count)` grava a quantidade de dedos (aqui, `02`).
4. Para cada dedo `p`: `WriteByte(p.Id)`, `WriteByte((byte)p.Action)` e três `WriteSingle` (`p.X`, `p.Y` e `p.Pressure`). Dá 1 + 1 + 4 + 4 + 4 = **14** bytes por dedo.

**Como `0.25f` vira `00 00 80 3E`.** Um `float` ocupa 32 bits (padrão **IEEE 754**), divididos em 3 partes: 1 bit de **sinal**, 8 bits de **expoente** e 23 bits de **fração**. É uma notação científica em binário: o número vale `1.fração` vezes 2 elevado ao expoente, com o sinal à parte. Para o `0.25`:

- `0.25` é um quarto: 1.0 × 2⁻² (dois elevado a menos dois).
- **Sinal:** `0` (positivo).
- **Expoente:** o padrão guarda `expoente + 127` (assim o expoente não precisa de um bit de sinal só dele). Aqui: `-2 + 127 = 125`, que em binário é `01111101`.
- **Fração:** nada além do `1.` que já está implícito, então são 23 zeros.

```
sinal  expoente   fração
  0    01111101   00000000000000000000000      os 32 bits em sequência

00111110  10000000  00000000  00000000         os mesmos bits, de 8 em 8
   3E        80        00        00            em hexadecimal
```

Lido de uma vez, é `0x3E800000`. Como o protocolo é little-endian (menos significativo primeiro), os bytes vão para o fio na ordem inversa: **`00 00 80 3E`**. O `ReadSingle`, que o `Decode` usa, faz o caminho de volta: pega 4 bytes, desfaz a ordem e reconstrói o `0.25f`.

Os outros números do vetor seguem a mesma conta:

| Valor | Bytes no protocolo (little-endian) |
|---|---|
| `0.25` | `00 00 80 3E` |
| `0.5` | `00 00 00 3F` |
| `1.0` | `00 00 80 3F` |
| `0.75` | `00 00 40 3F` |
| `0.125` | `00 00 00 3E` |

O resultado completo (34 bytes) é o conteúdo de `docs/protocol-vectors/touch.hex`; a parte depois do `#` é só comentário:

```
04 1D 00 00 00                              # type=TOUCH, length=29
02                                          # count=2
00 01 00 00 80 3E 00 00 00 3F 00 00 80 3F   # id=0 MOVE x=0.25 y=0.5 pressure=1.0
01 00 00 00 40 3F 00 00 00 3E 00 00 00 3F   # id=1 DOWN x=0.75 y=0.125 pressure=0.5
```

Cada linha de dedo são os 14 bytes na ordem em que o `Encode` os escreveu. O primeiro, por partes:

```
00     01     00 00 80 3E   00 00 00 3F   00 00 80 3F
id=0   MOVE   x=0.25        y=0.5         pressão=1.0
```

O teste `Touch_matches_vector` confere os dois sentidos: o `Encode` gera esses 34 bytes e o `Decode` deles devolve dois `TouchPointer` iguais aos originais.

## 7. Os testes

**xUnit** é o framework de testes. O `.csproj` de testes referencia os projetos `Core`, `Display` e `DevHost` e os pacotes do xUnit. São **224 casos de teste** ao todo (223 rodam; 1 de integração fica ignorado sem `SCREENSHARE_VDD_TESTS=1`): `MessageCodecTests` (32), `MessageReaderTests` (5), os de `Security/` (25: `DeviceRegistryTests` 12, `PairingSessionTests` 7, `HostIdentityTests` 4, `PairingUriTests` 2), os do host de desenvolvimento (63: `HostServerTests` 23, `LanAddressSelectorTests` 18, `DriverCommandsTests` 14, `MonitorSetupTests` 8) e os de `Display/` (99: `VirtualMonitorManagerTests` 31, `DriverInstallerTests` 17, `VddSettingsFileTests` 13, `DisplayTopologyTests` 12, `ScaleCalculatorTests` 11, `DisplayStateStoreTests` 10, `ElevatedCommandTests` 4, `DisplayTopologyIntegrationTests` 1, ignorado por padrão). As seções abaixo detalham os dois primeiros arquivos, que são os do protocolo básico.

- `[Fact]` = um teste simples.
- `[Theory]` + `[InlineData(...)]` = o mesmo teste rodado várias vezes com valores diferentes (cada `[InlineData]` conta como um caso). Em `Hello_with_invalid_codec_flags_is_rejected`, roda com `0` (nenhum codec) e `4` (bit desconhecido); em `Touch_with_invalid_pointer_count_is_rejected`, com `0` e `11` dedos.
- `Assert.Equal(esperado, atual)` falha o teste se forem diferentes; `Assert.Throws<ProtocolException>(...)` falha se o código **não** lançar aquele erro.
- Mais três do xUnit que aparecem nos testes novos: `Assert.IsType<ConfigMessage>(x)` confere o tipo e devolve `x` já como esse tipo; `Assert.Null(x)` confere que `x` é `null`; `await Assert.ThrowsAsync<...>(...)` é o `Throws` para código `async`.

**`MessageCodecTests`: os 21 casos do protocolo básico** (os outros 11 cobrem PAIR, PAIRED, AUTH e DENIED, com vetores `pair.hex`, `paired.hex`, `auth.hex` e `denied.hex`).
- **7 de vetor**, um por mensagem (`Hello_matches_vector`, `Ping_...`, `Pong_...`, `KeyframeRequest_...`, `Config_...`, `Frame_...`, `Touch_...`): o `Encode` precisa gerar exatamente os bytes do `.hex`, e o `Decode` desses bytes precisa devolver a mensagem original. Os de CONFIG e FRAME usam a expressão `with` (ver glossário), por causa do `byte[]`.
- **14 de erro**, todos esperando uma `ProtocolException`:
  - os já existentes: tipo desconhecido (`0x63`), payload truncado (8 bytes num HELLO que pede 9), bytes sobrando (9 bytes num PING que pede 8) e flags de codec inválidas no HELLO (0 e 4);
  - CONFIG: codec `3` (`H264|H265` não é um codec único) e um `codecConfigLength` que declara 8 bytes mas o payload traz só 2;
  - FRAME: payload de 8 bytes, que não chega a ter o byte de flags;
  - TOUCH: 0 e 11 dedos, ação `9` e `NaN` na coordenada x;
  - no `Encode`: TOUCH sem nenhum dedo e CONFIG com 70 000 bytes de codec config (acima de 65 535).

**`MessageReaderTests` (5 casos).** Todos são `async` e trocam a rede por fluxos de teste:
- `Reads_consecutive_messages_then_null_at_clean_end`: junta os vetores de HELLO, PING e KEYFRAME_REQ numa sequência só, como se tivessem chegado um atrás do outro, e confere que o leitor devolve as três mensagens na ordem e depois `null`.
- `Reassembles_messages_delivered_one_byte_at_a_time`: usa `OneByteAtATimeStream`, um `Stream` de teste (definido dentro do próprio arquivo) que entrega **no máximo 1 byte por leitura**, o pior caso de um TCP picotado. O vetor do TOUCH (34 bytes) ainda assim chega inteiro, com 2 dedos, e o fim do fluxo dá `null`.
- `Stream_ending_inside_header_throws_EndOfStream`: só 2 bytes (`05 08`), metade de um cabeçalho → `EndOfStreamException`.
- `Stream_ending_inside_payload_throws_EndOfStream`: o vetor do PING sem os 3 últimos bytes (`ping[..^3]`): cabeçalho completo e payload incompleto → `EndOfStreamException`.
- `Oversized_length_is_rejected_before_reading_payload`: os 5 bytes `03 FF FF FF FF`, um FRAME que declara cerca de 4 GB de payload e não traz mais nada → `ProtocolException`. O leitor recusa só de olhar o `length`, sem tentar reservar nem ler o payload.

**Vetores compartilhados.** A pasta `docs/protocol-vectors` tem os bytes exatos de cada mensagem. O `.csproj` de testes copia esses `.hex` para a pasta de saída (a linha `<None Include="..\..\docs\protocol-vectors\*.hex" ...>`), e `Vectors.Load("ping.hex")` os lê: ignora tudo depois de `#` (comentário), separa os pares hexadecimais e converte cada um em `byte` com `Convert.ToByte(token, 16)`. O app Android (Kotlin) testa contra os **mesmos arquivos**, então os dois lados provam que falam o mesmo "idioma" sem precisarem rodar juntos.

## ScreenShare.Display (monitor virtual)

**Para que serve.** Faz o Windows ganhar um monitor de verdade quando o celular conecta. O driver que cria o monitor é o Virtual Display Driver (VDD), de terceiros; este projeto apenas o instala e o controla.

**`VddSettingsFile`.** É o XML de configuração do driver, com a lista de resoluções que ele oferece. O driver só lê esse XML quando reinicia, por isso resolução nova exige reiniciá-lo.

**`DisplayTopology` / `IDisplayTopology`.** Liga o monitor (anexa a saída do driver à área de trabalho) e desliga (desanexa), usando as funções do próprio Windows, sem precisar de administrador. Também troca a resolução e a escala. A saída do driver é achada pelo ID do dispositivo `Root\MttVDD`, e não pelo nome `\.\DISPLAYn`, porque esse nome muda a cada vez que o Windows renumera os monitores; o ID do driver é fixo.

**`VirtualMonitorManager`.** É quem decide quando ligar e desligar:

- **Contagem de referências:** cada celular conectado soma 1; o monitor só desliga quando a conta chega a zero.
- **Espera de 10 s:** ao desconectar, ele espera 10 s antes de desligar, para que uma reconexão rápida reaproveite o monitor.
- **Resolução nova:** grava no XML e pede o reinício do driver (com UAC), em segundo plano. Enquanto isso usa a resolução mais próxima; se o reinício falhar, o monitor fica nela (ligado se há celular, desligado se não há).
- **Nunca derruba a sessão:** qualquer falha aqui é registrada e engolida; o vídeo e o resto do host continuam funcionando sem o monitor.

**`DisplayStateStore`.** Guarda o `display.json`: a escala aplicada uma única vez (depois disso vale o que o usuário ajustar) e a última posição do monitor. Gravar é "melhor esforço": se falhar, nada quebra.

**`DriverInstaller` / `WindowsDriverSystem` / `ElevatedCommand`.** O DevHost tem três modos que rodam como administrador, e só eles: `install-driver`, `uninstall-driver` e `restart-driver`. O `ElevatedCommand` relança o próprio programa com o UAC (e não relança o `dotnet.exe`; use `dotnet run --project host/ScreenShare.DevHost`). O instalador confere o **SHA-256 fixado** do pacote do driver antes de instalar, prepara o XML antes de instalar o dispositivo e, no fim, remove de `TrustedPublisher` o certificado do fabricante que ele mesmo adicionou.

**Por que não o pipe do driver.** O VDD tem um canal próprio (um pipe) para receber comandos, mas o spike mostrou que esses comandos derrubam o driver. Por isso o projeto usa o XML + reinício. Detalhes na seção "Resultados do spike" de `docs/superpowers/specs/2026-10-04-parte-2-monitor-virtual-design.md`.

## 8. Como rodar e o que falta

Na raiz do repositório:

```bash
dotnet test host
```

(Se você já estiver dentro da pasta `host`, basta `dotnet test`.) O resultado esperado é **223 testes aprovados e nenhum com falha** (mais 1 teste de integração com o driver real, que só roda com `SCREENSHARE_VDD_TESTS=1` e fica ignorado por padrão). A última linha da saída fica assim; o tempo muda a cada execução e, num .NET em inglês, ela começa com `Passed!` e usa `Failed`, `Passed` e `Skipped`:

```
Aprovado!  – Com falha:     0, Aprovado:   223, Ignorado:     1, Total:   224, Duração: 4 s - ScreenShare.Tests.dll (net10.0)
```

**O que falta.** No lado C#, a Parte 1 está completa (as mensagens do protocolo, o `MessageCodec` e o `MessageReader`) e o pareamento por QR com TLS já funciona no host de desenvolvimento. O que ainda não existe é o resto do sistema (captura, vídeo, toque), que vem nas próximas partes do roteiro. A tabela "Roteiro das partes", no plano da Parte 1 (em `docs/superpowers/plans/`), resume o que cada uma entrega, e cada parte ganha o seu plano quando chegar a vez dela:

- **Parte 2 — Monitor virtual:** instalar o Virtual Display Driver (VDD) e criar o `DisplayManager`, que ativa e ajusta o monitor virtual no Windows.
- **Parte 3 — Vídeo no Wi-Fi:** captura da tela, encode, conexão TCP, decodificação no celular, descoberta automática (mDNS) e overlay de latência. É aqui que o `MessageReader` passa a ler de uma conexão de verdade.
- **Parte 4 — USB:** conexão por cabo, com `adb reverse`.
- **Parte 5 — Toque:** capturar o toque no celular e injetá-lo no Windows (o `TouchMessage` que leva os dedos já existe).
- **Parte 6 — Robustez:** reconexão, bandeja do sistema e qualidade.

O app Android (Kotlin) implementa o **mesmo protocolo** e é testado contra os **mesmos vetores** `.hex`, o que garante que os dois lados vão se entender quando a conexão existir.
