"""
pxd Speak - Chatterbox voice server.

Started by pxd Speak (never by hand) as:  python server.py --port 0 --parent-pid <pid>
Prints "READY <port> <device> <sample rate>" once the model is loaded and warmed up, then serves on 127.0.0.1 only:

  GET  /health            {"ok": true, "device": "cuda", "sr": 24000}
  POST /speak             JSON {"text": "...", "voice": "<path to a clip>" or "", "temperature": 0.8}
                          -> a stream of chunks, one per sentence: int32 sample count (little endian), then that
                             many float32 samples (mono, sr Hz). A newer /speak, or the client hanging up, stops it.

It quits by itself when pxd Speak exits.
"""
import argparse
import ctypes
import json
import os
import re
import struct
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import numpy as np
import torch

from chatterbox.tts_turbo import ChatterboxTurboTTS

LOCK = threading.Lock()
CURRENT = {"id": 0}
VOICES = {}          # clip path -> prepared conditionals
MODEL = None
DEFAULT_CONDS = None


def log(*a):
    print(*a, file=sys.stderr, flush=True)


def sentences(text, max_len=220):
    """Sentence-sized pieces (merged up to max_len) so the first one plays quickly and long text stays stable."""
    parts = [p.strip() for p in re.split(r"(?<=[.!?…])\s+", text) if p.strip()]
    out, cur = [], ""
    for p in parts:
        while len(p) > max_len:
            cut = p.rfind(" ", 0, max_len)
            if cut < max_len // 2:
                cut = max_len
            out.append(p[:cut].strip())
            p = p[cut:].strip()
        if cur and len(cur) + 1 + len(p) > max_len:
            out.append(cur)
            cur = p
        else:
            cur = (cur + " " + p).strip()
    if cur:
        out.append(cur)
    # The first sentence goes on its own so sound starts as soon as possible; the rest are merged for steady prosody.
    if len(out) and len(parts) > 1 and len(parts[0]) >= 12 and len(out[0]) > len(parts[0]) and out[0].startswith(parts[0]):
        out = [parts[0], out[0][len(parts[0]):].strip()] + out[1:]
    return out


def use_voice(path):
    if not path:
        MODEL.conds = DEFAULT_CONDS
        return
    if path not in VOICES:
        MODEL.prepare_conditionals(path)
        VOICES[path] = MODEL.conds
    MODEL.conds = VOICES[path]


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.0"   # stream until we close the connection

    def log_message(self, *a):
        pass

    def do_GET(self):
        if self.path != "/health":
            self.send_error(404)
            return
        body = json.dumps({"ok": True, "device": str(MODEL.device), "sr": MODEL.sr}).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(body)

    def do_POST(self):
        if self.path != "/speak":
            self.send_error(404)
            return
        try:
            req = json.loads(self.rfile.read(int(self.headers.get("Content-Length", "0"))) or b"{}")
        except Exception:
            self.send_error(400)
            return
        text = (req.get("text") or "").strip()
        voice = req.get("voice") or ""
        temperature = float(req.get("temperature", 0.8))
        CURRENT["id"] += 1
        my_id = CURRENT["id"]

        self.send_response(200)
        self.send_header("Content-Type", "application/octet-stream")
        self.end_headers()

        with LOCK:
            try:
                use_voice(voice)
            except Exception as e:
                log("voice failed:", e)
                msg = ("Couldn't use that voice clip: " + str(e)).encode("utf-8")
                try:
                    self.wfile.write(struct.pack("<i", -len(msg)) + msg)
                except OSError:
                    pass
                return
            for piece in sentences(text):
                if CURRENT["id"] != my_id:
                    return
                t = time.time()
                with torch.inference_mode():
                    wav = MODEL.generate(piece, temperature=temperature)
                audio = wav.squeeze(0).cpu().numpy().astype(np.float32)
                log(f"{len(piece)} chars -> {len(audio) / MODEL.sr:.1f}s audio in {time.time() - t:.2f}s")
                try:
                    self.wfile.write(struct.pack("<i", len(audio)))
                    self.wfile.write(audio.tobytes())
                    self.wfile.flush()
                except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
                    return


def watch_parent(pid):
    """Quit when pxd Speak does (it may be killed without a chance to stop us)."""
    SYNCHRONIZE = 0x00100000
    k32 = ctypes.windll.kernel32
    h = k32.OpenProcess(SYNCHRONIZE, False, pid)
    if not h:
        os._exit(0)
    k32.WaitForSingleObject(h, 0xFFFFFFFF)
    os._exit(0)


def main():
    global MODEL, DEFAULT_CONDS
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=0)
    ap.add_argument("--parent-pid", type=int, default=0)
    ap.add_argument("--cpu", action="store_true")
    args = ap.parse_args()

    if args.parent_pid:
        threading.Thread(target=watch_parent, args=(args.parent_pid,), daemon=True).start()

    device = "cuda" if torch.cuda.is_available() and not args.cpu else "cpu"
    t = time.time()
    MODEL = ChatterboxTurboTTS.from_pretrained(device=device)
    DEFAULT_CONDS = MODEL.conds
    with torch.inference_mode():
        MODEL.generate("Warming up.")   # first CUDA run compiles kernels; do it before the user waits on it
    log(f"loaded on {device} in {time.time() - t:.1f}s")

    server = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    print(f"READY {server.server_address[1]} {device} {MODEL.sr}", flush=True)
    server.serve_forever()


if __name__ == "__main__":
    main()
