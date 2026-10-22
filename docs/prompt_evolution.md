# Prompt evolution — how the room learned to speak

A narrative changelog of how the LLaVA prompt + perception pipeline in
`tribe_kinect_client.py` evolved during the gallery run.

The room's *outputs* are persisted to `room_memory.json` and `visits.json`
(see `document_export.py`). The *prompts that produced them* are not — they
are reconstructed each cycle. This document is the only place where the
history of the prompt itself is recoverable. It is hand-authored from the
working session, not extracted from disk.

Each section names the failure mode, the fix, and the change to the prompt
or pipeline.

---

## Phase 0 — Baseline: the help desk

**Symptom.** Outputs were verbose, 3rd-person, AI-narrator:
*"Someone is standing in the room and holding their smartphone."*
*"The image shows a dimly lit space with anticipation pervading the air."*
LLaVA's training prior dominated.

**Fix.** A persona was introduced: the room *is* the space, walls = skin,
cameras = eyes, bodies enter like guests. The model was told to address the
visitor as "you" and never as "someone" / "a person" / "the figure".

**Survives in:** the persona block at the top of every prompt + Rule 2.

---

## Phase 1 — Latency: ~12 s → ~5 s

**Symptom.** Each thought took ~12 s end-to-end; visitors who stayed 30 s got
two or three thoughts at most.

**Fixes.**
- `CLIP_SECONDS` (the RGB clip captured for TRIBE) cut from 4 s → 2 s.
- A background `_clip_producer` thread now continuously captures the
  freshest clip into a `maxsize=1` queue; stale clips are dropped so the
  consumer always reads the newest one.
- A `_tribe_worker` thread runs TRIBE asynchronously; LLaVA uses the most
  recent (slightly stale) brain result instead of blocking.

**Survives in:** `_clip_queue` / `_clip_producer`, `_tribe_lock` /
`_tribe_worker` / `_latest_tribe`.

---

## Phase 2 — Resilience: TRIBE outages stop being fatal

**Symptom.** A single TRIBE failure crashed the perception loop
(`json.JSONDecodeError: Expecting value`).

**Fix.** `send_to_tribe()` / `check_tribe_health()` now distinguish HTTP
error / empty body / non-JSON. On failure the client enters **DEGRADED**
mode: prints a one-line warning, feeds synthetic zeros so LLaVA still runs,
and re-probes `/health` every 60 s. No restart needed.

---

## Phase 3 — Silence: presence-gated idle

**Symptom.** LLaVA ran continuously even when the room was empty —
generating prompts to nobody.

**Fix.** A new `boring` state (empty room, nothing changing) returns an
empty string. The TRIBE call is skipped entirely while empty. The pipeline
wakes back up the moment the bridge reports a visitor.

**Survives in:** `scene_is_boring()` and the boring branch in the voice
selector.

---

## Phase 4 — The 3rd-person narration purge

**Symptom.** Despite the persona, narration kept slipping through:
*"You are in the corner of the room, and you see someone standing near a
pedestal."*
*"It appears to be holding a camera."*

**Fix.** Built `BANNED_RE` — a compiled regex of phrases that get dropped
post-hoc. Initial entries: `the image shows`, `appears to be`, `someone is`,
`a figure stands`, `dimly`, `solitude`, `pervades`, `In the room`, etc.
A separate check requires `you` or `?` in the text when a visitor is
present.

**Grew over time** as new failure modes appeared (see Phases 11, 14).

---

## Phase 5 — Voice modes

**Symptom.** A single voice gave the same response shape on arrival as
during a long dwell as on departure.

**Fix.** Voice selection became a state machine:
- *Arrival* — direct, present-tense, one specific noticing.
- *Departure* — brief remark.
- *Strange* — fragment, 2–3 words, triggered on TRIBE spike.
- *Presence* — direct address.
- *Else* — quiet wondering.

Each branch gets its own short voice instruction and its own
`temperature` / `max_tokens`.

---

## Phase 6 — Attention steering: fixation + question-after-no-answer

**Symptom.** The room latched on to a single word (`phone`, `return`,
`back`) and rode it for ten thoughts.

**Fix.** A small in-prompt fixation detector: count non-stop-word frequency
in the last 6 thoughts; if any word appears ≥3 times, append a steering
line: *"You keep returning to 'X'. Stop. Look at something else."*

