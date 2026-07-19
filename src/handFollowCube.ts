import * as THREE from "three";
import { createSystem } from "@iwsdk/core";
import { GLTFLoader } from "three/examples/jsm/loaders/GLTFLoader.js";

// ------------------------------------------------------------
// Hand-driven markers on rails — one per hand
// ------------------------------------------------------------
// A yin-yang sphere rides a rail within arm's reach of each hand. The player
// TOUCHES one and carries it along; it only moves while that hand's fingertip
// is on it. Left hand drives the left marker, right the right.
//
// Rails are RECONFIGURABLE, because the two gestures differ:
//   三 reveal — both VERTICAL, side by side. Lifting draws the world upward.
//               Only the right rail's progress drives the reveal.
//   四 morph  — both sweeping INWARD to meet at the midline, along opposed
//               ARCS: the right bows up and settles, the left bows down and
//               rises, so the pair traces the halves of a taiji. The two are
//               averaged, so the transition needs both hands.
//   五 expand — the hands part again, travelling OUTWARD from just past where
//               they met, on lines offset from the previous ones so the new
//               gesture is visibly not a continuation of the old.
//
// Progress always runs 0 -> 1 along whatever the rail's own travel is, so a
// consumer never needs to know which way a hand is physically moving.
//
// Touching ENGAGES a marker; it does not place it. On contact the marker stays
// exactly where it is and from then on follows the hand's VELOCITY — each frame
// it moves by however far the fingertip moved. Grabbing therefore never jumps
// the gesture, wherever the player happened to reach in.

const TRACK_Z = -0.42; // within arm's reach, so the markers can be touched

/** Lateral offset of each VERTICAL rail from centre — roughly shoulder width,
 *  so each falls under its own hand without the two crowding each other. */
const RAIL_SPREAD = 0.26;

// Vertical travel (三 reveal): chest to eye level, 0.55m — a relaxed lift
// rather than an overhead stretch, so the top is comfortable to hold.
const TRACK_BOTTOM = 1.0;
const TRACK_TOP = 1.55;

// Horizontal travel (四 morph): the hands sweep INWARD toward each other, the
// right from its side and the left from its own.
//
// The rails stop just SHY of centre rather than running the full width. Full
// width made the two paths overlap, so the hands crossed in front of one
// another mid-gesture — and a hand passing behind the other leaves the
// headset's cameras with nothing to track, dropping the marker at exactly the
// moment the gesture was meant to be at its most committed. Ending where they
// meet keeps both hands visible and separated the whole way.
//
// The lines also sit at different heights, so even at the inner limit the
// hands are clear of each other vertically.
const SWEEP_OUTER = 0.34; // where each hand starts, left/right of centre
const SWEEP_INNER = 0.04; // where it stops — just short of the midline
const SWEEP_Y_RIGHT = 1.32; // right hand's line, the higher one
const SWEEP_Y_LEFT = 1.1; // left hand's line, a little lower

/** How far each sweep bows away from the straight line between its ends.
 *  Right arcs UP, left arcs DOWN — opposed curves, so the two hands trace the
 *  halves of a taiji rather than two parallel wipes. Endpoints are unaffected:
 *  the bow is a sine that is zero at both ends. */
const SWEEP_ARC = 0.13;

// 五 expand: the hands part again, travelling OUTWARD from where they met.
//
// Each starts offset from where the previous gesture ended — the right a
// little lower and further right, the left a little higher and further left —
// so the new rails are visibly not the old ones, and the hands are already
// past each other before they begin. Picking up exactly where they stopped
// would read as the same gesture continuing.
const EXPAND_INNER = 0.1; // where each hand starts, either side of centre
const EXPAND_OUTER = 0.42; // where it ends, out at the edge of reach
const EXPAND_Y_RIGHT = 1.2; // lower than the sweep's right line (1.32)
const EXPAND_Y_LEFT = 1.22; // higher than the sweep's left line (1.10)

const MARKER_SIZE = 0.14;

/** Radius of the dots marking each end of a rail. Small enough to read as
 *  punctuation on the line rather than as another thing to grab. */
