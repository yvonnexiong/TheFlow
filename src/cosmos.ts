import * as THREE from "three";
import { createSystem } from "@iwsdk/core";

// ------------------------------------------------------------
// 六 Resonance — orbital rings and the planets riding them
// ------------------------------------------------------------
// Thin glowing curves arcing across the far sky, each carrying one or more
// spheres. The player's gesture rotates them: every planet advances along its
// own orbit, counter-clockwise, by an amount set by how far the gesture has
// been carried.
//
// The story's point is that the planets are NOT being dragged. So nothing here
// tracks a hand's position — the gesture sets how far the whole system has
// turned, and each body answers on its own orbit at its own rate. Reaching the
// end of the gesture is not "moving a planet to where you put it"; it is the
// system having turned by that much.
//
// Everything sits far away and moves slowly in angular terms, which is what
// sells distance in VR: near objects swing past as you move your head, distant
// ones barely shift.

/**
 * Scales the whole system — every radius, every body, and the centre height.
 *
 * Applied to radius AND size together on purpose: that leaves each planet's
 * APPARENT size unchanged while bringing it nearer, so the sky looks the same
 * and simply sits inside the splat world rather than beyond its edges.
 *
 * Lower to pull everything closer. Note this does change the parallax — nearer
 * bodies shift more as the player moves their head, so pulled in far enough
 * they stop reading as distant.
 */
const ORBIT_SCALE = 0.3;

/** Orbit centre, before scaling.
 *
 *  Pushed FORWARD as well as up, so the system reads as a tableau standing in
 *  front of the player — the way the reference image frames it — rather than a
 *  dome overhead they have to crane up into. A little above the eye line is
 *  enough to feel celestial; directly overhead means most of it is never seen.
 */
const CENTRE_Y = 11;
const CENTRE_Z = -78;

/** How far the whole system turns across a full gesture, in revolutions.
 *
 *  Still under a full turn — a complete orbit reads as spinning a dial rather
 *  than as a vast thing answering — but 0.16 was too subtle to register as a
 *  response at all, which defeats the scene. Inner orbits move at 1.6x this,
 *  so they carry visibly further than the outer ones. */
const TURNS_PER_GESTURE = 0.3;

/** Ring line colour and weight. Warm white to match the seed circle and the
 *  marker flare, so the whole piece shares one light. */
const RING_COLOR = 0xffe9a8;
const RING_OPACITY = 0.34;
const RING_SEGMENTS = 160;

/**
 * Planet colours, cycled across the bodies so no two neighbours match.
 *
 * Plain spheres rather than models. The glb planets were disproportionately
 * expensive for what they added at this distance — one was 50k triangles for a
 * featureless ball, another was 16 meshes and 13 textures, so every instance
 * cost 16 draw calls. Seventeen procedural spheres are a rounding error beside
 * three resident splat worlds, and at 15-60m away the silhouette and the size
 * relationships carry the image, not surface detail.
 *
 * Warm through cool, all desaturated: they read as bodies catching a distant
 * light rather than as coloured balls.
 */
const PLANET_COLORS = [
  0xf6efdf, 0xd9c7a8, 0xc9d2dd, 0xe8d3b8, 0xbfc8c4, 0xe3d9e6, 0xd2bfa6,
];

/** Emissive lift. The scene carries only an ambient light, so a purely diffuse
 *  sphere renders as a flat disc — this gives it enough of its own light to
 *  read as a body with a near and a far side. */
const PLANET_EMISSIVE_SCALE = 0.34;

/** Sphere tessellation. Modest on purpose: at these distances the extra rings
 *  of a high-poly sphere are invisible, and there are seventeen of them. */
const PLANET_SEGMENTS = 20;

/**
 * Optional planet textures, cycled across the bodies alongside the colours.
 *
 * SphereGeometry unwraps EQUIRECTANGULAR (lat-long): u runs around the equator,
 * v from south pole to north. So these want 2:1 images — 2048x1024 is ample at
 * 15-60m. Expect visible pinching at the poles; that is inherent to the
 * projection, not a fault in the image.
 *
 * Drop files into public/textures/planets/ and list them here. An empty list,
 * or any file that fails to load, simply leaves that body its flat colour — the
 * scene never depends on a texture existing.
 *
 * Keep them modest. Seventeen 2K colour maps is fine; seventeen with normal and
 * specular maps each is what made the glb planets untenable.
 */
const PLANET_TEXTURES: string[] = [
  "./textures/planets/cyan.jpg",
  "./textures/planets/planet1.jpg",
  "./textures/planets/red.jpg",
  "./textures/planets/planet2.jpg",
  "./textures/planets/planet3.jpg",
  "./textures/planets/planet4.jpg",
  "./textures/planets/planet5.jpg",
  "./textures/planets/planet6.jpg",
];

