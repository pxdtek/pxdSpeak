# Store screenshots

## Microsoft Store (app)
`pxdSpeak.exe --store-assets DIR` draws them headless (nothing on screen) into `DIR\screens\`, 1366×768 PNG:

1. `1-reading.png`: the Read tab mid-sentence, word lit up. Headline "Hear it. See every word."
2. `2-start.png`: first run, the three Start here cards. "Ready in a minute"
3. `3-voices.png`: the Voices tab. "28 natural AI voices, free"
4. `4-anywhere.png`: Settings, hotkeys and the browser card. "Read anywhere with a hotkey"

Add by hand on Windows (real screen captures, 1366×768 or larger):

5. A web page in Edge with the extension highlighting a word (the free browser feature).
6. Pro: the Save as MP3 dialog or the audiobook progress, once the Pro add-on is in (Store build, test purchase).

`build-msix.ps1` runs `--store-assets` too and leaves them in `packaging\out\store-assets\screens`.

## Chrome Web Store / Edge Add-ons (extension)
1280×800 PNG, real captures on Windows:
1. A news article with a sentence tinted and one word lit up.
2. The right-click menu on selected text showing "Read aloud with pxd Speak".
3. The extension popup saying "Linked to pxd Speak".
