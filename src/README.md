# src/

The installation's runtime source, copied from the installation PC and cleaned for release
(machine addresses moved to environment variables, personal paths made relative).

## What's here

```
src/
  config.example.env         environment variables (TRIBE_URL, CLIP_URL, OLLAMA_URL, ...)
  python/
    tribe_kinect_client.py   perception loop: clip producer, async TRIBE worker, MediaPipe pose worker,
                             CLIP recall, prompt assembly, LLaVA via Ollama, memory writes, nightly reflection
    clip_server.py           open_clip ViT-B-32 embedding server (POST /embed, port 8001; RTX 3090 PC)
    theremini_bridge.py      Moog Theremini MIDI (CC20 pitch, CC2 volume) → OSC → Unity
    perception_pipeline.py   earlier, simpler LLaVA loop (Unity frame.jpg → Ollama → OSC); see python/README.md
    memory_inspector.py      prints a summary of the room's memory files (reads them locally, writes nothing)
    requirements.txt         installation PC; requirements-gpu.txt for the RTX 3090 PC
  unity/Assets/              scripts and shaders from the Unity 2022.3 project "DualKinectPointCloud"
    Scripts/Bridge/BridgePointCloudManager.cs      dual-Kinect merge, depth post-processing, background plate, presence
    Scripts/Projection/CaptionProjector.cs         the room's line on the third wall (screen-space overlay, fades)
    Scripts/Perception/PerceptionReceiver.cs       receives perception output over OSC
    Scripts/Perception/TribePerceptionReceiver.cs  polls tribe_kinect_client.py (localhost:8183/perception)
    Scripts/Projection/ProjectorCalibrator.cs      in-piece projector calibration
    Scripts/...                                    background model, calibration, OSC, point cloud,
                                                   post-processing, projection warp, Theremini input
    Editor/SceneSetup.cs                           builds the scene's object graph from a menu item
    Shaders/PointCloud.shader, QuadWarp.shader     point-cloud rendering and projector keystone warp
    StreamingAssets/CalibrationData/calibration.json  the two Kinects' poses (position/rotation only)
  kinect-bridge/             ThereminKinectBridge: reads both Kinect v1 sensors and serves depth, colour and
                             skeleton/face data over HTTP at localhost:8182 (Program.cs, KinectBridge.csproj)
  scripts/                   Windows launchers with auto-restart (launch_all.bat starts everything in order)
```

`.meta` files are kept so Unity keeps the script GUIDs. The scene (`main.unity`), materials and the
room's 3D scan (`usdan_space.fbx`) are not included; `Editor/SceneSetup.cs` expects the FBX at
`Assets/Models/usdan_space.fbx`.

See [`../docs/architecture.md`](../docs/architecture.md) for how the pieces connect.

## Still missing

- **`tribe_server.py`** (TRIBE v2 brain-encoder server, POST /predict, port 8000). It is on the RTX 3090 PC
  or a backup drive, not on the installation PC. Until it is added, `tribe_kinect_client.py` runs in
  "LLaVA-only" mode when it can't reach TRIBE.
- **Kinect SDK DLLs** for `Assets/Plugins/`, which come from the Kinect for Windows SDK and aren't redistributed here.
- No compute shaders: the point cloud is drawn by `PointCloud.shader` alone.

## How to run

Two machines, on the same network.

**RTX 3090 PC** (TRIBE + CLIP)

```bash
pip install -r src/python/requirements-gpu.txt
python src/python/clip_server.py        # listens on 0.0.0.0:8001
python src/python/tribe_server.py       # port 8000 (not in the repo yet)
```

**Installation PC** (Kinects, Theremini, projectors, Ollama, Unity build)

1. `pip install -r src/python/requirements.txt`; install [Ollama](https://ollama.com) and `ollama pull llava`.
   Install the Kinect for Windows SDK 1.8 and Developer Toolkit 1.8 (default `C:\Program Files` locations,
   which `KinectBridge.csproj` references), then build the bridge (x64 Release, .NET Framework 4.8):
   `msbuild src/kinect-bridge/KinectBridge.csproj` → `src/kinect-bridge/bin/Release/ThereminKinectBridge.exe`.
2. Set the variables from [`config.example.env`](config.example.env) (at least `TRIBE_URL` and `CLIP_URL`
   pointing at the 3090 PC), plus `BUILD_EXE` / `BRIDGE_EXE` if your build and Kinect bridge live elsewhere.
3. Build the Unity project (Unity 2022.3) with these `Assets/` files, then run `src/scripts/launch_all.bat`.
   It starts the Kinect bridge, Ollama, `theremini_bridge.py`, `tribe_kinect_client.py` and the Unity build,
   each in its own window that restarts on a crash. `install_autostart.bat` adds it to Windows startup.

Ports: Kinect bridge 8182, perception JSON 8183, Unity OSC 9000, Ollama 11434, TRIBE 8000, CLIP 8001.
The room's memory (`Memory/`, `Presence/`) is written under Unity's `Application.persistentDataPath`
(`%USERPROFILE%\AppData\LocalLow\DefaultCompany\DualKinectPointCloud` on Windows) and never into the repo.

## Before adding code

1. **No addresses in code.** Read machine addresses from the environment, not literals:

   ```python
   TRIBE_URL  = os.environ.get("TRIBE_URL",  "http://localhost:8000")   # the GPU machine
   CLIP_URL   = os.environ.get("CLIP_URL",   "http://localhost:8001")
   OLLAMA_URL = os.environ.get("OLLAMA_URL", "http://localhost:11434")
   ```

   and document them in a `config.example.env` (never commit the real `.env`).
2. **No personal paths.** Use `Path.home()`, Unity's `Application.persistentDataPath`, or a relative path
   instead of `C:\Users\<name>\...`.  <!-- privacy-ok: an example of what not to commit -->
3. **No visitor data.** `Memory/`, `Episodes/`, `visits.json`, `room_memory.json` and `presence.json` are
   git-ignored; keep it that way.
4. **Unity:** copy only `Assets/` scripts, shaders and the needed settings, never `Library/`, `Temp/`,
   `Logs/`, `Builds/` or `UserSettings/` (also git-ignored). Player logs contain machine paths and GPU details.
5. **Run the privacy check** from the repo root and fix anything it reports:

   ```bash
   python tools/privacy_check.py
   ```
