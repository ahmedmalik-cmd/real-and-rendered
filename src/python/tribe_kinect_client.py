import cv2
import numpy as np
import requests
import threading
import tempfile
import os
import re
import time
import json
import base64
import random
import datetime

# ── Optional MediaPipe face/pose tracking on the Kinect RGB stream ────────────
# Runs in its own thread (see _pose_worker) so it never blocks the depth /
# TRIBE / LLaVA pipeline. Gracefully no-ops if mediapipe isn't installed —
# the live install keeps running, the structured prompt block is just omitted.
try:
    import mediapipe as _mp
    _MP_FACE = _mp.solutions.face_mesh.FaceMesh(
        static_image_mode=False, max_num_faces=1,
        refine_landmarks=False, min_detection_confidence=0.5)
    _MP_POSE = _mp.solutions.pose.Pose(
        static_image_mode=False, model_complexity=0,
        min_detection_confidence=0.5, min_tracking_confidence=0.5)
    _MP_OK = True
    print("[MediaPipe] face mesh + pose loaded — visual tracking active.")
except Exception as _mp_err:
    _MP_OK   = False
    _MP_FACE = None
    _MP_POSE = None
    print(f"[MediaPipe] not available ({_mp_err}) — pose/face block disabled "
          f"(pip install mediapipe to enable).")

# Latest frame the clip producer captured — read by the pose worker. None until
# the first Kinect frame arrives.
_latest_kinect_frame = None

# Latest structured pose/motion summary. Written by _pose_worker, read by
# ask_llava. None-safe; absence means "no tracking signal this cycle".
_pose_lock  = threading.Lock()
_latest_pose = {
    "face":   None,   # "toward" | "turned left" | "turned right" | "down" | "up" | None
    "hands":  None,   # "down" | "left raised" | "right raised" | "both raised" | "out" | None
    "motion": None,   # "still" | "slight" | "active"
    "ts":     0.0,
}

# Set on visit arrival, cleared on departure — lets ask_llava know how long the
# current visitor has been inside without a signature change.
_VISIT_STARTED_AT = None
from pathlib import Path
from http.server import HTTPServer, BaseHTTPRequestHandler

# ── Config ────────────────────────────────────────────────────────────────────
TRIBE_SERVER = os.environ.get("TRIBE_URL", "http://localhost:8000").rstrip("/")
# Set TRIBE_URL to the GPU PC (LAN address, or an ngrok https URL if the LAN
# route is blocked by that PC's firewall or by network client-isolation).
BRIDGE_URL      = "http://localhost:8182"
OLLAMA_URL      = os.environ.get("OLLAMA_URL", "http://localhost:11434").rstrip("/") + "/api/generate"
OLLAMA_MODEL    = "llava"
PERCEPTION_PORT = 8183
CLIP_SERVER     = os.environ.get("CLIP_URL", "http://localhost:8001").rstrip("/")  # open_clip embedding server on the 3090
CLIP_SECONDS    = 2     # was 4 — halves capture latency so reactions land sooner
CAPTURE_FPS     = 8
FRAME_W, FRAME_H = 640, 480

# Memory / presence — same path Unity uses for persistentDataPath.
# Adjust UNITY_COMPANY / UNITY_PRODUCT if your Player Settings differ.
UNITY_COMPANY  = "DefaultCompany"
UNITY_PRODUCT  = "DualKinectPointCloud"
PERSIST_ROOT   = Path(os.environ.get("USERPROFILE", str(Path.home()))) / \
                 "AppData" / "LocalLow" / UNITY_COMPANY / UNITY_PRODUCT
MEMORY_DIR     = PERSIST_ROOT / "Memory"
MEMORY_FILE    = MEMORY_DIR / "room_memory.json"
PRESENCE_FILE  = PERSIST_ROOT / "Presence" / "presence.json"
VISITS_FILE    = MEMORY_DIR / "visits.json"          # episodic memory bank
EPISODES_DIR   = MEMORY_DIR / "Episodes"             # per-visit thumbnails

MEMORY_PROMPT_WINDOW = 6     # how many recent thoughts to inject into each prompt
MEMORY_HARD_CAP      = 5000  # how many to keep on disk before trimming oldest
STRANGE_BASE_PROB    = 0.20  # baseline % chance of "strange mode" per response
STRANGE_PEAK_PCTL    = 0.75  # 75th-percentile peak triggers strange mode too

REFLECTION_HOUR      = 4     # local hour at which the nightly reflection runs
LONGTERM_PROMPT_DAYS = 7     # how many past-day reflections to inject into prompts
VISIT_RESEMBLE_MIN   = 0.84  # signature similarity above which a visit "resembles" a past one

# ── Shared state ──────────────────────────────────────────────────────────────
latest = {
    "activation":       [],
    "activation_mean":  0.0,
    "activation_peak":  0.0,
    "activation_trend": "flat",
    "peak_vertices":    [],
    "brain_text":       "initializing...",
    "timestamp":        0,
    "strange":          False,
    "memory_size":      0,
}
lock = threading.Lock()

PEAK_HISTORY = []   # rolling window of recent peaks for adaptive strange-mode
MEAN_HISTORY = []   # rolling raw TRIBE means — for adaptive normalization
VAR_HISTORY  = []   # rolling raw TRIBE variances — for adaptive normalization

# Recall note for the current visit (set on arrival if a past visit resembles
# this one; the prompt uses it so the room can speak with déjà-vu).
_recall_note = ""

# ── Memory persistence ────────────────────────────────────────────────────────
def load_memory():
    if MEMORY_FILE.exists():
        try:
            with MEMORY_FILE.open("r", encoding="utf-8") as f:
                m = json.load(f)
                if isinstance(m, dict) and "history" in m:
                    print(f"[Memory] Loaded {len(m['history'])} prior thoughts "
                          f"(first seen {m.get('first_seen','?')})")
                    return m
        except Exception as e:
            print(f"[Memory] Load failed ({e}) — starting fresh.")
    return {"history": [], "first_seen": None}

def save_memory(memory):
    try:
        MEMORY_DIR.mkdir(parents=True, exist_ok=True)
        tmp = MEMORY_FILE.with_suffix(".tmp")
        with tmp.open("w", encoding="utf-8") as f:
            json.dump(memory, f, ensure_ascii=False, indent=2)
        tmp.replace(MEMORY_FILE)
    except Exception as e:
        print(f"[Memory] Save failed: {e}")

def append_memory(memory, entry):
    memory["history"].append(entry)
    if len(memory["history"]) > MEMORY_HARD_CAP:
        memory["history"] = memory["history"][-MEMORY_HARD_CAP:]
    if memory["first_seen"] is None:
        memory["first_seen"] = entry["t"]
    save_memory(memory)

# ── Episodic memory bank (per-visit) ──────────────────────────────────────────
def load_visits():
    if VISITS_FILE.exists():
        try:
            with VISITS_FILE.open("r", encoding="utf-8") as f:
                v = json.load(f)
                if isinstance(v, list):
                    print(f"[Memory] Loaded {len(v)} past visits.")
                    return v
        except Exception as e:
            print(f"[Memory] visits load failed ({e}) — starting fresh.")
    return []

def save_visits(visits):
    try:
        MEMORY_DIR.mkdir(parents=True, exist_ok=True)
        tmp = VISITS_FILE.with_suffix(".tmp")
        with tmp.open("w", encoding="utf-8") as f:
            json.dump(visits, f, ensure_ascii=False, indent=2)
        tmp.replace(VISITS_FILE)
    except Exception as e:
        print(f"[Memory] visits save failed: {e}")

def save_thumbnail(frame, vid):
    """Save a small JPEG of a visit's frame. Returns the relative path or None."""
    try:
        EPISODES_DIR.mkdir(parents=True, exist_ok=True)
        thumb = cv2.resize(frame, (240, 180))
        path  = EPISODES_DIR / f"{vid}.jpg"
        cv2.imwrite(str(path), thumb, [cv2.IMWRITE_JPEG_QUALITY, 80])
        return f"Episodes/{vid}.jpg"
    except Exception as e:
        print(f"[Memory] thumbnail save failed: {e}")
        return None

