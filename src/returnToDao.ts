import * as THREE from "three";
import { createSystem } from "@iwsdk/core";

// ------------------------------------------------------------
// Scene 5 — Return to Dao
// ------------------------------------------------------------
// Everything the player has witnessed comes apart into drifting particles, the
// particles gather in the empty space in front of them, and settle into 道.
//
// The character is NOT an image or a model. It is rasterised at runtime into a
// canvas, sampled, and each dark pixel becomes a target position for one
// particle. That matters for two reasons: the form is made of the same drifting
// stuff as everything that dissolved into it — nothing is swapped in — and
// there is no asset to go missing or to have been authored at the wrong scale.
//
// Particles do not travel in step. Each carries its own delay and its own
// wandering path, so the form assembles the way ink settles in water rather
// than the way a loading bar fills.

/** How many particles the character is made of. Points are cheap — this is a
 *  single draw call — but the sampler needs enough dark pixels to choose from,
 *  so raising it much past the stroke area gives duplicates rather than
 *  detail. */
const PARTICLE_COUNT = 5200;

/** Canvas the character is rasterised into. Larger samples finer strokes; this
 *  is comfortably more than PARTICLE_COUNT needs. */
const RASTER_SIZE = 320;

/** The character, and how it is drawn. A generic family on purpose: the exact
 *  typeface matters far less than the silhouette at this scale, and naming a
 *  font that is not installed silently yields a blank canvas and no form. */
const GLYPH = "道";
const GLYPH_FONT = `bold ${Math.round(RASTER_SIZE * 0.82)}px serif`;

/** Finished size and placement, metres. Head-relative, so it lands in front of
 *  the player whatever the floor correction and whatever their height. */
const GLYPH_HEIGHT = 1.7;
const GLYPH_DIST = 2.6;
const GLYPH_EYE_DROP = 0.12;

/** Particle appearance. Ink on a white void, so dark — and small, because the
 *  form should read as made of countless fragments rather than of dots. */
const PARTICLE_COLOR = 0x14171c;
const PARTICLE_SIZE = 0.016;

/** Radius of the cloud the particles drift in from — roughly the space the
 *  dissolving worlds occupied, so the form gathers out of where they were. */
const SCATTER_RADIUS = 14;

/** Fraction of the gesture each particle takes to travel. Under 1 so they can
 *  be staggered: one starting at 0.4 with a 0.5 span arrives at 0.9, while its
 *  neighbour starting at 0.1 has long since settled. */
const TRAVEL_SPAN = 0.55;

/** Deterministic hash, so the scatter and the stagger are the same every run.
 *  A random layout could never be judged — the ink would settle differently
 *  each time and there would be nothing to tune. */
function hash(n: number): number {
  const x = Math.sin(n * 127.1 + 311.7) * 43758.5453;
  return x - Math.floor(x);
}

export class ReturnToDaoSystem extends createSystem({}) {
  private points!: THREE.Points;
  private material!: THREE.PointsMaterial;
  private positions!: Float32Array;
  /** Where each particle drifts in FROM. */
  private from!: Float32Array;
  /** Where each particle ends: its pixel in the character. */
  private to!: Float32Array;
  /** Per-particle start offset within the gesture, 0..1-TRAVEL_SPAN. */
  private delay!: Float32Array;
  private progress = 0;

  setVisible(visible: boolean): void {
    if (this.points) this.points.visible = visible;
  }

  /** 0 = fully dispersed, 1 = settled into the character. */
  setProgress(value: number): void {
    this.progress = Math.min(1, Math.max(0, value));
    this.apply();
  }

  /** Put the character in front of the player. Called as the scene begins. */
  placeInFrontOf(camera: THREE.Camera): void {
    if (!this.points) return;

    const head = new THREE.Vector3();
    camera.getWorldPosition(head);
    this.player.worldToLocal(head);

    const dir = new THREE.Vector3();
    camera.getWorldDirection(dir);
    dir.y = 0;
    if (dir.lengthSq() < 1e-8) dir.set(0, 0, -1);
    dir.normalize();

    this.points.position.set(
      head.x + dir.x * GLYPH_DIST,
      head.y - GLYPH_EYE_DROP,
      head.z + dir.z * GLYPH_DIST,
    );
    this.points.rotation.y = Math.atan2(-dir.x, -dir.z);
  }

