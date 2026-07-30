# The Flow XR — Claude Code context

A WebXR experience where the player witnesses a world being born, structured on
the Dao De Jing (道生一，一生二，二生三，三生万物). The player never *creates*
the world by force — they stay in harmony with it and it reveals itself.
Target: **Quest 3** (hand tracking).

## Run

```
npm run dev      # https://localhost:8081  (mkcert HTTPS, required for WebXR; auto-opens)
npm run build    # → dist/
npx tsc --noEmit -p tsconfig.json   # typecheck (do this after edits)
```

On Quest over LAN: `https://<PC-IP>:8081` (accept the cert warning), or
`adb reverse tcp:8081 tcp:8081` then use `https://localhost:8081` on the headset.

## Stack

- **IWSDK** (`@iwsdk/core`) — WebXR session + ECS (systems/entities/components), locomotion, grab, hand input.
- **SparkJS** (`@sparkjsdev/spark`, v2.0.0-preview) — Gaussian splat rendering + the `dyno` shader-graph used for all splat effects.
- **three** = `super-three@0.181.0` (Vite plugin dedupes IWSDK's bundled r177).
- Vite + TypeScript. Node ≥ 20.19.

## Story

**[STORY.md](STORY.md) is canonical.** Read it before changing anything about
what a scene *means*. It also carries a table of where this build currently
diverges from the intended narrative — the largest being that scenes 3 and 4 are
meant to share ONE repeating circular gesture, which is not what is built.

## The experience: ONE continuous session, phases not scenes

Never separate apps or hard scene-loads — the philosophy depends on unbroken
continuity. Sequenced by the Director:

- **Scene 0 / 一 Breath** — pure white void, silent but for a faint synthesised heartbeat (`heartbeat.ts`, 54 BPM). A grey ring pulses ON the heartbeat a step ahead; the player physically STEPS INTO it to begin. No ground, no sky, no UI — the loading gate is a bare white field (`SHOW_LOADING_INDICATOR` restores a dev spinner). Voice line "Before all things, there was only Dao." not yet recorded.
- **Scene 1 / 二 Disc** — a flat disc opens under the player's feet, 0 → `DISC_RADIUS` (2 m) over `DISC_GROW_SECONDS` (2.5 s), cubic ease-out. **No splat in this phase.** The disc is PLACEHOLDER geometry to be swapped for real mesh. Holds via `HOLD_AFTER_DISC`.
- **Scene 2 / 三 Reveal** — the splat world is revealed BY HAND: `railProgress` drives the reveal wavefront directly, so the world only exists as far as the player has swept. Scrub, not trigger.
- **Scene 3 / 四 Morph** — the bamboo cross-dissolves into the mountain temple. Two-handed curved inward sweep.
- **Scene 4 / 五 Expand** — the mountains open into the celestial world. Hands part outward.
- **Scene 5 / 六 Resonance** — nothing transitions; the sweep turns the orbital system instead (`cosmos.ts`, 17 planets on 7 orbits). The planets answer the movement rather than being carried by it.
- **Scene 6 / 返 Return** — the cosmos dissolves into 道 (`dao.spz`). `advance()` stops here: the markers leave and do not come back.

The two-splat cross-fade (`splatMorph.ts`) is **central**, not unused — 四, 五 and 返 are all the same cross-dissolve restaged onto the next pair of worlds.

## The three gestures

There are only three rail layouts, used by five phases — the last two are
deliberate reprises, and the story turns on the gesture NOT changing while what
answers it does. `RailConfig.gesture` names them, and the name selects the hand
graphic.

| | Layout | Motion | Phases |
|---|---|---|---|
| **举 Lift** | `configureForReveal()` | both hands rise, chest 1.0m → eye 1.55m | 三 |
| **合 Gather** | `configureForMorph()` | opposed arcs sweeping inward to meet — the taiji | 四, then 六 |
| **开 Open** | `configureForExpand()` | both hands part outward from the midline | 五, then 返 |

Order through the journey: **举 → 合 → 开 → 合 → 开.** After the opening lift it
is gather/open alternating, which is a breath.

## How gestures drive the world

**Marker POSITION, throughout** — the rails are a scrub, and carrying the
markers from one end to the other is what completes a transition.

- 三 reads the right rail alone: `railProgress` → the reveal wavefront.
- 四 / 五 / 六 / 返 read both hands averaged: `bothProgress` → the transition's
  `setPhase()` (or `cosmos.setProgress()` in 六). Averaged, so one hand alone
  gets halfway and the transition needs both.
- Reaching the end of the line is the event: crossing `GESTURE_COMPLETE_AT`
  (0.97, not 1.0 — the last millimetre is unreachable in practice) flares both
  markers for `GLOW_SECONDS` and hands off. See `updateHandoff()`, which
  **latches** on the first completed frame: the markers stay under the player's
  fingers afterwards, so drift would otherwise drop progress back under the
  threshold and freeze the flare with the advance never firing.

A time-paced alternative ("hands sustain a flow, the world crosses at its own
rate") was built and rejected on 2026-07-29 — it broke the directness of
sweeping a world into being. Position-driven is deliberate.

### The three rules that shape a gesture

Designed as one system in `handFollowCube.ts`; each is nearly useless without
the others, so change them together or not at all.

**1. Ratchet.** A marker never travels back toward `from`. Nothing the player
has drawn out of the world retreats, and progress stays monotonic so the
completion latch cannot chatter. A backward hand is *not* ignored, though — the
reference point follows it back, or the fingertip would walk away from a marker
that refuses to move and exceed `RELEASE_RADIUS` within one stroke. Following it
back is also what makes the gesture **pumpable**: push, return, push again,
which is how a 0.30m rail gets carried by a repeating circular motion.

**2. Speed drops the marker — measured on the MARKER, not the hand.** This
distinction is the whole thing, and getting it wrong produced three separate
false alarms: pulling away from a marker (fast, moves nothing), arriving at the
end stop (fast, but clamped), and drawing back for another stroke (fast, but
ratcheted). All three read as "too fast" while hand speed was the input. The
test now runs *after* the marker moves and *after* the clamp, so only actual
sweeping counts. `DEACTIVATE_SPEED` (0.25 m/s of marker advance) over
`DEACTIVATE_DWELL`, then a `LOCKOUT_SECONDS` pause. **The ratchet is what makes
this humane** — a drop costs a pause, never the progress already earned.

**3. Two hints, mutually exclusive.** A marker goes quiet for exactly two
reasons and each gets its own answer:

| Cause | Shown | Where |
|---|---|---|
| dropped for speed | the word `"slowly"` | on the offending marker |
| hand simply left, or never arrived | that hand's gesture graphic (`public/ui/HandGestureGraphic/`) | in front of the marker |

Both live on the **Rail**, not the system, so they sit on the marker they are
about to talk about. An earlier centred version failed: the marker died in one
place and a word appeared elsewhere a moment later, with no way to tell which
hand was meant. Placement carries all of that wordlessly, which is also why
`HINT_DELAY` could drop to 0.15.

Nothing explains itself in a sentence. While locked out, the marker **brightens
back toward normal in step with the pause it is serving** — it is its own
countdown, answering "is it broken or is it coming back?" without language.

Speed thresholds form a hysteresis exactly as the radii do: `GRAB_MAX_SPEED`
(0.6) to catch, the looser `DEACTIVATE_SPEED` to keep, `REARM_SPEED` (0.25) to
be given it back. `REARM_SPEED` is absolute and NOT derived from
`DEACTIVATE_SPEED` — they measure different things now, and the old derivation
silently demanded a motionless hand whenever the marker rule was tightened.

Distances, from the marker's centre: marker **0.14m across**, `TOUCH_RADIUS`
**0.32**, `RELEASE_RADIUS` **0.44**. The trigger volumes overlap the 0.52m
between the rails; harmless, because `findSource()` binds each hand to its own
rail and the cross comparison never happens. A source reporting `handedness:
"none"` drives neither rail — `checkHandedness()` warns about that, because from
inside the headset it is indistinguishable from a badly tuned grab radius.

## Source map (`src/`)

- **index.ts** — bootstrap. `SCENE_MODE` = `"flow"` (Director, the real thing) | `"reveal"` | `"morph"` (standalone harnesses). Contains the IWER emulator workaround + a DOM "enter" button (styled faint so Scene 0 stays UI-free).
- **director.ts** — `DirectorSystem`. Phases are `"breath" | "disc" | "reveal" | "morph" | "expand" | "resonance" | "return"`. Owns `GESTURE_COMPLETE_AT` + `updateHandoff()` (the flare-and-advance beat), the per-world splat constants (`REVEAL_SPLAT`, `MORPH_SPLAT`, `EXPAND_SPLAT`, `RETURN_SPLAT`) and their per-world `flipUp` / Y / yaw corrections. Seed circle + step-in detection, white loading gate, phase-driven background (`VOID_COLOR` white → `WORLD_COLOR` once a world exists, so splat gaps don't glare through), preloads the splat, disc growth, cube visibility. Flags: `HOLD_AFTER_DISC`, `SHOW_LOADING_INDICATOR`, **`START_PHASE`** (dev: jump straight to a scene — note it CANNOT meaningfully test scene 1, whose content is a step-in-triggered animation). Tuning: `CIRCLE_RADIUS`, `CIRCLE_FRONT_Z`, `DISC_RADIUS`, `DISC_GROW_SECONDS`, `BEGIN_ARM_SECONDS`, `RAIL_FALLBACK`, `DWELL_SECONDS`.
- **heartbeat.ts** — synthesised WebAudio heartbeat, the only sound in Scene 0. No asset file. Dials: `HEARTBEAT_BPM`, `PEAK_GAIN` (deliberately tiny), `THUMP_HZ`. Needs a user gesture to start (the `enter` click).
- **splatReveal.ts** — `SplatRevealSystem`. Scene-1 radial spread reveal (dyno **worldModifier**). `grow(seconds)` auto-reveals then latches; `setProgress()` manual; `isReady`/`isRevealed`; `reset()` before unload. `MAX_RADIUS` = wavefront reach at full (fixed 1000; SparkJS bounding boxes under-report extent so it's not measured).
- **splatMorph.ts** — `SplatMorphSystem`. Cross-dissolve between two worlds via one shared `phase` uniform, scrubbed by `bothProgress`. `setScenes()` to stage a pair, `restage()` to move to the next, `prewarm()` to compile a world's shader before it is needed, `release()` before either mesh is unloaded. Used by 四, 五 and 返.
- **splatFlow.ts** — `SplatFlowSystem`. Alternative transition for 返, ported from SparkJS's `splat-transitions/effects/flow.js`: every splat travels between its true position and a shared **gather point**, at a rate randomised per splat by `hash(index)`. That per-splat spread is the effect — a uniform contraction would read as a zoom, a scattered one reads as ink dispersing. Same interface as `SplatMorphSystem` (`restage`/`setPhase`/`isReady`/`release`/`prewarm`) so the two are interchangeable at the call site; `RETURN_USES_FLOW` in director.ts picks. Extras: `setGatherPoint()`, tuned by `RETURN_GATHER`. **Only 返** — there is one worldModifier slot per mesh and the two systems would fight over it, which is why `enterPhase` calls `morph.release()` before the flow attaches.
- **cosmos.ts** — `CosmosSystem`. The 六 sky: 17 planets on 7 orbits, turned by `setProgress()`. Nothing transitions in this phase — the gesture is answered, not spent.
- **audio.ts** — `Sound`. Scene music and the gesture SFX layer (one-hand / both-hands loops + a completion hit).
- **returnToDao.ts** — sketch of a particle-based 道; **wired to nothing.** 返 is currently a plain cross-dissolve into `dao.spz` instead.
- **gaussianSplatLoader.ts** — `GaussianSplatLoader` component + system. Loads splats, LoD (`lodSplatScale` = quality dial), `getSplat(entity)`, `unload()`. Camera-clone patch for SparkJS LoD.
- **gaussianSplatAnimator.ts** — fly-in/out via dyno **objectModifier** (why reveal/morph use worldModifier — to stack).
- **handFollowCube.ts** — `HandFollowCubeSystem`. Two yin-yang markers on reconfigurable rails, one per hand; see "the three gestures" and the three rules above, which is where all the real reasoning lives. Touching ENGAGES a marker, it does not place it — on contact the marker stays put and then follows the hand's velocity 1:1, so grabbing never jumps the gesture. Outputs: `railProgress` (三), `bothProgress` (四 onward), `leftProgress`, `heldCount`, `anyHeld`, `tooFast`, `rightHandSpeed`. `reset()`, `setVisible()`, `placeInFrontOf()`, `setFollowHead()`.
- **uiPanel.ts** — Sensai spatial UI panel (disabled).

## SparkJS dyno gotchas

- **worldModifier vs objectModifier**: objectModifier is taken by the fly-in animator; reveal/morph use worldModifier to stack. World at origin → `center.xz` == local.
- **updateGenerator()** recompiles the whole pipeline — call ONCE at attach. Per frame only set `uniform.value` + `mesh.updateVersion()`.
- Gsplat fields: `.center` (vec3), `.scales` (vec3), `.rgba` (vec4, `.a`), `.index` (int).
- `reset()` a system before its mesh is unloaded, or per-frame `updateVersion()` hits a disposed mesh.

## Splat asset format — IMPORTANT

Export from SuperSplat as **Compressed PLY** (`.compressed.ply`). SparkJS
explicitly supports it, it's compact, and it sidesteps SPZ versioning.
**Do NOT export SPZ from SuperSplat** — it writes SPZ 4, which this pinned
SparkJS preview cannot read (loader hangs on the loading screen forever).
Existing working splats are SPZ **v2** (gzip). URLs use per-segment encoding so
spaces + subfolders survive.

**`flipUp` goes with the format, not the capture.** Compressed `.ply` exports
Y-down and needs `flipUp: true`; `.spz` exports are already Y-up and need
`flipUp: false`. Getting this wrong hangs the world upside down — so when you
swap a splat, check whether the FORMAT changed, not just the file.

Worlds in use (all set in `director.ts`):

| Phase | Constant | File | `flipUp` |
|---|---|---|---|
| 三 | `REVEAL_SPLAT` | `Scene1/Enchanted Bamboo Forest Sanctuary_lowRes.spz` | false |
| 四 | `MORPH_SPLAT` | `Scene2/Mystical Mountain Temple Platform.compressed.ply` | true |
| 五 | `EXPAND_SPLAT` | `Scene3/Celestial Gateway to Floating Realms.spz` | — |
| 返 | `RETURN_SPLAT` | `dao.spz` (SPZ v3) | — |

`sensai.spz` is small and loads fast — useful for testing anything that is not
about a specific world. Unused leftovers: `newMoutains.spz`, `Scene1_Ancient
Chinese Bamboo Courtyard.spz`, `Scene2_Ruined Sanctuary Apocalyptic
Aftermath.spz`, the full-res Scene1/Scene3 captures.

To check an SPZ's version before wiring it up (v4 hangs the loader silently):

```bash
python -c "import gzip,struct,sys; d=gzip.open(sys.argv[1],'rb').read(16); \
print('ver',struct.unpack('<I',d[4:8])[0],'count',struct.unpack('<I',d[8:12])[0])" file.spz
```

## Status & next

The journey runs end to end: 一 → 二 → 三 → 四 → 五 → 六 → 返. Typechecks clean.

**Not yet Quest-tested (2026-07-30):** the whole ratchet/speed/hint system above,
and 返's flow transition. Every threshold in both is an estimate — they cannot be
judged on desktop, since they depend on real hand-tracking speed and on how the
actual captures look when gathered. Most likely to need moving, in order:
`DEACTIVATE_SPEED` (0.25 leaves under 1.7x margin over a relaxed sweep, so
ordinary movement tripping it means this went too far), `LOCKOUT_SECONDS` (1.2s
is long in VR), `FLOW_WAVES` / `WORLD_SCALE` in splatFlow.ts.

**Performance risk in splatFlow.ts:** its shader runs a hash, a `pow`, a `sin`
and a `length` per splat per frame across two ~450k-splat worlds. If 返 drops
frames, delete the `ripple` term first — it is purely decorative.

Next: record the two voice lines; wind in 二; decide whether `returnToDao.ts`'s
particle 道 replaces `dao.spz` entirely; close the remaining gesture divergence
in [STORY.md](STORY.md) (each transition still has its own rail layout, where
the story asks for one recurring loop).
