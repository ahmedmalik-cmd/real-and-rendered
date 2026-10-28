# src/python

The Python side of the installation. Everything talks over HTTP or OSC on localhost, except the two GPU
servers, which run on a second machine on the same network.

| script | runs on | what it does |
|---|---|---|
| `tribe_kinect_client.py` | installation PC | the perception loop: 2 s clips from the Kinect bridge, async TRIBE v2 call, MediaPipe face + pose, CLIP recall, prompt assembly, LLaVA via Ollama, memory, nightly reflection; serves the latest thought as JSON on `localhost:8183/perception` for Unity |
| `clip_server.py` | GPU PC | open_clip ViT-B-32 (`laion2b_s34b_b79k`) embedding server, FastAPI on port 8001: `GET /health`, `POST /embed` |
| `tribe_server.py` | GPU PC | TRIBE v2 brain-encoder server, port 8000, `POST /predict` (**not in the repo yet**) |
| `theremini_bridge.py` | installation PC | Moog Theremini USB MIDI (CC20 pitch, CC2 volume) → OSC `/theremin/*` → Unity |
| `perception_pipeline.py` | installation PC | the earlier, simpler loop: Unity's `frame.jpg` → LLaVA → OSC `/perception/*` |
| `memory_inspector.py` | anywhere | read-only report on the room's memory files (thoughts, hours, vocabulary, visits, reflections) |

## Install

```bash
cd src/python
pip install -r requirements.txt          # installation PC (Python 3.9–3.12)
pip install -r requirements-gpu.txt      # GPU PC; install PyTorch for your CUDA version first
```

MediaPipe is pinned to `0.10.x`: newer releases dropped the `solutions` API the face and pose tracking uses, and
0.10.x has no build for Python 3.13+, so make the environment with 3.12 or older (`python3.12 -m venv .venv`).
`python-rtmidi` may need a C++ toolchain on Windows; `pip install --only-binary :all: python-rtmidi` avoids it.

## Try it without the installation

You need [Ollama](https://ollama.com) with LLaVA (`ollama pull llava`) and a webcam. The Kinects, Unity, TRIBE and
CLIP are all optional: without them the loop runs "LLaVA-only" and recognizes returns by colour histogram.

```bash
cd src/python
ROOM_CAMERA=0 ROOM_DATA_DIR=./room-data python tribe_kinect_client.py      # 0 = first webcam
ROOM_CAMERA=../../media/video/perceive.mp4 ROOM_DATA_DIR=./room-data python tribe_kinect_client.py   # or a video file
```

(Windows PowerShell: `$env:ROOM_CAMERA="0"; $env:ROOM_DATA_DIR=".\room-data"; python tribe_kinect_client.py`. On a Mac,
allow the terminal to use the camera when macOS asks.)

Someone counts as present while MediaPipe sees a face or body; step out of view for about 10 s and the visit ends,
come back and the room may recognize you. It speaks a line every 30 s or so, in the terminal and at
`http://localhost:8183/perception`, and writes its memory to `./room-data/Memory/`. That folder is git-ignored; it
holds thumbnails of whoever the camera sees, so keep it private. For CLIP recognition, as in the installation, run
`python clip_server.py` (a CPU works; it downloads ~600 MB of weights once) and set `CLIP_URL=http://localhost:8001`.
Read the memory back with `ROOM_DATA_DIR=./room-data python memory_inspector.py`.

## Configuration

All optional; defaults in brackets. See [`../config.example.env`](../config.example.env).

| variable | used by | meaning |
|---|---|---|
| `TRIBE_URL` [`http://localhost:8000`] | client | TRIBE v2 server (the GPU PC's LAN address, or an ngrok URL) |
| `CLIP_URL` [`http://localhost:8001`] | client | open_clip server |
| `OLLAMA_URL` [`http://localhost:11434`] | client, pipeline | Ollama base URL |
| `OLLAMA_MODEL` [`llava`] | client | Ollama vision model |
| `BRIDGE_URL` [`http://localhost:8182`] | client | Kinect bridge (`../kinect-bridge`) |
| `ROOM_CAMERA` [unset] | client | demo source instead of the Kinect bridge: webcam index or video file |
| `ROOM_DATA_DIR` [Unity's `persistentDataPath`] | client, inspector | where `Memory/` and `Presence/` live |
| `ROOM_MEMORY_DIR` | inspector, `../../analysis` | the `Memory/` folder itself |

## Ports and OSC

Kinect bridge 8182 · perception JSON 8183 · Unity OSC 9000 · Ollama 11434 · TRIBE 8000 · CLIP 8001

| OSC address | args | from |
|---|---|---|
| `/theremin/pitch` | float 0..1 | `theremini_bridge.py` |
| `/theremin/volume` | float 0..1 | `theremini_bridge.py` |
| `/perception/llava_response` | string | `perception_pipeline.py` |
| `/perception/llava_latency` | float (s) | `perception_pipeline.py` |

## Options

```bash
python theremini_bridge.py --list              # show MIDI inputs
python theremini_bridge.py --port "Theremini 0"
python perception_pipeline.py --interval 8     # faster cadence
python perception_pipeline.py --prompt-file my_room.txt   # your own room voice
python perception_pipeline.py --frame /path/to/frame.jpg
python memory_inspector.py --recent 20 | --strange | --grep "alone" | --visits | --show-prompt
```
