import * as THREE from "three";
import { createSystem } from "@iwsdk/core";

// ------------------------------------------------------------
// Hand-driven cube on a linear rail
// ------------------------------------------------------------
// A cube rides a VERTICAL track within arm's reach. The player TOUCHES it and
// carries it upward; the cube only moves while a hand is actually on it.
//
// railProgress runs 0 at the BOTTOM to 1 at the TOP, so the world is drawn
// upward out of nothing — the gesture is lifting, not sweeping.
//
// Touching ENGAGES the cube; it does not place it. On contact the cube stays
// exactly where it is — at 0, if it hasn't been moved — and from then on
// follows the hand's VELOCITY: each frame it moves by however far the fingertip
// moved. Grabbing therefore never jumps the reveal, however high the player
// happened to reach to make contact.

const TRACK_Z = -0.42; // within arm's reach, so the cube can be touched
const TRACK_X = 0.0; // centred laterally

// Vertical travel: TRACK_BOTTOM (progress 0) up to TRACK_TOP (progress 1).
// Chest to eye level, 0.55m of travel — a relaxed lift rather than an overhead
// stretch, so the top is comfortable to hold while looking at the result.
const TRACK_BOTTOM = 1.0;
const TRACK_TOP = 1.55;
const TRACK_HEIGHT = TRACK_TOP - TRACK_BOTTOM;

const CUBE_SIZE = 0.14;

/** How close the fingertip must be to the cube's centre to grab it (metres).
 *  Generous relative to the 0.14m cube — hand tracking is noisy, and a miss
 *  that silently does nothing is more frustrating than an early catch. */
const TOUCH_RADIUS = 0.14;

/** Once held, the finger may stray this far before the cube is released.
 *  Larger than TOUCH_RADIUS so brief jitter doesn't drop it mid-lift. */
const RELEASE_RADIUS = 0.26;

// Fingertip, not wrist: the gesture is now touching a specific object, and the
// wrist sits ~15cm behind where the player believes their hand is — enough to
// make contact feel wrong. Fingertip joints do wobble a few mm at rest, which
// shows up as slight cube shimmer while held; that is the price of contact
// landing where it looks like it should.
const JOINT: XRHandJoint = "index-finger-tip";

export class HandFollowCubeSystem extends createSystem({}) {
  private root!: THREE.Group;
  private cube!: THREE.Mesh;
  private cubeMaterial!: THREE.MeshStandardMaterial;

  /** True while a fingertip is holding the cube. */
  private held = false;
  /** Fingertip height (rail-local) last frame while held; null when not held.
   *  Movement is measured against this, never assigned from it. */
  private prevTipY: number | null = null;
  // Scratch, reused per frame so the update loop allocates nothing.
  private readonly cubeWorld = new THREE.Vector3();

  /**
   * Cube position along the rail, normalized to 0..1
   * (0 = bottom, 1 = top). Drives the splat-world reveal.
   *
   * Returns 0 before init() has built the cube — systems may read this during
   * their own init/update ordering before ours has run.
   */
  get railProgress(): number {
    if (!this.cube) return 0;
    return (this.cube.position.y - TRACK_BOTTOM) / TRACK_HEIGHT;
  }

  /**
   * Park the cube back at the bottom (railProgress = 0) and drop the tracking
   * anchor, so the next phase starts its 0..1 gesture range fresh without a
   * jump. Used by the Director between phases.
   */
  reset(): void {
    if (this.cube) this.cube.position.y = TRACK_BOTTOM;
    this.held = false;
  }

