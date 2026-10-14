# ExoSetup (TECHNICAL_DESIGN 5, PROJECT_SPEC SC-13). Installs Exo from this
# repository's build outputs, in place, or with -Uninstall reverses it.
#
#   setup.ps1 -Camera <serial> -Display <serial>    install
#   setup.ps1 -Uninstall                            uninstall
#
# Either serial may be left out to leave that role empty, and one phone may
# hold both. Build first: src/ExoCamera/build.bat, the hub in Release, and
# the phone app (app-debug.apk). Phones to get the app must be plugged in
# and unlocked, and no hub may be running, since the install uses the hub's
# pinned adb.exe and starts its own short-lived server.
#
# Install does four things, and uninstall undoes each:
#   1. the DLL's log folder, with a DACL LOCAL SERVICE can write to (3.4)
#   2. regsvr32 on exocam.dll, HKLM, so the Frame Server can load it
#   3. a desktop shortcut that starts the hub with the chosen roles
#   4. the phone app on every attached phone
#
# Not done here, by hand once: the display driver, src/ExoDisplay's
# build.bat and install.ps1. Printed at the end of an install.
param([string]$Camera, [string]$Display, [switch]$Uninstall)

$root = (Resolve-Path "$PSScriptRoot\..\..").Path
$dll = "$root\src\ExoCamera\out\exocam.dll"
$hubDir = "$root\src\ExoHub\bin\Release\net10.0-windows"
$hub = "$hubDir\ExoHub.exe"
$adb = "$hubDir\adb\adb.exe"
$apk = "$root\src\ExoPhone\app\build\outputs\apk\debug\app-debug.apk"
$logDir = 'C:\Windows\Temp\Exo\logs'
$shortcut = [Environment]::GetFolderPath('Desktop') + '\Exo.lnk'

# regsvr32 and the log folder's DACL need administrator. Rerun elevated,
# with the same arguments, and wait, so this window shows the result.
$me = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $me.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $pass = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($Camera) { $pass += '-Camera', $Camera }
    if ($Display) { $pass += '-Display', $Display }
    if ($Uninstall) { $pass += '-Uninstall' }
    $p = Start-Process powershell -Verb RunAs -ArgumentList $pass -Wait -PassThru
    exit $p.ExitCode
}

# The elevated window closes when done, so its output also goes to a file.
Start-Transcript "$env:TEMP\exo-setup.log" | Out-Null
function Step($s) { Write-Output "--- $s" }

# Every attached phone, through a server of our own that is stopped by PID
# afterwards, never kill-server (CLAUDE.md). Refused while another adb runs:
# a hub would refuse the server this leaves, and this would reuse its one.
function OnEachPhone([scriptblock]$action) {
    if (Get-Process adb, ExoHub -ErrorAction SilentlyContinue) {
        Write-Output 'SKIPPED: an adb or ExoHub is running. Close it and run this again.'
        return
    }
    & $adb start-server 2>&1 | Out-Null
    try {
        $serials = & $adb devices | Select-String '^(\S+)\s+device$' | ForEach-Object { $_.Matches[0].Groups[1].Value }
        if (-not $serials) { Write-Output 'no phone attached and authorised' }
        foreach ($s in $serials) { & $action $s }
    }
    finally {
        Get-Process adb -ErrorAction SilentlyContinue | Where-Object Path -eq $adb | Stop-Process -Force
    }
}

if ($Uninstall) {
    Step 'phone app'
    OnEachPhone { param($s) "$s uninstall: $(& $adb -s $s uninstall exo.phone 2>&1)" }
    Step 'desktop shortcut'
    Remove-Item $shortcut -ErrorAction SilentlyContinue
    Step 'DLL registration'
    # The Frame Server keeps the DLL and its log file open after the last
    # camera goes. It starts again by itself when an app next wants one.
    Stop-Service FrameServer -Force -ErrorAction SilentlyContinue
    $p = Start-Process regsvr32 -ArgumentList '/u', '/s', "`"$dll`"" -Wait -PassThru
    "regsvr32 /u exit $($p.ExitCode)"
    Step 'log folder'
    Remove-Item 'C:\Windows\Temp\Exo' -Recurse -Force -ErrorAction SilentlyContinue
    "removed: $(-not (Test-Path 'C:\Windows\Temp\Exo'))"
    exit 0
}

foreach ($f in $dll, $hub, $adb, $apk) {
    if (-not (Test-Path $f)) { Write-Output "MISSING $f, build it first"; exit 1 }
}

Step 'log folder'
New-Item -ItemType Directory -Force $logDir | Out-Null
# Protected, inherited by files: Administrators, SYSTEM, LOCAL SERVICE and
# Users, full control. LOCAL SERVICE is the Frame Server's account.
$acl = Get-Acl $logDir
$acl.SetSecurityDescriptorSddlForm('D:PAI(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)(A;OICI;FA;;;LS)(A;OICI;FA;;;BU)')
Set-Acl $logDir $acl
(Get-Acl $logDir).Sddl

Step 'DLL registration'
$p = Start-Process regsvr32 -ArgumentList '/s', "`"$dll`"" -Wait -PassThru
"regsvr32 exit $($p.ExitCode)"
if ($p.ExitCode -ne 0) { exit 1 }

Step 'desktop shortcut'
$roles = @()
if ($Camera) { $roles += '--camera', $Camera }
if ($Display) { $roles += '--display', $Display }
$lnk = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut)
$lnk.TargetPath = $hub
$lnk.Arguments = $roles -join ' '
$lnk.WorkingDirectory = $hubDir
$lnk.Save()
"$shortcut -> ExoHub.exe $($lnk.Arguments)"

Step 'phone app'
OnEachPhone { param($s) "$s install: $(& $adb -s $s install -r $apk 2>&1 | Select-Object -Last 1)" }

Step 'by hand, once: the display driver'
@'
src/ExoDisplay/build.bat, then src/ExoDisplay/install.ps1. Its monitor
appears only while the display phone is connected.
BLACK SCREEN: Windows may put everything on the virtual display. Blind
recovery: Win+P, arrow, Enter for "PC screen only". Win+Ctrl+Shift+B
resets the graphics stack.
'@
