param([ValidateSet('M', 'N', 'G', 'T')][string]$Tecla = 'T', [int]$Vezes = 1)
# Simula Ctrl+Alt+<Tecla> para testar os atalhos do widget sem teclado (o RegisterHotKey aceita entrada sintética).
# Atenção: T troca o modo da Avell e o plano de energia de verdade.
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class AtalhoWidget {
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    public static void CtrlAlt(byte vk) {
        keybd_event(0x11, 0, 0, UIntPtr.Zero); keybd_event(0x12, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, 0, UIntPtr.Zero); keybd_event(vk, 0, 2, UIntPtr.Zero);
        keybd_event(0x12, 0, 2, UIntPtr.Zero); keybd_event(0x11, 0, 2, UIntPtr.Zero);
    }
}
'@
for ($i = 0; $i -lt $Vezes; $i++) {
    [AtalhoWidget]::CtrlAlt([byte][char]$Tecla)
    if ($i -lt $Vezes - 1) { Start-Sleep -Seconds 3 }
}
