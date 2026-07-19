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
type PhaseId = "breath" | "disc" | "reveal" | "morph" | "expand";

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

/** Ring colour — the same warm white as its halo, so the line and the light
 *  around it read as one thing rather than a grey circle with a gold glow.
 *
 *  It has to stay off pure white: 一 Breath is a pure white void, and white on
 *  white is nothing. The warmth is what makes it visible here, not brightness. */
const CIRCLE_COLOR = 0xffe9a8;
const OPACITY_MIN = 0.35;
const OPACITY_MAX = 1.0;

/** Horizontal distance (m) from the circle centre that counts as "stepped in". */
const CIRCLE_RADIUS = 0.5;

/** Circle sits this far in front of the XR origin. Close enough that stepping
 *  in is a half-step rather than a stride — at 1.2m the player was walking out
 *  of their guardian boundary to reach it. */
const CIRCLE_FRONT_Z = -0.7;

/** Ring thickness. A thin line reads as drawn rather than as a disc; the glow
 *  around it does the work of presence. */
const CIRCLE_THICKNESS = 0.035;

/** The halo: a soft radial falloff around the ring, reaching this many times
 *  the ring's radius before it has faded to nothing. */
const GLOW_RADIUS_SCALE = 2.6;
// Warm white — a pale gold rather than a saturated yellow. Against a white
// void the halo tints rather than adds, so a strong colour would read as a
// stain on paper; keeping it close to white lets it read as light that happens
// to be warm.
const GLOW_COLOR = 0xffe9a8;
const GLOW_STRENGTH = 0.55;

/** Additive blending is what real glare looks like — but it can only brighten,
 *  and 一 Breath is a PURE WHITE void, where there is nothing left to brighten
 *  toward. On white the halo must therefore tint (normal blending) rather than
 *  add, reading as an aura bleeding outward like ink on wet paper.
 *
 *  Set true if the void is ever darkened; then this becomes true glare. */
const GLOW_ADDITIVE = false;

/** A DELIBERATE hand sweep past this (half the rail) begins the experience too,
 *  for seated / desktop-emulator testing. High enough that idle hand jitter
 *  won't trip it. */
const RAIL_FALLBACK = 0.5;

/** Ignore begin-triggers for this long after the world is ready, so the player
 *  gets a beat in the void first and startup pose jumps don't skip it. */
const BEGIN_ARM_SECONDS = 1.5;

/** Frames the head must stay in the circle before it counts (rejects glitches). */
const IN_CIRCLE_FRAMES = 3;

/** Seconds a completed phase must hold before advancing (the harmony dwell).
 *  Short: the platform finishing and the handles arriving should feel like one
 *  continuous event. At 1.5 it read as the piece pausing to think. */
const DWELL_SECONDS = 0.45;

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
/** Manual vertical nudge for the platform, metres.
 *
 *  The measured placement is supposed to put the platform's top at y=0, and by
 *  every reading of the glb it should — no node transform, bounds measured at
 *  runtime through Box3. It nonetheless sits half a metre high, which is
 *  exactly half the fitted slab's thickness. That points at the offset being
 *  applied to the centre rather than the top somewhere in the chain, but I have
 *  not found where, and guessing at it has cost more than it is worth.
 *
 *  So: measured placement, then this correction on top. Note it scales with
 *  DISC_RADIUS — a bigger platform is a thicker slab, and half of a different
 *  thickness is a different number. */
const DISC_Y_NUDGE = -0.46;
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

/** 四 morph — its own swish, same speed-triggered principle as 三. */
const MORPH_GESTURE_SOUND_URL = "./sfx/sound_gestue_5.mp3";
const MORPH_GESTURE_SOUND_VOLUME = 0.5;

/** 四 morph — the world the reveal transitions INTO. */
const MORPH_SPLAT = "Scene2/newMoutains.spz";

/** 五 expand — the world the mountains transition into in turn. */
const EXPAND_SPLAT = "Scene3/Celestial Pathways Amidst Clouds.spz";

/** Handoff from 三 to 四, in seconds. The markers glow where the player left
 *  them, vanish, and return laid out for the next gesture — so the change of
 *  axis is announced by their absence rather than by them silently rotating. */
