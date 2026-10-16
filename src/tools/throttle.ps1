# Caps the hub's outbound traffic, the display stream, for a run on a
# throttled link. A Windows QoS policy on ExoHub.exe in the ActiveStore,
# so a reboot also removes it. The hub stays unelevated; only this elevates.
#
#   throttle.ps1 -Mbps 8    cap at 8 Mbps, or change the cap
#   throttle.ps1 -Off       remove the cap
#   throttle.ps1            show the cap, if any
#
# Limits: it caps only what the hub sends, so the webcam stream (phone to
# PC) is untouched. A fixed cap is not a link that varies by itself; change
# it during a run to imitate one.
param([double]$Mbps, [switch]$Off)

$name = 'ExoThrottle'

if (-not $Mbps -and -not $Off) {
    Get-NetQosPolicy -Name $name -PolicyStore ActiveStore -ErrorAction SilentlyContinue |
        Select-Object Name, AppPathName, ThrottleRate
    exit 0
}

$me = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $me.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $pass = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($Off) { $pass += '-Off' } else { $pass += @('-Mbps', $Mbps) }
    $p = Start-Process powershell -Verb RunAs -ArgumentList $pass -Wait -PassThru
    Get-Content "$env:TEMP\exo-throttle.log" -ErrorAction SilentlyContinue
    exit $p.ExitCode
}

# The elevated window closes when done, so its output also goes to a file,
# which the unelevated caller prints.
Start-Transcript "$env:TEMP\exo-throttle.log" | Out-Null
Remove-NetQosPolicy -Name $name -PolicyStore ActiveStore -Confirm:$false -ErrorAction SilentlyContinue
if ($Off) {
    Write-Output 'Throttle off.'
} else {
    New-NetQosPolicy -Name $name -AppPathNameMatchCondition 'ExoHub.exe' -PolicyStore ActiveStore `
        -ThrottleRateActionBitsPerSecond ([uint64]($Mbps * 1000000)) |
        Select-Object Name, AppPathName, ThrottleRate | Format-List
}
Stop-Transcript | Out-Null
