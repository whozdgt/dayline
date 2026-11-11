"""Wraps src/app.html into an installable app in dist/ and zips it."""
import io, json, os, pathlib, shutil, tarfile, zipfile

root = pathlib.Path(__file__).parent
src, dist = root / "src", root / "dist"
dist.mkdir(exist_ok=True)

head, body = (src / "app.html").read_text(encoding="utf-8").split("<!--body-->", 1)
head = head.replace('<meta name="claude:viewport-fit-sides" content="cover">\n', "")

page = f"""<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
<meta name="theme-color" content="#15161A">
<link rel="manifest" href="manifest.webmanifest">
<link rel="icon" href="icon.svg" type="image/svg+xml">
<link rel="apple-touch-icon" href="icon-192.png">
<style>:root{{box-sizing:border-box;padding:env(safe-area-inset-top,0px) 0 env(safe-area-inset-bottom,0px)}}[hidden]{{display:none!important}}</style>
<script>window.DAYLINE_STANDALONE=true</script>
{head.strip()}
</head>
<body>
{body.strip()}
<script>if('serviceWorker' in navigator)addEventListener('load',()=>navigator.serviceWorker.register('sw.js').catch(()=>{{}}))</script>
</body>
</html>
"""
(dist / "index.html").write_text(page, encoding="utf-8")
for name in ("sw.js", "manifest.webmanifest", "icon.svg", "icon-192.png", "icon-512.png"):
    if (src / name).exists():
        shutil.copy(src / name, dist / name)

# The build number is the same on every platform; apps compare it with latest.json to see if they are out of date.
build = int(os.environ.get("GITHUB_RUN_NUMBER", "0"))
(dist / "build.json").write_text(json.dumps({"build": build}) + "\n", encoding="utf-8")

def add(tar, path, name, mode=0o644):
    data = path.read_bytes()
    info = tarfile.TarInfo(name); info.size = len(data); info.mode = mode
    tar.addfile(info, io.BytesIO(data))

# dayline-web.tar.gz: just the pages (what the Linux app downloads to update itself).
# dayline-linux.tar.gz: the pages plus the Linux window, launcher and menu entry (what the Arch package is made from).
with tarfile.open(root / "dayline-web.tar.gz", "w:gz") as web, tarfile.open(root / "dayline-linux.tar.gz", "w:gz") as linux:
    for f in sorted(dist.iterdir()):
        if f.is_file():
            add(web, f, f.name)
            add(linux, f, "dayline-linux/web/" + f.name)
    add(linux, root / "linux" / "dayline.py", "dayline-linux/dayline.py")
    add(linux, root / "linux" / "dayline", "dayline-linux/dayline", 0o755)
    add(linux, root / "linux" / "app.dayline.desktop", "dayline-linux/app.dayline.desktop")

with zipfile.ZipFile(root / "dayline.zip", "w", zipfile.ZIP_DEFLATED) as z:
    for f in sorted(dist.iterdir()):
        z.write(f, "dayline/" + f.name)
print("built", sorted(p.name for p in dist.iterdir()), (dist / "index.html").stat().st_size, "bytes")