/**
 * The orbits, authored rather than randomised.
 *
 * Deliberately hand-listed: a random layout re-rolls on every reload, so it can
 * never be judged or tuned — you would be looking at a different sky each time.
 * These give a spread of scales and tilts that reads as a system rather than a
 * set of concentric circles.
 *
 * radius   metres from centre
 * tilt     radians the ring leans out of horizontal
 * yaw      radians the ring is turned about Y, so tilts do not all lean the
 *          same way
 * rate     multiplier on the shared turn; inner orbits move faster, as they
 *          would
 * bodies   [angle at rest (radians), radius in metres] per planet
 */
const ORBITS: {
  radius: number;
  tilt: number;
  yaw: number;
  rate: number;
  bodies: [number, number][];
}[] = [
  {
    radius: 34,
    tilt: 0.42,
    yaw: 0.0,
    rate: 1.6,
    bodies: [
      [0.6, 1.5],
      [3.4, 0.8],
    ],
  },
  {
    radius: 48,
    tilt: 0.18,
    yaw: 1.1,
    rate: 1.25,
    bodies: [
      [2.4, 2.6],
      [4.9, 1.1],
      [0.9, 1.8],
    ],
  },
  {
    radius: 61,
    tilt: 0.55,
    yaw: 2.3,
    rate: 1.0,
    bodies: [
      [1.2, 3.4],
      [4.0, 1.2],
    ],
  },
  {
    radius: 76,
    tilt: 0.3,
    yaw: 0.7,
    rate: 0.85,
    bodies: [
      [3.8, 2.0],
      [0.2, 1.4],
      [5.4, 3.6],
    ],
  },
  {
    radius: 92,
    tilt: 0.62,
    yaw: 3.0,
    rate: 0.7,
    bodies: [
      [5.4, 4.6],
      [2.2, 1.5],
    ],
  },
  {
    radius: 110,
    tilt: 0.24,
    yaw: 1.8,
    rate: 0.58,
    bodies: [
      [2.9, 3.0],
      [0.5, 1.3],
      [4.6, 5.2],
    ],
  },
  {
    radius: 132,
    tilt: 0.48,
    yaw: 4.2,
    rate: 0.45,
    bodies: [
      [4.4, 5.8],
      [1.7, 2.2],
    ],
  },
];

type Body = {
  mesh: THREE.Object3D;
  /** Angle on its ring when the gesture is at rest. */
  restAngle: number;
  /** Basis of the ring's plane, so a body's position is centre + R(cos·u +
   *  sin·v). Precomputed once — recomputing a rotation per body per frame is
   *  the kind of thing that quietly costs frames. */
  u: THREE.Vector3;
  v: THREE.Vector3;
  radius: number;
  rate: number;
};

export class CosmosSystem extends createSystem({}) {
  private root!: THREE.Group;
  private bodies: Body[] = [];
  private progress = 0;
  /** 1 = fully present, 0 = gone. The rings and planets fade together. */
  private fade = 1;
  private readonly ringMaterials: THREE.LineBasicMaterial[] = [];
  private readonly bodyMaterials: THREE.MeshStandardMaterial[] = [];
  private readonly textures = new Map<string, THREE.Texture>();

  /** Show or hide the whole system. */
  setVisible(visible: boolean): void {
    if (!this.root) {
      console.warn("[Cosmos] setVisible before init — nothing built yet");
      return;
    }
    this.root.visible = visible;
  }

  /**
   * Fade the whole system, 1 to 0.
   *
   * Material opacity rather than hiding: the stars are meant to dim and the
   * space to dissolve, which is continuous, not a cut. At zero the group is
   * switched off outright so nothing is still being submitted to draw.
   */
  setFade(value: number): void {
    this.fade = Math.min(1, Math.max(0, value));
    for (const m of this.ringMaterials) m.opacity = RING_OPACITY * this.fade;
    for (const m of this.bodyMaterials) m.opacity = this.fade;
    if (this.root) this.root.visible = this.fade > 0.001;
  }

  /**
   * How far the system has turned, 0..1 across one gesture.
   *
   * Applied immediately rather than eased: the gesture is already a physical
   * movement with its own acceleration, and smoothing on top of it reads as the
   * planets lagging behind the hand — which is exactly the "being dragged"
   * feeling this scene is trying not to have.
   */
  setProgress(value: number): void {
    this.progress = Math.min(1, Math.max(0, value));
    this.apply();
  }

