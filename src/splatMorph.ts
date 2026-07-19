import { createSystem, Entity } from "@iwsdk/core";
import { SplatMesh, dyno } from "@sparkjsdev/spark";
import { GaussianSplatLoaderSystem } from "./gaussianSplatLoader.js";
import { HandFollowCubeSystem } from "./handFollowCube.js";

// ------------------------------------------------------------
// Hand-driven morph between two Gaussian Splat worlds
// ------------------------------------------------------------
// Sliding the cube along its rail (see handFollowCube.ts) scrubs a single
// `phase` uniform from 0 to 1. At phase 0 scene A is fully present and scene B
// is fully dissolved; at phase 1 they've swapped. In between, both scenes'
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
const SCATTER_RADIUS = 2.5;

/** How far splats shrink at full dispersal (0.4 = down to 40% size). */
const SCATTER_SHRINK = 0.6;

export class SplatMorphSystem extends createSystem({}) {
  private sceneEntities: [Entity, Entity] | null = null;
  private meshes: SplatMesh[] = [];
  private phase = dyno.dynoFloat(0);
  private attached = false;
  private waitLogged = false;

  /**
   * Register the two splat entities to morph between. Call once, right after
   * creating them; the modifiers attach later, on the first frame where both
   * meshes have finished loading.
   */
  setScenes(sceneA: Entity, sceneB: Entity): void {
    this.sceneEntities = [sceneA, sceneB];
  }

  update() {
    if (!this.attached) {
      this.tryAttach();
      return;
    }

    // Rail position drives the morph directly — no smoothing, so the worlds
    // track the hand exactly like the cube does.
    const cubeSystem = this.world.getSystem(HandFollowCubeSystem);
    this.phase.value = cubeSystem ? cubeSystem.railProgress : 0;

    // Push the new uniform value into the compiled program. updateVersion() is
    // the cheap per-frame call; the uniform updater registered at compile time
    // copies phase.value into the GPU uniform.
    for (const mesh of this.meshes) mesh.updateVersion();
  }

  /**
   * Attach the morph modifier once both splats have loaded. Polled from
   * update() because loading is async and the 65MB/29MB scenes take a while.
   */
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
      mesh.worldModifier = this.createMorphModifier(index);
      mesh.updateGenerator(); // once — full pipeline recompile
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
    const phase = this.phase;
    const index = dyno.dynoInt(sceneIndex);
    const radius = dyno.dynoFloat(SCATTER_RADIUS);
    const shrink = dyno.dynoFloat(SCATTER_SHRINK);

    return dyno.dynoBlock(
      { gsplat: dyno.Gsplat },
      { gsplat: dyno.Gsplat },
      ({ gsplat }) => {
        const morphDyno = new dyno.Dyno({
          inTypes: {
            gsplat: dyno.Gsplat,
            phase: "float",
            sceneIndex: "int",
            radius: "float",
            shrink: "float",
          },
          outTypes: { gsplat: dyno.Gsplat },
          globals: () => [
            dyno.unindent(`
              // Stable per-splat random direction, keyed on splat index so a
              // given splat always scatters the same way.
              vec3 morphHash3(int n) {
                float x = float(n);
                return fract(sin(vec3(x, x + 1.0, x + 2.0)) * 43758.5453123);
              }

              float morphEase(float t) {
                return t * t * (3.0 - 2.0 * t);
              }
            `),
          ],
          statements: ({ inputs, outputs }) =>
            dyno.unindentLines(`
              ${outputs.gsplat} = ${inputs.gsplat};

              // Scene 0 leaves as phase rises; scene 1 leaves as it falls.
              float s = (${inputs.sceneIndex} == 0)
                ? ${inputs.phase}
                : (1.0 - ${inputs.phase});
              s = clamp(s, 0.0, 1.0);

              float e = morphEase(s);

              // Random unit direction. The epsilon keeps normalize() safe when
              // the hash lands near zero, which would otherwise produce NaN
              // positions and make those splats vanish or streak.
              vec3 r = morphHash3(${inputs.gsplat}.index) * 2.0 - 1.0;
              vec3 dir = normalize(r + vec3(1e-4));

              ${outputs.gsplat}.center += dir * (e * ${inputs.radius});
              ${outputs.gsplat}.scales *= (1.0 - ${inputs.shrink} * e);
              ${outputs.gsplat}.rgba.a *= (1.0 - e);
            `),
        });

        return {
          gsplat: morphDyno.apply({
            gsplat,
            phase,
            sceneIndex: index,
            radius,
            shrink,
          }).gsplat,
        };
      },
    );
  }
}