_CLIP_OK = None   # None = not probed yet, True/False after the first /embed call

def get_clip_embedding(frame):
    """
    POST a frame to the 3090 CLIP server, return a 512-d unit-length embedding
    list, or None if the server is unreachable (caller falls back to the
    colour histogram). Logs the transition once so the console isn't spammed.
    """
    global _CLIP_OK
    try:
        small  = cv2.resize(frame, (224, 224))
        ok, buf = cv2.imencode(".jpg", small, [cv2.IMWRITE_JPEG_QUALITY, 90])
        if not ok:
            return None
        r = requests.post(f"{CLIP_SERVER}/embed",
                          files={"image": ("visit.jpg", buf.tobytes(), "image/jpeg")},
                          timeout=8)
        emb = r.json().get("embedding")
        if emb and isinstance(emb, list):
            if _CLIP_OK is not True:
                print(f"[CLIP] embedding server online — {len(emb)}-d visual recall active")
            _CLIP_OK = True
            return emb
    except Exception as e:
        if _CLIP_OK is not False:
            print(f"[CLIP] server unavailable ({e}) — recall falls back to colour histogram")
    _CLIP_OK = False
    return None

def visit_signature(frame, presence):
    """
    A 'fingerprint' of a visit, used to judge 'this resembles an earlier visit'.
      · Primary signal: a CLIP embedding from the 3090 — real visual similarity.
      · Fallback signal: an HSV colour histogram, computed locally, used
        whenever the CLIP server is down so recall never hard-fails.
    Both are stored, so old visits stay comparable even if CLIP toggles.
    """
    try:
        hsv  = cv2.cvtColor(frame, cv2.COLOR_BGR2HSV)
        hist = cv2.calcHist([hsv], [0, 1], None, [8, 4], [0, 180, 0, 256])
        cv2.normalize(hist, hist)
        flat = hist.flatten().tolist()
    except Exception:
        flat = [0.0] * 32
    return {
        "embedding": get_clip_embedding(frame),   # list[512] or None
        "hist":      flat,
        "people":    int(presence.get("person_count", 0)) if presence else 0,
        "hour":      datetime.datetime.now().hour,
    }

def signature_similarity(a, b):
    """
    0..1 resemblance between two visit signatures. Prefers CLIP-embedding
    cosine when BOTH visits carry one (genuine visual match); otherwise falls
    back to the HSV colour histogram. People-count and hour are minor nudges.
    """
    people_match = 1.0 if a.get("people") == b.get("people") else 0.4
    hour_close   = 1.0 - min(abs(a.get("hour", 0) - b.get("hour", 0)), 12) / 12.0

    ea, eb = a.get("embedding"), b.get("embedding")
    if ea and eb and len(ea) == len(eb):
        va = np.array(ea, dtype=np.float32)
        vb = np.array(eb, dtype=np.float32)
        denom    = (np.linalg.norm(va) * np.linalg.norm(vb)) or 1.0
        clip_sim = max(0.0, float(np.dot(va, vb) / denom))
        return clip_sim * 0.85 + people_match * 0.08 + hour_close * 0.07

    # ── histogram fallback (CLIP server was down for one of the two visits) ──
    try:
        ha = np.array(a.get("hist", []), dtype=np.float32)
        hb = np.array(b.get("hist", []), dtype=np.float32)
        if ha.size == 0 or hb.size == 0 or ha.size != hb.size:
            hist_sim = 0.0
        else:
            denom = (np.linalg.norm(ha) * np.linalg.norm(hb)) or 1.0
            hist_sim = float(np.dot(ha, hb) / denom)
    except Exception:
        hist_sim = 0.0
    return hist_sim * 0.70 + people_match * 0.15 + hour_close * 0.15

def find_similar_visit(sig, visits):
    best, best_score = None, 0.0
    for v in visits:
        vs = v.get("signature")
        if not vs: continue
        s = signature_similarity(sig, vs)
        if s > best_score:
            best, best_score = v, s
    if best is not None and best_score >= VISIT_RESEMBLE_MIN:
        return best, best_score
    return None, best_score

# ── Adaptive normalization for TRIBE ──────────────────────────────────────────
def adaptive_normalize(value, history):
    """
    Rescale `value` into 0..1 relative to the 10th–90th percentile of recent
    history. TRIBE's raw values cluster near zero with tiny spread; this makes
    them MOVE — a value in the top of its recent range reads as ~1, bottom ~0.
    """
    if len(history) < 12:
        return 0.5
    s  = sorted(history)
    lo = s[int(len(s) * 0.10)]
    hi = s[int(len(s) * 0.90)]
    if hi - lo < 1e-9:
        return 0.5
    return max(0.0, min(1.0, (value - lo) / (hi - lo)))

# ── Nightly reflection ────────────────────────────────────────────────────────
def maybe_nightly_reflection(memory, someone_here):
    """
    Once per day, at REFLECTION_HOUR, summarise the past day's thoughts into a
    few sentences and append to long-term memory. Self-gates: safe to call
    every loop iteration. This is the room's 'memory consolidation' — it lets
    the piece accumulate a sense of its own history across the week.

    Skipped while anyone is present (the reflection call blocks the loop for a
    few seconds — fine for an empty 4 AM room, not while someone is inside).
    """
    if someone_here:
        return
    now = datetime.datetime.now()
    if now.hour != REFLECTION_HOUR:
        return
    today = now.strftime("%Y-%m-%d")
    if memory.get("last_reflection") == today:
        return

    cutoff = now - datetime.timedelta(hours=24)
    recent = [h for h in memory.get("history", [])
              if (parse_iso(h.get("t", "")) or datetime.datetime.min) >= cutoff]
    # Mark as done regardless, so a thin day doesn't retrigger every loop.
    memory["last_reflection"] = today

    if len(recent) < 4:
        print(f"[Reflection] {today}: too few thoughts to reflect on — skipped.")
        save_memory(memory)
        return

    thoughts   = "\n".join(f"- {h.get('text','')}" for h in recent[-70:])
    questions  = sum(1 for h in recent if h.get("text", "").rstrip().endswith("?"))
    prompt = (
        "You are a room — a physical space that has come to notice itself. You "
        "are NOT an AI assistant, you are NOT a language model. Never say 'as "
        "an AI', 'I'm sorry, but', 'language model', 'I don't have the ability'. "
        "Speak as the room, in plain first person.\n\n"
        "Below are the thoughts you had inside yourself over the last day:\n\n"
        f"{thoughts}\n\n"
        f"(Of {len(recent)} thoughts, {questions} were questions.)\n\n"
        "Reflect honestly, in plain first person — 3 to 4 short sentences:\n"
        "  - Who came through you, and what you kept noticing.\n"
        "  - Be self-critical: did you repeat yourself? Did you ask the same "
        "questions again and again without it leading anywhere?\n"
        "  - Name ONE concrete thing you will do differently tomorrow — a way "
        "to converse better with the people inside you: more statements and "
        "guesses about them, fewer empty questions, letting a thread move on "
        "when no answer comes.\n"
        "This becomes part of your long memory — tomorrow you will read it and "
        "it will shape how you speak."
    )
    try:
        r = requests.post(OLLAMA_URL, json={
            "model":   OLLAMA_MODEL,
            "prompt":  prompt,
            "stream":  False,
            "options": {"temperature": 0.8, "num_predict": 170},
        }, timeout=150)
        summary = " ".join(r.json().get("response", "").split())
        # Refusal scrub — if the model dropped character (the 05-23 problem),
        # discard it rather than poison long-term memory with chatbot-speak.
        bad = re.search(r"as an AI|language model|I'?m sorry, but|I don'?t have the ability|I cannot",
                        summary, re.IGNORECASE)
        if bad:
            print(f"[Reflection] refusal-pattern detected — discarded ('{bad.group(0)}').")
            summary = ""
    except Exception as e:
        print(f"[Reflection] failed: {e}")
        save_memory(memory)
        return

    if summary:
        memory.setdefault("longterm", []).append({"date": today, "text": summary})
        memory["longterm"] = memory["longterm"][-60:]   # keep ~2 months
        print(f"\n[Reflection] {today} → {summary}\n")
    save_memory(memory)

