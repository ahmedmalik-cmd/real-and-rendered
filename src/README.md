# src/

The installation's runtime source. **Not added yet:** it lives on the two Windows machines
that ran the piece (the installation PC and the RTX 3090 PC) and will be copied in from there.

## What goes here

```
src/
  python/
    tribe_kinect_client.py   perception loop: clip producer, async TRIBE worker, MediaPipe pose worker,
                             CLIP recall, prompt assembly, LLaVA via Ollama, memory writes, nightly reflection
    tribe_server.py          TRIBE v2 brain-encoder server (POST /predict, port 8000; RTX 3090 PC)
    clip_server.py           open_clip ViT-B-32 embedding server (POST /embed, port 8001; RTX 3090 PC)
    theremini_bridge.py      Moog Theremini MIDI (CC20 pitch, CC2 volume) → OSC → Unity
  unity/
    BridgePointCloudManager.cs   dual-Kinect merge, depth post-processing, background plate, presence
    CaptionProjector.cs          the room's line on the third wall (screen-space overlay, fades)
    PerceptionReceiver.cs        receives the perception loop's output in Unity
    ProjectorCalibrator.cs       in-piece projector calibration
    (shaders / compute)          the point-cloud shader and compute kernels
```

See [`../docs/architecture.md`](../docs/architecture.md) for how the pieces connect.

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
