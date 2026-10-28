"""
visualize.py — make charts from the live memory for documentation / posters.

Run (from the repo root):
    pip install -r requirements.txt
    ROOM_MEMORY_DIR=examples/sample_memory python analysis/visualize.py

Output: out/charts/*.png  (the charts from the real run are committed in charts/)
    01_thoughts_over_time.png       — every thought as a dot on a timeline,
                                      coloured by hour-of-day, vertical axis
                                      = strange-mode intensity
    02_thoughts_per_hour_heatmap.png — day × hour heatmap of activity
    03_dwell_distribution.png       — histogram of visit durations (log x)
    04_visit_arrival_timeline.png   — when visitors actually came
    05_clip_recall_matrix.png       — visit-to-visit similarity heatmap
    06_top_words.png                — bar chart of top vocabulary
    07_question_ratio_over_days.png — % of thoughts that were questions per day
    08_voice_modes_pie.png          — inferred voice mode distribution

All charts use the gallery palette (black bg, amber accent) so they drop
straight into a slide deck or the website.
"""
import os, re, json, math
from pathlib import Path
from datetime import datetime
from collections import Counter, defaultdict

try:
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    import numpy as np
except ImportError:
    print("install matplotlib first:  pip install matplotlib numpy")
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
OUT      = REPO_OUT / "charts"
OUT.mkdir(parents=True, exist_ok=True)

# Gallery palette
BG     = "#0a0a0a"
INK    = "#e8e6e1"
DIM    = "#888479"
AMBER  = "#ffb84d"
TEAL   = "#5dd6c1"
PINK   = "#ff6b9d"
ORANGE = "#ff9d6c"
GRID   = "#222222"


def style(ax, title=None, xlabel=None, ylabel=None):
    ax.set_facecolor(BG)
    if title:  ax.set_title(title,  color=INK, pad=14, fontsize=14)
    if xlabel: ax.set_xlabel(xlabel, color=DIM, fontsize=10)
    if ylabel: ax.set_ylabel(ylabel, color=DIM, fontsize=10)
    ax.tick_params(colors=DIM)
    for s in ax.spines.values(): s.set_color(GRID)
    ax.grid(True, color=GRID, linewidth=0.5, alpha=0.6)


def save(fig, name):
    path = OUT / name
    fig.savefig(path, dpi=150, facecolor=BG, bbox_inches="tight")
    print(f"  → {path.name}")
    plt.close(fig)


def parse(s):
    try: return datetime.fromisoformat(s)
    except Exception: return None


