// pxd Speak extension, in the page: turns the selection into one <span> per word, highlights the word being read,
// keeps it on screen, and puts the page back exactly as it was when reading ends.
(() => {
  if (window.__pxdSpeak) return;
  window.__pxdSpeak = true;

  const W = "pxd-speak-w", ON = "pxd-speak-on", SENT = "pxd-speak-s";
  const SKIP = new Set(["SCRIPT", "STYLE", "NOSCRIPT", "TEXTAREA", "INPUT", "SELECT", "OPTION", "BUTTON", "SVG", "MATH"]);
  const MAX_WORDS = 50000;

  let spans = [];          // one per word, in reading order
  let current = null;      // id of the reading these spans belong to
  let pending = false;     // a new reading was asked for; ignore "end" for the old one until "start" arrives
  let lit = null;          // span lit up now
  let sentence = [];       // spans of the sentence being read
  let userScrolledAt = 0;

  function blockOf(node) {
    let el = node.parentElement;
    while (el && el !== document.body) {
      const d = getComputedStyle(el).display;
      if (d !== "inline" && d !== "inline-block" && d !== "contents") return el;
      el = el.parentElement;
    }
    return document.body;
  }

  function skipped(node) {
    for (let el = node.parentElement; el; el = el.parentElement) {
      if (SKIP.has(el.tagName.toUpperCase())) return true;
      if (el.isContentEditable) return true;
    }
    return false;
  }

  /** Wraps every word of the selection in a span. Returns {words, blocks} or null when nothing usable is selected. */
  function wrapSelection() {
    const sel = window.getSelection();
    if (!sel || sel.rangeCount === 0 || sel.isCollapsed || !sel.toString().trim()) return null;
    const range = sel.getRangeAt(0);
    const root = range.commonAncestorContainer.nodeType === Node.TEXT_NODE
      ? range.commonAncestorContainer.parentNode : range.commonAncestorContainer;

    const nodes = [];
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
      acceptNode: n => (range.intersectsNode(n) && n.nodeValue.trim() && !skipped(n)) ? NodeFilter.FILTER_ACCEPT : NodeFilter.FILTER_REJECT,
    });
    if (root.nodeType === Node.TEXT_NODE) nodes.push(root);
    while (walker.nextNode()) nodes.push(walker.currentNode);
    if (!nodes.length) return null;

    unwrap();
    const words = [], blocks = [];
    let lastBlock = null;
    for (const node of nodes) {
      if (words.length >= MAX_WORDS) break;
      const text = node.nodeValue;
      const from = node === range.startContainer ? range.startOffset : 0;
      const to = node === range.endContainer ? range.endOffset : text.length;
      if (to <= from) continue;
      const block = blockOf(node);
      if (lastBlock && block !== lastBlock && words.length) {
        if (blocks[blocks.length - 1] !== words.length - 1) blocks.push(words.length - 1);
      }
      lastBlock = block;

      const frag = document.createDocumentFragment();
      if (from > 0) frag.appendChild(document.createTextNode(text.slice(0, from)));
      const part = text.slice(from, to);
      const re = /\S+/g;
      let at = 0, m;
      while ((m = re.exec(part)) && words.length < MAX_WORDS) {
        if (m.index > at) frag.appendChild(document.createTextNode(part.slice(at, m.index)));
        const span = document.createElement("span");
        span.className = W;
        span.textContent = m[0];
        span.dataset.pxdI = String(words.length);
        frag.appendChild(span);
        spans.push(span);
        words.push(m[0]);
        at = m.index + m[0].length;
      }
      if (at < part.length) frag.appendChild(document.createTextNode(part.slice(at)));
      if (to < text.length) frag.appendChild(document.createTextNode(text.slice(to)));
      node.parentNode.replaceChild(frag, node);
    }
    if (!words.length) return null;
    sel.removeAllRanges();   // our highlight takes over from the blue selection
    return { words, blocks };
  }

  /** Puts the page back: each span becomes plain text again and split text nodes are merged. */
  function unwrap() {
    const parents = new Set();
    for (const s of spans) {
      const p = s.parentNode;
      if (!p) continue;
      p.replaceChild(document.createTextNode(s.textContent), s);
      parents.add(p);
    }
    parents.forEach(p => p.normalize());
    spans = [];
    lit = null;
    sentence = [];
    current = null;
  }

  function light(i, first, last) {
    if (lit) lit.classList.remove(ON);
    const span = spans[i];
    if (!span) return;
    if (!sentence.length || sentence[0] !== spans[first]) {
      sentence.forEach(s => s.classList.remove(SENT));
      sentence = spans.slice(first, last + 1);
      sentence.forEach(s => s.classList.add(SENT));
    }
    span.classList.add(ON);
    lit = span;
    // Keep the word in view, unless the user is scrolling around themselves.
    if (Date.now() - userScrolledAt > 4000) {
      const r = span.getBoundingClientRect();
      if (r.top < 80 || r.bottom > window.innerHeight - 80)
        span.scrollIntoView({ block: "center", behavior: "smooth" });
    }
  }

  chrome.runtime.onMessage.addListener((m, sender, reply) => {
    if (m.type === "grab") {
      if (!document.hasFocus()) return;           // another window's page answers
      const r = wrapSelection();
      if (!r) return;                              // no answer: the app falls back to copying
      pending = true;
      reply(r);
    } else if (m.type === "readSelection") {
      const r = wrapSelection();
      if (!r) return;
      pending = true;
      chrome.runtime.sendMessage({ type: "read", words: r.words, blocks: r.blocks, start: 0 });
    } else if (m.type === "start") {
      current = m.id;
      pending = false;
    } else if (m.type === "word") {
      if (m.id === current) light(m.i, m.f, m.l);
    } else if (m.type === "end") {
      if (!pending && m.id === current) unwrap();
    }
  });

  // Click a word while reading: carry on from there.
  document.addEventListener("click", e => {
    const t = e.target;
    if (!(t instanceof Element) || !t.classList.contains(W) || !spans.length) return;
    if (window.getSelection() && !window.getSelection().isCollapsed) return;
    e.preventDefault();
    e.stopPropagation();
    pending = true;
    chrome.runtime.sendMessage({ type: "read", words: spans.map(s => s.textContent), blocks: blocksNow(), start: Number(t.dataset.pxdI) });
  }, true);

  function blocksNow() {
    const out = [];
    for (let i = 0; i + 1 < spans.length; i++)
      if (blockOf(spans[i].firstChild || spans[i]) !== blockOf(spans[i + 1].firstChild || spans[i + 1])) out.push(i);
    return out;
  }

  // Esc stops reading.
  document.addEventListener("keydown", e => {
    if (e.key === "Escape" && spans.length) chrome.runtime.sendMessage({ type: "stop" });
  }, true);

  for (const ev of ["wheel", "touchmove"]) window.addEventListener(ev, () => { userScrolledAt = Date.now(); }, { passive: true });
  window.addEventListener("keydown", e => {
    if (["PageUp", "PageDown", "ArrowUp", "ArrowDown", "Home", "End", " "].includes(e.key)) userScrolledAt = Date.now();
  }, { passive: true });

  // Test hook for pxd Speak's own checks (a page can't reach the extension through it).
  window.__pxdSpeakTest = { wrapSelection, unwrap, light, spans: () => spans };
})();
