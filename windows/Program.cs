// Dayline for Windows.
//
// Shows the bundled pages in a native window using WebView2, the web engine that ships with Windows 10 and 11, so no
// browser is needed. To-dos are kept in %LOCALAPPDATA%\Dayline. Updates to the app's pages are downloaded there too
// (checked against the checksum published with each release), so the app can update itself without an installer.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Dayline
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            string selfTest = null;
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--self-test") selfTest = args[i + 1];

            bool first;
            using (var mutex = new System.Threading.Mutex(true, "app.dayline.single", out first))
            {
                // Starting Dayline a second time brings the open window forward instead.
                if (!first && selfTest == null) { Native.ShowExisting(); return; }
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                if (selfTest != null)
                {
                    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                    Application.ThreadException += (s, e) => { try { File.WriteAllText(selfTest, "SELFTEST error: " + e.Exception); } catch { } Environment.Exit(1); };
                }
                Application.Run(new MainForm(selfTest));
                GC.KeepAlive(mutex);
            }
        }
    }

    static class Native
    {
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hwnd, int command);

        /// Paints the title bar in the page's background colour (Windows 11), with light or dark buttons to match.
        public static void TitleBar(IntPtr hwnd, int r, int g, int b)
        {
            try
            {
                int dark = r * 299 + g * 587 + b * 114 > 150000 ? 0 : 1;
                DwmSetWindowAttribute(hwnd, 20, ref dark, 4);
                int colour = r | (g << 8) | (b << 16);
                DwmSetWindowAttribute(hwnd, 35, ref colour, 4);
            }
            catch { }
        }

        public static void ShowExisting()
        {
            try
            {
                var me = Process.GetCurrentProcess();
                foreach (var p in Process.GetProcessesByName(me.ProcessName))
                {
                    if (p.Id == me.Id || p.MainWindowHandle == IntPtr.Zero) continue;
                    ShowWindow(p.MainWindowHandle, 9);
                    SetForegroundWindow(p.MainWindowHandle);
                    break;
                }
            }
            catch { }
        }
    }

    sealed class MainForm : Form
    {
        const string Version = "1.0";
        const string Host = "dayline.example";            // a made-up address that only exists inside this window
        const string Origin = "https://" + Host + "/";
        const string Releases = "https://github.com/whozdgt/dayline/releases/latest/download/";
        static readonly string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dayline");
        static readonly string Here = AppDomain.CurrentDomain.BaseDirectory;

        // What the page can ask the app to do (the same names the Android and Linux apps answer to).
        const string Bridge = @"(function(){var post=function(o){window.chrome.webview.postMessage(JSON.stringify(o))};
window.DAYLINE_STANDALONE=true;
window.DaylineHost={appVersion:function(){return %VERSION%},
checkUpdate:function(m){post({cmd:'check',manual:!!m})},
installUpdate:function(){post({cmd:'install'})},
saveBackup:function(t,n){post({cmd:'save',text:t,name:n})},
setChrome:function(r,g,b){post({cmd:'chrome',r:r,g:g,b:b})}};})();";

        const string SelfTestJs = @"(async()=>{const r={title:document.title,origin:location.origin,host:!!window.DaylineHost,
