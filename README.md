# Exo

Turns Android phones, plugged in by USB, into a webcam and a second
monitor with touch for one Windows 11 PC. One program, the hub, owns the
single adb connection and runs both at once. That is the point: two
separate products each brought their own adb and could not run together.

Personal tool for now, USB only, set up by hand. A version anyone can
install, with a tray app and Wi-Fi, is planned.

## What you get

- **A webcam called Exo** that any Windows camera app can pick. The
  picture comes from a phone's camera, upright, at 1920x1080, 1280x720
  or 640x360, 30fps, or 60 where the phone's camera offers it.
- **A second monitor** on a phone or tablet, at the panel's own
  resolution or a lower one set on the phone, landscape or portrait, 30
  or 60 fps, with touch acting as touch on that part of the desktop.
  About a tenth of a second behind the PC.
- One phone can be both, or two phones one role each. Either role may be
  left empty.

## Setup, once

1. **Display driver:** `src/ExoDisplay/build.bat`, then
   `src/ExoDisplay/install.ps1`, which asks for administrator. It trusts
   the build's test certificate and adds the Exo display adapter. Its
   monitor appears only while the display phone is connected.
   **Black screen risk:** Windows may move everything to the virtual
   display. Recover blind with Win+P, arrow, Enter ("PC screen only").
   Win+Ctrl+Shift+B resets the graphics stack.
2. **Build** the other three parts: `src/ExoCamera/build.bat`, the hub
   with `dotnet build src/ExoHub/ExoHub.csproj -c Release`, and the phone
   app with `./gradlew assembleDebug` in `src/ExoPhone`.
3. **Phones:** enable USB debugging, plug in, unlock, accept the PC.
4. **Install:** `src/ExoSetup/setup.ps1`. It asks for administrator,
   registers the camera, makes the log folder, installs the app on every
   attached phone, and puts an Exo shortcut on the desktop.

To remove everything: `src/ExoSetup/setup.ps1 -Uninstall`, then
`src/ExoDisplay/install.ps1 -Uninstall`.

## Each time

Double-click **Exo** on the desktop. Plug the phones in, in any order.
On each phone, open Exo and tap **Use as display** or **Use as webcam**,
or both. The webcam asks for the camera the first time. A role another
phone already holds is refused, and the phone says which one has it.
Under **Use as display** are its resolution, landscape or portrait, and
30 or 60 fps; the PC's monitor takes exactly that mode the next time the
display starts. Under **Use as webcam** are the front or back camera,
the lens, the size and frame rate the PC's camera offers apps,
a torch where there is one, and zoom, exposure and focus
(left at the start for auto), which change the picture as you move them.
Ctrl+C in the hub's window stops it, and nothing it started is left
running.

## What you will notice

- **One camera app at a time.** A second app gets "Camera Reserved By
  Another App". Windows' Multi-App Camera setting (Settings, Bluetooth &
  devices, Cameras, Exo, Advanced camera options) allows sharing, but it
  turns itself off every time the hub starts, and whenever the webcam's
  size or frame rate changes.
- **A new webcam size or frame rate cuts off the app using it.** Windows
  learns a camera's size only when it is added, so Exo adds it again:
  reopen the camera in the app to get the new one.
- **No hub, no camera.** Exo exists only while the hub runs.
- **The extra monitor comes and goes with the display phone.** It
  appears when the phone connects, beside the PC's screen, and leaves 10
  seconds after the phone does, or at once when the hub stops. Windows
  then moves its windows to the PC's screen. Its scale is set in
  Windows' display settings, like any monitor's.
- **Touch cannot reach administrator windows** (Task Manager, installers,
  UAC prompts). Against those the tablet looks frozen.
- **Two-finger gestures** work only in apps that handle touch natively.
- **The pointer runs slightly ahead of the picture** on the tablet, so a
  dragged window trails it.
- **The display's resolution is set on the phone,** not in Windows'
  display settings. The tablet keeps running through a UAC prompt. Unplugging a
  phone stops only that phone's role; plugging it back resumes in about
  a second.

## How it works

ExoHub (C#) talks to each phone over adb reverse, one connection per
role. The camera phone sends H.264, which the hub decodes once and hands
to ExoCamera.dll (C++), the media source Windows loads into its camera
service. For the display, Exo's display driver hands each image of the
virtual monitor to the hub on the GPU; the hub encodes it to H.264 and
sends it to the phone, which decodes and shows it, and sends touches back. Details in `PLAN.md`.
