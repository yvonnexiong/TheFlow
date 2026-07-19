import * as THREE from "three";
import { createSystem, Entity } from "@iwsdk/core";
import {
  GaussianSplatLoader,
  GaussianSplatLoaderSystem,
} from "./gaussianSplatLoader.js";
import { HandFollowCubeSystem } from "./handFollowCube.js";
import { SplatRevealSystem } from "./splatReveal.js";
import { Heartbeat, HEARTBEAT_BPM } from "./heartbeat.js";

// ------------------------------------------------------------
// Director — sequences the whole journey in ONE continuous session
// ------------------------------------------------------------
// Scene 0 一 breath → a white void holds until the world is ready, then a single
//              pulsing circle a step in front of the player, over a faint
//              heartbeat. The player physically STEPS INTO it to begin.
// Scene 1 二 disc   → a flat disc opens under the player's feet, growing from
//              nothing out to DISC_RADIUS. No splat here. Placeholder geometry.
// Scene 2 三 reveal → the splat world is revealed BY HAND: the rail cube's
//              progress drives the reveal wavefront directly, so the world
//              only exists as far as the player has reached.
//
// The splat is PRELOADED at startup and kept hidden, so scene 2 opens instantly
// rather than triggering a load. The white loading field blocks the start until
// that preload is ready. Each phase's underlying system is used once, so nothing
// ever re-attaches to a second set of meshes.
//
// The two-splat cross-fade (SplatMorphSystem) is no longer part of the journey.
type PhaseId = "breath" | "disc" | "reveal";

/** DEV ONLY. Scene 0 is specified as pure white with no UI, which means a
 *  successful load and a hung load look identical — both are a blank white
 *  screen. Set true while building to get a faint progress mark back; set
 *  false for the real experience. */
const SHOW_LOADING_INDICATOR = true;

/** 一 Breath is a pure white void. That white belongs to Scene 0 only — once a
 *  world exists, gaps in the splat would show it through as bright holes, so
 *  the background changes with the phase. Splat captures are never watertight;
 *  the backdrop's job from 二 onward is to be what you *don't* notice behind
 *  them. Dark reads as depth; white reads as damage. */
const VOID_COLOR = 0xffffff;
const WORLD_COLOR = 0x000000;

/** Ring colour. Grey, because 一 Breath is a pure white void — white on white
 *  is nothing. Pulses between OPACITY_MIN and OPACITY_MAX on the heartbeat. */
const CIRCLE_COLOR = 0xcccccc;
const OPACITY_MIN = 0.35;
const OPACITY_MAX = 0.8;

/** Horizontal distance (m) from the circle centre that counts as "stepped in". */
const CIRCLE_RADIUS = 0.5;

/** Circle sits this far in front of the XR origin, so beginning takes a real
 *  physical step forward. */
const CIRCLE_FRONT_Z = -1.2;

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

/** While true, HOLD on scene 1 once the disc has grown instead of advancing
 *  into scene 2. Flip to false to chain the next scene. */
const HOLD_AFTER_DISC = true;

/** Scene 1 — the ground disc.
 *
 *  PLACEHOLDER. A flat CircleGeometry standing in for the real mesh, which is
 *  still to be designed. What matters here and should survive the swap is the
 *  behaviour, not the shape: it starts at zero the moment the player steps into
 *  the circle, and opens to DISC_RADIUS under their feet on a cubic ease-out.
 *  To replace it, change the geometry in init() — the growth is driven purely
 *  by uniform scale, so any mesh authored at full size will animate unchanged. */
const DISC_RADIUS = 2.0;
const DISC_GROW_SECONDS = 2.5;
const DISC_COLOR = 0xbbbbbb;

/** DEV ONLY. Start the journey at this phase instead of the beginning, so a
 *  scene can be checked without walking the whole sequence. null = normal. */
const START_PHASE: PhaseId | null = null;

// Encode each path segment so spaces survive AND subfolder "/" is preserved.
const splatPath = (p: string) =>
  "./splats/" + p.split("/").map(encodeURIComponent).join("/");

