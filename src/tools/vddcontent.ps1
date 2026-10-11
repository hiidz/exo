# Fills the VDD with static text plus motion, repainting as fast
# as possible, then closes itself after $Seconds. -Static paints the
# text once, with no motion, so DWM presents nothing more (DECISIONS 5.4).
param([int]$Seconds = 150, [switch]$Static)

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Dpi {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
}
"@
# -4 is DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2. Must precede any window.
$ok = [Dpi]::SetProcessDpiAwarenessContext([IntPtr](-4))
Write-Output "per-monitor aware: $ok"

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$vdd = [System.Windows.Forms.Screen]::AllScreens | Where-Object { -not $_.Primary } | Select-Object -First 1
Write-Output "vdd $($vdd.DeviceName) $($vdd.Bounds)"

$form = New-Object System.Windows.Forms.Form
$form.FormBorderStyle = 'None'
$form.StartPosition = 'Manual'
$form.Bounds = $vdd.Bounds
$form.BackColor = [System.Drawing.Color]::White
$form.TopMost = $true
$form.GetType().GetProperty('DoubleBuffered', [Reflection.BindingFlags]'NonPublic,Instance').SetValue($form, $true)

$sizes = 8, 9, 10, 11, 12, 14, 16, 20, 28
$colors = [System.Drawing.Color]::Black, [System.Drawing.Color]::Red, [System.Drawing.Color]::Blue, [System.Drawing.Color]::DarkGreen, [System.Drawing.Color]::Purple
$line = 'The quick brown fox jumps over the lazy dog 0123456789 Il1| O0 rn m {}[]()'
$script:n = 0
$sw = [Diagnostics.Stopwatch]::StartNew()

$form.Add_Paint({
    param($s, $e)
    $g = $e.Graphics
    $y = 20
    foreach ($pt in $sizes) {
        $font = New-Object System.Drawing.Font('Segoe UI', $pt)
        $x = 20
        foreach ($c in $colors) {
            [System.Windows.Forms.TextRenderer]::DrawText($g, "$pt pt $line", $font, (New-Object System.Drawing.Point($x, $y)), $c)
            $y += [int]($font.GetHeight($g) * 1.2)
        }
        $font.Dispose()
    }
    $script:n++
    if ($Static) { return }
    # Motion: a bar sweeping across the bottom, and a frame counter.
    $w = $s.ClientSize.Width
    $bx = [int](($sw.Elapsed.TotalSeconds * 600) % $w)
    $g.FillRectangle([System.Drawing.Brushes]::Orange, $bx, $s.ClientSize.Height - 200, 160, 160)
    $big = New-Object System.Drawing.Font('Consolas', 36)
    [System.Windows.Forms.TextRenderer]::DrawText($g, "frame $($script:n)  t=$($sw.Elapsed.TotalSeconds.ToString('0.000'))", $big, (New-Object System.Drawing.Point(20, ($s.ClientSize.Height - 320))), [System.Drawing.Color]::Black)
    $big.Dispose()
    if ($sw.Elapsed.TotalSeconds -gt $Seconds) { $s.Close() } else { $s.Invalidate() }
})

# With no Invalidate loop, a timer closes the static window.
if ($Static) {
    $timer = New-Object System.Windows.Forms.Timer
    $timer.Interval = $Seconds * 1000
    $timer.Add_Tick({ $form.Close() })
    $timer.Start()
}

[System.Windows.Forms.Application]::Run($form)
Write-Output "painted $($script:n) frames in $($sw.Elapsed.TotalSeconds) s"
