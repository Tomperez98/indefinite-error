/** Shared test helpers. They fail the test themselves; none returns an error. */

import { expect } from "bun:test";
import { existsSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";

import { modeOf, type Mode, type Phase } from "../src/core.ts";
import { enter, Fault, IndefiniteFault, Injection, open, type Log } from "../src/injection.ts";
import { indefinite } from "../src/site.ts";

export const SEEDS: readonly bigint[] = Array.from({ length: 300 }, (_, i) => BigInt(i));
export const CALLS = 50;

// --- spec/ ---------------------------------------------------------------------

/** The repository's `spec/`: the contract every implementation is tested against. */
function specDir(): string {
  for (let dir = import.meta.dir; dir !== dirname(dir); dir = dirname(dir)) {
    if (existsSync(join(dir, "spec", "README.md"))) {
      return join(dir, "spec");
    }
  }
  throw new Error(`no spec/README.md above ${import.meta.dir}: tests run from a repository checkout`);
}

export const SPEC = specDir();

export function specText(name: string): string {
  return readFileSync(join(SPEC, name), "utf8");
}

/** The tab-separated rows of `spec/<name>`, without its `#` comments. */
export function specRows(name: string): string[][] {
  return specText(name)
    .split("\n")
    .filter((line) => line !== "" && !line.startsWith("#"))
    .map((line) => line.split("\t"));
}

// --- Logs and injections -------------------------------------------------------

/** A log that keeps every line, for tests to read. */
export function captured(): { log: Log; text: () => string } {
  const lines: string[] = [];
  return { log: (line) => lines.push(line), text: () => lines.join("") };
}

export const discard: Log = () => {};

/** An injection seeded `seed`, as the middleware would open one. */
export function injection(seed: bigint, log: Log = discard): Injection {
  return open(seed, log);
}

/** The first seed that gives `site` this `mode`. */
export function seedWhere(site: string, mode: Mode): bigint {
  const seed = SEEDS.find((s) => modeOf(s, site) === mode);
  if (seed === undefined) {
    throw new Error(`no seed in 0..${SEEDS.length} gives ${site} mode ${mode}`);
  }
  return seed;
}

/**
 * What `fn` does inside `inj`, as one request: its value, or the fault that
 * ended it. Then the injection carries on, keeping its call numbers, as if
 * the next call were its own request. Stands in for the middleware.
 */
export async function drive<T>(inj: Injection, fn: () => T): Promise<{ value: Awaited<T> } | { fault: Fault }> {
  try {
    return { value: await enter(inj, fn) };
  } catch (error) {
    if (!(error instanceof IndefiniteFault)) {
      throw error;
    }
    return { fault: error.fault };
  } finally {
    inj.resume();
  }
}

// --- Flavors: one contract, three ways to write the function --------------------

export type Outcome = "ok" | Phase;
export type Op = (i: number) => number | Promise<number>;

/** A way to write the function `indefinite` wraps. */
export interface Flavor {
  readonly name: "sync" | "async" | "promise";
  /** `indefinite(site, body)`, with body written this flavor's way. */
  mark<A extends unknown[], R>(site: string, body: (...args: A) => R): (...args: A) => R | Promise<R>;
}

export const FLAVORS: readonly Flavor[] = [
  { name: "sync", mark: (site, body) => indefinite(site, body) },
  {
    name: "async",
    mark: (site, body) =>
      indefinite(site, async (...args: Parameters<typeof body>) => {
        await Promise.resolve(); // a real suspension point, like I/O
        return body(...args);
      }),
  },
  {
    name: "promise", // a plain function that returns a promise
    mark: (site, body) =>
      indefinite(site, (...args: Parameters<typeof body>) => Promise.resolve().then(() => body(...args))),
  },
];

/** An op that records each real execution, and returns `i * 2`. */
export function recorder(flavor: Flavor, site = "op"): { ran: number[]; op: Op } {
  const ran: number[] = [];
  const op = flavor.mark(site, (i: number) => {
    ran.push(i);
    return i * 2;
  });
  return { ran, op };
}

/** "ok", or the injected phase: one call is one request. */
export async function attempt(inj: Injection, op: Op, i: number): Promise<Outcome> {
  const result = await drive(inj, () => op(i));
  if ("fault" in result) {
    return result.fault.phase;
  }
  expect(result.value).toBe(i * 2);
  return "ok";
}

/** The outcomes of `calls` calls to `op` in one injection. */
export async function run(seed: bigint, op: Op, calls = CALLS): Promise<Outcome[]> {
  const inj = injection(seed);
  const outcomes: Outcome[] = [];
  for (let i = 0; i < calls; i++) {
    outcomes.push(await attempt(inj, op, i));
  }
  inj.close();
  return outcomes;
}

/** The first fault `fn` hits across SEEDS (of `phase`, if given). */
export async function firstFault(fn: () => unknown, phase?: Phase): Promise<Fault> {
  for (const seed of SEEDS) {
    const result = await drive(injection(seed), fn);
    if ("fault" in result && (phase === undefined || result.fault.phase === phase)) {
      return result.fault;
    }
  }
  throw new Error(`no seed faulted ${phase ?? "at all"}`);
}

export class DefiniteError extends Error {
  override readonly name = "DefiniteError";
}

/** Sorted, for comparing sets of outcomes. */
export function sorted(values: Iterable<string>): string[] {
  return [...values].sort();
}
