import * as THREE from "three";
import { GLTFLoader } from "three/examples/jsm/loaders/GLTFLoader.js";
import { createSystem, Entity } from "@iwsdk/core";
import {
  GaussianSplatLoader,
  GaussianSplatLoaderSystem,
} from "./gaussianSplatLoader.js";
import { HandFollowCubeSystem } from "./handFollowCube.js";
import { SplatRevealSystem } from "./splatReveal.js";
import { SplatMorphSystem } from "./splatMorph.js";
import { Heartbeat } from "./heartbeat.js";
import { Sound } from "./audio.js";

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
type PhaseId = "breath" | "disc" | "reveal" | "morph";

/** DEV ONLY. Scene 0 is specified as pure white with no UI, which means a
 *  successful load and a hung load look identical — both are a blank white
 *  screen. Set true while building to get a faint progress mark back; set
 *  false for the real experience. */
const SHOW_LOADING_INDICATOR = true;

/** White throughout: 一 Breath's void and the backdrop behind the revealed
 *  world are now the same. Kept as two constants because they are conceptually
 *  different surfaces and may diverge again.
 *
 *  Trade-off worth remembering: gaps in a splat capture show the backdrop
 *  through, and against white those read as bright holes rather than depth. If
 *  the world starts looking punctured, this is the first thing to change. */
const VOID_COLOR = 0xffffff;
const WORLD_COLOR = 0xffffff;

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
const HOLD_AFTER_DISC = false;

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

/** The platform mesh. Authored as a 1m disc standing UPRIGHT in XY, and NOT
 *  centred on its own origin: X spans ±0.499 but Y runs 0→0.998, with the
 *  0.229 thickness in Z. Laying it flat therefore needs a re-centre as well as
 *  a rotation, or it grows off to one side.
 *
 *  Growth still drives the wrapper's scale 0→1, so the animation is unchanged
 *  by the swap — rotation, fit and offset are applied once, to the child. */
const DISC_URL = "./glbs/taichi_platform.glb";
/** Fired the instant the platform starts growing, not when it finishes — the
 *  sound is the ground arriving, and it has to land on the movement. */
const DISC_SOUND_URL = "./sfx/effect_ground_1.mp3";
const DISC_SOUND_VOLUME = 0.7;

/** Scene 2 — a swish when the right hand moves with intent.
 *
 *  Thresholds come from what tracked hands actually do: at rest they jitter at
 *  0.05–0.15 m/s, casual repositioning is 0.3–0.5, and a deliberate sweep is
 *  1–2. Firing at 0.9 sits clear of the first two.
 *
 *  Two separate guards, because they catch different failures: the hysteresis
 *  gap stops a hand hovering near the threshold from stuttering, and the
 *  cooldown bounds how often it can fire even when speed genuinely oscillates
 *  across the whole gap. */
const GESTURE_SOUND_URL = "./sfx/sound_gestue_2.mp3";
const GESTURE_SOUND_VOLUME = 0.5;
const GESTURE_SPEED_ON = 0.9; // m/s — fire above this
const GESTURE_SPEED_OFF = 0.4; // m/s — re-arm below this
const GESTURE_COOLDOWN = 0.4; // seconds, minimum between triggers

/** 四 morph — the world the reveal transitions INTO. */
const MORPH_SPLAT = "Scene2/newMoutains.spz";

/** Handoff from 三 to 四, in seconds. The markers glow where the player left
 *  them, vanish, and return laid out for the next gesture — so the change of
 *  axis is announced by their absence rather than by them silently rotating. */
const GLOW_SECONDS = 2.0;
const HANDLES_GONE_SECONDS = 0.9;
const DISC_SOURCE_DIAMETER = 0.998;
/** Half the source thickness (its Z half-extent) — becomes the Y half-extent
 *  once flat, and is how far to sink the mesh so its TOP sits on the ground. */
const DISC_HALF_THICKNESS = 0.114;
/** Half the source diameter axis (Y runs 0→0.998), which becomes Z once flat —
 *  how far to shift it back so the disc's centre is the wrapper's origin. */