version:window.DaylineHost&&window.DaylineHost.appVersion(),cards:document.querySelectorAll('.card').length};
try{r.persisted=localStorage.getItem('dayline.selftest');localStorage.setItem('dayline.selftest',String(Date.now()));r.storage=true}catch(e){r.storage=String(e)}
try{const x=await fetch(%URL%,{headers:{apikey:%KEY%}});r.fetch=x.status}catch(e){r.fetch=String(e)}
window.chrome.webview.postMessage(JSON.stringify({cmd:'selftest',r:r}))})()";

        readonly WebView2 web = new WebView2();
        readonly string selfTest;
        string webDir, scriptId, sha = "";
        int build = -1, latest;
        volatile bool busy;
        bool available, tested;

        public MainForm(string selfTest)
        {
            this.selfTest = selfTest;
            Text = "Dayline";
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(21, 22, 26);
            StartPosition = FormStartPosition.CenterScreen;
            float scale = DeviceDpi / 96f;
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            ClientSize = new Size(Math.Min((int)(1180 * scale), area.Width - 80), Math.Min((int)(780 * scale), area.Height - 80));
            MinimumSize = new Size((int)(360 * scale), (int)(520 * scale));
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            web.Dock = DockStyle.Fill;
            web.DefaultBackgroundColor = BackColor;
            Controls.Add(web);
            Native.TitleBar(Handle, 21, 22, 26);
            Load += async (s, e) => { try { await StartAsync(); } catch (Exception ex) { Fail(ex); } };
        }

        void Fail(Exception ex)
        {
            if (selfTest != null) { Finish("SELFTEST error: " + ex); return; }
            string text = ex is WebView2RuntimeNotFoundException
                ? "Dayline needs the Microsoft Edge WebView2 Runtime, which is missing on this PC.\n\nInstall it from https://developer.microsoft.com/microsoft-edge/webview2/ and open Dayline again."
                : "Dayline couldn't start.\n\n" + ex.Message;
            MessageBox.Show(this, text, "Dayline", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Close();
        }

        void Finish(string text)
        {
            try { File.WriteAllText(selfTest, text); } catch { }
            Environment.Exit(0);
        }

        // ---- window ----

        async Task StartAsync()
        {
            Directory.CreateDirectory(Data);
            webDir = PickWeb();
            build = BuildOf(webDir);
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Data, "storage"));
            await web.EnsureCoreWebView2Async(environment);
            CoreWebView2 core = web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;      // no F5 / Ctrl+P; copy and paste still work
            // Keep the right-click menu for text fields (paste, spelling); hide the browser one everywhere else.
            core.ContextMenuRequested += (s, e) => { if (!e.ContextMenuTarget.IsEditable) e.Handled = true; };
            // Links to other sites open in the browser, never inside the app.
            core.NewWindowRequested += (s, e) => { e.Handled = true; OpenOutside(e.Uri); };
            core.NavigationStarting += (s, e) =>
            {
                if (e.Uri.StartsWith(Origin, StringComparison.OrdinalIgnoreCase) || e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;
                e.Cancel = true;
                OpenOutside(e.Uri);
            };
            core.WebMessageReceived += OnMessage;
            core.NavigationCompleted += OnLoaded;
            await MountAsync();
        }

        /// Points the window at the current copy of the pages and (re)loads it.
        async Task MountAsync()
        {
            CoreWebView2 core = web.CoreWebView2;
            if (scriptId != null) core.RemoveScriptToExecuteOnDocumentCreated(scriptId);
            string version = new JavaScriptSerializer().Serialize(Version + " (build " + build + ")");
            scriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(Bridge.Replace("%VERSION%", version));
            try { core.ClearVirtualHostNameToFolderMapping(Host); } catch { }
            core.SetVirtualHostNameToFolderMapping(Host, webDir, CoreWebView2HostResourceAccessKind.Allow);
            core.Navigate(Origin + "index.html?b=" + build);
        }

        static void OpenOutside(string uri)
        {
            try
            {
                if (uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                    Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            }
            catch { }
        }

        void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            Dictionary<string, object> m;
            try
            {
                if (!e.Source.StartsWith(Origin, StringComparison.OrdinalIgnoreCase)) return;
                m = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(e.TryGetWebMessageAsString());
            }
            catch { return; }
            object cmd;
            if (m == null || !m.TryGetValue("cmd", out cmd)) return;
            try
            {
                switch (cmd as string)
                {
                    case "check": Check(m.ContainsKey("manual") && Equals(m["manual"], true)); break;
                    case "install": Install(); break;
                    case "save": SaveBackup(Convert.ToString(m["text"]), Convert.ToString(m["name"])); break;
                    case "chrome":
                        int r = Convert.ToInt32(m["r"]), g = Convert.ToInt32(m["g"]), b = Convert.ToInt32(m["b"]);
                        BackColor = Color.FromArgb(r, g, b);
                        web.DefaultBackgroundColor = BackColor;
                        Native.TitleBar(Handle, r, g, b);
                        break;
                    case "selftest":
                        if (selfTest != null) Finish("SELFTEST " + new JavaScriptSerializer().Serialize(m["r"]));
                        break;
                }
            }
            catch { }
        }

        // ---- backup ----

        void SaveBackup(string text, string name)
        {
            using (var dialog = new SaveFileDialog { Title = "Save backup", FileName = name, Filter = "Dayline backup (*.json)|*.json", DefaultExt = "json" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try { File.WriteAllText(dialog.FileName, text, new UTF8Encoding(false)); }
                catch (Exception ex) { MessageBox.Show(this, "The backup couldn't be saved there.\n\n" + ex.Message, "Dayline", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        // ---- updates: read latest.json, download the newer pages, check them, switch over ----

        void Ui(Action action)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(action); } catch { }
        }

        void Report(string status)
        {
            var state = new Dictionary<string, object> { { "status", status }, { "available", available }, { "busy", busy }, { "latest", latest } };
            string js = "window.daylineUpdate&&window.daylineUpdate(" + new JavaScriptSerializer().Serialize(state) + ")";
            Ui(() => { try { web.CoreWebView2.ExecuteScriptAsync(js); } catch { } });
        }

        static byte[] Fetch(string name)
        {
            using (var client = new WebClient())
            {
                client.Headers[HttpRequestHeader.UserAgent] = "dayline";
                return client.DownloadData(Releases + name);
            }
        }

        void Check(bool manual)
        {
            if (busy) return;
            busy = true;
            if (manual) Report("Checking\u2026");
            Task.Run(() =>
            {
                string status;
                try
                {
                    var info = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(Fetch("latest.json")));
                    int newest = Convert.ToInt32(info["build"]);
                    object value;
                    string webSha = info.TryGetValue("web_sha256", out value) ? Convert.ToString(value) : "";
                    if (newest <= 0 || webSha.Length == 0) throw new InvalidDataException();
                    latest = newest;
                    sha = webSha.ToLowerInvariant();
                    available = newest > build;
                    status = available ? "Build " + newest + " is available." : "You're up to date.";
                }
                catch (WebException ex)
                {
                    var reply = ex.Response as HttpWebResponse;
                    status = !manual ? "" : reply != null && reply.StatusCode == HttpStatusCode.NotFound
                        ? "No update has been published yet." : "Couldn't reach GitHub. Check your connection and try again.";
                }
                catch
                {
                    status = manual ? "Couldn't reach GitHub. Check your connection and try again." : "";
                }
                busy = false;
                Report(status);
            });
        }

        void Install()
        {
            if (!available || busy) return;
            busy = true;
            Report("Downloading\u2026");
            int target = latest;
            string want = sha;
            Task.Run(() =>
            {
                string dir = Path.Combine(Data, "web-" + target);
                try
                {
                    byte[] data = Fetch("dayline-web.tar.gz");
                    if (Sha256(data) != want) throw new InvalidDataException();
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    Untar(data, dir);
                    if (BuildOf(dir) != target) { Directory.Delete(dir, true); throw new InvalidDataException(); }
                }
                catch (InvalidDataException)
                {
                    busy = false;
                    Report("The downloaded update looks damaged. Try again in a minute.");
                    return;
                }
                catch
                {
                    busy = false;
                    Report("The download didn't finish. Check your connection and try again.");
                    return;
                }
                Ui(async () =>
                {
                    webDir = dir;
                    build = target;
                    busy = false;
                    available = false;
                    try { await MountAsync(); } catch { }
                });
            });
        }

        static string Sha256(byte[] data)
        {
            using (var hash = SHA256.Create())
            {
                var text = new StringBuilder();
                foreach (byte b in hash.ComputeHash(data)) text.Append(b.ToString("x2"));
                return text.ToString();
            }
        }

        /// Unpacks a .tar.gz: plain files with ordinary names only, and nothing outside the target folder.
        static void Untar(byte[] gz, string dir)
        {
            byte[] tar;
            using (var input = new GZipStream(new MemoryStream(gz), CompressionMode.Decompress))
            using (var buffer = new MemoryStream())
            {
                input.CopyTo(buffer);
                tar = buffer.ToArray();
            }
            Directory.CreateDirectory(dir);
            int at = 0;
            while (at + 512 <= tar.Length && tar[at] != 0)
            {
                string name = Field(tar, at, 100);
                string prefix = Field(tar, at + 345, 155);
                if (prefix.Length > 0) name = prefix + "/" + name;
                string octal = Field(tar, at + 124, 12).Trim();
                int size = octal.Length == 0 ? 0 : Convert.ToInt32(octal, 8);
                byte type = tar[at + 156];
                at += 512;
                if (size < 0 || at + size > tar.Length) throw new InvalidDataException();
                if ((type == (byte)'0' || type == 0) && SafeName(name))
                {
                    string path = Path.Combine(dir, name.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    using (var file = File.Create(path)) file.Write(tar, at, size);
                }
                at += (size + 511) / 512 * 512;
            }
        }

        static string Field(byte[] data, int at, int length)
        {
            int end = at;
            while (end < at + length && data[end] != 0) end++;
            return Encoding.UTF8.GetString(data, at, end - at);
        }

        static bool SafeName(string name)
        {
            if (name.Length == 0 || name.StartsWith("/") || name.IndexOfAny(new[] { '\\', ':' }) >= 0) return false;
            foreach (string part in name.Split('/')) if (part == ".." || part.Length == 0) return false;
            return true;
        }

        /// The build number of a copy of the app's pages, or -1 if that copy is missing or broken.
        static int BuildOf(string dir)
        {
            try
            {
                if (!File.Exists(Path.Combine(dir, "index.html"))) return -1;
                var info = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(dir, "build.json")));
                return Convert.ToInt32(info["build"]);
            }
            catch { return -1; }
        }

        /// Uses downloaded pages when they are newer than the ones installed with the app, and tidies away older copies.
        static string PickWeb()
        {
            string best = Path.Combine(Here, "web");
            int top = BuildOf(best);
            try
            {
                string[] copies = Directory.GetDirectories(Data, "web-*");
                foreach (string copy in copies)
                {
                    int b = BuildOf(copy);
                    if (b > top) { top = b; best = copy; }
                }
                foreach (string copy in copies)
                    if (copy != best) try { Directory.Delete(copy, true); } catch { }
            }
            catch { }
            return best;
        }

        // ---- self-test (used by the build to check the app starts, saves and can reach the sync server) ----

        void OnLoaded(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (selfTest == null || tested) return;
            tested = true;
            if (!e.IsSuccess) { Finish("SELFTEST navigation failed: " + e.WebErrorStatus); return; }
            string page = File.ReadAllText(Path.Combine(webDir, "index.html"));
            var serializer = new JavaScriptSerializer();
            string url = Regex.Match(page, "SUPA_URL = '([^']+)'").Groups[1].Value + "/auth/v1/settings";
            string key = Regex.Match(page, "SUPA_KEY = '([^']+)'").Groups[1].Value;
            string js = SelfTestJs.Replace("%URL%", serializer.Serialize(url)).Replace("%KEY%", serializer.Serialize(key));
            var soon = new System.Windows.Forms.Timer { Interval = 800 };
            soon.Tick += (s, a) => { soon.Stop(); web.CoreWebView2.ExecuteScriptAsync(js); };
            soon.Start();
            var limit = new System.Windows.Forms.Timer { Interval = 60000 };
            limit.Tick += (s, a) => Finish("SELFTEST timeout");
            limit.Start();
        }
    }
}
