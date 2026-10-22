"""
make_collage.py — build a short documentary video from the visit thumbnails
and the room's actual thoughts. ffmpeg-driven.

Two outputs:

  1. visit_collage.mp4
     A timelapse of every recorded visit. Each thumbnail fades in for 1.5 s,
     captioned with the timestamp + the most striking thought the room spoke
     during that visit + the dwell time.

  2. voice_collage.mp4
     A typewriter-style scroll of the 40 curated best thoughts in order,
     over a slow ken-burns of randomly picked thumbnails. Designed for
     gallery/portfolio looping. 1080p, ~3 minutes.

Run:
    python make_collage.py            # both videos
    python make_collage.py --voice    # voice_collage only
    python make_collage.py --visits   # visit_collage only

Requires:
    ffmpeg on PATH                    (free, https://ffmpeg.org/download.html)
    pip install pillow                (for caption rendering)

Output: Documentation/collage/*.mp4
"""
import os
import re
import json
import shutil
import argparse
import tempfile
import subprocess
from pathlib import Path
from datetime import datetime
from random import shuffle

try:
    from PIL import Image, ImageDraw, ImageFont
except ImportError:
    print("install Pillow first:  pip install pillow")
    raise SystemExit(1)

UNITY_COMPANY = "DefaultCompany"
UNITY_PRODUCT = "DualKinectPointCloud"
# Where the run's memory lives: $ROOM_MEMORY_DIR if set, otherwise Unity's persistentDataPath on the
# installation PC (%USERPROFILE%/AppData/LocalLow/<company>/<product>/Memory).
# Try it with the synthetic sample:  ROOM_MEMORY_DIR=examples/sample_memory python analysis/<script>.py
MEMORY_DIR = Path(os.environ.get("ROOM_MEMORY_DIR") or
                  Path(os.environ.get("USERPROFILE", str(Path.home()))) / "AppData" / "LocalLow"
                  / UNITY_COMPANY / UNITY_PRODUCT / "Memory")
REPO_OUT = Path(__file__).resolve().parent.parent / "out"     # generated files; git-ignored
MEMORY   = MEMORY_DIR / "room_memory.json"
VISITS   = MEMORY_DIR / "visits.json"
EPISODES = MEMORY_DIR / "Episodes"
OUT      = REPO_OUT / "collage"
OUT.mkdir(parents=True, exist_ok=True)

# Look
W, H        = 1920, 1080
BG          = (10, 10, 10)
INK         = (232, 230, 225)
AMBER       = (255, 184, 77)
DIM         = (136, 132, 121)
FONT_SERIF  = None   # found at runtime
FONT_MONO   = None

BAD_RE  = re.compile(r"\b(hello|welcome|nice to see|curious visitor|quiet space|"
                     r"my space|as an AI|language model|I'm sorry, but|AI:)\b",
                     re.IGNORECASE)
GOOD_RE = re.compile(r"(tall figure|dark rectangle|something dark|white tower|"
                     r"you came back|you'?re here again|two of you|who did you bring|"
                     r"you brought someone|you stood still|you stand alone|stillness|"
                     r"very still|you keep looking|in your hands|in the corner|"
                     r"walked in so fast|left hand|your hands|right here, now|"
                     r"you are awake|why am i awake)", re.IGNORECASE)


def find_fonts():
    """Find a serif and a monospace font on Windows. Fallback to PIL default."""
    global FONT_SERIF, FONT_MONO
    candidates_serif = [r"C:\Windows\Fonts\georgia.ttf",
                        r"C:\Windows\Fonts\cambria.ttc",
                        r"C:\Windows\Fonts\times.ttf",
                        "/System/Library/Fonts/Supplemental/Georgia.ttf",
                        "/usr/share/fonts/truetype/dejavu/DejaVuSerif.ttf"]
    candidates_mono  = [r"C:\Windows\Fonts\consola.ttf",
                        r"C:\Windows\Fonts\lucon.ttf",
                        "/System/Library/Fonts/Menlo.ttc",
                        "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf"]
    for p in candidates_serif:
        if Path(p).exists(): FONT_SERIF = p; break
    for p in candidates_mono:
        if Path(p).exists(): FONT_MONO = p; break


def font(size, kind="serif"):
    p = FONT_SERIF if kind == "serif" else FONT_MONO
    try:    return ImageFont.truetype(p, size) if p else ImageFont.load_default()
    except: return ImageFont.load_default()


def load_data():
    memory = json.loads(MEMORY.read_text("utf-8")) if MEMORY.exists() else {}
    visits = json.loads(VISITS.read_text("utf-8")) if VISITS.exists() else []
    if not isinstance(visits, list): visits = []
    return memory, visits