def main():
    memory = json.loads(MEMORY.read_text(encoding="utf-8")) if MEMORY.exists() else {}
    visits = json.loads(VISITS.read_text(encoding="utf-8")) if VISITS.exists() else []
    if not isinstance(visits, list): visits = []
    history = memory.get("history", []) or []
    if not history:
        print("No history found — nothing to plot.")
        return

    H = sorted(history, key=lambda h: parse(h.get("t","")) or datetime.min)
    times  = [parse(h.get("t","")) for h in H]
    valid  = [(t, h) for t, h in zip(times, H) if t is not None]
    if not valid:
        print("No parseable timestamps — nothing to plot.")
        return

    print(f"plotting {len(valid)} thoughts and {len(visits)} visits → {OUT}")

    # ── 01: thoughts over time scatter ───────────────────────────────
    fig, ax = plt.subplots(figsize=(11, 4))
    style(ax, "Every thought, in order", "time", "TRIBE peak (normalised)")
    ts = [v[0] for v in valid]
    pk = [float(v[1].get("peak", 0) or 0) for v in valid]
    hr = [v[0].hour for v in valid]
    strange = np.array([bool(v[1].get("strange")) for v in valid])
    sc = ax.scatter(ts, pk, c=hr, cmap="twilight", s=14, alpha=0.75, edgecolor="none")
    if strange.any():
        ax.scatter([t for t,s in zip(ts, strange) if s],
                   [p for p,s in zip(pk, strange) if s],
                   s=70, facecolors="none", edgecolors=PINK, linewidths=1.2,
                   label=f"strange moments ({int(strange.sum())})")
        ax.legend(facecolor=BG, edgecolor=GRID, labelcolor=INK, fontsize=9)
    cb = plt.colorbar(sc, ax=ax, pad=0.01)
    cb.ax.tick_params(colors=DIM); cb.outline.set_edgecolor(GRID)
    cb.set_label("hour of day", color=DIM)
    save(fig, "01_thoughts_over_time.png")

    # ── 02: day × hour heatmap ───────────────────────────────────────
    days = sorted({t.date() for t in ts})
    M = np.zeros((len(days), 24), dtype=int)
    di = {d: i for i, d in enumerate(days)}
    for t in ts:
        M[di[t.date()], t.hour] += 1
    fig, ax = plt.subplots(figsize=(11, 1.0 + 0.45*len(days)))
    style(ax, "Activity: thoughts per hour, per day", "hour of day", "")
    im = ax.imshow(M, aspect="auto", cmap="magma", interpolation="nearest")
    ax.set_yticks(range(len(days)))
    ax.set_yticklabels([d.strftime("%a %d %b") for d in days], color=INK, fontsize=9)
    ax.set_xticks(range(0, 24, 2))
    cb = plt.colorbar(im, ax=ax, pad=0.01); cb.ax.tick_params(colors=DIM)
    cb.outline.set_edgecolor(GRID); cb.set_label("thoughts spoken", color=DIM)
    save(fig, "02_thoughts_per_hour_heatmap.png")

    # ── 03: dwell distribution ───────────────────────────────────────
    durs = [int(v.get("duration_sec",0) or 0) for v in visits if v.get("departed")]
    if durs:
        fig, ax = plt.subplots(figsize=(9, 4))
        style(ax, f"Visit dwell time  (n={len(durs)})", "seconds  (log scale)", "visits")
        bins = np.logspace(0.5, math.log10(max(durs)+1), 30)
        ax.hist(durs, bins=bins, color=AMBER, edgecolor=BG)
        ax.set_xscale("log")
        ax.axvline(np.median(durs), color=PINK, linestyle="--", linewidth=1,
                   label=f"median {int(np.median(durs))}s")
        ax.legend(facecolor=BG, edgecolor=GRID, labelcolor=INK, fontsize=9)
        save(fig, "03_dwell_distribution.png")

    # ── 04: visit arrival timeline ───────────────────────────────────
    arrivals = sorted([(parse(v.get("arrived","")), v) for v in visits
                       if parse(v.get("arrived",""))], key=lambda x: x[0])
    if arrivals:
        fig, ax = plt.subplots(figsize=(11, 3.5))
        style(ax, "When visits happened", "time", "duration  (min)")
        for t, v in arrivals:
            dur = (v.get("duration_sec") or 0) / 60.0
            n   = v.get("peak_people", 1) or 1
            ax.scatter(t, dur, s=20 + n*40, color=AMBER if not v.get("resembles") else ORANGE,
                       alpha=0.7, edgecolor=BG, linewidth=0.5)
        # Legend
        ax.scatter([], [], s=60, color=AMBER, label="new visitor")
        ax.scatter([], [], s=60, color=ORANGE, label="CLIP-recognised return")
        ax.legend(facecolor=BG, edgecolor=GRID, labelcolor=INK, fontsize=9, loc="upper left")
        save(fig, "04_visit_arrival_timeline.png")

    # ── 05: visit similarity matrix (CLIP cosine where available) ────
    sigs = [v.get("signature") for v in visits]
    if sigs and any(s and s.get("embedding") for s in sigs):
        # build pairwise cosine using embeddings; fall back NaN where missing
        N = len(sigs)
        S = np.full((N, N), np.nan)
        embs = [np.array(s["embedding"], dtype=np.float32) if (s and s.get("embedding")) else None
                for s in sigs]
        for i in range(N):
            if embs[i] is None: continue
            for j in range(N):
                if embs[j] is None: continue
                d = (np.linalg.norm(embs[i]) * np.linalg.norm(embs[j])) or 1.0
                S[i, j] = float(np.dot(embs[i], embs[j]) / d)
        fig, ax = plt.subplots(figsize=(8, 7))
        style(ax, "CLIP recall — visit-to-visit visual similarity", "visit #", "visit #")
        im = ax.imshow(np.where(np.isnan(S), 0, S), cmap="magma", vmin=0.5, vmax=1.0)
        cb = plt.colorbar(im, ax=ax); cb.ax.tick_params(colors=DIM)
        cb.outline.set_edgecolor(GRID); cb.set_label("cosine similarity", color=DIM)
        save(fig, "05_clip_recall_matrix.png")

    # ── 06: top words ────────────────────────────────────────────────
    STOP = set("the a an and or of to in is it on at with for you your i my me this that be are was were as by but do did have has had will would not no there here they them their so its im ive youre theyre what why how when who where still again just now very one two into from about like been being than then yet".split())
    words = Counter()
    for _, h in valid:
        for w in re.findall(r"[a-zA-Z']{3,}", (h.get("text","") or "").lower()):
            if w not in STOP: words[w] += 1
    top = words.most_common(25)
    if top:
        labels, counts = zip(*reversed(top))
        fig, ax = plt.subplots(figsize=(8, 7))
        style(ax, "Top 25 words across the week", "occurrences", "")
        ax.barh(labels, counts, color=AMBER, edgecolor=BG)
        save(fig, "06_top_words.png")

    # ── 07: question ratio over days ─────────────────────────────────
    by_day = defaultdict(lambda: [0, 0])  # [total, questions]
    for t, h in valid:
        by_day[t.date()][0] += 1
        if (h.get("text") or "").rstrip().endswith("?"):
            by_day[t.date()][1] += 1
    if by_day:
        ds = sorted(by_day.keys())
        ratio = [by_day[d][1] / max(1, by_day[d][0]) * 100 for d in ds]
        total = [by_day[d][0] for d in ds]
        fig, ax = plt.subplots(figsize=(10, 4))
        style(ax, "% of each day's thoughts that ended in a question",
              "", "% questions")
        bars = ax.bar([d.strftime("%a %d") for d in ds], ratio, color=AMBER, edgecolor=BG)
        for b, n in zip(bars, total):
            ax.text(b.get_x() + b.get_width()/2, b.get_height() + 1,
                    f"n={n}", ha="center", color=DIM, fontsize=8)
        ax.set_ylim(0, max(ratio) * 1.2)
        ax.axhline(50, color=PINK, linestyle="--", linewidth=0.8, alpha=0.6)
        save(fig, "07_question_ratio_over_days.png")

    # ── 08: voice modes (inferred) ───────────────────────────────────
    modes = Counter()
    for _, h in valid:
        if h.get("strange"): modes["strange"] += 1
        elif (h.get("text") or "").endswith("?"): modes["question"] += 1
        elif len(h.get("text","").split()) <= 4: modes["fragment"] += 1
        else: modes["noticing"] += 1
    if modes:
        labels  = list(modes.keys())
        sizes   = list(modes.values())
        colours = {"strange": PINK, "question": ORANGE, "fragment": TEAL, "noticing": AMBER}
        cols    = [colours.get(l, DIM) for l in labels]
        fig, ax = plt.subplots(figsize=(7, 7))
        ax.set_facecolor(BG); fig.patch.set_facecolor(BG)
        wedges, texts, autotexts = ax.pie(
            sizes, labels=labels, colors=cols, autopct="%1.0f%%",
            wedgeprops={"linewidth": 2, "edgecolor": BG},
            textprops={"color": INK, "fontsize": 11})
        for a in autotexts: a.set_color(BG); a.set_fontweight("bold")
        ax.set_title("Shapes of voice", color=INK, fontsize=14, pad=14)
        save(fig, "08_voice_modes_pie.png")

    print(f"\nDone.  Charts are in {OUT}")

if __name__ == "__main__":
    main()