const CAP_RADIUS = 0.011;

/** How close the fingertip must be to a marker's centre to grab it (metres).
 *  Generous relative to the 0.14m sphere — hand tracking is noisy, and a miss
 *  that silently does nothing is more frustrating than an early catch. */
const TOUCH_RADIUS = 0.14;

/** Once held, the finger may stray this far before the marker is released.
 *  Larger than TOUCH_RADIUS so brief jitter doesn't drop it mid-gesture. */
const RELEASE_RADIUS = 0.26;

// Fingertip, not wrist: the gesture is touching a specific object, and the
// wrist sits ~15cm behind where the player believes their hand is — enough to
// make contact feel wrong.
const JOINT: XRHandJoint = "index-finger-tip";

/** Smoothing on reported hand speed, 0..1 — weight given to the newest sample.
 *  Tracking drops frames often enough that raw per-frame velocity spikes
 *  wildly; averaging over ~3 frames removes that without noticeable lag. */
const SPEED_SMOOTHING = 0.35;

/** How fast the rails chase the player's head when following, per second.
 *  Loose enough that turning your head does not drag them rigidly with you,
 *  tight enough that they are always within reach. */
const FOLLOW_RATE = 3.0;

/** Below this, the rails are already where they should be — stops them
 *  creeping forever on floating-point residue. */
const FOLLOW_EPSILON = 0.005;

/** The marker meshes — yin-yang spheres, 1m across, sitting ON their own
 *  origin (centre at y=0.499) rather than centred on it. A different model per
 *  hand; both share the same geometry and fitting. */
const MARKER_URL = {
  left: "./glbs/yin-yang+sphere+3d+model_white.glb",
  right: "./glbs/yin-yang+sphere+3d+model_black.glb",
} as const;
const MARKER_SOURCE_DIAMETER = 0.998;
const MARKER_CENTRE_Y = 0.499;

/** Per-hand tint — now NEUTRAL, because the light/dark distinction lives in the
 *  models themselves rather than being faked in the material.
 *
 *  The previous values multiplied one texture down and lifted the other with
 *  emissive to manufacture the contrast. Applying that on top of models that
 *  are already white and black would double it: the white one would blow out
 *  and the black one would crush to a featureless dot.
 *
 *  Kept as a structure rather than deleted, since the held and glow states
 *  still return to these as their resting values. */
const TINT = {
  left: { color: 0xffffff, emissive: 0x000000 },
  right: { color: 0xffffff, emissive: 0x000000 },
} as const;

/** Emissive while held, so contact reads on both tints. */
const HELD_EMISSIVE = 0x66ccff;
/** Emissive while glowing — the beat after a gesture completes. */
const GLOW_EMISSIVE = 0xffffff;

/** Halo colour, matching the seed circle's — warm white rather than a
 *  saturated yellow, because on a white void it tints rather than adds. */
const GLOW_COLOR = 0xffe9a8;
/** Halo size as a multiple of the marker. */
const GLOW_SCALE = 3.4;

/**
 * Soft radial falloff, drawn once into a texture.
 *
 * A sprite rather than a shaded quad here: sprites always face the camera, and
 * the markers are spheres seen from any angle — a flat quad would show itself
 * edge-on the moment the player moved. Shared by both rails; a texture has no
 * per-instance state.
 */
let glowTexture: THREE.Texture | null = null;
function getGlowTexture(): THREE.Texture {
  if (glowTexture) return glowTexture;

  const size = 128;
  const canvas = document.createElement("canvas");
  canvas.width = canvas.height = size;
  const ctx = canvas.getContext("2d")!;

  const g = ctx.createRadialGradient(
    size / 2,
    size / 2,
    0,
    size / 2,
    size / 2,
    size / 2,
  );
  // Squared falloff, same reasoning as the seed circle's halo: linear reads as
  // a flat disc, this keeps a tight core with a long tail.
  for (let i = 0; i <= 8; i++) {
    const t = i / 8;
    g.addColorStop(t, `rgba(255,255,255,${Math.pow(1 - t, 2).toFixed(3)})`);
  }
  ctx.fillStyle = g;
  ctx.fillRect(0, 0, size, size);

  glowTexture = new THREE.CanvasTexture(canvas);
  return glowTexture;
}

