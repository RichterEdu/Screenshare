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
