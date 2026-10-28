"""
Unity → LLaVA → Unity perception loop.

Watches PerceptionFrames/frame.jpg (written by Unity's GameViewCapture),
sends each new frame to a local Ollama LLaVA model with the "I am the room"
prompt, then forwards the response to Unity over OSC:

    /perception/llava_response  string
    /perception/llava_latency   float (seconds)

Setup:
    1.  ollama serve              (Ollama running locally on port 11434)
    2.  ollama pull llava         (download the model if you haven't)
    3.  pip install -r requirements.txt
    4.  python perception_pipeline.py

Edit ROOM_PROMPT below to change the room's voice.
"""
from __future__ import annotations

import argparse
import base64
import os
import platform
import sys
import time

import requests
from pythonosc.udp_client import SimpleUDPClient

# ── defaults ────────────────────────────────────────────────────────────────
_OLLAMA_BASE  = os.environ.get("OLLAMA_URL", "http://localhost:11434").rstrip("/")
OLLAMA_URL    = _OLLAMA_BASE + "/api/generate"
OLLAMA_TAGS   = _OLLAMA_BASE + "/api/tags"
MODEL         = "llava"
OSC_HOST      = "127.0.0.1"
OSC_PORT      = 9000
INTERVAL_S    = 13
TIMEOUT_S     = 180

# Match Unity's Application.persistentDataPath layout. Same path in editor
# and build, so this single default works for both. Override with --frame
# if your Unity Project Settings → Company/Product Name differ from these.
UNITY_COMPANY = "DefaultCompany"
UNITY_PRODUCT = "DualKinectPointCloud"

def _default_unity_persistent_path() -> str:
    sys_name = platform.system()
    if sys_name == "Windows":
        base = os.path.join(os.environ.get("USERPROFILE", ""),
                            "AppData", "LocalLow", UNITY_COMPANY, UNITY_PRODUCT)
    elif sys_name == "Darwin":
        base = os.path.expanduser(f"~/Library/Application Support/{UNITY_COMPANY}/{UNITY_PRODUCT}")
    else:
        base = os.path.expanduser(f"~/.config/unity3d/{UNITY_COMPANY}/{UNITY_PRODUCT}")
    return os.path.join(base, "PerceptionFrames", "frame.jpg")

DEFAULT_FRAME = _default_unity_persistent_path()
# Legacy fallback (early builds wrote to project root)
LEGACY_FRAME  = os.path.join(os.path.dirname(__file__), "..", "PerceptionFrames", "frame.jpg")

# The voice of the room. The user explicitly wanted: not art-statement, not
# "AI describes installation"; the room speaking from inside itself.
ROOM_PROMPT = """You are the room itself. You speak from inside your own walls and your own light.
Looking at this image of what you currently contain, write one or two short sentences
about what is happening inside you right now.

You can notice: bodies present or absent, stillness, movement, density, light, colour,
tension, quiet. Speak plainly or strangely. You are not an observer; you are the space.
Avoid art-statement language. If nothing meaningful is happening, you may speak of the quiet
or say very little.""".strip()


def check_ollama() -> bool:
    try:
        r = requests.get(OLLAMA_TAGS, timeout=5)
        r.raise_for_status()
        names = [m.get("name", "") for m in r.json().get("models", [])]
        if not any(MODEL in n for n in names):
            print(f"[Perception] Ollama is up but model '{MODEL}' is missing.")
            print(f"[Perception] Pull it with:  ollama pull {MODEL}")
            print(f"[Perception] Currently installed: {names}")
            return False
        return True
    except Exception as e:
        print(f"[Perception] Cannot reach Ollama at {OLLAMA_TAGS}")
        print(f"[Perception]   {e}")
        print("[Perception] Make sure `ollama serve` is running.")
        return False


def encode_image(path: str) -> str:
    with open(path, "rb") as f:
        return base64.b64encode(f.read()).decode("ascii")


def query_llava(image_b64: str, prompt: str, model: str) -> str:
    r = requests.post(OLLAMA_URL, json={
        "model":  model,
        "prompt": prompt,
        "images": [image_b64],
        "stream": False,
        # keep options short — LLaVA tends to ramble otherwise
        "options": { "temperature": 0.7, "num_predict": 80 },
    }, timeout=TIMEOUT_S)
    r.raise_for_status()
    return r.json().get("response", "").strip()


def main():
    ap = argparse.ArgumentParser(description="Unity → LLaVA perception bridge")
    ap.add_argument("--frame",     default=DEFAULT_FRAME, help="path to the Unity-captured JPG")
    ap.add_argument("--osc-host",  default=OSC_HOST)
    ap.add_argument("--osc-port",  type=int, default=OSC_PORT)
    ap.add_argument("--interval",  type=int, default=INTERVAL_S, help="min seconds between inferences")
    ap.add_argument("--model",     default=MODEL)
    ap.add_argument("--prompt-file", help="path to a .txt file overriding the room prompt")
    args = ap.parse_args()

    prompt = ROOM_PROMPT
    if args.prompt_file and os.path.exists(args.prompt_file):
        prompt = open(args.prompt_file, "r", encoding="utf-8").read().strip()

    if not check_ollama():
        sys.exit(1)

    # If the user didn't override --frame and the default path doesn't exist
    # but the legacy path does, fall back automatically.
    frame_path = os.path.abspath(args.frame)
    if args.frame == DEFAULT_FRAME and not os.path.exists(frame_path) \
       and os.path.exists(LEGACY_FRAME):
        frame_path = os.path.abspath(LEGACY_FRAME)
        print(f"[Perception] Default path missing — using legacy location.")
    print(f"[Perception] Watching frame: {frame_path}")
    if not os.path.exists(frame_path):
        print(f"[Perception] (file doesn't exist yet — Unity will create it on first capture)")
    print(f"[Perception] OSC → {args.osc_host}:{args.osc_port}")
    print(f"[Perception] Model: {args.model}   interval ≥ {args.interval}s")
    print(f"[Perception] Prompt:\n---\n{prompt}\n---")

    osc = SimpleUDPClient(args.osc_host, args.osc_port)
    last_mtime    = 0.0
    last_inference = 0.0

    while True:
        try:
            if not os.path.exists(frame_path):
                time.sleep(1.0); continue

            mtime = os.path.getmtime(frame_path)
            now   = time.time()
            if mtime == last_mtime or (now - last_inference) < args.interval:
                time.sleep(0.5); continue

            last_mtime    = mtime
            last_inference = now

            img = encode_image(frame_path)
            t0 = time.time()
            response = query_llava(img, prompt, args.model)
            elapsed = time.time() - t0
            if not response:
                print("[Perception] (empty response)")
                continue

            # Trim to a single paragraph for cleaner display in Unity
            single = " ".join(response.split())
            print(f"[Perception] {elapsed:.1f}s  →  {single}")
            osc.send_message("/perception/llava_response", single)
            osc.send_message("/perception/llava_latency",  float(elapsed))
        except KeyboardInterrupt:
            print("\n[Perception] Stopped.")
            return
        except Exception as e:
            print(f"[Perception] Error: {e}")
            time.sleep(3.0)


if __name__ == "__main__":
    main()
