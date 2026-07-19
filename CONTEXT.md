# THE FLOW — project context

## Current experience

The release path is the Unity project in `WayfinderUnity/`, built with Unity `6000.3.17f1` for PICO 4 Ultra. It is one continuous, controller-free experience:

1. The player touches **OPEN THE GAME** with a tracked hand.
2. They place a 60 cm circle in a comfortable world position.
3. Three valid slow orbits raise exactly three increasingly large stones: **Listen**, **Patience**, and **Peace**.
4. The gate opens, becomes substantially brighter, and presents **PEACE STATE REACHED**.
5. The memory-world, reflection, reward, and local memory-save flow begins.

The physical PICO audit passed on 2026-07-18. The APK ran as the foreground activity without crashes or Unity exceptions, and the complete EditMode suite passed 79/79. PICO configuration is Hands Only with high-frequency hand tracking; controller profiles are disabled.

## Unity map

- Scene: `WayfinderUnity/Assets/Wayfinder/Scenes/WayfinderRiver.unity`
- First-time flow and story: `WayfinderVerticalSliceController.cs`
- Three-stone and gate progression: `WayfinderRiverController.cs`
- Circle placement/orbit recognition: `WayfinderCircularGesture.cs`
- Hand sampling and motion quality: `WayfinderXRHandsProvider.cs`, `WayfinderHandMotion.cs`
- Memory/reflection/reward state machine: `WayfinderMemoryKeeper.cs`
- Current world placeholder: `WayfinderWorldRevealSlot.cs`
- PICO build validation: `WayfinderUnity/Assets/Editor/WayfinderPicoBuilder.cs`

Do not add controller requirements, trigger/grip input, dwell to the opening button, or a second scene for the memory world. Preserve the placed circle between orbits and the one-orbit/one-stone contract.

## Splat integration seam

The next step is to replace the placeholder world behind `IWayfinderWorldSlot` / `WayfinderWorldRevealSlot` with a Unity-compatible Gaussian-splat renderer. Load or warm the splat before completion, keep it hidden during practice, then drive its reveal from `SetRevealProgress` and make it fully available from `OpenMemoryWorld`. The gate, peace-state card, reflection, and reward state machine should remain unchanged.

The repository's existing Vite/WebXR prototype in `src/` uses IWSDK, SparkJS, and a dyno world modifier. It is reference code and a visual test harness, not the PICO release runtime. Its working source asset is:

`public/splats/Scene1/Celestial Pathways Amidst Clouds.compressed.ply`

Use compressed PLY for new source splats. Do not assume the web shader graph can run directly in Unity; port the reveal behavior through the Unity renderer's material or compute interface.

## Commands

Build/install PICO:

```bash
./scripts/build_install_pico.sh
```

Run Unity EditMode tests:

```bash
/Applications/Unity/Hub/Editor/6000.3.17f1/Unity.app/Contents/MacOS/Unity \
  -batchmode -projectPath "$PWD/WayfinderUnity" \
  -runTests -testPlatform EditMode \
  -testResults /tmp/the-flow-editmode-results.xml \
  -logFile /tmp/the-flow-editmode.log
```

Run the web splat prototype:

```bash
npm install
npm run dev
npm run build
npx tsc --noEmit -p tsconfig.json
```

Optional network integrations in the Unity source default off and must never block the local PICO flow.
