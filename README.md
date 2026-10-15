# Exo

Turns an Android phone into a webcam and a second monitor with touch for a
Windows 11 PC, by USB or on the same Wi-Fi. One phone can be both, or two
phones one role each.

## What you get

- **A webcam called Exo** that any Windows camera app can pick. The
  picture comes from a phone's camera, upright, at 1920x1080, 1280x720
  or 640x360, 30fps, or 60 where the phone's camera offers it.
- **A second monitor** on a phone or tablet, at the panel's own
  resolution or a lower one set on the phone, landscape or portrait, 30
  or 60 fps, with touch acting as touch on that part of the desktop.
  About a tenth of a second behind the PC.

## What you need

- A PC with **Windows 11**, 64-bit. Windows 10 is not supported.
- An Android phone or tablet with **Android 10** or later.
- A USB cable, or the phone and the PC on the same Wi-Fi network.

## Install

Download both files from the latest release on this project's
**Releases** page: `Exo-<version>-setup.exe` for the PC and
`Exo-<version>.apk` for the phone.

### On the PC

1. Run `Exo-<version>-setup.exe`. Exo is not code-signed yet, so Windows
   shows **Windows protected your PC**: click **More info**, then **Run
   anyway**. Allow it to make changes when asked.
2. Leave **Start Exo** ticked at the end. Exo opens its window; closing
   it leaves Exo in the tray, by the clock. From then on it starts with
   Windows, in the tray.

The installer adds the Exo webcam, Exo's display driver and a Windows
firewall rule so phones can reach Exo over Wi-Fi. To trust the display
driver, it adds a certificate named **Exo Release** to the PC's trusted
certificates. Uninstalling removes all of these.

**Smart App Control:** if it is on (Windows Security, App & browser
control), Windows blocks Exo's unsigned webcam silently, and no app will
show a picture from it. Exo cannot work with Smart App Control on until
it is code-signed.

### On the phone

1. Open the release page in the phone's browser and download the APK.
2. Open the download. Android asks to allow installs from the browser
   the first time: allow it. On a Samsung phone, **Auto Blocker**
   (Settings, Security and privacy) must be off first. If Play Protect
   warns about an unknown app, choose to install anyway.
3. Open **Exo**.

### For USB only: USB debugging

Exo uses Android's USB debugging to talk over the cable. Wi-Fi does not
need it.

1. On the phone, Settings, About phone: tap **Build number** seven times.
   On a Samsung phone it is under Software information.
2. Settings, Developer options (on some phones under System): turn on
   **USB debugging**.
3. Plug the phone into the PC and unlock it. When the phone asks to allow
   USB debugging from this computer, tick **Always allow** and allow it.

## Use

On the phone, open Exo and pick **USB** or **Wi-Fi** under **Connect
by**.

- **USB:** plug the phone in.
- **Wi-Fi:** tap **Pick a PC** and choose yours from those running Exo,
  or type its address. The first time, the PC asks whether to allow the
  phone; once allowed, it connects without asking. If you say no, the
  phone stops trying until you start its role again.

Then tap **Use as display** or **Use as webcam**, or both. The webcam
asks for the camera the first time. A role another phone already holds is
refused, and the phone says which one has it.

Going back from the display keeps it connected: the PC's monitor and
its windows stay where they are. Exo's screen then shows **Back to
display** and **Stop display**; the webcam can start from there too.
The home screen or another app keeps it too, with a notification that
says so. Settings changed
meanwhile apply when you go back to the display, which then reconnects
in about a second.

Settings are on the phone:

- Under **Use as display**: the resolution, landscape or portrait, 30 or
  60 fps (60 at first). The phone holds each frame 50 ms to show them at
  an even pace. The PC's
  monitor takes exactly that mode when the display starts or shows again, and
  while it shows, the phone's screen refreshes at a rate the frame rate
  fits evenly. A tablet on Android 16 or
  later ignores landscape or portrait: Android lets no app turn it, so
  the display takes the way the tablet is held when it connects.