const REVEAL_SPLAT = "Scene1/Celestial Pathways Amidst Clouds.compressed.ply";

export class DirectorSystem extends createSystem({}) {
  private readonly phases: PhaseId[] = ["breath", "disc", "reveal"];
  private index = 0;
  private started = false;
  private loaded = false;
  private dwell = 0;
  private breathElapsed = 0;
  private inCircleFrames = 0;

  private circle!: THREE.Mesh;
  private circleMat!: THREE.MeshBasicMaterial;
  private domOverlay: HTMLElement | null = null;
  private readonly heartbeat = new Heartbeat();

  private revealEntity: Entity | null = null;

  private disc!: THREE.Mesh;
  private discElapsed = 0;
  private discGrown = false;

  // Scratch vectors reused each frame to avoid per-frame allocation.
  private readonly camPos = new THREE.Vector3();
  private readonly ringPos = new THREE.Vector3();

  init() {
    // Seed circle of 一 — hidden until the world has loaded.
    // Soft grey, not white: the void behind it is pure white, so a white ring
    // would be invisible. This reads as faint but unmistakably present.
    this.circleMat = new THREE.MeshBasicMaterial({
      color: CIRCLE_COLOR,
      transparent: true,
      opacity: 0.6,
      depthWrite: false,
    });
    const ring = new THREE.RingGeometry(0.32, CIRCLE_RADIUS, 64);
    ring.rotateX(-Math.PI / 2);
    this.circle = new THREE.Mesh(ring, this.circleMat);
    this.circle.position.set(0, 0.02, CIRCLE_FRONT_Z);
    this.circle.renderOrder = 10;

    // Scene 1 — the ground disc. Flat placeholder, built at full size and
    // scaled from zero, so growth costs nothing to animate.
    const discGeo = new THREE.CircleGeometry(DISC_RADIUS, 96);
    discGeo.rotateX(-Math.PI / 2);
    this.disc = new THREE.Mesh(
      discGeo,
      new THREE.MeshBasicMaterial({
        color: DISC_COLOR,
        transparent: true,
        opacity: 0.9,
        depthWrite: false,
        side: THREE.DoubleSide,
      }),
    );
    // Under the player's feet, just above the floor so it doesn't z-fight.
    this.disc.position.set(0, 0.01, 0);
    this.disc.scale.setScalar(0);
    this.disc.renderOrder = 9;
    this.disc.visible = false;
    this.player.add(this.disc);
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

    if (phase === "disc") {
      this.updateDisc(delta);
      return;
    }

    // "reveal" — scene 2. The splat is revealed by hand: the rail cube's
    // progress drives the wavefront directly, so the world only appears as
    // far as the player has reached. Nothing advances past this yet.
    const cube = this.world.getSystem(HandFollowCubeSystem);
    const reveal = this.world.getSystem(SplatRevealSystem);
    if (cube && reveal) reveal.setProgress(cube.railProgress);
  }

  /** Scene 1 — grow the disc from nothing to DISC_RADIUS under the player. */
  private updateDisc(delta: number) {
    if (!this.discGrown) {
      this.discElapsed += delta;
      const t = Math.min(this.discElapsed / DISC_GROW_SECONDS, 1);
      // Ease out: quick to open, settling at the edge.
      const eased = 1 - Math.pow(1 - t, 3);
      this.disc.scale.setScalar(eased);
      if (t >= 1) {
        this.discGrown = true;
        console.log("[Director] disc fully grown");
      }
      return;
    }

    if (HOLD_AFTER_DISC) return;
    this.dwell += delta;
    if (this.dwell >= DWELL_SECONDS) this.advance();
  }