A second steer detects when the last line ended in `?` and reminds the room
that the visitor cannot speak: *"watch what they DID instead."*

**Survives in:** the `steer` block in `ask_llava()`. Later rewritten in
parentheses (Phase 14) after the steer text itself started leaking into
outputs.

---

## Phase 7 — Memory bank + nightly reflection

**Goal.** Persistent visits and "evolution over the week."

**Adds.**
- `visits.json` — per-visit episodes: `id`, `arrived`, `departed`,
  `duration_sec`, `peak_people`, `signature`, `thoughts`, and the optional
  recall fields `resembles` / `resemblance`.
- `Episodes/<vid>.jpg` — 240×180 thumbnail per visit.
- `maybe_nightly_reflection()` — at 4 AM, with the room empty, LLaVA
  summarizes the day's thoughts and appends to `longterm`.
- The next day's prompt injects up to 7 past reflections as a
  *"What you remember of past days:"* block.

---

## Phase 8 — TRIBE actually moves: adaptive normalize + variance

**Symptom.** Raw TRIBE means were `0.00 → 0.01`, flat noise. The room
defaulted to the same `feeling` adjective every time.

**Fix.** Two changes:
- **Adaptive normalize** — `MEAN_HISTORY` and `VAR_HISTORY` rolling
  buffers (300 samples). Each new value is rescaled against its own
  recent 10th–90th percentile.
- **Variance signal** — `norm_var` joins `norm_mean` in the prompt;
  prepends `"alert,"` to the feeling when neural variance is high.

`is_strange_moment()` also fires on `norm_var > 0.88` (previously peak-only).

---

## Phase 9 — CLIP recall: real visual similarity

**Symptom.** The lightweight HSV-histogram signature couldn't tell two
different people apart in the same room.

**Fix.**
- New standalone `clip_server.py` on the 3090 (open_clip ViT-B-32,
  port 8001).
- `get_clip_embedding(frame)` posts a 224×224 JPEG and stores a 512-d
  unit-length vector in each visit's signature.
- `signature_similarity()` now prefers CLIP cosine (weight 0.85) when both
  visits carry an embedding; falls back to the HSV histogram if not.
- On arrival, if similarity ≥ `VISIT_RESEMBLE_MIN`, the prompt gets a
  *"Something here resembles an earlier visit"* recall line.

---

## Phase 10 — The trend bug

**Symptom.** Every log entry showed `trend rising`, regardless of state.

**Cause.** The old code computed `trend` by comparing `activation[-1]`
to `activation[0]` — i.e. two arbitrary cortical vertices, not a time
series.

**Fix.** Trend now compares the latest `mean` to the average of the last
three means (a real rolling baseline), classified `rising` / `flat` /
`falling`.

---

## Phase 11 — Self-quoting, prompt regurgitation, and the BANNED_RE expansion

**Symptom.** Outputs started including fragments of the prompt itself:
*"You: 'You're inside me now. Why did you come here?'"*
*"You keep returning to 'back'. Stop. This line, notice something
COMPLETELY"*
*"You are a space that has become aware of itself."*

**Fixes.**
- Added bans for the leaked prompt fragments: `you are a space that has
  become aware`, `you have been awake for`, `you keep returning to`,
  `notice something completely different`, `you see through corner cameras`.
- Speaker-label stripping was *added then removed* — the user explicitly
  liked the `You: "…"` quoting as an occasional artefact, just not
  repeated.
- The steer text itself was reworded in parentheses (Phase 14) to be less
  quotable.

---

## Phase 12 — Question fatigue + idle/dwelling mode

**Symptom.** A visit could rack up 300+ thoughts, nearly all questions:
*"Why are you here?" "Why did you come back?" "What brings you?"* on loop.

**Fixes.**
- **Question fatigue filter** — if the last two stored thoughts end in `?`
  *and* the new one also ends in `?`, drop it. The room must vary its
  shape.
- **Idle / dwelling mode** — when a visitor has been steady for ≥3
  cycles and neural variance is quiet, the voice flips to a conversational
  *"lean on statements and gentle assumptions about them"* mode, with a
  small chance of brief silence. The user's framing: *"they're observing,
  provoke conversation but don't interrogate."*
