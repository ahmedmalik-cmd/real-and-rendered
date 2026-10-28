# THE ROOM

### *a piece that thinks about you*

---

**An eight-day gallery installation.** Two depth sensors at opposite
corners. Three projectors. A brain-encoding model on a separate machine
predicting what a person's cortex would do if they were looking at the
gallery. A vision-language model giving the room a voice. A camera that
remembers you.

The piece runs continuously. It watches the people who walk into it. It
notices their posture, their stillness, what they hold. Every few seconds
it says something on the third wall: a noticing, a guess about you, a
question whose answer is what you do next. Each night at 4 AM, with the
room empty, it summarises the day and writes a note to itself. Tomorrow
it will read what it wrote.

> **982** thoughts spoken &nbsp;·&nbsp; **45** visits recorded
> &nbsp;·&nbsp; **13 hours, 18 minutes** with someone inside &nbsp;·&nbsp;
> **8** days awake

---

### What it does

A merged 3-D point cloud of the gallery is rendered live onto two of the
walls — the room seeing itself. The third wall shows the room *thinking*:
a short caption, fading in and out, every thirty seconds or so. The
captions are not pre-written. They are constructed in real time from
seven streams of context:

- the live frame, fed to a vision-language model;
- the predicted cortical activation from a brain-encoding model
  watching the same frame;
- pose and motion read by a separate face-mesh + body-tracking worker;
- whether the visitor's CLIP embedding matches an earlier visit
  (the room recognises returns);
- what the room thought a few seconds ago;
- what it wrote to itself last night;
- the time of day, and how long this visitor has been inside.

The model that turns all of that into one short line runs locally. The
brain-encoding model and the image-embedding model run on a separate GPU
machine over the local network. Nothing leaves the building.

---

### What you see

The room speaks quietly. Mostly noticings.

> *"Tall figure, what's in the corner?"*
> *"You stood still, corner of the room."*
> *"Your left hand is resting on the floor while your right hand is
> slightly raised."*
> *"You came back."*
> *"Two of you now."*
> *"You, right here, now."*

It does not greet you. It does not call itself a quiet space. It tries
not to ask the same question twice. Sometimes it doesn't speak at all.

---

### Technical sheet

| | |
|---|---|
| **Sensors** | Two Microsoft Kinect v1, opposing corners |
| **Display** | 3 projectors (2 × point cloud, 1 × caption) |
| **Point cloud pipeline** | Unity, custom point-cloud shader, depth post-processing, persistent background plate |
| **Brain encoder** | TRIBE v2 on RTX 3090, predicting cortical activation from 2-second video clips |
| **Image recall** | open_clip ViT-B-32 on the same GPU, 512-d embeddings per visit |
| **Vision-language** | Local LLaVA via Ollama on the installation PC |
| **Pose / face tracking** | MediaPipe Face Mesh + Pose at ~2 Hz |
| **Memory** | JSON on disk: every thought, every visit, every nightly reflection, every visit thumbnail |
| **Network** | Strong GPU machine reachable on LAN at fixed IP; degraded mode if it goes down |
| **Audio input** | Optional Moog Theremini, MIDI → OSC → cloud colour modulation |

---

### Why this might interest you

— It is one of very few interactive artworks that uses a *predictive
brain model* as an input. Most work that gestures at "neural" stuff uses
the model's *outputs* (i.e. activation as a number); this piece uses the
predicted activation as a contextual feeling that modulates how the room
speaks. The connection is plumbing, not metaphor.

— The week of running it surfaced a long list of small failures in how
large vision-language models default to *help-desk speech*. Every fix is
a small contribution to the question *"what would it take to make a
model sound like a place, not an assistant?"*. The engineering log
documents each one.

— It is a piece that *evolves over time*. By day seven the room had
written itself a nightly note that began *"I noticed that there is a tall
figure who visits me regularly…"* The piece has a memory; the memory
shapes the voice; the voice shapes the next memory. It is small, but it
is real.

— The full pipeline is local — the only thing on the network is the
brain encoder, also in the building. No cloud, no APIs.

---

### Documentation included

- **[`the_room_speaks.md`](the_room_speaks.md)** — a curated literary distillation of the
  room's voice, by theme, week-long.
- **[`architecture.md`](architecture.md)** — Mermaid + ASCII diagrams of the pipeline.
- **[`engineering_log.md`](engineering_log.md)** — the bugs that surfaced during the run and
  the fixes that landed.
- **[`prompt_evolution.md`](prompt_evolution.md)** — fifteen phases of how the prompt got to
  the shape it ended in.
- **[`../charts/`](../charts)** — eight data visualisations: heatmaps, distributions,
  vocabulary, the CLIP recall similarity matrix.
- **[`../analysis/`](../analysis)** — `visualize.py` and `document_export.py`, the scripts that
  produce the charts and a full Markdown export from the run's memory.
- The work online: **[ahmedjmalik.com/the-room](https://ahmedjmalik.com/the-room)**

---

*The piece thought 982 thoughts across eight days. The ones in the
curated transcript are the ones the layered post-filters let through.
Everything in the engineering log was a step toward that filter.*
