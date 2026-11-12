# Dayline

To-dos and a calendar in one small app. Everything is saved on the device and works offline.
Signing in (optional) syncs to-dos and events between devices.

- `src/app.html` is the whole app: markup, styles and script in one file, no dependencies.
- `src/sw.js` and `src/manifest.webmanifest` make it installable from a browser.
- `android/` wraps the same page in an Android app.
- `windows/` wraps it in a native window for Windows (WebView2), with an installer.
- `linux/` wraps it in a native GTK window for Linux, with an Arch package (`PKGBUILD`).
- `supabase/dayline.sql` sets up the one database table that sync uses. Run it once in the Supabase SQL editor.
- `python3 build.py` writes the installable web version to `dist/`.

Every push to `main` builds the Android app. The newest APK is always at
https://github.com/whozdgt/dayline/releases/latest/download/dayline.apk

The web version for Windows and Linux is published from the `gh-pages` branch at https://whozdgt.github.io/dayline/

## Windows

Download and run https://github.com/whozdgt/dayline/releases/latest/download/dayline-setup.exe
(or unzip `dayline-windows.zip` anywhere and run `Dayline.exe`). Windows 10 and 11.

## Arch Linux (CachyOS, Hyprland)

Install the ready-made package from the latest release:

    curl -LO https://github.com/whozdgt/dayline/releases/latest/download/dayline-any.pkg.tar.zst
    sudo pacman -U dayline-any.pkg.tar.zst

Or build it yourself: `cd linux && makepkg -si`. Then run `dayline`, or pick Dayline in your launcher.
The app updates its own pages from Settings; the window itself only changes when you reinstall the package.

On Hyprland the window has the class `app.dayline`, for rules such as:

    windowrulev2 = float, class:^(app\.dayline)$
    windowrulev2 = size 420 760, class:^(app\.dayline)$
