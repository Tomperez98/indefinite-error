/**
 * The contract every marked call keeps, whichever way its function is
 * written: sync, `async`, or a plain function returning a promise. A test
 * catching the fault (`drive`) stands in for the middleware;
 * middleware.test.ts has the real one.
 */

import { describe, expect, test } from "bun:test";

import { enter, IndefiniteFault } from "../src/injection.ts";
import { indefinite } from "../src/site.ts";
import {
  attempt,
  CALLS,
  captured,
  DefiniteError,
  drive,
  firstFault,
  FLAVORS,
  injection,
  recorder,
  run,
  SEEDS,
  sorted,
  type Outcome,
} from "./helpers.ts";

describe.each([...FLAVORS])("$name", (flavor) => {
  // --- Before never ran; after ran; outside a request nothing happens ---

  test("inert outside a request", async () => {
    const { ran, op } = recorder(flavor);
    for (let i = 0; i < CALLS; i++) {
      expect(await op(i)).toBe(i * 2);
    }
    expect(ran).toEqual(Array.from({ length: CALLS }, (_, i) => i));
  });

  test("before never runs, after always runs", async () => {
    for (const seed of SEEDS) {
      const { ran, op } = recorder(flavor);
      const outcomes = await run(seed, op);
      expect(ran).toEqual(outcomes.flatMap((o, i) => (o === "before" ? [] : [i])));
    }
  });

  // --- Definite outcomes pass through; after discards them ---

  test("a definite error passes through unless faulted", async () => {
    const boom = flavor.mark("boom", () => {
      throw new DefiniteError();
    });
    const seen = new Set<string>();
    for (const seed of SEEDS) {
      try {
        const result = await drive(injection(seed), boom);
        seen.add("fault" in result ? result.fault.phase : "returned");
      } catch (error) {
        expect(error).toBeInstanceOf(DefiniteError);
        seen.add("definite");
      }
    }
    expect(sorted(seen)).toEqual(["after", "before", "definite"]);
  });

  test("the call returns what the function returned", async () => {
    const value = { partial: true };
    const op = flavor.mark("value", () => value);
    for (const seed of SEEDS) {
      const result = await drive(injection(seed), op);
      if ("value" in result) {
        expect(result.value).toBe(value);
      }
    }
  });

  // --- The fault ends the request ---

  test("once a fault fires, no later call runs", async () => {
    // JavaScript can't hide an exception from `catch`. So a retry loop can
    // catch the fault, but every later marked call throws it again, unrun.
    const { ran, op } = recorder(flavor);
    const { ran: otherRan, op: other } = recorder(flavor, "other");
    let faulted = 0;
    for (const seed of SEEDS) {
      ran.length = 0;
      otherRan.length = 0;
      const inj = injection(seed);
      const errors: unknown[] = [];
      await enter(inj, async () => {
        for (let n = 0; n < 5; n++) {
          try {
            await op(n);
          } catch (error) {
            errors.push(error); // the retry loop swallows it
          }
        }
        try {
          await other(0);
        } catch (error) {
          errors.push(error);
        }
      });
      inj.close();
      if (errors.length === 0) {
        expect(ran).toEqual([0, 1, 2, 3, 4]);
        continue;
      }
      faulted++;
      const [first] = errors;
      expect(first).toBeInstanceOf(IndefiniteFault);
      expect(errors.every((e) => e === first)).toBe(true); // the same fault, every time
      const { site, n, phase } = (first as IndefiniteFault).fault;
      if (site === "other") {
        expect([ran, errors.length]).toEqual([[0, 1, 2, 3, 4], 1]);
        continue;
      }
      expect(errors).toHaveLength(5 - n + 1); // call n, every later retry, and the other site
      expect(ran).toEqual(Array.from({ length: phase === "after" ? n + 1 : n }, (_, i) => i));
      expect(otherRan).toEqual([]);
      expect(inj.calls().get("op")).toBe(n + 1); // an unrun call isn't counted
      expect(inj.calls().get("other")).toBeUndefined();
    }
    expect(faulted).toBeGreaterThan(0);
  });

  test("a fault runs finally blocks", async () => {
    // The server lives on, so finally runs and resources are released.
    const events: string[] = [];
    const commit = flavor.mark("commit", () => {
      events.push("commit");
    });
    const want: Record<Outcome, string[]> = {
      ok: ["commit", "after", "finally"],
      before: ["finally"],
      after: ["commit", "finally"],
    };
    for (const seed of SEEDS) {
      events.length = 0;
      const result = await drive(injection(seed), async () => {
        try {
          await commit();
          events.push("after");
        } finally {
          events.push("finally");
        }
      });
      expect(events).toEqual(want["fault" in result ? result.fault.phase : "ok"]);
    }
  });

  test("a fault writes the line", async () => {
    const { op } = recorder(flavor, "logged");
    const { log, text } = captured();
    const want: string[] = [];
    for (const seed of SEEDS) {
      const result = await drive(injection(seed, log), () => op(0));
      if ("fault" in result) {
        want.push(`indefinite-error: ${result.fault}\n`);
      }
    }
    expect(want.length).toBeGreaterThan(0);
    expect(text()).toBe(want.join(""));
  });

  test("a fault throws where the function would fail", async () => {
    // An async function rejects; a plain one throws, before it runs, or after
    // it returns -- unless it returned a promise, which then rejects.
    const op = flavor.mark("where", () => 0);
    const seen = new Set<string>();
    for (const seed of SEEDS) {
      const inj = injection(seed);
      let result: unknown;
      try {
        result = enter(inj, op);
      } catch (error) {
        expect(error).toBeInstanceOf(IndefiniteFault);
        seen.add(`throws ${(error as IndefiniteFault).fault.phase}`);
        continue;
      }
      if (result instanceof Promise) {
        await result.then(
          () => seen.add("resolves"),
          (error: IndefiniteFault) => seen.add(`rejects ${error.fault.phase}`),
        );
      } else {
        seen.add("returns");
      }
    }
    const want = {
      sync: ["returns", "throws after", "throws before"],
      async: ["rejects after", "rejects before", "resolves"],
      promise: ["rejects after", "resolves", "throws before"],
    };
    expect(sorted(seen)).toEqual(want[flavor.name]);
  });

  // --- Replay: same seed, same faults; sites don't interfere ---

  test("same seed, same outcomes", async () => {
    for (const seed of SEEDS) {
      const a = recorder(flavor);
      const b = recorder(flavor);
      expect(await run(seed, a.op)).toEqual(await run(seed, b.op));
    }
  });

  test("a request depends on its seed alone", async () => {
    // seed=41 takes the same path in its first request or after 100 others.
    const { op } = recorder(flavor);
    const first = await run(41n, op);
    for (let other = 1000n; other < 1100n; other++) {
      await run(other, op);
    }
    expect(await run(41n, op)).toEqual(first);
  });

  test("sites are independent", async () => {
    // Calls to other sites don't shift a site's decisions.
    const { op } = recorder(flavor);
    const { op: other } = recorder(flavor, "other");
    for (const seed of SEEDS) {
      const alone = await run(seed, op);
      const inj = injection(seed);
      const mixed: Outcome[] = [];
      for (let i = 0; i < CALLS; i++) {
        await attempt(inj, other, i);
        mixed.push(await attempt(inj, op, i));
      }
      expect(mixed).toEqual(alone);
    }
  });

  test("seeds vary which phases a site gets", async () => {
    // Swarm testing: per seed, a site faults never, before-only, after-only, or both.
    const { op } = recorder(flavor);
    const kinds = new Set<string>();
    for (const seed of SEEDS) {
      kinds.add(sorted(new Set((await run(seed, op)).filter((o) => o !== "ok"))).join("+"));
    }
    expect(sorted(kinds)).toEqual(["", "after", "after+before", "before"]);
  });

  // --- What the fault says ---

  test("the fault names the site, call, and seed", async () => {
    const { op } = recorder(flavor, "db.commit");
    const fault = await firstFault(() => op(0));
    expect([fault.site, fault.n]).toEqual(["db.commit", 0]);
    expect(String(fault)).toBe(`${fault.phase} db.commit#0 (seed=${fault.seed})`);
  });

  test("calls count every call, faulted or not", async () => {
    const { op } = recorder(flavor, "counted");
    for (const seed of SEEDS) {
      const inj = injection(seed);
      for (let i = 0; i < CALLS; i++) {
        await attempt(inj, op, i);
      }
      expect(inj.calls()).toEqual(new Map([["counted", CALLS]]));
    }
  });
});

