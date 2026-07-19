import { createSystem, Entity } from "@iwsdk/core";
import { SplatMesh, dyno } from "@sparkjsdev/spark";
import { GaussianSplatLoaderSystem } from "./gaussianSplatLoader.js";

// ------------------------------------------------------------
// Radial "spread" reveal of a single Gaussian Splat world
// ------------------------------------------------------------
// Scene 1 (一 → 二): "The player steps into the circle. Ground grows outward
// infinitely." One world starts fully dissolved. When grow() is called a
// circular wavefront expands outward from the world origin over a few seconds,
// on its own, and each splat materialises as the front passes it — ink across
// rice paper, the earth awakening around where the player stood.
//
// Unlike the morph, this reveal is NOT scrubbed by the hand. The Director
// triggers grow() the moment the player's body enters the seed circle, and the
// growth plays out cinematically, then LATCHES (the world stays revealed).
//
// Spark specifics (same gotchas documented in splatMorph.ts):
//   * worldModifier, not objectModifier. objectModifier is already taken by
//     GaussianSplatAnimator's fly-in; worldModifier stacks independently. For a
//     world at the origin, world-space center.xz == local, so the radial
//     distance is unchanged.
//   * updateGenerator() recompiles the whole pipeline — call it ONCE at attach.
//     Per frame we only assign progress.value and call updateVersion().

/** Radius (metres) the wavefront reaches at progress = 1 — the whole world must
 *  fit inside this or its far edges never reveal. Fixed rather than measured,
 *  because SparkJS bounding boxes under-report the true splat extent. Raise it
 *  if any of the world still stays hidden at full bloom. */
// Must clear the world's extent PLUS two edge-widths (see setWavefront), so
// the outermost content lands inside the fully-opaque boundary rather than in
// the fade band. World reaches 85m, edge is ~7m there → 99m minimum.
const MAX_RADIUS = 150.0;

/** DIAGNOSTIC (2026-07-18): skip the reveal modifier entirely so the splat
 *  renders raw. Isolates "is the shader eating splats?" from "is the data or
 *  the loader at fault?". The world will simply be fully visible — the hand
 *  gesture does nothing while this is true. Set false to restore. */
const BYPASS_MODIFIER = false;

/**
 * Radial content distribution of the scene-1 world, measured from the
 * compressed .ply's own per-chunk bounds.
 *
 * Entry i is the wavefront radius (metres) that reveals i/(N-1) of the splats.
 * The world is non-uniform — half its content sits within 11 m and 95% within
 * 26 m, but a far corner reaches 85 m — so a wavefront whose RADIUS moves
 * linearly would dump most of the world in the first third of the lift.
 *
 * Interpolating this table instead maps the gesture to CONTENT rather than
 * distance: equal hand movement reveals an equal amount of world, which is
 * what "linear" actually feels like.
 *
 * Regenerate if the scene changes — it is specific to this splat.
 */
const CONTENT_RADIUS_LUT = [
  0, 2, 4, 5, 6, 7, 8, 9, 10, 11, 11, 12, 13, 14, 15, 15, 17, 19, 21, 26, 85,
];

/** Map 0..1 through the table above, linearly interpolating between entries. */
function contentRadius(t: number): number {
  const clamped = Math.min(1, Math.max(0, t));
  const scaled = clamped * (CONTENT_RADIUS_LUT.length - 1);
  const i = Math.min(Math.floor(scaled), CONTENT_RADIUS_LUT.length - 2);
  const f = scaled - i;
  return (
    CONTENT_RADIUS_LUT[i] * (1 - f) + CONTENT_RADIUS_LUT[i + 1] * f
  );
}

/** Softness (metres) of the wavefront edge — larger = a wider, mistier band. */
const EDGE_SOFTNESS = 3.0;

/** Edge band as a fraction of the current radius, so the front stays
 *  proportionally misty as the wavefront grows. */
const EDGE_RATIO = 0.08;

/** Per-splat radial jitter (metres) so the front is an irregular ink edge. */
const EDGE_JITTER = 1.2;

/** Colour splats emerge FROM (1.0 = white mist); real colour fades up over the
 *  edge band. */
const EMERGE_FROM = 1.0;

/** Default seconds for a full grow if grow() is called without an argument. */
const DEFAULT_GROW_SECONDS = 5;

export class SplatRevealSystem extends createSystem({}) {
  private sceneEntity: Entity | null = null;
  private mesh: SplatMesh | null = null;
  private progress = dyno.dynoFloat(0);
  // Wavefront radius at progress = 1. Sized to the loaded world's extent in
  // applyMeasuredRadius() so the whole world reveals; MAX_RADIUS is the fallback.
  private maxRadiusUniform = dyno.dynoFloat(MAX_RADIUS);
  // Edge width is driven per-frame by setWavefront, so it must be the SAME
  // dyno object the shader samples — not a fresh one built at attach time.
  private edgeUniform = dyno.dynoFloat(EDGE_SOFTNESS);
  /** Gesture value 0..1 — content revealed. Distinct from progress.value,
   *  which is the remapped radius fraction the shader consumes. */
  private revealT = 0;
  private attached = false;
  private waitLogged = false;

