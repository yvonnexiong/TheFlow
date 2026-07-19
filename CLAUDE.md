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

## The experience: ONE continuous session, phases not scenes

Never separate apps or hard scene-loads — the philosophy depends on unbroken
continuity. Sequenced by the Director:

- **一 Breath** — loading screen holds until the world is ready; then an empty void with a pulsing ring a step in front. The player physically STEPS INTO the ring to begin.
- **二 Reveal** — the world appears (currently shown instantly; a spread-reveal animation exists behind `REVEAL_ANIMATION`).
- **三 Morph** — one world disperses as another reconverges, hand-scrubbed. Gated off behind `HOLD_AFTER_REVEAL` while scene 1 is built.
- **万物 Resonance** — not built yet.

## Source map (`src/`)

- **index.ts** — bootstrap. `SCENE_MODE` = `"flow"` (Director, the real thing) | `"reveal"` (scene 1 alone) | `"morph"` (scene 2 alone). Contains the IWER emulator workaround + a DOM "Enter XR" button.
- **director.ts** — `DirectorSystem`. Phase state machine, the seed circle + step-in detection, loading overlay, preloads/unloads scene splats, cube visibility per phase. Key flags: `HOLD_AFTER_REVEAL`, `REVEAL_ANIMATION`. Tuning: `CIRCLE_RADIUS`, `CIRCLE_FRONT_Z`, `GROW_SECONDS`, `BEGIN_ARM_SECONDS`, `RAIL_FALLBACK`, `DWELL_SECONDS`. `REVEAL_SPLAT`/`MORPH_A_SPLAT`/`MORPH_B_SPLAT` name the assets.
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