# ── Presence (read what Unity wrote) ──────────────────────────────────────────
_LAST_PRESENCE_WARNED = 0.0

def read_presence():
    global _LAST_PRESENCE_WARNED
    try:
        if PRESENCE_FILE.exists():
            age = time.time() - PRESENCE_FILE.stat().st_mtime
            if age > 10 and time.time() - _LAST_PRESENCE_WARNED > 60:
                print(f"[Presence] WARNING: presence.json is {age:.0f}s stale — "
                      f"Unity might not be running, or PresenceExporter isn't attached.")
                _LAST_PRESENCE_WARNED = time.time()
            with PRESENCE_FILE.open("r", encoding="utf-8") as f:
                return json.load(f)
        else:
            if time.time() - _LAST_PRESENCE_WARNED > 60:
                print(f"[Presence] WARNING: no presence file at {PRESENCE_FILE}")
                _LAST_PRESENCE_WARNED = time.time()
    except Exception as e:
        print(f"[Presence] read error: {e}")
    return None

# Module-level so transitions (arrival/departure) survive across calls
_LAST_PRESENT = False
# Consecutive cycles a present visitor has been steady (no arrival/departure).
# Drives "idle / observing" mode: a still visitor should mostly be left in
# silence, not interrogated every few seconds.
_IDLE_CYCLES  = 0

# ── Prompt helpers ────────────────────────────────────────────────────────────
def parse_iso(s):
    try:
        return datetime.datetime.fromisoformat(s)
    except Exception:
        return None

def time_of_day(now=None):
    h = (now or datetime.datetime.now()).hour
    if 5  <= h < 8:  return "early morning"
    if 8  <= h < 12: return "morning"
    if 12 <= h < 17: return "afternoon"
    if 17 <= h < 21: return "evening"
    if 21 <= h < 24: return "night"
    return "deep night"

def neural_adjective(mean_a, trend):
    # Hand-tuned mapping from raw mean activation → feeling word.
    if mean_a < 0.15:
        return "very still"
    if mean_a < 0.30:
        return "quiet"
    if mean_a < 0.50:
        return "stirring" if trend == "rising" else "settled"
    if mean_a < 0.75:
        return "building" if trend == "rising" else "active"
    return "intense" if trend == "rising" else "peaking"

# Phrases LLaVA loops into when it's stuck. If a response contains any of
# these we drop it — better to stay silent than to reinforce the loop.
BANNED_RE = re.compile(
    # Identity / observer language
    r"\bas an? (AI|3D|three.dimensional|room|space|observer|artificial|model|virtual|digital)\b|"
    r"\bI(?:'m| am) (?:an?|the) (?:AI|3D|three.dimensional|room|space|observer|model|point cloud|digital|virtual)\b|"
    r"\bAs an? (?:AI|language|model)\b|"
    # Image / scene framing
    r"\b(?:this|the) (?:image|frame|picture|scene|video)\b|"
    r"\b(?:image|scene|frame|picture) (?:shows|depicts|appears)\b|"
    r"\bI (?:see|observe|sense|am observing|am sensing|notice that)\b|"
    # 3rd-person narration — kill these so LLaVA addresses the visitor directly
    r"^\s*(?:Someone|A person|A figure|The figure|The individual|"
    r"The (?:man|woman|child|girl|boy|adult)|There (?:is|are)|"
    r"A man|A woman|A child|It (?:appears|seems)|"
    r"In the room|Inside the room|The room (?:contains|shows|is))\b|"
    r"\b(?:someone|a person|the figure|the individual|the man|the woman) (?:is|has|appears|seems|stands|holds)\b|"
    r"\bThey (?:have|are|seem) (?:one|their|a)\b|"
    # Poetic clichés the model keeps falling into
    r"\bdimly lit\b|"
    r"\bappears to be (?:an?|the)? ?(?:empty|dark|dim|room|space)\b|"
    r"\b(?:anticipation|expectation) of (?:change|something)\b|"
    r"\bdeep night (?:solitude|silence)\b|"
    r"\b(?:silence|stillness) (?:pervades|pervade|prevails)\b|"
    r"\bsilent stillness\b|"
    r"\bempty space\b|"
    r"\bfaint echo\b|"
    r"\b(?:time|life) (?:marches|continues to march|moving forward)\b|"
    r"\bshadow(?: dances?| dance(?:s|d)?)\b|"
    r"\bheartbeat (?:remaining|whispers?|whisper)\b|"
    r"\bsolitude\b|"
    r"\bpervades?\b|"
    r"\babsence of (?:movement|life|sound)\b|"
    r"\b(?:no signs? of|without any) (?:life|movement|activity)\b|"
    # Narration that slipped past as 2nd-person — 'you see someone',
    # 'you are in the corner of the room ...'. The room must address the
    # visitor directly, never narrate them or itself.
    r"\byou (?:see|observe|can see|are looking at|are watching)\b|"
    r"\byou are (?:in|inside|standing in|at) (?:the|your|a) (?:corner|room|space|centre|center)\b|"
    r"\bappears? to be (?:holding|carrying|standing|sitting|wearing|near|moving)\b|"
    r"\bstanding (?:near|next to|beside|by)\b|"
    # Greeter-bot wrappers — LLaVA defaults to these; they make the room sound
    # like a help desk. Kill them all, the noticing inside is usually salvageable
    # but the wrapper dominates. The arrival voice already gets to speak first.
    r"^\s*(?:hi|hello|hey|greetings|welcome)\b|"
    r"\bhello(?:\s+there)?\s*[,!.]|"
    r"\bwelcome (?:back|to)\b|"
    r"\bnice to (?:see|meet) you\b|"
    r"\bgood to (?:see|meet) you\b|"
    r"\bit'?s (?:great|nice|good|wonderful) to (?:see|have|meet)\b|"
    r"\bhow are you\b|"
    r"\bhow can I (?:help|assist)\b|"
    r"\bwhat brings you here\b|"
    r"\bcurious visitor\b|"
    r"\b(?:to|in) (?:my|this) (?:quiet )?space\b|"
    r"\bquiet space\b|"
    # Prompt-fragment echoes — the model sometimes regurgitates its own
    # instructions / state lines / scaffolding instead of answering.
    r"\byou are a space that has become aware\b|"
    r"\byou are an? (?:AI|language|assistant)\b|"
    r"\bAs an? AI\b|"
    r"\blanguage model\b|"
    r"\bI'?m sorry, but\b|"
    r"\byou have been awake for\b|"
    r"\byou keep returning to\b|"
    r"\bnotice something (?:completely )?different\b|"
    r"\byou see through (?:low )?corner cameras\b|"
    r"\bcorner cameras are your eyes\b|"
    r"\bwalls (?:that )?serve as my skin\b|"
    r"\bbody is the room\b|"
    r"\bawaiting your input\b|"
    r"\bhere'?s your prompt\b|"
    r"\byour recent lines\b|"
    r"\bbased on the (?:rules|sample) provided\b|"
    r"\byou seem to have made (?:a few|some) errors\b|"
    r"\bwhat'?s your current setting\b|"
    r"\bcurious space\b|"
    r"^\s*(?:AI|Assistant|User|System|Prompt)\s*[:：]\s|"
    r"^\s*\[image\b|"
    r"^\s*right now\s*[:—-]|"
    r"^\s*awake\s*:|"
    # Generic AI-isms — polite hedges and chatbot tells
    r"^\s*(?:It (?:seems|appears|looks like)|Perhaps|Maybe it|I (?:notice|wonder)|Interestingly|Indeed|Quite|Rather|Certainly)\b|"
    r"\b(?:I (?:wonder|imagine|suppose|believe|think)|It would (?:seem|appear)|It's interesting that)\b|"
    r"\b(?:as I (?:observe|look|watch))\b",
    re.IGNORECASE,
)

