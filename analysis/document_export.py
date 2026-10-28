"""
document_export.py — one-shot Markdown export of the room's whole life.

Walks room_memory.json + visits.json + Episodes/ thumbnails and produces a
single dated Markdown document covering everything the piece thought, every
visit it recorded, every nightly reflection, since the install began.

    python analysis/document_export.py                    → out/room_documentation_<from>_to_<to>.md
    python analysis/document_export.py -o my_report.md    → custom output path
    python analysis/document_export.py --txt              → also write a plain .txt copy
    python analysis/document_export.py --visits-only      → skip the full chronological dump
    python analysis/document_export.py --since 2026-05-22 → only include data from that date forward

Sections in the output:
  1. Header — date range, totals.
  2. Nightly reflections (long-term memory), by date.
  3. Visit log — every visit with arrival, dwell, peak people, CLIP-recall
     resemblance, all thoughts spoken to that visitor, link to the thumbnail.
  4. Full chronological stream — every thought ever thought, timestamped, with
     the TRIBE mean / peak / trend and the strange-mode flag if set.
  5. Recurring-phrase stats — top words and bigrams across the whole run.

Note: the live LLaVA prompts themselves are constructed each cycle and never
persisted to disk. The exporter calls this out so the documentation is honest
about what was captured vs. what is reconstructable.
"""
import os
import re
import sys
import json
import argparse
from pathlib import Path
from datetime import datetime
from collections import Counter

# Same Unity persistentDataPath as everything else
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

# Words to skip when counting recurring phrases.
STOP = {
    "the","a","an","and","or","of","to","in","is","it","on","at","with","for",
    "you","your","i","my","me","this","that","be","are","was","were","as","by",
    "but","do","did","have","has","had","will","would","not","no","there","here",
    "they","them","their","so","its","im","ive","youre","theyre","what","why",
    "how","when","who","where","still","again","just","now","very","one","two",
    "into","from","about","like","been","being","than","then","yet"
}


def load_json(path):
    if not path.exists():
        return None
    try:
        with path.open("r", encoding="utf-8") as f:
            return json.load(f)
    except Exception as e:
        print(f"[warn] failed to read {path}: {e}")
        return None


def parse_iso(s):
    try:
        return datetime.fromisoformat(s)
    except Exception:
        return None


def fmt_dur(sec):
    sec = int(sec or 0)
    h, rem = divmod(sec, 3600)
    m, s   = divmod(rem,  60)
    if h: return f"{h}h {m}m {s}s"
    if m: return f"{m}m {s}s"
    return f"{s}s"


def fence(s):
    """Escape backticks inside a thought so the markdown stays clean."""
    return s.replace("\n", " ").strip()