  // Optional manual override (0..1). When set, and not growing, it drives the
  // reveal directly. Left null in normal (grow-driven) use.
  private externalProgress: number | null = null;

  // Auto-grow state. grow() advances progress 0→1 over growDuration seconds,
  // eased, then latches. pendingGrow defers the start until the splat has
  // actually finished loading, so growth never plays out during the load.
  private pendingGrow = false;
  private growing = false;
  private growElapsed = 0;
  private growDuration = DEFAULT_GROW_SECONDS;

  /**
   * Drive the wavefront from a 0..1 gesture value.
   *
   * `t` is CONTENT revealed, not distance travelled. It goes through
   * contentRadius() to get the metre radius that exposes that fraction of the
   * world, then back into the shader's progress uniform (which the GLSL
   * multiplies by MAX_RADIUS). The edge band widens with the radius so the
   * front stays proportionally soft instead of turning into a hard line once
   * the wavefront is hundreds of metres out.
   */
  private setWavefront(t: number): void {
    const clamped = Math.min(1, Math.max(0, t));
    this.revealT = clamped;

    // contentRadius(t) is the radius that should be FULLY revealed. The shader
    // only reaches full opacity inside (R - edge), and fades out to nothing by
    // (R + edge) — so R has to sit a whole edge-width beyond the target, and
    // the progress that produces it another edge-width beyond that:
    //
    //     R = progress * MAX_RADIUS - edge   (shader)
    //     R - edge = radius                  (what we want fully revealed)
    //  => progress = (radius + 2 * edge) / MAX_RADIUS
    //
    // Setting progress = radius / MAX_RADIUS (as this did) left the outermost
    // content sitting inside the fade band, so the world never fully arrived
    // at the top of the rail. With the edge scaling as 8% of radius, that band
    // is ~85m wide out at the rim — very visible.
    const radius = contentRadius(clamped);
    const edge = Math.max(EDGE_SOFTNESS, radius * EDGE_RATIO);
    this.edgeUniform.value = edge;
    this.progress.value = (radius + 2 * edge) / MAX_RADIUS;
  }

  /** Register the splat world to reveal. Call once, right after creating it. */
  setScene(scene: Entity): void {
    this.sceneEntity = scene;
  }

  /** Manually drive the reveal (0..1). Pass null to release manual control. */
  setProgress(value: number | null): void {
    this.externalProgress = value;
  }

  /** Auto-grow the reveal to fully revealed over `seconds`, then latch. Safe to
   *  call before the splat has loaded — it begins once attached. This is the
   *  scene-1 "ground grows outward" behaviour. */
  grow(seconds = DEFAULT_GROW_SECONDS): void {
    this.growDuration = Math.max(0.001, seconds);
    this.externalProgress = null;
    this.pendingGrow = true;
    this.growing = false;
    this.growElapsed = 0;
  }

  /** Radius (m) currently revealed at full opacity — what the shader's
   *  R - edge boundary actually works out to. Diagnostic. */
  get fullyRevealedRadius(): number {
    return this.progress.value * MAX_RADIUS - 2 * this.edgeUniform.value;
  }

  /** True once the reveal has fully grown. */
  get isRevealed(): boolean {
    return this.revealT >= 0.999;
  }

  /** True once the splat has loaded and the reveal modifier is attached — i.e.
   *  the world is ready to be revealed. Used to gate the loading screen. */
  get isReady(): boolean {
    return this.attached;
  }

  /** Detach and forget the current world. Call before its splat is unloaded so
   *  update() never calls updateVersion() on a disposed mesh. */
  reset(): void {
    this.attached = false;
    this.mesh = null;
    this.sceneEntity = null;
    this.externalProgress = null;
    this.waitLogged = false;
    this.pendingGrow = false;
    this.growing = false;
    this.growElapsed = 0;
    this.progress.value = 0;
    this.revealT = 0;
    this.edgeUniform.value = EDGE_SOFTNESS;
  }

  update(delta: number) {
    if (!this.attached) {
      this.tryAttach();
      return;
    }

    // Begin a deferred grow now that the splat is loaded.
    if (this.pendingGrow) {
      this.pendingGrow = false;
      this.growing = true;
      this.growElapsed = 0;
    }

    if (this.growing) {
      // delta is assumed to be in seconds (three/IWSDK convention).
      this.growElapsed += delta;
      const t = Math.min(1, this.growElapsed / this.growDuration);
      this.setWavefront(t * t * (3 - 2 * t)); // smoothstep ease
      if (t >= 1) this.growing = false;
    } else if (this.externalProgress !== null) {
      this.setWavefront(this.externalProgress);
    }
    // else: latched — progress holds its last value.

    this.mesh!.updateVersion();
  }

