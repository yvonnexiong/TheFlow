import * as THREE from "three";
import { createSystem, Entity } from "@iwsdk/core";
import {
  GaussianSplatLoader,
  GaussianSplatLoaderSystem,
} from "./gaussianSplatLoader.js";
import { HandFollowCubeSystem } from "./handFollowCube.js";
import { SplatRevealSystem } from "./splatReveal.js";
import { SplatMorphSystem } from "./splatMorph.js";

// ------------------------------------------------------------
// Director — sequences the whole journey in ONE continuous session
// ------------------------------------------------------------
// 一 breath  → a loading screen holds until the world is ready, then an empty
//              void with a single glowing circle a step in front of the player.
//              The player physically STEPS INTO the circle to begin.
// 二 reveal  → the ground grows outward from the circle, on its own, over a few
//              seconds ("ground grows outward infinitely").
// 三 morph   → that world disperses as another reconverges, scrubbed by hand
//              (behind HOLD_AFTER_REVEAL — off while scene 1 is being built).
//
// The scene-1 splat is PRELOADED at startup and kept hidden, so stepping in
// blooms it instantly instead of triggering a load. A loading overlay blocks
// the start until that preload is ready. Each phase's underlying system is used
// once, so nothing ever re-attaches to a second set of meshes.

type PhaseId = "breath" | "reveal" | "morph";

/** Horizontal distance (m) from the circle centre that counts as "stepped in". */
const CIRCLE_RADIUS = 0.5;

/** Circle sits this far in front of the XR origin, so beginning takes a real
 *  physical step forward. */
const CIRCLE_FRONT_Z = -1.2;

/** Seconds for the ground to grow fully outward once the player steps in. */
const GROW_SECONDS = 5;

/** Temporary: when false, stepping in shows the whole world instantly instead
 *  of playing the spread-reveal animation. Set true to restore the bloom. */
const REVEAL_ANIMATION = false;

/** A DELIBERATE hand sweep past this (half the rail) begins the experience too,
 *  for seated / desktop-emulator testing. High enough that idle hand jitter
 *  won't trip it. */
const RAIL_FALLBACK = 0.5;

/** Ignore begin-triggers for this long after the world is ready, so the player
 *  gets a beat in the void first and startup pose jumps don't skip it. */
const BEGIN_ARM_SECONDS = 1.5;

/** Frames the head must stay in the circle before it counts (rejects glitches). */
const IN_CIRCLE_FRAMES = 3;

/** Seconds a completed phase must hold before advancing (the harmony dwell). */
const DWELL_SECONDS = 1.5;

/** While true, HOLD on scene 1 after the ground blooms instead of advancing
 *  into the morph. Flip to false to chain the next scene. */
const HOLD_AFTER_REVEAL = true;

// Encode each path segment so spaces survive AND subfolder "/" is preserved.
const splatPath = (p: string) =>
  "./splats/" + p.split("/").map(encodeURIComponent).join("/");

const REVEAL_SPLAT = "Scene1/Celestial Pathways Amidst Clouds.compressed.ply";
const MORPH_A_SPLAT = "Scene1_Ancient Chinese Bamboo Courtyard.spz";
const MORPH_B_SPLAT = "Scene2_Ruined Sanctuary Apocalyptic Aftermath.spz";

export class DirectorSystem extends createSystem({}) {
  private readonly phases: PhaseId[] = ["breath", "reveal", "morph"];
  private index = 0;
  private started = false;
  private loaded = false;
  private dwell = 0;
  private breathElapsed = 0;
  private inCircleFrames = 0;

  private circle!: THREE.Mesh;
  private circleMat!: THREE.MeshBasicMaterial;
  private domOverlay: HTMLElement | null = null;

  private revealEntity: Entity | null = null;
  private morphA: Entity | null = null;
  private morphB: Entity | null = null;

  // Scratch vectors reused each frame to avoid per-frame allocation.
  private readonly camPos = new THREE.Vector3();
  private readonly ringPos = new THREE.Vector3();

  init() {
    // Seed circle of 一 — hidden until the world has loaded.
    this.circleMat = new THREE.MeshBasicMaterial({
      color: 0xffffff,
      transparent: true,
      opacity: 0.6,
      depthWrite: false,
    });
    const ring = new THREE.RingGeometry(0.32, CIRCLE_RADIUS, 64);
    ring.rotateX(-Math.PI / 2);
    this.circle = new THREE.Mesh(ring, this.circleMat);
    this.circle.position.set(0, 0.02, CIRCLE_FRONT_Z);
    this.circle.renderOrder = 10;
    this.circle.visible = false;
    this.player.add(this.circle);

    this.buildLoadingOverlay();
  }

  update(delta: number, time: number) {
    if (!this.started) {
      this.started = true;
      this.enterPhase(0);
      this.preloadReveal();
    }

    const phase = this.phases[this.index];

    if (phase === "breath") {
      this.updateBreath(delta, time);
      return;
    }

    if (phase === "reveal") {
      // The reveal grows itself (kicked off on enter). Hold here once fully
      // grown unless auto-advance is enabled.
      const reveal = this.world.getSystem(SplatRevealSystem);
      if (reveal?.isRevealed && !HOLD_AFTER_REVEAL) {
        this.dwell += delta;
        if (this.dwell >= DWELL_SECONDS) this.advance();
      } else {
        this.dwell = 0;
      }
      return;
    }

    // "morph" — driven by railProgress inside SplatMorphSystem; nothing for the
    // Director to do here yet.
  }

