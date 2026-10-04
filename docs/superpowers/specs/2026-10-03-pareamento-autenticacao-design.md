# Pareamento e autenticação — design

## Contexto

O protocolo v1 aceita qualquer conexão na porta TCP 38700, que o PC anuncia por mDNS. Qualquer aparelho na mesma rede pode mandar HELLO, receber o vídeo da segunda tela (a partir da Parte 3) e injetar toques (Parte 5). A revisão final da Parte 1 apontou isso como a decisão a tomar antes de o vídeo real trafegar.

Uso pessoal: um PC e um celular do mesmo dono.

## Decisões

- **Wi-Fi: pareamento por QR code + TLS.** Ninguém na rede consegue se conectar, ver a tela ou mandar toques sem ter pareado, e o pareamento resiste a interceptação ativa.
- **USB: sem pareamento e sem TLS.** *(Decisão substituída em 2026-10-03: ver "Revisão: USB autenticado" no fim deste documento.)* O `adb reverse` já exige que o celular tenha autorizado a depuração USB daquele PC, e o tráfego não passa pela rede. A porta do USB escuta só em `127.0.0.1`.
- Código curto de 6 dígitos foi descartado: sem um PAKE (sem biblioteca madura em C#/Kotlin), um atacante ativo no momento do pareamento quebra o código por força bruta offline.

## Portas

| Porta | Uso | Escuta em | Transporte |
|---|---|---|---|
| 38700 | Wi-Fi | todas as interfaces | TLS 1.2+ obrigatório |
| 38701 | USB (`adb reverse tcp:38701 tcp:38701`) | só `127.0.0.1` | TCP puro *(substituído: agora TLS obrigatório; ver Revisão)* |

Só a 38700 é anunciada por mDNS. Um programa local do próprio PC consegue abrir a 38701; isso é aceito (quem roda código no PC já controla a tela).

## Identidade do PC

- Na primeira execução o host gera um certificado autoassinado ECDSA P-256 (validade de 20 anos, CN = nome do PC) e o salva em `%APPDATA%\ScreenShare\host-cert.pfx`. A senha do PFX é aleatória e fica em `host-cert.key`, protegida pelo DPAPI do Windows (escopo do usuário).
- A **digital** do PC é o SHA-256 do certificado em DER.
- O TXT do anúncio mDNS ganha `fp=<16 primeiros caracteres hex da digital>`, para o celular reconhecer o PC pareado mesmo se o IP mudar.

## Pareamento (uma vez por celular)

1. O usuário pede o pareamento no host (nesta fase, o DevHost imprime o QR no console; o QR na bandeja fica para a Parte 6).
2. O host gera um **segredo de pareamento** de 32 bytes aleatórios, válido por 2 minutos e de uso único (uma sessão de pareamento ativa por vez; pedir de novo invalida a anterior), e mostra o QR com a URI:
   `screenshare://pair?h=<ip>&p=38700&fp=<digital em base64url>&s=<segredo em base64url>&n=<nome do PC, URL-encoded>`
3. O celular lê o QR (leitor do Google Play Services, `com.google.android.gms:play-services-code-scanner`, sem permissão de câmera), abre TLS para `h:p` e só aceita o certificado cuja digital é exatamente `fp`.
4. O celular envia `PAIR` (segredo + nome do aparelho). O host compara o segredo em tempo constante, consome a sessão, gera uma **chave de acesso** de 32 bytes aleatórios, registra o aparelho e responde `PAIRED` (chave).
5. O celular salva o PC pareado e segue direto para `AUTH` + `HELLO` na mesma conexão.

## Conexão normal

- **Wi-Fi:** TLS com a digital fixa → `AUTH` (chave) → `HELLO` → `CONFIG` → resto da sessão como no v1.
- **USB:** TCP em `127.0.0.1:38701` → `HELLO` → `CONFIG` → resto da sessão como no v1. *(Substituído: agora igual ao Wi-Fi; ver Revisão.)*

## Protocolo v2

`protocolVersion` passa a 2. O layout de 9 bytes do `HELLO` fica congelado em todas as versões futuras; extensões entram em mensagens novas.

| Código | Nome | Direção | Payload |
|---|---|---|---|
| 8 | PAIR | celular → PC | `secret` (32 bytes) + `deviceNameLength` (u8, 1..64) + `deviceName` (UTF-8) |
| 9 | PAIRED | PC → celular | `token` (32 bytes) |
| 10 | AUTH | celular → PC | `token` (32 bytes) |
| 11 | DENIED | PC → celular | `reason` (u8): 1 = segredo inválido/expirado/usado; 2 = aparelho não pareado ou removido; 3 = versão incompatível |

Regras:
- Na porta 38700, a primeira mensagem tem de ser `PAIR` ou `AUTH`; depois de `PAIR`/`PAIRED` vem `AUTH`. Qualquer outra coisa → `DENIED` (quando houver motivo aplicável) e fechamento.
- Na porta 38701, a primeira mensagem tem de ser `HELLO`; `PAIR`/`AUTH` ali são erro de protocolo. *(Substituído: nas duas portas a primeira mensagem é `PAIR` ou `AUTH`; ver Revisão.)*
- `HELLO` com `protocolVersion` diferente de 2 → `DENIED(3)` e fechamento (nas duas portas).
- Depois de `DENIED` o PC fecha a conexão. O celular mostra "Pareie de novo" (1, 2) ou "Atualize o app" (3), e no caso 2 apaga o pareamento salvo.
- Vetores novos em `docs/protocol-vectors/`: `pair.hex`, `paired.hex`, `auth.hex`, `denied.hex`; os vetores existentes de `HELLO` passam a usar a versão 2.

## Armazenamento

- **PC:** `%APPDATA%\ScreenShare\paired-devices.json` — lista de `{ id, name, tokenSha256 (hex), pairedAt (ISO 8601) }`. Só o hash da chave é guardado. Gravação atômica (arquivo temporário + rename).
- **Celular:** um único PC pareado — `{ pcName, fingerprint, token, lastHost }` em DataStore; a chave é cifrada com AES-GCM usando uma chave do Android Keystore (não usar EncryptedSharedPreferences, descontinuada).

## Componentes

**PC (C#)**
- `ScreenShare.Core.Protocol`: as 4 mensagens novas no `MessageCodec` (sem rede, sem TLS) e `ProtocolVersion = 2`.
- `ScreenShare.Core.Security` (novo namespace):
  - `HostIdentity` — gera/carrega o certificado e expõe a digital.
  - `PairingSession` — segredo, validade, uso único, comparação em tempo constante.
  - `DeviceRegistry` — adicionar, autenticar por chave (hash + tempo constante), listar, remover; persistência em JSON.
  - `PairingUri` — monta a URI do QR.
- `ScreenShare.DevHost`: dois listeners (TLS na 38700 com `SslStream`, TCP puro na 38701 em loopback), fluxo PAIR/AUTH, comando de console para gerar o QR (QR em texto no terminal) e para listar/remover aparelhos; TXT do mDNS com `fp`. *(Substituído: ver Revisão: USB autenticado.)*

**Android (Kotlin)**
- `protocol`: as 4 mensagens novas e `PROTOCOL_VERSION = 2`.
- `security/PinnedTrustManager` — aceita só o certificado cuja digital SHA-256 é a esperada.
- `security/PairingStore` — salva/lê/apaga o PC pareado (DataStore + Keystore).
- `pairing/PairingUri` — interpreta a URI do QR.
- `net/Connection` — modo Wi-Fi (TLS + `AUTH`, ou `PAIR` antes no primeiro uso) e modo USB (`127.0.0.1:38701`, sem TLS); estados novos para "não pareado" e "pareamento recusado". *(Substituído: ver Revisão: USB autenticado.)*
- UI: botão "Parear com PC" (abre o leitor de QR), lista de descoberta filtrando pelo `fp` do PC pareado. *(Substituído: os botões agora são "Parear pelo Wi-Fi (QR)" e "Parear pelo cabo USB (QR)"; ver Revisão: USB autenticado.)*

## Erros

- Digital diferente da esperada no TLS → o celular aborta antes de enviar qualquer mensagem e mostra "Este não é o PC pareado".
- Segredo expirado, já usado ou errado → `DENIED(1)`.
- Chave desconhecida (nunca pareado ou removido) → `DENIED(2)`; o celular apaga o pareamento salvo.
- Certificado do PC apagado/regenerado → a digital muda e o celular recusa; é preciso parear de novo.
- `paired-devices.json` corrompido → o host não sobe silenciosamente: registra o erro e trata como lista vazia, preservando o arquivo ruim como `.bak`.

## Testes

- **Vetores:** codec C# e Kotlin de `PAIR`, `PAIRED`, `AUTH`, `DENIED` contra os `.hex` novos; `deviceName` vazio ou acima de 64 bytes rejeitado.
- **C# unitários:** `PairingSession` (segredo certo, errado, expirado, reutilizado, sessão substituída); `DeviceRegistry` (autenticar, remover, persistir e recarregar, JSON corrompido); `HostIdentity` (gera uma vez e recarrega a mesma digital); `PairingUri`.
- **C# integração:** `SslStream` cliente contra o `HostServer` real — pareamento completo, reconexão com a chave, chave removida → `DENIED(2)`, porta 38700 sem `AUTH` → recusa, porta 38701 só em loopback.
- **Kotlin unitários:** `PairingUri` (válida, campos faltando, base64 inválido); `PinnedTrustManager` (aceita a digital certa, recusa outra).
- **Manual:** escanear o QR no celular, reconectar após reiniciar o app, remover o aparelho no PC e ver "Pareie de novo", conectar por USB sem pareamento. *(Substituído: USB sem pareamento pede pareamento; parear pelo cabo funciona. Ver Revisão: USB autenticado.)*

## Fora de escopo

- QR na bandeja do Windows (Parte 6, junto com a bandeja).
- Mais de um PC pareado no mesmo celular.
- Rotação ou revogação do certificado do PC.
- Código alternativo para digitar quando a câmera falhar.

## Revisão: USB autenticado (2026-10-03)

**Por que mudou.** A decisão original supunha que a porta do USB só era alcançável por quem tem o cabo. Na prática:

- O `adb reverse tcp:38701 tcp:38701` abre `127.0.0.1:38701` **dentro do celular**, e qualquer app com internet pode se conectar a esse endereço. Sem TLS nem chave, esse app veria a tela do PC (Parte 3) e injetaria toques (Parte 5).
- O inverso também vale: um app malicioso pode ocupar a porta 38701 do celular **antes** do `adb reverse` e se passar pelo PC. Sem TLS, o ScreenShare entregaria a ele a chave de acesso, que depois valeria também pelo Wi-Fi.

**Nova regra.** O USB fica igual ao Wi-Fi:

- A porta 38701 exige TLS (1.2+) com o certificado do PC, cuja digital o celular já tem fixada, e continua escutando só em `127.0.0.1` do PC. Só a 38700 é anunciada por mDNS.
- Nas duas portas a primeira mensagem tem de ser `PAIR` ou `AUTH`; qualquer outra coisa, inclusive `HELLO` direto, recebe `DENIED(2)`. Só depois vêm `HELLO` e `CONFIG`. TCP puro não tem sessão.
- O pareamento também pode ser feito pelo cabo: o mesmo QR, com o app conectando em `127.0.0.1:38701` em vez do IP de LAN do QR (botão **Parear pelo cabo USB (QR)**; o do Wi-Fi é **Parear pelo Wi-Fi (QR)**). Com o app pareado, **Conectar por cabo USB** faz `AUTH` pela 38701, e **Parear de novo (QR)** refaz o pareamento.
- Sem pareamento, o app não oferece a conexão por cabo e pede que o PC seja pareado primeiro.

O custo de latência é desprezível: um handshake por conexão e depois AES-GCM por hardware.

