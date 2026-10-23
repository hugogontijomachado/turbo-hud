# Monitor de Jogo

Overlay de desempenho para Windows (um arquivo C#) com integração ao Avell Custom Control. Uso e instalação: [README.md](README.md). Integração com a Avell: [docs/avell-custom-control.md](docs/avell-custom-control.md).

O dono fala português; código, comentários, textos da tela e commits em português. Repositório público no GitHub: só publicar (push) quando o dono pedir.

## Código

- C# 5 compilado pelo `csc.exe` do .NET Framework 4.x (`instalar.ps1`): sem `$"..."`, `?.`, `??=`, `=>` em membros, `nameof`, tuplas ou `out var`. Só as referências padrão + `System.Windows.Forms` e `System.Drawing`; nada de NuGet ou DLL extra.
- Um arquivo só. Classes por responsabilidade: `Overlay` (janela e desenho), `Sensors` (medições, plano de energia), `AvellControl` (modo da Avell via MQTT), `FrameStats` (PresentMon), `Nvml`, `Native` (P/Invoke).
- Comentários curtos, explicando o porquê.
- Consumo é requisito: ~0,03% de CPU visível; escondido, nada de timer ou polling (só threads bloqueadas em espera). Medir CPU e RAM antes e depois de mudanças que adicionem trabalho periódico.
- A lista de atalhos aparece em quatro lugares, que precisam ficar iguais: comentário no topo do `.cs`, `HelpCard` no `.cs`, descrição do atalho em `instalar.ps1` e o README.
- No broker MQTT da Avell (porta 13688), cada ID de cliente só pode ter uma conexão: o widget usa `OcTool`; ferramentas usam `OcScanner` ou `OpenCL`. Nunca `MyControlCenter` (serviço da Avell) nem `UWP*` (app da Avell).

## Testar

1. `powershell -ExecutionPolicy Bypass -File instalar.ps1` (fecha o widget aberto, compila, copia). Erro de compilação aparece na saída do `csc`.
2. Abrir: `Start-Process "$env:LOCALAPPDATA\MonitorJogo\MonitorJogo.exe"` e conferir o processo `MonitorJogo`.
3. Atalhos: `ferramentas\atalho-widget.ps1 -Tecla T`. O T troca o modo da Avell e o plano de verdade: pedir aprovação ao dono, testar indo e voltando, e deixar como estava.
4. Conexão com a Avell: `Get-NetTCPConnection -RemotePort 13688 -State Established` deve listar `MonitorJogo`. Para ver o que o widget manda: `ferramentas\mqtt-avell.ps1 -Topico Fan/Control` (usa outro ID, não derruba o widget).

## PowerShell 5.1

- Sem `&&`, `||`, `?.`, `??`.
- `powershell -File script.ps1 -Param '{"a":"b"}'` tira as aspas de dentro do argumento: chamar com `&` de dentro do PowerShell ou montar o JSON no script.
- Scripts `.ps1` com acentos em strings precisam de UTF-8 com BOM, senão o PowerShell 5.1 lê como ANSI.
