# Parte 2: monitor virtual (design)

> Revisado em 2026-10-04 com os resultados do spike (seção no fim). A primeira versão controlava o monitor pelo pipe do driver; o spike mostrou que esses comandos derrubam o driver, então esse caminho foi abandonado.

## Contexto

Com as Partes 1 e 4 e o pareamento prontos, o celular conecta ao PC pelo Wi-Fi ou pelo cabo, mas o Windows ainda não enxerga monitor nenhum. A Parte 2 faz o Windows ganhar um **monitor de verdade** quando o celular conecta e o perder quando ele desconecta. Esse monitor aparece em Configurações › Tela, na resolução do celular. A Parte 3 vai capturar esse monitor e mandar o vídeo, e a Parte 5 vai injetar os toques nas coordenadas dele.

Uso pessoal: um PC com Windows 11 e um celular do mesmo dono.

## Decisões

- **Driver:** [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) (VDD), MIT, assinado pela SignPath. É um driver IddCx em modo usuário (UMDF). O pacote é o zip "Driver Only" do release **25.7.23**, que traz o driver 24.12.24 (veja "Fatos do VDD"). Escrever um driver próprio está fora do escopo, porque exigiria assinatura.
- **O ScreenShare instala o driver sozinho.** Ao iniciar, se o driver falta, o host pergunta e se reabre como administrador **só para instalar**. O host em si nunca roda como administrador.
- **Ligar e desligar sem administrador, pelo próprio Windows:**
  - "ligar" é anexar a saída do VDD à área de trabalho, com posição e resolução;
  - "desligar" é desanexá-la, com tamanho 0×0.
  
  As duas operações usam `ChangeDisplaySettingsEx` e são o mesmo que "Desconectar este monitor" em Configurações. O driver fica **sempre com um monitor** (count 1). Com o driver instalado, esse monitor aparece em Configurações › Tela mesmo desligado, só que fora da área de trabalho.
- **O pipe do VDD não é usado.** `SETDISPLAYCOUNT` e `RELOAD_DRIVER` derrubam o processo do driver, e o Windows o desabilita depois de algumas quedas.
- **Resolução nativa do celular + escala automática:** o monitor usa o `width×height` do `HELLO` (sempre em paisagem: o app manda o maior lado como largura), arredondado para números pares. A escala do Windows é calculada pelo DPI do celular e só é aplicada na primeira vez que aquela resolução aparece. Depois disso vale o que o usuário escolher em Configurações › Tela.
- **Resolução nova:** o driver só lê a lista de resoluções quando o dispositivo inicia, e reiniciar o dispositivo exige administrador. Então, na primeira vez que uma resolução nova aparece:
  - o host a acrescenta no XML, sem administrador;
  - pede **um UAC** para reiniciar o driver (modo `restart-driver`);
  - enquanto isso, a conexão segue com a resolução disponível mais próxima;
  - quando o driver volta, o host aplica a resolução exata se o celular ainda estiver conectado.
  
  Isso acontece uma vez por resolução, ou seja, na prática uma vez por celular.
- **Um monitor só, com contagem de referências.** Vários celulares ao mesmo tempo continuam fora do escopo, mas uma segunda conexão simultânea não quebra: ela recebe o monitor que já existe, na resolução em que ele está.
- **Posição:** a primeira vez, à direita da tela mais à direita, no topo. Ao desligar, o host guarda a posição atual e a reaproveita na próxima vez, assim o arranjo que o usuário fizer em Configurações › Tela continua valendo.
- **O protocolo não muda.** O `CONFIG` leva a resolução **realmente aplicada** ao monitor (ou a pedida, sem monitor).

## Fatos do VDD (confirmados no spike)

