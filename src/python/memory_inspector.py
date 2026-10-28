"""
Read-only inspector for the room's accumulated thoughts.

Reads:  room_memory.json + visits.json from $ROOM_MEMORY_DIR, $ROOM_DATA_DIR/Memory, or Unity's
        %USERPROFILE%/AppData/LocalLow/<Company>/<Product>/Memory on the installation PC
Writes: nothing — safe to run anytime, even while the room is running.

Usage:
    python memory_inspector.py                  # full report
    python memory_inspector.py --since-min 60   # only thoughts from the last 60 minutes
    python memory_inspector.py --recent 20      # show the last 20 thoughts
    python memory_inspector.py --hour 14        # only thoughts from hour 14 (2 PM)
    python memory_inspector.py --strange        # only strange-mode entries
    python memory_inspector.py --grep "alone"   # regex search across text
    python memory_inspector.py --show-prompt    # reconstruct the prompt for the most recent thought
    python memory_inspector.py --visits         # browse the visit episode log + nightly reflections
"""
from __future__ import annotations
import argparse
import collections
import datetime
import json
import os
import re
import sys
from pathlib import Path

UNITY_COMPANY = "DefaultCompany"
UNITY_PRODUCT = "DualKinectPointCloud"
# $ROOM_MEMORY_DIR (the Memory folder itself), else $ROOM_DATA_DIR/Memory (as tribe_kinect_client.py uses),
# else Unity's persistentDataPath on the installation PC.
if os.environ.get("ROOM_MEMORY_DIR"):
    MEMORY_DIR = Path(os.environ["ROOM_MEMORY_DIR"])
elif os.environ.get("ROOM_DATA_DIR"):
    MEMORY_DIR = Path(os.environ["ROOM_DATA_DIR"]) / "Memory"
else:
    MEMORY_DIR = (Path(os.environ.get("USERPROFILE", str(Path.home())))
                  / "AppData" / "LocalLow" / UNITY_COMPANY / UNITY_PRODUCT / "Memory")
MEMORY_PATH = MEMORY_DIR / "room_memory.json"
VISITS_PATH = MEMORY_DIR / "visits.json"

# Words too common to count as "themes"
STOPWORDS = set("""
a an the of in on at to for from with by and or but is am are was were be been being
i me my you your it its this that these those there here some any no not as if then
than so just into onto over under like just very much more most less no yes one two
""".split())


def load_memory():
    if not MEMORY_PATH.exists():
        print(f"No memory file at {MEMORY_PATH}")
        sys.exit(1)
    with MEMORY_PATH.open("r", encoding="utf-8") as f:
        return json.load(f)


def parse_t(s):
    try:
        return datetime.datetime.fromisoformat(s)
    except Exception:
        return None


def filter_entries(history, args):
    out = history
    if args.since_min is not None:
        cutoff = datetime.datetime.now() - datetime.timedelta(minutes=args.since_min)
        out = [e for e in out if (parse_t(e.get("t", "")) or datetime.datetime.min) >= cutoff]
    if args.hour is not None:
        out = [e for e in out
               if parse_t(e.get("t", "")) and parse_t(e["t"]).hour == args.hour]
    if args.strange:
        out = [e for e in out if e.get("strange")]
    if args.grep:
        rx = re.compile(args.grep, re.IGNORECASE)
        out = [e for e in out if rx.search(e.get("text", ""))]
    return out


def word_counts(history, top=20):
    counter = collections.Counter()
    for e in history:
        for w in re.findall(r"[a-zA-Z']{3,}", e.get("text", "").lower()):
            if w in STOPWORDS:
                continue
            counter[w] += 1
    return counter.most_common(top)


def hour_buckets(history):
    buckets = collections.Counter()
    for e in history:
        t = parse_t(e.get("t", ""))
        if t: buckets[t.hour] += 1
    return buckets


