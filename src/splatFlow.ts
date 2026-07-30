import * as THREE from "three";
import { createSystem, Entity } from "@iwsdk/core";
import { SplatMesh, dyno } from "@sparkjsdev/spark";
import { GaussianSplatLoaderSystem } from "./gaussianSplatLoader.js";

// ------------------------------------------------------------
// 返 — one world gathers into a point, and the next opens out of it
// ------------------------------------------------------------
// Ported from SparkJS's own splat-transitions "flow" example, restaged for a
// gesture instead of a clock.
//
// The example runs a three-object carousel off an auto-advancing `time`
// uniform, cross-fading whichever pair the cycle has arrived at. None of that
// survives here: we have exactly two worlds, one transition, and it is scrubbed
// by the player's hands. What was kept is the part that matters — every splat
// travels between its true position and a shared GATHER POINT, and the rate it
// travels at is randomised per splat by a hash of its index. That per-splat
// spread is the whole effect. A uniform contraction reads as a scale-down; a
// scattered one reads as ink dispersing in water.
//
// This is also the transition STORY.md actually asks for in scene 5 — "worlds
// dissolve into ink, gather, and settle into 道" — which the plain cross-
// dissolve in SplatMorphSystem never attempted.
//
// Shares SplatMorphSystem's structure deliberately: the two scenes are never
// blended in one shader. Each mesh carries its own modifier and its own
// `present` uniform, and the handover is an emergent effect of both agreeing.
// Only ONE of them is ever driven past zero at a time, apart from a narrow
// overlap, so the cost stays at roughly one world.
//
// Spark specifics, same as the morph:
//   * worldModifier, not objectModifier — objectModifier is taken by the fly-in
//     animator, and there is exactly one worldModifier slot, so this REPLACES
//     the cross-dissolve for its phase rather than stacking with it.
//   * updateGenerator() recompiles the entire pipeline. Called ONCE at attach.
//     Per frame only `present.value` is assigned and updateVersion() called.

/**
 * How much the two halves overlap, in gesture units.
 *
 * Without it there is one instant where the leaving world has fully gathered
 * and the arriving one has not begun — a frame of nothing. The overlap lets the
 * next world start opening while the last is still condensing, so the point
 * they share reads as one thing passing through another.
 */
const FLOW_OVERLAP = 0.14;

/**
 * Characteristic world size in metres, used to put the example's unit-scale
 * maths back in range.
 *
 * The Spark example is authored against roughly unit-sized objects and writes
 * literals like `sin(p * 2.5)`. These captures run 50–115m across, where that
 * term oscillates many times per metre and the ripple turns into noise. Same
 * correction SplatMorphSystem makes with UNROLL_HEIGHT, and for the same
 * reason.
 */
const WORLD_SCALE = 25.0;

/**
 * Strength of the ripple that runs through a world as it gathers.
 *
 * The example exposes this as a 0..1 GUI slider ("waves"). At 0 the gather is a
 * clean implosion; higher values push splats along a sine of their own position
 * on the way in, which is what gives it the drifting, liquid quality. Scaled by
 * (1 - present), so a fully formed world is never displaced.
 */
const FLOW_WAVES = 0.5;

/**
 * Spread of the per-splat rate randomisation, as an exponent range.
 *
 * Each splat's travel is `pow(present, v)` with v = MIN + hash(index) * RANGE.
 * v = 1 would be linear; larger values hold a splat out near its true position
 * and then snap it in late. The spread between the fastest and slowest splat is
 * the effect — narrow it and the world contracts as a rigid body.
 */
const RATE_MIN = 0.5;
const RATE_RANGE = 2.0;

const clamp01 = (v: number) => Math.min(1, Math.max(0, v));

