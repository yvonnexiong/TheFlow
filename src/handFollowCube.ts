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

// Far enough to sit inside the headset's RECORDING frame, which is narrower
// and from a different viewpoint than the eyes — at 0.42m the markers were
// visible to the player but cropped out of captures. Still comfortably within
// reach.
const TRACK_Z = -0.72;

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
const SWEEP_Y_RIGHT = 1.42; // right hand's line, the higher one
const SWEEP_Y_LEFT = 1.2; // left hand's line, a little lower

/** How far each sweep bows away from the straight line between its ends.
 *  Right arcs UP, left arcs DOWN — opposed curves, so the two hands trace the
 *  halves of a taiji rather than two parallel wipes. Endpoints are unaffected:
 *  the bow is a sine that is zero at both ends. */
const SWEEP_ARC = 0.13;

// 五 expand: the hands part again, travelling OUTWARD from where they met.
//
// Both run on a single level, starting apart and opening further apart. The
// sweep staggered its two lines vertically to stop the hands colliding as they
// converged; here they only ever separate, so there is nothing to avoid and one
// shared line reads as a single opening rather than two parallel moves.
const EXPAND_INNER = 0.1; // where each hand starts, either side of centre
const EXPAND_OUTER = 0.5; // where it ends — wide, but short of full reach

/** Both hands travel on ONE level here, unlike the sweep's staggered lines.
 *  They start already apart and move further apart, so they never approach each
 *  other — the vertical stagger that kept them from crossing has no work to do,
 *  and a single line reads as one opening gesture rather than two. */
const EXPAND_Y = 1.32;

const MARKER_SIZE = 0.14;

/** Radius of the dots marking each end of a rail. Small enough to read as
 *  punctuation on the line rather than as another thing to grab. */
const CAP_RADIUS = 0.011;

/**
 * How close the fingertip must be to a marker's centre to grab it (metres).
 *
 * Deliberately much larger than the 0.14m sphere it surrounds — this is a
 * trigger volume, not the object: ~4.6x the marker's 0.07m radius. Hand
 * tracking is noisy, the markers sit at arm's length, and a miss that silently
 * does nothing is far more frustrating than an early catch. The player should
 * be able to reach toward a marker and have it respond, rather than having to
 * land on it.
 *
 * Cross-talk is not a concern even though the two trigger volumes now OVERLAP
 * substantially (0.32 each against rails 0.52 apart): each hand only ever
 * drives its own marker, so a left hand inside the right marker's radius does
 * nothing.
 *
 * Kept clear of RELEASE_RADIUS (0.44) by a wide margin — that gap IS the
 * hysteresis, and closing it would bring back the catch/drop chatter.
 */
const TOUCH_RADIUS = 0.32;

/** Once held, the finger may stray this far before the marker is released.
 *  Larger than TOUCH_RADIUS so brief jitter doesn't drop it mid-gesture. */
const RELEASE_RADIUS = 0.44;

// Fingertip, not wrist: the gesture is touching a specific object, and the
// wrist sits ~15cm behind where the player believes their hand is — enough to
// make contact feel wrong.
const JOINT: XRHandJoint = "index-finger-tip";

/** Smoothing on reported hand speed, 0..1 — weight given to the newest sample.
 *  Tracking drops frames often enough that raw per-frame velocity spikes
 *  wildly; averaging over ~3 frames removes that without noticeable lag. */
const SPEED_SMOOTHING = 0.35;

// ------------------------------------------------------------
// One way only, and not in a hurry
// ------------------------------------------------------------
// Two rules, and they are designed as a pair.
//
// RATCHET: the marker never travels back toward `from`. A gesture is a
// commitment; nothing the player has drawn out of the world retreats. This is
// also what lets the completion test stay simple — progress is monotonic, so it
// cannot drift back under GESTURE_COMPLETE_AT once reached.
//
// A hand moving backward is not ignored, though: the reference point follows it
// back. Otherwise the fingertip would walk away from a marker that refuses to
// move and, within one stroke, exceed RELEASE_RADIUS and drop it. Following the
// hand back means the player can PUMP — push, return, push again — which is how
// a 0.30m rail is carried by a repeating circular motion rather than one pull.
//
// SPEED: snatching at a marker lets it go. On its own that would be a cruel
// rule, because the player loses their work and is told nothing; combined with
// the ratchet it costs them only a pause, since the progress stays. The "slowly"
// mark explains why. The three parts only work together — removing any one of
// them makes the other two worse than useless.

