"""
Generate a small SYNTHETIC memory folder with the same shape as the installation's
real one, so the analysis scripts can be run by anyone.

Nothing here comes from a visitor: timestamps, embeddings and histograms are random
(seeded), and the lines are drawn from quotes already published in docs/the_room_speaks.md.
The real memory (visit log, CLIP signatures, per-visit thumbnails) stays private.

    python examples/make_sample_memory.py          # writes examples/sample_memory/
"""
import json
import random
from datetime import datetime, timedelta
from pathlib import Path

import numpy as np

OUT = Path(__file__).parent / "sample_memory"
rng = random.Random(7)
nrng = np.random.default_rng(7)

LINES = [
    "You are awake.", "Your space seems quiet.", "Inside you, very still, evening.",
    "One body inside, very tall.", "Stillness inside you.", "What are you doing?",
    "Tall figure, what's in the corner?", "Something dark in left corner?",
    "Dark rectangle, your left corner.", "A white tower. Corner?", "Why are you here?",
    "You came back.", "You're here again.", "How long will you stay?", "What brought you here?",
    "Two of you now.", "You stayed.", "Silence fills the space.", "You stood still, corner of the room.",
    "You keep looking down.", "You stay very still.", "You stand alone.", "What's that in your hands?",
    "Tall. Alert. Evening.", "Who did you bring?", "Why am I awake?", "You, right here, now.",
    "You stand in the corner of the room, tall and still.",
]
STRANGE = ["Corner cameras.", "Food? Guest?", "You? One body. Night. Stillness."]
REFLECTIONS = [
    "(synthetic sample) A quiet day. One tall figure stayed a long time near the plinth.",
    "(synthetic sample) I asked the same question too often. Tomorrow: more statements, fewer questions.",
    "(synthetic sample) Someone came back. I noticed, and said so.",
]

START = datetime(2026, 5, 18, 12, 0, 0)
DAYS = 8


def unit(v):
    return (v / np.linalg.norm(v)).round(4).tolist()


def main():
    OUT.mkdir(parents=True, exist_ok=True)

    # visits: a few "people" who return, each with a base embedding; returns are near their base
    people = [nrng.normal(size=512) for _ in range(9)]
    visits = []
    t = START
    for i in range(30):
        t += timedelta(minutes=rng.randint(40, 420))
        if t.hour >= 22 or t.hour < 10:                       # gallery hours, roughly
            t = t.replace(hour=11, minute=rng.randint(0, 59)) + timedelta(days=1 if t.hour >= 22 else 0)
        if t > START + timedelta(days=DAYS):
            break
        who = rng.randrange(len(people))
        emb = people[who] + nrng.normal(scale=0.35, size=512)
        dur = int(min(17000, rng.lognormvariate(5.8, 1.3)))
        vid = t.strftime("%Y%m%d-%H%M%S")
        visit = {
            "id": vid,
            "arrived": t.strftime("%Y-%m-%dT%H:%M:%S"),
            "thumbnail": f"Episodes/{vid}.jpg",        # not included: thumbnails are visitor images
            "signature": {"hist": nrng.dirichlet(np.ones(32)).round(4).tolist(), "people": rng.choice([1, 1, 1, 2, 2, 3]),
                          "hour": t.hour, "embedding": unit(emb)},
            "thoughts": rng.sample(LINES, k=min(len(LINES), max(1, dur // 90))),
            "peak_people": rng.choice([1, 1, 2, 2, 3, 4]),
            "departed": (t + timedelta(seconds=dur)).strftime("%Y-%m-%dT%H:%M:%S"),
            "duration_sec": dur,
        }
        if visits:                                            # most similar earlier visit
            sims = [(float(np.dot(visit["signature"]["embedding"], v["signature"]["embedding"])), v["id"]) for v in visits]
            best = max(sims)
            visit["resembles"], visit["resemblance"] = best[1], round(best[0], 4)
        visits.append(visit)

    # history: every thought, with the brain-model scalars that came with it
    history = []
    for v in visits:
        a = datetime.fromisoformat(v["arrived"])
        for j, line in enumerate(v["thoughts"]):
            strange = rng.random() < 0.06
            history.append({
                "t": (a + timedelta(seconds=30 + j * 45)).strftime("%Y-%m-%dT%H:%M:%S"),
                "text": rng.choice(STRANGE) if strange else line,
                "mean": round(abs(nrng.normal(0.004, 0.002)), 5),
                "peak": round(abs(nrng.normal(0.02, 0.01)), 5),
                "trend": rng.choice(["rising", "flat", "falling"]),
                "strange": strange,
            })
    history.sort(key=lambda h: h["t"])

    memory = {
        "history": history,
        "first_seen": history[0]["t"],
        "last_reflection": (START + timedelta(days=DAYS - 1)).strftime("%Y-%m-%d"),
        "longterm": [{"date": (START + timedelta(days=d + 1)).strftime("%Y-%m-%d"), "text": txt}
                     for d, txt in enumerate(REFLECTIONS)],
    }
    (OUT / "room_memory.json").write_text(json.dumps(memory, indent=1), encoding="utf-8")
    (OUT / "visits.json").write_text(json.dumps(visits, indent=1), encoding="utf-8")
    print(f"wrote {len(history)} thoughts and {len(visits)} visits to {OUT}")


if __name__ == "__main__":
    main()