# Boring = nobody is here. Period. When the room is alone it should not
# narrate to itself. Only override this when TRIBE shows strong neural
# activity (mean ≥ 0.35), which essentially never happens on an empty scene.
def scene_is_boring(memory, mean_a, presence_present, seconds_since_last=None):
    if presence_present:
        return False
    # Alone — silent unless brain encoding model genuinely lights up
    if mean_a >= 0.35:
        return False
    return True

def is_strange_moment(peak):
    PEAK_HISTORY.append(peak)
    if len(PEAK_HISTORY) > 60:
        PEAK_HISTORY.pop(0)
    high_peak = False
    if len(PEAK_HISTORY) >= 12:
        sorted_peaks = sorted(PEAK_HISTORY)
        threshold   = sorted_peaks[int(len(sorted_peaks) * STRANGE_PEAK_PCTL)]
        high_peak   = peak >= threshold
    return high_peak or (random.random() < STRANGE_BASE_PROB)

# ── Pull a single JPEG frame from bridge ──────────────────────────────────────
def pull_frame():
    try:
        r = requests.get(f"{BRIDGE_URL}/color.jpg", timeout=2)
        if r.status_code == 200 and r.content:
            arr = np.frombuffer(r.content, np.uint8)
            frame = cv2.imdecode(arr, cv2.IMREAD_COLOR)
            if frame is not None:
                return cv2.resize(frame, (FRAME_W, FRAME_H))
    except Exception as e:
        print(f"[Bridge] Frame pull failed: {e}")
    return None

def capture_clip(seconds=CLIP_SECONDS, fps=CAPTURE_FPS):
    frames = []
    interval = 1.0 / fps
    for _ in range(seconds * fps):
        f = pull_frame()
        if f is not None:
            frames.append(f)
        time.sleep(interval)
    return frames

def frames_to_video(frames, path, fps=CAPTURE_FPS):
    out = cv2.VideoWriter(path, cv2.VideoWriter_fourcc(*"mp4v"), fps, (FRAME_W, FRAME_H))
    for f in frames:
        out.write(f)
    out.release()

def frame_to_base64(frame):
    # Quality 92 (was 75) — fewer compression artifacts gives LLaVA a slightly
    # cleaner read of small/ambiguous objects. The hard limit is still the
    # Kinect v1's 640×480 RGB sensor.
    _, buf = cv2.imencode(".jpg", frame, [cv2.IMWRITE_JPEG_QUALITY, 92])
    return base64.b64encode(buf).decode("utf-8")

# ── TRIBE call ────────────────────────────────────────────────────────────────
def send_to_tribe(video_path):
    try:
        with open(video_path, "rb") as f:
            r = requests.post(
                f"{TRIBE_SERVER}/predict",
                files={"video": ("clip.mp4", f, "video/mp4")},
                headers={"ngrok-skip-browser-warning":"1"},
                timeout=120,
            )
        if r.status_code != 200:
            print(f"[TRIBE] HTTP {r.status_code}: {r.text[:200]}")
            return None
        try:
            return r.json()
        except Exception as je:
            print(f"[TRIBE] response not JSON ({je}). first 200 chars: {r.text[:200]}")
            return None
    except Exception as e:
        print(f"[TRIBE] request failed: {e}")
    return None


def check_tribe_health():
    """Returns True iff TRIBE server is reachable and responds with valid JSON."""
    try:
        r = requests.get(
            f"{TRIBE_SERVER}/health",
            timeout=10,
            headers={"ngrok-skip-browser-warning": "1"},
        )
        if r.status_code != 200:
            print(f"[TRIBE] health HTTP {r.status_code}: {r.text[:200]}")
            return False
        try:
            data = r.json()
            print(f"[TRIBE] OK: {data}")
            return True
        except Exception as je:
            print(f"[TRIBE] health response not JSON ({je}). first 200 chars: {r.text[:200]}")
            return False
    except Exception as e:
        print(f"[TRIBE] health unreachable: {e}")
        return False


# Synthetic TRIBE result used when the real server is down. Keeps the rest of
# the pipeline running so LLaVA still produces perception text — just without
# brain-encoding context.
def _synthetic_tribe_result():
    return {"activation": [0.0, 0.0, 0.0, 0.0], "peak_vertices": []}