const DISC_CENTER_OFFSET = 0.499;

/** DEV ONLY. Start the journey at this phase instead of the beginning, so a
 *  scene can be checked without walking the whole sequence. null = normal. */
const START_PHASE: PhaseId | null = null;

/** Scene 2 — the moon. The .glb is a ~2m sphere authored at the origin, so
 *  scale 1 is life-size-ish. Placed in front of wherever the player is standing
 *  when scene 2 begins, same as the rail — the XR origin is not where they are
 *  by then, since scene 0 made them walk. */
const MOON_URL = "./glbs/moon.glb";
const MOON_DISTANCE = 3.0; // metres ahead of the head
const MOON_HEIGHT = 1.6; // metres, roughly eye level
const MOON_SCALE = 1.0;

// Encode each path segment so spaces survive AND subfolder "/" is preserved.
const splatPath = (p: string) =>
  "./splats/" + p.split("/").map(encodeURIComponent).join("/");

const REVEAL_SPLAT = "Scene1/Enchanted Bamboo Forest Sanctuary.compressed.ply";

export class DirectorSystem extends createSystem({}) {
  private readonly phases: PhaseId[] = ["breath", "disc", "reveal", "morph"];
  private index = 0;
  private started = false;
  private loaded = false;
  private dwell = 0;
  private breathElapsed = 0;
  private inCircleFrames = 0;
  /** 一 has begun — set on the first XR frame after loading. */
  private breathBegun = false;
  /** Highest railProgress seen this session — diagnostic only. */
  private railPeak = 0;

  /** Handoff sub-state inside the reveal phase: idle -> glowing -> hidden ->
   *  done. Separate from PhaseId because it is a sequence within one phase. */
  private handoff: "idle" | "glowing" | "hidden" | "done" = "idle";
  private handoffTimer = 0;
  private morphEntity: Entity | null = null;

  private circle!: THREE.Mesh;
  private circleMat!: THREE.MeshBasicMaterial;
  private domOverlay: HTMLElement | null = null;
  private readonly heartbeat = new Heartbeat();
  private readonly groundSound = new Sound(DISC_SOUND_URL, DISC_SOUND_VOLUME);
  private readonly gestureSound = new Sound(
    GESTURE_SOUND_URL,
    GESTURE_SOUND_VOLUME,
  );
  /** False while speed is above the re-arm threshold — prevents one continuous
   *  fast movement from retriggering every frame. */
  private gestureArmed = true;
  private gestureCooldown = 0;

  private revealEntity: Entity | null = null;

  private moon: THREE.Object3D | null = null;

  private disc!: THREE.Group;
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

    // Scene 1 — the ground platform. An empty wrapper exists immediately so the
    // phase logic never has to care whether the mesh has finished loading; the
    // glb is parented into it when it arrives.
    this.disc = new THREE.Group();
    // Centred on the SEED CIRCLE, not the XR origin — the player walks forward
    // into the circle, so the origin is a stride behind them by the time this
    // grows. y = 0 exactly: the child is sunk so its top surface is the ground.
    this.disc.position.set(0, 0, CIRCLE_FRONT_Z);
    this.disc.scale.setScalar(0);
    this.disc.visible = false;
    this.player.add(this.disc);
    this.loadDisc();
    this.circle.visible = false;
    this.player.add(this.circle);

