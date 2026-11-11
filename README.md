# Dayline

To-dos and a calendar in one small app. Everything is saved on the device and works offline.
Signing in (optional) syncs to-dos and events between devices.

- `src/app.html` is the whole app: markup, styles and script in one file, no dependencies.
- `src/sw.js` and `src/manifest.webmanifest` make it installable from a browser.
- `android/` wraps the same page in an Android app.
- `supabase/dayline.sql` sets up the one database table that sync uses. Run it once in the Supabase SQL editor.
- `python3 build.py` writes the installable web version to `dist/`.

Every push to `main` builds the Android app. The newest APK is always at
https://github.com/whozdgt/dayline/releases/latest/download/dayline.apk

The web version for Windows and Linux is published from the `gh-pages` branch at https://whozdgt.github.io/dayline/
