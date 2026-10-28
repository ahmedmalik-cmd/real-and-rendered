# Python perception + theremin bridges

Two independent scripts that talk to Unity over OSC (single port, **9000**).

```
theremini_bridge.py      Moog Theremini USB MIDI  →  /theremin/pitch  + /theremin/volume
perception_pipeline.py   PerceptionFrames/frame.jpg → Ollama LLaVA → /perception/llava_response
```

## Install

```bash
cd Python
pip install -r requirements.txt
```

`python-rtmidi` may need a C++ build toolchain on Windows; if it errors out,
install the prebuilt wheel:

```bash
pip install --only-binary :all: python-rtmidi
```

## Run

In separate terminals (any order):

```bash
# Theremini → Unity
python theremini_bridge.py            # default: first MIDI port containing "theremini"
python theremini_bridge.py --list     # show all MIDI input names
python theremini_bridge.py --port "Theremini 0"
```

```bash
# LLaVA → Unity   (requires `ollama serve` and `ollama pull llava`)
python perception_pipeline.py
python perception_pipeline.py --interval 8           # faster cadence
python perception_pipeline.py --prompt-file my.txt   # custom room voice
```

## Ports

Unity listens on a single OSC receiver at **localhost:9000**. Both Python
scripts send to that port. Address routing happens Unity-side.

## Where the LLaVA frame lives

Unity writes `<project>/PerceptionFrames/frame.jpg` every ~13 s
(see `GameViewCapture.cs`). The Python script polls that file's mtime and
only sends to LLaVA when it changes.

If you want to run from a different working directory:

```bash
python perception_pipeline.py --frame /full/path/to/frame.jpg
```

## OSC addresses Unity expects

| Address                          | Args            | Source                |
|----------------------------------|-----------------|-----------------------|
| `/theremin/pitch`                | float 0..1      | theremini_bridge.py   |
| `/theremin/volume`               | float 0..1      | theremini_bridge.py   |
| `/perception/llava_response`     | string          | perception_pipeline.py|
| `/perception/llava_latency`      | float (seconds) | perception_pipeline.py|

## Customising the room's voice

The default prompt in `perception_pipeline.py` tells LLaVA to speak *as* the
room — not as an outside observer. Override by passing your own text file:

```bash
python perception_pipeline.py --prompt-file ../prompts/quieter_room.txt
```