export class SplatFlowSystem extends createSystem({}) {
  private sceneEntities: [Entity, Entity] | null = null;
  private meshes: SplatMesh[] = [];
  /** Per-scene formedness: 1 = fully itself, 0 = gathered into the point.
   *  Scene A runs 1→0 over the first half, scene B runs 0→1 over the second. */
  private readonly present = [dyno.dynoFloat(1), dyno.dynoFloat(0)];
  /** Where both worlds gather. Origin by default — the same space
   *  SplatMorphSystem contracts toward — but settable, because for 返 the
   *  meaningful point is wherever 道 is about to stand. */
  private readonly gather = dyno.dynoVec3(new THREE.Vector3(0, 0, 0));
  private prewarmQueue: Entity[] = [];
  private readonly compiledFor = new Map<SplatMesh, number>();
  private attached = false;
  private waitLogged = false;

  /** Compile an incoming world's shader ahead of time, during a quiet moment.
   *  Same rationale as SplatMorphSystem.prewarm(): updateGenerator() is the
   *  stall, and it can be paid before the player is looking. */
  prewarm(sceneB: Entity): void {
    this.prewarmQueue.push(sceneB);
  }

  /** Move the point both worlds gather into. In the modifier's own space —
   *  the same space `gsplat.center` arrives in. */
  setGatherPoint(x: number, y: number, z: number): void {
    this.gather.value.x = x;
    this.gather.value.y = y;
    this.gather.value.z = z;
  }

  /** Point the flow at a pair of worlds and attach again. */
  restage(sceneA: Entity, sceneB: Entity): void {
    this.sceneEntities = [sceneA, sceneB];
    this.meshes = [];
    this.attached = false;
    this.waitLogged = false;
    this.setPhase(0);
  }

  setScenes(sceneA: Entity, sceneB: Entity): void {
    this.sceneEntities = [sceneA, sceneB];
  }

  /**
   * Let go of both worlds. Must run BEFORE either splat is unloaded — update()
   * calls updateVersion() on every attached mesh, and doing that to a disposed
   * SplatMesh is a crash.
   */
  release(): void {
    this.sceneEntities = null;
    this.meshes = [];
    this.attached = false;
    // compiledFor and prewarmQueue are kept deliberately, as in SplatMorph: a
    // later phase may restage onto a world prewarmed long ago.
  }

  /** Drive the transition, 0 (scene A) to 1 (scene B). */
  setPhase(value: number): void {
    const p = clamp01(value);

    // A gathers across [0, aEnd]; B opens across [bStart, 1], overlapping
    // around the midpoint so the point they share is never empty.
    const aEnd = 0.5 + FLOW_OVERLAP / 2;
    const bStart = 0.5 - FLOW_OVERLAP / 2;

    const aRaw = clamp01((aEnd - p) / aEnd);
    const bRaw = clamp01((p - bStart) / (1 - bStart));

    this.present[0].value = aRaw;
    this.present[1].value = bRaw;

    // Skip a world outright once it has nothing left to show. A zero-alpha
    // splat is still sorted and rasterised; an unsubmitted mesh is not.
    if (this.meshes.length === 2) {
      this.meshes[0].visible = aRaw > 0;
      this.meshes[1].visible = bRaw > 0;
    }
  }

  get isReady(): boolean {
    return this.attached;
  }

  update() {
    if (!this.attached) {
      this.tryPrewarm();
      this.tryAttach();
      return;
    }
    for (const mesh of this.meshes) mesh.updateVersion();
  }

  private tryPrewarm(): void {
    if (!this.prewarmQueue.length) return;
    const loader = this.world.getSystem(GaussianSplatLoaderSystem);
    if (!loader) return;

    this.prewarmQueue = this.prewarmQueue.filter((entity) => {
      const mesh = loader.getSplat(entity);
      if (!mesh) return true; // not loaded yet — retry next frame

      // Index 1: a prewarmed world is always the one arriving.
      mesh.worldModifier = this.createFlowModifier(1);
      mesh.updateGenerator();
      this.compiledFor.set(mesh, 1);
      console.log("[SplatFlow] incoming world prewarmed");
      return false;
    });
  }

