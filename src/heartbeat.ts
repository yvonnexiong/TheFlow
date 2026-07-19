// ------------------------------------------------------------
// Heartbeat — the only sound in 一 Breath
// ------------------------------------------------------------
// Synthesised rather than sampled: a heartbeat is two low thumps with an
// envelope, which WebAudio does exactly, and it keeps the repo free of an
// audio asset for a sound that wants tuning by ear anyway.
//
// "Faint" is the whole brief — this should sit at the edge of hearing, felt
// more than heard. Tune with PEAK_GAIN.
//
// The AudioContext cannot start without a user gesture, so start() is called
// from the Enter XR click path. Calling it earlier leaves the context
// suspended and silent.

/** Beats per minute — resting, slightly slow. The void is calm, not tense.
 *  Exported so the seed ring can pulse in time with what you hear. */
export const HEARTBEAT_BPM = 54;
const BPM = HEARTBEAT_BPM;

/** Peak gain of the first thump. Deliberately tiny. */
const PEAK_GAIN = 0.055;

/** The second thump ("dub") is quieter and follows this soon after the first. */
const DUB_DELAY = 0.34;
const DUB_GAIN_RATIO = 0.62;

/** Fundamental of each thump. Low enough to feel chest-like on headphones. */
const THUMP_HZ = 48;

/** How far ahead to schedule beats, and how often to top the queue up. */
const SCHEDULE_AHEAD = 0.4;
const TICK_MS = 120;

export class Heartbeat {
  private ctx: AudioContext | null = null;
  private master: GainNode | null = null;
  private timer: number | null = null;
  private nextBeat = 0;

  /** Start (or resume) the heartbeat. Safe to call repeatedly. */
  async start() {
    if (!this.ctx) {
      const Ctor =
        window.AudioContext ??
        (window as unknown as { webkitAudioContext?: typeof AudioContext })
          .webkitAudioContext;
      if (!Ctor) {
        console.warn("[Heartbeat] No AudioContext available — staying silent.");
        return;
      }
      this.ctx = new Ctor();
      this.master = this.ctx.createGain();
      this.master.gain.value = 1;
      this.master.connect(this.ctx.destination);
      this.nextBeat = this.ctx.currentTime + 0.15;
    }

    // Autoplay policy parks the context until a gesture resumes it.
    if (this.ctx.state === "suspended") await this.ctx.resume();

    if (this.timer === null) {
      this.timer = window.setInterval(() => this.pump(), TICK_MS);
    }
  }

  /** Fade out over `seconds` and stop scheduling. */
  stop(seconds = 1.5) {
    if (!this.ctx || !this.master) return;
    const now = this.ctx.currentTime;
    this.master.gain.cancelScheduledValues(now);
    this.master.gain.setValueAtTime(this.master.gain.value, now);
    this.master.gain.linearRampToValueAtTime(0.0001, now + seconds);
    if (this.timer !== null) {
      window.clearInterval(this.timer);
      this.timer = null;
    }
  }

  /** Queue up any beats falling inside the lookahead window. */
  private pump() {
    if (!this.ctx) return;
    const period = 60 / BPM;
    while (this.nextBeat < this.ctx.currentTime + SCHEDULE_AHEAD) {
      this.thump(this.nextBeat, PEAK_GAIN);
      this.thump(this.nextBeat + DUB_DELAY, PEAK_GAIN * DUB_GAIN_RATIO);
      this.nextBeat += period;
    }
  }

  /** One thump: a low sine pitched down through a fast percussive envelope. */
  private thump(at: number, peak: number) {
    if (!this.ctx || !this.master) return;

    const osc = this.ctx.createOscillator();
    osc.type = "sine";
    osc.frequency.setValueAtTime(THUMP_HZ, at);
    // Slight downward glide gives the thump its body rather than a beep.
    osc.frequency.exponentialRampToValueAtTime(THUMP_HZ * 0.62, at + 0.16);

    const env = this.ctx.createGain();
    env.gain.setValueAtTime(0.0001, at);
    env.gain.exponentialRampToValueAtTime(peak, at + 0.018);
    env.gain.exponentialRampToValueAtTime(0.0001, at + 0.26);

    osc.connect(env).connect(this.master);
    osc.start(at);
    osc.stop(at + 0.3);
  }
}
