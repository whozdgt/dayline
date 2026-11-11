#!/bin/bash
# Runs inside an archlinux container during the build: makes the package the same way a user would, installs it,
# then starts the app twice on a virtual screen to check it loads, keeps its data and can reach the sync server.
set -euo pipefail
pacman -Syu --noconfirm --needed base-devel python python-gobject gtk4 webkitgtk-6.0 xorg-server-xvfb xorg-xauth dbus mesa > /tmp/pacman.log 2>&1 || { tail -30 /tmp/pacman.log; exit 1; }
useradd -m builder
mkdir /build
cp /work/linux/PKGBUILD /work/dayline-linux.tar.gz /build/
chown -R builder /build
su builder -c 'cd /build && makepkg -f --noconfirm'
cp /build/dayline-*.pkg.tar.zst /work/dayline-any.pkg.tar.zst
pacman -U --noconfirm /build/dayline-*.pkg.tar.zst
pacman -Qi dayline | grep -E "^(Name|Version|Depends On|Installed Size)"
pacman -Ql dayline | grep -v '/$' | head -20
{
  echo "webkitgtk $(pacman -Q webkitgtk-6.0 | cut -d' ' -f2), gtk4 $(pacman -Q gtk4 | cut -d' ' -f2), python $(python3 -V | cut -d' ' -f2)"
  for run in 1 2; do
    echo "--- run $run"
    su builder -c 'export WEBKIT_DISABLE_SANDBOX_THIS_IS_DANGEROUS=1 LIBGL_ALWAYS_SOFTWARE=1 GSK_RENDERER=cairo GDK_BACKEND=x11; timeout 120 dbus-run-session -- xvfb-run -a dayline --self-test 2>&1 | tail -12' || echo "exit $?"
  done
} > /work/linux-test.txt 2>&1 || true
cat /work/linux-test.txt