  private tryAttach(): void {
    if (!this.sceneEntities) return;

    const loader = this.world.getSystem(GaussianSplatLoaderSystem);
    if (!loader) return;

    const meshes = this.sceneEntities.map((e) => loader.getSplat(e));
    if (meshes.some((m) => m === null)) {
      if (!this.waitLogged) {
        this.waitLogged = true;
        console.log(
          "[SplatFlow] Waiting for splats — " +
            `A:${meshes[0] ? "ready" : "loading"} ` +
            `B:${meshes[1] ? "ready" : "loading"}`,
        );
      }
      return;
    }

    this.meshes = meshes as SplatMesh[];
    this.meshes.forEach((mesh, index) => {
      // Already compiled for THIS role — the two directions are different
      // shaders, so only a role change forces the recompile.
      if (this.compiledFor.get(mesh) === index) return;
      mesh.worldModifier = this.createFlowModifier(index);
      mesh.updateGenerator();
      this.compiledFor.set(mesh, index);
    });

    this.attached = true;
    console.log("[SplatFlow] attached — 返 will gather rather than dissolve");
  }

  /**
   * Per-splat gather, in GLSL.
   *
   * `present` is this scene's formedness: 1 = exactly its original, 0 = fully
   * collapsed into the gather point. Scene 0 falls as the gesture runs, scene 1
   * rises, so the pair is a mirror and one world is always resolving.
   */
  private createFlowModifier(sceneIndex: number) {
    const present = this.present[sceneIndex];
    const gather = this.gather;
    const worldScale = dyno.dynoFloat(WORLD_SCALE);
    const waves = dyno.dynoFloat(FLOW_WAVES);

    return dyno.dynoBlock(
      { gsplat: dyno.Gsplat },
      { gsplat: dyno.Gsplat },
      ({ gsplat }) => {
        const flowDyno = new dyno.Dyno({
          inTypes: {
            gsplat: dyno.Gsplat,
            present: "float",
            gather: "vec3",
            worldScale: "float",
            waves: "float",
          },
          outTypes: { gsplat: dyno.Gsplat },
          globals: () => [
            dyno.unindent(`
              // Spark's own hash from the flow example — cheap, and good enough
              // to decorrelate neighbouring splat indices, which is all that is
              // being asked of it.
              float flowHash11(float p) {
                p = fract(p * .1031);
                p += dot(p, p + 33.33);
                return fract(p * p);
              }
            `),
          ],
          statements: ({ inputs, outputs }) =>
            dyno.unindentLines(`
              ${outputs.gsplat} = ${inputs.gsplat};

              vec3 origin = ${inputs.gsplat}.center;
              float f = clamp(${inputs.present}, 0.0, 1.0);

              // Per-splat travel rate. Without this the world contracts as a
              // rigid body and reads as a zoom; with it, some splats are still
              // out at their true position while others have already arrived,
              // which is what makes it disperse.
              float v = ${RATE_MIN.toFixed(3)} +
                        flowHash11(float(${inputs.gsplat}.index)) *
                        ${RATE_RANGE.toFixed(3)};
              vec3 p = mix(${inputs.gather}, origin, pow(f, v));

              // Ripple on the way in, normalised out of the example's unit
              // scale. Strongest fully gathered, exactly zero fully formed —
              // so an arrived world is its original, untouched.
              float ripple = length(sin(p / ${inputs.worldScale} * 2.5));
              p += ripple * ${inputs.waves} * (1.0 - f) * ${inputs.worldScale};

              ${outputs.gsplat}.center = p;

              // Shrink toward a fifth of true size rather than a fixed metre
              // value: these worlds differ hugely in scale, and a literal that
              // suits one turns the other into either dust or boulders.
              ${outputs.gsplat}.scales =
                mix(${inputs.gsplat}.scales * 0.2,
                    ${inputs.gsplat}.scales,
                    pow(f, 3.0));

              // Fade out the last of it rather than popping, and dim toward the
              // gathered state so the point reads as dense ink, not as the
              // world simply being somewhere else.
              ${outputs.gsplat}.rgba.a *= smoothstep(0.0, 0.3, f);
              ${outputs.gsplat}.rgba.rgb *= 0.5 + f * 0.5;
            `),
        });

        return {
          gsplat: flowDyno.apply({
            gsplat,
            present,
            gather,
            worldScale,
            waves,
          }).gsplat,
        };
      },
    );
  }
}