| Item | Valor |
|---|---|
| Pacote | `https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/25.7.23/VirtualDisplayDriver-x86.Driver.Only.zip`, SHA-256 `e24210692b442b39af763536330ce78b423f19342b7a7792c26de3944e418b3a`. Apesar do nome, traz o driver 24.12.24 x64 (`DriverVer = 12/24/2024,11.30.4.434`) |
| Arquivos | `VirtualDisplayDriver\MttVDD.inf`, `mttvdd.cat`, `MttVDD.dll`, `vdd_settings.xml` |
| ID de hardware | `Root\MttVDD` (dispositivo criado pela raiz, por exemplo `ROOT\DISPLAY\0000`), classe Display `{4d36e968-e325-11ce-bfc1-08002be10318}` |
| Configuração | `C:\VirtualDisplayDriver\vdd_settings.xml`, lida só quando o dispositivo inicia: `<monitors><count>`, `<resolutions><resolution><width/><height/><refresh_rate/>`, `<global><g_refresh_rate>` |
| Como achar a saída | `EnumDisplayDevices`: o adaptador com `DeviceID = "Root\MttVDD"` (`DeviceString = "Virtual Display Driver"`). O nome `\\.\DISPLAYn` **muda a cada reinício do driver**, então nunca é guardado |
| Escala | `DisplayConfigGetDeviceInfo` tipo `-3` (ler) e `DisplayConfigSetDeviceInfo` tipo `-4` (gravar), em passos relativos à escala recomendada (lista 100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500; índice da recomendada = `|min|`). Num monitor sem tamanho físico, o máximo informado é 175% |

## Componentes

Projeto novo: **`host/ScreenShare.Display`** (`net10.0-windows`). O `ScreenShare.DevHost` e o `ScreenShare.Tests` passam a ser `net10.0-windows` e a referenciá-lo. O `ScreenShare.Core` não muda.

| Componente | Responsabilidade |
|---|---|
| `VddSettingsFile` | Lê e grava o `vdd_settings.xml`: garante que uma resolução está na lista (com 60 Hz), sem duplicar e preservando todo o resto do arquivo; cria o mínimo (count 1) se faltar. XML que não abre → `VddSettingsException`. Gravação atômica. |
| `ScaleCalculator` | `escala = DPI ÷ 160 × 100`, arredondada (meio para cima) ao múltiplo de 25 mais próximo, limitada a 100–200. Ex.: 160 → 100, 240 → 150, 280 → 175, 420 → 200. |
| `DisplayStateStore` | `%LOCALAPPDATA%\ScreenShare\display.json`: as resoluções que já receberam a escala automática e a última posição do monitor. Arquivo corrompido conta como vazio. |
| `IDisplayTopology` / `DisplayTopology` | Win32: acha a saída do VDD (`VddOutput`: nome, anexada?, posição, tamanho, resoluções disponíveis); anexa com posição e resolução; desanexa; troca a resolução; lê e grava a escala; calcula a posição padrão. |
| `IDriverRestarter` / `ElevatedDriverRestarter` | Reinicia o driver pedindo UAC (`restart-driver`); devolve se deu certo. |
| `VirtualMonitorManager` (`IVirtualMonitorManager`) | O "DisplayManager" do spec geral. `Acquire(width, height, dpi)` devolve um `VirtualMonitorLease` (o `VirtualMonitor?` e um `Dispose` que libera). Contagem de referências, desligamento 10 s depois do último lease, resolução nova com reinício do driver, limpeza ao iniciar e desligamento no `Dispose`. Usa `TimeProvider`. Nunca lança por causa do monitor: falhas viram lease sem monitor. |
| `NullVirtualMonitorManager` | Sem monitor (flag `--sem-monitor`, driver ausente). |
| `VirtualMonitor` | `record(string DeviceName, int X, int Y, int Width, int Height, int ScalePercent)`: o que a captura (Parte 3) e o toque (Parte 5) consomem. |
| `IDriverSystem` / `WindowsDriverSystem` | As operações do Windows para instalar: detectar o dispositivo, baixar, extrair, certificados de `TrustedPublisher`, criar o dispositivo e instalar o `.inf` (SetupAPI), preparar o XML e as permissões, remover, reiniciar (`pnputil /restart-device`). |
| `DriverInstaller` | A sequência de instalação, desinstalação e reinício sobre um `IDriverSystem`, com os códigos de saída. |
| `ElevatedCommand` | Reabre o próprio executável como administrador (`runas`) e devolve o código de saída; UAC recusado = 5; qualquer outra falha = 4, sem lançar. Não reabre o `dotnet.exe` (rodando como `dotnet ScreenShare.DevHost.dll`): só o executável do host. |

