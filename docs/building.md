# Building

- [How it works](#how-it-works)
- [Building from source](#building-from-source)

## How it works

Exo on the PC talks to each phone over USB debugging or Wi-Fi, one
connection per role. The camera phone sends H.264, which Exo decodes
once for its webcam, a media source Windows loads into its camera
service. For the display, Exo's driver hands each image of the virtual
monitor to Exo on the GPU; Exo encodes it to H.264 and sends it to the
phone, which shows it and sends touches back. Over Wi-Fi every
connection is TLS: each side pins the other's certificate at the first
pairing, when both show the code made from both certificates.

The parts, under `src/`:

- **ExoHub:** the PC app (C#). It owns adb, the connections, decoding,
  capture, encoding and touch.
- **ExoCamera:** the webcam (C++), a DLL that only passes the hub's
  decoded frames to Windows.
- **ExoDisplay:** the display driver (C++) for the virtual monitor.
- **ExoPhone:** the Android app (Java).
- **ExoSetup:** the installer and release scripts.

## Building from source

Windows 11 with Visual Studio 2026 Community (Desktop C++, the Windows
Driver Kit, and the x64 Spectre-mitigated libraries; the build scripts
expect Community's default folder), the .NET 10 SDK, Android Studio
with Android SDK Platform-Tools 37.0.1, and Inno Setup 6. Each part:

- **Webcam DLL:** `src\ExoCamera\build.bat`, from PowerShell or cmd.
- **Display driver:** `src\ExoDisplay\build.bat`, signed with the WDK's
  test certificate; `src\ExoDisplay\install.ps1` installs or updates it
  (it elevates itself), `-Uninstall` removes it.
- **PC app:** `dotnet build src/ExoHub/ExoHub.csproj -c Release`. It
  copies adb from the SDK's `platform-tools` and stops if that is not
  37.0.1; pass `-p:AdbSource=<folder>` for a copy elsewhere.
- **Phone app:** in `src/ExoPhone`, with `JAVA_HOME` set to Android
  Studio's `jbr` folder and `ANDROID_HOME` to the Android SDK,
  `gradlew assembleDebug`.
- **A development install** of those builds: `src\ExoSetup\setup.ps1`
  (it elevates itself), `-Uninstall` to remove it. Not alongside an
  installed release, and with Exo not running.
- **A release:** `src\ExoSetup\release.ps1` builds the installer and the
  APK into `dist\`. It needs your own signing certificate and APK
  keystore, as its header describes.