  init() {
    console.log("[Cosmos] init");
    this.root = new THREE.Group();
    this.root.position.set(0, CENTRE_Y * ORBIT_SCALE, CENTRE_Z * ORBIT_SCALE);
    this.root.visible = false;
    this.player.add(this.root);

    for (const orbit of ORBITS) {
      // Ring plane basis. Start in the XZ plane, then lean by `tilt` and turn
      // by `yaw`, so no two rings lie the same way.
      const basis = new THREE.Matrix4().makeRotationY(orbit.yaw);
      basis.multiply(new THREE.Matrix4().makeRotationX(orbit.tilt));

      const u = new THREE.Vector3(1, 0, 0).applyMatrix4(basis);
      // -Z, not +Z: with u = +X this makes increasing angle run
      // COUNTER-CLOCKWISE seen from above, which is the direction asked for.
      const v = new THREE.Vector3(0, 0, -1).applyMatrix4(basis);

      const radius = orbit.radius * ORBIT_SCALE;
      const ring = this.buildRing(radius, u, v);
      this.ringMaterials.push(ring.material as THREE.LineBasicMaterial);
      this.root.add(ring);

      for (const [restAngle, size] of orbit.bodies) {
        const color = PLANET_COLORS[this.bodies.length % PLANET_COLORS.length];
        const emissive = new THREE.Color(color).multiplyScalar(
          PLANET_EMISSIVE_SCALE,
        );

        const map = this.textureFor(this.bodies.length);

        const mesh = new THREE.Mesh(
          new THREE.SphereGeometry(
            size * ORBIT_SCALE,
            PLANET_SEGMENTS,
            PLANET_SEGMENTS / 2,
          ),
          new THREE.MeshStandardMaterial({
            // A texture is multiplied by `color`, so a tinted body would stain
            // its map. Textured planets go white and let the image speak;
            // untextured ones keep the palette.
            color: map ? 0xffffff : color,
            emissive: map ? new THREE.Color(0x2a2a2a) : emissive,
            map,
            roughness: 0.85,
            metalness: 0.0,
            // Opaque in practice at fade 1, but declared transparent so the
            // system can dissolve at the end.
            transparent: true,
            opacity: 1,
          }),
        );
        this.root.add(mesh);
        this.bodyMaterials.push(mesh.material as THREE.MeshStandardMaterial);
        this.bodies.push({
          mesh,
          restAngle,
          u: u.clone(),
          v: v.clone(),
          radius,
          rate: orbit.rate,
        });
      }
    }

    this.apply(); // seat everything at rest before the first frame
    const near = Math.min(...ORBITS.map((o) => o.radius)) * ORBIT_SCALE;
    const far = Math.max(...ORBITS.map((o) => o.radius)) * ORBIT_SCALE;
    console.log(
      `[Cosmos] built ${ORBITS.length} orbits, ${this.bodies.length} bodies, ` +
        `${near.toFixed(0)}m to ${far.toFixed(0)}m out, centre ` +
        `${(CENTRE_Y * ORBIT_SCALE).toFixed(0)}m up, ` +
        `${Math.abs(CENTRE_Z * ORBIT_SCALE).toFixed(0)}m ahead`,
    );
  }

  /**
   * The texture for body `index`, or null if none are configured.
   *
   * Loaded once per file and shared between the bodies that use it — a texture
   * has no per-instance state, so seventeen planets across three maps cost
   * three uploads.
   */
  private textureFor(index: number): THREE.Texture | null {
    if (!PLANET_TEXTURES.length) return null;

    const url = PLANET_TEXTURES[index % PLANET_TEXTURES.length];
    const cached = this.textures.get(url);
    if (cached) return cached;

    const tex = new THREE.TextureLoader().load(
      url,
      undefined,
      undefined,
      () => console.warn(`[Cosmos] texture ${url} failed — body stays flat`),
    );
    // Without this, colours render washed out.
    tex.colorSpace = THREE.SRGBColorSpace;
    // Equirectangular maps wrap seamlessly around the equator but not over the
    // poles, so repeat horizontally and clamp vertically.
    tex.wrapS = THREE.RepeatWrapping;
    tex.wrapT = THREE.ClampToEdgeWrapping;
    tex.anisotropy = 4;

    this.textures.set(url, tex);
    return tex;
  }

  /** A ring as a closed line, sampled around the orbit's own plane. */
  private buildRing(
    radius: number,
    u: THREE.Vector3,
    v: THREE.Vector3,
  ): THREE.LineLoop {
    const points: THREE.Vector3[] = [];
    for (let i = 0; i < RING_SEGMENTS; i++) {
      const a = (i / RING_SEGMENTS) * Math.PI * 2;
      points.push(
        new THREE.Vector3()
          .addScaledVector(u, Math.cos(a) * radius)
          .addScaledVector(v, Math.sin(a) * radius),
      );
    }

    return new THREE.LineLoop(
      new THREE.BufferGeometry().setFromPoints(points),
      new THREE.LineBasicMaterial({
        color: RING_COLOR,
        transparent: true,
        opacity: RING_OPACITY,
        depthWrite: false,
      }),
    );
  }

  /** Place every body for the current progress. */
  private apply() {
    const turn = this.progress * Math.PI * 2 * TURNS_PER_GESTURE;
    for (const b of this.bodies) {
      const a = b.restAngle + turn * b.rate;
      b.mesh.position
        .set(0, 0, 0)
        .addScaledVector(b.u, Math.cos(a) * b.radius)
        .addScaledVector(b.v, Math.sin(a) * b.radius);
    }
  }
}
