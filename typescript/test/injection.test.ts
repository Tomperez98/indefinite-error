/** The injection: its seed, its scope, what sees it, and its bookkeeping. */

import { expect, test } from "bun:test";
import fc from "fast-check";
import { closeSync, openSync, readFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

import { decide, INT64_MAX, INT64_MIN, modeOf, RATES } from "../src/core.ts";
import { current, enter, fdLog, Injection, open } from "../src/injection.ts";
import { indefinite } from "../src/site.ts";
import { attempt, CALLS, discard, injection, run, SEEDS, seedWhere } from "./helpers.ts";

// --- The seed --------------------------------------------------------------------

test.each([
  ["1", TypeError],
  [1, TypeError],
  [true, TypeError],
  [undefined, TypeError],
  [INT64_MAX + 1n, RangeError],
  [INT64_MIN - 1n, RangeError],
])("a seed must be an int64 bigint: %p", (seed, error) => {
  // The middleware parses the header; anything else here is its bug.
  expect(() => open(seed as bigint, discard)).toThrow(error);
});

test("any int64 is a seed", async () => {
  // Negative, zero, and the extremes: all valid, all replayable.
  const op = indefinite("any", (i: number) => i * 2);
  await fc.assert(
    fc.asyncProperty(fc.bigInt({ min: INT64_MIN, max: INT64_MAX }), async (seed) => {
      expect(await run(seed, op, 5)).toEqual(await run(seed, op, 5));
    }),
  );
});

// --- Scope -----------------------------------------------------------------------

test("a nested injection throws", () => {
  const outer = injection(1n);
  expect(() => enter(outer, () => open(2n, discard))).toThrow(
    "indefinite: a request seeded 2 inside one seeded 1: is withIndefinite installed twice?",
  );
  outer.close();
  expect(enter(outer, () => open(2n, discard)).seed).toBe(2n); // a closed one is over
});

test("a closed injection injects nothing", () => {
  const ran: number[] = [];
  const op = indefinite("op", (i: number) => ran.push(i));
  const inj = injection(3n);
  inj.close();
  for (let i = 0; i < CALLS; i++) {
    enter(inj, () => op(i));
  }
  expect(ran).toHaveLength(CALLS);
  expect(inj.calls()).toEqual(new Map());
});

test("an injection nobody entered is inert", () => {
  // Only entering one activates it; building it by hand changes nothing.
  const ran: number[] = [];
  const op = indefinite("op", (i: number) => ran.push(i));
  const inj = new Injection(3n, discard);
  for (let i = 0; i < CALLS; i++) {
    op(i);
  }
  expect([ran.length, inj.calls().size, current()]).toEqual([CALLS, 0, undefined]);
});

test("everything the request starts sees the injection", async () => {
  // Awaits, Promise.all, timers, microtasks, and stream callbacks.
  const inj = injection(0n);
  const seen = await enter(inj, async () => {
    const found: Record<string, boolean> = {};
    await Promise.resolve();
    found["await"] = current() === inj;
    await Promise.all([1, 2].map(async () => (found["all"] = current() === inj)));
    await new Promise<void>((resolve) =>
      setTimeout(() => {
        found["timer"] = current() === inj;
        resolve();
      }, 0),
    );
    await new Promise<void>((resolve) =>
      queueMicrotask(() => {
        found["microtask"] = current() === inj;
        resolve();
      }),
    );
    const stream = new ReadableStream({
      pull(controller) {
        found["stream"] = current() === inj;
        controller.close();
      },
    });
    await stream.getReader().read();
    return found;
  });
  expect(seen).toEqual({ await: true, all: true, timer: true, microtask: true, stream: true });
  expect(current()).toBeUndefined(); // and nothing outside it does
});

test("a task that outlives its request gets no faults", async () => {
  // Once the request ends, marked calls go through, even in a leaked timer.
  const ran: number[] = [];
  const op = indefinite("op", async (i: number) => {
    ran.push(i);
    return i;
  });
  for (const seed of SEEDS.slice(0, 50)) {
    ran.length = 0;
    const inj = injection(seed);
    const leaked = enter(inj, async () => {
      await Bun.sleep(1);
      return Promise.all(Array.from({ length: CALLS }, (_, i) => op(i)));
    });
    inj.close();
    expect(await leaked).toEqual(Array.from({ length: CALLS }, (_, i) => i));
    expect(inj.calls()).toEqual(new Map());
  }
});

test("a leaked task may open its own injection", async () => {
  const outer = injection(1n);
  const leaked = enter(outer, async () => {
    await Bun.sleep(1);
    return open(2n, discard);
  });
  outer.close();
  expect((await leaked).seed).toBe(2n);
});

test("concurrent calls in one request are numbered in the order they start", async () => {
  // Calls started together draw n = 0, 1, 2, ... in order, until a before
  // fault ends the request: every call started after it is unrun and uncounted.
  const site = "hot";
  const seed = seedWhere(site, "both");
  const op = indefinite(site, async () => {
    await Bun.sleep(Math.random() * 2); // settle in any order
  });
  const inj = injection(seed);
  const settled = await enter(inj, () => Promise.allSettled(Array.from({ length: 200 }, () => op())));
  const phases = Array.from({ length: 200 }, (_, n) => decide(seed, inj.rate, "both", site, n));
  const firstBefore = phases.indexOf("before");
  expect(firstBefore).toBeGreaterThan(0);
  expect(inj.calls()).toEqual(new Map([[site, firstBefore + 1]]));
  expect(settled.slice(0, firstBefore).some((s) => s.status === "fulfilled")).toBe(true);
  expect(settled.slice(firstBefore).every((s) => s.status === "rejected")).toBe(true);
});

test("concurrent requests do not interfere", async () => {
  // Interleaved requests each get the faults they get alone.
  const op = indefinite("op", async (i: number) => {
    await Bun.sleep(0);
    return i * 2;
  });
  const alone = await Promise.all(SEEDS.slice(0, 50).map((seed) => run(seed, op)));
  const together = await Promise.all(SEEDS.slice(0, 50).map((seed) => run(seed, op)));
  expect(together).toEqual(alone);
});

// --- The injection's bookkeeping ---------------------------------------------------

test("calls and modes show which sites were reached", async () => {
  const reached = indefinite("reached", (i: number) => i * 2);
  indefinite("unreached", (i: number) => i * 2);
  const inj = injection(3n);
  for (let i = 0; i < CALLS; i++) {
    await attempt(inj, reached, i);
  }
  expect(inj.calls()).toEqual(new Map([["reached", CALLS]]));
  expect(inj.modes()).toEqual(new Map([["reached", modeOf(3n, "reached")]]));
  expect(inj.seed).toBe(3n);
  expect(RATES).toContain(inj.rate as (typeof RATES)[number]);
});

test("calls and modes are copies", () => {
  // Nothing outside the injection can shift the replay.
  const inj = injection(1n);
  inj.calls().set("x", 99);
  inj.modes().set("x", "both");
  expect([inj.calls().size, inj.modes().size]).toEqual([0, 0]);
});

test("an injection says its seed, rate, and sites", async () => {
  const a = indefinite("repr.a", (i: number) => i * 2);
  const b = indefinite("repr.b", (i: number) => i * 2);
  const inj = injection(3n);
  await attempt(inj, b, 0);
  await attempt(inj, a, 0);
  const modes = `repr.a=${modeOf(3n, "repr.a")}, repr.b=${modeOf(3n, "repr.b")}`;
  expect(String(inj)).toBe(`Injection(seed=3, rate=${inj.rate}, sites=[${modes}])`);
});

// --- The log ---------------------------------------------------------------------

test("a log writes each line whole to its file descriptor", () => {
  const path = join(tmpdir(), `indefinite-log-${process.pid}.txt`);
  const fd = openSync(path, "w");
  fdLog(fd)("indefinite-error: after app.get#4 (seed=13)\n");
  fdLog(fd)("indefinite-error: before 中#0 (seed=-1)\n");
  closeSync(fd);
  expect(readFileSync(path, "utf8")).toBe(
    "indefinite-error: after app.get#4 (seed=13)\nindefinite-error: before 中#0 (seed=-1)\n",
  );
  expect(() => fdLog(fd)("lost\n")).not.toThrow(); // closed: the response still names the fault
});