type Handedness = "left" | "right";

/** Which way a rail runs, and between what. Progress is always 0 at `from`. */
type RailConfig = {
  /** Axis of travel in rail-local space. */
  axis: "x" | "y";
  /** Position at progress 0. May be GREATER than `to` — that is how a rail
   *  runs right-to-left without any consumer needing to know. */
  from: number;
  /** Position at progress 1. */
  to: number;
  /** The coordinate on the other axis AT BOTH ENDS: x for a vertical rail, y
   *  for a horizontal one. */
  cross: number;
  /** Bow away from `cross` at the midpoint, as a signed distance. Positive
   *  bows toward +cross-axis. Zero (the default) gives a straight rail. The
   *  profile is a half sine, so both endpoints stay exactly on `cross`. */
  arc?: number;
};

/**
 * One rail and its marker, bound to a single hand.
 *
 * Each keeps its own hold state and reference position — sharing them across
 * hands would mean grabbing with one releases the other, and a two-handed
 * gesture would fight itself.
 */
class Rail {
  readonly group = new THREE.Group();
  /** The thing that actually moves. A bare anchor, so the visual can arrive
   *  later without the hold or position logic caring. */
  private readonly marker = new THREE.Group();
  private readonly bar: THREE.Mesh;
  private readonly caps: THREE.Mesh[] = [];
  /** Materials of the attached visual, cloned per rail so tinting one hand
   *  never bleeds into the other. */
  private materials: THREE.MeshStandardMaterial[] = [];
  private held = false;
  private prevTip: number | null = null;
  private glow = 0;
  private halo!: THREE.Sprite;
  private cfg: RailConfig | null = null;
  private readonly markerWorld = new THREE.Vector3();

  constructor(private readonly side: Handedness) {
    // Unit geometry, scaled per configuration — the rail changes orientation
    // between gestures, and rebuilding geometry each time would be wasteful.
    this.bar = new THREE.Mesh(
      new THREE.BoxGeometry(1, 1, 1),
      new THREE.MeshBasicMaterial({ color: 0x334455 }),
    );
    this.group.add(this.bar);

    // Dots, not bars: the ends are terminals, and a crossbar reads as a second
    // rail crossing the first. A sphere also stays the same shape whichever way
    // the rail runs, so it needs no per-orientation scaling.
    for (let i = 0; i < 2; i++) {
      const cap = new THREE.Mesh(
        new THREE.SphereGeometry(CAP_RADIUS, 16, 12),
        new THREE.MeshBasicMaterial({ color: 0x334455 }),
      );
      this.caps.push(cap);
      this.group.add(cap);
    }

    this.halo = new THREE.Sprite(
      new THREE.SpriteMaterial({
        map: getGlowTexture(),
        color: GLOW_COLOR,
        transparent: true,
        depthWrite: false,
        opacity: 0,
      }),
    );
    this.halo.scale.setScalar(MARKER_SIZE * GLOW_SCALE);
    this.halo.visible = false;
    this.marker.add(this.halo);

    this.group.add(this.marker);
  }

  /** Lay the rail out for a gesture, and park the marker at progress 0. */
  configure(cfg: RailConfig): void {
    this.cfg = cfg;

    // Rebuild the bar along the path. Straight rails could reuse a scaled box,
    // but a bowed one cannot — and configure() runs twice a session, so the
    // rebuild costs nothing worth optimising away.
    this.bar.geometry.dispose();
    this.bar.geometry = this.buildBarGeometry(cfg);
    this.bar.position.set(0, 0, 0);
    this.bar.scale.set(1, 1, 1);

    this.caps.forEach((cap, i) => {
      cap.position.copy(this.pointAt(cfg, i === 0 ? 0 : 1));
    });

    this.marker.position.copy(this.pointAt(cfg, 0));
    this.release();
  }