  /**
   * Attach the reveal modifier once the splat has loaded. Polled from update()
   * because the load is async and the splat is tens of MB.
   */
  private tryAttach(): void {
    if (!this.sceneEntity) return;

    const loader = this.world.getSystem(GaussianSplatLoaderSystem);
    if (!loader) return;

    const mesh = loader.getSplat(this.sceneEntity);
    if (!mesh) {
      if (!this.waitLogged) {
        this.waitLogged = true;
        console.log("[SplatReveal] Waiting for the splat world to load…");
      }
      return;
    }

    this.mesh = mesh;
    this.maxRadiusUniform.value = MAX_RADIUS; // fixed reveal radius

    if (BYPASS_MODIFIER) {
      // DIAGNOSTIC: attach nothing at all. The splat renders raw, exactly as
      // Spark loaded it. Previous attempts only widened MAX_RADIUS, which left
      // the shader in the pipeline still scaling every splat by `reveal`.
      this.attached = true;
      console.warn("[SplatReveal] BYPASS_MODIFIER — raw splat, no reveal.");
      return;
    }

    mesh.worldModifier = this.createRevealModifier();
    mesh.updateGenerator(); // once — full pipeline recompile
    this.attached = true;
    console.log(`[SplatReveal] Attached; reveal radius = ${MAX_RADIUS}m.`);
  }

  /**
   * Per-splat spread reveal, in GLSL.
   *
   * A wavefront of radius R = progress * MAX_RADIUS expands from the world
   * origin. `reveal` is 1 inside the front, 0 outside, soft over EDGE_SOFTNESS.
   * Unrevealed splats are shrunk to zero size (so nothing pops as stray dots)
   * and their colour lifts out of the emerge colour as they appear.
   */
  private createRevealModifier() {
    const progress = this.progress;
    const maxRadius = this.maxRadiusUniform;
    const edge = this.edgeUniform;
    const jitter = dyno.dynoFloat(EDGE_JITTER);
    const emergeFrom = dyno.dynoFloat(EMERGE_FROM);

    return dyno.dynoBlock(
      { gsplat: dyno.Gsplat },
      { gsplat: dyno.Gsplat },
      ({ gsplat }) => {
        const revealDyno = new dyno.Dyno({
          inTypes: {
            gsplat: dyno.Gsplat,
            progress: "float",
            maxRadius: "float",
            edge: "float",
            jitter: "float",
            emergeFrom: "float",
          },
          outTypes: { gsplat: dyno.Gsplat },
          globals: () => [
            dyno.unindent(`
              // Stable per-splat pseudo-random in [0,1), keyed on splat index.
              float revealHash(int n) {
                return fract(sin(float(n) * 12.9898) * 43758.5453123);
              }
            `),
          ],
          statements: ({ inputs, outputs }) =>
            dyno.unindentLines(`
              ${outputs.gsplat} = ${inputs.gsplat};

              // Horizontal distance from the world origin (the wavefront centre,
              // under where the player stepped in). y is ignored so the reveal
              // sweeps across the ground plane rather than climbing upward.
              float l = length(${inputs.gsplat}.center.xz);

              // Irregular ink edge: nudge each splat's effective distance.
              l += (revealHash(${inputs.gsplat}.index) - 0.5) * ${inputs.jitter};

              // Expanding wavefront radius. The -edge bias guarantees a fully
              // hidden world at progress 0.
              float R = ${inputs.progress} * ${inputs.maxRadius} - ${inputs.edge};

              // 1 inside the front, 0 outside, soft across the edge band.
              float reveal = 1.0 - smoothstep(R - ${inputs.edge}, R + ${inputs.edge}, l);

              // Shrink unrevealed splats to nothing so none pop as stray dots.
              ${outputs.gsplat}.scales = ${inputs.gsplat}.scales * reveal;

              // Fade opacity in, and bleed colour up from the emerge colour so
              // the world resolves out of mist rather than snapping to full hue.
              ${outputs.gsplat}.rgba.a *= reveal;
              ${outputs.gsplat}.rgba.rgb = mix(
                vec3(${inputs.emergeFrom}),
                ${inputs.gsplat}.rgba.rgb,
                reveal
              );
            `),
        });

        return {
          gsplat: revealDyno.apply({
            gsplat,
            progress,
            maxRadius,
            edge,
            jitter,
            emergeFrom,
          }).gsplat,
        };
      },
    );
  }
}