// A quick flare, not a hold: the markers spike bright and fall away, then the
// next gesture arrives almost immediately. The earlier 2.0 + 0.9 read as the
// piece stalling — the player has finished, and waiting three seconds for
// acknowledgement feels like the software thinking rather than the world
// responding.
/** How complete a two-handed gesture must be to count as finished. Not 1.0:
 *  both markers have to be carried to their very last millimetre for the
 *  average to reach it, and one hand stopping a centimetre short would stall
 *  the piece with no way for the player to know why. */
const GESTURE_COMPLETE_AT = 0.97;

const GLOW_SECONDS = 0.85;
const HANDLES_GONE_SECONDS = 0.35;

/**
 * Vertical correction for the whole world, in metres.
 *
 * Everything here assumes y = 0 is the floor, which holds only if the XR
 * runtime gave us a `local-floor` reference space AND its floor estimate is
 * accurate. Neither is guaranteed: IWSDK silently falls back to `local` (origin
 * at the head, not the floor), and even with local-floor a headset's guardian
 * floor can be off by several centimetres.
 *
 * Rather than trying to detect that, this is a dial. Enter XR, look at where
 * the taichi platform sits relative to your real floor, and adjust:
 *
 *   platform floating ABOVE the floor  ->  make this MORE NEGATIVE
 *   platform sunk BELOW the floor      ->  make this MORE POSITIVE
 *
 * It moves the world, not the viewer — offsetting the XR origin would carry
 * the camera with it and change nothing.
 */
/** Manual correction, metres. Should stay 0 now that local-floor is required —
 *  if this needs a value again, the reference space is lying and that is the
 *  thing to fix, not this. Negative lowers the world. */
const FLOOR_TRIM = 0.0;

/**
 * The player's standing eye height, in metres. THE calibration value.
 *
 * The floor is derived from this rather than taken from the runtime, because
 * the runtime's floor turned out to be unreliable: with local-floor granted and
 * the XR origin at world zero, a standing player measured 1.01m from head to
 * "floor" — which is nobody's eye height. The guardian's floor estimate was
 * simply wrong, and by a different amount on different runs, which is why every
 * fixed offset worked once and then failed.
 *
 * Measuring down from the head instead makes the piece self-calibrating: the
 * head is a thing we can actually observe, and the floor is a known distance
 * below it.
 *
 * IT IS A 1:1 DIAL. Raising this by 5cm lowers the whole world by 5cm, and
 * vice versa — so if the ground sits high, add exactly the gap you see. Set it
 * to the wearer's real standing eye height (roughly their height minus 10-12cm)
 * and it should need no further trimming.
 */
const EYE_HEIGHT = 1.68;

/** How fast the standing-height estimate falls back toward the current head
 *  position, in metres per second.
 *
 *  A plain maximum was wrong: the headset spends time ABOVE standing height
 *  every session — carried, lifted onto the head, adjusted — and a raw max
 *  latches onto that forever, putting the floor half a metre out for the whole
 *  run. Decaying makes the estimate self-correcting: it still rises instantly
 *  (standing up is registered at once) but a spurious peak washes out in a few
 *  seconds. Slow enough that crouching briefly does not disturb it. */
const HEAD_MAX_DECAY = 0.09;

/** DEV ONLY. A small readout floating in front of the player, showing the
 *  numbers that are otherwise only visible in a browser console — which a
 *  headset does not have. Set false for the real experience. */