  /**
   * Position along the rail at t = 0..1.
   *
   * The primary axis interpolates linearly between the endpoints; the other
   * bows away from `cross` by a half sine, which is exactly zero at t=0 and
   * t=1 — so adding an arc never moves where the gesture starts or ends.
   */
  private pointAt(cfg: RailConfig, t: number): THREE.Vector3 {
    const along = cfg.from + (cfg.to - cfg.from) * t;
    const bow = cfg.cross + (cfg.arc ?? 0) * Math.sin(Math.PI * t);
    return cfg.axis === "y"
      ? new THREE.Vector3(bow, along, TRACK_Z)
      : new THREE.Vector3(along, bow, TRACK_Z);
  }

  /** A thin tube following the path, or a plain box when it is straight. */
  private buildBarGeometry(cfg: RailConfig): THREE.BufferGeometry {
    if (!cfg.arc) {
      const span = Math.abs(cfg.to - cfg.from);
      const box =
        cfg.axis === "y"
          ? new THREE.BoxGeometry(0.004, span, 0.004)
          : new THREE.BoxGeometry(span, 0.004, 0.004);
      const mid = this.pointAt(cfg, 0.5);
      box.translate(mid.x, mid.y, mid.z);
      return box;
    }

    const samples: THREE.Vector3[] = [];
    for (let i = 0; i <= 32; i++) samples.push(this.pointAt(cfg, i / 32));
    return new THREE.TubeGeometry(
      new THREE.CatmullRomCurve3(samples),
      32,
      0.002,
      6,
      false,
    );
  }

  /** 0 at `from`, 1 at `to`, whichever direction that runs. */
  get isHeld(): boolean {
    return this.held;
  }

  get progress(): number {
    if (!this.cfg) return 0;
    const pos = this.marker.position[this.cfg.axis];
    return (pos - this.cfg.from) / (this.cfg.to - this.cfg.from);
  }

  reset(): void {
    if (this.cfg) this.marker.position.copy(this.pointAt(this.cfg, 0));
    this.release();
  }

  release(): void {
    this.held = false;
    this.prevTip = null;
    // Clear the flare too. A glow left running would otherwise survive into the
    // next gesture, since configure() releases but never touched it.
    this.glow = 0;
    this.halo.visible = false;
    (this.halo.material as THREE.SpriteMaterial).opacity = 0;
    this.refreshColour(true);
  }

  /** No hand tracked this frame — keep position, drop the hold. */
  untracked(): void {
    this.held = false;
    this.prevTip = null;
    this.refreshColour(false);
  }

  /** Glow intensity 0..1 — the beat after a gesture completes. */
  setGlow(intensity: number): void {
    this.glow = Math.min(1, Math.max(0, intensity));
    this.halo.visible = this.glow > 0.001;
    (this.halo.material as THREE.SpriteMaterial).opacity = this.glow;
    this.refreshColour(true);
  }

  /**
   * Drive from this hand's fingertip, given in WORLD space.
   *
   * `root` is the moved/rotated parent the rail lives under: distances are
   * compared in world, then the tip is converted into rail-local space so the
   * delta is measured along the rail's own axis.
   */
  drive(tipWorld: THREE.Vector3, root: THREE.Object3D): void {
    if (!this.cfg) return;

    this.marker.getWorldPosition(this.markerWorld);
    const dist = tipWorld.distanceTo(this.markerWorld);

    // Asymmetric thresholds: harder to catch than to keep. Without hysteresis
    // the marker drops and re-grabs repeatedly as the fingertip jitters around
    // a single boundary.
    const wasHeld = this.held;
    this.held = this.held ? dist < RELEASE_RADIUS : dist < TOUCH_RADIUS;
    this.refreshColour(true);

    if (!this.held) {
      this.prevTip = null; // released — next grab starts a fresh reference
      return;
    }

    const local = root.worldToLocal(tipWorld.clone());
    const along = local[this.cfg.axis];

    // The frame contact is made, record the reference and move NOTHING. This is
    // what stops the marker snapping: the player may have reached in anywhere,
    // and where carries no meaning — only what they do next.
    if (!wasHeld || this.prevTip === null) {
      this.prevTip = along;
      return;
    }

    const delta = along - this.prevTip;
    this.prevTip = along;

    // Velocity match at 1:1 — the marker travels exactly as far as the hand
    // did, and stops the instant the hand stops. No smoothing: a lerp toward a
    // target would lag on acceleration and coast past on stop, reading as the
    // marker being dragged rather than carried.
    const lo = Math.min(this.cfg.from, this.cfg.to);
    const hi = Math.max(this.cfg.from, this.cfg.to);
    const moved = THREE.MathUtils.clamp(
      this.marker.position[this.cfg.axis] + delta,
      lo,
      hi,
    );

    // Advance along the PRIMARY axis from the hand's motion, then re-seat onto
    // the curve. The hand drives the sweep; the arc is the rail's shape, not
    // something the player has to trace — trying to follow a bow exactly would
    // make the marker drop the moment they cut the corner.
    const t = (moved - this.cfg.from) / (this.cfg.to - this.cfg.from);
    this.marker.position.copy(this.pointAt(this.cfg, t));
  }

