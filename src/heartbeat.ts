// ------------------------------------------------------------
// Heartbeat — the only sound in 一 Breath
// ------------------------------------------------------------
// Plays a looped sample. "Faint" is the whole brief: this should sit at the
// edge of hearing, felt more than heard. Tune with VOLUME.
//
// The AudioContext cannot start without a user gesture, so start() is called
// from the Enter XR click path. Calling it earlier leaves the context
// suspended and silent — decoding still happens, so the first beat is
// immediate once the gesture arrives.

import { getAudioContext, resumeAudio } from "./audio.js";

/** Served from public/ — assets/ is not on Vite's static path. */
const HEARTBEAT_URL = "./sfx/effect_heartbeat_1.mp3";

/** Playback gain. Deliberately low; raise if inaudible on Quest speakers. */
const VOLUME = 0.35;

/** Fallback tempo for the seed ring's pulse, used only until the file has
 *  decoded. Once loaded, the ring follows the sample's real length instead. */
export const HEARTBEAT_BPM = 54;

export class Heartbeat {
  private ctx: AudioContext | null = null;
  private master: GainNode | null = null;
  private buffer: AudioBuffer | null = null;
  private source: AudioBufferSourceNode | null = null;
  private loadPromise: Promise<void> | null = null;
  /** The Director wants sound right now (cleared by stop()). */
  private wanted = false;

  /**
   * Tempo the ring should pulse at, in BPM.
   *
   * Assumes the sample is ONE heartbeat cycle, so its duration is the beat
   * period. If the file holds several beats the ring will pulse too slowly —
   * check the decoded duration logged by load() and set HEARTBEAT_BPM by hand
   * if that's the case.
   */
  get bpm(): number {
    if (!this.buffer || this.buffer.duration <= 0) return HEARTBEAT_BPM;
    return 60 / this.buffer.duration;
  }

  /** Fetch and decode ahead of time so the first beat isn't late. */
  load(): Promise<void> {
    if (this.loadPromise) return this.loadPromise;
    this.loadPromise = (async () => {
      try {
        const res = await fetch(HEARTBEAT_URL);
        if (!res.ok) throw new Error(`HTTP ${res.status} for ${HEARTBEAT_URL}`);
        const bytes = await res.arrayBuffer();
        this.buffer = await this.context().decodeAudioData(bytes);
        console.log(
          `[Heartbeat] loaded ${this.buffer.duration.toFixed(2)}s ` +
            `→ ring pulse ${this.bpm.toFixed(1)} BPM`,
        );
      } catch (err) {
        // Silence is survivable; a thrown error during 一 is not.
        console.warn("[Heartbeat] could not load — staying silent.", err);
      }
    })();
    return this.loadPromise;
  }

  /** Start (or resume) the loop. Safe to call repeatedly. */
  async start() {
    this.wanted = true;
    await this.load();
    const ctx = this.context();
    if (!ctx || !this.buffer) return;

    // Autoplay policy parks the context until a user gesture resumes it. The
    // Director calls start() as soon as the world has loaded, which is usually
    // BEFORE the player has clicked anything — so resume() here does nothing
    // and the beat would be lost silently. Arm a one-shot gesture listener that
    // retries, so the first click/tap/key anywhere brings it in.
    if (ctx.state !== "running") {
      console.log("[Heartbeat] waiting for a gesture to unlock audio…");
      if (!(await resumeAudio())) return;
      if (!this.wanted) return; // Director moved on while we waited
    }

    if (this.source) return; // already beating

    const master = this.master!;
    master.gain.cancelScheduledValues(ctx.currentTime);
    master.gain.setValueAtTime(VOLUME, ctx.currentTime);

    this.source = ctx.createBufferSource();
    this.source.buffer = this.buffer;
    this.source.loop = true;
    this.source.connect(master);
    this.source.start();
    console.log(`[Heartbeat] playing (gain ${VOLUME}, ctx ${ctx.state})`);
  }

  /** Fade out over `seconds`, then stop. */
  stop(seconds = 1.5) {
    this.wanted = false;
    if (!this.ctx || !this.master || !this.source) return;
    const now = this.ctx.currentTime;
    const src = this.source;
    this.source = null;

    this.master.gain.cancelScheduledValues(now);
    this.master.gain.setValueAtTime(this.master.gain.value, now);
    this.master.gain.linearRampToValueAtTime(0.0001, now + seconds);
    // Stop slightly after the ramp so the tail isn't clipped.
    src.stop(now + seconds + 0.05);
  }

  /** Master gain on the shared context, built on first use. */
  private context(): AudioContext {
    const c = getAudioContext();
    if (!this.master) {
      this.master = c.createGain();
      this.master.gain.value = VOLUME;
      this.master.connect(c.destination);
    }
    this.ctx = c;
    return c;
  }
}
