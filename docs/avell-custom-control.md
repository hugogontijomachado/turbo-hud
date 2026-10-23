# Avell Custom Control: como funciona por dentro

Engenharia reversa feita em 18/10/2026 (versão 5.24.50.3) para o widget trocar o modo de desempenho. Tudo aqui foi observado num Avell Storm 460 (i9-14900HX, RTX 4060); nada foi modificado nos arquivos da Avell. Outros notebooks com o Control Center 3 da Uniwill devem funcionar parecido, mas não foram testados.

## Peças

| Peça | Onde | Papel |
| --- | --- | --- |
| App (UWP) | `C:\Program Files\WindowsApps\ControlCenter3_5.24.50.3_x64__wrbgcf7aesyd8\GamingCenter3_Cross.dll` | Interface. É o "Control Center 3" da Uniwill (fabricante do chassi) com a marca Avell. .NET Native: as strings estão no binário (dá para buscar `OPERATING_`, `Topic` etc.). |
| `GCUBridge` | Serviço Windows, `C:\Program Files\OEM\Avell Custom Control\AiStoneService\GCUBridge.exe` | **Broker MQTT** (MQTTnet) em `0.0.0.0:13688`. Também abre os processos do usuário. .NET sem ofuscação: dá para ler com reflexão. |
| `GCUService` | `...\AiStoneService\MyControlCenter\GCUService.exe`, aberto pelo GCUBridge | Faz o trabalho: fala com o EC/BIOS por WMI/ACPI, aplica os limites de potência e as curvas de ventoinha. .NET **ofuscado**: o corpo de todos os métodos foi trocado por `throw new Exception("Runtime exception")` e é decifrado só em execução. Ler pelos nomes de tipos e constantes (`Define.ServCMD+FAN`, `Define.GCU2_Define+OperatingMode`), não pelo código. |
| Registro | `HKLM\SOFTWARE\OEM\GamingCenter2` | Configuração e recursos suportados (`ItemSupport`). **O modo atual não fica no registro.** |

O app e o serviço nunca se falam direto: tudo passa por mensagens MQTT no broker.

## Conectar no broker

MQTT 3.1.1 com usuário e senha. O GCUBridge só aceita IDs de cliente de uma lista fixa (`MQTTServer.InitialzeClient`); ID desconhecido recebe CONNACK 2. As senhas estão em texto puro no próprio GCUBridge.

| ID do cliente | Usuário / senha | Quem usa |
| --- | --- | --- |
| `MyControlCenter` | fixos no GCUBridge (omitidos aqui) | **GCUService. Não usar.** |
| `UWPClient` (e variantes UWP*) | senha definida em execução (`Service/SetPassword`) | **App da Avell. Não usar.** |
| `OcTool` | `OcToolUser` / `OcToolPwd123` | **Widget MonitorJogo** (conexão permanente) |
| `OcScanner` | `OcScannerUser` / `OcScannerPwd123` | Livre: [ferramentas/mqtt-avell.ps1](../ferramentas/mqtt-avell.ps1) usa por padrão |
| `OpenCL` | `OpenCLUser` / `OpenCLPwd123` | Livre |

O broker derruba a conexão antiga quando outra entra com o mesmo ID. Usar o ID de outro cliente desconecta esse cliente: com `MyControlCenter` o serviço da Avell perde a conexão; com `OcTool` o widget perde (ele reconecta em 10 s e as duas conexões ficam se derrubando).

## Tópicos

Mensagens em JSON. As principais:

| Tópico | Sentido | Conteúdo |
| --- | --- | --- |
| `Fan/Control` | app → serviço | `{"Action":"OPERATING_TURBO_MODE","ProfileIndex":"0"}` troca o modo. Também `OPERATING_OFFICE_MODE`, `OPERATING_GAMING_MODE`, `OPERATING_CUSTOM_MODE`. `{"Action":"GETSTATUS"}` pede o status. |
| `Fan/Status` | serviço → todos | Resposta ao GETSTATUS e às trocas: `OperatingMode`, limites `CPU_PL1/PL2/PL4`, offsets da GPU, tabela de ventoinha, etc. (~3,5 KB). Logo depois de uma troca vem com `"OperatingMode":null`, e o painel da Avell não se atualiza com isso; um GETSTATUS ~2,5 s depois publica o modo certo e o painel acompanha (o widget faz isso). |
| `Tray/Status` | serviço → todos | `{"OperatingMode":"2","FanBoostEnable":"0"}` logo após cada troca. |
| `Customize/SupportInfo`, `Keyboard/Status`, `HidLightbar*/Status` | serviço → todos | Retidos: chegam assim que alguém assina. |
| `Keyboard/Ctrl`, `MyRgbLightbar/Control`, `System/Control`, `Setting/Control`, `BatteryProtection/Control`, `LCHWOC/Control` | app → serviço | Luzes, sistema, bateria, refrigeração líquida (não existe no Storm 460). Formatos ainda não estudados. |

Ao escolher Turbo, o app também manda `LCHWOC/Control {"IsNormalRun":2}` (refrigeração líquida externa); o widget não manda e a troca funciona igual.

## Modos

`OperatingMode`: 0 Escritório, 1 Gaming, 2 Turbo, 3 Personalizado, 4 Benchmark.

| Modo | CPU PL1 / PL2 / PL4 | GPU | Medido |
| --- | --- | --- | --- |
| Escritório (0) | 45 / 45 / 100 W | — | 18/10/2026 |
| Turbo (2) | 140 / 165 / 145 W | +105 MHz núcleo, +500 MHz memória | 18/10/2026 |
| Gaming (1) | não medido | — | — |

O modo da Avell e o plano de energia do Windows são independentes: o Turbo não troca o plano. Com um plano de energia que desabilita o boost do processador, a CPU não sobe de frequência mesmo no Turbo.

## Como repetir a investigação

- Ouvir tudo enquanto se mexe no app: `powershell -File ferramentas\mqtt-avell.ps1 -Segundos 120 -Saida captura.txt` (só lê). O script grava o arquivo só no fim.
- Ler o modo e os limites atuais: `powershell -File ferramentas\mqtt-avell.ps1 -Segundos 4 -Topico Fan/Status -Acao GETSTATUS`.
- Mandar outro comando: `-Acao OPERATING_GAMING_MODE` monta o JSON e manda para `Fan/Control`. Para JSON arbitrário, `-Publicar` (chamando com `&` de dentro do PowerShell; via `powershell -File` as aspas internas somem) e `-TopicoPublicar`. Comandos que mudam o hardware precisam da aprovação do usuário antes.
- Ler o GCUBridge: `[Reflection.Assembly]::ReflectionOnlyLoadFrom(...)` e varrer o IL atrás de `ldstr` (opcode `0x72`) com `ManifestModule.ResolveString`. No GCUService isso só devolve "Runtime exception".
- Strings do app: ler `GamingCenter3_Cross.dll` como Latin-1 e filtrar com regex.
