/**
 * The pure core: every decision is a function of `(seed, site, n)`.
 *
 * It hashes exactly as every other implementation of `spec/` does, so a seed
 * replays the same faults in a TypeScript service, a Go one, a Rust one, and a
 * Python one. `spec/schedule.tsv` pins it.
 *
 * @module
 */

import { blake2b } from "@noble/hashes/blake2.js";

/** When a faulted call ends its request: before it runs, or after. */
export type Phase = "before" | "after";

/** Which phases may fault at one site, for one seed. */
export type Mode = "off" | "before" | "after" | "both";

/** Per-seed fault rates (swarm testing): some seeds are gentle, some brutal. */
export const RATES = [0.01, 0.05, 0.2, 0.5] as const;
export const MODES = ["off", "before", "after", "both"] as const satisfies readonly Mode[];

/** A seed is an int64, as in every implementation of spec/. */
export const INT64_MIN = -(2n ** 63n);
export const INT64_MAX = 2n ** 63n - 1n;

export function isInt64(seed: bigint): boolean {
  return seed >= INT64_MIN && seed <= INT64_MAX;
}

const utf8 = new TextEncoder();

/**
 * A uniform float in [0, 1) from `parts`, stable across processes and
 * languages: BLAKE2b with an 8-byte digest over the parts joined by NUL, read
 * big-endian, rounded to the nearest float64, divided by 2^64.
 */
export function unit(...parts: ReadonlyArray<string | number | bigint>): number {
  for (const part of parts) {
    if (typeof part === "number" && !Number.isSafeInteger(part)) {
      throw new TypeError(`indefinite: unit part ${part} is not an integer`);
    }
  }
  const digest = blake2b(utf8.encode(parts.join("\x00")), { dkLen: 8 });
  const word = new DataView(digest.buffer, digest.byteOffset, 8).getBigUint64(0); // big-endian
  return Number(word) / 2 ** 64; // Number() rounds to nearest, as the spec pins
}

export function pick<T>(options: readonly T[], u: number): T {
  assert(u >= 0 && u < 1, `u=${u} out of [0, 1)`);
  return options[Math.floor(u * options.length)] as T;
}

export function rateOf(seed: bigint): number {
  return pick(RATES, unit("rate", seed));
}

export function modeOf(seed: bigint, site: string): Mode {
  return pick(MODES, unit("mode", seed, site));
}

/**
 * The phase of the `n`-th call to `site`, or `undefined` if it passes;
 * `rate` and `mode` derive from `seed`.
 */
export function decide(
  seed: bigint,
  rate: number,
  mode: Mode,
  site: string,
  n: number,
): Phase | undefined {
  if (mode === "off" || unit("call", seed, site, n) >= rate) {
    return undefined;
  }
  if (mode === "both") {
    return unit("phase", seed, site, n) < 0.5 ? "before" : "after";
  }
  return mode;
}

/**
 * A site name must be non-empty and printable, with no whitespace: it goes on
 * the fault line, which must stay unambiguous to parse. Unicode "other" (C*)
 * and "separator" (Z*) are exactly the unprintable and whitespace characters.
 */
const PRINTABLE = /^[^\p{C}\p{Z}]+$/u;

export function checkSite(name: unknown): asserts name is string {
  if (typeof name !== "string" || !PRINTABLE.test(name)) {
    throw new TypeError(
      `indefinite: a site name must be a non-empty string, printable with no whitespace, got ${show(name)}`,
    );
  }
}

/** Throws if `condition` is false: a broken invariant is a bug, so crash at the line. */
export function assert(condition: boolean, message: string): asserts condition {
  if (!condition) {
    throw new Error(`indefinite: bug: ${message}`);
  }
}

/** Does nothing: for a promise whose rejection is someone else's to report. */
export function ignore(): void {}

/** A value, for an error message: strings quoted and escaped, anything else as String() has it. */
export function show(value: unknown): string {
  return typeof value === "string" ? JSON.stringify(value) : String(value);
}
