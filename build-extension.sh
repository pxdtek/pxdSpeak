#!/bin/sh
# Packs BrowserExtension/ for the Chrome Web Store and Edge Add-ons: dist/pxdSpeak-extension-<version>.zip
# (manifest.json at the root of the zip, as both stores want). Store pictures: pxdSpeak.exe --store-assets DIR › extension\
set -e
cd "$(dirname "$0")"
version=$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' BrowserExtension/manifest.json | head -1)
mkdir -p dist
out="dist/pxdSpeak-extension-$version.zip"
rm -f "$out"
(cd BrowserExtension && python3 -m zipfile -c "../$out" manifest.json background.js content.js content.css popup.html popup.js icons)
echo "$out"
