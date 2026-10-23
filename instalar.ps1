# Compila o Monitor de Jogo, instala em %LOCALAPPDATA%\MonitorJogo e cria o atalho na Área de Trabalho.
# -PresentMon: caminho do PresentMon-x.y.z-x64.exe (Intel) para FPS/latência; é copiado como PresentMon.exe.
# -Inicializacao: também inicia o widget escondido com o Windows, para os atalhos funcionarem sempre.
param([string]$PresentMon, [switch]$Inicializacao)
$ErrorActionPreference = 'Stop'
$src = Join-Path $PSScriptRoot 'MonitorJogo.cs'
$dir = Join-Path $env:LOCALAPPDATA 'MonitorJogo'
$exe = Join-Path $dir 'MonitorJogo.exe'
$csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

New-Item -ItemType Directory -Force $dir | Out-Null

# Fecha o widget se estiver aberto, para poder substituir o .exe
if (Get-Process MonitorJogo -ErrorAction SilentlyContinue) {
    Start-Process $exe -ArgumentList '--sair'
    $p = Get-Process MonitorJogo -ErrorAction SilentlyContinue
    if ($p -and -not $p.WaitForExit(5000)) { $p | Stop-Process -Force }
    Start-Sleep -Milliseconds 300
}

& $csc /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll "/out:$exe" $src
if ($LASTEXITCODE -ne 0) { throw "Falha ao compilar (codigo $LASTEXITCODE)" }
Copy-Item $src $dir -Force

if ($PresentMon) {
    $sig = Get-AuthenticodeSignature $PresentMon
    if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'CN=Intel Corporation') { throw "PresentMon sem assinatura valida da Intel" }
    Copy-Item $PresentMon (Join-Path $dir 'PresentMon.exe') -Force
}
if (-not (Test-Path (Join-Path $dir 'PresentMon.exe'))) { "Aviso: PresentMon.exe ausente; o widget mostra FPS como indisponivel." }

$ws = New-Object -ComObject WScript.Shell
# Sem tecla de atalho no .lnk: o Ctrl+Alt+M é do próprio widget (funciona dentro de jogos).
$lnk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Monitor de Jogo.lnk'
$s = $ws.CreateShortcut($lnk)
$s.TargetPath = $exe
$s.WorkingDirectory = $dir
$s.Hotkey = ''
$s.Description = 'Monitor de Jogo - Ctrl+Alt+M mostra/esconde, N troca de monitor, G graficos, T turbo'
$s.IconLocation = "$env:SystemRoot\System32\Taskmgr.exe,0"
$s.Save()
"Instalado: $exe"
"Atalho:    $lnk"

$startup = Join-Path ([Environment]::GetFolderPath('Startup')) 'Monitor de Jogo (oculto).lnk'
if ($Inicializacao) {
    $s = $ws.CreateShortcut($startup)
    $s.TargetPath = $exe
    $s.Arguments = '--oculto'
    $s.WorkingDirectory = $dir
    $s.Description = 'Monitor de Jogo escondido; Ctrl+Alt+M mostra'
    $s.Save()
    "Inicializacao: $startup"
}
