#!/usr/bin/env python3
"""Dayline for Linux.

Shows the bundled web app in a native GTK 4 window using the system's WebKitGTK, so no browser is needed.
To-dos are kept in ~/.local/share/dayline. Updates to the app's pages are downloaded there too (checked against
the checksum published with each release), so the app can update itself without root.
"""
import hashlib
import io
import json
import mimetypes
import os
import re
import shutil
import sys
import tarfile
import threading
import urllib.error
import urllib.request

# WebKitGTK's fast renderer shows an empty window on some NVIDIA + Wayland setups; this is the documented switch for it.
if os.path.exists('/proc/driver/nvidia/version'):
    os.environ.setdefault('WEBKIT_DISABLE_DMABUF_RENDERER', '1')

import gi

gi.require_version('Gtk', '4.0')
gi.require_version('WebKit', '6.0')
from gi.repository import Gdk, Gio, GLib, Gtk, WebKit  # noqa: E402

APP_ID = 'app.dayline'
VERSION = '1.0'
RELEASES = 'https://github.com/whozdgt/dayline/releases/latest/download/'
HOME = 'dayline://app/index.html'
HERE = os.path.dirname(os.path.realpath(__file__))
DATA = os.path.join(GLib.get_user_data_dir(), 'dayline')
TILING = ('hyprland', 'sway', 'niri', 'river', 'dwl', 'i3', 'bspwm', 'qtile')

# What the page can ask the app to do (the same names the Android app answers to).
BRIDGE = """window.DAYLINE_STANDALONE=true;(function(){
var post=function(o){window.webkit.messageHandlers.dayline.postMessage(JSON.stringify(o))};
window.DaylineHost={appVersion:function(){return %s},
checkUpdate:function(m){post({cmd:'check',manual:!!m})},
installUpdate:function(){post({cmd:'install'})},
saveBackup:function(t,n){post({cmd:'save',text:t,name:n})}};})();"""

SELFTEST = """(async()=>{const r={title:document.title,origin:location.origin,host:!!window.DaylineHost,
version:window.DaylineHost&&window.DaylineHost.appVersion(),cards:document.querySelectorAll('.card').length};
try{r.persisted=localStorage.getItem('dayline.selftest');localStorage.setItem('dayline.selftest',String(Date.now()));r.storage=true}catch(e){r.storage=String(e)}
try{const x=await fetch(%s,{headers:{apikey:%s}});r.fetch=x.status}catch(e){r.fetch=String(e)}
window.webkit.messageHandlers.dayline.postMessage(JSON.stringify({cmd:'selftest',r:r}))})()"""


def build_of(folder):
    """The build number of a copy of the app's pages, or -1 if that copy is missing or broken."""
    try:
        with open(os.path.join(folder, 'build.json'), encoding='utf-8') as f:
            build = int(json.load(f)['build'])
        return build if os.path.isfile(os.path.join(folder, 'index.html')) else -1
    except Exception:
        return -1


def pick_web():
    """Use the downloaded pages when they are newer than the ones installed with the package."""
    system, mine = os.path.join(HERE, 'web'), os.path.join(DATA, 'web')
    return mine if build_of(mine) > build_of(system) else system


