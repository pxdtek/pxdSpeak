<#
  Builds pxd Speak for the Microsoft Store: pxdSpeak.msix in packaging\out.

    .\packaging\build-msix.ps1                       # placeholders for the identity: fine for a local test
    .\packaging\build-msix.ps1 -IdentityName 12345pxd.pxdSpeak -Publisher "CN=…" -PublisherDisplayName pxd

  It publishes the app (Release, win-x64, self-contained: Store customers may not have .NET 10, and MSIX cannot install it for them), adds the Pro add-on (pxdSpeak.Pro.dll and its
  libraries) when ..\..\pxdSpeakPro exists, draws the logos with --store-assets, fills in AppxManifest.xml and packs
  it with makeappx. The Store signs the upload; nothing is signed here.

  Local test without signing (Developer Mode on):  .\packaging\build-msix.ps1 -Register
    That registers packaging\out\layout in place (Add-AppxPackage -Register) so it runs as a packaged app.
    Remove it again with:  Get-AppxPackage *pxdSpeak* | Remove-AppxPackage
#>
param(
    [string]$IdentityName = "pxd.pxdSpeak.Placeholder",
    [string]$Publisher = "CN=00000000-0000-0000-0000-000000000000",
    [string]$PublisherDisplayName = "pxd",
    [string]$MakeAppx = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\makeappx.exe",
    [switch]$NoPro,
    [switch]$Register
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $PSScriptRoot "out"
$layout = Join-Path $out "layout"
$assets = Join-Path $out "store-assets"
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $layout, $assets | Out-Null

# 1. The app
dotnet publish (Join-Path $root "pxdSpeak.csproj") -c Release -r win-x64 --self-contained true -p:StoreBuild=true -o $layout
if ($LASTEXITCODE) { throw "publish failed" }

# 2. The Pro add-on (private repo, next to this one). It references the app but doesn't copy it, so only its own
#    files come over.
$pro = Join-Path (Split-Path $root -Parent) "pxdSpeakPro\pxdSpeakPro.csproj"
if (-not $NoPro -and (Test-Path $pro)) {
    $proOut = Join-Path $out "pro"
    dotnet publish $pro -c Release -r win-x64 --self-contained false -o $proOut
    if ($LASTEXITCODE) { throw "Pro publish failed" }
    Get-ChildItem $proOut -File | Where-Object { -not (Test-Path (Join-Path $layout $_.Name)) -and $_.Extension -ne ".pdb" } |
        Copy-Item -Destination $layout
    Write-Host "Pro add-on included"
} else {
    Write-Host "No Pro add-on (free build)"
}

# 3. Logos and Store pictures, drawn by the app itself (headless, nothing on screen)
& (Join-Path $layout "pxdSpeak.exe") --store-assets $assets | Out-Null
$wait = 0
while (-not (Test-Path (Join-Path $assets "done.txt")) -and $wait -lt 120) { Start-Sleep 1; $wait++ }
New-Item -ItemType Directory -Force (Join-Path $layout "Assets") | Out-Null
Copy-Item (Join-Path $assets "msix\*.png") (Join-Path $layout "Assets")

# 4. The manifest
[xml]$proj = Get-Content (Join-Path $root "pxdSpeak.csproj")
$v = [version](($proj.Project.PropertyGroup | Where-Object Version | Select-Object -First 1).Version)
$version = "{0}.{1}.{2}.0" -f $v.Major, $v.Minor, [math]::Max(0, $v.Build)
(Get-Content (Join-Path $PSScriptRoot "AppxManifest.xml") -Raw).
    Replace("{{IDENTITY_NAME}}", $IdentityName).Replace("{{PUBLISHER}}", $Publisher).
    Replace("{{PUBLISHER_DISPLAY_NAME}}", $PublisherDisplayName).Replace("{{VERSION}}", $version) |
    Set-Content (Join-Path $layout "AppxManifest.xml") -Encoding utf8

# 5. Logos: plain names as a fallback, plus resources.pri so Windows picks the scale-100/200 and target-size files
$assetDir = Join-Path $layout "Assets"
Get-ChildItem $assetDir -Filter "*.scale-200.png" | ForEach-Object {
    Copy-Item $_.FullName (Join-Path $assetDir ($_.Name -replace "\.scale-200", "")) -Force
}
$makePri = Join-Path (Split-Path $MakeAppx) "makepri.exe"
if (Test-Path $makePri) {
    $priConfig = Join-Path $out "priconfig.xml"
    & $makePri createconfig /cf $priConfig /dq en-US /pv 10.0.0 /o | Out-Null
    & $makePri new /pr $layout /cf $priConfig /mn (Join-Path $layout "AppxManifest.xml") /of (Join-Path $layout "resources.pri") /o | Out-Null
    if ($LASTEXITCODE) { throw "makepri failed" }
}

if ($Register) {
    Add-AppxPackage -Register (Join-Path $layout "AppxManifest.xml")
    Write-Host "Registered. Start it from the Start menu (pxd Speak)."
    return
}

# 6. Pack
& $MakeAppx pack /o /d $layout /p (Join-Path $out "pxdSpeak-$version.msix")
if ($LASTEXITCODE) { throw "makeappx failed" }
Write-Host "Done: $(Join-Path $out "pxdSpeak-$version.msix")  (upload this in Partner Center; the Store signs it)"
Write-Host "Store screenshots: $(Join-Path $assets 'screens')"
