function show() {
  chrome.runtime.sendMessage({ type: "status" }, r => {
    const ok = !!(r && r.connected);
    document.getElementById("state").className = "state " + (ok ? "ok" : "bad");
    document.getElementById("text").textContent = ok ? "Linked to pxd Speak" : "pxd Speak isn't running";
  });
}
show();
setTimeout(show, 800);   // the first check may be the one that starts the link
