# Architecture — how the room thinks

Two diagrams of the same system. The Mermaid version renders in GitHub,
Obsidian, Notion, VS Code preview, and a handful of slide tools. The ASCII
version is for terminals, plaintext docs, and when nothing else will render.

---

## Mermaid

```mermaid
flowchart TB
    subgraph G["the gallery"]
        K0["Kinect K0<br>depth + RGB<br>(corner camera)"]
        K1["Kinect K1<br>depth + RGB<br>(corner camera)"]
        PA["Projector A<br>point cloud"]
        PB["Projector B<br>point cloud"]
        PC["Projector C<br>thoughts"]
    end

    subgraph U["installation PC — Unity"]
        BR["BridgePointCloudManager<br>merge clouds, presence,<br>per-person heights"]
        PR["PerceptionReceiver<br>caption<br>sub-system"]
    end

    subgraph P["installation PC — Python"]
        TKC["tribe_kinect_client.py<br>perception loop"]
        CP["_clip_producer<br>2 s clips @ 8 fps"]
        TW["_tribe_worker<br>async TRIBE call"]
        MP["_pose_worker<br>MediaPipe<br>face / hands / motion"]
        REFL["maybe_nightly_reflection<br>4 AM, room empty"]
    end

    subgraph S["3090 PC — strong"]
        TR["tribe_server.py<br>TRIBE v2 brain encoder<br>POST /predict"]
        CL["clip_server.py<br>open_clip ViT-B-32<br>POST /embed"]
    end

    subgraph L["installation PC — local LLM"]
        LV["Ollama<br>LLaVA vision+language"]
    end

    subgraph D["persistent state"]
        RM["room_memory.json<br>history + longterm"]
        VS["visits.json<br>episodic memory"]
        EP["Episodes/*.jpg<br>per-visit thumbnails"]
        PRES["presence.json<br>live count + heights"]
    end

    K0 --> BR
    K1 --> BR
    BR --> PA
    BR --> PB
    BR -->|merged RGB stream| TKC
    BR --> PRES

    TKC --> CP
    CP -->|2 s MP4| TW
    CP -->|single frame| MP
    TW -->|/predict| TR
    TR -->|activation array<br>peak vertices| TW
    TW -->|mean, peak, variance| TKC
    MP -->|face, hands, motion| TKC

    TKC -->|/embed JPEG| CL
    CL -->|512-d embedding| TKC
    TKC --> VS
    TKC --> EP
    PRES --> TKC

    TKC -->|assembled prompt<br>+ frame| LV
    LV -->|brain_text| TKC
    TKC --> RM
    TKC --> PR
    PR --> PC

    RM -.->|injects past reflections<br>and recent thoughts| TKC
    REFL -.->|daily summary| RM

    classDef sensor    fill:#0a0a0a,stroke:#5dd6c1,color:#e8e6e1
    classDef projector fill:#0a0a0a,stroke:#ffb84d,color:#e8e6e1
    classDef compute   fill:#141414,stroke:#444,color:#e8e6e1
    classDef data      fill:#141414,stroke:#ff9d6c,color:#e8e6e1
    class K0,K1 sensor
    class PA,PB,PC projector
    class BR,PR,TKC,CP,TW,MP,REFL,TR,CL,LV compute
    class RM,VS,EP,PRES data
```

---

## ASCII

