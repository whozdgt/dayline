"""Wraps src/app.html into an installable app in dist/ and zips it."""
import pathlib, shutil, zipfile

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

with zipfile.ZipFile(root / "dayline.zip", "w", zipfile.ZIP_DEFLATED) as z:
    for f in sorted(dist.iterdir()):
        z.write(f, "dayline/" + f.name)
print("built", sorted(p.name for p in dist.iterdir()), (dist / "index.html").stat().st_size, "bytes")
