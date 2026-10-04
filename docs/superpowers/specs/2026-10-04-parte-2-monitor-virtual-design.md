# Parte 2: monitor virtual (design)

## Contexto

Com as Partes 1 e 4 e o pareamento prontos, o celular conecta ao PC pelo Wi-Fi ou pelo cabo, mas o Windows ainda não enxerga monitor nenhum. A Parte 2 faz o Windows ganhar um **monitor de verdade** quando o celular conecta e o perder quando ele desconecta. Esse monitor aparece em Configurações › Tela, na resolução do celular. A Parte 3 vai capturar esse monitor e mandar o vídeo, e a Parte 5 vai injetar os toques nas coordenadas dele.

Uso pessoal: um PC com Windows 11 e um celular do mesmo dono.

## Decisões

- **Driver:** [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) (VDD), MIT, assinado pela SignPath. É um driver IddCx em modo usuário (UMDF). A versão é fixada em **25.7.23** e o pacote é o zip "Driver Only" do release, que a instalação silenciosa oficial (`Community Scripts/silent-install.ps1`) usa. Escrever um driver próprio está fora do escopo, porque exigiria assinatura.
- **O ScreenShare instala o driver sozinho.** Ao iniciar, se o driver falta, o host pergunta e se reabre como administrador **só para instalar**. O host em si nunca roda como administrador.
- **Controle sem administrador:** o monitor é ligado e desligado pelo pipe do VDD (`SETDISPLAYCOUNT`). O modo e a escala são aplicados pelas APIs de configuração de tela do Windows (CCD). Desligar e ligar o dispositivo inteiro (`Disable-PnpDevice`) foi descartado porque exige administrador a cada conexão.
- **Resolução nativa do celular + escala automática:** o monitor usa exatamente o `width×height` do `HELLO`, sempre em paisagem, porque o app manda o maior lado como largura. A escala do Windows é calculada pelo DPI do celular e só é aplicada na primeira vez que aquela resolução aparece. Depois disso vale o que o usuário escolher em Configurações › Tela.
- **Um monitor só, com contagem de referências.** Vários celulares ao mesmo tempo continuam fora do escopo, mas uma segunda conexão simultânea não quebra: ela recebe o monitor que já existe, na resolução em que ele está.
- **O protocolo não muda.** O `CONFIG` já leva "a resolução do monitor virtual". A partir de agora, ele leva a resolução **realmente aplicada**.

## Fatos do VDD (lidos no código-fonte, a confirmar no spike)