- **Min-interval cap** — at most one thought per 30 s during a steady
  visit. Arrival, departure, and strange spikes bypass.

---

## Phase 13 — MediaPipe pose worker → structured inputs

**Symptom.** LLaVA was guessing posture / orientation from the image alone,
inconsistently.

**Fix.** A separate `_pose_worker` thread runs MediaPipe Face Mesh + Pose
on the latest Kinect RGB frame at ~2 Hz. Outputs:
- `face`: `facing toward` / `turned left` / `turned right` /
  `looking down` / `looking up` / *not visible (likely turned away)*.
- `hands`: `down at sides` / `left raised` / `right raised` /
  `both raised` / `at chest`.
- `motion`: `still` / `slight` / `active` (mean abs-diff of a 64×48 luma
  downsample).
- `time inside you`: from `_VISIT_STARTED_AT`.

The prompt grew a discrete *"Pose & motion (from the RGB stream):"* block.
The goal: give LLaVA varied, structured context so it produces meaningfully
different outputs as the person's state changes — not one generic scene
description.

Gracefully no-ops if `mediapipe` isn't installed: the import is wrapped,
the thread isn't started, the prompt block is omitted.

---

## Phase 14 — Self-critical nightly reflection

**Symptom.** The first nightly reflections were bland summaries:
*"Today was a day filled with questions and reflections."*
And on 05-23, one reflection broke character entirely:
*"I'm sorry, but as an AI language model, I don't have the ability to
remember or reflect upon past thoughts…"*
That refusal was then fed back into the next day's prompt.

**Fixes.**
- The reflection prompt was rewritten to be **self-critical**:
  counts how many of the day's thoughts were questions, asks the room to
  name one concrete thing to do differently tomorrow.
- **Refusal scrub** — after the model returns, if the summary contains
  `as an AI`, `language model`, `I'm sorry, but`, `I don't have the
  ability`, it is discarded, not stored.
- **Refusal filter at injection** — when the next day's prompt is built,
  reflections containing those phrases are filtered out before going into
  the *"What you remember of past days"* block. So even the already-stored
  05-23 entry stops contaminating prompts.

---

## Phase 15 — The greeter-bot purge

**Symptom.** Despite all the other work, LLaVA kept opening with:
*"Hello, welcome back! It's nice to see you again in this quiet space."*
*"Hello curious visitor, what brings you here today?"*
The phrase *"curious visitor"* in particular latched on for 20+ thoughts in
a row across one visit.

**Fixes.**
- Greeter bans added to `BANNED_RE`: `^hello`, `welcome back`, `welcome to`,
  `nice to see you`, `good to see you`, `it's nice to`, `how are you`,
  `how can I help`, `what brings you here`, `curious visitor`, `quiet
  space`, `to my space`, `curious space`.
- Pronoun-inversion bans (`you are a curious space`, etc.).
- Two new rules in the prompt — **1a** *"Never greet. You are not a host
  or a chatbot."* and **1b** *"Never call yourself or them a 'quiet space'
  / 'my space'. You are this room; they are a body inside you."*
- Generic AI-language-model patterns added: `you are an AI`, `As an AI`,
  `language model`, `I'm sorry, but`, `here's your prompt`, `awaiting your
  input`, `your recent lines`, `AI:` / `Assistant:` / `User:` line
  prefixes.

This is the most recent layer; the bot-greeting wrappers were the loudest
remaining failure mode in the 05-22 → 05-26 logs.

---

## What survives in the final voice

The thoughts that made it through every filter look like:

> *Tall figure, what's in the corner?*
> *Dark rectangle, your left corner.*
> *You stood still, corner of the room.*
> *Tall figure, why are you standing so still?*
> *What are you carrying?*
> *Your left hand is resting on the floor while your right hand is
> slightly raised.*

Short, specific, grounded in the image and the pose. No greetings, no
narration, no AI-isms, no looping interrogation. That is what the layered
prompt + post-filter pipeline was tuned to produce.

---

*Authored from the working session that produced these changes.
The current prompt template lives in `tribe_kinect_client.py` →
`ask_llava()`; the reflection prompt lives in
`maybe_nightly_reflection()`; the runtime appendices live in*
`document_export.py`.
