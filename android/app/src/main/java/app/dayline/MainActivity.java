package app.dayline;

import android.app.Activity;
import android.content.Intent;
import android.graphics.Color;
import android.graphics.Insets;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.provider.Settings;
import android.view.View;
import android.view.WindowInsets;
import android.view.WindowInsetsController;
import android.webkit.JavascriptInterface;
import android.webkit.ValueCallback;
import android.webkit.WebChromeClient;
import android.webkit.WebResourceRequest;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.FrameLayout;
import android.widget.Toast;

import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileNotFoundException;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;

/** Shows the bundled web app full screen. Data stays in the app's own storage on the phone. */
public class MainActivity extends Activity {
    private static final int PICK_FILE = 1, SAVE_FILE = 2;
    /** Every build is published as a GitHub release holding the APK and a small latest.json that describes it. */
    private static final String RELEASES = "https://github.com/whozdgt/dayline/releases/latest/download/";
    private static final String APK_TYPE = "application/vnd.android.package-archive";

    private WebView web;
    private FrameLayout root;
    private ValueCallback<Uri[]> fileCallback;
    private String pendingBackup;

    private volatile boolean updateBusy, updateAvailable;
    private volatile int latestBuild;
    private volatile String latestSha = "";

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        web = new WebView(this);
        web.setBackgroundColor(0xFF15161A);
        WebSettings s = web.getSettings();
        s.setJavaScriptEnabled(true);
        s.setDomStorageEnabled(true);
        web.addJavascriptInterface(new Host(), "DaylineHost");

        // Links to other sites open in the browser, never inside the app.
        web.setWebViewClient(new WebViewClient() {
            @Override
            public boolean shouldOverrideUrlLoading(WebView v, WebResourceRequest r) {
                if ("file".equals(r.getUrl().getScheme())) return false;
                try { startActivity(new Intent(Intent.ACTION_VIEW, r.getUrl())); } catch (Exception ignored) { }
                return true;
            }
        });