| Item | Valor |
|---|---|
| ID de hardware | `Root\MttVDD` (dispositivo enumerado pela raiz) |
| Arquivos do pacote | `MttVDD.inf`, `mttvdd.cat`, a DLL do driver, dentro de `VirtualDisplayDriver\` no zip |
| Configuração | `C:\VirtualDisplayDriver\vdd_settings.xml` (o driver também lê valores de `HKLM\SOFTWARE\MikeTheTech\VirtualDisplayDriver`) |
| Pipe | `\\.\pipe\MTTVirtualDisplayPipe`, comandos em UTF-16 com até 127 caracteres, permissão `D:(A;;GA;;;WD)` (qualquer processo local) |
| Comandos usados | `PING`; `SETDISPLAYCOUNT <n>` (grava `<monitors><count>` no XML e reinicia o adaptador); `RELOAD_DRIVER` (reinicia o adaptador, relendo o XML) |
| Modos | `<resolutions><resolution><width/><height/><refresh_rate/></resolution>…` mais `<global><g_refresh_rate>` |

O processo do driver (UMDF) grava no XML quando recebe `SETDISPLAYCOUNT`. Por isso, o XML precisa poder ser escrito também pela conta do serviço, e não só pelo usuário.

## Componentes

Projeto novo: **`host/ScreenShare.Display`** (`net10.0-windows`). O `ScreenShare.DevHost` e o `ScreenShare.Tests` passam a ser `net10.0-windows` e a referenciá-lo. O `ScreenShare.Core` não muda.

| Componente | Responsabilidade |
|---|---|
| `VddSettingsFile` | Lê e grava o `vdd_settings.xml`: garante que um modo `w×h@60` está em `<resolutions>` sem duplicar e preserva todo o resto do arquivo. Se o arquivo não existe, cria um mínimo, com `<count>0</count>` e os modos pedidos. Um XML que não abre gera `VddSettingsException` com mensagem clara. |
| `ScaleCalculator` | `escala = DPI ÷ 160 × 100`, arredondada para o múltiplo de 25 mais próximo e limitada a 100–200. Exemplos: 160 → 100, 240 → 150, 280 → 175, 420 → 200. |
| `IVddControl` / `VddPipeClient` | `PingAsync`, `SetDisplayCountAsync(n)` e `ReloadAsync` pelo pipe. Cada comando abre uma conexão, escreve a string UTF-16 e fecha. Prazo de 2 s para conectar. O nome do pipe vem pelo construtor, para os testes. |
| `IDisplayTopology` / `DisplayTopology` | Via CCD (`QueryDisplayConfig`, `DisplayConfigGetDeviceInfo`): acha a saída do VDD, espera ela aparecer (até 5 s), aplica o modo (`ChangeDisplaySettingsEx` com `CDS_UPDATEREGISTRY`) e a escala (`DisplayConfigSetDeviceInfo`, tipo não documentado `-4`), e lê o nome GDI (`\\.\DISPLAYn`) e as coordenadas na área de trabalho. |
| `ScaledResolutionsStore` | Guarda em `%LOCALAPPDATA%\ScreenShare\display.json` as resoluções que já receberam a escala automática. Se o arquivo estiver corrompido, conta como vazio e grava um novo, sem lançar exceção. |
| `VirtualMonitorManager` (`IVirtualMonitorManager`) | O "DisplayManager" do spec geral. `AcquireAsync(width, height, dpi)` devolve um `VirtualMonitorLease`: o `VirtualMonitor?` mais um `Dispose` que libera. Faz a contagem de referências, a espera de 10 s ao chegar a 0, a limpeza ao iniciar e o desligamento no `Dispose`. Usa `TimeProvider`. |
| `VirtualMonitor` | `record(string GdiDeviceName, int X, int Y, int Width, int Height, int ScalePercent)`: o que a captura (Parte 3) e o toque (Parte 5) consomem. |
| `DriverDetector` | Diz se um dispositivo `Root\MttVDD` está presente (SetupAPI, sem administrador). |
| `DriverInstaller` | Só no modo `install-driver`, como administrador: baixa, confere, instala e prepara o XML. No modo `uninstall-driver`, desfaz tudo. |

### DevHost

- **Ao iniciar (sem flags):**
  - **Driver presente:** cria o `VirtualMonitorManager`, que manda `SETDISPLAYCOUNT 0` para limpar um monitor que tenha sobrado de um host que caiu antes.
  - **Driver ausente:** pergunta `O driver de monitor virtual não está instalado. Instalar agora? [S/n]`.
    - Com "sim", reabre o próprio executável (`Environment.ProcessPath`) com `Verb = "runas"` e o argumento `install-driver` e espera ele sair.
    - Se der certo, segue com monitor.
    - Se você responder "não", recusar o UAC ou a instalação falhar, o host segue **sem monitor**, com um aviso.
- `install-driver` / `uninstall-driver`: só fazem isso e saem com um código (0 = ok). Não abrem portas nem leem a identidade TLS.
- `--sem-monitor`: não detecta nem instala nada e usa um `IVirtualMonitorManager` nulo, que mantém o comportamento atual.
- `HostServer` recebe um `IVirtualMonitorManager`:
  - Depois do `HELLO`, chama `AcquireAsync` e manda `CONFIG(monitor.Width, monitor.Height, …)`. Sem monitor, manda o `width×height` do `HELLO`, já limitado, como hoje.
  - O lease é liberado num `finally` quando a sessão termina, por qualquer motivo.
  - Se a sessão terminar antes do `HELLO`, ela não pega lease nenhum.
- Ctrl+C: o `Dispose` do gerenciador desliga o monitor na hora.

## Fluxo

### Conexão (`AcquireAsync(w, h, dpi)`), serializado por uma trava

1. **Limitar o pedido:** `w` fica entre 640 e 7680 e `h` entre 360 e 4320. O DPI fica entre 72 e 1000.
2. **Monitor ligado e com outra resolução:**
   - se há outra sessão usando, só incrementa a contagem e devolve o monitor como está;
   - se não há, segue para trocar o modo (passos 3 e 5).
3. Se `w×h@60` não está no XML, acrescenta o modo. Com o monitor ligado, manda `RELOAD_DRIVER`; com ele desligado, o próximo passo já faz o driver reler o XML.
4. Se o monitor está desligado: `SETDISPLAYCOUNT 1` e espera a saída do VDD aparecer (até 5 s).
5. Aplica o modo `w×h`.
6. Se `w×h` não está no `display.json`, aplica `ScaleCalculator(dpi)` e grava a resolução no `display.json`.
7. Lê nome GDI, coordenadas, tamanho e escala reais e devolve o `VirtualMonitor`. Cancela a espera de desligamento, se houver.

### Desconexão (fim do lease)

- A contagem cai. Ao chegar a 0, agenda o desligamento para daqui a **10 s**: `SETDISPLAYCOUNT 0`.
- Um `AcquireAsync` antes disso cancela o agendamento, e o monitor continua lá, sem piscar.

### Fim do host

- O `Dispose` do gerenciador cancela o agendamento e manda `SETDISPLAYCOUNT 0`, engolindo e registrando erros.

## Instalação (`install-driver`, como administrador)

1. Se `DriverDetector` já vê `Root\MttVDD`, só garante o XML (passo 5) e sai com 0.
2. Baixa, por HTTPS, o zip "Driver Only" do release 25.7.23 para uma pasta temporária e confere o **SHA-256 fixado no código**. Se não bater, aborta sem instalar nada (código 2).
3. Extrai o zip e localiza `MttVDD.inf`.
4. **Instala o driver:**
   - cria o dispositivo `Root\MttVDD` via SetupAPI, com P/Invoke: `SetupDiCreateDeviceInfoList`, `SetupDiCreateDeviceInfoW`, `SetupDiSetDeviceRegistryPropertyW(SPDRP_HARDWAREID)` e `SetupDiCallClassInstaller(DIF_REGISTERDEVICE)`;
   - instala o driver com `UpdateDriverForPlugAndPlayDevicesW`.
   - O certificado da SignPath **não** é colocado em `TrustedPublisher`. O Windows mostra uma vez a própria confirmação ("Deseja instalar este software de dispositivo?"), e não fica uma confiança permanente nesse editor.
5. Cria `C:\VirtualDisplayDriver\` e o `vdd_settings.xml` mínimo (count 0, modo 1920×1080@60). Dá permissão de modificação ao usuário que pediu a instalação, cujo SID o processo pai passa como argumento, e à conta `LOCAL SERVICE`.
6. Apaga a pasta temporária e sai com 0.

Falhas:

| Falha | Código | Mensagem |
|---|---|---|
| Download | 3 | Inclui o link do release para instalar à mão |
| SHA-256 diferente | 2 | Aborta sem instalar |
| SetupAPI | 4 | Inclui o código de erro do Windows |
| Usuário recusa a confirmação do Windows | 5 | — |

Desinstalação (`uninstall-driver`):
1. Lê o `InfPath` (`oemNN.inf`) de cada dispositivo `Root\MttVDD`.
2. Remove cada dispositivo (`DIF_REMOVE`).
3. Remove o pacote com `SetupUninstallOEMInfW(SUOI_FORCEDELETE)`.
4. Apaga `C:\VirtualDisplayDriver\`.
5. Se não houver driver, sai com 0 sem fazer nada.

## Erros em tempo de uso

| Situação | Comportamento |
|---|---|
| Driver ausente (instalação recusada ou falhou) | Funciona sem monitor: um aviso no console ao iniciar; o `CONFIG` leva a resolução pedida |
| Pipe não conecta em 2 s, a saída não aparece em 5 s ou o CCD falha | Registra o erro e devolve um lease sem monitor. **A sessão nunca cai por causa do monitor.** |
| O Windows recusa o modo | Usa o modo que ficou ativo e o informa no `CONFIG` |
| A escala falha | Deixa a escala como está, registra o erro e não grava no `display.json`, para tentar de novo na próxima vez |
| XML corrompido | Erro registrado, sem monitor nesta conexão; o arquivo não é sobrescrito |

## Segurança

- O pipe do VDD aceita comandos de qualquer processo local (permissão Everyone, definida no driver). O efeito máximo é ligar e desligar o monitor virtual ou mudar a lista de resoluções. **Risco aceito:** quem já roda código no PC já controla a tela.
- O código que roda como administrador se limita ao modo `install-driver`/`uninstall-driver`: não abre portas nem lê dados da rede além do download, que só vem de `github.com` por HTTPS e passa pelo SHA-256 fixado.
- O certificado do driver não entra em `TrustedPublisher`.

## Spike (primeira tarefa do plano, código descartável)

O spike confirma no PC do usuário o que só a leitura do código não garante. O usuário aceita o UAC e a confirmação do Windows. Os resultados entram neste documento, numa seção "Resultados do spike", e o plano é ajustado conforme eles.

| Pergunta | Plano B se falhar |
|---|---|
| Nome, arquitetura (x64) e conteúdo do zip "Driver Only" 25.7.23; o SHA-256 | Usar o asset `Signed-Driver-v24.12.24-x64.zip` do release 24.12.24 |
| A instalação via SetupAPI sem `TrustedPublisher` funciona (com a confirmação do Windows)? | Importar o certificado em `TrustedPublisher`, como o script oficial faz, e registrar o risco |
| Com o XML em count 0, o driver sobe sem monitor? | Instalar com count 1 e mandar `SETDISPLAYCOUNT 0` logo depois |
| `SETDISPLAYCOUNT 0/1` funciona sem administrador e o monitor some/aparece? | Desativar e ativar o caminho do monitor pelo CCD (`SetDisplayConfig`), sem administrador |
| Um modo novo no XML aparece depois de `RELOAD_DRIVER`? | Desligar e ligar (count 0 → 1) para o driver reler o XML |
| Como reconhecer a saída do VDD no CCD (caminho do adaptador ou do monitor contendo `MttVDD`)? | Reconhecer pelo nome amigável do monitor |
| A escala via `DisplayConfigSetDeviceInfo` (-4) funciona sem administrador? | O monitor fica com a escala padrão do Windows e o console avisa para ajustar à mão |
| O processo do driver consegue gravar o XML com a permissão definida? | Ajustar a permissão do XML |
| A desinstalação deixa o sistema limpo? | — |

## Testes

Automáticos (xUnit, `dotnet test host`, no CI `windows-latest`, sem driver):

| Alvo | Casos |
|---|---|
| `VddSettingsFile` | Acrescenta um modo; não duplica; preserva comentários e outros elementos; cria o arquivo mínimo; XML inválido gera `VddSettingsException` |
| `ScaleCalculator` | A tabela de exemplos e os limites (72 → 100, 1000 → 200) |
| `VddPipeClient` | Servidor `NamedPipeServerStream` falso com nome único: confere os bytes UTF-16 de cada comando; sem servidor, falha em cerca de 2 s |
| `VirtualMonitorManager` | Com `IVddControl`/`IDisplayTopology` falsos e `FakeTimeProvider`: primeira conexão liga e aplica modo e escala; a contagem de referências e a segunda conexão recebe o mesmo monitor; desligamento 10 s depois do último lease; reconexão em 9 s não desliga; troca de resolução sem outra sessão; escala só na primeira vez; falha do pipe ou da topologia gera lease sem monitor, sem exceção; limpeza (`SETDISPLAYCOUNT 0`) ao iniciar; `Dispose` desliga; pedido absurdo é limitado |
| `ScaledResolutionsStore` | Ida e volta; arquivo corrompido conta como vazio |
| `HostServer` | O `CONFIG` leva a resolução do gerenciador; o lease é liberado ao desconectar, inclusive em erro; sessão sem `HELLO` não pega lease; gerenciador sem monitor mantém o comportamento atual |
| `DriverInstaller` | Conferência do SHA-256 (certo e errado); idempotência com detector falso dizendo "já instalado" |

Manuais, no PC do usuário, ao fim do plano:
1. Na primeira execução, o host pergunta, pede o UAC e a confirmação do Windows, e instala. Em Gerenciador de Dispositivos › Adaptadores de vídeo aparece **Virtual Display Driver**. Em Configurações › Tela, ainda nada.
2. Ao conectar o celular, aparece um monitor novo com a resolução do celular e a escala automática, e o console mostra nome, posição e escala.
3. Uma janela levada para lá com Win+Shift+→ some da vista, porque ainda não há vídeo.
4. Ao desconectar, o monitor some em cerca de 10 s. Reconectando antes disso, ele não pisca.
5. Com Ctrl+C no host, o monitor some na hora.
6. Mudar a escala para 150% à mão, desconectar e reconectar mantém 150%.
7. `uninstall-driver` remove o driver.

## Documentação

- `README.md`: a instalação do driver (o que aparece, o UAC), `--sem-monitor`, `uninstall-driver`, a Parte 2 marcada no roteiro e a verificação manual.
- `docs/guia-do-codigo.md`: seção sobre o `ScreenShare.Display`.
- Este spec: a seção "Resultados do spike".

## Fora do escopo

- Vídeo (Parte 3) e toque (Parte 5).
- Bandeja e escolha de resolução ou qualidade (Parte 6).
- HDR, taxas acima de 60 Hz e mais de um monitor virtual.
- Escolher a posição do monitor: o Windows decide na primeira vez e depois lembra o arranjo que o usuário fizer.