### DevHost

- `install-driver [sid]`, `uninstall-driver`, `restart-driver`: fazem só isso e saem com um código. Se o processo não for administrador, ele se reabre como administrador (é assim que o usuário roda `dotnet run --project host/ScreenShare.DevHost -- uninstall-driver`). Em caso de falha, a janela elevada espera Enter antes de fechar.
- **Ao iniciar (sem flags):**
  - **Driver presente:** cria o `VirtualMonitorManager`, que desliga um monitor que tenha ficado ligado (host que caiu antes).
  - **Driver ausente:** pergunta `O driver de monitor virtual não está instalado. Instalar agora? [S/n]`. Com "sim", roda `install-driver <SID do usuário>` como administrador e espera.
    - Se der certo, segue com monitor.
    - Se você responder "não", recusar o UAC ou a instalação falhar, segue **sem monitor**, com um aviso.
- `--sem-monitor`: não detecta nem instala nada (`NullVirtualMonitorManager`).
- `HostServer` recebe um `IVirtualMonitorManager`:
  - Depois do `HELLO` com a versão certa, faz `using var lease = monitors.Acquire(...)` e manda `CONFIG(lease.Width, lease.Height, …)`.
  - O lease é liberado quando a sessão termina, por qualquer motivo.
  - Sessão que termina antes do `HELLO` não pega lease.
- Ctrl+C: o `Dispose` do gerenciador desliga o monitor na hora.

## Fluxo

### Conexão (`Acquire(w, h, dpi)`, serializado por uma trava)

1. **Normalizar o pedido:**
   - `w` fica entre 640 e 7680 e `h` entre 360 e 4320, e os dois são arredondados para baixo até número par;
   - o DPI fica entre 72 e 1000.
2. Achar a saída do VDD. Se não houver, devolve um lease sem monitor.
3. Se outra sessão já usa o monitor (anexado, contagem > 0), só incrementa a contagem e devolve o monitor como está.
4. **Escolher a resolução:**
   - Se `w×h` está entre as disponíveis, usa ela.
   - Se não está, e é a primeira vez nesta execução do host, acrescenta `w×h` no XML e dispara **em segundo plano** o reinício do driver (UAC).
   - Enquanto isso, usa a disponível mais próxima (menor `|Δw| + |Δh|`).
5. **Ligar ou ajustar:**
   - **Monitor desanexado:** anexa com a resolução escolhida na última posição guardada. Se o Windows recusar, tenta de novo na posição padrão.
   - **Monitor já anexado com outra resolução:** só troca a resolução.
6. Se a resolução aplicada é a exata e ainda não está no `display.json`, aplica `ScaleCalculator(dpi)` e grava. A escala é limitada ao máximo do Windows.
7. Relê a saída e devolve o `VirtualMonitor`. Cancela a espera de desligamento, se houver.

### Depois do reinício do driver (resolução nova)

1. O host espera até 5 s a saída voltar com a resolução nova.
2. **Celular ainda conectado:** aplica a resolução exata e a escala (passos 5 e 6). O `CONFIG` daquela sessão já foi enviado com a resolução antiga; a Parte 2 não tem vídeo, e a Parte 3 trata a troca de tamanho como uma recaptura.
3. **Ninguém conectado:** garante o monitor desanexado.

### Desconexão (fim do lease)

- A contagem cai. Ao chegar a 0, agenda o desligamento para daqui a **10 s**.
- Um `Acquire` antes disso cancela o agendamento, e o monitor continua lá, sem piscar.
- Ao desligar, guarda a posição atual no `display.json`.

### Fim do host

- O `Dispose` do gerenciador cancela o agendamento e desanexa o monitor, registrando erros sem lançar.

## Instalação (`install-driver`, como administrador)

