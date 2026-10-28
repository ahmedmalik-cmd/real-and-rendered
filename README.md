# real & rendered

**A room that sees, thinks and remembers.** An eight-day gallery installation in which two depth sensors merge into one live point cloud, a brain-encoding model predicts how a viewer's cortex would respond to the scene, and a local vision-language model gives the room a first-person voice, with a memory of who came back.

**Ahmed Jamal Malik** · Senior thesis, Bennington College · Usdan Gallery, 18–26 May 2026 · [ahmedjmalik.com/the-room](https://ahmedjmalik.com/the-room)

![The gallery as a live, merged point cloud from two Kinect sensors](media/hero.webp)

> **982** thoughts spoken · **45** visits recorded · **13 h 18 m** with someone inside · **8** days running · **5** nightly reflections

---

## The work

An eight-day gallery installation that gives a room machine perception and a voice: two depth sensors, a brain-encoding model, and a vision-language pipeline that turn each visitor's presence into a live point cloud and a stream of short, spoken perceptions.

The work stages the room as a mirror (an artificial system assembling a reflection of human consciousness from whatever it can sense) and attends to what gets lost in the translation. It asks what survives when a person in a room is reduced to a vector and four words of English, and treats the machine's falling-short not as a failure but as the subject. It is not a claim that the machine is conscious; it argues the opposite.

| | |
|---|---|
| ![The gallery: point cloud projected on two walls, the wire hand on its plinth](media/installation/gallery-overview.jpg) | ![The sensor rig: Kinect, cabling and projector in a corner of the gallery](media/installation/sensor-rig.jpg) |
| The gallery: the merged cloud on two walls, the wire hand on its plinth | A sensor corner, with its cabling |

## See it running

| [![A visitor inside the live point cloud](media/video/perceive.jpg)](media/video/perceive.mp4?raw=true) | [![Walkthrough: the wire hand, then the cloud thrown on the wall](media/video/gallery-walkthrough.jpg)](media/video/gallery-walkthrough.mp4?raw=true) |
|---|---|
| **Perceiving.** A visitor inside the live cloud, with the bridge's HUD (18 s) | **In the gallery.** The wire hand, then the cloud and the room's line on the wall (22 s) |
| [![Orbiting the merged cloud](media/video/room-orbit.jpg)](media/video/room-orbit.mp4?raw=true) | [![The projected cloud under its ceiling projector](media/video/projection-wall.jpg)](media/video/projection-wall.mp4?raw=true) |
| **The room, seen by itself.** An orbit of the merged two-sensor cloud (15 s) | **On the wall.** The cloud under its ceiling projector (19 s) |

*Click a still to play the clip.*

## How it works

- **Two Kinect v1 sensors** in opposite corners, merged in Unity into one calibrated point cloud (~140,000 points per frame) and projected onto two walls.
- **A perception loop** in Python takes 2-second clips and single frames from the merged stream and runs four workers in parallel:
  - **TRIBE v2**, a brain-encoding model on a separate RTX 3090 machine, predicts cortical activation for each clip.
  - **MediaPipe** Face Mesh + Pose reads orientation, hands, stillness and motion as discrete labels.
  - **open_clip ViT-B-32** embeds each arrival frame, so the room can recognize a returning visitor.
  - **LLaVA via Ollama**, running locally, turns the frame plus all of that context into one short line, shown on the third wall.
- **Memory.** Every thought, every visit and a nightly reflection are written to disk. At 4 AM, with the room empty, it summarizes the day and writes itself a note that the next day's prompt reads.
- **Everything runs locally.** No cloud APIs. The only network hop is the GPU machine in the same building.

![System map: sense → fuse → perceive → speak → show, with the GPU host and memory](media/system-map.svg)

Mermaid and ASCII versions, plus a component table: **[docs/architecture.md](docs/architecture.md)**

## Engineering highlights

From **[docs/engineering_log.md](docs/engineering_log.md)**, written during the live run:

- **Latency from ~12 s to ~5 s per thought.** A serial pipeline (capture → brain model → language model → caption) became a single-slot clip producer plus an async TRIBE worker, so the language model reads the freshest brain result instead of waiting for it.
- **Recognition without a database.** CLIP embeddings served from the GPU machine, plus a persistent visit log, let the room recognize returning visitors across days (cosine 0.84 threshold, tuned from the recall logs).
- **A usable brain signal.** Raw activations sat between 0.00 and 0.01; rescaling against a rolling 10th–90th percentile gave the prompt real state changes. A "trend" metric that compared two arbitrary cortical vertices was rebuilt as an actual time series.
- **Graceful failure.** If the GPU machine drops, the client feeds synthetic zeros, keeps speaking, and re-probes `/health` every 60 s.
- **Depth pipeline fixes.** A background plate that kept restoring a stale depth range, sensor calibration drift, and caption text occluded by depth-writing point shaders.
- **Making a model sound like a place, not an assistant.** A first-person persona, regex filters for third-person and greeter speech, a fixation detector that steers off repeated words, question fatigue, an idle mode for still visitors, and refusal filtering before anything reaches long-term memory.

How the prompt got there, in fifteen phases: **[docs/prompt_evolution.md](docs/prompt_evolution.md)**

## Process

| | |
|---|---|
| ![A first Kinect experiment: a sparse point field](media/process/first-point-field.jpg) | ![Infrared ChArUco calibration of the two sensors](media/process/charuco-calibration.jpg) |
| An early test: a Kinect point field in Python ("kinect theremin demo") | Calibrating the two sensors against a ChArUco board in infrared |
| ![Dual-Kinect fusion with the perception debug panel](media/process/fusion-debug.jpg) | ![Depth post-processing and room-blueprint controls](media/process/depth-controls.jpg) |
| Dual-Kinect fusion with the perception debug panel | Depth post-processing: EMA smoothing, deadband, plate fill, per-sensor timing |
| ![An early caption: the model describing the room as an assistant would](media/process/helpdesk-voice.jpg) | ![The room's line on the wall, before tuning: third-person, help-desk](media/room/caption.jpg) |
| Early on, the model talked like an assistant | Before tuning: third person, describing rather than noticing |

## Results

| | |
|---|---|
| ![Activity heatmap](charts/02_thoughts_per_hour_heatmap.png) | ![CLIP recall matrix](charts/05_clip_recall_matrix.png) |
| Thoughts per hour, per day | Visit-to-visit CLIP similarity: how the room recognized returns |
| ![Dwell time](charts/03_dwell_distribution.png) | ![Voice modes](charts/08_voice_modes_pie.png) |
| How long visitors stayed | The shapes of the room's voice |

*The charts are drawn from the complete log, which runs to 30 May: the system kept running for four days after the exhibition closed, so their counts (e.g. 121 visits) are higher than the exhibition figures at the top of this page.*

What the room said, curated by theme: **[docs/the_room_speaks.md](docs/the_room_speaks.md)** · one-page overview: **[docs/one_pager.md](docs/one_pager.md)**

## Gallery

| | | |
|---|---|---|
| ![Top-down view of the merged cloud with the live read-out](media/room/cloud-hud.jpg) | ![A visitor inside the merged cloud](media/room/cloud-body.jpg) | ![The gallery corner and the plinth in the point cloud](media/room/cloud-corner.jpg) |
| Top-down, with the live read-out: presence, TRIBE activation, theremin | A visitor inside the merged cloud | The gallery corner. The room kept calling the plinth a dark rectangle, a white tower, never a hand |
| [![The wire hand](media/installation/wire-hand.jpg)](media/video/wire-hand-orbit.mp4?raw=true) | ![The merged cloud projected on the wall](media/installation/cloud-on-wall.jpg) | ![Projection, cabling and the plinth from another angle](media/installation/projector-corner.jpg) |
| A wire hand reaching up out of the floor: what the sensors were pointed at all week ([orbit clip](media/video/wire-hand-orbit.mp4?raw=true)) | The merged cloud on the wall, with its projector | Projection, cabling and the plinth from another angle |

## Repository

```
docs/        architecture, engineering log, prompt evolution, one-pager, curated transcript
src/         the installation's source; see src/README.md
  python/        perception loop, CLIP server, Theremini bridge, memory inspector
  unity/Assets/  Unity 2022.3 scripts and shaders: dual-Kinect merge, depth post-processing,
                 projector calibration and warp, captions, presence
  kinect-bridge/ C# (.NET Framework 4.8) service serving both Kinects' depth + colour over HTTP
  scripts/       Windows launchers with auto-restart
analysis/    turns the run's memory into charts, a full Markdown export and video collages
examples/    a synthetic memory folder (same shape as the real one) to run the analysis on
charts/      the eight charts from the real run
media/       stills, installation photos, process screenshots, short clips
tools/       privacy_check.py: scans for IPs, personal paths, keys and photo/video metadata
```

### Try the room with a webcam

The perception loop runs without the installation: it needs [Ollama](https://ollama.com) with LLaVA and a webcam
(or a video file). The Kinects, Unity, TRIBE and CLIP are optional; without them it runs LLaVA-only.

```bash
cd src/python && pip install -r requirements.txt && ollama pull llava
ROOM_CAMERA=0 ROOM_DATA_DIR=./room-data python tribe_kinect_client.py
```

It speaks a line every 30 s or so and keeps its memory in `./room-data` (git-ignored). More in
[src/python/README.md](src/python/README.md#try-it-without-the-installation). Still to be added from the GPU machine:
`tribe_server.py`, the TRIBE v2 server.

### Run the analysis

The real memory is private, so the repo ships a synthetic one with the same structure:

```bash
pip install -r requirements.txt
ROOM_MEMORY_DIR=examples/sample_memory python analysis/visualize.py        # → out/charts/
ROOM_MEMORY_DIR=examples/sample_memory python analysis/document_export.py  # → out/room_documentation_*.md
```

On the installation PC the scripts read Unity's data folder directly when `ROOM_MEMORY_DIR` is not set.

## Stack

Unity 2022.3 (C#, custom point-cloud shader) · C# / .NET (Kinect bridge) · Python · FastAPI · Kinect v1 × 2 · TRIBE v2 · open_clip ViT-B-32 · MediaPipe · Ollama / LLaVA · OSC + MIDI (Moog Theremini) · three projectors

## Privacy

Visitors were never identified. The room knew people only as anonymous image embeddings, and everything stayed on machines in the gallery. The raw visit log, embeddings, per-visit thumbnails and memory files are **not** in this repository. Faces in development screenshots are pixelated, hardware identifiers are redacted, and all photos and videos have had their metadata (including location) removed. `tools/privacy_check.py` runs before anything is pushed.

---

© 2026 Ahmed Jamal Malik. Code is under the [MIT License](LICENSE). Images, video and written documentation are all rights reserved.
