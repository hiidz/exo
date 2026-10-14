# Installs the built ExoDisplay driver on this PC for development, or with
# -Uninstall removes it. Build first with build.bat. Run again after each
# build to update the driver in place.
#
#   install.ps1              install, or update
#   install.ps1 -Uninstall   uninstall
#
# Install does two things, and uninstall undoes each:
#   1. trusts the certificate the build signed with, in LocalMachine Root and
#      TrustedPublisher. It is the WDK's per-user test certificate; a
#      user-mode driver needs no test-signing mode, which Secure Boot forbids.
#   2. creates the root device Root\ExoDisplay with devcon, or updates its
#      driver. The adapter shows no monitor until the hub plugs one.
param([switch]$Uninstall)

$pkg = "$PSScriptRoot\x64\Release\ExoDisplay"
$inf = "$pkg\ExoDisplay.inf"
$cat = "$pkg\exodisplay.cat"
$hwid = 'Root\ExoDisplay'
$devcon = Get-ChildItem "$PSScriptRoot\packages\Microsoft.Windows.WDK.x64.*\c\tools\*\x64\devcon.exe" | Select-Object -First 1 -ExpandProperty FullName

# A running hub holds the driver open, and a driver held open cannot be
# restarted to load the new build (SudoVDA's README).
if (Get-Process ExoHub -ErrorAction SilentlyContinue) {
    Write-Output 'FAILED: ExoHub is running. Stop it, then run this again.'
    exit 1
}

$me = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $me.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $pass = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($Uninstall) { $pass += '-Uninstall' }
    $p = Start-Process powershell -Verb RunAs -ArgumentList $pass -Wait -PassThru
    Get-Content "$env:TEMP\exo-display-install.log" -ErrorAction SilentlyContinue
    exit $p.ExitCode
}

# The elevated window closes when done, so its output also goes to a file,
# which the unelevated caller prints.
Start-Transcript "$env:TEMP\exo-display-install.log" | Out-Null
function Step($s) { Write-Output "--- $s" }

function Device { Get-PnpDevice -Class Display -ErrorAction SilentlyContinue | Where-Object { $_.HardwareID -contains $hwid } }

function Stores([scriptblock]$action) {
    foreach ($name in 'Root', 'TrustedPublisher') {
        $store = New-Object Security.Cryptography.X509Certificates.X509Store($name, 'LocalMachine')
        $store.Open('ReadWrite')
        try { & $action $store } finally { $store.Close() }
    }
}

$signer = if (Test-Path $cat) { (Get-AuthenticodeSignature $cat).SignerCertificate }

if ($Uninstall) {
    Step 'device'
    if (Device) { & $devcon remove $hwid } else { Write-Output 'none' }

    Step 'driver package'
    $oem = pnputil /enum-drivers | Out-String -Stream | Select-String -Pattern 'Published Name:\s+(oem\d+\.inf)' -Context 0, 1 |
        Where-Object { $_.Context.PostContext[0] -match 'exodisplay\.inf' } | ForEach-Object { $_.Matches[0].Groups[1].Value }
    if ($oem) { foreach ($o in $oem) { pnputil /delete-driver $o /uninstall /force } } else { Write-Output 'none' }

    Step 'certificate'
    if ($signer) {
        Stores { param($s) $s.Remove($signer); Write-Output "removed $($signer.Thumbprint) from $($s.Name)" }
    } else {
        Write-Output "no build at $pkg, so the certificate to remove is unknown; left in place"
    }
    Stop-Transcript | Out-Null
    exit 0
}

if (-not (Test-Path $inf) -or -not $signer) {
    Write-Output "FAILED: no signed build at $pkg. Run build.bat first."
    Stop-Transcript | Out-Null
    exit 1
}

Step "certificate $($signer.Subject) $($signer.Thumbprint)"
# A root this PC trusts must be good for code signing only, or anything
# holding its key could mint TLS certificates the PC would believe.
$eku = @($signer.EnhancedKeyUsageList | ForEach-Object { $_.ObjectId })
if ($eku.Count -ne 1 -or $eku[0] -ne '1.3.6.1.5.5.7.3.3') {
    Write-Output "FAILED: the certificate's usage is '$($eku -join ', ')', not Code Signing alone. Not trusted."
    Stop-Transcript | Out-Null
    exit 1
}
Stores { param($s) $s.Add($signer); Write-Output "trusted in $($s.Name)" }

if (Device) {
    Step 'update driver'
    & $devcon update $inf $hwid
} else {
    Step 'create device'
    & $devcon install $inf $hwid
}
$code = $LASTEXITCODE

Step 'status'
Device | Format-Table Status, Problem, InstanceId, FriendlyName -AutoSize | Out-String -Width 200
Stop-Transcript | Out-Null
exit $code