        // "Import backup" opens the system file picker.
        web.setWebChromeClient(new WebChromeClient() {
            @Override
            public boolean onShowFileChooser(WebView v, ValueCallback<Uri[]> cb, FileChooserParams p) {
                if (fileCallback != null) fileCallback.onReceiveValue(null);
                fileCallback = cb;
                Intent i = new Intent(Intent.ACTION_OPEN_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("*/*");
                try {
                    startActivityForResult(i, PICK_FILE);
                } catch (Exception e) {
                    fileCallback = null;
                    return false;
                }
                return true;
            }
        });

        root = new FrameLayout(this);
        root.setBackgroundColor(0xFF15161A);
        root.addView(web, new FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT));
        setContentView(root);
        // From Android 15 an app draws behind the status and gesture bars. Keep the page clear of them, the camera
        // cut-out and the keyboard; the strip behind the bars is painted in the app's own background colour.
        if (Build.VERSION.SDK_INT >= 35) {
            getWindow().setNavigationBarContrastEnforced(false);
            root.setOnApplyWindowInsetsListener((v, insets) -> {
                Insets in = insets.getInsets(WindowInsets.Type.systemBars() | WindowInsets.Type.displayCutout() | WindowInsets.Type.ime());
                v.setPadding(in.left, in.top, in.right, in.bottom);
                return WindowInsets.CONSUMED;
            });
        }
        web.loadUrl("file:///android_asset/index.html");
    }

    @Override
    protected void onActivityResult(int req, int res, Intent data) {
        Uri uri = res == RESULT_OK && data != null ? data.getData() : null;
        if (req == PICK_FILE) {
            if (fileCallback != null) fileCallback.onReceiveValue(uri == null ? null : new Uri[]{uri});
            fileCallback = null;
        } else if (req == SAVE_FILE) {
            String text = pendingBackup;
            pendingBackup = null;
            if (uri == null || text == null) return;
            try (OutputStream out = getContentResolver().openOutputStream(uri)) {
                out.write(text.getBytes(StandardCharsets.UTF_8));
                Toast.makeText(this, "Backup saved", Toast.LENGTH_SHORT).show();
            } catch (Exception e) {
                Toast.makeText(this, "Could not save the backup", Toast.LENGTH_LONG).show();
            }
        }
    }

    /** Back closes an open dialog first, then leaves the app. */
    @Override
    public void onBackPressed() {
        web.evaluateJavascript(
            "(function(){var d=document.getElementById('dlg');if(d&&d.open){d.close();return true}return false})()",
            v -> { if (!"true".equals(v)) moveTaskToBack(true); });
    }

    @Override protected void onPause() { super.onPause(); web.onPause(); }
    @Override protected void onResume() { super.onResume(); web.onResume(); }

    // ---- updates: read latest.json, download the newer APK, check it, hand it to Android's installer ----

    private int currentBuild() {
        try { return getPackageManager().getPackageInfo(getPackageName(), 0).versionCode; } catch (Exception e) { return 0; }
    }

    private String currentVersion() {
        try {
            return getPackageManager().getPackageInfo(getPackageName(), 0).versionName + " (build " + currentBuild() + ")";
        } catch (Exception e) {
            return "";
        }
    }

    /** Tells the page what the updater is doing; the page shows it in Settings. */
    private void report(String status) {
        try {
            JSONObject o = new JSONObject();
            o.put("status", status);
            o.put("available", updateAvailable);
            o.put("busy", updateBusy);
            o.put("latest", latestBuild);
            final String js = "window.daylineUpdate&&window.daylineUpdate(" + o + ")";
            runOnUiThread(() -> web.evaluateJavascript(js, null));
        } catch (Exception ignored) { }
    }

    private void download(String address, OutputStream out, boolean showProgress) throws IOException {
        HttpURLConnection c = (HttpURLConnection) new URL(address).openConnection();
        try {
            c.setConnectTimeout(15000);
            c.setReadTimeout(30000);
            c.setUseCaches(false);
            c.setRequestProperty("User-Agent", "dayline");
            int code = c.getResponseCode();
            if (code == 404) throw new FileNotFoundException(address);
            if (code != 200) throw new IOException("HTTP " + code);
            long total = c.getContentLength(), got = 0;
            int shown = -1;
            byte[] buf = new byte[16384];
            try (InputStream in = c.getInputStream()) {
                for (int n; (n = in.read(buf)) > 0; ) {
                    out.write(buf, 0, n);
                    got += n;
                    int pct = total > 0 ? (int) (got * 100 / total) : -1;
                    if (showProgress && pct != shown) { shown = pct; report("Downloading… " + pct + "%"); }
                }
            }
        } finally {
            c.disconnect();
        }
    }

    private static String sha256(File f) throws Exception {
        MessageDigest md = MessageDigest.getInstance("SHA-256");
        byte[] buf = new byte[16384];
        try (InputStream in = new FileInputStream(f)) {
            for (int n; (n = in.read(buf)) > 0; ) md.update(buf, 0, n);
        }
        StringBuilder sb = new StringBuilder();
        for (byte b : md.digest()) sb.append(String.format("%02x", b));
        return sb.toString();
    }

    private void checkUpdate(final boolean manual) {
        if (updateBusy) return;
        updateBusy = true;
        if (manual) report("Checking…");
        new Thread(() -> {
            String status;
            try {
                ByteArrayOutputStream json = new ByteArrayOutputStream();
                download(RELEASES + "latest.json", json, false);
                JSONObject o = new JSONObject(new String(json.toByteArray(), StandardCharsets.UTF_8));
                int build = o.optInt("build");
                if (build <= 0) throw new IOException("no build number");
                latestBuild = build;
                latestSha = o.optString("sha256");
                updateAvailable = build > currentBuild();
                status = updateAvailable ? "Build " + build + " is available." : "You're up to date.";
            } catch (FileNotFoundException e) {
                status = manual ? "No update has been published yet." : "";
            } catch (Exception e) {
                status = manual ? "Couldn't reach GitHub. Check your connection and try again." : "";
            }
            updateBusy = false;
            report(status);
        }).start();
    }

    private void installUpdate() {
        if (!updateAvailable || updateBusy) return;
        // Android asks once, per app, before an app may install other apps (including itself).
        if (Build.VERSION.SDK_INT >= 26 && !getPackageManager().canRequestPackageInstalls()) {
            try {
                startActivity(new Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:" + getPackageName())));
            } catch (Exception ignored) { }
            report("Android needs your OK first. Switch on “Allow from this source”, go back, then tap Update now again.");
            return;
        }
        updateBusy = true;
        report("Downloading… 0%");
        new Thread(() -> {
            File apk = ApkProvider.file(this);
            String failed = null;
            try {
                apk.getParentFile().mkdirs();
                String want = latestSha;
                // A finished download from an earlier try (installer cancelled) can be reused.
                if (want.isEmpty() || !apk.exists() || !sha256(apk).equalsIgnoreCase(want)) {
                    try (OutputStream out = new FileOutputStream(apk)) {
                        download(RELEASES + "dayline.apk", out, true);
                    }
                    if (want.isEmpty() || !sha256(apk).equalsIgnoreCase(want)) failed = "The downloaded update looks damaged. Try again in a minute.";
                }
            } catch (Exception e) {
                failed = "The download didn't finish. Check your connection and try again.";
            }
            updateBusy = false;
            if (failed != null) {
                apk.delete();
                report(failed);
                return;
            }
            runOnUiThread(() -> {
                try {
                    startActivity(new Intent(Intent.ACTION_VIEW)
                        .setDataAndType(Uri.parse("content://" + getPackageName() + ".update/dayline.apk"), APK_TYPE)
                        .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION | Intent.FLAG_ACTIVITY_NEW_TASK));
                    report("Tap Install in the Android prompt. Your to-dos are kept.");
                } catch (Exception e) {
                    report("Couldn't open Android's installer.");
                }
            });
        }).start();
    }

    /** What the page can ask the app to do. */
    private class Host {
        /** "Download backup" asks where to save the file. */
        @JavascriptInterface
        public void saveBackup(String text, String name) {
            Intent i = new Intent(Intent.ACTION_CREATE_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE)
                .setType("application/json").putExtra(Intent.EXTRA_TITLE, name);
            runOnUiThread(() -> {
                pendingBackup = text;
                try { startActivityForResult(i, SAVE_FILE); } catch (Exception e) { pendingBackup = null; }
            });
        }

        @JavascriptInterface
        public String appVersion() { return currentVersion(); }

        /** The page's background colour, so the bars around it match the chosen theme. */
        @JavascriptInterface
        public void setChrome(int r, int g, int b) {
            runOnUiThread(() -> {
                int c = Color.rgb(r, g, b);
                boolean light = r * 299 + g * 587 + b * 114 > 150000;
                root.setBackgroundColor(c);
                web.setBackgroundColor(c);
                if (Build.VERSION.SDK_INT < 35) {
                    getWindow().setStatusBarColor(c);
                    getWindow().setNavigationBarColor(c);
                }
                if (Build.VERSION.SDK_INT >= 30) {
                    int mask = WindowInsetsController.APPEARANCE_LIGHT_STATUS_BARS | WindowInsetsController.APPEARANCE_LIGHT_NAVIGATION_BARS;
                    WindowInsetsController ctl = getWindow().getInsetsController();
                    if (ctl != null) ctl.setSystemBarsAppearance(light ? mask : 0, mask);
                } else {
                    getWindow().getDecorView().setSystemUiVisibility(light
                        ? View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR | View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR : 0);
                }
            });
        }

        @JavascriptInterface
        public void checkUpdate(boolean manual) { MainActivity.this.checkUpdate(manual); }

        @JavascriptInterface
        public void installUpdate() { runOnUiThread(MainActivity.this::installUpdate); }
    }
}
