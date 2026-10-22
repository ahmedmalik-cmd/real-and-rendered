# Engineering log

What broke during the gallery run, and what was done about it. The piece
was live for eight days. These are the bugs and rough edges that surfaced
in front of viewers — and how the fixes shaped the work.

---

## The depth pipeline

### The plate's stale snapshot
**Symptom.** The back wall and floor were sliced off along a hard
rectangular edge. The room looked like a postcard with the edges cut.

**Root cause.** The point cloud has a persistent **plate** — pixels that
stay valid long enough get locked into a background snapshot, so brief
dropouts get filled in. The plate had been captured while `maxDepth` was
4 m. After it was raised to 8 m, the plate kept restoring the *old*
4-metre-clipped depth, so the new far-field never showed up.

**Fix.** The plate now auto-resets the moment `minDepth` or `maxDepth`
changes. The depth range is its source of truth; the moment they
disagree, the plate wipes and re-locks at the new range. No more F6
dance after a slider change.

**File.** `BridgePointCloudManager.cs` — added `_lastMinDepth` /
`_lastMaxDepth` tracking and a `ResetPlate()` call inside `BuildClouds`.

### The two clouds drifted apart
**Symptom.** After the plate fix, the back wall *still* had a hard edge —
plus ragged banded blobs floating off to the sides.

**Root cause.** Not one cloud being clipped. Two sensors no longer
overlapping. Someone had pressed *Place sensors from blueprint* which
snapped both Kinects to rough corner positions, throwing away the
fine-tuned calibration. The "edge" was the gap between two unaligned
sensor frames.

**Fix.** Reload saved calibration / re-run ICP. Procedural, not code.
The takeaway is that the blueprint button is destructive — flagged in
the documentation now.

### Projector caption hidden behind the cloud
**Symptom.** The third projector's caption text — the room's spoken
thoughts — kept getting eaten by the point cloud passing in front of it.
You could see half a sentence. "Canvas above the cloud" in Unity's
hierarchy did nothing.

**Root cause.** The caption canvas was `ScreenSpaceCamera`, which places
the UI as geometry at a fixed depth. The point cloud shader writes depth
(`ZWrite On`), so any point closer than the canvas plane drew right
over the text. Hierarchy order doesn't matter to a depth test.

**Fix.** Three layers:
1. Canvas switched to `ScreenSpaceOverlay` — composited after the camera
   finishes drawing. Cannot be occluded by cloud geometry, ever.
2. Semi-transparent dark backing band behind the caption — guaranteed
   contrast over a bright cloud.
3. Black outline around each glyph — keeps the amber text crisp even
   where the band is faint.

All three live under one `CanvasGroup`, so the fade-in / breathe /
fade-out still animate together.

**File.** `CaptionProjector.cs`.

---

## The voice

The bulk of the engineering happened here. The model wanted to sound
like a help desk; the piece needed it to sound like a room. Every
filter and rule below was earned by reading a specific failure in the
logs.

### 1. The 3rd-person purge
LLaVA's default register was *"Someone is standing in the room and
holding their smartphone."* The room is a *first-person space*; that
voice broke the premise. A persona block was introduced — *walls are
skin, cameras are eyes, bodies enter like guests* — and a regex started
dropping any output containing `someone is`, `appears to be`, `the
image shows`, `a figure stands`.

### 2. Latency from ~12 s to ~5 s
The original pipeline serialised everything: capture → TRIBE → LLaVA →
caption. A visitor staying 30 seconds got two thoughts at most.

Fix: a background **clip producer** continuously captures the freshest
2 s clip into a single-slot queue; an async **TRIBE worker** runs the
brain call in another thread; LLaVA reads the most recent (slightly
stale) brain result instead of blocking. End-to-end latency dropped to
~5 s per thought.

### 3. The trend bug
`trend` (rising / flat / falling) was computed by comparing
`activation[-1]` to `activation[0]` — two *arbitrary cortical
vertices*. Not a time series. It always read "rising," noise.

Fix: trend now compares the latest mean to the rolling average of the
last three. A real time series. Suddenly the prompt's "feeling"
adjective actually moved.

### 4. The fixation problem
The room latched on to single words and rode them for ten thoughts:
*phone, return, back, return, back, again, return…*

Fix: the prompt grew a small in-context analyzer. Count non-stop-word
frequency in the last six thoughts; if any word appeared three or more
times, append a steering line: *"You keep returning to 'X'. Stop.
Look at something else."*

### 5. The steer became the output
The fixation steer worked — but then the model started *quoting it back*
as a thought: *"You keep returning to 'back'. Stop. This line, notice
something COMPLETELY"*.

Fix: the steer text was reworded in parentheses (less quotable), and
the regurgitated phrases were added to the banned list.

### 6. The May 23 refusal
The nightly reflection at 4 AM on 05-23 produced:
*"I'm sorry, but as an AI language model, I don't have the ability to
remember or reflect upon past thoughts or experiences in the same way a
human would…"*

That refusal was then stored as the room's long-term memory and fed
into the next morning's prompt, where it tried to act as a guide to
how the room should think. The model had broken character, written a
chatbot refusal, and then been told to *believe it.*

Two fixes:
1. The reflection prompt now opens with *"You are NOT an AI assistant,
   you are NOT a language model"* — and any reply containing those
   phrases is discarded before being saved.