  /**
   * Parent a loaded marker mesh onto this rail's anchor and tint it.
   *
   * Materials are cloned first: the glb loads once and is cloned per rail, and
   * three.js clones share material instances — without this, tinting the right
   * hand would repaint the left one too.
   */
  attachVisual(visual: THREE.Object3D): void {
    const fit = MARKER_SIZE / MARKER_SOURCE_DIAMETER;
    visual.scale.setScalar(fit);
    // Model sits on its origin rather than centred on it — drop it back so the
    // sphere's middle is the point the touch test measures against.
    visual.position.y = -MARKER_CENTRE_Y * fit;

    const tint = TINT[this.side];
    visual.traverse((obj) => {
      const mesh = obj as THREE.Mesh;
      if (!mesh.isMesh) return;
      const src = Array.isArray(mesh.material) ? mesh.material : [mesh.material];
      const cloned = src.map((m) => {
        const c = (m as THREE.MeshStandardMaterial).clone();
        c.color.setHex(tint.color);
        c.emissive.setHex(tint.emissive);
        return c;
      });
      mesh.material = cloned.length === 1 ? cloned[0] : cloned;
      this.materials.push(...cloned);
    });

    this.marker.add(visual);
    this.refreshColour(true);
  }

  /**
   * Repaint from current state. Only emissive moves — the base colour carries
   * the yin-yang tint, and overwriting it would erase the black/white
   * distinction the moment a hand came near.
   */
  private refreshColour(tracked: boolean) {
    if (!this.materials.length) return;
    const tint = TINT[this.side];
    for (const m of this.materials) {
      m.color.setHex(tint.color);
      if (this.glow > 0) {
        // Lerp toward the glow rather than snapping, so intensity actually
        // reads on the marker itself and not only on the halo around it.
        m.emissive.setHex(tint.emissive).lerp(
          new THREE.Color(GLOW_EMISSIVE),
          this.glow,
        );
      }
      else if (this.held) m.emissive.setHex(HELD_EMISSIVE);
      else if (tracked) m.emissive.setHex(tint.emissive);
      else m.emissive.setHex(0x000000);
    }
  }
}

export class HandFollowCubeSystem extends createSystem({}) {
  private root!: THREE.Group;
  private rails!: Record<Handedness, Rail>;
  private readonly tip = new THREE.Vector3();
  /** Floor correction, applied to the rails' heights — see Director's
   *  FLOOR_OFFSET. Set before the rails are placed. */
  private floorOffset = 0;
  /** While true, the rails keep themselves in front of the player instead of
   *  staying where they were first placed. */
  private follow = false;
  private readonly headLocal = new THREE.Vector3();
  private readonly lookDir = new THREE.Vector3();

  // Right-hand speed tracking, for gesture-triggered audio.
  private readonly prevRightTip = new THREE.Vector3();
  private hasPrevRightTip = false;
  private smoothedRightSpeed = 0;