class App(Gtk.Application):
    def __init__(self, selftest):
        flags = Gio.ApplicationFlags.NON_UNIQUE if selftest else Gio.ApplicationFlags.FLAGS_NONE
        super().__init__(application_id=APP_ID, flags=flags)
        self.selftest, self.tested = selftest, False
        self.win = self.view = self.ucm = None
        self.web, self.build = '', -1
        self.busy = self.available = False
        self.latest, self.sha = 0, ''

    # ---- window ----

    def do_activate(self):
        if self.win:
            self.win.present()
            return
        os.makedirs(DATA, exist_ok=True)
        self.web = pick_web()
        self.build = build_of(self.web)

        context = WebKit.WebContext.new()
        context.register_uri_scheme('dayline', self.serve)
        security = context.get_security_manager()
        security.register_uri_scheme_as_secure('dayline')
        security.register_uri_scheme_as_cors_enabled('dayline')
        session = WebKit.NetworkSession.new(os.path.join(DATA, 'storage'), os.path.join(GLib.get_user_cache_dir(), 'dayline'))

        self.ucm = WebKit.UserContentManager.new()
        self.ucm.register_script_message_handler('dayline', None)
        self.ucm.connect('script-message-received::dayline', self.on_message)
        self.add_bridge()

        self.view = WebKit.WebView(web_context=context, network_session=session, user_content_manager=self.ucm)
        colour = Gdk.RGBA()
        colour.parse('#15161A')
        self.view.set_background_color(colour)
        self.view.connect('decide-policy', self.on_policy)
        self.view.connect('context-menu', self.on_menu)
        self.view.connect('load-changed', self.on_load)

        self.win = Gtk.ApplicationWindow(application=self, title='Dayline', default_width=1180, default_height=780)
        # Tiling compositors draw their own borders; a GTK title bar on top of that is just wasted space.
        if any(name in os.environ.get('XDG_CURRENT_DESKTOP', '').lower() for name in TILING):
            self.win.set_decorated(False)
        self.win.set_child(self.view)

        quit_action = Gio.SimpleAction.new('quit', None)
        quit_action.connect('activate', lambda *a: self.quit())
        self.add_action(quit_action)
        self.set_accels_for_action('app.quit', ['<Control>q'])

        self.view.load_uri(HOME)
        self.win.present()
        if self.selftest:
            GLib.timeout_add_seconds(60, self.selftest_timeout)

    def add_bridge(self):
        version = '%s (build %d)' % (VERSION, self.build)
        self.ucm.remove_all_scripts()
        self.ucm.add_script(WebKit.UserScript.new(
            BRIDGE % json.dumps(version), WebKit.UserContentInjectedFrames.TOP_FRAME,
            WebKit.UserScriptInjectionTime.START, None, None))

    def serve(self, request):
        """Answers dayline://app/... with files from the app's own folder, and nothing outside it."""
        name = request.get_path().lstrip('/') or 'index.html'
        root = os.path.realpath(self.web)
        full = os.path.realpath(os.path.join(root, name))
        if not full.startswith(root + os.sep) or not os.path.isfile(full):
            request.finish_error(GLib.Error.new_literal(Gio.io_error_quark(), 'Not found', Gio.IOErrorEnum.NOT_FOUND))
            return
        with open(full, 'rb') as f:
            data = f.read()
        mime = mimetypes.guess_type(full)[0] or 'application/octet-stream'
        request.finish(Gio.MemoryInputStream.new_from_bytes(GLib.Bytes.new(data)), len(data), mime)

    def on_policy(self, view, decision, kind):
        # Links to other sites open in the browser, never inside the app.
        if kind in (WebKit.PolicyDecisionType.NAVIGATION_ACTION, WebKit.PolicyDecisionType.NEW_WINDOW_ACTION):
            uri = decision.get_navigation_action().get_request().get_uri() or ''
            if not uri.startswith(('dayline://', 'about:')):
                try:
                    Gio.AppInfo.launch_default_for_uri(uri, None)
                except GLib.Error:
                    pass
                decision.ignore()
                return True
        return False

    def on_menu(self, view, menu, hit):
        # Keep the right-click menu for text fields (paste, spelling); hide the browser one everywhere else.
        return not hit.context_is_editable()

    def run_js(self, script):
        def done(view, result, *rest):
            try:
                view.evaluate_javascript_finish(result)
            except GLib.Error:
                pass
        self.view.evaluate_javascript(script, -1, None, None, None, done)
        return False

    def on_message(self, ucm, value):
        try:
            message = json.loads(value.to_string())
        except Exception:
            return
        cmd = message.get('cmd')
        if cmd == 'check':
            self.check(bool(message.get('manual')))
        elif cmd == 'install':
            self.install()
        elif cmd == 'save':
            self.save_backup(str(message.get('text', '')), str(message.get('name', 'dayline-backup.json')))
        elif cmd == 'selftest' and self.selftest:
            print('SELFTEST ' + json.dumps(message.get('r')), flush=True)
            self.quit()

    # ---- backup ----

    def save_backup(self, text, name):
        dialog = Gtk.FileDialog(title='Save backup', initial_name=name)

        def done(source, result):
            try:
                target = source.save_finish(result)
                target.replace_contents(text.encode('utf-8'), None, False, Gio.FileCreateFlags.REPLACE_DESTINATION, None)
            except GLib.Error:
                pass          # cancelled, or the folder can't be written to
        dialog.save(self.win, None, done)

    # ---- updates: read latest.json, download the newer pages, check them, switch over ----

    def report(self, status):
        state = {'status': status, 'available': self.available, 'busy': self.busy, 'latest': self.latest}
        GLib.idle_add(self.run_js, 'window.daylineUpdate&&window.daylineUpdate(%s)' % json.dumps(state))

    @staticmethod
    def fetch(name):
        request = urllib.request.Request(RELEASES + name, headers={'User-Agent': 'dayline'})
        with urllib.request.urlopen(request, timeout=20) as reply:
            return reply.read()

    def check(self, manual):
        if self.busy:
            return
        self.busy = True
        if manual:
            self.report('Checking…')

        def work():
            try:
                info = json.loads(self.fetch('latest.json'))
                build = int(info.get('build') or 0)
                if build <= 0 or not info.get('web_sha256'):
                    raise ValueError('incomplete latest.json')
                self.latest, self.sha = build, str(info['web_sha256']).lower()
                self.available = build > self.build
                status = 'Build %d is available.' % build if self.available else "You're up to date."
            except urllib.error.HTTPError as e:
                status = ('No update has been published yet.' if e.code == 404 else "Couldn't reach GitHub. Try again later.") if manual else ''
            except Exception:
                status = "Couldn't reach GitHub. Check your connection and try again." if manual else ''
            self.busy = False
            self.report(status)
        threading.Thread(target=work, daemon=True).start()

    def install(self):
        if not self.available or self.busy:
            return
        self.busy = True
        self.report('Downloading…')

        def work():
            new, live = os.path.join(DATA, 'web.new'), os.path.join(DATA, 'web')
            try:
                data = self.fetch('dayline-web.tar.gz')
                if hashlib.sha256(data).hexdigest() != self.sha:
                    raise ValueError('checksum')
                shutil.rmtree(new, ignore_errors=True)
                os.makedirs(new)
                with tarfile.open(fileobj=io.BytesIO(data), mode='r:gz') as archive:
                    for member in archive.getmembers():
                        parts = member.name.split('/')
                        if not member.isfile() or member.name.startswith('/') or '..' in parts:
                            continue                     # plain files inside the folder only
                        target = os.path.join(new, *parts)
                        os.makedirs(os.path.dirname(target), exist_ok=True)
                        with open(target, 'wb') as out:
                            shutil.copyfileobj(archive.extractfile(member), out)
                if build_of(new) != self.latest:
                    raise ValueError('wrong build')
                shutil.rmtree(live, ignore_errors=True)
                os.replace(new, live)
            except ValueError:
                self.busy = False
                self.report('The downloaded update looks damaged. Try again in a minute.')
                return
            except Exception:
                self.busy = False
                self.report("The download didn't finish. Check your connection and try again.")
                return
            self.web, self.build = live, self.latest
            self.busy = self.available = False
            GLib.idle_add(self.reload)
        threading.Thread(target=work, daemon=True).start()

    def reload(self):
        self.add_bridge()
        self.view.load_uri(HOME)
        return False

    # ---- self-test (used by the build to check the app starts, saves and can reach the sync server) ----

    def on_load(self, view, event):
        if not self.selftest or self.tested or event != WebKit.LoadEvent.FINISHED:
            return
        self.tested = True
        with open(os.path.join(self.web, 'index.html'), encoding='utf-8') as f:
            page = f.read()
        url = re.search(r"SUPA_URL = '([^']+)'", page)
        key = re.search(r"SUPA_KEY = '([^']+)'", page)
        script = SELFTEST % (json.dumps((url.group(1) if url else '') + '/auth/v1/settings'), json.dumps(key.group(1) if key else ''))
        GLib.timeout_add(800, self.run_js, script)

    def selftest_timeout(self):
        print('SELFTEST timeout', flush=True)
        self.quit()
        return False


if __name__ == '__main__':
    selftest = '--self-test' in sys.argv
    sys.exit(App(selftest).run([a for a in sys.argv if a != '--self-test']))