  private updateBreath(delta: number, time: number) {
    // Gate on the preload: keep the loading screen up until the world is ready.
    if (!this.loaded) {
      const reveal = this.world.getSystem(SplatRevealSystem);
      if (!reveal?.isReady) return;
      this.loaded = true;
      console.log("[Director] world ready — lifting the white, ring in");
      this.setLoadingVisible(false);

      // DEV: jump straight to the scene under test. Done here rather than at
      // startup so the splat is still loaded first — otherwise scene 2 opens
      // on an empty world and looks broken.
      if (START_PHASE && START_PHASE !== "breath") {
        const target = this.phases.indexOf(START_PHASE);
        console.log(`[Director] START_PHASE — skipping to "${START_PHASE}"`);
        this.index = target;
        this.enterPhase(target);
        return;
      }

      this.circle.visible = true;
      this.breathElapsed = 0;
      this.inCircleFrames = 0;
      // The only sound in the void. Silent until this moment.
      void this.heartbeat.start();
    }

    // Pulse the circle in time with the audible heartbeat, so the light and the
    // sound are one thing. HEARTBEAT_BPM/60 = beats per second → radians/s.
    const w = (HEARTBEAT_BPM / 60) * Math.PI * 2;
    const pulse = 0.5 + 0.5 * Math.sin(time * w);
    this.circleMat.opacity = OPACITY_MIN + (OPACITY_MAX - OPACITY_MIN) * pulse;

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
      flipUp: true, // this .ply exports Y-down
      enableLod: false, // DIAGNOSTIC (2026-07-18): re-run, now that it works
    });
    this.world.getSystem(SplatRevealSystem)?.setScene(this.revealEntity);
  }

  /** Set the scene backdrop, reusing the existing Color so nothing allocates
   *  mid-session. */
  private setBackground(hex: number) {
    const bg = this.world.scene.background;
    if (bg instanceof THREE.Color) bg.setHex(hex);
    else this.world.scene.background = new THREE.Color(hex);
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
      this.setBackground(VOID_COLOR);
      this.circle.visible = this.loaded;
      cube?.setVisible(false);
      return;
    }

    // Leaving 一: the world takes over from the heartbeat, and the white void
    // gives way so splat gaps don't glare through it.
    this.setBackground(WORLD_COLOR);
    this.circle.visible = false;
    this.heartbeat.stop();

    if (phase === "disc") {
      // Scene 1 — no splat. Just the disc opening under the player's feet.
      cube?.setVisible(false);
      this.disc.visible = true;
      this.disc.scale.setScalar(0);
      this.discElapsed = 0;
      this.discGrown = false;
    } else if (phase === "reveal") {
      // Scene 2 — the splat, revealed by hand. Start fully hidden; the rail
      // cube's progress is what brings it in, so the player does the revealing.
      cube?.setVisible(true);
      if (this.revealEntity?.object3D) this.revealEntity.object3D.visible = true;
      this.world.getSystem(SplatRevealSystem)?.setProgress(0);
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
      /* Scene 0 is pure white, silent, and has no UI — so the loading gate is
         an empty white field. No spinner, no text, no progress. It still
         blocks the start until the world is ready; it just doesn't announce
         itself. The player sees white, then a circle. */
      #flow-loading { position:fixed; inset:0; z-index:10000; background:#fff;
        display:flex; align-items:center; justify-content:center; }
      /* Dev-only mark. Grey on white so it reads against the void, and quiet
         enough that leaving it on by accident isn't jarring. */
      #flow-loading .mark { width:34px; height:34px; border:2px solid #e6e6e6;
        border-top-color:#bbb; border-radius:50%;
        animation:flowspin 1.4s linear infinite; }
      @keyframes flowspin { to { transform:rotate(360deg); } }
    `;
    document.head.appendChild(style);

    const el = document.createElement("div");
    el.id = "flow-loading";
    el.innerHTML = SHOW_LOADING_INDICATOR ? `<div class="mark"></div>` : "";
    document.body.appendChild(el);
    this.domOverlay = el;
  }

  private setLoadingVisible(visible: boolean) {
    if (this.domOverlay) this.domOverlay.style.display = visible ? "flex" : "none";
  }
}
