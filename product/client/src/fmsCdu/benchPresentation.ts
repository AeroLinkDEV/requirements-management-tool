import type { CduBackend } from "./screen";
import type { TickSchedulingHost } from "./benchPacing";

/** Presentation notifications only; every kernel frame and model computation still settles normally. */
export class BenchPresentation {
  private readonly listeners = new Set<() => void>();
  private removers: (() => void)[] = [];
  private version = 0;
  private sourceVersion: number;
  private inFrame = false;
  private dirty = false;
  private timer: number | null = null;
  private publishedAt: number;
  private readonly sources: readonly Pick<CduBackend, "revision" | "subscribe">[];
  private readonly host: TickSchedulingHost;
  private readonly intervalMilliseconds: number;
  constructor(sources: readonly Pick<CduBackend, "revision" | "subscribe">[], host: TickSchedulingHost, intervalMilliseconds = 250) {
    this.sources = sources; this.host = host;
    this.intervalMilliseconds = intervalMilliseconds;
    this.sourceVersion = this.sources.reduce((sum, source) => sum + source.revision(), 0);
    this.publishedAt = host.performance.now();
  }
  readonly revision = () => this.version;
  readonly subscribe = (listener: () => void) => {
    this.listeners.add(listener);
    if (!this.removers.length) {
      this.removers = this.sources.map(source => source.subscribe(() => {
        this.dirty = true;
        if (!this.inFrame) this.flush();
      }));
      this.dirty = true;
      this.flush();
    }
    return () => {
      this.listeners.delete(listener);
      if (!this.listeners.size) {
        this.removers.forEach(remove => remove()); this.removers = [];
        if (this.timer !== null) this.host.clearTimeout(this.timer);
        this.timer = null;
      }
    };
  };
  readonly flush = () => {
    if (this.timer !== null) this.host.clearTimeout(this.timer);
    this.timer = null;
    const next = this.sources.reduce((sum, source) => sum + source.revision(), 0);
    this.dirty = false;
    if (next === this.sourceVersion) return;
    this.sourceVersion = next; this.version += 1; this.publishedAt = this.host.performance.now();
    this.listeners.forEach(listener => listener());
  };
  readonly frame = <T>(action: () => T): T => {
    const previous = this.inFrame;
    this.inFrame = true;
    try { return action(); }
    finally {
      this.inFrame = previous;
      if (!previous && this.dirty && this.listeners.size) {
        const remaining = this.intervalMilliseconds - (this.host.performance.now() - this.publishedAt);
        // The complete frame has settled; a publication already due needs no additional timer turn.
        if (remaining <= 0) this.flush();
        else if (this.timer === null) this.timer = this.host.setTimeout(this.flush, remaining);
      }
    }
  };
}