  /** The RIGHT marker's progress, 0..1 — what drives the 三 reveal. */
  get railProgress(): number {
    return this.rails ? this.rails.right.progress : 0;
  }

  /** The LEFT marker's progress, 0..1. */
  get leftProgress(): number {
    return this.rails ? this.rails.left.progress : 0;
  }

  /** Average of both hands — what drives the 四 morph. Both must travel for
   *  the transition to complete; one hand alone gets halfway. */
  get bothProgress(): number {
    return this.rails
      ? (this.rails.left.progress + this.rails.right.progress) / 2
      : 0;
  }

  /** Right fingertip speed in m/s, smoothed. 0 when untracked — a hand that
   *  vanishes has no velocity, and reporting its last would fire gestures on
   *  tracking loss. */
  get rightHandSpeed(): number {
    return this.smoothedRightSpeed;
  }

  /** Lay both rails out VERTICALLY, side by side, for the 三 reveal. */
  configureForReveal(): void {
    this.rails.left.configure({
      axis: "y",
      from: TRACK_BOTTOM,
      to: TRACK_TOP,
      cross: -RAIL_SPREAD,
    });
    this.rails.right.configure({
      axis: "y",
      from: TRACK_BOTTOM,
      to: TRACK_TOP,
      cross: +RAIL_SPREAD,
    });
  }

  /**
   * Lay both rails out HORIZONTALLY and opposed, for the 四 morph.
   *
   * The right hand sweeps right-to-left (its `from` is the greater x) and the
   * left sweeps left-to-right, on a slightly lower line so the two hands pass
   * rather than collide at the crossover.
   */
  configureForMorph(): void {
    // Right starts on the right and travels inward; left mirrors it. Both end
    // just short of the midline, so they meet rather than pass.
    this.rails.right.configure({
      axis: "x",
      from: +SWEEP_OUTER,
      to: +SWEEP_INNER,
      cross: SWEEP_Y_RIGHT,
      arc: +SWEEP_ARC, // rises, then settles back to where it began
    });
    this.rails.left.configure({
      axis: "x",
      from: -SWEEP_OUTER,
      to: -SWEEP_INNER,
      cross: SWEEP_Y_LEFT,
      arc: -SWEEP_ARC, // dips, then returns — the mirror of the right
    });
  }

  /**
   * Lay both rails out for 五 expand — the hands parting outward.
   *
   * Mirrors configureForMorph but reversed: progress 0 is nearest the midline
   * and 1 is out at the edge of reach, so "complete" still means the gesture
   * was carried all the way through, and bothProgress needs no special casing.
   *
   * The start points are deliberately offset from where the sweep ended — the
   * right lower and further right, the left higher and further left. Beginning
   * exactly where the hands stopped would read as the same gesture continuing
   * rather than a new one being offered.
   */
  configureForExpand(): void {
    this.rails.right.configure({
      axis: "x",
      from: +EXPAND_INNER,
      to: +EXPAND_OUTER,
      cross: EXPAND_Y_RIGHT,
    });
    this.rails.left.configure({
      axis: "x",
      from: -EXPAND_INNER,
      to: -EXPAND_OUTER,
      cross: EXPAND_Y_LEFT,
    });
  }

  /** Glow both markers, 0..1. */
  setGlow(intensity: number): void {
    this.rails?.left.setGlow(intensity);
    this.rails?.right.setGlow(intensity);
  }

  reset(): void {
    this.rails?.left.reset();
    this.rails?.right.reset();
  }

  init() {
    // Parent everything to the XR origin (this.player). Poses from
    // frame.getJointPose are in the XR reference space, and the XROrigin group
    // *is* that space in scene terms — so a reference-space position can be
    // used directly as a local position here. Adding to world.scene instead
    // would drift as soon as the user locomotes.
    this.root = new THREE.Group();
    this.player.add(this.root);

    this.rails = { left: new Rail("left"), right: new Rail("right") };
    this.root.add(this.rails.left.group, this.rails.right.group);
    this.configureForReveal();

    this.drawOverSplats(this.root);
    this.loadMarkers();
  }

