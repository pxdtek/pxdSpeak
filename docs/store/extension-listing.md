# pxd Speak extension: Chrome Web Store and Edge Add-ons

Package: `./build-extension.sh` (or `build-extension.ps1`) → `dist/pxdSpeak-extension-<version>.zip`.
Pictures: `pxdSpeak.exe --store-assets DIR` → `DIR\extension\` (icon-128.png, logo-300.png, promo-small-440x280.png,
promo-marquee-1400x560.png). Screenshots (1280×800): see screenshots.md.

Accounts: Chrome Web Store developer account (one-time $5), Edge: Partner Center › Microsoft Edge program (free).
Once listed, put the two store URLs in `Platform/BrowserStores.cs` (`Chrome`, `Edge`): the app then shows
"Add to Chrome" / "Add to Edge" buttons and moves "load unpacked" under Advanced.

## Name
pxd Speak

## Summary (Chrome: max 132 characters)
Highlights every word on the page as the pxd Speak app reads your selection aloud in natural voices. Needs pxd Speak for Windows.

## Description
pxd Speak reads text aloud in natural AI voices that run offline on your Windows PC. This extension adds word
highlighting on web pages:

• Select text and press Ctrl + Alt + R (or right-click › Read aloud with pxd Speak)
• Each word lights up as it's heard and the sentence is tinted
• The page scrolls along with the reading, unless you scroll yourself
• Click a word to jump there; Esc stops
• The page is put back exactly as it was afterwards

The extension needs the free pxd Speak app for Windows (Microsoft Store or github.com/pxdtek/pxdSpeak). It talks only
to that app, on the same PC (127.0.0.1). Nothing is sent over the internet.

## Category
Chrome: Accessibility. Edge: Accessibility.

## Language
English

## Single purpose (Chrome › Privacy practices)
Highlight, on the current web page, each word of the user's selected text as the pxd Speak desktop app reads it aloud.

## Permission justifications
- **contextMenus:** adds "Read aloud with pxd Speak" to the right-click menu when text is selected.
- **scripting:** when the extension is installed or updated, tabs that are already open have no page script yet; it adds
  the script to them so reading works without reloading every tab.
- **alarms:** wakes the background script every 30 seconds to keep the link to the pxd Speak app (the browser stops idle
  extension workers, and the app may start after the browser).
- **Host permission `<all_urls>` and the content script on all pages:** the user can read selected text on any page. The
  script wraps the selected words in highlight markers while they're read and removes them afterwards. It does nothing
  until the user asks for a reading.
- **Remote code:** none. All code is in the package. The only connection is a WebSocket to `ws://127.0.0.1:47321`, the
  pxd Speak app on the same computer.

## Data usage (Chrome › Privacy practices)
- Collects: **Website content** (only the text the user selects, only when they ask for it to be read). Sent only to
  the pxd Speak app on the same device; not to any server.
- Does not collect: personally identifiable information, health, financial, authentication, personal communications,
  location, web history, user activity.
- Certify: not sold to third parties; not used or transferred for purposes unrelated to the single purpose; not used
  for creditworthiness or lending.
- Privacy policy URL: https://github.com/pxdtek/pxdSpeak/blob/main/docs/store/extension-privacy.md

## Edge: notes for certification
The extension needs the pxd Speak desktop app to read aloud. Without it, selecting text and choosing Read aloud shows
"pxd Speak isn't running" on the page. The app is free: https://github.com/pxdtek/pxdSpeak/releases