```
┌─ in the gallery ─────────────────────────────────────────────────┐
   Kinect K0  ──┐                              ┌──▶  Projector A   (cloud view)
                ├──▶  Unity bridge  ──merged──┤
   Kinect K1  ──┘     • point cloud           └──▶  Projector B   (cloud view)
                        • presence + heights
                        • RGB @ 8 fps                ──▶  Projector C   (caption)
                                │
└─ on the installation PC ──────┼──────────────────────────────────┘
                                ▼
                       tribe_kinect_client.py
                                │
                  ┌─────────────┼──────────────┬─────────────────┐
                  ▼             ▼              ▼                 ▼
            2 s RGB clip   single frame   single frame      presence.json
                  │             │              │
┌─ on the 3090 ──┼─────────────┼─────┐        │
   TRIBE v2  ◀───┘             │     │        ▼
   /predict                    │     │   MediaPipe  ──▶  face / hands /
   • activation                │     │   pose worker     motion / time
   • peak vertices             │     │
   /predict                    │     │
                               │     │
   CLIP server  ◀──────────────┘     │
   /embed                            │
   • 512-d ViT-B-32 vector ──┐       │
└────────────────────────────┼──────┘
                             │       │
                  ┌──────────┘       │
                  ▼                  ▼
            visits.json         Ollama / LLaVA
            + Episodes/         (local, vision+language)
                                       │
                                       ▼
                                  brain_text
                                       │
                                       ├──▶  Unity → Projector C
                                       ├──▶  room_memory.json
                                       └──▶  current_visit.thoughts

         At 4 AM, room empty:
            maybe_nightly_reflection()
            • summarise the last 24 h
            • self-critical: did the questions lead anywhere?
            • name one thing to do differently tomorrow
            • append to long-term memory; tomorrow's prompt will read it
```

---

## What each piece does

| component | what it does | runs where |
|---|---|---|
| **Kinect K0 / K1** | depth + RGB at 30 fps each; their feeds are merged into one point cloud in Unity | hardware |
| **Unity bridge** | point-cloud merging, depth post-processing (temporal smoothing, plate hole-fill), presence detection, presence.json writer | installation PC, Unity |
| **`tribe_kinect_client.py`** | the perception loop. Pulls frames, runs the four parallel workers, assembles the prompt, calls LLaVA, writes memory | installation PC, Python |
| **`_clip_producer`** | continuously captures 2 s RGB clips, keeps only the freshest in a maxsize-1 queue, exposes the middle frame to the pose worker | thread |
| **`_tribe_worker`** | async TRIBE call — never blocks the LLaVA loop | thread |
| **`_pose_worker`** | MediaPipe Face Mesh + Pose at ~2 Hz; classifies face orientation, hand position, motion, time elapsed | thread |
| **TRIBE v2 server** | brain-encoding model: predicts cortical activation from a 2 s video clip. Returns per-vertex activation; client averages to scalars | 3090 PC, Python |
| **CLIP server** | open_clip ViT-B-32 image embeddings — 512-d, unit length. Used for visit-to-visit recall | 3090 PC, Python |
| **Ollama / LLaVA** | vision + language model. Constructs the actual spoken thought from the assembled prompt + the current frame | installation PC, local |
| **`maybe_nightly_reflection`** | runs once a day at 4 AM with the room empty; summarises the day, names one thing to do differently | called from the perception loop |
| **`room_memory.json`** | every thought ever spoken (with TRIBE scalars), plus the day-by-day long-term reflections | persistentDataPath |
| **`visits.json`** | per-visit episodes: arrival, duration, peak people, CLIP signature, thoughts spoken during that visit, link to thumbnail | persistentDataPath |
| **`Episodes/`** | one 240×180 JPEG per visit, taken at the arrival moment | persistentDataPath |

---

## Hardware notes

- **Two Kinect v1 sensors** (older PrimeSense depth, 640×480 @ 30 fps).
  Mounted in opposing corners, ~3 m apart, angled inward + slightly down.
- **Installation PC** — runs Unity, the bridge, the perception loop,
  MediaPipe, and the local Ollama / LLaVA. CPU+GPU mid-range. No admin
  required on the strong PC.
- **3090 PC** — runs TRIBE v2 (`tribe_server.py`, port 8000) and the CLIP
  embedding server (`clip_server.py`, port 8001). Reachable over LAN at a
  a fixed address. No admin access from the installation side; both
  servers are user-space Python.
- **Three projectors** — daisy-chained as extended displays. Two show the
  point cloud (calibrated via the in-piece ProjectorCalibrator). The
  third shows the room's spoken thoughts on a black background with
  amber type, against a top-down view of the cloud.
- **Moog Theremini** — USB MIDI controller. CC20 (pitch) and CC2
  (volume) are read by `theremini_bridge.py`, smoothed, and sent over
  OSC to Unity, where they modulate the cloud's hue and shimmer.