1. Se já existe um dispositivo `Root\MttVDD` **com driver**, só prepara o XML e as permissões (passo 6) e sai com 0. Um dispositivo que ficou sem driver (instalação interrompida) não conta.
2. Baixa o zip por HTTPS e confere o SHA-256 fixado. Se não bater, aborta sem instalar nada (código 2).
3. Extrai numa pasta temporária.
4. Lê os certificados do `mttvdd.cat` (antes de instalar: se o catálogo não abrir, nada foi instalado) e anota as impressões dos certificados em `TrustedPublisher` (máquina).
5. **Instala o driver:**
   - remove dispositivos `Root\MttVDD` que tenham ficado sem driver (senão o driver seria ligado aos dois e haveria dois adaptadores);
   - cria o dispositivo `Root\MttVDD` (SetupAPI: `SetupDiCreateDeviceInfoList`, `SetupDiCreateDeviceInfoW` com `DICD_GENERATE_ID`, `SetupDiSetDeviceRegistryPropertyW(SPDRP_HARDWAREID)` e `SetupDiCallClassInstaller(DIF_REGISTERDEVICE)`);
   - instala com `UpdateDriverForPlugAndPlayDevicesW`. O Windows mostra uma vez a confirmação "Deseja instalar este software de dispositivo?".
   - Se a instalação falhar, o dispositivo recém-criado é removido.
6. Cria `C:\VirtualDisplayDriver\vdd_settings.xml` se faltar (count 1, 1920×1080@60). Dá permissão de modificação na pasta ao usuário (SID recebido do processo pai) e a `LOCAL SERVICE`.
7. **Remove de `TrustedPublisher`** os certificados que não estavam lá antes e que vêm do `mttvdd.cat`. A caixa "Sempre confiar" da confirmação do Windows vem marcada; o driver já instalado não precisa dela.
8. Apaga a pasta temporária e sai com 0.

Códigos de saída:

| Código | Significado |
|---|---|
| 0 | Ok |
| 2 | SHA-256 diferente (nada instalado) |
| 3 | Download falhou (a mensagem traz o link do release para instalar à mão) |
| 4 | Erro do SetupAPI ou do `pnputil` (com o código do Windows) |
| 5 | Usuário recusou o UAC ou a confirmação do Windows |