    this.buildLoadingOverlay();
  }

  update(delta: number, time: number) {
    if (!this.started) {
      this.started = true;
      this.enterPhase(0);
      this.preloadReveal();
      this.preloadMorph();
      void this.heartbeat.load(); // decode during the load gate
      void this.groundSound.load();
      void this.gestureSound.load();
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

    if (phase === "morph") {
      // 四 — both hands sweeping in opposition drive the cross-dissolve into
      // the mountains. Averaged, so one hand alone only gets halfway.
      const rails = this.world.getSystem(HandFollowCubeSystem);
      const morph = this.world.getSystem(SplatMorphSystem);
      if (!rails || !morph) return;

      morph.setPhase(rails.bothProgress);

      // Reveal scene B only once the dissolve shader owns it. At phase 0 the
      // modifier renders it fully dissolved, so there is nothing to see — but
      // before attach it would draw at full opacity, straight over the bamboo.
      if (morph.isReady && this.morphEntity?.object3D?.visible === false) {
        this.morphEntity.object3D.visible = true;
        console.log("[Director] morph attached — mountains armed");
      }
      return;
    }

    // "reveal" — scene 2. The splat is revealed by hand: the right rail's
    // progress drives the wavefront directly, so the world only appears as
    // far as the player has reached.
    const cube = this.world.getSystem(HandFollowCubeSystem);
    const reveal = this.world.getSystem(SplatRevealSystem);
    if (!cube || !reveal) return;

    const p = cube.railProgress;
    reveal.setProgress(p);

    this.updateGestureSound(cube.rightHandSpeed, delta);

    this.updateRevealHandoff(reveal.isRevealed, delta, cube);

    // Report the high-water mark, so it is visible whether the gesture is
    // actually reaching 1.0 — a rail that stalls at 0.8 and a reveal that is
    // mathematically short look identical from inside the headset.
    if (p > this.railPeak + 0.02) {
      this.railPeak = p;
      console.log(
        `[Director] rail ${p.toFixed(2)} → fully revealed to ` +
          `${reveal.fullyRevealedRadius.toFixed(0)}m`,
      );
    }
  }

  /**
   * The beat between 三 and 四.
   *
   * Once the world is fully revealed: glow both markers for GLOW_SECONDS, hide
   * them, pause, then advance. Held in the reveal phase rather than in enter/
   * exit hooks because it is a timed sequence, not an instant.
   */
  private updateRevealHandoff(
    revealed: boolean,
    delta: number,
    cube: HandFollowCubeSystem,
  ) {
    if (!revealed || this.handoff === "done") return;

    this.handoffTimer += delta;

    if (this.handoff === "idle") {
      this.handoff = "glowing";
      this.handoffTimer = 0;
      cube.setGlow(true);
      console.log("[Director] reveal complete — markers glowing");
      return;
    }

    if (this.handoff === "glowing" && this.handoffTimer >= GLOW_SECONDS) {
      this.handoff = "hidden";
      this.handoffTimer = 0;
      cube.setGlow(false);
      cube.setVisible(false);
      console.log("[Director] markers away");
      return;
    }

    if (this.handoff === "hidden" && this.handoffTimer >= HANDLES_GONE_SECONDS) {
      this.handoff = "done";
      this.advance();
    }
  }

  /**
   * Fire the gesture swish on a RISING edge of hand speed.
   *
   * Rising-edge rather than level-triggered: a level test would retrigger every
   * frame the hand stays fast, which at 72Hz is a machine gun.
   */
  private updateGestureSound(speed: number, delta: number) {
    this.gestureCooldown = Math.max(0, this.gestureCooldown - delta);

    if (this.gestureArmed && speed >= GESTURE_SPEED_ON) {
      if (this.gestureCooldown === 0) {
        void this.gestureSound.play();
        this.gestureCooldown = GESTURE_COOLDOWN;
      }
      this.gestureArmed = false;
    } else if (!this.gestureArmed && speed <= GESTURE_SPEED_OFF) {
      this.gestureArmed = true;
    }
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
      console.log("[Director] world ready — white lifted, waiting for XR");
      // The overlay must come down even outside XR: it sits above the "enter"
      // button, so leaving it up would make entering the session impossible.
      this.setLoadingVisible(false);
    }

    // 一 does not begin until the player is actually inside the session. The
    // Director runs on the flat page too, and without this the ring would pulse
    // and the heartbeat would play to a desktop tab before anyone has entered —
    // the sound arriving before its cause.
    if (!this.world.session) return;

    if (!this.breathBegun) {
      this.breathBegun = true;

      // DEV: jump straight to the scene under test. Held until now so the splat
      // has loaded AND we are in XR — otherwise the scene plays out on the flat
      // page before the player ever sees it.
      if (START_PHASE && START_PHASE !== "breath") {
        const target = this.phases.indexOf(START_PHASE);
        console.log(`[Director] START_PHASE — skipping to "${START_PHASE}"`);
        this.index = target;
        this.enterPhase(target);
        return;
      }

      // DIAGNOSTIC: which reference space did we actually get?
      //
      // IWSDK asks for local-floor but falls back to `local` without
      // complaining, and under `local` the origin is the HEAD at session start,
      // not the floor — so ground-level content ends up floating at whatever
      // height the player happened to be. Camera Y tells us which we got:
      // ~1.5-1.8 standing (or ~1.2 seated) means local-floor is working;
      // near 0 means we are in `local` and everything at y=0 is at eye level.
      this.world.camera.getWorldPosition(this.camPos);
      console.log(
        `[Director] in XR — ring in, heartbeat in. ` +
          `camera y=${this.camPos.y.toFixed(2)}m ` +
          `(≈0 means reference space fell back to "local" and ground is wrong)`,
      );
      this.circle.visible = true;
      this.breathElapsed = 0;
      this.inCircleFrames = 0;
      // The only sound in the void. Silent until this moment.
      void this.heartbeat.start();
    }

    // Pulse the circle in time with the audible heartbeat, so the light and the
    // sound are one thing. The tempo comes from the decoded sample's own length
    // (falling back to HEARTBEAT_BPM until it has loaded), so swapping the mp3
    // re-syncs the ring automatically.
    const w = (this.heartbeat.bpm / 60) * Math.PI * 2;
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

  /**
   * Start loading the 四 splat at startup, kept invisible.
   *
   * Only the LOADING is done early. setScenes() still waits for the morph
   * phase, because splatMorph and splatReveal both drive `worldModifier` and
   * there is only one slot — attaching the morph while 三 is still revealing
   * would silently overwrite the reveal's own modifier.
   */
  private preloadMorph() {
    this.morphEntity = this.world.createTransformEntity();
    if (this.morphEntity.object3D) this.morphEntity.object3D.visible = false;
    this.morphEntity.addComponent(GaussianSplatLoader, {
      splatUrl: splatPath(MORPH_SPLAT),
      animate: false,
    });
  }

  /** Create the scene-1 splat entity and start loading it now, kept invisible
   *  so it doesn't flash into the void before the player steps in. */
  private preloadReveal() {
    this.revealEntity = this.world.createTransformEntity();
    if (this.revealEntity.object3D) this.revealEntity.object3D.visible = false;
    this.revealEntity.addComponent(GaussianSplatLoader, {
      splatUrl: splatPath(REVEAL_SPLAT),
      animate: false,
      flipUp: true, // the compressed .ply exports Y-down
      enableLod: false, // DIAGNOSTIC (2026-07-18): re-run, now that it works
    });
    this.world.getSystem(SplatRevealSystem)?.setScene(this.revealEntity);
  }

  /** Load the platform mesh, lay it flat, and fit it to DISC_RADIUS. */
  private loadDisc() {
    new GLTFLoader().load(
      DISC_URL,
      (gltf) => {
        const mesh = gltf.scene;
        // Flipped onto the ground plane (+90°, so the intended face is up).
        mesh.rotation.x = Math.PI / 2;

        const fit = (DISC_RADIUS * 2) / DISC_SOURCE_DIAMETER;
        mesh.scale.setScalar(fit);

        // After the flip the disc spans z 0→0.998 and y ±0.114, so:
        //   z: shift back half a diameter to centre it on the wrapper origin
        //   y: sink half the thickness so the TOP surface lands at y = 0
        //
        // Both offsets live on the CHILD, inside the wrapper, so they scale
        // with it — as the wrapper grows 0→1 the top stays exactly on the
        // ground plane and the centre stays put, instead of the slab rising
        // out of the floor.
        mesh.position.set(
          0,
          -DISC_HALF_THICKNESS * fit,
          -DISC_CENTER_OFFSET * fit,
        );

        this.disc.add(mesh);
        console.log(
          `[Director] platform loaded (${DISC_RADIUS * 2}m across, top at ground)`,
        );
      },
      undefined,
      (err) => console.warn("[Director] platform failed to load", err),
    );
  }

  /** Load the scene-2 moon and park it hidden until that phase begins. */
  private loadMoon() {
    new GLTFLoader().load(
      MOON_URL,
      (gltf) => {
        this.moon = gltf.scene;
        this.moon.scale.setScalar(MOON_SCALE);
        this.moon.visible = false;
        this.player.add(this.moon);
        console.log("[Director] moon loaded");
      },
      undefined,
      (err) => console.warn("[Director] moon failed to load", err),
    );
  }

  /**
   * Put an object a fixed distance in front of the player's head.
   *
   * Same reasoning as the rail: everything here is parented to the XR origin,
   * but scene 0 makes the player physically walk, so the origin is no longer
   * where they are. Yaw only — the object stays at MOON_HEIGHT regardless of
   * where they are looking vertically.
   */
  private placeAhead(obj: THREE.Object3D, distance: number, height: number) {
    this.world.camera.getWorldPosition(this.camPos);
    const local = this.player.worldToLocal(this.camPos.clone());

    const dir = new THREE.Vector3();
    this.world.camera.getWorldDirection(dir);
    dir.y = 0;
    if (dir.lengthSq() < 1e-8) dir.set(0, 0, -1); // looking straight up/down
    dir.normalize();

    obj.position.set(
      local.x + dir.x * distance,
      height,
      local.z + dir.z * distance,
    );
    obj.lookAt(local.x, height, local.z);
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
      // Scene 1 — no splat. Just the platform opening under the player's feet.
      cube?.setVisible(false);
      this.disc.visible = true;
      this.disc.scale.setScalar(0);
      this.discElapsed = 0;
      this.discGrown = false;
      void this.groundSound.play();
    } else if (phase === "morph") {
      // The revealed world becomes scene A; the mountains (preloaded at
      // startup) are scene B. Both stay loaded — a cross-dissolve needs both
      // present at once. Scene B stays HIDDEN until the morph modifier has
      // attached, or it would flash in at full opacity for a frame before the
      // shader has anything to say about it.
      if (this.revealEntity && this.morphEntity) {
        this.world
          .getSystem(SplatMorphSystem)
          ?.setScenes(this.revealEntity, this.morphEntity);
      } else {
        console.error("[Director] morph entered without both worlds loaded");
      }

      // Rails come back laid out horizontally and opposed, seated in front of
      // wherever the player is now.
      if (cube) {
        cube.configureForMorph();
        cube.placeInFrontOf(this.world.camera);
        cube.setVisible(true);
      }
    } else if (phase === "reveal") {
      // Scene 2 — the splat, revealed by hand. Start fully hidden; the rail
      // cube's progress is what brings it in, so the player does the revealing.
      // Seat the rails in front of wherever the player actually ended up after
      // stepping in — not where they started. Must happen before setVisible so
      // they never show for a frame in the wrong place.
      if (!cube) {
        console.error("[Director] HandFollowCubeSystem NOT REGISTERED");
      } else {
        cube.configureForReveal();
        cube.placeInFrontOf(this.world.camera);
      }
      cube?.setVisible(true);

      if (this.revealEntity?.object3D) this.revealEntity.object3D.visible = true;
      this.world.getSystem(SplatRevealSystem)?.setProgress(0);
    }
  }

  private exitPhase(phase: PhaseId) {
    const loader = this.world.getSystem(GaussianSplatLoaderSystem);

    if (phase === "reveal") {
      // Detach the reveal modifier but KEEP the splat loaded and keep the
      // entity reference: 四 cross-dissolves *from* this world, so unloading
      // it here would morph away from nothing.
      this.world.getSystem(SplatRevealSystem)?.reset();
    }
    void loader;
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
