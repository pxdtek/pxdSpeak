# pxd Speak privacy policy

_Last updated: 26 November 2026_

pxd Speak reads text aloud on your PC. It is made by pxd (pxdtek.com).

## What pxd Speak collects
**Nothing.** There is no account, no analytics, no tracking and no advertising. The text you read, the files you open
and the audio pxd Speak makes never leave your PC.

## What stays on your PC
- **Settings** (voice, speed, hotkeys, the text on the Read tab): `%APPDATA%\pxdSpeak\settings.json`.
- **Voices** you download: `%LOCALAPPDATA%\pxdSpeak\Voices`. Delete them in the app (Voices tab) or by uninstalling.
- In the Microsoft Store version Windows keeps these in the app's own storage, and removes them when you uninstall.
- If you choose "Advanced: load it unpacked" for the browser extension, a copy of the extension is put in
  `Documents\pxd Speak\BrowserExtension`.

## What pxd Speak downloads
Only when you click a download button: voice files from the sherpa-onnx project's public release on GitHub
(`github.com/k2-fsa/sherpa-onnx/releases`). GitHub sees an ordinary file download from your IP address, as with any
website; pxd Speak sends nothing else with it.

## The browser extension
The optional pxd Speak extension for Edge and Chrome reads the text you select on a page and sends it only to the pxd
Speak app on the same PC (over `127.0.0.1`, which never leaves the computer). See [extension-privacy.md](extension-privacy.md).

## Reading selected text
When you press the read hotkey, pxd Speak copies the selection in the app in front, reads it, and puts your clipboard
back. The copied text is kept out of Windows clipboard history and is not saved.

## Purchases
pxd Speak Pro is sold through the Microsoft Store. Microsoft handles the payment; pxd never sees your payment details.
The app only asks the Store whether this Microsoft account owns the add-on. Microsoft's privacy statement:
https://privacy.microsoft.com/privacystatement

## Children
pxd Speak collects no data from anyone, including children.

## Contact
Questions: open an issue at https://github.com/pxdtek/pxdSpeak/issues
