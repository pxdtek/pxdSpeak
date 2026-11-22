// pxd Speak extension: keeps a link to the pxd Speak app on this PC (ws://127.0.0.1:47321) and passes messages
// between it and the page. Nothing is sent anywhere else.

const APP = "ws://127.0.0.1:47321/pxdspeak";
let ws = null;
let owner = null;          // tab whose words are being read
const queue = [];          // messages waiting for the link to open

function browserName() {
  const brands = (navigator.userAgentData && navigator.userAgentData.brands) || [];
  const named = brands.map(b => b.brand).find(b => !/Not.?A.?Brand|Chromium/i.test(b));
  return named || "Chrome";
}

function connect() {
  if (ws && (ws.readyState === WebSocket.CONNECTING || ws.readyState === WebSocket.OPEN)) return;
  try {
    ws = new WebSocket(APP);
  } catch (e) {
    ws = null;
    return;
  }
  ws.onopen = () => {
    ws.send(JSON.stringify({ type: "hello", browser: browserName(), version: chrome.runtime.getManifest().version }));
    while (queue.length) ws.send(JSON.stringify(queue.shift()));
  };
  ws.onmessage = e => {
    try { fromApp(JSON.parse(e.data)); } catch (err) { /* ignore a bad message */ }
  };
  ws.onclose = () => { ws = null; };
  ws.onerror = () => {};
}

function send(m, wait) {
  if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(m));
  else if (wait) { queue.push(m); connect(); }
}

async function fromApp(m) {
  if (m.type === "grab") {
    // The hotkey was pressed with the browser in front: hand over the selection of the active tab, if it has one.
    let reply = null;
    try {
      const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
      if (tab && tab.id !== undefined) {
        reply = await chrome.tabs.sendMessage(tab.id, { type: "grab" }, { frameId: 0 });
        if (reply && reply.words) owner = tab.id;
      }
    } catch (e) { reply = null; }
    send(reply && reply.words
      ? { type: "grabbed", grabId: m.grabId, words: reply.words, blocks: reply.blocks, start: 0 }
      : { type: "grabbed", grabId: m.grabId, none: true });
  } else if (m.type === "start" || m.type === "word" || m.type === "end") {
    if (owner !== null) chrome.tabs.sendMessage(owner, m, { frameId: 0 }).catch(() => {});
  }
}

chrome.runtime.onMessage.addListener((msg, sender, reply) => {
  if (msg.type === "read" && sender.tab) {
    owner = sender.tab.id;
    send({ type: "read", words: msg.words, blocks: msg.blocks, start: msg.start || 0 }, true);
  } else if (msg.type === "stop") {
    send({ type: "stop" });
  } else if (msg.type === "status") {
    connect();
    reply({ connected: !!(ws && ws.readyState === WebSocket.OPEN) });
  }
});

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.create({ id: "pxd-speak-read", title: "Read aloud with pxd Speak", contexts: ["selection"] });
});

chrome.contextMenus.onClicked.addListener((info, tab) => {
  if (info.menuItemId === "pxd-speak-read" && tab && tab.id !== undefined)
    chrome.tabs.sendMessage(tab.id, { type: "readSelection" }, { frameId: 0 }).catch(() => {});
});

// Stay linked: the app may start after the browser, and the browser puts idle extension workers to sleep.
chrome.alarms.create("pxd-speak-link", { periodInMinutes: 0.5 });
chrome.alarms.onAlarm.addListener(() => connect());
setInterval(() => { connect(); send({ type: "ping" }); }, 20000);
connect();