  init() {
    // Parent everything to the XR origin (this.player). Poses we read from
    // frame.getJointPose are expressed in the XR reference space, and the
    // XROrigin group *is* that space in scene terms — so a reference-space
    // position can be used directly as a local position here. Adding to
    // world.scene instead would drift as soon as the user locomotes.
    this.root = new THREE.Group();
    this.player.add(this.root);

    // --- the rail ---
    const rail = new THREE.Mesh(
      new THREE.BoxGeometry(0.004, TRACK_HEIGHT, 0.004),
      new THREE.MeshBasicMaterial({ color: 0x334455 }),
    );
    rail.position.set(TRACK_X, (TRACK_BOTTOM + TRACK_TOP) / 2, TRACK_Z);
    this.root.add(rail);

    // --- end stops, so the travel limits are visible ---
    for (const y of [TRACK_BOTTOM, TRACK_TOP]) {
      const cap = new THREE.Mesh(
        new THREE.BoxGeometry(0.06, 0.006, 0.006),
        new THREE.MeshBasicMaterial({ color: 0x334455 }),
      );
      cap.position.set(TRACK_X, y, TRACK_Z);
      this.root.add(cap);
    }

    // --- the cube ---
    this.cubeMaterial = new THREE.MeshStandardMaterial({
      color: 0x00aaff,
      emissive: 0x003355,
      roughness: 0.35,
      metalness: 0.1,
    });
    this.cube = new THREE.Mesh(
      new THREE.BoxGeometry(CUBE_SIZE, CUBE_SIZE, CUBE_SIZE),
      this.cubeMaterial,
    );
    // Start at the BOTTOM, not the middle. railProgress feeds the reveal, and
    // a centred cube would mean the world is half-revealed before the player
    // has touched anything.
    this.cube.position.set(TRACK_X, TRACK_BOTTOM, TRACK_Z);
    this.root.add(this.cube);

    this.drawOverSplats(this.root);
  }

  /**
   * Draw the rail and cube on top of the splat world.
   *
   * The rail is a control, not scenery — it has to stay readable wherever the
   * player has revealed the world to. Splats sit at renderOrder -10 and the
   * revealed world routinely occupies the space between the eye and TRACK_Z,
   * so by ordinary depth testing the cube is simply *inside* the world and gets
   * covered. Same trick uiPanel.ts uses for panels: AlwaysDepth passes the
   * depth test unconditionally, and a high renderOrder draws it last.
   * depthWrite stays true so the cube still occludes itself correctly.
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

  /** Show/hide the whole rail + cube. The Director hides it during 一 breath
   *  and 二 disc so the void stays uncluttered, and shows it for 三 reveal. */
  setVisible(visible: boolean): void {
    if (this.root) this.root.visible = visible;
  }

  /**
   * Re-seat the rail an arm's length in front of the player's head, facing them.
   *
   * The rail is parented to the XR origin, but the player physically WALKS
   * during scene 0 — stepping into the seed circle moves them ~1.2m forward
   * while the origin stays put. A rail fixed at z=-1.5 in origin space would
   * then be inches from their face, or behind them. So scene 2 places it
   * relative to wherever the player actually ended up.
   *
   * Only yaw is taken from the head: the rail stays upright and at fixed
   * heights, so looking up or down doesn't tilt it.
   */
  placeInFrontOf(camera: THREE.Camera): void {
    if (!this.root?.parent) return;

    const headWorld = new THREE.Vector3();
    camera.getWorldPosition(headWorld);
    const headLocal = this.root.parent.worldToLocal(headWorld.clone());

    const dir = new THREE.Vector3();
    camera.getWorldDirection(dir);

    // Straight up/down gives a degenerate horizontal direction — keep the
    // rail's current yaw rather than snapping it somewhere arbitrary.
    if (Math.hypot(dir.x, dir.z) > 1e-4) {
      // A group rotated by θ about Y sends its local -Z to (-sinθ, 0, -cosθ);
      // matching that to the look direction gives θ = atan2(-x, -z).
      this.root.rotation.y = Math.atan2(-dir.x, -dir.z);
    }

    // Keep y at 0 so TRACK_BOTTOM/TRACK_TOP still set heights in player space.
    this.root.position.set(headLocal.x, 0, headLocal.z);
    this.held = false; // a hold cannot survive the rail moving
    this.prevTipY = null;
  }

