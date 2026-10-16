# Builds a release into dist\: the PC installer,
# Exo-<version>-setup.exe, and the phone app, Exo-<version>.apk. The
# version is the phone app's versionName in src/ExoPhone/app/build.gradle.
#
#   release.ps1           both
#   release.ps1 -PcOnly   the installer only, no keystore needed
#
# Needs, made once by the author and kept outside the repo, so no password
# passes through it:
#   - the release certificate, CN=Exo Release, in CurrentUser\My; it signs
#     the display driver, and the installer trusts it on the user's PC
#   - the APK keystore, C:\Users\you\keys\exo-release.jks, alias exo,
#     or EXO_KEYSTORE; its password from EXO_KEYSTORE_PASSWORD, else asked
#   - Inno Setup 6
# Rebuilds the display driver signed with the release certificate; run
# src/ExoDisplay/build.bat again before a dev install.ps1.
param([switch]$PcOnly)
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path "$PSScriptRoot\..\..").Path
$dist = "$root\dist"
$stage = "$dist\stage"
$version = (Select-String "versionName = '([^']+)'" "$root\src\ExoPhone\app\build.gradle").Matches[0].Groups[1].Value
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
function Step($s) { Write-Output "--- $s" }
function Check($what) { if ($LASTEXITCODE -ne 0) { throw "$what failed, exit $LASTEXITCODE" } }

if (-not $iscc) { throw 'Inno Setup 6 is not installed' }
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object Subject -eq 'CN=Exo Release' | Select-Object -First 1
if (-not $cert) { throw 'no CN=Exo Release certificate in CurrentUser\My' }
Step "Exo $version, driver signed by $($cert.Thumbprint)"

# -PcOnly keeps an APK already built.
Remove-Item $stage, "$dist\*.exe" -Recurse -Force -ErrorAction SilentlyContinue
if (-not $PcOnly) { Remove-Item "$dist\*.apk" -ErrorAction SilentlyContinue }
New-Item -ItemType Directory "$stage\driver", "$stage\licenses" | Out-Null

Step 'camera DLL'
cmd /c "$root\src\ExoCamera\build.bat"; Check 'ExoCamera build'
Copy-Item "$root\src\ExoCamera\out\exocam.dll" $stage

Step 'display driver'
# Test signing, naming the release certificate: the WDK's ProductionSign
# demands a cross-certificate, which a self-made root has none of.
$env:TestCertificate = "CN=Exo Release | $($cert.Thumbprint)"
try { cmd /c "$root\src\ExoDisplay\build.bat"; Check 'ExoDisplay build' }
finally { Remove-Item env:TestCertificate }
$pkg = "$root\src\ExoDisplay\x64\Release\ExoDisplay"
$signer = (Get-AuthenticodeSignature "$pkg\exodisplay.cat").SignerCertificate
if ($signer.Thumbprint -ne $cert.Thumbprint) { throw "the catalog is signed by $($signer.Subject), not the release certificate" }
Copy-Item "$pkg\ExoDisplay.dll", "$pkg\ExoDisplay.inf", "$pkg\exodisplay.cat" "$stage\driver"
Copy-Item "$root\src\ExoDisplay\x64\Release\exodevice.exe" $stage
Export-Certificate -Cert $cert -FilePath "$stage\exo-release.cer" | Out-Null

Step 'hub'
# Self-contained, one file, adb loose beside it (ExoHub.csproj).
& "C:\Program Files\dotnet\dotnet.exe" publish "$root\src\ExoHub\ExoHub.csproj" -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -o $stage -nodeReuse:false; Check 'hub publish'
if (-not (Test-Path "$stage\adb\adb.exe")) { throw 'adb is not beside the published hub' }

Step 'licences'
Copy-Item "$root\LICENSE" "$stage\licenses\LICENSE-Exo.txt"
Copy-Item "$root\src\ExoDisplay\LICENSE" "$stage\licenses\LICENSE-ExoDisplay.txt"
Copy-Item "$env:LOCALAPPDATA\Android\Sdk\platform-tools\NOTICE.txt" "$stage\licenses\NOTICE-adb.txt"
# Inside the self-contained ExoHub.exe: Vortice, and the .NET runtime.
Copy-Item "$PSScriptRoot\licenses\*" "$stage\licenses"
Copy-Item "C:\Program Files\dotnet\LICENSE.txt" "$stage\licenses\LICENSE-dotnet.txt"
Copy-Item "C:\Program Files\dotnet\ThirdPartyNotices.txt" "$stage\licenses\ThirdPartyNotices-dotnet.txt"

Step 'installer'
& $iscc /Qp "/DAppVersion=$version" "/DStage=$stage" "/DOut=$dist" "/DCertThumb=$($cert.Thumbprint)" "$PSScriptRoot\Exo.iss"; Check 'ISCC'

if (-not $PcOnly) {
    Step 'phone app'
    if (-not $env:EXO_KEYSTORE) { $env:EXO_KEYSTORE = 'C:\Users\you\keys\exo-release.jks' }
    if (-not (Test-Path $env:EXO_KEYSTORE)) { throw "no keystore at $env:EXO_KEYSTORE" }
    if (-not $env:EXO_KEYSTORE_PASSWORD) {
        $env:EXO_KEYSTORE_PASSWORD = [Net.NetworkCredential]::new('', (Read-Host 'Keystore password' -AsSecureString)).Password
    }
    $env:JAVA_HOME = 'C:\Program Files\Android\Android Studio\jbr'
    $env:ANDROID_HOME = "$env:LOCALAPPDATA\Android\Sdk"
    Push-Location "$root\src\ExoPhone"
    try { & .\gradlew.bat assembleRelease; Check 'assembleRelease' } finally { Pop-Location }
    $apk = "$dist\Exo-$version.apk"
    Copy-Item "$root\src\ExoPhone\app\build\outputs\apk\release\app-release.apk" $apk
    $apksigner = Get-ChildItem "$env:ANDROID_HOME\build-tools\*\apksigner.bat" | Select-Object -Last 1 -ExpandProperty FullName
    & $apksigner verify --print-certs $apk; Check 'apksigner verify'
}

Step 'done'
Get-ChildItem $dist -File | ForEach-Object { '{0}  {1:N1} MB  SHA-256 {2}' -f $_.Name, ($_.Length / 1MB), (Get-FileHash $_.FullName).Hash }