def parse(s):
    try: return datetime.fromisoformat(s)
    except: return None


def fmt_dur(sec):
    sec = int(sec or 0)
    h, rem = divmod(sec, 3600); m, s = divmod(rem, 60)
    if h: return f"{h}h {m}m"
    if m: return f"{m}m {s}s"
    return f"{s}s"


def best_thought(thoughts):
    """Pick the most striking thought from a visit: short, no bot wrappers."""
    if not thoughts: return None
    candidates = [t.strip() for t in thoughts
                  if t and 8 <= len(t) <= 100 and not BAD_RE.search(t)]
    # Prefer GOOD_RE matches
    good = [t for t in candidates if GOOD_RE.search(t)]
    if good: return good[0]
    if candidates: return candidates[0]
    return None


def wrap(text, font_obj, max_w):
    """Word-wrap text to fit max_w pixels using font_obj."""
    words = text.split()
    lines = []
    cur = ""
    for w in words:
        test = (cur + " " + w).strip()
        if font_obj.getlength(test) <= max_w:
            cur = test
        else:
            if cur: lines.append(cur)
            cur = w
    if cur: lines.append(cur)
    return lines


def render_visit_card(thumb_path, when_iso, dur, quote, out_path):
    """One frame in the visit collage: thumbnail + caption."""
    img = Image.new("RGB", (W, H), BG)
    draw = ImageDraw.Draw(img)

    # Thumbnail centred
    try:
        thumb = Image.open(thumb_path).convert("RGB")
    except Exception:
        thumb = None

    if thumb:
        # Fit into 1100×825 area centred upper-half
        max_w, max_h = 1100, 700
        tw, th = thumb.size
        scale = min(max_w / tw, max_h / th)
        nw, nh = int(tw * scale), int(th * scale)
        thumb = thumb.resize((nw, nh), Image.LANCZOS)
        img.paste(thumb, ((W - nw) // 2, 110))

    # Caption block under the image
    dt = parse(when_iso)
    when_str = dt.strftime("%-d %b %Y · %H:%M") if dt else when_iso
    f_meta = font(28, "mono"); f_quote = font(48, "serif"); f_dim = font(22, "mono")

    # When
    draw.text((W // 2 - f_meta.getlength(when_str) / 2, 860),
              when_str, fill=AMBER, font=f_meta)
    # Quote
    if quote:
        lines = wrap(f"“{quote}”", f_quote, W - 320)
        y = 910
        for ln in lines[:2]:
            draw.text((W // 2 - f_quote.getlength(ln) / 2, y),
                      ln, fill=INK, font=f_quote)
            y += 60
    # Dwell
    draw.text((W // 2 - f_dim.getlength(dur) / 2, H - 60),
              dur, fill=DIM, font=f_dim)

    img.save(out_path, "JPEG", quality=92)


def render_voice_card(thumb_path, quote, out_path, dim=True):
    """One frame in the voice collage: a single quote over a dimmed thumbnail."""
    img = Image.new("RGB", (W, H), BG)
    try:
        bg = Image.open(thumb_path).convert("RGB")
    except Exception:
        bg = None
    if bg:
        bg = bg.resize((W, H), Image.LANCZOS)
        # Dim it
        if dim:
            black = Image.new("RGB", (W, H), BG)
            bg = Image.blend(bg, black, 0.75)
        img.paste(bg, (0, 0))
    draw = ImageDraw.Draw(img)
    f = font(72, "serif")
    lines = wrap(quote, f, W - 360)
    total_h = len(lines) * 88
    y = (H - total_h) // 2
    for ln in lines:
        draw.text((W // 2 - f.getlength(ln) / 2, y), ln, fill=INK, font=f)
        y += 88
    img.save(out_path, "JPEG", quality=92)


def render_card(text, out_path, eyebrow=None, big=True):
    """Title / outro card."""
    img = Image.new("RGB", (W, H), BG)
    draw = ImageDraw.Draw(img)
    if eyebrow:
        fe = font(28, "mono")
        draw.text((W // 2 - fe.getlength(eyebrow) / 2, H // 2 - 160),
                  eyebrow, fill=AMBER, font=fe)
    f = font(96 if big else 56, "serif")
    lines = wrap(text, f, W - 200)
    y = H // 2 - 40
    for ln in lines:
        draw.text((W // 2 - f.getlength(ln) / 2, y), ln, fill=INK, font=f)
        y += 110 if big else 70
    img.save(out_path, "JPEG", quality=95)


def ffmpeg_slideshow(image_glob_dir, output, fps=30, sec_per_image=2.5):
    """Concat all JPEGs in image_glob_dir into a video, each shown for sec_per_image."""
    # ffmpeg can read a concat list with explicit per-image durations
    files = sorted(Path(image_glob_dir).glob("frame_*.jpg"))
    if not files:
        print(f"[!] no frames in {image_glob_dir}")
        return
    concat = Path(image_glob_dir) / "concat.txt"
    with concat.open("w", encoding="utf-8") as f:
        for p in files:
            f.write(f"file '{p.as_posix()}'\n")
            f.write(f"duration {sec_per_image}\n")
        # ffmpeg needs the last file repeated without duration
        f.write(f"file '{files[-1].as_posix()}'\n")
    cmd = ["ffmpeg", "-y", "-f", "concat", "-safe", "0", "-i", str(concat),
           "-vf", f"fps={fps},format=yuv420p,fade=t=in:st=0:d=0.4,"
                  f"fade=t=out:st={len(files)*sec_per_image - 0.5}:d=0.5",
           "-c:v", "libx264", "-pix_fmt", "yuv420p", "-preset", "medium",
           "-crf", "20", str(output)]
    print(f"  ffmpeg → {output.name}  ({len(files)} frames)")
    subprocess.run(cmd, check=False)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--voice", action="store_true", help="only voice_collage.mp4")
    ap.add_argument("--visits", action="store_true", help="only visit_collage.mp4")
    args = ap.parse_args()
    do_voice  = args.voice  or not args.visits
    do_visits = args.visits or not args.voice

    if not shutil.which("ffmpeg"):
        print("ffmpeg not found on PATH — install it from https://ffmpeg.org/download.html")
        return

    find_fonts()
    memory, visits = load_data()
    history = memory.get("history", []) or []

    # ── Visit collage ────────────────────────────────────────────────
    if do_visits:
        with tempfile.TemporaryDirectory() as tmpd:
            tmp = Path(tmpd)
            render_card("The Room", tmp / "frame_0000.jpg",
                        eyebrow="18 – 26 MAY 2026 · 45 VISITS")
            render_card("Every visit, in order", tmp / "frame_0001.jpg",
                        eyebrow="A LIGHT TIMELAPSE", big=False)
            i = 2
            for v in sorted(visits, key=lambda v: parse(v.get("arrived","")) or datetime.min):
                thumb_name = Path(v.get("thumbnail","")).name
                thumb_path = EPISODES / thumb_name
                if not thumb_path.exists(): continue
                quote = best_thought(v.get("thoughts") or [])
                dur   = fmt_dur(v.get("duration_sec"))
                render_visit_card(thumb_path, v.get("arrived","?"),
                                  dur, quote, tmp / f"frame_{i:04d}.jpg")
                i += 1
            render_card("982 thoughts", tmp / f"frame_{i:04d}.jpg",
                        eyebrow="13H 18M WITH SOMEONE INSIDE", big=False)
            print(f"rendering visit collage from {i+1} frames…")
            ffmpeg_slideshow(tmp, OUT / "visit_collage.mp4", sec_per_image=2.2)

    # ── Voice collage ────────────────────────────────────────────────
    if do_voice:
        # 40 best thoughts in chronological order
        H = sorted([h for h in history if h.get("text")],
                   key=lambda h: parse(h.get("t","")) or datetime.min)
        good = []
        seen = set()
        for h in H:
            t = (h.get("text") or "").strip()
            if not (8 <= len(t) <= 100): continue
            if BAD_RE.search(t): continue
            if not GOOD_RE.search(t): continue
            norm = re.sub(r"\s+", " ", t.lower()).strip(" ?.!,")
            if norm in seen: continue
            seen.add(norm); good.append(h)
        good = good[:40]

        # Pick a backdrop thumbnail pool
        all_thumbs = [EPISODES / Path(v.get("thumbnail","")).name
                      for v in visits if v.get("thumbnail")]
        all_thumbs = [t for t in all_thumbs if t.exists()]
        shuffle(all_thumbs)

        with tempfile.TemporaryDirectory() as tmpd:
            tmp = Path(tmpd)
            render_card("The Room", tmp / "frame_0000.jpg",
                        eyebrow="THINGS IT SAID")
            i = 1
            for j, h in enumerate(good):
                bg_pool = all_thumbs[j % len(all_thumbs)] if all_thumbs else None
                render_voice_card(bg_pool, h.get("text","").strip(),
                                  tmp / f"frame_{i:04d}.jpg")
                i += 1
            render_card("the end", tmp / f"frame_{i:04d}.jpg",
                        eyebrow="982 THOUGHTS · 45 VISITS · 8 DAYS")
            print(f"rendering voice collage from {i+1} frames…")
            ffmpeg_slideshow(tmp, OUT / "voice_collage.mp4", sec_per_image=4.5)

    print(f"\nDone.  Output: {OUT}")


if __name__ == "__main__":
    main()