  private updateBreath(delta: number, time: number) {
    // Gate on the preload: keep the loading screen up until the world is ready.
    if (!this.loaded) {
      const reveal = this.world.getSystem(SplatRevealSystem);
      if (!reveal?.isReady) return;
      this.loaded = true;
      this.setLoadingVisible(false);
      this.circle.visible = true;
      this.breathElapsed = 0;
      this.inCircleFrames = 0;
    }

    // Pulse the circle like a heartbeat until the player steps in.
    this.circleMat.opacity = 0.45 + 0.35 * (0.5 + 0.5 * Math.sin(time * 2.0));

    // Settle before arming, so the first pose / hand jump can't skip the void.
    this.breathElapsed += delta;
    if (this.breathElapsed < BEGIN_ARM_SECONDS) return;

    this.inCircleFrames = this.playerInCircle() ? this.inCircleFrames + 1 : 0;

    const cube = this.world.getSystem(HandFollowCubeSystem);
    const railFallback = cube ? cube.railProgress > RAIL_FALLBACK : false;

    if (this.inCircleFrames >= IN_CIRCLE_FRAMES) {
      console.log("[Director] begin: stepped into the circle");
      this.advance();
    } else if (railFallback) {
      console.log("[Director] begin: deliberate hand sweep (fallback)");
      this.advance();
    }
  }

  /** Create the scene-1 splat entity and start loading it now, kept invisible
   *  so it doesn't flash into the void before the player steps in. */
  private preloadReveal() {
    this.revealEntity = this.world.createTransformEntity();
    if (this.revealEntity.object3D) this.revealEntity.object3D.visible = false;
    this.revealEntity.addComponent(GaussianSplatLoader, {
      splatUrl: splatPath(REVEAL_SPLAT),
      animate: false,
    });
    this.world.getSystem(SplatRevealSystem)?.setScene(this.revealEntity);
  }

  /** True when the player's head is horizontally within the seed circle. */
  private playerInCircle(): boolean {
    this.world.camera.getWorldPosition(this.camPos);
    this.circle.getWorldPosition(this.ringPos);
    const dx = this.camPos.x - this.ringPos.x;
    const dz = this.camPos.z - this.ringPos.z;
    return Math.hypot(dx, dz) < CIRCLE_RADIUS;
  }

  private advance() {
    this.exitPhase(this.phases[this.index]);
    this.index = Math.min(this.index + 1, this.phases.length - 1);
    this.dwell = 0;
    this.enterPhase(this.index);
  }

  private enterPhase(i: number) {
    const phase = this.phases[i];
    console.log(`[Director] → ${phase}`);
    this.dwell = 0;

    const cube = this.world.getSystem(HandFollowCubeSystem);
    cube?.reset();

    if (phase === "breath") {
      this.circle.visible = this.loaded;
      cube?.setVisible(false);
      return;
    }

    this.circle.visible = false;

    if (phase === "reveal") {
      cube?.setVisible(false);
      // Already preloaded — make it visible, then either grow it or (temporary)
      // snap it to fully revealed.
      if (this.revealEntity?.object3D) this.revealEntity.object3D.visible = true;
      const reveal = this.world.getSystem(SplatRevealSystem);
      if (REVEAL_ANIMATION) reveal?.grow(GROW_SECONDS);
      else reveal?.setProgress(1);
    } else if (phase === "morph") {
      cube?.setVisible(true);
      this.morphA = this.world.createTransformEntity();
      this.morphA.addComponent(GaussianSplatLoader, {
        splatUrl: splatPath(MORPH_A_SPLAT),
        animate: false,
      });
      this.morphB = this.world.createTransformEntity();
      this.morphB.addComponent(GaussianSplatLoader, {
        splatUrl: splatPath(MORPH_B_SPLAT),
        animate: false,
      });
      this.world.getSystem(SplatMorphSystem)?.setScenes(this.morphA, this.morphB);
    }
  }

  private exitPhase(phase: PhaseId) {
    const loader = this.world.getSystem(GaussianSplatLoaderSystem);

    if (phase === "reveal") {
      this.world.getSystem(SplatRevealSystem)?.reset();
      if (this.revealEntity) {
        loader?.unload(this.revealEntity, { animate: false }).catch(() => {});
      }
      this.revealEntity = null;
    }
  }

  // ----------------------------------------------------------
  // Loading overlay — a full-screen 2D screen shown until the scene-1 splat is
  // ready. It sits above the "Enter XR" button (z-index 10000 > 9999), so the
  // player can't start the experience until the world has loaded.
  // ----------------------------------------------------------
  private buildLoadingOverlay() {
    const style = document.createElement("style");
    style.textContent = `
      #flow-loading { position:fixed; inset:0; z-index:10000; display:flex;
        flex-direction:column; align-items:center; justify-content:center;
        background:#000; color:#fff; gap:22px;
        font:500 20px system-ui,-apple-system,sans-serif; letter-spacing:.02em; }
      #flow-loading .spin { width:44px; height:44px; border:3px solid #2a2a2a;
        border-top-color:#fff; border-radius:50%;
        animation:flowspin 1s linear infinite; }
      @keyframes flowspin { to { transform:rotate(360deg); } }
    `;
    document.head.appendChild(style);

    const el = document.createElement("div");
    el.id = "flow-loading";
    el.innerHTML = `<div class="spin"></div><div>Awakening the world…</div>`;
    document.body.appendChild(el);
    this.domOverlay = el;
  }

  private setLoadingVisible(visible: boolean) {
    if (this.domOverlay) this.domOverlay.style.display = visible ? "flex" : "none";
  }
}
