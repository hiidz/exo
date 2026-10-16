# Usage

- [Connect](#connect)
- [Roles](#roles)
- [Settings](#settings)
- [On the PC](#on-the-pc)
- [What you will notice](#what-you-will-notice)

## Connect

On the phone, open Exo and pick **USB** or **Wi-Fi** under **Connect
by**.

- **USB:** plug the phone in. It needs USB debugging, as in
  [Install](install.md#for-usb-only-usb-debugging).
- **Wi-Fi:** tap **PC** (it reads **Pick one** at first) and choose
  yours, or type its address.
  The first time, the PC asks whether to allow the phone, and both show
  the same 6-digit code: allow only if they match. After that the phone
  connects by itself. If you say no, the phone stops trying until
  you start its role again. Everything over Wi-Fi is encrypted.
- **Picking the PC again** pairs again, with a new code. Do that if the
  phone says the PC is not the one it paired with, after Exo was
  installed on the PC again.

## Roles

Tap **Use as display** or **Use as webcam**, or both. A role another
phone already holds is refused, and the phone says which one has it.

Going back from the display keeps it connected, and the PC's monitor
keeps its windows; Exo's screen then offers **Back to display** and
**Stop display**. Settings changed meanwhile apply when you go back,
which reconnects in about a second.

## Settings

Settings are on the phone, under **Connection**, **Display** and
**Webcam**:

- **Display:** 30 or 60fps. The monitor is the phone's full screen, long
  side across, and turns the right way up when you turn the phone upside
  down. For a portrait monitor, set its **Display orientation** to
  Portrait (or Portrait (flipped), whichever comes out upright) in
  Windows' display settings and hold the phone upright; touch follows.
- **Webcam:** front or back, the lens, the size and frame rate, shown as
  one line until you tap **Settings**. While the webcam runs: a torch,
  and zoom, exposure and focus, live. The turn: **Auto** keeps the
  picture upright whichever way the phone lies on its side; **Fixed**
  keeps the way it lay when the webcam started.

## On the PC

Exo lives in the tray. Its window, light or dark as Windows' app mode,
lists each phone with its role, USB or Wi-Fi, mode and state; with no
phone, the steps to start. It also
holds:

- **Start with Windows,** on from the first run.
- **Enable adb connections (USB).** Turn it off to free USB debugging
  for Android Studio or another adb tool; Exo cannot share it. If one is
  running when Exo starts, the window says so: close it, then turn this
  off and on.
- **Windows display settings,** where the monitor is placed and scaled.

**Quit** in the tray icon's menu stops Exo. Its log is
`C:\Windows\Temp\Exo\logs\exo-hub.log`.

## What you will notice

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