  init() {
    const targets = this.sampleGlyph();

    this.positions = new Float32Array(PARTICLE_COUNT * 3);
    this.from = new Float32Array(PARTICLE_COUNT * 3);
    this.to = new Float32Array(PARTICLE_COUNT * 3);
    this.delay = new Float32Array(PARTICLE_COUNT);

    for (let i = 0; i < PARTICLE_COUNT; i++) {
      const t = targets[i % targets.length];
      this.to[i * 3] = t.x;
      this.to[i * 3 + 1] = t.y;
      // Slight depth spread, so the finished character has body rather than
      // being a flat decal hanging in the air.
      this.to[i * 3 + 2] = (hash(i * 3.7) - 0.5) * 0.05;

      // Drift in from a sphere around where the worlds were. Cube-rooted so
      // particles are spread through the volume rather than bunched at its rim.
      const u = hash(i * 1.3) * 2 - 1;
      const theta = hash(i * 2.1) * Math.PI * 2;
      const r = Math.cbrt(hash(i * 5.9)) * SCATTER_RADIUS;
      const s = Math.sqrt(1 - u * u);
      this.from[i * 3] = Math.cos(theta) * s * r;
      this.from[i * 3 + 1] = u * r;
      this.from[i * 3 + 2] = Math.sin(theta) * s * r;

      this.delay[i] = hash(i * 7.3) * (1 - TRAVEL_SPAN);
    }

    const geo = new THREE.BufferGeometry();
    geo.setAttribute("position", new THREE.BufferAttribute(this.positions, 3));

    this.material = new THREE.PointsMaterial({
      color: PARTICLE_COLOR,
      size: PARTICLE_SIZE,
      sizeAttenuation: true,
      transparent: true,
      opacity: 0,
      depthWrite: false,
    });

    this.points = new THREE.Points(geo, this.material);
    this.points.visible = false;
    this.points.frustumCulled = false; // particles move far outside their bounds
    this.player.add(this.points);

    this.apply();
    console.log(
      `[Dao] ${PARTICLE_COUNT} particles over ${targets.length} glyph samples`,
    );
  }

  /**
   * Rasterise 道 and return the dark pixels as positions, centred on origin and
   * scaled to GLYPH_HEIGHT.
   */
  private sampleGlyph(): { x: number; y: number }[] {
    const canvas = document.createElement("canvas");
    canvas.width = canvas.height = RASTER_SIZE;
    const ctx = canvas.getContext("2d", { willReadFrequently: true })!;

    ctx.fillStyle = "#fff";
    ctx.fillRect(0, 0, RASTER_SIZE, RASTER_SIZE);
    ctx.fillStyle = "#000";
    ctx.font = GLYPH_FONT;
    ctx.textAlign = "center";
    ctx.textBaseline = "middle";
    ctx.fillText(GLYPH, RASTER_SIZE / 2, RASTER_SIZE / 2);

    const data = ctx.getImageData(0, 0, RASTER_SIZE, RASTER_SIZE).data;
    const found: { x: number; y: number }[] = [];
    const scale = GLYPH_HEIGHT / RASTER_SIZE;

    for (let py = 0; py < RASTER_SIZE; py++) {
      for (let px = 0; px < RASTER_SIZE; px++) {
        // Red channel is enough: the glyph is drawn black on white.
        if (data[(py * RASTER_SIZE + px) * 4] > 128) continue;
        found.push({
          x: (px - RASTER_SIZE / 2) * scale,
          // Canvas y runs down, world y runs up.
          y: (RASTER_SIZE / 2 - py) * scale,
        });
      }
    }

    if (found.length < 200) {
      // A missing CJK font yields a blank canvas and therefore no form at all —
      // worth saying out loud rather than rendering an empty scene.
      console.warn(
        `[Dao] only ${found.length} samples — the font may not have ${GLYPH}`,
      );
    }
    return found;
  }

  /** Move every particle for the current progress. */
  private apply() {
    if (!this.positions) return;

    for (let i = 0; i < PARTICLE_COUNT; i++) {
      // Each particle runs its own window of the gesture.
      const local = (this.progress - this.delay[i]) / TRAVEL_SPAN;
      const t = Math.min(1, Math.max(0, local));
      // Ease out: fast out of the void, slowing as it finds its place — ink
      // settling, not an object being placed.
      const e = 1 - Math.pow(1 - t, 3);

      const j = i * 3;
      for (let k = 0; k < 3; k++) {
        this.positions[j + k] =
          this.from[j + k] + (this.to[j + k] - this.from[j + k]) * e;
      }
    }

    // Fade in early, so the dispersal is visible rather than appearing from
    // nothing once it is already half-formed.
    this.material.opacity = Math.min(1, this.progress * 3);
    (this.points.geometry.getAttribute("position") as THREE.BufferAttribute).needsUpdate =
      true;
  }
}
