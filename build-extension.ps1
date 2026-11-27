# Windows twin of build-extension.sh: dist\pxdSpeak-extension-<version>.zip with manifest.json at the root.
$root = $PSScriptRoot
$version = (Get-Content (Join-Path $root "BrowserExtension\manifest.json") -Raw | ConvertFrom-Json).version
New-Item -ItemType Directory -Force (Join-Path $root "dist") | Out-Null
$out = Join-Path $root "dist\pxdSpeak-extension-$version.zip"
Remove-Item $out -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $root "BrowserExtension\*") -DestinationPath $out
$out