- Under **Use as webcam**: the front or back camera, the lens, the size
  and frame rate the PC's camera offers apps, a torch where there is one,
  and zoom, exposure and focus (left at the start for auto), which change
  the picture as you move them. The turn: Auto keeps the picture upright
  whichever way the phone lies on its side; Fixed keeps the way the phone
  lay when the webcam started, so it never flips. To change it, lie the
  phone the other way and start the webcam again.

On the PC, Exo lives in the tray. Its window, opened from the tray icon
or the Start menu, shows each connected phone, its role and whether it
works, and any USB phone waiting for its USB debugging prompt. It also
holds:

- **Start with Windows,** on from the first run.
- **Allow Wi-Fi connections without asking.** Off by default.
- **Enable adb connections (USB).** Off frees USB debugging for Android
  Studio or another tool; Exo cannot share it with one. If another such
  tool is running when Exo starts, the window says so: close it, then
  turn this off and on.
- **Windows display settings,** where the extra monitor is placed and
  scaled.

Closing the window leaves Exo running. **Quit** in the tray icon's menu
stops it. Its log is `C:\Windows\Temp\Exo\logs\exo-hub.log`.

## What you will notice

- **Wi-Fi is not encrypted.** Anyone on the same network who looks can
  see the screen and the camera, and with **Allow Wi-Fi connections
  without asking** on, could send touches to the PC. Use Wi-Fi only on a
  network you trust.
- **One camera app at a time.** A second app gets "Camera Reserved By
  Another App". Windows' Multi-App Camera setting (Settings, Bluetooth &
  devices, Cameras, Exo, Advanced camera options) allows sharing, but it
  turns itself off every time Exo starts, and whenever the webcam's size
  or frame rate changes.
- **A new webcam size or frame rate cuts off the app using it.** Windows
  learns a camera's size only when it is added, so Exo adds it again:
  reopen the camera in the app to get the new one.
- **No Exo running, no camera.** The Exo webcam exists only while Exo
  runs on the PC.
- **The extra monitor comes and goes with the display phone.** It
  appears when the phone connects, beside the PC's screen, and leaves 10
  seconds after the phone does, or at once when Exo stops. Windows then
  moves its windows to the PC's screen.
- **If the PC's screen goes black** because Windows put everything on the
  phone's monitor: press Win+P, then an arrow key and Enter for "PC
  screen only". Win+Ctrl+Shift+B restarts the graphics.
- **Touch cannot reach administrator windows** (Task Manager, installers,
  UAC prompts). Against those the phone's screen looks frozen.
- **Two-finger gestures** work only in apps that handle touch natively.
- **The pointer runs slightly ahead of the picture,** so a dragged window
  trails it.
- **The display's resolution is set on the phone,** not in Windows'
  display settings. Unplugging a phone stops only that phone's role;
  plugging it back resumes in about a second.

## Update

Run the new setup on the PC, and install the new APK on the phone over the
old one. If the two do not match, the phone says which one to update.

## Uninstall

On the PC: Settings, Apps, Installed apps, **Exo**, Uninstall. On the
phone: uninstall the Exo app.

## How it works

The Exo program on the PC talks to each phone over USB debugging or Wi-Fi,
one connection per role. The camera phone sends H.264, which Exo decodes
once and hands to its webcam, a media source Windows loads into its camera
service. For the display, Exo's display driver hands each image of the
virtual monitor to Exo on the GPU; Exo encodes it to H.264 and sends it to
the phone, which decodes and shows it, and sends touches back. Details in
`PLAN.md`.

## Building from source

Windows 11 with Visual Studio 2026 (Desktop C++, the Windows Driver Kit),
the .NET 10 SDK, Android Studio and Inno Setup 6. `CLAUDE.md` lists each
part's build command; `src/ExoSetup/release.ps1` builds the installer and
the APK into `dist\`.

## Licence

MIT, see `LICENSE`. The display driver is under the Microsoft Public
License (`src/ExoDisplay/LICENSE`). adb, shipped with the PC installer, is
under the Apache License 2.0. Their notices, and those of Vortice and the
.NET runtime inside the hub, are installed in `C:\Program
Files\Exo\licenses`.
