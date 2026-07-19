# The Flow XR — Codex context

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

## The experience: ONE continuous session, phases not scenes

Never separate apps or hard scene-loads — the philosophy depends on unbroken
continuity. Sequenced by the Director:

- **Scene 0 / 一 Breath** — pure white void, silent but for a faint synthesised heartbeat (`heartbeat.ts`, 54 BPM). A grey ring pulses ON the heartbeat a step ahead; the player physically STEPS INTO it to begin. No ground, no sky, no UI — the loading gate is a bare white field (`SHOW_LOADING_INDICATOR` restores a dev spinner). Voice line "Before all things, there was only Dao." not yet recorded.
- **Scene 1 / 二 Disc** — a flat disc opens under the player's feet, 0 → `DISC_RADIUS` (2 m) over `DISC_GROW_SECONDS` (2.5 s), cubic ease-out. **No splat in this phase.** The disc is PLACEHOLDER geometry to be swapped for real mesh. Holds via `HOLD_AFTER_DISC`.
- **Scene 2 / 三 Reveal** — the splat world is revealed BY HAND: `railProgress` drives the reveal wavefront directly, so the world only exists as far as the player has swept. Scrub, not trigger.
- **万物 Resonance** — not built yet.

The two-splat cross-fade is **no longer part of the journey** — `splatMorph.ts` is unused by the Director.

## Source map (`src/`)

- **index.ts** — bootstrap. `SCENE_MODE` = `"flow"` (Director, the real thing) | `"reveal"` | `"morph"` (standalone harnesses). Contains the IWER emulator workaround + a DOM "enter" button (styled faint so Scene 0 stays UI-free).
- **director.ts** — `DirectorSystem`. Phases are `"breath" | "disc" | "reveal"`. Seed circle + step-in detection, white loading gate, phase-driven background (`VOID_COLOR` white → `WORLD_COLOR` once a world exists, so splat gaps don't glare through), preloads the splat, disc growth, cube visibility. Flags: `HOLD_AFTER_DISC`, `SHOW_LOADING_INDICATOR`, **`START_PHASE`** (dev: jump straight to a scene — note it CANNOT meaningfully test scene 1, whose content is a step-in-triggered animation). Tuning: `CIRCLE_RADIUS`, `CIRCLE_FRONT_Z`, `DISC_RADIUS`, `DISC_GROW_SECONDS`, `BEGIN_ARM_SECONDS`, `RAIL_FALLBACK`, `DWELL_SECONDS`.
- **heartbeat.ts** — synthesised WebAudio heartbeat, the only sound in Scene 0. No asset file. Dials: `HEARTBEAT_BPM`, `PEAK_GAIN` (deliberately tiny), `THUMP_HZ`. Needs a user gesture to start (the `enter` click).
- **splatReveal.ts** — `SplatRevealSystem`. Scene-1 radial spread reveal (dyno **worldModifier**). `grow(seconds)` auto-reveals then latches; `setProgress()` manual; `isReady`/`isRevealed`; `reset()` before unload. `MAX_RADIUS` = wavefront reach at full (fixed 1000; SparkJS bounding boxes under-report extent so it's not measured).
- **splatMorph.ts** — `SplatMorphSystem`. Cross-dissolve between two worlds via one shared `phase` uniform, scrubbed by `railProgress`.
- **gaussianSplatLoader.ts** — `GaussianSplatLoader` component + system. Loads splats, LoD (`lodSplatScale` = quality dial), `getSplat(entity)`, `unload()`. Camera-clone patch for SparkJS LoD.
- **gaussianSplatAnimator.ts** — fly-in/out via dyno **objectModifier** (why reveal/morph use worldModifier — to stack).
- **handFollowCube.ts** — `HandFollowCubeSystem`. Rail cube; `railProgress` (0..1) gesture driver; `reset()`, `setVisible()`.
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

`public/splats/`: `Scene1/Celestial Pathways Amidst Clouds.compressed.ply`
(current scene 1); `Scene1_Ancient Chinese Bamboo Courtyard.spz` + `Scene2_Ruined
Sanctuary Apocalyptic Aftermath.spz` (morph); `sensai.spz` (small, fast tests).

## Status & next

Working + typechecks clean: scene 1 (step-into-circle → world shows), the
loading gate, and the Director. Not yet Quest-tested for feel. Next: 三 Life and
万物 Resonance systems; re-enable `REVEAL_ANIMATION`; audio (heartbeat→wind→bells);
white void background; flip `HOLD_AFTER_REVEAL` to chain scenes.
