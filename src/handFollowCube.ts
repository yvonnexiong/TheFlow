import * as THREE from "three";
import { createSystem } from "@iwsdk/core";

// ------------------------------------------------------------
// Hand-driven cube on a linear rail
// ------------------------------------------------------------
// A cube sits on a horizontal track in front of the user. Moving a tracked
// hand (or controller) left/right moves the cube left/right at exactly the
// same speed. Hand stops -> cube stops. Only the X axis is mapped; the cube's
// height and depth are fixed, so it can only slide along the rail.
//
// The mapping is RELATIVE, not absolute: each frame we add the hand's X
// *delta* to the cube, rather than assigning the hand's X to the cube. Both
// reproduce hand speed exactly, but absolute mapping would teleport the cube
// to the hand's X on the first tracked frame. Relative mapping leaves the cube
// where it is and nudges it by however much the hand moved.

const TRACK_Y = 1.3; // eye-ish height (world.camera sits at y=1.5)
const TRACK_Z = -1.5; // 1.5m in front of the user
const TRACK_HALF_WIDTH = 1.0; // cube travels within +/- 1m
const CUBE_SIZE = 0.14;

// Hand travel -> rail travel multiplier.
//
// At 1.0 the cube exactly matches the hand's distance and speed, but the rail
// is 2m wide and a comfortable lateral reach is only ~0.7m — so the cube can
// never pass the middle. At 3.0 a ~0.7m sweep covers the full rail.
//
// This deliberately trades away exact speed-matching for reachability. Raise
// it for less arm movement per transition, lower it for finer control.
const HAND_TO_RAIL_GAIN = 3.0;

// Wrist is noticeably steadier than a fingertip — fingertip joints wobble a
// few mm even when the hand is still, and that shows up as cube shimmer.
const JOINT: XRHandJoint = "wrist";

export class HandFollowCubeSystem extends createSystem({}) {
  private cube!: THREE.Mesh;
  private cubeMaterial!: THREE.MeshStandardMaterial;

  // Hand X from the previous frame, in reference space. null means "we have no
  // usable previous sample" — either we haven't started, or tracking dropped.
  // Resetting to null is what prevents a stale-position jump when the hand
  // leaves the camera's view mid-motion and comes back somewhere else.
  private prevHandX: number | null = null;

  /**
   * Cube position along the rail, normalized to 0..1
   * (0 = far left, 1 = far right). Drives the splat-world morph.
   *
   * Returns 0 before init() has built the cube — systems may read this during
   * their own init/update ordering before ours has run.
   */
  get railProgress(): number {
    if (!this.cube) return 0;
    return (
      (this.cube.position.x + TRACK_HALF_WIDTH) / (TRACK_HALF_WIDTH * 2)
    );
  }

  init() {
    // Parent everything to the XR origin (this.player). Poses we read from
    // frame.getJointPose are expressed in the XR reference space, and the
    // XROrigin group *is* that space in scene terms — so a reference-space
    // position can be used directly as a local position here. Adding to
    // world.scene instead would drift as soon as the user locomotes.
    const root = new THREE.Group();
    this.player.add(root);

    // --- the rail ---
    const rail = new THREE.Mesh(
      new THREE.BoxGeometry(TRACK_HALF_WIDTH * 2, 0.004, 0.004),
      new THREE.MeshBasicMaterial({ color: 0x334455 }),
    );
    rail.position.set(0, TRACK_Y, TRACK_Z);
    root.add(rail);

    // --- end stops, so the travel limits are visible ---
    for (const x of [-TRACK_HALF_WIDTH, TRACK_HALF_WIDTH]) {
      const cap = new THREE.Mesh(
        new THREE.BoxGeometry(0.006, 0.06, 0.006),
        new THREE.MeshBasicMaterial({ color: 0x334455 }),
      );
      cap.position.set(x, TRACK_Y, TRACK_Z);
      root.add(cap);
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
    // Start at the LEFT end, not centre. railProgress feeds the splat morph,
    // and a centred cube would mean phase 0.5 — both worlds half-dispersed
    // before the user has touched anything, which just looks like fog.
    this.cube.position.set(-TRACK_HALF_WIDTH, TRACK_Y, TRACK_Z);
    root.add(this.cube);
  }

  update(_delta: number, _time: number) {
    const frame = this.xrFrame;
    const refSpace = this.xrManager.getReferenceSpace();

    // Not in XR yet, or no reference space — park the tracking state so the
    // next tracked frame starts fresh instead of differencing against a
    // position from before the session.
    if (!frame || !refSpace) {
      this.setTracked(false);
      this.prevHandX = null;
      return;
    }

    const handX = this.readHandX(frame, refSpace);
    if (handX === null) {
      this.setTracked(false);
      this.prevHandX = null;
      return;
    }
    this.setTracked(true);

    // First tracked frame: record the anchor and move nothing. Without this the
    // very first delta would be measured against null and the cube would jump.
    if (this.prevHandX === null) {
      this.prevHandX = handX;
      return;
    }

    const deltaX = handX - this.prevHandX;
    this.prevHandX = handX;

    // Scaled 1:1 — the cube moves in lockstep with the hand and stops the
    // instant the hand stops, but covers HAND_TO_RAIL_GAIN times the distance.
    // Still no smoothing: a lerp toward a target would lag on acceleration and
    // coast past on stop, which would feel disconnected from the hand.
    this.cube.position.x = THREE.MathUtils.clamp(
      this.cube.position.x + deltaX * HAND_TO_RAIL_GAIN,
      -TRACK_HALF_WIDTH,
      TRACK_HALF_WIDTH,
    );
  }

  /**
   * X position of the driving hand in reference space, or null if nothing is
   * tracked this frame.
   *
   * Prefers an articulated hand; falls back to a controller grip so this is
   * testable with controllers (and under the IWER desktop emulator). Right hand
   * wins over left so the two hands can't fight over the cube.
   */
  private readHandX(frame: XRFrame, refSpace: XRReferenceSpace): number | null {
    const session = this.world.session;
    if (!session) return null;

    let fallbackX: number | null = null;

    for (const source of session.inputSources) {
      let x: number | null = null;

      // getJointPose is optional — absent on runtimes without hand input.
      if (source.hand && frame.getJointPose) {
        const joint = source.hand.get(JOINT);
        // Returns null whenever the joint isn't currently tracked —
        // routinely, at the edge of the headset's camera FOV.
        const pose = joint ? frame.getJointPose(joint, refSpace) : null;
        if (pose) x = pose.transform.position.x;
      }

      // Controller grip, or a hand whose joints we couldn't read.
      if (x === null && source.gripSpace) {
        const pose = frame.getPose(source.gripSpace, refSpace);
        if (pose) x = pose.transform.position.x;
      }

      if (x === null) continue;
      if (source.handedness === "right") return x;
      fallbackX = x;
    }

    return fallbackX;
  }

  /** Tint the cube so it's obvious on-device whether tracking is live. */
  private setTracked(tracked: boolean) {
    this.cubeMaterial.color.setHex(tracked ? 0x00aaff : 0x555555);
    this.cubeMaterial.emissive.setHex(tracked ? 0x003355 : 0x000000);
  }
}