// --- Things only one flavor can do ---

test("a dropped fault doesn't crash the process", async () => {
  // Fire and forget: nobody awaits the rejection. withIndefinite answers anyway.
  const publish = indefinite("publish", async () => {});
  const unhandled: unknown[] = [];
  const listener = (reason: unknown) => unhandled.push(reason);
  process.on("unhandledRejection", listener);
  let faulted = 0;
  for (const seed of SEEDS) {
    const inj = injection(seed);
    void enter(inj, publish);
    await Bun.sleep(0);
    faulted += inj.aborted === undefined ? 0 : 1;
  }
  process.off("unhandledRejection", listener);
  expect(faulted).toBeGreaterThan(0);
  expect(unhandled).toEqual([]);
});

test("an after call whose request ended first returns its real outcome", async () => {
  // Nothing is left to fault: the request that would have lost it is gone.
  let release = () => {};
  const gate = new Promise<void>((resolve) => (release = resolve));
  const slow = indefinite("slow", async () => {
    await gate;
    return "done";
  });
  const seed = SEEDS.find((s) => phaseOf(s) === "after");
  const inj = injection(seed!);
  const pending = enter(inj, slow);
  inj.close();
  release();
  expect(await pending).toBe("done");
});

function phaseOf(seed: bigint): string | undefined {
  const draw = injection(seed).next("slow");
  return draw.kind === "fault" ? draw.fault.phase : undefined;
}