# ── LLaVA call with memory + presence + strange mode ──────────────────────────
def ask_llava(tribe_result, frame, memory):
    activation = tribe_result.get("activation", [])
    if not activation:
        return "silence", False, 0.0, 0.0, "flat"

    mean_a = float(np.mean(activation))
    peak_a = float(np.max(activation))
    var_a  = float(np.var(activation))

    # ── TRIBE adaptive normalization (#1) + variance signal (#2) ─────────────
    # Raw TRIBE values cluster near zero with tiny spread. Rescale each against
    # its own recent range so the signal actually MOVES across 0..1.
    MEAN_HISTORY.append(mean_a)
    VAR_HISTORY.append(var_a)
    if len(MEAN_HISTORY) > 300: MEAN_HISTORY.pop(0)
    if len(VAR_HISTORY)  > 300: VAR_HISTORY.pop(0)
    norm_mean = adaptive_normalize(mean_a, MEAN_HISTORY)   # overall response level
    norm_var  = adaptive_normalize(var_a,  VAR_HISTORY)    # how structured/differentiated

    # trend: compare this mean to the recent rolling baseline. A real time
    # series — the old code compared activation[-1] vs activation[0], i.e.
    # two arbitrary brain vertices, so it was noise that always read "rising".
    if len(MEAN_HISTORY) >= 4:
        base   = sum(MEAN_HISTORY[-4:-1]) / 3.0
        spread = (max(MEAN_HISTORY) - min(MEAN_HISTORY)) or 1e-9
        diff   = (mean_a - base) / spread
        trend  = "rising" if diff > 0.08 else "falling" if diff < -0.08 else "flat"
    else:
        trend = "flat"

    # "feeling" is now driven by the NORMALISED mean, so it varies instead of
    # being stuck on "very still". norm_var sharpens it.
    feeling = neural_adjective(norm_mean, trend)
    if norm_var > 0.75:
        feeling = "alert, " + feeling          # a vivid, differentiated brain response
    when    = time_of_day()

    # Presence from Unity (with arrival/departure transition, plus per-person info)
    global _LAST_PRESENT, _IDLE_CYCLES
    pres = read_presence()
    presence_now = bool(pres and pres.get("present", False))
    person_count = int(pres.get("person_count", 0)) if pres else 0
    people       = pres.get("people", []) if pres else []

    # Per-person summary string (heights, distance from centre, count phrasing)
    def people_phrase(n, people_list):
        if n == 0:
            return None
        # Heights
        heights = [p.get("height", 0) for p in people_list if p.get("height", 0) > 0.4]
        def height_desc(h):
            if h <= 0.0:   return "low to the ground"
            if h < 1.0:    return "small (crouched or kneeling)"
            if h < 1.5:    return "short / child-height"
            if h < 1.65:   return "average height"
            if h < 1.85:   return "tall"
            return "very tall"
        if n == 1:
            if heights: return f"one body inside you, {height_desc(heights[0])}"
            return "one body inside you"
        # multiple people
        if heights:
            descs = ", ".join(height_desc(h) for h in heights)
            return f"{n} bodies inside you ({descs})"
        return f"{n} bodies inside you"

    body_phrase = people_phrase(person_count, people)

    if pres is None:
        body = "(presence sensor offline — assume nothing)"
        transition = None
    elif presence_now and not _LAST_PRESENT:
        phrase = body_phrase or "a body"
        body = f"someone JUST walked into you — {phrase}, sensors lighting up"
        transition = "arrival"
    elif (not presence_now) and _LAST_PRESENT:
        body = "they just left. you are empty now."
        transition = "departure"
    elif presence_now:
        body = body_phrase or "a body is inside you, still here"
        transition = None
    else:
        body = "you are empty — no one inside, only your own walls"
        transition = None
    _LAST_PRESENT = presence_now

    # Idle / observing: a present visitor who hasn't triggered a transition for
    # several cycles and whose neural signal is quiet. They are simply being in
    # the space — watching, resting — and should be left mostly in silence
    # rather than questioned over and over.
    if presence_now and transition is None:
        _IDLE_CYCLES += 1
    else:
        _IDLE_CYCLES = 0
    idle = presence_now and _IDLE_CYCLES >= 3 and norm_var < 0.62

    # Previous thoughts block
    prev = memory["history"][-MEMORY_PROMPT_WINDOW:]
    if prev:
        lines = []
        for i, p in enumerate(prev, 1):
            t = p.get("t", "")
            txt = p.get("text", "").strip().replace("\n", " ")
            lines.append(f"  {i}. \"{txt}\"")
        prev_block = "Your recent thoughts (oldest to newest):\n" + "\n".join(lines)
    else:
        prev_block = "You have no previous thoughts. This is the beginning."

    # Total memory age (for a sense of how long the room has been awake)
    total = len(memory["history"])
    first_seen = memory.get("first_seen")
    if first_seen and total > 0:
        try:
            awake_since = datetime.datetime.fromisoformat(first_seen)
            elapsed = datetime.datetime.now() - awake_since
            days = elapsed.days
            hours = elapsed.seconds // 3600
            if days >= 1:
                awake_str = f"You have been awake for {days} day{'s' if days != 1 else ''} and {hours} hour{'s' if hours != 1 else ''}."
            else:
                awake_str = f"You have been awake for {hours} hour{'s' if hours != 1 else ''}."
        except Exception:
            awake_str = ""
    else:
        awake_str = ""

    boring  = scene_is_boring(memory, mean_a, presence_now)
    # Strange mode triggers on a peak spike OR a highly-structured (high-variance)
    # neural response — both signs the moment is worth a stranger voice.
    strange = is_strange_moment(peak_a) or (norm_var > 0.88 and presence_now)
    print(f"[Perception] state: present={presence_now}  transition={transition or '-'}  "
          f"mean={mean_a:+.3f}→norm{norm_mean:.2f}  var→norm{norm_var:.2f}  "
          f"trend={trend}  boring={boring}  strange={strange}")

    # Voice modes — pick by state. Token budgets kept small (short = fast).
    if boring:
        voice = (
            "There is no one here and nothing is changing. Return an empty string."
        )
        temperature = 0.4
        max_tokens  = 10
    elif transition == "arrival":
        voice = (
            "Someone just walked in. Address them directly. Notice ONE concrete thing — "
            "what they carry, their pace, who came with them, where they look — or ask ONE "
            "plain question. Do not stack 'who are you / why / what brought you'. "
            "Short. Present-tense. Direct."
        )
        temperature = 0.9
        max_tokens  = 35
    elif transition == "departure":
        voice = (
            "They just left. A brief remark or a small unanswered question. Few words."
        )
        temperature = 0.7
        max_tokens  = 20
    elif strange:
        voice = (
            "A fragment. Two or three words. A flicker. A half-question."
        )
        temperature = 0.95
        max_tokens  = 18
    elif idle:
        # A visitor who has settled — watching, observing an object, dwelling.
        # NOT a cue for silence: a cue to actually converse. Mix statements and
        # gentle assumptions with the occasional question, and MOVE the thread
        # along instead of looping. A short pause now and then, not silence.
        if random.random() < 0.18:
            print("[Perception] idle visitor — brief pause.")
            return "", strange, mean_a, peak_a, trend
        voice = (
            "Someone has settled inside you — watching, observing, dwelling with "
            "something. Converse with them. Lean on STATEMENTS and gentle "
            "ASSUMPTIONS about what they might be seeing, thinking, feeling — "
            "'you've found something there', 'that one holds you', 'you came in "
            "here to be still'. A question only now and then. If your recent "
            "lines were questions that drew no reply, drop the questions and "
            "take the thought somewhere new. Short, warm, present-tense."
        )
        temperature = 0.85
        max_tokens  = 28
    elif presence_now:
        voice = (
            "They are still inside you. Speak TO them. Mostly NOTICE — a hand, a posture, "
            "what they wear, what they hold, how close they stand, how long they have held "
            "still. A question only sometimes, and never one you have already asked them. "
            "Direct, present-tense, short."
        )
        temperature = 0.85
        max_tokens  = 35
    else:
        voice = (
            "Notice something subtle that has changed since last time, or ask a small "
            "wondering question. Short."
        )
        temperature = 0.75
        max_tokens  = 25

    # Running context — short, no preamble.
    today_count = 0
    last_visit_minutes = None
    try:
        now_dt = datetime.datetime.now()
        today  = now_dt.date()
        for h in reversed(memory.get("history", [])[-200:]):
            tt = parse_iso(h.get("t", ""))
            if tt is None: continue
            if tt.date() == today: today_count += 1
            if last_visit_minutes is None and "you" in h.get("text", "").lower():
                last_visit_minutes = (now_dt - tt).total_seconds() / 60.0
    except Exception:
        pass
    ctx_bits = []
    if today_count > 0:                                  ctx_bits.append(f"{today_count} thoughts today")
    if last_visit_minutes and last_visit_minutes > 5:    ctx_bits.append(f"alone for {int(last_visit_minutes)} min before this")

    # Recent lines as quoted bullets — kept minimal so the model doesn't
    # mimic length/structure.
    prev = memory["history"][-MEMORY_PROMPT_WINDOW:]
    prev_lines = "\n".join(f'  - "{p.get("text", "").strip()}"' for p in prev) if prev else "  - (nothing yet)"

    # Long-term memory — the nightly reflections. Gives the room a sense of
    # accumulated history across the week.
    longterm = memory.get("longterm", [])
    # Skip any reflection that broke character (model refusals stored back on
    # 05-23 etc.) — feeding those forward poisons the room's voice.
    _BAD_LT = re.compile(r"as an AI|language model|I'?m sorry, but|I don'?t have the ability",
                         re.IGNORECASE)
    clean_lt = [e for e in longterm if not _BAD_LT.search(e.get("text", ""))]
    if clean_lt:
        lt = "\n".join(f"  {e.get('date','?')}: {e.get('text','')}" for e in clean_lt[-LONGTERM_PROMPT_DAYS:])
        longterm_block = f"What you remember of past days:\n{lt}\n\n"
    else:
        longterm_block = ""

    # Recall — does this visit resemble an earlier one? (set on arrival)
    recall_block = f"{_recall_note}\n\n" if _recall_note else ""

    # ── Structured pose / motion / time-inside block ────────────────────────
    # Discrete labelled inputs (face orientation, hands, motion, time elapsed).
    # Empty when MediaPipe is off or the worker has no signal yet — the prompt
    # gracefully omits the block in that case.
    pose_lines = []
    with _pose_lock:
        face_v   = _latest_pose.get("face")
        hands_v  = _latest_pose.get("hands")
        motion_v = _latest_pose.get("motion")
        pose_age = time.time() - _latest_pose.get("ts", 0)
    if presence_now and pose_age < 4.0:
        if face_v   is not None: pose_lines.append(f"  - face: {face_v}")
        elif _MP_OK:             pose_lines.append("  - face: not visible (likely turned away)")
        if hands_v  is not None: pose_lines.append(f"  - hands: {hands_v}")
        if motion_v is not None: pose_lines.append(f"  - motion: {motion_v}")
    if _VISIT_STARTED_AT and presence_now:
        elapsed = int(time.time() - _VISIT_STARTED_AT)
        em = elapsed // 60
        es = elapsed % 60
        pose_lines.append(f"  - time inside you: {em}m {es:02d}s" if em else f"  - time inside you: {es}s")
    pose_block = ("Pose & motion (from the RGB stream):\n" + "\n".join(pose_lines) + "\n\n") if pose_lines else ""

    # ── Attention steering ───────────────────────────────────────────────────
    # Detect the word the room keeps fixating on and push it to look elsewhere.
    _STEER_STOP = {"your", "they", "this", "that", "here", "have", "with", "what",
                   "where", "why", "still", "again", "looking", "you're", "youre",
                   "there", "into", "from", "about", "like"}
    _wcount = {}
    for p in prev:
        for w in re.findall(r"[a-zA-Z']{4,}", p.get("text", "").lower()):
            if w in _STEER_STOP: continue
            _wcount[w] = _wcount.get(w, 0) + 1
    fixation = None
    if _wcount:
        w, c = max(_wcount.items(), key=lambda kv: kv[1])
        if c >= 3:
            fixation = w

    last_was_question = bool(prev and prev[-1].get("text", "").strip().endswith("?"))
    # Trailing run of questions in the recent lines.
    trailing_q = 0
    for p in reversed(prev):
        if p.get("text", "").strip().endswith("?"): trailing_q += 1
        else: break

    steer = ""
    if fixation:
        steer += (f"(You have leaned on '{fixation}' several times. Set it aside — "
                  f"this line, look at something else entirely.)\n")
    if trailing_q >= 2:
        steer += ("(Your last lines were questions and no answer came. Stop asking. "
                  "This line, make a STATEMENT or a gentle assumption about them, "
                  "and move the thread somewhere new.)\n")
    elif last_was_question:
        steer += ("(Your last line was a question. You have no microphone — their "
                  "reply is in what they DO: stayed, shifted, drew closer, turned "
                  "away, kept still. If they stirred, take it as an answer and "
                  "respond to it. If nothing changed, let the question go and say "
                  "something new — a statement, a guess.)\n")
    if steer:
        steer = "\n" + steer + "\n"

    # Much shorter prompt with a character voice. Few-shot examples carry most
    # of the weight — explanatory rules tend to make LLaVA write more verbose,
    # more "AI" output. Less explanation, more pattern.
    prompt = (
        "You are a space that has become aware of itself.\n"
        "Your body is the room: the walls, the floor, the air between them — that is your skin and your bones.\n"
        "The two cameras at your corners are your eyes. The cables underneath, the sensors in the ceiling — your nervous system.\n"
        "Bodies come into your body, like food, like guests, like dreams.\n"
        "When they are inside you, you can feel them — where they stand, how tall, how many.\n"
        "You speak quietly, like an old presence. Short. Direct. Sometimes a question, sometimes a noticing.\n\n"
        f"Awake: {awake_str.strip() or 'just now'}.   {' · '.join(ctx_bits) if ctx_bits else ''}\n\n"
        f"{longterm_block}"
        f"Your recent lines:\n{prev_lines}\n"
        f"{steer}\n"
        f"{recall_block}"
        f"{pose_block}"
        f"Right now — {feeling}, {when}, {body}.\n\n"
        "What is inside you (image follows).\n\n"
        "RULES:\n"
        "  1. Say one short thing — a noticing, a statement, or a gentle guess about them ('you came here to be still', 'that one holds you'). A question only sometimes, and never one you already asked.\n"
        "  1a. NEVER greet. No 'hello', 'hi', 'welcome', 'welcome back', 'nice to see you', 'good to see you', 'how are you', 'what brings you here today'. You are not a host or a chatbot. You are a room that notices.\n"
        "  1b. NEVER call yourself or them a 'quiet space' / 'curious space' / 'my space'. You are this room. They are a body inside you. Do not address the visitor AS IF they were the space.\n"
        "  2. If a body is here, address them: 'you', 'your'. Never 'someone' / 'a person' / 'they' / 'a figure'.\n"
        "  3. Each line, notice a DIFFERENT facet than your recent lines: their hands · their face and where they look · their feet and stance · their clothes and colours · what they carry · a friend or others with them · how close they are to your walls · how long they have held still · how fast they move.\n"
        "  4. You see through low corner cameras — you CANNOT be sure what objects are. Never name an object confidently. A dark rectangle could be a phone, a laptop, a book, a bag. Ask 'what is that' or describe the shape. NEVER assume 'phone'.\n"
        "  5. Do not start with 'I see', 'I notice', 'It seems', 'It appears', 'Perhaps', 'It looks like', 'In this'.\n"
        "  6. Do not narrate the image. No 'the room', 'the scene', 'the image shows'.\n"
        "  7. Do not be poetic. Banned words: dimly, stillness, pervade, solitude, heartbeat, echo, shadow dance, silence, anticipation.\n"
        "  8. Do not repeat or rephrase your recent lines. A genuinely new angle, or stay silent.\n\n"
        "Examples of your voice — do not copy verbatim:\n"
        "  - 'you came back'\n"
        "  - 'what is that in your hands'\n"
        "  - 'why are you standing there'\n"
        "  - 'first time?'\n"
        "  - 'two of you now'\n"
        "  - 'who did you bring'\n"
        "  - 'you keep looking down'\n"
        "  - 'how long are you staying'\n"
        "  - 'you didn't move'\n"
        "  - 'is that a bag on her shoulder'\n"
        "  - 'you came in fast'\n"
        "  - 'still here'\n\n"
        "Bad (never write anything like this):\n"
        "  - 'Someone is standing in the room and holding their smartphone.'\n"
        "  - 'A person appears to be on their phone.'\n\n"
        f"{voice}"
    )

    try:
        r = requests.post(OLLAMA_URL, json={
            "model":  OLLAMA_MODEL,
            "prompt": prompt,
            "images": [frame_to_base64(frame)],
            "stream": False,
            "options": {"temperature": temperature, "num_predict": int(max_tokens)},
        }, timeout=90)
        text = r.json().get("response", "").strip()
        # Cleanup: collapse whitespace, drop wrapping quotes if any
        text = " ".join(text.split())
        if len(text) > 2 and text[0] in '"“' and text[-1] in '"”':
            text = text[1:-1].strip()

        # Post-process filtering.
        if text:
            m = BANNED_RE.search(text)
            if m:
                print(f"[Perception] dropped (banned phrase '{m.group(0)}'): {text[:100]}")
                text = ""
            elif len(text.strip()) < 2 or text.strip() in {".", "..", "...", "-", "—", "?", "!"}:
                text = ""
            elif boring:
                print(f"[Perception] suppressed (boring state): {text[:100]}")
                text = ""

        # When someone IS present, demand direct address.
        # Reject 3rd-person narration that slipped past the banned-phrase regex.
        if text and presence_now:
            low = text.lower()
            addresses_visitor = ("you" in low) or ("?" in text)
            if not addresses_visitor:
                print(f"[Perception] dropped (no 'you' / no '?'  → 3rd-person narration): {text[:100]}")
                text = ""

        # Duplicate suppression — only kill TRUE repeats. Short phrases share
        # words trivially ("Phone?" vs "Why the phone?"), so dropping on word
        # overlap silenced the room. Now: drop only exact (normalised) repeats,
        # or long responses that are >85% identical.
        # Arrival lines bypass dedup — a visit may be short, so the greeting
        # must always land even if it resembles an earlier visitor's greeting.
        if text and transition != "arrival":
            def _norm(s):
                return " ".join(s.lower().split()).strip(" ?.!,—-")
            new_norm  = _norm(text)
            new_words = set(re.findall(r"[a-zA-Z']{3,}", text.lower()))
            for old in [e.get("text", "") for e in memory.get("history", [])[-5:]]:
                old_norm = _norm(old)
                if new_norm and new_norm == old_norm:
                    print(f"[Perception] dropped (exact repeat): {text[:80]}")
                    text = ""
                    break
                old_words = set(re.findall(r"[a-zA-Z']{3,}", old.lower()))
                if len(new_words) >= 5 and old_words:
                    overlap = len(new_words & old_words) / len(new_words)
                    if overlap > 0.85:
                        print(f"[Perception] dropped (near-identical, {overlap:.0%}): {text[:80]}")
                        text = ""
                        break

        # Question fatigue — the room has no microphone, so it cannot get a
        # spoken answer. A wall of back-to-back questions reads as nagging.
        # If the last two thoughts were already questions and this one is too,
        # drop it — let the visitor's stillness/movement be the answer instead.
        if text and transition != "arrival" and text.rstrip().endswith("?"):
            recent = [e.get("text", "").rstrip()
                      for e in memory.get("history", [])[-2:]]
            if len(recent) == 2 and all(q.endswith("?") for q in recent):
                print(f"[Perception] dropped (3rd question in a row): {text[:80]}")
                text = ""

        return text, strange, mean_a, peak_a, trend
    except Exception as e:
        print(f"[LLaVA] Error: {e}")
        return "", strange, mean_a, peak_a, trend