/**
 * Speed (m/s) at which the MARKER may be carried along its rail before it is
 * let go. Not the hand's speed — see the test at the end of drive() for why
 * that distinction is the whole thing.
 *
 * A relaxed sweep of the 0.30m rail advances at ~0.15 m/s. At 0.45 this allowed
 * the whole rail to be crossed in 0.67s, which still read as hurried; 0.25 puts
 * the floor at roughly 1.2s per sweep. The margin over a relaxed sweep is now
 * modest — under ~1.7x — so if ordinary movement starts tripping it, this is
 * the number that has been taken too far, not DEACTIVATE_DWELL.
 */
const DEACTIVATE_SPEED = 0.25;

/**
 * Fingertip speed (m/s) at or below which a marker may be PICKED UP.
 *
 * This was 0.2 while the lockout still read HAND speed: back then a reach could
 * catch a marker and be punished for the deceleration that followed, so the
 * grab had to wait for a hand that had completely settled. That is no longer
 * how the penalty works — it now measures how fast the MARKER is being carried,
 * and merely reaching toward one advances nothing — so the strict gate had
 * stopped buying anything and was only making the marker feel unreachable. A
 * reach across arm's length runs 0.3–0.8 m/s, which 0.2 refused outright: the
 * player had to push their hand right up against the marker and stop before it
 * would respond.
 *
 * What is left for this to do is narrow but real: stop a hand that is flying
 * past on its way somewhere else from snatching a marker in passing.
 */
const GRAB_MAX_SPEED = 0.6;

/**
 * Having dropped a marker for speed, the HAND must slow below this (m/s) before
 * it may pick the marker up again.
 *
 * An absolute figure, not a fraction of DEACTIVATE_SPEED as it used to be.
 * Those two once measured the same quantity, so deriving one from the other was
 * harmless; now that DEACTIVATE_SPEED governs the MARKER's advance and this
 * governs the HAND, the link was only a trap — tightening the marker rule would
 * silently demand a nearly motionless hand before giving the marker back.
 */
const REARM_SPEED = 0.25;

/**
 * Minimum time a speed-drop lasts, regardless of how fast the hand settles.
 *
 * Slowing down cannot be the only condition. A hand DECELERATES at the end of
 * every stroke — it has to, in order to turn around — so a lockout that cleared
 * on speed alone cleared once per stroke. Since the ratchet keeps whatever the
 * stroke already advanced, that let a player pump their way through the whole
 * gesture at full speed with a flicker of "slowly" between strokes: fast was still
 * the winning strategy, and the mark was noise. The pause has to outlast the
 * turnaround for the rule to mean anything.
 */
const LOCKOUT_SECONDS = 1.2;

/** How long a lockout must have lasted before the "slowly" mark appears.
 *
 *  Was 0.35 when the mark floated in the middle of the rails: a long delay was
 *  the only thing keeping it from feeling like noise. Now that it appears ON
 *  the marker that was dropped, the link to the cause is visible and it can
 *  come almost at once — this is down to filtering a momentary slip, nothing
 *  more. Waiting longer would only break the causality again. */
const HINT_DELAY = 0.15;

/** How long the marker must keep advancing above DEACTIVATE_SPEED before it is
 *  dropped. Tracking spikes for a single frame often enough that acting on one
 *  reading would throw the marker away at random; ~3-4 frames does not. Also
 *  caps how far a fast sweep gets before it is stopped — at 0.05s it cannot
 *  steal more than a couple of centimetres. */
const DEACTIVATE_DWELL = 0.05;

/** Speed-tracking weights: attack quickly, release slowly. A snatch has to
 *  register on the frames it happens, and — more importantly — the reading must
 *  not sag back under the threshold halfway through a stroke, which is what let
 *  a fast sweep quietly resume after being dropped. */
