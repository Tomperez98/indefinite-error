/**
 * One request's injection: what its seed chose, and which sites it reached.
 *
 * The only state is a call counter per site, and whether a fault has ended the
 * request. It travels with the request through `AsyncLocalStorage`: every
 * `await`, promise callback, timer, and stream callback the request starts
 * sees it; a worker thread doesn't.
 *
 * @module
 */

import { AsyncLocalStorage } from "node:async_hooks";
import { writeSync } from "node:fs";

import { decide, isInt64, modeOf, rateOf, show, type Mode, type Phase } from "./core.ts";

/** One injected fault: the `n`-th call to `site` in the request seeded `seed`. */
export class Fault {
  constructor(
    readonly seed: bigint,
    readonly site: string,
    readonly n: number,
    readonly phase: Phase,
  ) {}

  /** The fault line's payload: `after app.get#4 (seed=13)`. */
  toString(): string {
    return `${this.phase} ${this.site}#${this.n} (seed=${this.seed})`;
  }
}

/**
 * What a faulted call throws. It ends the request: `withIndefinite` answers
 * for it, whether it propagates or not.
 *
 * Not exported from the package. JavaScript has no exception a `catch` can't
 * see, so instead of hiding it the injection remembers it: once it's thrown,
 * every later marked call in the request throws it again without running, and
 * the request's response is replaced by the fault's.
 */
export class IndefiniteFault extends Error {
  override readonly name = "IndefiniteFault";

  constructor(readonly fault: Fault) {
    super(`indefinite-error: ${fault}: this request has ended; let it reach withIndefinite`);
  }
}

/** Where fault lines go: stderr, outside tests. */
export type Log = (line: string) => void;

/**
 * A log that writes each line to file descriptor `fd` in one synchronous
 * write, so lines from concurrent requests don't interleave or wait in a
 * buffer. A lost line is fine: the response still names the fault.
 */
export function fdLog(fd: number): Log {
  return (line) => {
    try {
      writeSync(fd, line);
    } catch {
      // closed: the response still says it
    }
  };
}

export const stderr: Log = fdLog(2);

/** What a site call draws when it starts. */
export type Draw =
  /** Run the call and return what it returns. */
  | { readonly kind: "pass" }
  /** Fault it. */
  | { readonly kind: "fault"; readonly fault: Fault }
  /** A fault already ended this request: the call must never run. */
  | { readonly kind: "over"; readonly error: IndefiniteFault };

const PASS: Draw = { kind: "pass" };

export class Injection {
  readonly seed: bigint;
  /** The chance that a call to a site whose mode isn't `off` faults. */
  readonly rate: number;
  readonly #log: Log;
  readonly #calls = new Map<string, number>();
  readonly #modes = new Map<string, Mode>(); // a cache of modeOf(seed, site)
  readonly #ended = new AbortController();
  #aborted: IndefiniteFault | undefined;
  #closed = false;

  constructor(seed: bigint, log: Log) {
    if (typeof seed !== "bigint") {
      throw new TypeError(`indefinite: seed ${show(seed)} is not a bigint`);
    }
    if (!isInt64(seed)) {
      throw new RangeError(`indefinite: seed ${seed} is not an int64`);
    }
    this.seed = seed;
    this.rate = rateOf(seed);
    this.#log = log;
  }

  /** Counts one call to `site`, and draws its outcome. */
  next(site: string): Draw {
    if (this.#closed) {
      return PASS;
    }
    if (this.#aborted !== undefined) {
      return { kind: "over", error: this.#aborted };
    }
    const n = this.#calls.get(site) ?? 0;
    this.#calls.set(site, n + 1);
    let mode = this.#modes.get(site);
    if (mode === undefined) {
      mode = modeOf(this.seed, site);
      this.#modes.set(site, mode);
    }
    const phase = decide(this.seed, this.rate, mode, site, n);
    return phase === undefined ? PASS : { kind: "fault", fault: new Fault(this.seed, site, n, phase) };
  }

  /**
   * Ends the request with `fault`, writing its line, unless the request is
   * already over. Returns what to throw: the fault that ended the request,
   * which is `fault` unless another got there first. Returns `undefined` if
   * the request has closed and there is nothing left to fault: the caller
   * carries on.
   */
  abort(fault: Fault): IndefiniteFault | undefined {
    if (this.#closed) {
      return undefined;
    }
    if (this.#aborted === undefined) {
      this.#log(`indefinite-error: ${fault}\n`);
      this.#aborted = new IndefiniteFault(fault);
      this.#ended.abort(this.#aborted);
    }
    return this.#aborted;
  }

  /** Aborts, with the fault as its reason, when a fault ends the request. */
  get signal(): AbortSignal {
    return this.#ended.signal;
  }

  /** The fault that ended the request, if one did. */
  get aborted(): IndefiniteFault | undefined {
    return this.#aborted;
  }

  /** Whether the request has ended; a closed injection injects nothing. */
  get closed(): boolean {
    return this.#closed;
  }

  close(): void {
    this.#closed = true;
  }

  /** Calls per site so far (a copy). A missing site was never reached. */
  calls(): Map<string, number> {
    return new Map(this.#calls);
  }

  /** Which phases may fault, per site reached so far (a copy). */
  modes(): Map<string, Mode> {
    return new Map(this.#modes);
  }

  /**
   * Forgets the fault that ended the request, keeping the counters. For
   * tests: they make one call per "request" without resetting call numbers.
   */
  resume(): void {
    this.#aborted = undefined;
  }

  toString(): string {
    const sites = [...this.#modes]
      .sort(([a], [b]) => (a < b ? -1 : 1))
      .map(([site, mode]) => `${site}=${mode}`)
      .join(", ");
    return `Injection(seed=${this.seed}, rate=${this.rate}, sites=[${sites}])`;
  }
}

const storage = new AsyncLocalStorage<Injection>();

/** The injection of the request being served, if any, open or closed. */
export function current(): Injection | undefined {
  return storage.getStore();
}

/**
 * Opens an injection seeded `seed`. Throws inside another open one: the
 * middleware is installed twice.
 */
export function open(seed: bigint, log: Log): Injection {
  const active = current();
  if (active !== undefined && !active.closed) {
    throw new Error(
      `indefinite: a request seeded ${seed} inside one seeded ${active.seed}: is withIndefinite installed twice?`,
    );
  }
  return new Injection(seed, log);
}

/** Runs `fn` with `injection` current, and everything `fn` starts. */
export function enter<T>(injection: Injection, fn: () => T): T {
  return storage.run(injection, fn);
}