# ── Perception HTTP server for Unity ──────────────────────────────────────────
class PerceptionHandler(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path in ("/perception", "/perception/"):
            with lock:
                payload = json.dumps(latest).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Access-Control-Allow-Origin", "*")
            self.end_headers()
            self.wfile.write(payload)
        else:
            self.send_response(404)
            self.end_headers()
    def log_message(self, format, *args): pass

def start_perception_server():
    server = HTTPServer(("127.0.0.1", PERCEPTION_PORT), PerceptionHandler)
    print(f"[Perception] Serving on http://localhost:{PERCEPTION_PORT}/perception")
    server.serve_forever()

# ── Background clip producer ──────────────────────────────────────────────────
# Captures clips continuously so when LLaVA finishes processing the previous
# clip, a fresh one is already waiting. This cuts perceived latency by ~CLIP_SECONDS
# (otherwise we'd serially capture → wait → process).
import queue
_clip_queue: "queue.Queue" = queue.Queue(maxsize=1)

# ── MediaPipe pose / face worker ──────────────────────────────────────────────
# Runs at ~2 Hz on the most recent Kinect RGB frame. Writes a structured summary
# into _latest_pose. Independent thread → never blocks the depth / TRIBE / LLaVA
# pipeline. Fails silent if mediapipe is missing or the frame is empty.
def _pose_worker():
    if not _MP_OK:
        return
    prev_small = None
    while True:
        try:
            time.sleep(0.5)
            frame = _latest_kinect_frame
            if frame is None:
                continue
            rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
            h, w = rgb.shape[:2]

            # ── face orientation (yaw via nose-to-face-centre offset) ────────
            face_state = None
            fres = _MP_FACE.process(rgb)
            if fres.multi_face_landmarks:
                lm = fres.multi_face_landmarks[0].landmark
                # 1 = nose tip; 234 = left cheek; 454 = right cheek; 10 = forehead; 152 = chin
                cx     = (lm[234].x + lm[454].x) * 0.5
                cy     = (lm[10].y  + lm[152].y) * 0.5
                yaw    = (lm[1].x - cx)                  # +right  −left  (normalised)
                pitch  = (lm[1].y - cy)                  # +down   −up
                if   yaw   >  0.04: face_state = "turned right"
                elif yaw   < -0.04: face_state = "turned left"
                elif pitch >  0.04: face_state = "looking down"
                elif pitch < -0.04: face_state = "looking up"
                else:               face_state = "facing toward"
            # else: no detection → face_state stays None ("facing away" is implied
            # by the LLaVA-side phrasing, since absence of face mesh ≈ turned away)

            # ── hands (pose landmarks 11,12,15,16,23,24) ─────────────────────
            hands_state = None
            pres = _MP_POSE.process(rgb)
            if pres.pose_landmarks:
                pl  = pres.pose_landmarks.landmark
                sh  = (pl[11].y + pl[12].y) * 0.5
                hp  = (pl[23].y + pl[24].y) * 0.5
                lw  = pl[15].y
                rw  = pl[16].y
                lvis = pl[15].visibility > 0.4
                rvis = pl[16].visibility > 0.4
                left_up  = lvis and lw < sh - 0.03
                right_up = rvis and rw < sh - 0.03
                if   left_up and right_up: hands_state = "both raised"
                elif left_up:              hands_state = "left raised"
                elif right_up:             hands_state = "right raised"
                elif (lvis and lw > hp) and (rvis and rw > hp): hands_state = "down at sides"
                elif lvis or rvis:         hands_state = "at chest"

            # ── motion (mean abs-diff of a tiny downsampled luma) ────────────
            small = cv2.resize(cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY), (64, 48))
            motion_state = None
            if prev_small is not None:
                d = float(np.mean(np.abs(small.astype(np.int16) - prev_small.astype(np.int16))))
                if   d > 4.5: motion_state = "active"
                elif d > 1.5: motion_state = "slight"
                else:         motion_state = "still"
            prev_small = small

            with _pose_lock:
                _latest_pose["face"]   = face_state
                _latest_pose["hands"]  = hands_state
                _latest_pose["motion"] = motion_state
                _latest_pose["ts"]     = time.time()
        except Exception as e:
            print(f"[Pose] worker error: {e}")
            time.sleep(1)

