# Dayline

To-dos and a calendar in one small app. Everything is saved on the device and works offline.

- `src/app.html` is the whole app: markup, styles and script in one file, no dependencies.
- `src/sw.js` and `src/manifest.webmanifest` make it installable from a browser.
- `android/` wraps the same page in an Android app.
- `python3 build.py` writes the installable web version to `dist/`.

Every push to `main` builds the Android app. The newest APK is always at
https://github.com/whozdgt/dayline/releases/latest/download/dayline.apk