  /**
   * Load the marker mesh once and give each rail its own clone.
   *
   * drawOverSplats runs again afterwards: the glb arrives long after init, so
   * the always-on-top treatment applied there would otherwise miss it and the
   * markers alone would be swallowed by the revealed world.
   */
  private loadMarkers() {
    const loader = new GLTFLoader();
    for (const side of ["left", "right"] as const) {
      loader.load(
        MARKER_URL[side],
        (gltf) => {
          // No clone needed now that each hand has its own file — but the
          // material still gets cloned inside attachVisual, since a model could
          // later be shared between the two.
          this.rails[side].attachVisual(gltf.scene);
          // Re-apply: the glb arrives long after init, so the always-on-top
          // treatment would otherwise miss it and the marker alone would be
          // swallowed by the revealed world.
          this.drawOverSplats(this.root);
          console.log(`[Rails] ${side} marker attached`);
        },
        undefined,
        (err) => console.warn(`[Rails] ${side} marker failed to load`, err),
      );
    }
  }

  /**
   * Draw the rails and markers on top of the splat world.
   *
   * They are controls, not scenery — they must stay readable wherever the
   * player has revealed the world to. Splats sit at renderOrder -10 and the
   * revealed world routinely occupies the space between the eye and TRACK_Z,
   * so by ordinary depth testing a marker is simply *inside* the world and
   * gets covered. Same trick uiPanel.ts uses: AlwaysDepth passes the depth
   * test unconditionally, and a high renderOrder draws it last.
   */
  private drawOverSplats(root: THREE.Object3D) {
    root.traverse((obj) => {
      obj.renderOrder = 10_000;
      const mat = (obj as THREE.Mesh).material;
      if (!mat) return;
      for (const m of Array.isArray(mat) ? mat : [mat]) {
        m.depthTest = true;
        m.depthWrite = true;
        m.depthFunc = THREE.AlwaysDepth;
      }
    });
  }

  setVisible(visible: boolean): void {
    if (this.root) this.root.visible = visible;
  }

  /** Shift the rails vertically along with the rest of the world. */
  setFloorOffset(metres: number): void {
    this.floorOffset = metres;
  }

  /** Keep the rails in front of the player as they turn, rather than leaving
   *  them where they were first placed. */
  setFollowHead(on: boolean): void {
    this.follow = on;
  }

  /** True while either hand is holding its marker. */
  private get anyHeld(): boolean {
    return this.rails.left.isHeld || this.rails.right.isHeld;
  }

  /**
   * Ease the rails toward being in front of the player.
   *
   * Skipped entirely while a marker is held: moving the rail out from under a
   * hand mid-gesture would change the marker's position without the hand
   * having moved, which is exactly the snap the whole touch design avoids.
   * So the rails reposition between gestures, never during one.
   */
  private followHead(delta: number) {
    if (!this.root?.parent || this.anyHeld) return;

    this.world.camera.getWorldPosition(this.headLocal);
    this.root.parent.worldToLocal(this.headLocal);
    this.world.camera.getWorldDirection(this.lookDir);

    const k = 1 - Math.exp(-FOLLOW_RATE * delta); // frame-rate independent

    const dx = this.headLocal.x - this.root.position.x;
    const dz = this.headLocal.z - this.root.position.z;
    if (Math.hypot(dx, dz) > FOLLOW_EPSILON) {
      this.root.position.x += dx * k;
      this.root.position.z += dz * k;
    }

    if (Math.hypot(this.lookDir.x, this.lookDir.z) > 1e-4) {
      const target = Math.atan2(-this.lookDir.x, -this.lookDir.z);
      // Shortest way round, so passing through +/-pi does not spin the rails
      // the long way about.
      let diff = target - this.root.rotation.y;
      while (diff > Math.PI) diff -= Math.PI * 2;
      while (diff < -Math.PI) diff += Math.PI * 2;
      this.root.rotation.y += diff * k;
    }
  }

