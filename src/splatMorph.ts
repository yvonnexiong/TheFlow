import { createSystem, Entity } from "@iwsdk/core";
import { SplatMesh, dyno } from "@sparkjsdev/spark";
import { GaussianSplatLoaderSystem } from "./gaussianSplatLoader.js";
import { HandFollowCubeSystem } from "./handFollowCube.js";

// ------------------------------------------------------------
// Hand-driven morph between two Gaussian Splat worlds
// ------------------------------------------------------------
// Sliding the cube along its rail (see handFollowCube.ts) scrubs a single
// SEQUENTIAL, not a cross-dissolve. Phase 0 to 0.5 winds scene A up into
// nothing; 0.5 to 1 unrolls scene B out of it. Only ONE world is on screen at
// any moment, and the dormant one is hidden outright rather than drawn at zero
// alpha — a transparent splat is still sorted and rasterised, an invisible mesh
// is not submitted at all. That halving is the point: the shader maths was
// never the cost, two worlds' worth of sorting and overdraw was.
//
// (Historical note: this used to cross-dissolve, where at phase 0 scene A is
// fully present and scene B is fully dissolved; in between, both scenes'
// splats scatter outward, shrink, and fade — so the transition reads as one
// world dispersing into particles while the other reconverges out of them.
//
// This mirrors the approach in SparkJS's own splat-transitions/morph example:
// the two scenes are NEVER blended in a single shader. Each SplatMesh gets its
// own worldModifier carrying a baked scene index, and each independently
// decides from the shared phase whether it is arriving or leaving. The
// cross-fade is an emergent effect of both shaders agreeing on one number.
//
// Two Spark specifics that are easy to get wrong:
//   * worldModifier, not objectModifier. Scatter offsets must be in world
//     space so both scenes disperse into a shared region — under
//     objectModifier the scatter is per-mesh-local and the handoff doesn't
//     line up. (objectModifier is also already taken by GaussianSplatAnimator's
//     fly-in; the two arrays stack independently.)
//   * updateGenerator() recompiles the whole pipeline — it is called ONCE at
//     attach time. Per frame we only assign `phase.value` and call
//     updateVersion(). Calling updateGenerator() every frame would recompile
//     shaders continuously.

/** Peak outward scatter distance, in metres, at mid-transition. */
/** How far the unroll clock runs. At t=0 a world is fully wound up and gone;
 *  by this value exp(-t) is ~0.007, so it is fully formed. */
const UNROLL_T_MAX = 5.0;

/**
 * Shapes how the unroll clock maps onto the gesture, as a power curve.
 *
 * 1.0 is linear, and linear looked wrong: Unroll's own thresholds make a world
 * collapse quickly once t starts falling, so an evenly-descending t spent most
 * of the middle of the gesture showing an empty void — 80% of the world gone by
 * a third of the way across.
 *
 * Below 1 the curve is concave: t stays high through most of the travel and
 * only falls away near the end, so each world holds its substance far longer
 * and the transition happens in a shorter, more decisive window.
 *
 * Lower = more world visible through the middle. 0.35 keeps roughly 70% of the
 * clock at the point the linear version had already reached 40%.
 */
const UNROLL_CURVE = 0.35;

/**
 * How much the two halves overlap, in gesture units.
 *
 * Strictly sequential means there is one instant, exactly at 0.5, where the
 * outgoing world has fully gone and the incoming one has not started — a frame
 * of pure nothing. A small overlap lets the next world begin unrolling before
 * the last has finished leaving, which reads as one becoming the other rather
 * than as a gap.
 *
 * This is the ONLY window where both worlds render, so it is deliberately
 * narrow — widening it trades the performance the sequential design bought.
 */
const UNROLL_OVERLAP = 0.1;

/** Characteristic scene half-height in metres, used to normalise the effect
 *  into the unit-ish range Spark's example assumes. Roughly half the world's
 *  vertical span — these scenes run 50-115m, so ~25 sits about right. Larger
 *  values make the twist gentler and the reveal sweep slower up the scene. */
const UNROLL_HEIGHT = 25.0;

const clamp01 = (v: number) => Math.min(1, Math.max(0, v));