const GRAB_SPEED_ATTACK = 0.5;
const GRAB_SPEED_RELEASE = 0.08;

/** The word shown when a marker has been dropped for speed. Change it here and
 *  nowhere else — the canvas sizes itself around whatever this says. */
const HINT_TEXT = "slowly";

/** Cap height of that word in metres. Width follows from the canvas aspect.
 *  Deliberately NOT scaled up with the hand graphic — the word reads fine at
 *  this size, and enlarging it turned a murmur into a placard. */
const HINT_HEIGHT = 0.075;

/** Seconds for the mark to fade in and out. Slow enough to read as the piece
 *  speaking rather than as an error message appearing. */
const HINT_FADE = 0.4;

/** Offset of the "slowly" mark from the marker it belongs to — just above it.
 *
 *  It used to hang at a fixed height in the middle of the two rails, and that
 *  was the mistake: the marker went dark in one place and, a moment later, a
 *  word appeared somewhere else. Two events, no visible link, and no way to
 *  tell WHICH hand was being spoken to. Sitting it on the offending marker
 *  makes the placement carry all of that without a syllable of explanation. */
// Tied to HINT_HEIGHT: the sprite is CENTRED on this point, so the word's lower
// edge sits at (this - HINT_HEIGHT/2) and must stay clear of the 0.07-radius
// marker underneath. At 0.075 tall that leaves a 0.035m gap. If the word is
// ever enlarged, raise this with it or it will sit inside the sphere.
const SLOW_HINT_OFFSET_Y = 0.14;
const SLOW_HINT_OFFSET_Z = 0.04;

/** Canvas for the word. Module-level and built once — both rails show the same
 *  mark, and rasterising it twice would be two textures saying one thing. */
let slowHintTexture: THREE.Texture | null = null;
let slowHintAspect = 1;

function getSlowHintTexture(): THREE.Texture | null {
  if (slowHintTexture) return slowHintTexture;

  const width = 512;
  const height = 160;
  const canvas = document.createElement("canvas");
  canvas.width = width;
  canvas.height = height;
  const ctx = canvas.getContext("2d");
  if (!ctx) return null;

  ctx.clearRect(0, 0, width, height);
  ctx.textAlign = "center";
  ctx.textBaseline = "middle";
  ctx.font = `italic 96px Georgia, "Times New Roman", serif`;
  // Same warm white as the seed circle and the marker halo — the piece has
  // exactly one accent colour and this is it.
  ctx.fillStyle = "#ffe9a8";
  ctx.fillText(HINT_TEXT, width / 2, height / 2 + 4);

  slowHintTexture = new THREE.CanvasTexture(canvas);
  slowHintTexture.colorSpace = THREE.SRGBColorSpace;
  slowHintAspect = width / height;
  return slowHintTexture;
}

/** Scratch, so the per-frame lockout ramp does not allocate a Color a frame. */
const scratchColour = new THREE.Color();

// ------------------------------------------------------------
// Hand graphics — "reach here and it wakes up"
// ------------------------------------------------------------
// Grabbing is automatic on proximity, which is only discoverable if the player
// thinks to reach. These say so. One per hand per gesture, shown beside an
// IDLE marker and gone the moment it is taken.
type GestureId = "lift" | "gather" | "open";

const HAND_HINT_URL: Record<GestureId, Record<Handedness, string>> = {
  lift: {
    left: "./ui/HandGestureGraphic/hand_gesture_1_lift_left_white_outline_cropped.png",
    right:
      "./ui/HandGestureGraphic/hand_gesture_1_lift_right_white_outline_cropped.png",
  },
  gather: {
    left: "./ui/HandGestureGraphic/hand_gesture_2_gather_left_white_outline_cropped.png",
    right:
      "./ui/HandGestureGraphic/hand_gesture_2_gather_right_white_outline_cropped.png",
  },
  open: {
    left: "./ui/HandGestureGraphic/hand_gesture_3_open_left_white_outline_cropped.png",
    right:
      "./ui/HandGestureGraphic/hand_gesture_3_open_right_white_outline_cropped.png",
  },
};

