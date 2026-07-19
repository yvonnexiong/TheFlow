# THE FLOW — overnight finish plan

## Overnight result

- Integrated a 300,000-splat Enchanted Bamboo reward world natively in Unity; the 1.92-million source remains local and ignored by Git.
- Added the Unity-native spark veil and prewarmed renderer handoff after Stone III.
- Added the deterministic two-turn hand-touch reflection, skip/done paths, local reward selection, save, and quiet state.
- Passed all 80 EditMode tests.
- Built a 61 MB ARM64/Vulkan APK and installed/launched it on the connected PICO 4 Ultra.
- Confirmed THE FLOW as the foreground Unity activity with no crash or Unity exception in the clean launch logcat scan.
- Automated visual capture was blocked by the unworn PICO's **Environment Too Dark** tracking overlay. A human wearer still must verify the reward splat's stereo appearance, performance, interaction comfort, and trailer framing.

## Morning outcome

A trailer-ready, hands-only PICO 4 Ultra build that preserves the validated three-stone Tai Chi flow and adds one deterministic ending:

1. Stone III completes and the gate opens.
2. A warm spark veil hides the world handoff.
3. The prebuilt **Enchanted Bamboo Forest Sanctuary** reward world appears.
4. The world conducts a short, scripted two-turn reflection.
5. The user may respond, skip, or finish; a journal presents the reflection state.
6. The session ends in a quiet sitting state with a reward postcard/memory.

No world is generated at runtime. No browser or WebXR session is launched. The existing Unity APK remains the product.

## What the user must leave ready

- Connect the PICO by USB, wear/wake it, and accept the USB-debugging prompt.
- Leave the headset awake, charged, and in developer mode.
- Plug the Mac into power, keep networking on, and prevent system sleep.
- Close the interactive Unity Editor so batch tests/builds can open the project.
- Default asset choice is `splats/Enchanted Bamboo Forest Sanctuary.spz` unless the user explicitly selects another.
- Do not paste passwords, private keys, API keys, or service-account files into chat.

Firebase, Supabase, and World Labs credentials are **not required** for the overnight offline build. They are required only for a later authenticated web dashboard, cloud transcription, or generating additional worlds.

## Asset inventory

All four new SPZ files are valid gzip-compressed SPZ assets and are distinct:

| Asset | Compressed size | Expanded SPZ size | Overnight use |
| --- | ---: | ---: | --- |
| Enchanted Bamboo Forest Sanctuary | 28.2 MB | 36.5 MB | Default reward world |
| Celestial Pathways Amidst Clouds | 28.7 MB | 36.5 MB | Alternate |
| testofground | 28.7 MB | 36.5 MB | Diagnostic |
| Panoramic Mountain Sunset Vista | 28.7 MB | 36.5 MB | Alternate |

The inspected bamboo world contains 1.92 million splats. The PICO build must use a reduced/LOD representation or a renderer-side splat budget before shipping.

`splats/IMG_7190.JPG` contains phone EXIF metadata including GPS data. It must not be committed, uploaded, or used until metadata is stripped and the user confirms its intended purpose.

## Overnight scope

### P0 — required

- Preserve the existing opening, hand-only input, circle placement, three-orbit contract, story cards, gate, and peace-state presentation.
- Integrate one reward splat natively in Unity if the renderer passes the PICO spike.
- Preload/warm the reward before Stone III and keep it hidden.
- Add a Unity-native spark veil at the gate transition; do not embed SparkJS.
- Add a deterministic reflection sequence with no network dependency.
- Keep **Skip** and **I’m done** reachable by natural hand interaction.
- Save only bounded local session/reflection state; never save hand poses or trajectories.
- Run EditMode tests, build APK, install, launch, confirm foreground activity, and scan logcat.

### P1 — only if P0 is stable

- Reduced SPZ variants for more than one reward world.
- Journal text animation and reward postcard polish.
- Microphone capture behind explicit consent.
- A clean HTTPS interface for future transcription/session sync, defaulted off.

### Deferred — not an overnight acceptance requirement

- Live speech-to-text.
- LLM-generated conversation.
- Firebase/Supabase login and cloud history.
- Runtime World Labs generation.
- Personalized psychological or “patience” scoring.

## Scripted ending

The ending is intentionally brief and non-clinical:

1. **World:** “Before you rest here, what feels most present in your life today?”
2. The user may reflect, skip, or continue.
3. **World:** “Where did you notice patience in yourself today?”
4. The user may reflect or choose **I’m done**.
5. **World:** “Thank you for leaving that here. Stay as long as you need.”
6. Enter an untimed quiet-sitting state.

The world does not diagnose, advise, grade, or claim to measure the user’s personality. Existing motion values are described only as practice signals such as steadiness, pacing, completion, and time.

## Technical design

- `WayfinderWorldRevealSlot.OpenPlaceholder()` remains the completion seam.
- The reward renderer is initialized before completion and made visible only during reveal.
- The spark veil peaks around the renderer handoff so loading or shader pop-in is not visible.
- The reflection controller is a separate deterministic state machine: `Arrival -> PromptOne -> PromptTwo -> Closing -> Quiet`.
- The local flow remains successful when microphone permission, networking, transcription, or a future backend is unavailable.
- A web dashboard will later consume an aggregate session record through a narrow HTTPS API.

## Agent roles

No additional autonomous agents are required to make the overnight P0 build. The primary implementation agent owns the changes to avoid concurrent Unity scene/prefab edits.

If parallel review is explicitly requested later, use these bounded roles:

- **Splat/rendering reviewer:** SPZ reduction, Vulkan/XR compatibility, frame-time and memory checks.
- **Interaction/UX reviewer:** hands-only reachability, reading time, skip/done behavior, seated comfort.
- **Device QA reviewer:** EditMode suite, APK build/install/launch, foreground activity, logcat and screenshots.
- **Backend reviewer (deferred):** authentication, claim-code linking, transcript consent, retention/deletion, dashboard schema.

## Backend plan after the trailer

Use one backend, not both Firebase and Supabase. Recommended first implementation:

- Firebase Auth for the companion web app.
- A short claim code links an anonymous headset session to a signed-in web user; no VR password entry.
- Cloud Functions expose transcription/conversation/session endpoints.
- Firestore stores aggregate session records and approved journal text.
- Storage holds a sanitized reward postcard.
- Unity talks to a small HTTPS API; service secrets never ship in the APK.

Suggested record fields: session ID, timestamps, completion, orbit durations, aggregate steadiness/continuity/pacing, approved reflection text, reward-world ID, and postcard path. Do not upload raw audio, hand samples, poses, or trajectories by default.

## Validation gates

- Existing EditMode suite remains green.
- One APK installs and launches on the connected PICO.
- Hands Only and high-frequency tracking remain enabled; controller profiles remain zero.
- No crash, `AndroidRuntime` fatal, Unity exception, or shader failure appears in logcat.
- The third stone opens the gate exactly once.
- Spark veil appears without obscuring reflection controls permanently.
- Reward world is visible stereoscopically and maintains an acceptable device frame rate.
- Scripted reflection always reaches Quiet, including skip and offline paths.
- Human wearer must confirm comfort, legibility, emotional tone, and trailer framing.

## Fallback ladder

1. Native reduced splat on PICO.
2. Lower splat budget/quality with the same world.
3. Pre-rendered panoramic reward world behind the gate.
4. Existing geometric memory-world placeholder with spark transition.

The build must remain complete and filmable even if the experimental native splat renderer fails.