  update(_delta: number, _time: number) {
    const frame = this.xrFrame;
    const refSpace = this.xrManager.getReferenceSpace();

    if (!frame || !refSpace) {
      this.held = false;
      this.prevTipY = null;
      this.setTracked(false);
      return;
    }

    const tip = this.readHandTip(frame, refSpace);
    if (!tip) {
      this.held = false;
      this.prevTipY = null;
      this.setTracked(false);
      return;
    }
    this.setTracked(true);

    // Hand poses arrive in reference space, which IS player space in scene
    // terms; the rail sits under a moved/rotated root, so compare in world.
    this.player.localToWorld(tip);
    this.cube.getWorldPosition(this.cubeWorld);
    const dist = tip.distanceTo(this.cubeWorld);

    // Asymmetric thresholds: harder to catch than to keep. Without hysteresis
    // the cube drops and re-grabs repeatedly as the fingertip jitters around a
    // single boundary.
    const wasHeld = this.held;
    this.held = this.held ? dist < RELEASE_RADIUS : dist < TOUCH_RADIUS;
    this.setHeld(this.held);

    if (!this.held) {
      this.prevTipY = null; // released — next grab starts a fresh reference
      return;
    }

    this.root.worldToLocal(tip);

    // The frame contact is made, record the reference height and move NOTHING.
    // This is what stops the cube snapping: the player may have reached in at
    // any height, and that height carries no meaning — only what they do next.
    if (!wasHeld || this.prevTipY === null) {
      this.prevTipY = tip.y;
      return;
    }

    const deltaY = tip.y - this.prevTipY;
    this.prevTipY = tip.y;

    // Velocity match at 1:1 — the cube travels exactly as far as the hand did,
    // and stops the instant the hand stops. No smoothing: a lerp toward a
    // target would lag on acceleration and coast past on stop, which reads as
    // the cube being dragged rather than carried.
    this.cube.position.y = THREE.MathUtils.clamp(
      this.cube.position.y + deltaY,
      TRACK_BOTTOM,
      TRACK_TOP,
    );
  }

  private readHandTip(
    frame: XRFrame,
    refSpace: XRReferenceSpace,
  ): THREE.Vector3 | null {
    const session = this.world.session;
    if (!session) return null;

    let fallback: THREE.Vector3 | null = null;

    for (const source of session.inputSources) {
      let pos: THREE.Vector3 | null = null;

      // getJointPose is optional — absent on runtimes without hand input.
      if (source.hand && frame.getJointPose) {
        const joint = source.hand.get(JOINT);
        // Null whenever the joint isn't currently tracked — routinely, at the
        // edge of the headset's camera FOV.
        const pose = joint ? frame.getJointPose(joint, refSpace) : null;
        if (pose) {
          const t = pose.transform.position;
          pos = new THREE.Vector3(t.x, t.y, t.z);
        }
      }

      // Controller grip, or a hand whose joints we couldn't read.
      if (!pos && source.gripSpace) {
        const pose = frame.getPose(source.gripSpace, refSpace);
        if (pose) {
          const t = pose.transform.position;
          pos = new THREE.Vector3(t.x, t.y, t.z);
        }
      }

      if (!pos) continue;
      // Either hand may grab it; prefer the right when both are tracked.
      if (source.handedness === "right") return pos;
      fallback = pos;
    }

    return fallback;
  }

  private setTracked(tracked: boolean) {
    if (this.held) return; // held colour wins
    this.cubeMaterial.color.setHex(tracked ? 0x00aaff : 0x555555);
    this.cubeMaterial.emissive.setHex(tracked ? 0x003355 : 0x000000);
  }

  /** Brighten while held, so contact is unmistakable — the player needs to know
   *  they have it before they start lifting. */
  private setHeld(held: boolean) {
    this.cubeMaterial.color.setHex(held ? 0xffffff : 0x00aaff);
    this.cubeMaterial.emissive.setHex(held ? 0x66ccff : 0x003355);
  }
}