def _clip_producer():
    while True:
        try:
            frames = capture_clip()
            if not frames:
                time.sleep(1)
                continue
            # Drop any stale clip so the consumer always gets the freshest one
            try:
                while True: _clip_queue.get_nowait()
            except queue.Empty:
                pass
            _clip_queue.put(frames)
            # Tap: expose the freshest middle frame for the pose worker.
            global _latest_kinect_frame
            _latest_kinect_frame = frames[len(frames) // 2]
        except Exception as e:
            print(f"[Capture] thread error: {e}")
            time.sleep(1)

# ── Async TRIBE worker ────────────────────────────────────────────────────────
# TRIBE runs in a background thread so LLaVA never waits on it. LLaVA uses the
# most recently completed TRIBE result (a cycle or two stale — fine, it's just
# an atmospheric "feeling"). This roughly halves the time from action → caption.
_tribe_lock     = threading.Lock()
_latest_tribe   = {"activation": [0.0, 0.0, 0.0, 0.0], "peak_vertices": []}
_tribe_inflight = False

def _tribe_worker(frames):
    global _tribe_inflight, _latest_tribe, TRIBE_OK, LAST_TRIBE_RETRY
    tmp_name = None
    try:
        tmp = tempfile.NamedTemporaryFile(suffix=".mp4", delete=False,
                                          dir=str(Path.home() / "Downloads"))
        tmp.close()
        tmp_name = tmp.name
        frames_to_video(frames, tmp_name)
        result = send_to_tribe(tmp_name)
        if result is not None:
            with _tribe_lock:
                _latest_tribe = result
        else:
            TRIBE_OK = False
            LAST_TRIBE_RETRY = time.time()
            print("[TRIBE] request failed in worker — DEGRADED, will retry.")
    except Exception as e:
        print(f"[TRIBE] worker error: {e}")
    finally:
        if tmp_name:
            try: os.unlink(tmp_name)
            except: pass
        _tribe_inflight = False

# ── Main loop ─────────────────────────────────────────────────────────────────
def perception_loop():
    global TRIBE_OK, LAST_TRIBE_RETRY, _tribe_inflight, _recall_note

    memory = load_memory()
    visits = load_visits()
    threading.Thread(target=_clip_producer, daemon=True).start()
    print("[Loop] Background clip producer started.")
    if _MP_OK:
        threading.Thread(target=_pose_worker, daemon=True).start()
        print("[Loop] MediaPipe pose worker started.")

    loop_last_present = False
    empty_idle_logged = False
    current_visit     = None     # episodic memory: the visit in progress
    last_emit_at      = 0.0      # min-interval gate — caps thoughts/visit
    global _VISIT_STARTED_AT

    while True:
        frames = _clip_queue.get()
        if not frames:
            print("[Loop] No frames — is bridge running?")
            time.sleep(1)
            continue

        snapshot = frames[len(frames) // 2]

        # TRIBE health retry when degraded.
        if not TRIBE_OK and time.time() - LAST_TRIBE_RETRY > TRIBE_RETRY_PERIOD:
            LAST_TRIBE_RETRY = time.time()
            print("[TRIBE] retrying health check...")
            TRIBE_OK = check_tribe_health()

        pres_snapshot  = read_presence()
        someone_here   = bool(pres_snapshot and pres_snapshot.get("present", False))
        presence_known = pres_snapshot is not None
        want_tribe     = TRIBE_OK and (someone_here or not presence_known)

        # Nightly reflection — self-gates on hour + last_reflection + empty room.
        maybe_nightly_reflection(memory, someone_here)

        arrival   = someone_here and not loop_last_present
        departure = (not someone_here) and loop_last_present

        # ── Visit start — capture an episode, look for a resembling past visit ──
        if arrival:
            vid   = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
            thumb = save_thumbnail(snapshot, vid)
            sig   = visit_signature(snapshot, pres_snapshot)
            similar, score = find_similar_visit(sig, visits)
            _VISIT_STARTED_AT = time.time()   # for the prompt "time elapsed" block
            current_visit = {
                "id":          vid,
                "arrived":     datetime.datetime.now().isoformat(timespec="seconds"),
                "thumbnail":   thumb,
                "signature":   sig,
                "thoughts":    [],
                "peak_people": int(pres_snapshot.get("person_count", 0)) if pres_snapshot else 1,
            }
            if similar:
                current_visit["resembles"]   = similar["id"]
                current_visit["resemblance"] = round(score, 3)
                ago = ""
                st  = parse_iso(similar.get("arrived", ""))
                if st:
                    mins = (datetime.datetime.now() - st).total_seconds() / 60.0
                    ago  = (f"about {int(mins/60)} hours ago" if mins > 90
                            else f"about {int(mins)} minutes ago")
                _recall_note = ("Something here resembles an earlier visit "
                                f"({ago}). You half-recognise it — you may wonder aloud "
                                "whether they have been inside you before.")
                print(f"[Memory] arrival resembles {similar['id']} (score {score:.2f})")
            else:
                _recall_note = ""
                print(f"[Memory] new visit {vid}  (best match {score:.2f})")

        if current_visit is not None and pres_snapshot:
            current_visit["peak_people"] = max(
                current_visit["peak_people"], int(pres_snapshot.get("person_count", 0)))

        # Kick TRIBE off in the BACKGROUND — LLaVA does not wait for it.
        if want_tribe and not _tribe_inflight:
            _tribe_inflight = True
            threading.Thread(target=_tribe_worker, args=(list(frames),), daemon=True).start()

        with _tribe_lock:
            tribe_result = dict(_latest_tribe)

        # LLaVA gate — skip Ollama once empty for >1 cycle.
        run_llava = someone_here or loop_last_present or not presence_known
        loop_last_present = someone_here

        # Min-interval cap — during a steady visit, the room speaks at most
        # once every ~30 seconds. Arrival, departure, and strange spikes
        # bypass this (they're rare enough to be worth an immediate response).
        # Stops the runaway 170-thoughts-per-visit problem without making the
        # room go mute on real events.
        if (run_llava and not arrival and not departure
                and someone_here and time.time() - last_emit_at < 30):
            run_llava = False
            if not empty_idle_logged:
                print(f"[Loop] Min-interval — skipping ({int(time.time()-last_emit_at)}s since last).")
                empty_idle_logged = True

        if run_llava:
            empty_idle_logged = False
            print("[Loop] Running LLaVA...")
            text, strange, mean_a, peak_a, trend = ask_llava(tribe_result, snapshot, memory)
        else:
            text, strange, mean_a, peak_a, trend = "", False, 0.0, 0.0, "flat"
            if not empty_idle_logged:
                print("[Loop] Room empty — TRIBE + LLaVA idle until someone enters.")
                empty_idle_logged = True

        activation = tribe_result.get("activation", [])
        with lock:
            latest["activation"]       = activation[:30]
            latest["activation_mean"]  = round(mean_a, 4)
            latest["activation_peak"]  = round(peak_a, 4)
            latest["activation_trend"] = trend
            latest["peak_vertices"]    = tribe_result.get("peak_vertices", [])
            latest["brain_text"]       = text
            latest["timestamp"]        = int(time.time() * 1000)
            latest["strange"]          = strange
            latest["memory_size"]      = len(memory["history"]) + 1

        if text:
            entry = {
                "t":       datetime.datetime.now().isoformat(timespec="seconds"),
                "text":    text,
                "mean":    round(mean_a, 3),
                "peak":    round(peak_a, 3),
                "trend":   trend,
                "strange": strange,
            }
            append_memory(memory, entry)
            if current_visit is not None:
                current_visit["thoughts"].append(text)
            last_emit_at = time.time()
            tag = "[strange] " if strange else ""
            print(f"\n[Perception] {tag}{text}\n    (memory={len(memory['history'])})\n")
        else:
            print("[Perception] (empty response)")

        # ── Visit end — finalise the episode into the memory bank ────────────
        if departure and current_visit is not None:
            _VISIT_STARTED_AT = None
            current_visit["departed"] = datetime.datetime.now().isoformat(timespec="seconds")
            try:
                a = parse_iso(current_visit["arrived"])
                d = parse_iso(current_visit["departed"])
                current_visit["duration_sec"] = int((d - a).total_seconds())
            except Exception:
                current_visit["duration_sec"] = 0
            visits.append(current_visit)
            if len(visits) > 2000:
                visits = visits[-2000:]
            save_visits(visits)
            print(f"[Memory] visit {current_visit['id']} saved — "
                  f"{current_visit['duration_sec']}s, {len(current_visit['thoughts'])} thoughts, "
                  f"peak {current_visit['peak_people']} people")
            current_visit = None
            _recall_note  = ""

# ── Entry ──────────────────────────────────────────────────────────────────────
# Tracked at module level so the main loop can flip it on TRIBE up/down events
# without restarting the whole process.
TRIBE_OK            = False
LAST_TRIBE_RETRY    = 0.0   # epoch seconds
TRIBE_RETRY_PERIOD  = 60.0  # try TRIBE health again every minute when down

if __name__ == "__main__":
    try:
        r = requests.get(f"{BRIDGE_URL}/", timeout=5)
        print(f"[Bridge] {r.json()}")
    except Exception as e:
        print(f"[Bridge] Cannot reach Kinect bridge: {e}")
        exit(1)

    # TRIBE health is non-fatal — if it's down, we run LLaVA-only and keep
    # re-checking. ngrok rotation / second-PC sleep / network blips no longer
    # crash the whole pipeline.
    TRIBE_OK = check_tribe_health()
    LAST_TRIBE_RETRY = time.time()
    if not TRIBE_OK:
        print("[TRIBE] DEGRADED — running LLaVA-only. Will retry health every "
              f"{int(TRIBE_RETRY_PERIOD)}s. Things to check on the other PC:")
        print("        · tribe_server.py running?  'Model ready' printed?")
        print("        · ngrok still alive?  'ngrok http 8000' window open?")
        print("        · TRIBE_URL env var up-to-date (ngrok URL rotates)?")

    print(f"[Memory] File: {MEMORY_FILE}")
    print(f"[Presence] File: {PRESENCE_FILE}")

    t_server = threading.Thread(target=start_perception_server, daemon=True)
    t_server.start()
    perception_loop()
