param(
    [int]$Segundos = 20,
    [string]$Saida = "",
    [string]$Topico = '#',
    [string]$Acao = "",
    [string]$Publicar = "",
    [string]$TopicoPublicar = 'Fan/Control',
    [string]$Id = 'OcScanner', [string]$Usuario = 'OcScannerUser', [string]$Senha = 'OcScannerPwd123'
)
# Conecta no broker MQTT local do Avell Custom Control (GCUBridge, porta 13688), assina $Topico e mostra
# as mensagens por $Segundos. Com -Acao (ex.: GETSTATUS, OPERATING_TURBO_MODE), manda {"Action":...} para
# $TopicoPublicar logo depois de assinar; -Publicar manda um JSON qualquer. Chamado via "powershell -File",
# o PowerShell 5.1 tira as aspas de dentro do -Publicar: nesse caso, prefira -Acao.
# Não usar -Id OcTool (é do widget) nem MyControlCenter/UWPClient (serviço e app da Avell): o broker derruba
# a conexão antiga do mesmo ID. Detalhes em docs/avell-custom-control.md.
if ($Acao) {
    $Publicar = '{"Action":"' + $Acao + '"' + $(if ($Acao -like 'OPERATING_*') { ',"ProfileIndex":"0"' }) + '}'
}
$c = New-Object Net.Sockets.TcpClient('127.0.0.1', 13688)
$s = $c.GetStream()
$s.ReadTimeout = 500

function Enc([string]$t) { $b = [Text.Encoding]::UTF8.GetBytes($t); ,([byte[]](@([byte]($b.Length -shr 8), [byte]($b.Length -band 255)) + $b)) }
function Pkt([byte]$h, [byte[]]$body) {
    $len = $body.Length; $rl = @()
    do { $d = $len % 128; $len = [math]::Floor($len / 128); if ($len -gt 0) { $d = $d -bor 128 }; $rl += [byte]$d } while ($len -gt 0)
    ,([byte[]](@($h) + $rl + $body))
}

# CONNECT (MQTT 3.1.1, usuário + senha + sessão limpa, keep-alive 60 s) e SUBSCRIBE
$connect = Pkt 0x10 ([byte[]]((Enc 'MQTT') + @(4, 0xC2, 0, 60) + (Enc $Id) + (Enc $Usuario) + (Enc $Senha)))
$s.Write($connect, 0, $connect.Length)
$sub = Pkt 0x82 ([byte[]](@(0, 1) + (Enc $Topico) + @(0)))
$s.Write($sub, 0, $sub.Length)
if ($Publicar) {
    $pub = Pkt 0x30 ([byte[]]((Enc $TopicoPublicar) + [Text.Encoding]::UTF8.GetBytes($Publicar)))
    $s.Write($pub, 0, $pub.Length)
}

$buf = New-Object System.Collections.Generic.List[byte]
$tmp = New-Object byte[] 65536
$fim = (Get-Date).AddSeconds($Segundos)
$out = @()
while ((Get-Date) -lt $fim) {
    try { $n = $s.Read($tmp, 0, $tmp.Length); if ($n -le 0) { 'conexão fechada'; break }; $buf.AddRange([byte[]]$tmp[0..($n - 1)]) } catch { }
    while ($buf.Count -ge 2) {
        $mult = 1; $len = 0; $i = 1
        do { $d = $buf[$i]; $len += ($d -band 127) * $mult; $mult *= 128; $i++ } while (($d -band 128) -and $i -lt $buf.Count)
        if ($buf.Count -lt $i + $len) { break }
        $h = $buf[0]; $body = $buf.GetRange($i, $len).ToArray(); $buf.RemoveRange(0, $i + $len)
        $type = $h -shr 4
        if ($type -eq 3) {
            $tl = $body[0] * 256 + $body[1]; $topic = [Text.Encoding]::UTF8.GetString($body, 2, $tl)
            $p = 2 + $tl; if ((($h -shr 1) -band 3) -gt 0) { $p += 2 }
            $payload = if ($p -lt $body.Length) { [Text.Encoding]::UTF8.GetString($body, $p, $body.Length - $p) } else { '' }
            $line = '{0:HH:mm:ss.fff} [{1}] {2}' -f (Get-Date), $topic, $payload
            $line; $out += $line
        } elseif ($type -eq 2) { "CONNACK rc=$($body[1])" + $(if ($body[1] -eq 2) { ' (ID recusado)' } elseif ($body[1] -ne 0) { ' (recusado)' }) }
        elseif ($type -eq 9) { "SUBACK $([BitConverter]::ToString($body))" }
    }
}
$c.Close()
if ($Saida) { $out | Out-File -Encoding utf8 $Saida }