export class SplatMorphSystem extends createSystem({}) {
  private sceneEntities: [Entity, Entity] | null = null;
  private meshes: SplatMesh[] = [];
  private phase = dyno.dynoFloat(0);
  /** Unroll time per scene. Each world runs its OWN clock: A winds back to
   *  nothing over the first half of the gesture, B unrolls over the second, so
   *  the two never overlap and only one is ever on screen. */
  private readonly unrollT = [dyno.dynoFloat(UNROLL_T_MAX), dyno.dynoFloat(0)];
  /** Entities whose scene-B shader should be compiled ahead of time. */
  private prewarmQueue: Entity[] = [];
  /** Which modifier index each mesh has already been compiled for. A mesh
   *  reused across transitions changes role — the world you morphed INTO
   *  becomes the one you morph OUT OF — and the two directions are different
   *  shaders, so the index has to match or it must recompile. */
  private readonly compiledFor = new Map<SplatMesh, number>();
  private attached = false;
  private waitLogged = false;

  /**
   * Register the two splat entities to morph between. Call once, right after
   * creating them; the modifiers attach later, on the first frame where both
   * meshes have finished loading.
   */
  /**
   * Compile scene B's morph shader EARLY, during the loading gate.
   *
   * The stall at the transition was never the splat loading — it is
   * updateGenerator(), which recompiles a mesh's entire shader pipeline, and
   * setScenes() fires it for BOTH meshes at the instant the phase changes.
   *
   * Scene B's worldModifier is free the whole time it sits hidden, so its
   * compile can happen up front. Scene A's cannot: SplatRevealSystem owns that
   * slot until 三 ends. So this halves the hitch rather than removing it.
   */
  prewarm(sceneB: Entity): void {
    this.prewarmQueue.push(sceneB);
  }

  /**
   * Point the morph at a NEW pair of worlds and attach again.
   *
   * The system was built to run once. Chaining transitions means restaging it:
   * the world just morphed into becomes the one being morphed out of, which
   * flips its modifier index and so needs a recompile, while the incoming world
   * will already have been prewarmed in the right direction.
   */
  restage(sceneA: Entity, sceneB: Entity): void {
    this.sceneEntities = [sceneA, sceneB];
    this.meshes = [];
    this.attached = false;
    this.waitLogged = false;
    this.setPhase(0);
  }

  /** Drive the cross-dissolve, 0 (scene A) to 1 (scene B). */
  setPhase(value: number): void {
    const p = Math.min(1, Math.max(0, value));
    this.phase.value = p;

    // A leaves across [0, aEnd]; B arrives across [bStart, 1]. They overlap by
    // UNROLL_OVERLAP around the midpoint so there is never a frame of nothing.
    const half = 0.5 + UNROLL_OVERLAP / 2;
    const aEnd = half;
    const bStart = 0.5 - UNROLL_OVERLAP / 2;

    // 1 = fully present, 0 = fully wound away, before shaping.
    const aRaw = clamp01((aEnd - p) / aEnd);
    const bRaw = clamp01((p - bStart) / (1 - bStart));

    // The power curve is what puts world back into the middle of the gesture.
    this.unrollT[0].value = UNROLL_T_MAX * Math.pow(aRaw, UNROLL_CURVE);
    this.unrollT[1].value = UNROLL_T_MAX * Math.pow(bRaw, UNROLL_CURVE);

    // The real saving: skip a world entirely once it has nothing to show. A
    // zero-alpha splat is still sorted and rasterised — an invisible mesh is
    // not submitted at all. Both are only ever on screen inside the overlap.
    if (this.meshes.length === 2) {
      this.meshes[0].visible = aRaw > 0;
      this.meshes[1].visible = bRaw > 0;
    }
  }

  /** True once both splats have loaded and the modifier is attached. */
  get isReady(): boolean {
    return this.attached;
  }

  setScenes(sceneA: Entity, sceneB: Entity): void {
    this.sceneEntities = [sceneA, sceneB];
  }

  update() {
    if (!this.attached) {
      this.tryPrewarm();
      this.tryAttach();
      return;
    }

    // Phase is pushed in by the Director via setPhase(), not pulled from a rail
    // here: the 四 morph is driven by BOTH hands averaged, and which rails mean
    // what is the Director's business, not this system's.

    // Push the new uniform value into the compiled program. updateVersion() is
    // the cheap per-frame call; the uniform updater registered at compile time
    // copies phase.value into the GPU uniform.
    for (const mesh of this.meshes) mesh.updateVersion();
  }

  /**
   * Attach the morph modifier once both splats have loaded. Polled from
   * update() because loading is async and the 65MB/29MB scenes take a while.
   */
  /** Attach + compile scene B's modifier as soon as its mesh exists. */
  private tryPrewarm(): void {
    if (!this.prewarmQueue.length) return;
    const loader = this.world.getSystem(GaussianSplatLoaderSystem);
    if (!loader) return;

    this.prewarmQueue = this.prewarmQueue.filter((entity) => {
      const mesh = loader.getSplat(entity);
      if (!mesh) return true; // not loaded yet — try again next frame

      // Index 1: every prewarmed world arrives as the incoming one.
      mesh.worldModifier = this.createMorphModifier(1);
      mesh.updateGenerator(); // the expensive recompile, paid during the gate
      this.compiledFor.set(mesh, 1);
      console.log("[SplatMorph] incoming world prewarmed");
      return false;
    });
  }

