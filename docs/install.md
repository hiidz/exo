# Install

- [What you need](#what-you-need)
- [On the PC](#on-the-pc)
- [On the phone](#on-the-phone)
- [For USB only: USB debugging](#for-usb-only-usb-debugging)
- [Update](#update)
- [Uninstall](#uninstall)

## What you need

- A PC with **Windows 11**, 64-bit. Windows 10 is not supported.
- An Android phone or tablet with **Android 10** or later.
- A USB cable, or the phone and the PC on the same Wi-Fi network.

Download both files from the
[latest release](https://github.com/hiidz/exo/releases/latest):
`Exo-<version>-setup.exe` for the PC and `Exo-<version>.apk` for the
phone.

## On the PC

1. Run `Exo-<version>-setup.exe`. Exo is not code-signed yet, so Windows
   shows **Windows protected your PC**: click **More info**, then **Run
   anyway**. Allow it to make changes when asked.
2. Leave **Start Exo** ticked at the end. Exo opens its window; closing
   it leaves Exo in the tray. From then on it starts with Windows.

The installer adds the Exo webcam, Exo's display driver, a firewall rule
for Wi-Fi, and a certificate named **Exo Release** so Windows trusts the
driver. Uninstalling removes all of them.

**Smart App Control:** if it is on (Windows Security, App & browser
control), Windows blocks Exo, starting with the setup, and Exo cannot
run on that PC until it is code-signed.

## On the phone

1. Download the APK in the phone's browser and open it. Allow installs
   from the browser when asked. On a Samsung phone, turn off **Auto
   Blocker** (Settings, Security and privacy) first. If Play Protect
   warns, install anyway.
2. Open **Exo**.

## For USB only: USB debugging

1. Settings, About phone: tap **Build number** seven times (on Samsung,
   under Software information).
2. Settings, Developer options: turn on **USB debugging**.
3. Plug the phone into the PC and unlock it. Tick **Always allow** and
   allow USB debugging from this computer.

Then see [Usage](usage.md) to connect.

## Update

Run the new setup on the PC, and install the new APK over the old one.
If the two do not match, the phone says which one to update.

## Uninstall

On the PC: Settings, Apps, Installed apps, **Exo**, Uninstall. On the
phone: uninstall the Exo app.