**Desinstalação (`uninstall-driver`):**
1. Para cada dispositivo `Root\MttVDD` (presente ou não): lê o `DEVPKEY_Device_DriverInfPath` (`oemNN.inf`) e remove o dispositivo com `DIF_REMOVE`.
2. Remove o pacote com `SetupUninstallOEMInfW(SUOI_FORCEDELETE)`.
3. Apaga `C:\VirtualDisplayDriver\`.

Sem driver instalado, sai com 0.

**Reinício (`restart-driver`):** `%SystemRoot%\System32\pnputil.exe /restart-device <ID da instância>` para cada dispositivo `Root\MttVDD` presente. Sem nenhum dispositivo, é erro (`0xE000020B`, "não existe"), não sucesso. Código diferente de 0 → 4.

## Erros em tempo de uso

| Situação | Comportamento |
|---|---|
| Driver ausente (instalação recusada ou falhou) | Funciona sem monitor: um aviso no console ao iniciar; o `CONFIG` leva a resolução pedida (normalizada) |
| Saída não encontrada, o Windows recusa anexar nas duas posições ou qualquer exceção | Registra o erro e devolve um lease sem monitor. **A sessão nunca cai por causa do monitor.** |
| O Windows recusa a resolução | Mantém a que está e a informa no `CONFIG` |
| A escala falha | Deixa como está, registra o erro e não grava no `display.json` (tenta de novo na próxima vez) |
| XML corrompido | Registra o erro; não pede reinício; usa a resolução disponível mais próxima; o arquivo não é sobrescrito |
| Falha ao gravar o `display.json` | Registra e segue: a escala e a posição só não ficam lembradas; ligar e desligar nunca dependem disso |
| Erro depois de o monitor já ter sido ligado | O monitor é desligado de novo (ninguém ficou com ele) e a sessão segue sem monitor |
| UAC do reinício recusado | Registra o erro; não pergunta de novo até o host reiniciar; a conexão segue com a resolução mais próxima |

## Segurança

- O código que roda como administrador se limita aos modos `install-driver`, `uninstall-driver` e `restart-driver`. Eles não abrem portas nem leem dados da rede além do download, que só vem de `github.com` por HTTPS e passa pelo SHA-256 fixado.
- O certificado do driver não fica em `TrustedPublisher`, salvo se já estava antes.
- O pipe do VDD aceita comandos de qualquer processo local (permissão Everyone, definida no driver), e alguns comandos derrubam o driver. O ScreenShare não usa o pipe. **Risco aceito:** quem já roda código no PC já controla a tela.
- O XML do VDD fica com permissão de modificação para o usuário. Um programa do usuário pode mudar a lista de resoluções, o que tem o mesmo alcance do risco acima.

## Testes

Automáticos (xUnit, `dotnet test host/ScreenShare.slnx`, no CI `windows-latest`, sem driver):

| Alvo | Casos |
|---|---|
| `VddSettingsFile` | Acrescenta uma resolução; não duplica; preserva comentários e outros elementos; cria o arquivo mínimo; XML inválido gera `VddSettingsException` sem sobrescrever |
| `ScaleCalculator` | Os exemplos e os limites (72 → 100, 1000 → 200) |
| `DisplayStateStore` | Ida e volta de resoluções e posição; arquivo corrompido conta como vazio |
| `DriverInstaller` | Já instalado; download falha; SHA-256 errado; sucesso (prepara o XML, apaga o temporário); certificado novo do catálogo é removido e os outros ficam; usuário recusa; erro do SetupAPI; desinstalação; reinício |
| `VirtualMonitorManager` | Com topologia e reinício falsos e `FakeTimeProvider`: limpeza ao iniciar; primeira conexão anexa com resolução e escala; segunda conexão compartilha; desliga 10 s depois do último lease; reconexão em 9 s não desliga; troca de resolução; escala só na primeira vez; resolução nova pede reinício uma vez e aplica a exata depois; reinício recusado; driver ausente; exceções viram lease sem monitor; posição guardada e plano B na posição padrão; normalização; `Dispose` |
| `HostServer` | O `CONFIG` leva a resolução do lease; o lease é liberado ao desconectar; sessão sem `HELLO` não pega lease |

Integração, opcional (só com `SCREENSHARE_VDD_TESTS=1` e o driver instalado): `DisplayTopology` acha a saída, anexa, troca resolução e escala e desanexa.

Manuais, no PC do usuário, ao fim do plano:
1. **Primeira execução:** o host pergunta, pede o UAC e a confirmação do Windows, e instala.
   - Em Gerenciador de Dispositivos › Adaptadores de vídeo aparece **Virtual Display Driver**.
   - Em Configurações › Tela, o monitor aparece desconectado (fora da área de trabalho).
   - O certificado da SignPath não ficou em `TrustedPublisher`.
2. **Primeira conexão do celular:** aparece o UAC do reinício do driver (resolução nova). Depois, o monitor fica com a resolução do celular e a escala automática, e o console mostra nome, posição e escala.
3. **Segunda conexão:** não há UAC, e a resolução já vem exata.
4. Uma janela levada para lá com Win+Shift+→ some da vista, porque ainda não há vídeo.
5. **Ao desconectar:** o monitor sai da área de trabalho em cerca de 10 s. Reconectando antes disso, ele não pisca.
6. **Com Ctrl+C no host:** o monitor sai na hora.
7. **Escala e posição escolhidas à mão:** mudar a escala para 150% e arrastar o monitor para a esquerda em Configurações, depois desconectar e reconectar: os dois continuam como você deixou.
8. **`uninstall-driver`:** remove o driver e a pasta `C:\VirtualDisplayDriver`.

## Documentação

- `README.md`:
  - a instalação do driver (o que aparece, os UACs e a confirmação do Windows);
  - `--sem-monitor`, `uninstall-driver`;
  - o monitor que aparece desconectado em Configurações;
  - a Parte 2 marcada no roteiro.
- `docs/guia-do-codigo.md`: seção sobre o `ScreenShare.Display`.

## Fora do escopo

- Vídeo (Parte 3) e toque (Parte 5).
- Bandeja e escolha de resolução ou qualidade (Parte 6).
- HDR, taxas acima de 60 Hz e mais de um monitor virtual.
- Mandar um `CONFIG` novo no meio da sessão quando a resolução exata chega depois do reinício (Parte 3).

## Histórico: achado ao preparar o plano

- O zip do release 25.7.23 traz o driver 24.12.24.
- No código desse driver, o XML só é lido quando o dispositivo inicia, e `RELOAD_DRIVER`/`SETDISPLAYCOUNT` pegam o contexto WDF a partir do *handle do pipe*.
- Por isso, o controle pelo pipe foi para o spike, que confirmou que ele derruba o driver.

## Resultados do spike (2026-10-04, PC do usuário: Windows 11 26200, RTX 5060 Ti, monitor Samsung LC34G55T 3440×1440)

| # | Experimento | Resultado |
|---|---|---|
| E1 | Instalar via SetupAPI (`DIF_REGISTERDEVICE` + `UpdateDriverForPlugAndPlayDevicesW`) | ✅ `ok=True`, `reboot=False`, pacote `oem288.inf`, dispositivo `ROOT\DISPLAY\0000` com status OK. O monitor (1920×1080) apareceu à direita do principal, em (3440,0), sem virar o principal. A confirmação do Windows apareceu, e como a caixa "Sempre confiar em software de SignPath Foundation" vem marcada, o certificado (`3CF8CF26D8BA266C3A483AB7D26D4A818E317D76`) **foi gravado em `TrustedPublisher`** pela instalação. |
| E2 | `PING` / `GETSETTINGS` pelo pipe, sem admin | Conecta em ~2 ms; a resposta vem vazia. |
| E3 | Identificar o VDD | Adaptador: `DeviceString = "Virtual Display Driver"`, `DeviceID = "Root\MttVDD"`. Monitor `MONITOR\MTT1337\…`. CCD: alvo "VDD by MTT", adaptador `\\?\ROOT#DISPLAY#0000#…`. **O nome `\\.\DISPLAYn` muda a cada reinício do driver** (5 → 6 → … → 11). |
| E4 | `SETDISPLAYCOUNT 0` | ❌ Grava `count 0` e **derruba o processo do driver**; o monitor continua lá depois que ele volta. |
| E5 | Resolução nova (2400×1080) no XML | `RELOAD_DRIVER` torna a resolução disponível em menos de 1 s, mas **derrubando o processo do driver**. Na 6ª queda da sessão, o Windows **desabilitou o dispositivo** (status `Error`). ✅ `pnputil /restart-device ROOT\DISPLAY\0000` (admin, 147 ms) recuperou o dispositivo e releu o XML. |
| E6 | Desanexar (0×0) e anexar (posição + tamanho) com `ChangeDisplaySettingsEx`, sem admin | ✅ ~130 ms cada; **10 ciclos sem falha**, mesmo processo do driver, dispositivo OK. |
| E6b | Monitor desanexado e PC reiniciado com o host fechado | ✅ Continua desanexado depois do login. Continua **listado em Configurações › Tela** enquanto o driver estiver instalado. |
| E7 | Aplicar 2400×1080, sem admin | ✅ |
| E8 | Escala via CCD (`-3` / `-4`), sem admin | ✅ Recomendada 100% (`min 0`), máximo informado 175% (`max 3`); 200% foi aceito mesmo assim. A escolha manual de 150% em Configurações foi lida de volta como 150%. |
| E9 | Desinstalar (`DIF_REMOVE` + `SetupUninstallOEMInfW(SUOI_FORCEDELETE)`), admin | ✅ Dispositivo e pacote removidos sem reiniciar; nada de `mttvdd` em `pnputil /enum-drivers`. A pasta `C:\VirtualDisplayDriver` sobra, e a desinstalação do ScreenShare a apaga. |
