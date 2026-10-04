# Protocolo ScreenShare — versão 2

Conexões TCP entre o app Android (cliente) e o host Windows (servidor). Inteiros são **little-endian**; `f32` é IEEE 754 de 32 bits little-endian.

| Porta | Uso | Escuta em | Transporte |
|---|---|---|---|
| 38700 | Wi-Fi | todas as interfaces | TLS obrigatório, certificado autoassinado do PC fixado pelo celular no pareamento |
| 38701 | USB (`adb reverse tcp:38701 tcp:38701`) | só `127.0.0.1` | TLS obrigatório, igual à 38700 |

As mensagens e a sequência são as mesmas nas duas portas: TLS, depois `PAIR` ou `AUTH`, depois `HELLO` (ver Sequência). Detalhes do pareamento: `docs/superpowers/specs/2026-10-03-pareamento-autenticacao-design.md`.

## Quadro

| Campo | Tipo | Descrição |
|---|---|---|
| type | u8 | tipo da mensagem |
| length | u32 | tamanho do payload em bytes, no máximo 16 777 216 (16 MiB) |
| payload | `length` bytes | conteúdo, conforme o tipo |

`length` acima do limite é erro de protocolo: o receptor encerra a conexão sem alocar o payload.

## Sequência

A sequência é a mesma na 38700 (Wi-Fi) e na 38701 (USB, com o QR conectando em `127.0.0.1:38701`):

- **Primeiro uso — pareamento pelo QR:** TLS → `PAIR` → `PAIRED` → `AUTH` → `HELLO` → `CONFIG`.
- **Já pareado:** TLS → `AUTH` → `HELLO` → `CONFIG`.

Depois do `AUTH`, 10 s sem nenhuma mensagem do celular fecham a conexão (o app envia `PING` a cada segundo).

Depois do `CONFIG`: o PC envia `FRAME`s (o primeiro é keyframe) e, a qualquer momento, vêm `TOUCH`, `PING`/`PONG`, `KEYFRAME_REQ`.

Regras:
- Nas duas portas (38700 e 38701) a primeira mensagem tem de ser `PAIR` ou `AUTH`; depois de `PAIR`/`PAIRED` vem `AUTH`. Qualquer outra coisa (inclusive `HELLO` direto) → `DENIED(2)` e fechamento.
- TCP puro, sem TLS, não tem sessão em nenhuma das duas portas.
- `HELLO` com `protocolVersion` ≠ 2 → `DENIED(3)` e fechamento.
- Depois de `DENIED` o PC fecha a conexão.

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
| 8 | PAIR | celular → PC | 33 + n |
| 9 | PAIRED | PC → celular | 32 |
| 10 | AUTH | celular → PC | 32 |
| 11 | DENIED | PC → celular | 1 |

### HELLO
| Campo | Tipo | Observação |
|---|---|---|
| protocolVersion | u16 | 2 (o layout de 9 bytes do HELLO é congelado em todas as versões) |
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

### PAIR
| Campo | Tipo | Observação |
|---|---|---|
| secret | 32 bytes | segredo de pareamento lido do QR (válido por 2 minutos, uso único) |
| deviceNameLength | u8 | 1 a 64 bytes UTF-8 (UTF-8 inválido é erro) |
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

## Descoberta (Wi-Fi)
O host anuncia por mDNS/DNS-SD o serviço **`_screenshare._tcp`**, com a porta TCP do protocolo (padrão 38700) e o nome da máquina como nome da instância. O app Android o encontra com o `NsdManager`; se a rede bloquear multicast, o usuário digita `IP` ou `IP:porta`. No USB não há descoberta: o app conecta em `127.0.0.1` depois do `adb reverse`. O TXT do anúncio traz `fp` = 16 primeiros caracteres hex (minúsculos) do SHA-256 do certificado do PC, para o celular reconhecer o PC pareado mesmo se o IP mudar. No USB a porta é a 38701.

## Validação
- Payload com bytes faltando ou sobrando é erro; tipo desconhecido é erro.
- Erros de protocolo: `ProtocolException` (subclasse de `IOException`) em C# e Kotlin.
- Stream terminando no meio de uma mensagem: `EndOfStreamException` (C#) / `EOFException` (Kotlin). Terminando entre mensagens: desconexão normal (`null`).

## Vetores de teste
`docs/protocol-vectors/*.hex` são quadros completos (cabeçalho + payload) em pares hexadecimais; `#` inicia comentário. As duas implementações devem gerar e aceitar exatamente esses bytes. `pairing-uri.txt` guarda a URI de exemplo do QR, compartilhada pelos testes C# e Kotlin.

| Arquivo | Conteúdo |
|---|---|
| hello.hex | HELLO v2, 2400×1080, 420 dpi, H.264 + H.265 |
| config.hex | CONFIG 2400×1080, H.265, 20 000 kbps, 8 bytes de codec config |
| frame.hex | FRAME keyframe, t = 1 000 000 µs, 7 bytes de dados |
| touch.hex | TOUCH com 2 ponteiros (MOVE e DOWN) |
| ping.hex | PING t = 123 456 789 |
| pong.hex | PONG t = 123 456 789 |
| keyframe_req.hex | KEYFRAME_REQ |
| pair.hex | PAIR com segredo 00..1F e aparelho "Pixel 8" |
| paired.hex | PAIRED com chave A0..BF |
| auth.hex | AUTH com chave A0..BF |
| denied.hex | DENIED motivo 2 |