  private tryAttach(): void {
    if (!this.sceneEntities) return;

    const loader = this.world.getSystem(GaussianSplatLoaderSystem);
    if (!loader) return;

    const meshes = this.sceneEntities.map((e) => loader.getSplat(e));
    if (meshes.some((m) => m === null)) {
      // Log once, so "no splats" can be told apart from "splats loaded but the
      // morph shader hid them" without needing a console attached.
      if (!this.waitLogged) {
        this.waitLogged = true;
        console.log(
          "[SplatMorph] Waiting for splats to load — " +
            `A:${meshes[0] ? "ready" : "loading"} ` +
            `B:${meshes[1] ? "ready" : "loading"}`,
        );
      }
      return;
    }

    this.meshes = meshes as SplatMesh[];
    this.meshes.forEach((mesh, index) => {
      // Already compiled for THIS role — recompiling would pay the cost the
      // prewarm exists to avoid. A mesh compiled for the other index still has
      // to be rebuilt: the two directions are genuinely different shaders.
      if (this.compiledFor.get(mesh) === index) return;
      mesh.worldModifier = this.createMorphModifier(index);
      mesh.updateGenerator(); // full pipeline recompile
      this.compiledFor.set(mesh, index);
    });

    this.attached = true;
    console.log(
      "[SplatMorph] Attached morph modifiers to both splat scenes; " +
        "slide the cube along the rail to scrub between them.",
    );
  }

  /**
   * Per-splat morph, in GLSL.
   *
   * `s` is this scene's dissolve amount: 0 = fully present, 1 = fully gone.
   * Scene 0 dissolves as phase rises, scene 1 dissolves as it falls, so the two
   * are exact mirrors and always sum to one visible world.
   */
  private createMorphModifier(sceneIndex: number) {
    const t = this.unrollT[sceneIndex];
    const height = dyno.dynoFloat(UNROLL_HEIGHT);

    return dyno.dynoBlock(
      { gsplat: dyno.Gsplat },
      { gsplat: dyno.Gsplat },
      ({ gsplat }) => {
        const unrollDyno = new dyno.Dyno({
          inTypes: { gsplat: dyno.Gsplat, t: "float", height: "float" },
          outTypes: { gsplat: dyno.Gsplat },
          globals: () => [
            dyno.unindent(`
              mat2 unrollRot(float a) {
                float c = cos(a), s = sin(a);
                return mat2(c, -s, s, c);
              }
            `),
          ],
          statements: ({ inputs, outputs }) =>
            dyno.unindentLines(`
              ${outputs.gsplat} = ${inputs.gsplat};

              vec3 localPos = ${inputs.gsplat}.center;
              vec3 scales = ${inputs.gsplat}.scales;
              float t = ${inputs.t};

              // Spark's unroll example is authored for a roughly unit-sized
              // scene: it twists by localPos.y * 50 and thresholds against
              // literals like -0.5 and -2.0. These worlds are 80-115m across,
              // where that would be a ~2500 radian twist and every threshold
              // would be crossed at once. Normalising height puts the effect
              // back in the range it was designed for.
              float ny = localPos.y / ${inputs.height};

              float decay = exp(-t);

              // Rotating helix: the twist unwinds as t rises, and each height
              // unwinds at a different rate, which is what makes it a scroll
              // rather than a spin.
              localPos.xz = unrollRot((ny * 50.0 - 20.0) * decay) * localPos.xz;

              // Draw in toward the axis while rolled, out to true position as
              // it opens.
              ${outputs.gsplat}.center = localPos * (1.0 - decay * 2.0);

              // Splats stay near-invisibly small until the wavefront reaches
              // their height, then grow to full size.
              ${outputs.gsplat}.scales =
                mix(vec3(0.002) * ${inputs.height}, scales,
                    smoothstep(0.3, 0.7, t + ny - 2.0));

              // Hard cut-in from the bottom up, so the scroll has an edge.
              ${outputs.gsplat}.rgba =
                ${inputs.gsplat}.rgba * step(0.0, t * 0.5 + ny - 0.5);
            `),
        });

        return {
          gsplat: unrollDyno.apply({ gsplat, t, height }).gsplat,
        };
      },
    );
  }

}
