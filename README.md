# THE FLOW

THE FLOW is a hands-only PICO 4 Ultra experience about patience. One hand touch begins a three-orbit Tai Chi practice: Listen, Patience, then Peace. Stone III opens the gate through a warm spark veil into an optimized Enchanted Bamboo Gaussian-splat world, followed by a short offline reflection journal and an untimed quiet state.

The repository contains:

- `WayfinderUnity/` — the validated Unity 6 PICO application.
- `src/` and `public/splats/` — the separate WebXR/SparkJS prototype.
- `scripts/build_install_pico.sh` — builds, installs, and launches the APK on one connected PICO.

## Run on PICO

Use Unity `6000.3.17f1`, or connect one authorized headset and run:

```bash
./scripts/build_install_pico.sh
```

## Web splat prototype

Requires Node.js 20.19 or newer:

```bash
npm install
npm run dev
```

See `CONTEXT.md` for architecture and validated constraints, and `OVERNIGHT_BUILD_PLAN.md` for the reward-world plan and QA handoff.