/** Height of the hand graphic, in metres. Width follows the image's own aspect
 *  once it has loaded, so the cropped art is never stretched.
 *
 *  1.6x the original 0.16: legible at a glance without dominating the marker it
 *  is pointing at. A straight doubling was tried and read as too big. */
const HAND_HINT_SIZE = 0.256;

/** Offset from the marker's centre, along +Z — that is, toward the player, so
 *  the graphic floats IN FRONT of the yin-yang sphere rather than beside it.
 *  Set to 0 and raise HAND_HINT_OFFSET_Y instead to put it back above. */
const HAND_HINT_OFFSET_Z = 0.1;
const HAND_HINT_OFFSET_Y = 0;

/** How long a marker must sit idle before its hand graphic appears.
 *
 *  Not instant: the player loses contact briefly all the time — at the turn of
 *  a pump stroke, through a tracking blink — and a graphic that flashed up each
 *  time would be worse than none. But this is now the ONLY answer to "my hand
 *  drifted off and the marker went dark", which wants answering promptly, so it
 *  is shorter than a pure idle timeout would be. */
const HAND_HINT_DELAY = 0.5;

/** Seconds to fade the hand graphic in. It leaves faster than it arrives —
 *  see HAND_HINT_FADE_OUT — because acknowledging a grab should feel instant. */
const HAND_HINT_FADE_IN = 0.5;
const HAND_HINT_FADE_OUT = 0.15;

/** Loaded once and shared: the same six images serve both rails. */
const handHintTextures = new Map<string, THREE.Texture>();