  /**
   * Re-seat the rails an arm's length in front of the player's head.
   *
   * They are parented to the XR origin, but the player physically WALKS during
   * scene 0 — stepping into the seed circle moves them ~1.2m forward while the
   * origin stays put. Rails fixed in origin space would then be inches from
   * their face, or behind them.
   *
   * Only yaw is taken from the head, so the rails stay level and at fixed
   * heights however the player is looking.
   */
  placeInFrontOf(camera: THREE.Camera): void {
    if (!this.root?.parent) return;

    const headWorld = new THREE.Vector3();
    camera.getWorldPosition(headWorld);
    const headLocal = this.root.parent.worldToLocal(headWorld.clone());

    const dir = new THREE.Vector3();
    camera.getWorldDirection(dir);

    // Straight up/down gives a degenerate horizontal direction — keep the
    // current yaw rather than snapping somewhere arbitrary.
    if (Math.hypot(dir.x, dir.z) > 1e-4) {
      // A group rotated by θ about Y sends its local -Z to (-sinθ, 0, -cosθ);
      // matching that to the look direction gives θ = atan2(-x, -z).
      this.root.rotation.y = Math.atan2(-dir.x, -dir.z);
    }

    this.root.position.set(headLocal.x, this.floorOffset, headLocal.z);
    // A hold cannot survive the rails moving out from under it.
    this.rails?.left.release();
    this.rails?.right.release();
  }

  update(delta: number, _time: number) {
    const frame = this.xrFrame;
    const refSpace = this.xrManager.getReferenceSpace();

    if (!frame || !refSpace) {
      this.rails.left.untracked();
      this.rails.right.untracked();
      this.hasPrevRightTip = false;
      this.smoothedRightSpeed = 0;
      return;
    }

    // Each hand drives only its own rail, so a tracked left hand can never
    // move the right marker.
    for (const side of ["left", "right"] as const) {
      const source = this.findSource(side);
      if (!source || !this.readTip(source, frame, refSpace)) {
        this.rails[side].untracked();
        if (side === "right") {
          this.hasPrevRightTip = false;
          this.smoothedRightSpeed = 0;
        }
        continue;
      }
      // Poses arrive in reference space, which IS player space in scene terms;
      // the rails sit under a moved/rotated root, so compare in world.
      this.player.localToWorld(this.tip);
      if (side === "right") this.trackRightSpeed(delta);
      this.rails[side].drive(this.tip, this.root);
    }

    if (this.follow) this.followHead(delta);
  }

  /** Measure right-fingertip speed frame to frame, in world metres. */
  private trackRightSpeed(delta: number) {
    if (this.hasPrevRightTip && delta > 0) {
      const raw = this.prevRightTip.distanceTo(this.tip) / delta;
      this.smoothedRightSpeed +=
        (raw - this.smoothedRightSpeed) * SPEED_SMOOTHING;
    }
    this.prevRightTip.copy(this.tip);
    this.hasPrevRightTip = true;
  }

  private findSource(side: Handedness): XRInputSource | null {
    const session = this.world.session;
    if (!session) return null;
    for (const source of session.inputSources) {
      if (source.handedness === side) return source;
    }
    return null;
  }

  /** Fill `this.tip` from a source's fingertip (or grip). False if untracked. */
  private readTip(
    source: XRInputSource,
    frame: XRFrame,
    refSpace: XRReferenceSpace,
  ): boolean {
    // getJointPose is optional — absent on runtimes without hand input.
    if (source.hand && frame.getJointPose) {
      const joint = source.hand.get(JOINT);
      // Null whenever the joint isn't currently tracked — routinely, at the
      // edge of the headset's camera FOV.
      const pose = joint ? frame.getJointPose(joint, refSpace) : null;
      if (pose) {
        const t = pose.transform.position;
        this.tip.set(t.x, t.y, t.z);
        return true;
      }
    }

    // Controller grip, or a hand whose joints we couldn't read.
    if (source.gripSpace) {
      const pose = frame.getPose(source.gripSpace, refSpace);
      if (pose) {
        const t = pose.transform.position;
        this.tip.set(t.x, t.y, t.z);
        return true;
      }
    }

    return false;
  }
}