test("sites with one name share one stream", async () => {
  const a = indefinite("shared", () => 0);
  const b = indefinite("shared", () => 0);
  const c = indefinite("own", () => 0);
  const inj = injection(0n);
  for (const op of [a, b, c]) {
    await drive(inj, op);
  }
  expect(inj.calls()).toEqual(
    new Map([
      ["shared", 2],
      ["own", 1],
    ]),
  );
});

test("the wrapper keeps the function's name, length, this, and type", async () => {
  function add(this: { base: number }, x: number, y: number): number {
    return this.base + x + y;
  }
  const marked: typeof add = indefinite("add", add);
  expect([marked.name, marked.length]).toEqual(["add", 2]);
  expect(marked.call({ base: 1 }, 2, 3)).toBe(6);
  const counter = {
    n: 0,
    bump: indefinite("bump", function (this: { n: number }) {
      return ++this.n;
    }),
  };
  expect(counter.bump()).toBe(1);
});

test("a decorated method is a marked call", async () => {
  class Ledger {
    entries: number[] = [];

    @indefinite("ledger.add")
    add(amount: number): number {
      this.entries.push(amount);
      return this.entries.length;
    }

    @indefinite("ledger.save")
    async save(): Promise<string> {
      return "saved";
    }
  }
  const ledger = new Ledger();
  expect(ledger.add(5)).toBe(1);
  expect(await ledger.save()).toBe("saved");
  const inj = injection(3n);
  const outcomes = [];
  for (let i = 0; i < CALLS; i++) {
    const result = await drive(inj, () => ledger.add(i));
    outcomes.push("fault" in result ? `${result.fault.site} ${result.fault.phase}` : "ok");
    await drive(inj, () => ledger.save());
  }
  expect(inj.calls()).toEqual(
    new Map([
      ["ledger.add", CALLS],
      ["ledger.save", CALLS],
    ]),
  );
  expect(outcomes.some((o) => o !== "ok")).toBe(true);
  expect(ledger.add.name).toBe("add");
});

// --- Misuse is a bug: it throws at the line, in or out of a request ---

test("misuse throws a TypeError at the line", () => {
  const f = () => 0;
  for (const name of ["", "db commit", "a\nb", 7 as unknown as string]) {
    expect(() => indefinite(name, f)).toThrow(/a site name must be/);
    expect(() => indefinite(name)).toThrow(/a site name must be/);
  }
  expect(() => indefinite("x", null as unknown as () => void)).toThrow('"x" needs a function, got null');
  expect(() => indefinite("x", indefinite("y", f))).toThrow('"x" wraps a function already marked "y"');
  expect(() => indefinite("x", function* () {})).toThrow("generators aren't supported");
  expect(() => indefinite("x", async function* () {})).toThrow("generators aren't supported");
  // The decorator form, called on a plain function or a field.
  const decorate = indefinite("x") as unknown as (fn: unknown, context?: unknown) => unknown;
  expect(() => decorate(f)).toThrow('decorates class methods; to mark a function, call indefinite("x", fn)');
  expect(() => decorate(f, { kind: "field" })).toThrow("decorates class methods");
});
