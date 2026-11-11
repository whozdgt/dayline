package app.dayline;

import android.app.Activity;
import android.content.Intent;
import android.net.Uri;
import android.os.Bundle;
import android.webkit.JavascriptInterface;
import android.webkit.ValueCallback;
import android.webkit.WebChromeClient;
import android.webkit.WebResourceRequest;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.Toast;

import java.io.OutputStream;
import java.nio.charset.StandardCharsets;

/** Shows the bundled web app full screen. Data stays in the app's own storage on the phone. */
public class MainActivity extends Activity {
    private static final int PICK_FILE = 1, SAVE_FILE = 2;
    private WebView web;
    private ValueCallback<Uri[]> fileCallback;
    private String pendingBackup;

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

        setContentView(web);
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
    }
}
