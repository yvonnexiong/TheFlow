# THE FLOW

THE FLOW is a hands-only PICO 4 Ultra experience about patience. The player opens the experience with one hand touch, places a 60 cm practice circle, and completes three slow Tai Chi-inspired orbits. Each orbit raises one stone—Listen, Patience, then Peace. After the third stone, the gate opens into a memory world with reflection and reward choices.

The repository contains:

- `WayfinderUnity/` — the validated Unity 6 PICO application.
- `src/` and `public/splats/` — the existing WebXR/SparkJS Gaussian-splat prototype and source material for the next memory-world integration.
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

See `CONTEXT.md` for architecture, validated constraints, and the splat integration seam.