const DEBUG_HUD = true;

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
  private readonly phases: PhaseId[] = ["breath", "disc", "reveal", "morph", "expand"];
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
  /** Vertical correction for the whole world, resolved on the first XR frame.
   *  See resolveFloor(). */
  private floorOffset = 0;
  /** Highest head position seen this session — the standing-height estimate
   *  the floor is derived from. */
  private maxHeadY = -Infinity;

  /** Handoff sub-state inside the reveal phase: idle -> glowing -> hidden ->
   *  done. Separate from PhaseId because it is a sequence within one phase. */
  private handoff: "idle" | "glowing" | "hidden" | "done" = "idle";
  private handoffTimer = 0;
  private morphEntity: Entity | null = null;
  private expandEntity: Entity | null = null;

  private circle!: THREE.Mesh;
  private circleMat!: THREE.MeshBasicMaterial;
  private haloMat!: THREE.ShaderMaterial;
  private hud: THREE.Mesh | null = null;
  private hudCtx: CanvasRenderingContext2D | null = null;
  private hudTexture: THREE.CanvasTexture | null = null;
  private hudTimer = 0;
  private domOverlay: HTMLElement | null = null;
  private readonly heartbeat = new Heartbeat();
  private readonly groundSound = new Sound(DISC_SOUND_URL, DISC_SOUND_VOLUME);
  private readonly gestureSound = new Sound(
    GESTURE_SOUND_URL,
    GESTURE_SOUND_VOLUME,
  );
  private readonly morphGestureSound = new Sound(
    MORPH_GESTURE_SOUND_URL,
    MORPH_GESTURE_SOUND_VOLUME,
  );
  /** Rising-edge state per gesture sound. False while speed is above the
   *  re-arm threshold, which prevents one continuous fast movement from
   *  retriggering every frame. Kept separate per phase so crossing into 四
   *  starts fresh rather than inheriting 三's armed state. */
  private readonly gestureState = {
    reveal: { armed: true, cooldown: 0 },
    morph: { armed: true, cooldown: 0 },
  };

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
    const ring = new THREE.RingGeometry(
      CIRCLE_RADIUS - CIRCLE_THICKNESS,
      CIRCLE_RADIUS,
      96,
    );
    ring.rotateX(-Math.PI / 2);
    this.circle = new THREE.Mesh(ring, this.circleMat);
    this.circle.position.set(0, 0.02, CIRCLE_FRONT_Z);
    this.circle.renderOrder = 10;
    this.circle.add(this.buildHalo());

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
    if (DEBUG_HUD) this.buildHud();
  }

  update(delta: number, time: number) {
    if (!this.started) {
      this.started = true;
      this.enterPhase(0);
      this.preloadReveal();
      this.preloadMorph();
      this.preloadExpand();
      void this.heartbeat.load(); // decode during the load gate
      void this.groundSound.load();
      void this.gestureSound.load();
      void this.morphGestureSound.load();
    }

    if (this.world.session) this.trackFloor(delta);
    if (DEBUG_HUD && this.world.session) this.updateHud(delta);

    const phase = this.phases[this.index];

    if (phase === "breath") {
      this.updateBreath(delta, time);
      return;
    }

    if (phase === "disc") {
      this.updateDisc(delta);
      return;
    }

    if (phase === "expand") {
      // 五 — the hands part outward, carrying the mountains into the celestial
      // world. Same mechanism as 四, restaged onto the next pair.
      const rails = this.world.getSystem(HandFollowCubeSystem);
      const morph = this.world.getSystem(SplatMorphSystem);
      if (!rails || !morph) return;

      morph.setPhase(rails.bothProgress);
      this.updateGestureSound("morph", rails.rightHandSpeed, delta);

      if (morph.isReady && this.expandEntity?.object3D?.visible === false) {
        this.expandEntity.object3D.visible = true;
        console.log("[Director] expand attached — celestial armed");
      }
      return;
    }

    if (phase === "morph") {
      // 四 — both hands sweeping in opposition drive the cross-dissolve into
      // the mountains. Averaged, so one hand alone only gets halfway.
      const rails = this.world.getSystem(HandFollowCubeSystem);
      const morph = this.world.getSystem(SplatMorphSystem);
      if (!rails || !morph) return;

      morph.setPhase(rails.bothProgress);
      this.updateGestureSound("morph", rails.rightHandSpeed, delta);

      // Reveal scene B only once the dissolve shader owns it. At phase 0 the
      // modifier renders it fully dissolved, so there is nothing to see — but
      // before attach it would draw at full opacity, straight over the bamboo.
      if (morph.isReady && this.morphEntity?.object3D?.visible === false) {
        this.morphEntity.object3D.visible = true;
        console.log("[Director] morph attached — mountains armed");
      }

      this.updateHandoff(rails.bothProgress >= GESTURE_COMPLETE_AT, delta, rails);
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

    this.updateGestureSound("reveal", cube.rightHandSpeed, delta);

    this.updateHandoff(reveal.isRevealed, delta, cube);

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
  private updateHandoff(
    complete: boolean,
    delta: number,
    cube: HandFollowCubeSystem,
  ) {
    if (this.handoff === "done") return;
    // LATCH on the first completed frame. Re-testing `complete` every frame was
    // wrong: the markers stay under the player's fingers after they finish, so
    // any drift drops progress back below the threshold — which froze the
    // sequence mid-flare with the glow stuck on and the advance never firing.
    // Finishing a gesture is an event, not a state to be maintained.
    if (this.handoff === "idle" && !complete) return;

    this.handoffTimer += delta;

    if (this.handoff === "idle") {
      this.handoff = "glowing";
      this.handoffTimer = 0;
      cube.setGlow(0);
      console.log(`[Director] ${this.phases[this.index]} complete — markers flare`);
      return;
    }

    if (this.handoff === "glowing") {
      // Fast attack, slower decay — the shape of a spark rather than a fade in
      // and out. Peaks about a fifth of the way through, so the acknowledgement
      // lands the instant the player finishes rather than swelling up to it.
      const t = Math.min(1, this.handoffTimer / GLOW_SECONDS);
      const attack = 0.2;
      const intensity =
        t < attack ? t / attack : Math.pow(1 - (t - attack) / (1 - attack), 2);
      cube.setGlow(intensity);

      if (this.handoffTimer >= GLOW_SECONDS) {
        this.handoff = "hidden";
        this.handoffTimer = 0;
        cube.setGlow(0);
        cube.setVisible(false);
        console.log("[Director] markers away");
      }
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
  private updateGestureSound(
    which: "reveal" | "morph",
    speed: number,
    delta: number,
  ) {
    const state = this.gestureState[which];
    const sound = which === "reveal" ? this.gestureSound : this.morphGestureSound;

    state.cooldown = Math.max(0, state.cooldown - delta);

    if (state.armed && speed >= GESTURE_SPEED_ON) {
      if (state.cooldown === 0) {
        void sound.play();
        state.cooldown = GESTURE_COOLDOWN;
      }
      state.armed = false;
    } else if (!state.armed && speed <= GESTURE_SPEED_OFF) {
      state.armed = true;
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
    // The halo breathes with the line, so the glow is the ring's own light
    // rather than a separate object sitting behind it.
    if (this.haloMat) this.haloMat.uniforms.uOpacity.value = 0.45 + 0.55 * pulse;

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
    if (this.morphEntity.object3D) {
      this.morphEntity.object3D.visible = false;
      this.morphEntity.object3D.position.y = this.floorOffset;
    }
    this.morphEntity.addComponent(GaussianSplatLoader, {
      splatUrl: splatPath(MORPH_SPLAT),
      animate: false,
    });
    // Compile its morph shader now rather than at the transition — see
    // SplatMorphSystem.prewarm().
    this.world.getSystem(SplatMorphSystem)?.prewarm(this.morphEntity);
  }

  /** Same as preloadMorph, for the world 五 transitions into. */
  private preloadExpand() {
    this.expandEntity = this.world.createTransformEntity();
    if (this.expandEntity.object3D) {
      this.expandEntity.object3D.visible = false;
      this.expandEntity.object3D.position.y = this.floorOffset;
    }
    this.expandEntity.addComponent(GaussianSplatLoader, {
      splatUrl: splatPath(EXPAND_SPLAT),
      animate: false,
    });
    this.world.getSystem(SplatMorphSystem)?.prewarm(this.expandEntity);
  }

  /** Create the scene-1 splat entity and start loading it now, kept invisible
   *  so it doesn't flash into the void before the player steps in. */
  private preloadReveal() {
    this.revealEntity = this.world.createTransformEntity();
    if (this.revealEntity.object3D) {
      this.revealEntity.object3D.visible = false;
      this.revealEntity.object3D.position.y = this.floorOffset;
    }
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

        // MEASURE, don't assume. The earlier version derived scale and offsets
        // from the glb's accessor min/max, which are mesh-local and ignore any
        // transform on the node holding that mesh — so if the file has one, the
        // numbers are silently wrong and the slab sits at the wrong height.
        // Box3.setFromObject walks the actual world matrices instead.
        mesh.updateMatrixWorld(true);
        const box = new THREE.Box3().setFromObject(mesh);
        const size = box.getSize(new THREE.Vector3());

        // Fit the wider horizontal axis to the intended diameter.
        const fit = (DISC_RADIUS * 2) / Math.max(size.x, size.z);
        mesh.scale.setScalar(fit);

        // Re-measure at final scale, then place by what was measured:
        //   x/z: centre the platform on the wrapper's origin
        //   y:   drop it so its TOP surface is exactly y = 0
        mesh.updateMatrixWorld(true);
        const fitted = new THREE.Box3().setFromObject(mesh);
        const centre = fitted.getCenter(new THREE.Vector3());
        mesh.position.x -= centre.x;
        mesh.position.z -= centre.z;
        // Should be enough on its own: subtracting the measured top puts the
        // top at y=0. In practice the slab still lands half its own thickness
        // high — its CENTRE ends up on the floor — so DISC_Y_NUDGE makes up the
        // difference. The cause is not yet found; the verification log below
        // reports where the top actually ended up, which is the thread to pull.
        mesh.position.y -= fitted.max.y - DISC_Y_NUDGE;

        // Offsets live on the CHILD, inside the wrapper, so they scale with it
        // — as the wrapper grows 0→1 the top stays exactly on the ground plane
        // instead of the slab rising out of the floor.
        this.disc.add(mesh);

        // Verify rather than assert: re-measure through the wrapper at full
        // scale and report where the top ACTUALLY ended up. Three attempts at
        // this placement have each been confidently wrong, so the log states
        // the result, not the intention.
        const wrapperScale = this.disc.scale.x;
        this.disc.scale.setScalar(1);
        this.disc.updateMatrixWorld(true);
        const check = new THREE.Box3().setFromObject(this.disc);
        const topInPlayer = this.player.worldToLocal(check.max.clone()).y;
        this.disc.scale.setScalar(wrapperScale);

        console.log(
          `[Director] platform: source ${size.x.toFixed(2)}x${size.y.toFixed(2)}x${size.z.toFixed(2)}m, ` +
            `fit x${fit.toFixed(2)}, applied y offset ${mesh.position.y.toFixed(3)}m ` +
            `→ TOP MEASURED AT ${topInPlayer.toFixed(3)}m (want 0.000)`,
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

  /**
   * The glow — a flat disc around the ring whose alpha falls off with distance.
   *
   * Done as geometry rather than post-processing bloom on purpose: bloom needs
   * a render-target pipeline that fights IWSDK's renderer and has to run per
   * eye in stereo, which is real cost on a Quest for one small effect. A shaded
   * quad is a single draw and behaves correctly in both eyes for free.
   *
   * The falloff peaks AT the ring rather than at the centre, so the light reads
   * as coming off the line itself rather than filling the circle in.
   */
  private buildHalo(): THREE.Mesh {
    const outer = CIRCLE_RADIUS * GLOW_RADIUS_SCALE;
    const geo = new THREE.CircleGeometry(outer, 96);
    geo.rotateX(-Math.PI / 2);

    const mat = new THREE.ShaderMaterial({
      transparent: true,
      depthWrite: false,
      side: THREE.DoubleSide,
      blending: GLOW_ADDITIVE ? THREE.AdditiveBlending : THREE.NormalBlending,
      uniforms: {
        uColor: { value: new THREE.Color(GLOW_COLOR) },
        uStrength: { value: GLOW_STRENGTH },
        // Where the ring sits within the halo disc, 0..1.
        uRingAt: { value: CIRCLE_RADIUS / outer },
        uOpacity: { value: 1 },
      },
      vertexShader: `
        varying vec2 vUv;
        void main() {
          vUv = uv;
          gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
        }
      `,
      fragmentShader: `
        uniform vec3 uColor;
        uniform float uStrength;
        uniform float uRingAt;
        uniform float uOpacity;
        varying vec2 vUv;

        void main() {
          // CircleGeometry uvs run 0..1 across the quad, so centre is (0.5,0.5)
          // and the rim is at 0.5 — scale to a 0..1 radius.
          float r = length(vUv - vec2(0.5)) * 2.0;

          // Two falloffs meeting at the ring: a short one inward so the disc
          // does not fill, a long one outward for the spread.
          float inner = smoothstep(0.0, uRingAt, r);
          float outer = 1.0 - smoothstep(uRingAt, 1.0, r);
          float a = inner * outer;
          // Squared, so the bright core stays tight and the tail goes far —
          // a linear falloff reads as a flat disc, not as light.
          a *= a;

          gl_FragColor = vec4(uColor, a * uStrength * uOpacity);
        }
      `,
    });

    this.haloMat = mat;
    const halo = new THREE.Mesh(geo, mat);
    // Just under the ring, so the line stays crisp on top of its own light.
    halo.position.y = -0.005;
    halo.renderOrder = 9;
    return halo;
  }

  /** Build the in-world debug readout. Parented to the player so it travels
   *  with them; re-aimed each frame so it stays in view. */
  private buildHud() {
    const canvas = document.createElement("canvas");
    canvas.width = 512;
    canvas.height = 190;
    this.hudCtx = canvas.getContext("2d");
    this.hudTexture = new THREE.CanvasTexture(canvas);

    this.hud = new THREE.Mesh(
      new THREE.PlaneGeometry(0.44, 0.163),
      new THREE.MeshBasicMaterial({
        map: this.hudTexture,
        transparent: true,
        depthTest: false,
        depthWrite: false,
      }),
    );
    this.hud.renderOrder = 20_000; // above everything, including the rails
    this.player.add(this.hud);
  }

  /**
   * Refresh the readout and keep it in front of the player.
   *
   * Redrawn a few times a second rather than every frame: a canvas upload per
   * frame is real cost for numbers no one can read that fast.
   */
  private updateHud(delta: number) {
    if (!this.hud || !this.hudCtx || !this.hudTexture) return;

    // Sit it below the eyeline, a little way off, facing the player.
    this.world.camera.getWorldPosition(this.camPos);
    this.player.worldToLocal(this.camPos);
    const dir = new THREE.Vector3();
    this.world.camera.getWorldDirection(dir);
    dir.y = 0;
    if (dir.lengthSq() > 1e-8) {
      dir.normalize();
      this.hud.position.set(
        this.camPos.x + dir.x * 0.85,
        this.camPos.y - 0.34,
        this.camPos.z + dir.z * 0.85,
      );
      this.hud.rotation.y = Math.atan2(-dir.x, -dir.z);
    }

    this.hudTimer += delta;
    if (this.hudTimer < 0.25) return;
    this.hudTimer = 0;

    // camPos was converted to player space above; y in that space is the
    // player's height above the XR origin — the number that says whether the
    // floor is where the app thinks it is.
    const headY = this.camPos.y;
    const rails = this.world.getSystem(HandFollowCubeSystem);

    // Measure the platform's ACTUAL top in player space, every refresh. If the
    // placement maths is right this reads 0.00 once grown; anything else is the
    // discrepancy, directly, in metres.
    let topY = Number.NaN;
    if (this.disc.children.length && this.disc.scale.x > 0.01) {
      const box = new THREE.Box3().setFromObject(this.disc);
      topY = this.player.worldToLocal(box.max.clone()).y;
    }

    const ctx = this.hudCtx;
    ctx.clearRect(0, 0, 512, 190);
    ctx.fillStyle = "rgba(0,0,0,0.72)";
    ctx.fillRect(0, 0, 512, 190);
    // The three numbers that distinguish the remaining possibilities:
    //   headWorld  — where the headset actually is in world space
    //   headLocal  — its height above the XR origin (what local-floor defines)
    //   playerY    — whether the origin group is itself lifted off world zero,
    //                which would carry every parented object up with it
    const headWorld = new THREE.Vector3();
    this.world.camera.getWorldPosition(headWorld);
    const playerWorld = new THREE.Vector3();
    this.player.getWorldPosition(playerWorld);

    ctx.fillStyle = "#ffffff";
    ctx.font = "600 26px system-ui, sans-serif";
    ctx.fillText(
      `head world ${headWorld.y.toFixed(2)}   local ${headY.toFixed(2)}`,
      18,
      40,
    );
    ctx.font = "400 24px system-ui, sans-serif";
    ctx.fillStyle = "#ffd27f";
    ctx.fillText(
      `maxHead ${this.maxHeadY.toFixed(2)}  −eye ${EYE_HEIGHT}  ` +
        `= floor ${this.floorOffset.toFixed(2)}`,
      18,
      78,
    );
    ctx.fillStyle = "#cccccc";
    ctx.fillText(
      `offset ${this.floorOffset.toFixed(2)}   phase ${this.phases[this.index]}` +
        (rails ? `   rail ${rails.railProgress.toFixed(2)}` : ""),
      18,
      118,
    );
    if (!Number.isNaN(topY)) {
      ctx.fillStyle = Math.abs(topY) < 0.03 ? "#7fe08a" : "#ffcf6b";
      ctx.fillText(`platform top  ${topY >= 0 ? "+" : ""}${topY.toFixed(2)} m`, 18, 150);
    }
    this.hudTexture.needsUpdate = true;
  }

  /**
   * Work out where the floor actually is, once, on the first XR frame.
   *
   * Everything here assumes y = 0 is the floor. That holds only when the
   * runtime granted a `local-floor` reference space — and IWSDK asks for one
   * but falls back to `local` WITHOUT complaining, which puts the origin at the
   * head instead. Whether the fallback happens varies between sessions, which
   * is why a hand-set constant could never fix this: it was right on the runs
   * that got local-floor and wrong on the runs that did not.
   *
   * So: measure. In local-floor the head sits ~1.2-1.8m above the origin; under
   * `local` it sits at ~0 by definition. Anything below FLOOR_DETECT_THRESHOLD
   * means we are in the fallback and the world must be pushed down by an
   * assumed eye height — the real one is unknowable, since the distance from
   * head to floor is precisely what the missing reference space would have
   * told us.
   */
  private resolveFloor() {
    // Kept for the first frame so nothing renders at the wrong height before
    // the continuous tracker has a sample.
    this.trackFloor(0);
  }

  /**
   * Keep the floor EYE_HEIGHT below the player's head, every frame.
   *
   * The runtime's floor is not trustworthy — with local-floor granted and the
   * origin at world zero, a standing player measured 1.01m head-to-"floor",
   * and the error differed between runs. So the floor is derived rather than
   * read, and re-derived continuously instead of once: calibrating on a single
   * frame meant entering XR while seated, or mid-step, baked that mistake in
   * for the whole session.
   *
   * Tracked against a DECAYING maximum of head height rather than the current
   * position. Following the head directly would mean crouching drags the world
   * down with you; a plain maximum latches onto the headset being carried or
   * lifted on and never recovers. Decaying gives both: instant response to
   * standing up, and a few seconds to shed a peak that was not really you.
   */
  private trackFloor(delta: number) {
    this.world.camera.getWorldPosition(this.camPos);
    const headY = this.camPos.y;

    this.maxHeadY = Number.isFinite(this.maxHeadY)
      ? Math.max(headY, this.maxHeadY - HEAD_MAX_DECAY * delta)
      : headY;
    const next = this.maxHeadY - EYE_HEIGHT + FLOOR_TRIM;
    if (Math.abs(next - this.floorOffset) < 0.01) return;

    this.floorOffset = next;
    this.applyFloorOffset();
    console.log(
      `[Director] floor: head ${headY.toFixed(2)}m − eye ${EYE_HEIGHT}m ` +
        `→ offset ${this.floorOffset.toFixed(2)}m`,
    );
  }

  /** Push every floor-relative thing to the resolved height. */
  private applyFloorOffset() {
    const y = this.floorOffset;
    this.circle.position.y = 0.02 + y;
    this.disc.position.y = y;
    if (this.revealEntity?.object3D) this.revealEntity.object3D.position.y = y;
    if (this.morphEntity?.object3D) this.morphEntity.object3D.position.y = y;
    if (this.expandEntity?.object3D) this.expandEntity.object3D.position.y = y;
    this.world.getSystem(HandFollowCubeSystem)?.setFloorOffset(y);
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
    // Each phase gets a fresh handoff, or the second gesture would inherit the
    // first's "done" and never flare.
    this.handoff = "idle";
    this.handoffTimer = 0;

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
    } else if (phase === "expand") {
      // The mountains become the world being left behind; the celestial world
      // is the one arriving. Restage rather than construct a second system —
      // the modifier, the phase uniform and the prewarm all already exist.
      if (this.morphEntity && this.expandEntity) {
        this.world
          .getSystem(SplatMorphSystem)
          ?.restage(this.morphEntity, this.expandEntity);
      } else {
        console.error("[Director] expand entered without both worlds loaded");
      }

      if (cube) {
        cube.configureForExpand();
        cube.placeInFrontOf(this.world.camera);
        cube.setFollowHead(true);
        cube.setVisible(true);
      }
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
        cube.setFollowHead(true);
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
        cube.setFloorOffset(this.floorOffset);
        cube.configureForReveal();
        cube.setFollowHead(true);
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