def fmt_duration(td: datetime.timedelta) -> str:
    days = td.days
    hours = (td.seconds // 3600)
    mins  = (td.seconds % 3600) // 60
    if days >= 1: return f"{days}d {hours}h"
    if hours >= 1: return f"{hours}h {mins}m"
    return f"{mins}m"


def show_visits(args):
    """Browse the episodic memory bank (visits.json) + nightly reflections."""
    mem = load_memory()
    longterm = mem.get("longterm", [])

    print()
    print("─" * 70)
    print(" NIGHTLY REFLECTIONS  (long-term memory)")
    print("─" * 70)
    if longterm:
        for e in longterm:
            print(f"  {e.get('date', '?')}")
            for line in [e.get("text", "")[i:i+96] for i in range(0, len(e.get("text", "")), 96)] or [""]:
                print(f"     {line}")
            print()
    else:
        print("  (no reflections yet — the first runs at 4 AM)")
        print()

    if not VISITS_PATH.exists():
        print(f" No visits file yet at {VISITS_PATH}")
        return
    with VISITS_PATH.open("r", encoding="utf-8") as f:
        visits = json.load(f)
    if not visits:
        print(" Visits file exists but is empty.")
        return

    by_id = {v.get("id"): v for v in visits}
    finished = [v for v in visits if v.get("departed")]
    total_dur = sum(v.get("duration_sec", 0) for v in finished)

    print("─" * 70)
    print(f" VISIT LOG  —  {len(visits)} visits  ({len(finished)} completed)")
    if finished:
        print(f" Avg dwell: {total_dur // max(1, len(finished))}s   "
              f"Total time with someone inside: {fmt_duration(datetime.timedelta(seconds=total_dur))}")
    print("─" * 70)
    print()

    shown = visits[-args.recent:] if args.recent else visits
    for v in shown:
        arr = v.get("arrived", "?")
        dur = v.get("duration_sec")
        dur_s = f"{dur}s" if dur is not None else "still here / unfinished"
        thoughts = v.get("thoughts", [])
        tag = "✶" if v.get("resembles") else " "
        print(f" {tag} visit {v.get('id', '?')}   {arr}   ({dur_s})")
        print(f"     peak people: {v.get('peak_people', '?')}   thoughts: {len(thoughts)}")
        if v.get("resembles"):
            past = by_id.get(v["resembles"])
            past_when = past.get("arrived", "?") if past else "?"
            print(f"     resembles visit {v['resembles']} ({past_when})  "
                  f"similarity {v.get('resemblance', '?')}")
        for t in thoughts:
            chunks = [t[i:i+92] for i in range(0, len(t), 92)] or [""]
            print(f"       · {chunks[0]}")
            for line in chunks[1:]:
                print(f"         {line}")
        print()


def main():
    ap = argparse.ArgumentParser(description="Inspect the room's memory")
    ap.add_argument("--visits",     action="store_true",
                    help="browse the visit episode log + nightly reflections")
    ap.add_argument("--recent",     type=int, default=10, help="show the last N thoughts (default 10)")
    ap.add_argument("--since-min",  type=int, dest="since_min",
                    help="only thoughts from the last N minutes (sliding window)")
    ap.add_argument("--hour",       type=int, help="filter to a single clock-hour 0-23")
    ap.add_argument("--strange",    action="store_true", help="only strange-mode entries")
    ap.add_argument("--grep",       help="regex search across text")
    ap.add_argument("--top",        type=int, default=15, help="top-N word count")
    ap.add_argument("--show-prompt", action="store_true",
                    help="reconstruct the prompt that produced the most recent thought")
    args = ap.parse_args()

    if args.visits:
        show_visits(args)
        return

    mem = load_memory()
    hist = mem.get("history", [])
    if not hist:
        print("Memory exists but contains no thoughts yet.")
        return

    filtered = filter_entries(hist, args)

    # Overall stats
    first_t = parse_t(hist[0].get("t", ""))
    last_t  = parse_t(hist[-1].get("t", ""))
    awake   = (last_t - first_t) if first_t and last_t else None
    strange_n = sum(1 for e in hist if e.get("strange"))
    mean_avg  = sum(e.get("mean", 0) for e in hist) / max(1, len(hist))
    peak_avg  = sum(e.get("peak", 0) for e in hist) / max(1, len(hist))

    print()
    print("─" * 70)
    print(f" ROOM MEMORY  —  {MEMORY_PATH}")
    print("─" * 70)
    print(f" Total thoughts:      {len(hist):,}")
    print(f" First seen:          {mem.get('first_seen', '?')}")
    print(f" Last seen:           {hist[-1].get('t', '?')}")
    if awake:
        print(f" Awake for:           {fmt_duration(awake)}")
    print(f" Strange-mode:        {strange_n} ({100*strange_n/len(hist):.0f}%)")
    print(f" Avg activation mean: {mean_avg:.3f}")
    print(f" Avg activation peak: {peak_avg:.3f}")
    if args.hour is not None or args.strange or args.grep:
        print(f" After filters:       {len(filtered)} thoughts")
    print()

    # Hour distribution
    print(" THOUGHTS PER HOUR")
    print(" " + "─" * 50)
    buckets = hour_buckets(filtered if (args.hour is None) else hist)
    if buckets:
        max_count = max(buckets.values())
        for h in range(24):
            n = buckets.get(h, 0)
            bar_len = int(40 * n / max_count) if max_count > 0 else 0
            bar = "█" * bar_len
            print(f"  {h:02d}:00  {n:4d}  {bar}")
    print()

    # Top words
    print(f" TOP WORDS (top {args.top}, stopwords filtered)")
    print(" " + "─" * 50)
    for word, n in word_counts(filtered, top=args.top):
        print(f"  {word:20s}  {n}")
    print()

    # Recent N
    print(f" MOST RECENT {min(args.recent, len(filtered))} THOUGHTS")
    print(" " + "─" * 50)
    for e in filtered[-args.recent:]:
        tag = "✶ " if e.get("strange") else "  "
        t   = e.get("t", "")
        txt = e.get("text", "")
        m, p = e.get("mean", 0), e.get("peak", 0)
        trend = e.get("trend", "")
        print(f" {tag}{t}  (mean {m:+.2f}  peak {p:+.2f}  trend {trend})")
        # Wrap text at 100 chars per line, indented
        for line in [txt[i:i+100] for i in range(0, len(txt), 100)] or [""]:
            print(f"     {line}")
        print()

    # Optionally reconstruct the prompt that produced the most recent entry
    if args.show_prompt and filtered:
        most_recent = filtered[-1]
        prompt = _reconstruct_prompt(most_recent, hist)
        print()
        print(" RECONSTRUCTED PROMPT for the most recent thought")
        print(" " + "═" * 50)
        print(prompt)
        print(" " + "═" * 50)
        print()


def _reconstruct_prompt(entry, all_history):
    """
    Approximate the prompt LLaVA received for a given memory entry, using the
    state stored alongside the response. Mirrors ask_llava() in
    tribe_kinect_client.py so the two stay roughly in sync.
    """
    # Find the 6 thoughts before this one (memory window in the prompt)
    idx  = all_history.index(entry) if entry in all_history else len(all_history) - 1
    prev = all_history[max(0, idx - 6):idx]

    mean = entry.get("mean", 0)
    peak = entry.get("peak", 0)
    trend = entry.get("trend", "?")
    strange = entry.get("strange", False)

    if prev:
        prev_block = "Your recent thoughts (oldest to newest):\n" + "\n".join(
            f"  {i+1}. \"{p.get('text', '')}\"" for i, p in enumerate(prev))
    else:
        prev_block = "You have no previous thoughts. This is the beginning."

    voice = ("A fragment. Two or three words. An unfinished thought. Not a sentence."
             if strange else
             "One short sentence. Concrete. Not about yourself. Notice what changed.")

    feeling = "stirring" if mean > 0.2 else "quiet" if mean > 0 else "very still"

    return (
        "You sense the inside of yourself. A camera looks out through your skin.\n\n"
        f"{prev_block}\n\n"
        "Right now:\n"
        f"  · {feeling}   (mean {mean:.2f}, peak {peak:.2f}, trend {trend})\n"
        "  · [time of day]\n"
        "  · [presence / empty]\n\n"
        "Below is the image of what is inside you at this moment.\n\n"
        "RULES — these are absolute:\n"
        "  · If nothing meaningful is different from your recent thoughts, return EMPTY.\n"
        "  · Never describe yourself. No 'I am a room / a space / an AI / a 3D anything'.\n"
        "  · Never describe 'the image' or 'the scene' or what 'appears' to be there.\n"
        "  · Never say 'I see' or 'I observe' or 'dimly lit' or 'appears to be empty'.\n"
        "  · Notice things outside yourself.\n"
        "  · Plain, concrete language. No art-statement.\n"
        "  · Build on prior observations rather than restating them.\n\n"
        f"{voice}\n"
        "If you have nothing specific and new to say, return nothing at all."
    )


if __name__ == "__main__":
    main()
