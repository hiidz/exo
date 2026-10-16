# Full-screen random noise on the EXO monitor, for a frame the encoder
# cannot compress, to drive a FRAME past the wire's 4MiB limit. With the
# desktop still for 25s first, the TB375FC at 2944x1840@30 over USB gave 5.4 to 6.3MB
# P-frames in every run (2026-10-16). Run it with the display phone
# connected; the EXO monitor is taken to be the one non-primary screen.
#
#   noise.ps1                 one noise image for 5s
#   noise.ps1 -Seconds 8 -EveryMs 100   a new image every 100ms for 8s
#
# This process is not DPI aware, so at 200% scale the noise is 2x2 blocks;
# that was enough.
param([int]$Seconds = 5, [int]$EveryMs = 0)
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$screen = [System.Windows.Forms.Screen]::AllScreens | Where-Object { -not $_.Primary } | Select-Object -First 1
if (-not $screen) { "no second screen"; exit 1 }
"screen $($screen.DeviceName) $($screen.Bounds)"
$b = $screen.Bounds
$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height, ([System.Drawing.Imaging.PixelFormat]::Format32bppRgb)
$rng = New-Object System.Random
$bytes = New-Object byte[] ($b.Width * $b.Height * 4)
function Fill {
    $rng.NextBytes($bytes)
    $d = $bmp.LockBits((New-Object System.Drawing.Rectangle 0, 0, $b.Width, $b.Height), 'WriteOnly', $bmp.PixelFormat)
    [System.Runtime.InteropServices.Marshal]::Copy($bytes, 0, $d.Scan0, $bytes.Length)
    $bmp.UnlockBits($d)
}
Fill
$f = New-Object System.Windows.Forms.Form
$f.FormBorderStyle = 'None'; $f.StartPosition = 'Manual'; $f.Bounds = $b; $f.TopMost = $true
$box = New-Object System.Windows.Forms.PictureBox
$box.Dock = 'Fill'; $box.Image = $bmp; $f.Controls.Add($box)
$end = New-Object System.Windows.Forms.Timer; $end.Interval = $Seconds * 1000; $end.Add_Tick({ $f.Close() }); $end.Start()
if ($EveryMs -gt 0) {
    $t = New-Object System.Windows.Forms.Timer; $t.Interval = $EveryMs; $t.Add_Tick({ Fill; $box.Invalidate() }); $t.Start()
}
[System.Windows.Forms.Application]::Run($f)
"done"
