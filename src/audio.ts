// ------------------------------------------------------------
// Shared audio context + one-shot sample playback
// ------------------------------------------------------------
// ONE AudioContext for the whole piece. This matters more than tidiness: the
// autoplay policy unlocks a *context*, not the page, so a second context
// created later would start suspended and need its own gesture. Everything
// sharing this one means the first click unlocks all of it.

let ctx: AudioContext | null = null;

/** The shared context, created on first use. */
export function getAudioContext(): AudioContext {
  if (!ctx) {
    const Ctor =
      window.AudioContext ??
      (window as unknown as { webkitAudioContext: typeof AudioContext })
        .webkitAudioContext;
    ctx = new Ctor();
  }
  return ctx;
}

/**
 * Resume the context, arming a one-shot gesture listener if the browser is
 * still holding it suspended. Resolves true once actually running.
 */
export async function resumeAudio(): Promise<boolean> {
  const c = getAudioContext();
  // Read through a function: resume() mutates state asynchronously, which
  // narrowing on `c.state` would otherwise convince the compiler is impossible.
  const running = () => getAudioContext().state === "running";

  if (running()) return true;
  await c.resume();
  if (running()) return true;

  await new Promise<void>((done) => {
    const events = ["pointerdown", "keydown", "touchstart"];
    const unlock = () => {
      for (const e of events) window.removeEventListener(e, unlock);
      void c.resume().then(() => done());
    };
    for (const e of events) window.addEventListener(e, unlock, { once: true });
  });
  return running();
}

/**
 * A decoded sample that can be played on demand.
 *
 * Decoding happens once, up front — a one-shot that has to fetch when it fires
 * arrives late, which for a sound tied to a visual beat is the same as being
 * wrong.
 */
export class Sound {
  private buffer: AudioBuffer | null = null;
  private loading: Promise<void> | null = null;
  private playing: { src: AudioBufferSourceNode; gain: GainNode } | null = null;

  constructor(
    private readonly url: string,
    private readonly volume = 1,
    /** Loop forever until stopped. For ambience rather than punctuation. */
    private readonly loop = false,
  ) {}

  /** Fetch + decode ahead of time. Safe to call repeatedly. */
  load(): Promise<void> {
    if (this.loading) return this.loading;
    this.loading = (async () => {
      try {
        const res = await fetch(this.url);
        if (!res.ok) throw new Error(`HTTP ${res.status} for ${this.url}`);
        this.buffer = await getAudioContext().decodeAudioData(
          await res.arrayBuffer(),
        );
        console.log(
          `[Sound] ${this.url} ready (${this.buffer.duration.toFixed(2)}s)`,
        );
      } catch (err) {
        // Silence is survivable; a thrown error mid-scene is not.
        console.warn(`[Sound] ${this.url} failed — staying silent.`, err);
      }
    })();
    return this.loading;
  }

  /**
   * Play now. No-op if the sample never loaded.
   *
   * `fadeIn` ramps up over that many seconds — worth using on looping ambience,
   * which arriving at full volume announces itself as a cue rather than as
   * something that was always there.
   */
  async play(fadeIn = 0): Promise<void> {
    await this.load();
    if (!this.buffer) return;
    if (!(await resumeAudio())) return;

    const c = getAudioContext();
    const gain = c.createGain();
    gain.connect(c.destination);

    if (fadeIn > 0) {
      gain.gain.setValueAtTime(0.0001, c.currentTime);
      gain.gain.linearRampToValueAtTime(this.volume, c.currentTime + fadeIn);
    } else {
      gain.gain.value = this.volume;
    }

    const src = c.createBufferSource();
    src.buffer = this.buffer;
    src.loop = this.loop;
    src.connect(gain);
    src.start();

    if (this.loop) {
      // Held so it can be stopped later; a looping source never ends on its own.
      this.playing = { src, gain };
    } else {
      // Let the graph collect itself once the tail has finished.
      src.onended = () => {
        src.disconnect();
        gain.disconnect();
      };
    }
  }

  /** Fade out and stop a looping sound. No-op for one-shots. */
  stop(seconds = 2): void {
    if (!this.playing) return;
    const { src, gain } = this.playing;
    this.playing = null;

    const now = getAudioContext().currentTime;
    gain.gain.cancelScheduledValues(now);
    gain.gain.setValueAtTime(gain.gain.value, now);
    gain.gain.linearRampToValueAtTime(0.0001, now + seconds);
    src.stop(now + seconds + 0.05);
  }
}