2. Existing reflections that already contained refusal patterns are
   filtered out at injection time, so the 05-23 entry sits on disk but
   never reaches a prompt again.

### 7. The "curious visitor" plague
By 05-24 the model had latched onto a single phrase — *curious
visitor* — and used it 20+ times in a row. Plus *hello there*,
*welcome back*, *nice to see you*, *what brings you here today*. The
room sounded like a greeter at a tech demo.

Fix: a heavy purge of greeter-bot wrappers in the banned-phrase list
(`hello`, `welcome`, `nice to see you`, `curious visitor`, `quiet
space`, `my space`), plus two explicit rules in the prompt: *"Never
greet. You are not a host or a chatbot."* and *"Never call yourself or
them a 'quiet space.' You are this room; they are a body inside you."*

### 8. The 170-thought visit
One 4.5-hour dwell racked up 170 thoughts. Mostly variations of *why
did you come back?* The conservative gates weren't enough by
themselves.

Fix: a **hard 30-second minimum interval** between thoughts during a
steady visit. Arrival, departure, and strange-mode spikes bypass.
Combined with the question-fatigue filter (drop the third question in
a row), the long-dwell loop now produces something closer to one
thought every ~45 s — and the ones that land aren't all questions.

### 9. The idle visitor problem
When someone stood still observing an object, the room kept asking
questions and got no answer. It was bad conversation.

Fix: an **idle / dwelling mode**. When a visitor has been steady for
3+ cycles and neural variance is low, the voice mode flips to
*"converse with them — lean on statements and gentle assumptions, not
questions. The answer is in their stillness."* 18% chance of brief
silence. Question fatigue still applies. The voice goes from
interrogator to companion.

### 10. The conversation that can't get a reply
The room asks a question; nobody can speak; what now? The model
defaulted to asking it again.

Fix: a `last_was_question` steer. *"Their reply is in what they DID —
stayed, shifted, drew closer, turned away. If they stirred, take it as
an answer and respond to it. If nothing changed, let it go and say
something new — a statement, a guess."*

---

## TRIBE

### The brain signal was flat
Raw TRIBE means were `0.00 → 0.01`. The "feeling" adjective never moved
because the input never moved.

Fix: **adaptive normalisation**. Each new mean / variance is rescaled
against its own rolling 10th–90th percentile (300-sample window). The
flat tiny values gain spread, and the prompt sees genuine state
changes. A `norm_var > 0.88` also became a trigger for *strange mode*
— a vivid neural moment that earns a fragment-shaped response.

### The TRIBE server going down was fatal
A single failed JSON parse used to crash the perception loop.

Fix: a **degraded mode**. On failure the client prints one warning,
feeds synthetic zeros so LLaVA still runs, and re-probes `/health`
every 60 s. The piece kept thinking even when the brain was
unreachable.

---

## Recall

### Two different visitors looked "the same"
The first recall implementation used a lightweight HSV-colour
histogram — it could tell day from night, but it couldn't tell two
people in the same lighting apart.

Fix: a **CLIP embedding server** on the strong PC. Every visit's
arrival frame is embedded into a 512-d vector. Visit-to-visit
similarity is the cosine of those vectors. Suddenly the room could
say *you came back* and mean it — because the embedding had matched a
specific past visitor, not a colour palette.

The 0.84 similarity threshold needed tuning: CLIP same-room baseline
is already high (everything's in the same room) so the discriminator
is the *body*. Watching the recall logs tuned the threshold from below.

---

## Posture

### LLaVA was guessing posture inconsistently
Sometimes it would notice a hand, sometimes a posture, sometimes
nothing — and the prompt couldn't tell it *which*.

Fix: a **MediaPipe pose worker** on its own thread, reading the Kinect
RGB at 2 Hz. It produces four discrete labels — `face`, `hands`,
`motion`, `time inside you` — and they go into the prompt as a
structured block. The model went from guessing to reading. The best
thoughts of the run came from that block:

> *Your left hand is resting on the floor while your right hand is slightly raised. You are standing upright with a relaxed posture.*

That sentence was the room reading a structured input out loud.

---

## Hardware

- **Theremini** sent constant data (`pitch=1.000 vol=0.000`) — the
  device hadn't been calibrated and the volume sensor was reading
  closed. The volume → point-size feature was removed entirely; the
  pitch → hue shift stayed. Procedural fix needed (Moog Editor); no
  code fix possible.
- **Laptop lid closing** caused the projectors to lose their displays
  and Unity to throw windows on the wrong outputs. Workaround:
  configure the lid-close action to "do nothing." No dummy plug
  available.
- **The plate / depth-range coupling bug** above was a real engineering
  bug. The others in this section were operational.

---

## What didn't get fixed in time

- **TRIBE region-mapping (paper-side analysis).** The brain encoder
  returns a *per-vertex* activation array; the runtime averages it down
  to one scalar. The discriminative spatial structure is thrown away
  every cycle. A version that maps `peak_vertices` to anatomical
  regions and uses that as an additional prompt input is the work for
  the paper, not this run.
- **The Theremini.** Never got calibrated in position; the colour
  layer's potential was never fully realised.
- **Prompt logging.** The full constructed prompts were never persisted
  to disk this run. Only outputs survived. The history of the prompt
  itself is recoverable only from the working session.

---

*The piece thought 982 thoughts across eight days. The ones in
`the_room_speaks.md` are the ones the layered filter pipeline let
through. Everything in this log was a step toward that filter.*