function getHandHintTexture(
  gesture: GestureId,
  side: Handedness,
  onReady: (texture: THREE.Texture) => void,
): void {
  const url = HAND_HINT_URL[gesture][side];
  const cached = handHintTextures.get(url);
  if (cached) {
    onReady(cached);
    return;
  }
  new THREE.TextureLoader().load(
    url,
    (texture) => {
      texture.colorSpace = THREE.SRGBColorSpace;
      handHintTextures.set(url, texture);
      onReady(texture);
    },
    undefined,
    () => console.warn(`[Rails] hand graphic failed to load: ${url}`),
  );
}

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
  /** Which of the three gestures this layout is — selects the hand graphic.
   *  The journey runs 举 lift → 合 gather → 开 open → 合 gather → 开 open, so
   *  the last two layouts are deliberate reprises and reuse their art. */
  gesture: GestureId;
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
  /** Dropped for moving too fast, and refusing to be picked up until the hand
   *  settles. Distinct from simply not-held: this one shows the "slowly" mark. */
  private lockedOut = false;
  private readonly prevTipWorld = new THREE.Vector3();
  private hasPrevTipWorld = false;
  private tipSpeed = 0;
  /** Seconds the fingertip has been continuously over DEACTIVATE_SPEED. */
  private overSpeed = 0;
  /** Seconds the current lockout has lasted. Gates both the re-arm and "slowly". */
  private lockoutTimer = 0;
  private glow = 0;
  /** The "reach here" hand graphic. Parented to the marker, so it follows
   *  whatever the player has already carried rather than sitting at the start
   *  of a rail they are halfway along. */
  private handHint: THREE.Sprite | null = null;
  private handHintOpacity = 0;
  /** The "slowly" mark for THIS marker. Also parented to it, so the word and
   *  the marker that earned it are never in two different places. */
  private slowHint: THREE.Sprite | null = null;
  private slowHintOpacity = 0;
  /** Seconds this marker has sat untouched. Gates HAND_HINT_DELAY. */
  private idleTime = 0;
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
    const gestureChanged = this.cfg?.gesture !== cfg.gesture;
    this.cfg = cfg;
    if (gestureChanged) this.applyHandHint(cfg.gesture);

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

  /** Dropped for speed and not yet re-armed. Dims the marker. */
  get isLockedOut(): boolean {
    return this.lockedOut;
  }

  /** Locked out long enough to be worth saying something about — what the
   *  "slowly" mark reads. A momentary slip is not a lesson. */
  get wantsHint(): boolean {
    return this.lockedOut && this.lockoutTimer >= HINT_DELAY;
  }

  reset(): void {
    if (this.cfg) this.marker.position.copy(this.pointAt(this.cfg, 0));
    this.release();
  }

  release(): void {
    this.held = false;
    this.prevTip = null;
    // A deliberate release clears the lockout: this is how the rails are handed
    // over between phases, and a new gesture must never open already refusing
    // to be touched.
    this.lockedOut = false;
    this.lockoutTimer = 0;
    this.overSpeed = 0;
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
    // Forget the last seen fingertip. Tracking gaps are routine at the edge of
    // the cameras' view, and measuring across one would report the whole jump
    // as a single frame's motion — an enormous phantom speed that would lock
    // the marker out the instant the hand came back.
    this.hasPrevTipWorld = false;
    this.tipSpeed = 0;
    this.overSpeed = 0;
    // The LOCKOUT SURVIVES a tracking gap. Hands leave the cameras' view at the
    // extremes of a rail — exactly where a fast sweep ends — so clearing it
    // here would make "swing hard enough to lose tracking" a way to skip the
    // pause entirely. The timer simply stops while the hand is gone.
    this.refreshColour(false);
  }

  /**
   * Point the hand graphic at the gesture now being asked for.
   *
   * The sprite is created on first use and then only re-textured, because
   * configure() runs at every phase change and rebuilding a sprite each time
   * would churn a material the renderer has already compiled.
   */
  private applyHandHint(gesture: GestureId): void {
    getHandHintTexture(gesture, this.side, (texture) => {
      if (!this.handHint) {
        this.handHint = new THREE.Sprite(
          new THREE.SpriteMaterial({
            transparent: true,
            depthWrite: false,
            opacity: 0,
          }),
        );
        this.handHint.position.set(0, HAND_HINT_OFFSET_Y, HAND_HINT_OFFSET_Z);
        this.handHint.visible = false;
        // Same always-on-top treatment drawOverSplats() gives everything else
        // under the rail root, applied here because this sprite is born inside
        // an async texture callback and so misses that sweep entirely. Without
        // it the graphic is simply inside the revealed world and invisible.
        this.handHint.renderOrder = 10_000;
        const m = this.handHint.material as THREE.SpriteMaterial;
        m.depthTest = true;
        m.depthWrite = true;
        m.depthFunc = THREE.AlwaysDepth;
        this.marker.add(this.handHint);
      }
      const material = this.handHint.material as THREE.SpriteMaterial;
      material.map = texture;
      material.needsUpdate = true;

      // Take the width from the art. These are cropped exports with no common
      // aspect, so a uniform scale would squash some of them.
      const image = texture.image as { width: number; height: number };
      const aspect = image?.height ? image.width / image.height : 1;
      this.handHint.scale.set(HAND_HINT_SIZE * aspect, HAND_HINT_SIZE, 1);
    });
  }

  /** Build the "slowly" sprite on first need, sharing the module texture. */
  private ensureSlowHint(): void {
    if (this.slowHint) return;
    const texture = getSlowHintTexture();
    if (!texture) return;

    this.slowHint = new THREE.Sprite(
      new THREE.SpriteMaterial({
        map: texture,
        transparent: true,
        depthWrite: false,
        opacity: 0,
      }),
    );
    this.slowHint.scale.set(
      HINT_HEIGHT * slowHintAspect,
      HINT_HEIGHT,
      1,
    );
    this.slowHint.position.set(0, SLOW_HINT_OFFSET_Y, SLOW_HINT_OFFSET_Z);
    this.slowHint.visible = false;
    // Always-on-top, as drawOverSplats() does for everything under the rail
    // root — this sprite is created after that sweep has run.
    this.slowHint.renderOrder = 10_000;
    const m = this.slowHint.material as THREE.SpriteMaterial;
    m.depthTest = true;
    m.depthWrite = true;
    m.depthFunc = THREE.AlwaysDepth;
    this.marker.add(this.slowHint);
  }

  /**
   * Per-frame upkeep for everything this marker says about itself.
   *
   * Driven from the system every frame rather than from drive(), because the
   * moments that matter most here — no hand anywhere near the rails, or a hand
   * serving out a lockout — are exactly when drive() is not being called.
   */
  tick(dt: number): void {
    this.ensureSlowHint();

    // While locked out, the marker brightens back toward normal in step with
    // the pause it is serving. This is the whole answer to "is it broken, or is
    // it coming back?", and it is an answer the player can SEE — no wording
    // needed, and none would fit a piece that shows rather than instructs.
    if (this.lockedOut) this.refreshColour(true);

    if (this.slowHint) {
      const wanted = this.lockedOut && this.lockoutTimer >= HINT_DELAY ? 1 : 0;
      const slowStep = HINT_FADE > 0 ? dt / HINT_FADE : 1;
      this.slowHintOpacity += THREE.MathUtils.clamp(
        wanted - this.slowHintOpacity,
        -slowStep,
        slowStep,
      );
      this.slowHint.visible = this.slowHintOpacity > 0.001;
      (this.slowHint.material as THREE.SpriteMaterial).opacity =
        this.slowHintOpacity;
    }

    if (!this.handHint) return;

    // Locked out is NOT idle. That moment belongs to "slowly"; inviting the
    // player to reach for a marker that is refusing to be picked up would
    // contradict it outright.
    const idle = !this.held && !this.lockedOut;
    this.idleTime = idle ? this.idleTime + dt : 0;

    const target = this.idleTime >= HAND_HINT_DELAY ? 1 : 0;
    const fade = target > this.handHintOpacity
      ? HAND_HINT_FADE_IN
      : HAND_HINT_FADE_OUT;
    const step = fade > 0 ? dt / fade : 1;
    this.handHintOpacity += THREE.MathUtils.clamp(
      target - this.handHintOpacity,
      -step,
      step,
    );

    this.handHint.visible = this.handHintOpacity > 0.001;
    (this.handHint.material as THREE.SpriteMaterial).opacity =
      this.handHintOpacity;
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
  drive(tipWorld: THREE.Vector3, root: THREE.Object3D, dt: number): void {
    if (!this.cfg) return;

    // Fingertip speed. This one is about the HAND, and is used only to decide
    // whether the hand is settled enough to pick a marker up, and settled
    // enough to be given it back. It deliberately does NOT decide the penalty —
    // see the advance-speed test at the bottom of this method.
    if (this.hasPrevTipWorld && dt > 0) {
      const raw = this.prevTipWorld.distanceTo(tipWorld) / dt;
      const k = raw > this.tipSpeed ? GRAB_SPEED_ATTACK : GRAB_SPEED_RELEASE;
      this.tipSpeed += (raw - this.tipSpeed) * k;
    }
    this.prevTipWorld.copy(tipWorld);
    this.hasPrevTipWorld = true;

    // Serving the pause. Both conditions, not either: the clock stops a player
    // pumping through on the natural deceleration between strokes, and the
    // speed test stops the marker coming back while the hand is still racing.
    if (this.lockedOut) {
      this.lockoutTimer += dt;
      if (
        this.lockoutTimer >= LOCKOUT_SECONDS &&
        this.tipSpeed < REARM_SPEED
      ) {
        this.lockedOut = false;
        this.lockoutTimer = 0;
      }
    }

    this.marker.getWorldPosition(this.markerWorld);
    const dist = tipWorld.distanceTo(this.markerWorld);

    // Asymmetric thresholds: harder to catch than to keep. Without hysteresis
    // the marker drops and re-grabs repeatedly as the fingertip jitters around
    // a single boundary.
    // Catching needs a settled hand (GRAB_MAX_SPEED); keeping one only needs to
    // stay under the far looser drop threshold. See GRAB_MAX_SPEED for why the
    // gap between the two matters.
    const wasHeld = this.held;
    this.held = this.lockedOut
      ? false
      : this.held
        ? dist < RELEASE_RADIUS
        : dist < TOUCH_RADIUS && this.tipSpeed <= GRAB_MAX_SPEED;
    this.refreshColour(true);

    if (!this.held) {
      this.prevTip = null; // released — next grab starts a fresh reference
      this.overSpeed = 0; // nothing is being carried, so nothing is too fast
      return;
    }

    const local = root.worldToLocal(tipWorld.clone());
    const along = local[this.cfg.axis];

    // The frame contact is made, record the reference and move NOTHING. This is
    // what stops the marker snapping: the player may have reached in anywhere,
    // and where carries no meaning — only what they do next.
    if (!wasHeld || this.prevTip === null) {
      this.prevTip = along;
      this.overSpeed = 0;
      return;
    }

    const delta = along - this.prevTip;
    this.prevTip = along;

    // RATCHET. `to` may be less than `from` — a rail running right-to-left is
    // how the right hand sweeps inward — so "forward" is whichever way this
    // rail's own travel points, not a fixed sign.
    const forward = Math.sign(this.cfg.to - this.cfg.from);
    if (delta * forward <= 0) {
      // Backward. The marker stays; the reference has already followed the hand
      // (prevTip above), so the fingertip does not walk away from a marker that
      // will not move — and the next forward push advances from here. This is
      // what makes the gesture pumpable instead of a single one-way pull.
      this.overSpeed = 0; // the marker is not advancing; speed is irrelevant
      return;
    }

    // Velocity match at 1:1 — the marker travels exactly as far as the hand
    // did, and stops the instant the hand stops. No smoothing: a lerp toward a
    // target would lag on acceleration and coast past on stop, reading as the
    // marker being dragged rather than carried.
    const lo = Math.min(this.cfg.from, this.cfg.to);
    const hi = Math.max(this.cfg.from, this.cfg.to);
    const before = this.marker.position[this.cfg.axis];
    const moved = THREE.MathUtils.clamp(before + delta, lo, hi);

    // Advance along the PRIMARY axis from the hand's motion, then re-seat onto
    // the curve. The hand drives the sweep; the arc is the rail's shape, not
    // something the player has to trace — trying to follow a bow exactly would
    // make the marker drop the moment they cut the corner.
    const t = (moved - this.cfg.from) / (this.cfg.to - this.cfg.from);
    this.marker.position.copy(this.pointAt(this.cfg, t));

    // ------------------------------------------------------------
    // Too fast — measured on the MARKER, not the hand
    // ------------------------------------------------------------
    // The rule is "do not sweep the world along too quickly", and the only
    // motion that sweeps anything is the marker's own advance. Reading hand
    // speed instead punished three things that are not that offence at all:
    // pulling away from a marker (fast, but moving nothing), arriving at the
    // end stop (fast, but clamped), and drawing back for another stroke (fast,
    // but blocked by the ratchet). Every one of those left the player with a
    // "slowly" they could not connect to anything they had done.
    //
    // Post-clamp, so an end stop reads as zero — the gesture is over there, and
    // it must not be possible to finish a gesture and be told off for it.
    const advanceSpeed = dt > 0 ? Math.abs(moved - before) / dt : 0;
    this.overSpeed =
      advanceSpeed > DEACTIVATE_SPEED ? this.overSpeed + dt : 0;

    if (this.overSpeed >= DEACTIVATE_DWELL) {
      // Let go, and stay let go. The glow is NOT cleared the way release()
      // would: if this lands during the completion flare, that flare belongs to
      // a gesture already finished and is not the player's to lose.
      this.held = false;
      this.prevTip = null;
      this.overSpeed = 0;
      this.lockedOut = true;
      this.lockoutTimer = 0;
      this.refreshColour(true);
    }
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
        // Cull back faces. Both models ship doubleSided, and drawOverSplats
        // gives everything AlwaysDepth so the markers stay readable over the
        // splat world — but that combination lets the sphere's FAR triangles
        // pass the depth test and paint over its near ones, so a solid ball
        // renders as a hollow shell showing its own interior.
        c.side = THREE.FrontSide;
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
      // Locked out: dark at the instant it is dropped, then brightening back to
      // normal across the pause it is serving. The marker IS the countdown.
      // Holding it flatly black for the full LOCKOUT_SECONDS said "broken"
      // rather than "wait", and left the player no way to know a gesture was
      // about to become possible again except by prodding at it.
      else if (this.lockedOut) {
        const t =
          LOCKOUT_SECONDS > 0
            ? Math.min(1, this.lockoutTimer / LOCKOUT_SECONDS)
            : 1;
        m.emissive
          .setHex(0x000000)
          .lerp(scratchColour.setHex(tint.emissive), t);
      } else if (tracked) m.emissive.setHex(tint.emissive);
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

  /** Latch for the no-handedness warning — see checkHandedness(). */
  private warnedHandedness = false;

  /** True while either marker has been locked out long enough to be told about.
   *  The mark itself lives on the offending Rail — this is only for callers who
   *  want to know the state. */
  get tooFast(): boolean {
    return this.rails
      ? this.rails.left.wantsHint || this.rails.right.wantsHint
      : false;
  }

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
      gesture: "lift",
      axis: "y",
      from: TRACK_BOTTOM,
      to: TRACK_TOP,
      cross: -RAIL_SPREAD,
    });
    this.rails.right.configure({
      gesture: "lift",
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
      gesture: "gather",
      axis: "x",
      from: +SWEEP_OUTER,
      to: +SWEEP_INNER,
      cross: SWEEP_Y_RIGHT,
      arc: +SWEEP_ARC, // rises, then settles back to where it began
    });
    this.rails.left.configure({
      gesture: "gather",
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
      gesture: "open",
      axis: "x",
      from: +EXPAND_INNER,
      to: +EXPAND_OUTER,
      cross: EXPAND_Y,
    });
    this.rails.left.configure({
      gesture: "open",
      axis: "x",
      from: -EXPAND_INNER,
      to: -EXPAND_OUTER,
      cross: EXPAND_Y,
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
  get anyHeld(): boolean {
    return this.rails.left.isHeld || this.rails.right.isHeld;
  }

  /** How many markers are held right now, 0..2 — drives the gesture audio. */
  get heldCount(): number {
    return (this.rails.left.isHeld ? 1 : 0) + (this.rails.right.isHeld ? 1 : 0);
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
      // Keep fading, or a "slowly" caught mid-show by a lost session would hang
      // there at full opacity over whatever comes back.
      this.rails.left.tick(delta);
      this.rails.right.tick(delta);
      return;
    }

    this.checkHandedness();

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
      this.rails[side].drive(this.tip, this.root, delta);
    }

    this.rails.left.tick(delta);
    this.rails.right.tick(delta);

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

  /**
   * Warn when an input source turns up without a handedness.
   *
   * findSource() matches "left"/"right" exactly, so a source reporting "none"
   * drives NEITHER rail: that hand is completely dead, with no marker response
   * of any kind. From inside the headset that is indistinguishable from the
   * grab radius being too small — which is a tuning problem, and would send
   * anyone debugging it to entirely the wrong constant.
   *
   * Latched, because this runs every frame. The latch CLEARS when the condition
   * goes away, so a later episode logs again rather than being swallowed by a
   * flag set once at startup.
   */
  private checkHandedness() {
    const session = this.world.session;
    if (!session) return;

    let stray = 0;
    for (const source of session.inputSources) {
      if (source.handedness !== "left" && source.handedness !== "right") {
        stray += 1;
      }
    }

    if (stray > 0 && !this.warnedHandedness) {
      this.warnedHandedness = true;
      const seen = Array.from(session.inputSources)
        .map((s) => `${s.handedness}${s.hand ? "(hand)" : "(ctrl)"}`)
        .join(", ");
      console.warn(
        `[Rails] ${stray} input source(s) with no handedness — these drive ` +
          `neither rail, so that hand will not respond at all. Sources: ${seen}`,
      );
    } else if (stray === 0 && this.warnedHandedness) {
      this.warnedHandedness = false;
      console.log("[Rails] handedness recovered on all input sources");
    }
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
