# Exo

Turns an Android phone or tablet into a webcam and a second monitor with
touch for a Windows 11 PC, by USB or on the same Wi-Fi. One phone can be
both, or two phones one role each.

Contents: What you get · What you need · Install (PC, phone, USB
debugging) · Use · What you will notice · Update · Uninstall · How it
works · Building from source · Licence.

## What you get

- **A webcam** that any Windows camera app can pick: the phone's camera,
  upright, at 1920x1080, 1280x720 or 640x360, 30fps (60 where the
  phone's camera offers it).
- **A second monitor** at the phone's own resolution, 30 or 60fps, with
  touch acting as touch on that part of the desktop. About a tenth of a
  second behind the PC.

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
   it leaves Exo in the tray. From then on it starts with Windows.

The installer adds the Exo webcam, Exo's display driver, a firewall rule
for Wi-Fi, and a certificate named **Exo Release** so Windows trusts the
driver. Uninstalling removes all of them.

**Smart App Control:** if it is on (Windows Security, App & browser
control), Windows silently blocks Exo's unsigned webcam, and no app
shows a picture from it, until Exo is code-signed.

### On the phone

1. Download the APK in the phone's browser and open it. Allow installs
   from the browser when asked. On a Samsung phone, turn off **Auto
   Blocker** (Settings, Security and privacy) first. If Play Protect
   warns, install anyway.
2. Open **Exo**.

### For USB only: USB debugging

1. Settings, About phone: tap **Build number** seven times (on Samsung,
   under Software information).
2. Settings, Developer options: turn on **USB debugging**.
3. Plug the phone into the PC and unlock it. Tick **Always allow** and
   allow USB debugging from this computer.

## Use

On the phone, open Exo and pick **USB** or **Wi-Fi** under **Connect
by**.

- **USB:** plug the phone in.
- **Wi-Fi:** tap **Pick a PC** and choose yours, or type its address.
  The first time, the PC asks whether to allow the phone. If you say no,
  the phone stops trying until you start its role again.

Then tap **Use as display** or **Use as webcam**, or both. A role
another phone already holds is refused, and the phone says which one
has it.

Going back from the display keeps it connected, and the PC's monitor
keeps its windows; Exo's screen then offers **Back to display** and
**Stop display**. Settings changed meanwhile apply when you go back,
which reconnects in about a second.

Settings are on the phone, under **Connection**, **Display** and
**Webcam**:

- **Display:** 30 or 60fps. The monitor is the phone's full screen, long
  side across, and turns the right way up when you turn the phone upside
  down. For a portrait monitor, set its **Display orientation** to
  Portrait (or Portrait (flipped), whichever comes out upright) in
  Windows' display settings and hold the phone upright; touch follows.
  On an Android 17 tablet, Android may turn the display with the tablet.
- **Webcam:** front or back, the lens, the size and frame rate, a torch,
  and zoom, exposure and focus, live. The turn: **Auto** keeps the
  picture upright whichever way the phone lies on its side; **Fixed**
  keeps the way it lay when the webcam started.

On the PC, Exo lives in the tray. Its window lists each phone with its
role, USB or Wi-Fi, mode and state; with no phone, the steps to start.
It also holds:

- **Start with Windows,** on from the first run.
- **Allow Wi-Fi connections without asking,** off by default.
- **Enable adb connections (USB).** Turn it off to free USB debugging
  for Android Studio or another adb tool; Exo cannot share it. If one is
  running when Exo starts, the window says so: close it, then turn this
  off and on.
- **Windows display settings,** where the monitor is placed and scaled.

**Quit** in the tray icon's menu stops Exo. Its log is
`C:\Windows\Temp\Exo\logs\exo-hub.log`.

## What you will notice

- **Wi-Fi is not encrypted.** Anyone on the same network can see the
  screen and the camera, and with **Allow Wi-Fi connections without
  asking** on, send touches to the PC. Use Wi-Fi only on a network you
  trust.
- **One camera app at a time.** A second app gets "Camera Reserved By
  Another App". Windows' Multi-App Camera setting (Settings, Bluetooth &
  devices, Cameras, Exo, Advanced camera options) allows sharing, but
  turns itself off each time Exo starts or the webcam's size or frame
  rate changes.
- **A new webcam size or frame rate cuts off the app using it:** reopen
  the camera in the app.
- **No Exo running, no camera.**
- **The monitor comes and goes with the display phone,** 10 seconds
  after the phone leaves, or at once when Exo stops. Windows moves its
  windows to the PC's screen.
- **If the PC's screen goes black:** press Win+P, then an arrow key and
  Enter for "PC screen only". Win+Ctrl+Shift+B restarts the graphics.
- **Touch cannot reach administrator windows** (Task Manager,
  installers, UAC prompts).
- **Two-finger gestures** work only in apps that handle touch.
- **The pointer runs slightly ahead of the picture,** so a dragged
  window trails it.

## Update

Run the new setup on the PC, and install the new APK over the old one.
If the two do not match, the phone says which one to update.

## Uninstall

On the PC: Settings, Apps, Installed apps, **Exo**, Uninstall. On the
phone: uninstall the Exo app.

## How it works

Exo on the PC talks to each phone over USB debugging or Wi-Fi, one
connection per role. The camera phone sends H.264, which Exo decodes
once for its webcam, a media source Windows loads into its camera
service. For the display, Exo's driver hands each image of the virtual
monitor to Exo on the GPU; Exo encodes it to H.264 and sends it to the
phone, which shows it and sends touches back. Details in `PLAN.md`.

## Building from source

Windows 11 with Visual Studio 2026 (Desktop C++, the Windows Driver
Kit), the .NET 10 SDK, Android Studio and Inno Setup 6. `CLAUDE.md`
lists each part's build command; `src/ExoSetup/release.ps1` builds the
installer and the APK into `dist\`.

## Licence

MIT, see `LICENSE`. The display driver is under the Microsoft Public
License (`src/ExoDisplay/LICENSE`). adb, shipped with the installer, is
under the Apache License 2.0. Their notices, and those of Vortice and
the .NET runtime, are installed in `C:\Program Files\Exo\licenses`.