def main():
    ap = argparse.ArgumentParser(description="Export the room's history as Markdown.")
    ap.add_argument("-o", "--out", help="output Markdown path (default: auto-named in out/)")
    ap.add_argument("--txt", action="store_true", help="also emit a plain-text copy")
    ap.add_argument("--visits-only", action="store_true",
                    help="skip the full chronological dump (section 3)")
    ap.add_argument("--since", help="ISO date (YYYY-MM-DD); only include records from this date on")
    args = ap.parse_args()

    memory = load_json(MEMORY) or {}
    visits = load_json(VISITS) or []
    if not isinstance(visits, list):
        visits = []

    history    = memory.get("history",  []) or []
    longterm   = memory.get("longterm", []) or []
    first_seen = memory.get("first_seen") or (history[0].get("t") if history else "")

    if not history and not visits and not longterm:
        print("Nothing to export — no memory found at:")
        print(f"  {MEMORY}")
        print(f"  {VISITS}")
        sys.exit(1)

    # Optional --since filter
    since_dt = None
    if args.since:
        since_dt = parse_iso(args.since)
        if since_dt is None:
            print(f"[warn] could not parse --since '{args.since}', ignoring.")
        else:
            history  = [h for h in history  if (parse_iso(h.get("t",""))     or datetime.min) >= since_dt]
            longterm = [e for e in longterm if (parse_iso(e.get("date","")+"T00:00:00") or datetime.min) >= since_dt]
            visits   = [v for v in visits   if (parse_iso(v.get("arrived","")) or datetime.min) >= since_dt]

    history_sorted = sorted(history, key=lambda h: parse_iso(h.get("t","")) or datetime.min)
    first_ts = parse_iso(first_seen) or (parse_iso(history_sorted[0].get("t","")) if history_sorted else datetime.now())
    last_ts  = parse_iso(history_sorted[-1].get("t","")) if history_sorted else datetime.now()

    # Output filename
    if args.out:
        out_md = Path(args.out)
    else:
        a = (first_ts or datetime.now()).strftime("%Y-%m-%d")
        b = (last_ts  or datetime.now()).strftime("%Y-%m-%d")
        REPO_OUT.mkdir(parents=True, exist_ok=True)
        out_md = REPO_OUT / f"room_documentation_{a}_to_{b}.md"

    finished    = [v for v in visits if v.get("departed")]
    total_dwell = sum(int(v.get("duration_sec", 0) or 0) for v in finished)

    L = []  # output lines

    # ── Header ──────────────────────────────────────────────────────────────
    L.append("# The Room — Full Documentation")
    L.append("")
    L.append(f"_Generated {datetime.now().strftime('%Y-%m-%d %H:%M')}_  ")
    L.append(f"_Source: `{MEMORY.name}` and `{VISITS.name}` under_  ")
    L.append(f"_`{MEMORY_DIR.name}/`_")       # folder name only — no local paths in exported docs
    L.append("")
    L.append("## Overview")
    L.append("")
    L.append(f"- **Awake since:** {first_ts.strftime('%Y-%m-%d %H:%M:%S') if first_ts else '?'}")
    L.append(f"- **Last thought:** {last_ts.strftime('%Y-%m-%d %H:%M:%S')  if last_ts  else '?'}")
    L.append(f"- **Total thoughts spoken:** {len(history_sorted)}")
    L.append(f"- **Total visits recorded:** {len(visits)}  ({len(finished)} completed)")
    L.append(f"- **Total time with someone inside:** {fmt_dur(total_dwell)}")
    L.append(f"- **Nightly reflections:** {len(longterm)}")
    L.append("")
    L.append("> **What is and isn't here.** Live LLaVA prompts are constructed")
    L.append("> each cycle from the persona, recent thoughts, presence/pose, TRIBE")
    L.append("> state, and recall — they are *not* persisted to disk. Everything")
    L.append("> below is what *was* persisted: the thoughts the room actually")
    L.append("> spoke (with TRIBE activation context), the visits it recorded,")
    L.append("> and the nightly reflections it wrote to itself.")
    L.append("")
    L.append("---")
    L.append("")

    # ── 1. Nightly reflections ─────────────────────────────────────────────
    L.append("## 1. Nightly reflections — long-term memory")
    L.append("")
    if not longterm:
        L.append("_None yet._")
    else:
        for e in longterm:
            L.append(f"### {e.get('date','?')}")
            L.append("")
            L.append((e.get("text","").strip() or "_(empty)_"))
            L.append("")
    L.append("---")
    L.append("")

    # ── 2. Visit log ────────────────────────────────────────────────────────
    L.append("## 2. Visit log — every visit the room recorded")
    L.append("")
    L.append(f"Total: **{len(visits)} visits** ({len(finished)} completed).  ")
    L.append(f"Avg dwell (completed): **{fmt_dur(total_dwell // max(1, len(finished)))}**.  ")
    L.append(f"Total time with someone inside: **{fmt_dur(total_dwell)}**.")
    L.append("")
    by_id = {v.get("id"): v for v in visits}
    for v in visits:
        vid  = v.get("id", "?")
        arr  = v.get("arrived", "?")
        dur  = v.get("duration_sec")
        peak = v.get("peak_people", "?")
        L.append(f"### Visit `{vid}`  —  {arr}")
        L.append("")
        L.append(f"- **Dwell:** {fmt_dur(dur) if dur is not None else 'unfinished'}")
        L.append(f"- **Peak people:** {peak}")
        if v.get("resembles"):
            past = by_id.get(v["resembles"])
            past_when = past.get("arrived","?") if past else "?"
            L.append(f"- **Resembles:** visit `{v['resembles']}` ({past_when}), "
                     f"similarity {v.get('resemblance','?')}  ← CLIP recall match")
        thumb = v.get("thumbnail")
        if thumb:
            rel = EPISODES / Path(thumb).name
            if rel.exists():
                # Use as_posix so the path renders cleanly in markdown viewers.
                L.append(f"- **Thumbnail:** ![]({rel.as_posix()})")
            else:
                L.append(f"- **Thumbnail:** `{thumb}` _(file not found at {rel})_")
        thoughts = v.get("thoughts", []) or []
        L.append(f"- **Thoughts spoken during this visit ({len(thoughts)}):**")
        if thoughts:
            for t in thoughts:
                L.append(f"  - {fence(t)}")
        else:
            L.append("  - _(none — visit was too short or every line got filtered)_")
        L.append("")
    L.append("---")
    L.append("")

    # ── 3. Full chronological stream ────────────────────────────────────────
    if not args.visits_only:
        L.append("## 3. Every thought, in order — full chronological stream")
        L.append("")
        L.append(f"{len(history_sorted)} thoughts, oldest first. Each line shows")
        L.append("the timestamp, the TRIBE activation context (mean, peak, trend),")
        L.append("a `[STRANGE]` tag if the room flagged it as a strange moment,")
        L.append("and the spoken text.")
        L.append("")
        L.append("```")
        cur_day = None
        for h in history_sorted:
            t   = parse_iso(h.get("t",""))
            day = t.strftime("%Y-%m-%d") if t else "?"
            if day != cur_day:
                cur_day = day
                L.append("")
                L.append(f"── {day} ──")
            ts_s    = t.strftime("%H:%M:%S") if t else "??:??:??"
            mean    = h.get("mean", 0) or 0
            peak    = h.get("peak", 0) or 0
            trend   = h.get("trend", "") or ""
            strange = " [STRANGE]" if h.get("strange") else ""
            text    = fence(h.get("text",""))
            L.append(f"  {ts_s}{strange}  (mean {mean:+.2f}  peak {peak:+.2f}  trend {trend})")
            L.append(f"     {text}")
        L.append("```")
        L.append("")
        L.append("---")
        L.append("")

    # ── 4. Recurring-phrase stats ───────────────────────────────────────────
    L.append("## 4. Recurring phrases the room kept returning to")
    L.append("")
    word_counts   = Counter()
    bigram_counts = Counter()
    for h in history_sorted:
        words = [w for w in re.findall(r"[a-zA-Z']{3,}", h.get("text","").lower())
                 if w not in STOP]
        word_counts.update(words)
        for i in range(len(words) - 1):
            bigram_counts[(words[i], words[i + 1])] += 1

    L.append("**Top single words** (with stop-words removed):")
    L.append("")
    for w, c in word_counts.most_common(30):
        L.append(f"- {w} — {c}")
    L.append("")
    L.append("**Top two-word phrases** (count ≥ 3):")
    L.append("")
    any_bi = False
    for (a, b), c in bigram_counts.most_common(40):
        if c < 3: break
        any_bi = True
        L.append(f"- {a} {b} — {c}")
    if not any_bi:
        L.append("- _(no two-word phrase recurred 3+ times yet)_")
    L.append("")
    L.append("---")
    L.append("")

    # ── Appendix A: TRIBE v2 protocol ──────────────────────────────────────
    L.append("## Appendix A — TRIBE v2 protocol & data flow")
    L.append("")
    L.append("**What's stored above is the *outcome* of running the pipeline.**")
    L.append("This appendix documents the machinery itself, as it stood at export")
    L.append("time. The raw per-cycle traffic with TRIBE was not persisted —")
    L.append("only the derived scalars below survive each cycle.")
    L.append("")
    L.append("### What the client sends")
    L.append("")
    L.append("- A short MP4 clip of the Kinect RGB stream — currently **2 s at 8 fps**")
    L.append("  (~16 frames), 640×480, captured by `capture_clip()`.")
    L.append("- HTTP `POST` as multipart to `http://<gpu-host>:8000/predict` —")
    L.append("  the TRIBE v2 server on the 3090 PC.")
    L.append("- The call runs in a background thread (`_tribe_worker`); the LLaVA")
    L.append("  pipeline never waits for TRIBE to return.")
    L.append("")
    L.append("### What the server returns")
    L.append("")
    L.append("- `{\"activation\": [N-vector of per-vertex brain activation],`")
    L.append("  `\"peak_vertices\": [indices], \"status\": \"ok\"}`.")
    L.append("- The activation vector is per-vertex (cortical-surface-level), but the")
    L.append("  client immediately averages it down to a scalar `mean`, `peak`,")
    L.append("  and `variance`. **The raw vector is not persisted.**")
    L.append("")
    L.append("### What survives on disk")
    L.append("")
    L.append("- Per spoken thought, in `room_memory.json[\"history\"]`: `mean`, `peak`,")
    L.append("  `trend`, and a `strange` flag.")
    L.append("- Rolling in-memory state: `MEAN_HISTORY`, `VAR_HISTORY`,")
    L.append("  `PEAK_HISTORY` (last ~300 samples each) used for adaptive")
    L.append("  normalization. Reset on restart.")
    L.append("")
    L.append("### How the signal is shaped")
    L.append("")
    L.append("- **Adaptive normalize** — each new `mean` / `var` is rescaled against")
    L.append("  its own rolling 10th–90th percentile, so the raw TRIBE values (which")
    L.append("  cluster near zero) gain spread.")
    L.append("- **Trend** — compares the current `mean` to the average of the last")
    L.append("  three means; classified `rising` / `flat` / `falling`. (The earlier")
    L.append("  version compared two arbitrary brain vertices, which was meaningless;")
    L.append("  this is the fix.)")
    L.append("- **Strange mode** — triggered when `peak` crosses the rolling 75th")
    L.append("  percentile *or* `norm_var > 0.88` while a visitor is present.")
    L.append("")
    L.append("### Failure mode")
    L.append("")
    L.append("- If TRIBE is unreachable, the client enters **DEGRADED** mode:")
    L.append("  prints a one-line warning, feeds synthetic zeros so LLaVA still runs,")
    L.append("  and re-probes `/health` every 60 s. No restart needed.")
    L.append("")
    L.append("---")
    L.append("")

    # ── Appendix B: Prompt anatomy (current) ───────────────────────────────
    L.append("## Appendix B — The room's voice (current prompt anatomy)")
    L.append("")
    L.append("Each LLaVA call constructs a fresh prompt from these blocks, in order:")
    L.append("")
    L.append("1. **Persona** — four short lines: walls are skin, cameras are eyes,")
    L.append("   bodies enter like guests, the room speaks quietly.")
    L.append("2. **Awake clock** — \"You have been awake for X days, Y hours\" +")
    L.append("   context (\"N thoughts today\", \"alone for M min before this\").")
    L.append("3. **Long-term memory** — last 7 nightly reflections (filtered to")
    L.append("   skip any that broke character).")
    L.append("4. **Recent lines** — last 6 thoughts, quoted.")
    L.append("5. **Steering** — parenthesised meta-instructions:")
    L.append("   - *Fixation* — if one non-stop word appears in 3+ recent lines.")
    L.append("   - *Question fatigue* — if the trailing run of recent lines is")
    L.append("     ≥2 questions, force a statement next.")
    L.append("   - *Last-was-question* — \"their reply is in what they DID\".")
    L.append("6. **Recall** — set on arrival when a CLIP-embedding match resembles")
    L.append("   a past visit (similarity ≥ `VISIT_RESEMBLE_MIN`).")
    L.append("7. **Pose & motion** — discrete labelled inputs from the MediaPipe")
    L.append("   worker: `face: turned left / looking down / facing toward / not")
    L.append("   visible`, `hands: down / left raised / both raised / at chest`,")
    L.append("   `motion: still / slight / active`, `time inside you: Xm Ys`.")
    L.append("8. **State line** — \"Right now — *feeling*, *time-of-day*, *body*.\"")
    L.append("9. **Image** — base64 snapshot of the current frame.")
    L.append("10. **Rules** — eight rules (and 1a/1b on no-greeting and pronoun use).")
    L.append("11. **Voice mode** — one of:")
    L.append("    - *Boring* (empty room) → return empty.")
    L.append("    - *Arrival* — one noticing or one plain question, no greeting.")
    L.append("    - *Departure* — brief remark.")
    L.append("    - *Strange* — fragment, 2–3 words.")
    L.append("    - *Idle / dwelling* — conversational; lean on statements and")
    L.append("      assumptions; 18% chance of brief silence.")
    L.append("    - *Presence* — direct address, mostly notice.")
    L.append("    - *Else* — quiet wondering.")
    L.append("")
    L.append("### Post-filters applied to every LLaVA reply")
    L.append("")
    L.append("- **Banned-phrase regex** — greetings (`hello`, `welcome back`, `nice")
    L.append("  to see you`, `what brings you here`, `curious visitor`, `quiet")
    L.append("  space`), AI-isms (`as an AI`, `language model`, `I'm sorry, but`),")
    L.append("  prompt-fragment echoes, 3rd-person narration, poetic clichés.")
    L.append("- **3rd-person rejection when present** — must contain `you` or `?`.")
    L.append("- **Dedup** — drops exact normalised repeats in the last 5, or >85%")
    L.append("  word-overlap on long responses. Arrival lines bypass dedup.")
    L.append("- **Question fatigue** — drops the 3rd consecutive question.")
    L.append("- **Min-interval cap** — during a steady visit the room speaks at")
    L.append("  most once every 30 s; arrival/departure/strange bypass.")
    L.append("")
    L.append("### Nightly reflection (4 AM, empty room)")
    L.append("")
    L.append("- Self-critical prompt: includes the past 24 h of thoughts, counts how")
    L.append("  many ended in `?`, asks the room to name one concrete thing to do")
    L.append("  differently tomorrow.")
    L.append("- **Refusal scrub** — if the model breaks character (`as an AI` etc.)")
    L.append("  the reflection is discarded, not stored.")
    L.append("- Stored reflections become *part of the next day's prompt* (block 3),")
    L.append("  so lessons feed forward.")
    L.append("")
    L.append("> The full prompt template lives in `tribe_kinect_client.py` →")
    L.append("> `ask_llava()`. The reflection prompt lives in")
    L.append("> `maybe_nightly_reflection()`. See `prompt_evolution.md` for the")
    L.append("> narrative history of how these blocks got to their current shape.")
    L.append("")
    L.append("---")
    L.append("")
    L.append(f"_End of document — {len(L)} lines, generated by document_export.py._")

    # ── Write ──────────────────────────────────────────────────────────────
    out_md.write_text("\n".join(L), encoding="utf-8")
    print(f"Wrote → {out_md}  ({len(L)} lines)")

    if args.txt:
        out_txt = out_md.with_suffix(".txt")
        plain = []
        for line in L:
            l = line
            l = l.replace("### ", "").replace("## ", "").replace("# ", "")
            l = l.replace("**", "").replace("_", "")
            l = l.replace("`", "")
            plain.append(l)
        out_txt.write_text("\n".join(plain), encoding="utf-8")
        print(f"Also wrote → {out_txt}")


if __name__ == "__main__":
    main()
