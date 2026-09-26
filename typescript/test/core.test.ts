/** The pure core: every decision is a function of (seed, site, n). */

import { describe, expect, test } from "bun:test";
import fc from "fast-check";

import {
  checkSite,
  decide,
  INT64_MAX,
  INT64_MIN,
  MODES,
  modeOf,
  pick,
  RATES,
  rateOf,
  unit,
  type Mode,
  type Phase,
} from "../src/core.ts";
import { enter, Fault, IndefiniteFault } from "../src/injection.ts";
import { indefinite } from "../src/site.ts";
import { captured, injection, specRows, specText } from "./helpers.ts";

// --- The schedule: saved seeds keep replaying the same faults, in any language ---

const SYMBOL = { none: ".", before: "b", after: "a" } as const;

test("the schedule matches the spec", () => {
  // A change here breaks every seed users saved from a failing run, in every language.
  const rows = specRows("schedule.tsv");
  expect(rows.length).toBe(80);
  for (const [seedCell, siteCell, mode, rate, calls] of rows) {
    const seed = BigInt(seedCell!);
    const site: string = JSON.parse(siteCell!);
    expect(JSON.stringify(site)).toBe(siteCell!); // a JSON string, written as the spec writes it
    const r = rateOf(seed);
    const m = modeOf(seed, site);
    const drawn = Array.from({ length: calls!.length }, (_, n) => SYMBOL[decide(seed, r, m, site, n) ?? "none"]);
    expect([m, String(r), drawn.join("")]).toEqual([mode!, rate!, calls!]);
  }
});

test("fault lines match the spec", async () => {
  // Call n of site faults with the spec's payload, and writes it on the line.
  for (const [seedCell, siteCell, nCell, payload] of specRows("faults.tsv")) {
    const site: string = JSON.parse(siteCell!);
    const n = Number(nCell);
    const op = indefinite(site, () => {});
    const { log, text } = captured();
    const inj = injection(BigInt(seedCell!), log);
    const faults: Fault[] = [];
    for (let i = 0; i <= n; i++) {
      try {
        enter(inj, op);
      } catch (error) {
        expect(error).toBeInstanceOf(IndefiniteFault);
        faults.push((error as IndefiniteFault).fault);
        inj.resume();
      }
    }
    const last = faults.at(-1);
    expect([last?.n, String(last)]).toEqual([n, payload!]);
    expect(text().split("\n").at(-2)).toBe(`indefinite-error: ${payload}`);
  }
});

test("the spec's comments say where the schedule comes from", () => {
  expect(specText("schedule.tsv")).toStartWith("# The fault schedule every implementation must reproduce");
});

test("parts cannot run together", () => {
  // Parts are separated before hashing, so ("1", "2x") differs from ("12", "x").
  expect(unit("mode", 1n, "2x")).not.toBe(unit("mode", 12n, "x"));
  expect(unit("call", 0n, "a", 11)).not.toBe(unit("call", 0n, "a1", 1));
});

test("integers are written in decimal, whatever their type", () => {
  expect(unit("rate", -1n)).toBe(unit("rate", "-1"));
  expect(unit("call", 0n, "a", 7)).toBe(unit("call", "0", "a", "7"));
  expect(unit("rate", INT64_MIN)).toBe(unit("rate", "-9223372036854775808"));
  expect(() => unit("call", 0n, "a", 1.5)).toThrow("is not an integer");
});

// --- Decision table: every row, and exactly one row per input -------------------

type Expected = Phase | "coin" | undefined;
const ROWS: ReadonlyArray<[Mode, boolean, Expected]> = [
  ["off", false, undefined],
  ["off", true, undefined],
  ["before", false, undefined],
  ["before", true, "before"],
  ["after", false, undefined],
  ["after", true, "after"],
  ["both", false, undefined],
  ["both", true, "coin"], // before if the phase draw < 0.5, else after
];

describe.each(ROWS)("the decision table: mode %s, draw under the rate %p", (mode, draw, expected) => {
  test(`is ${expected}`, () => {
    const rate = draw ? 1 : 0; // every draw is < 1; none is < 0
    for (let seed = 0n; seed < 5n; seed++) {
      for (const site of ["a", "b"]) {
        for (let n = 0; n < 100; n++) {
          const coin: Phase = unit("phase", seed, site, n) < 0.5 ? "before" : "after";
          expect(decide(seed, rate, mode, site, n)).toBe(expected === "coin" ? coin : expected);
        }
      }
    }
  });
});

test("the decision table is complete and unambiguous", () => {
  for (const mode of MODES) {
    for (const draw of [false, true]) {
      expect(ROWS.filter(([m, d]) => m === mode && d === draw)).toHaveLength(1);
    }
  }
});

test("a draw under the rate is what faults", () => {
  for (let seed = 0n; seed < 5n; seed++) {
    for (let n = 0; n < 200; n++) {
      expect(decide(seed, 0.3, "before", "site", n) !== undefined).toBe(unit("call", seed, "site", n) < 0.3);
    }
  }
});

/** The next float64 after `x`, towards +infinity or 0. */
function nextAfter(x: number, direction: 1 | -1): number {
  const view = new DataView(new ArrayBuffer(8));
  view.setFloat64(0, x);
  view.setBigUint64(0, view.getBigUint64(0) + BigInt(direction));
  return view.getFloat64(0);
}

test("a draw equal to the rate does not fault", () => {
  // The fault window is [0, rate): rate 0 never faults, 1 always does.
  const draw = unit("call", 0n, "site", 0);
  expect(decide(0n, draw, "before", "site", 0)).toBeUndefined();
  expect(decide(0n, nextAfter(draw, 1), "before", "site", 0)).toBe("before");
});

test("pick covers the whole unit interval", () => {
  const options = ["a", "b", "c", "d"];
  expect(pick(options, 0)).toBe("a");
  expect(pick(options, nextAfter(1, -1))).toBe("d");
  expect([0, 1, 2, 3].map((k) => pick(options, k / 4))).toEqual(options);
});

test.each([1, -0.1, Number.NaN])("pick throws outside the unit interval: %p", (u) => {
  // A draw outside [0, 1) is a broken hash, not a choice: crash at the line.
  expect(() => pick(["a", "b"], u)).toThrow(/bug: u=.* out of \[0, 1\)/);
});

// --- Statistics: the seed's choices have the shape the docs promise ---------------
// Deterministic (the "randomness" is a hash), so these can't flake. Bounds are 5 sigma.

function within(observed: number, total: number, p: number): boolean {
  return Math.abs(observed - total * p) <= 5 * Math.sqrt(total * p * (1 - p));
}

function seedWithRate(rate: number): bigint {
  let seed = 0n;
  while (rateOf(seed) !== rate) {
    seed++;
  }
  return seed;
}

test.each([...RATES])("the observed fault rate matches rate %p", (rate) => {
  const seed = seedWithRate(rate);
  const total = 20_000;
  let faults = 0;
  for (let n = 0; n < total; n++) {
    faults += decide(seed, rate, "before", "site", n) === undefined ? 0 : 1;
  }
  expect(within(faults, total, rate)).toBe(true);
});

test("both mode splits evenly", () => {
  const seed = seedWithRate(0.5);
  let before = 0;
  let faults = 0;
  for (let n = 0; n < 40_000; n++) {
    const phase = decide(seed, 0.5, "both", "site", n);
    faults += phase === undefined ? 0 : 1;
    before += phase === "before" ? 1 : 0;
  }
  expect(within(before, faults, 0.5)).toBe(true);
});

function expectEven<T>(values: readonly T[], options: readonly T[]): void {
  for (const option of options) {
    const count = values.filter((v) => v === option).length;
    expect(within(count, values.length, 1 / options.length)).toBe(true);
  }
}

test("modes and rates are spread evenly", () => {
  // Swarm testing needs every mode and every rate, about equally often.
  const modes: Mode[] = [];
  for (let seed = 0n; seed < 1000n; seed++) {
    for (let k = 0; k < 4; k++) {
      modes.push(modeOf(seed, `site${k}`));
    }
  }
  expectEven(modes, MODES);
  expectEven(
    Array.from({ length: 4000 }, (_, s) => rateOf(BigInt(s))),
    RATES,
  );
});

// --- Site names -------------------------------------------------------------------

test("a site name must be printable with no whitespace", () => {
  for (const name of ["", "a b", "a\tb", "a\nb", "a\x00b", "a\u00a0b", "a\u200bb", "a\u2028b", "\ud800", 7, undefined]) {
    expect(() => checkSite(name)).toThrow(TypeError);
  }
  for (const name of ["db.commit", "ünïcode.sïte", "POST:/deposits#key", "中", "🙂"]) {
    expect(() => checkSite(name)).not.toThrow();
  }
});

// --- Properties -------------------------------------------------------------------

const int64 = fc.bigInt({ min: INT64_MIN, max: INT64_MAX });
const part = fc.oneof(fc.string(), fc.bigInt(), fc.integer());
/** A valid site name of 1 to 8 characters, some of them non-ASCII. */
const siteName = fc.string({ unit: "grapheme", minLength: 1, maxLength: 8 }).filter((s) => /^[^\p{C}\p{Z}]+$/u.test(s));

test("unit is in the unit interval", () => {
  fc.assert(fc.property(fc.array(part, { maxLength: 5 }), (parts) => unit(...parts) >= 0 && unit(...parts) < 1));
});

test("decide only returns phases the mode allows", () => {
  const allowed: Record<Mode, ReadonlyArray<Phase | undefined>> = {
    off: [undefined],
    before: [undefined, "before"],
    after: [undefined, "after"],
    both: [undefined, "before", "after"],
  };
  fc.assert(
    fc.property(int64, fc.string(), fc.nat(), fc.double({ min: 0, max: 1 }), fc.constantFrom(...MODES), (seed, site, n, rate, mode) =>
      allowed[mode].includes(decide(seed, rate, mode, site, n)),
    ),
  );
});

test("the injection matches a reference model", () => {
  // Any interleaving of calls across sites: the faults are exactly the model's.
  // The model keeps one counter per site and asks the pure core, so a site's
  // faults can't depend on its neighbours, or on anything but the seed.
  const program = fc
    .uniqueArray(siteName, { minLength: 1, maxLength: 4 })
    .chain((sites) => fc.tuple(fc.constant(sites), fc.array(fc.constantFrom(...sites), { maxLength: 60 })));
  fc.assert(
    fc.property(int64, program, (seed, [sites, calls]) => {
      const ops = new Map(sites.map((s) => [s, indefinite(s, () => {})]));
      const inj = injection(seed);
      const seen = calls.map((site) => {
        try {
          enter(inj, ops.get(site)!);
          return undefined;
        } catch (error) {
          inj.resume();
          return String((error as IndefiniteFault).fault);
        }
      });
      const counts = new Map<string, number>();
      const expected = calls.map((site) => {
        const n = counts.get(site) ?? 0;
        counts.set(site, n + 1);
        const phase = decide(seed, rateOf(seed), modeOf(seed, site), site, n);
        return phase === undefined ? undefined : String(new Fault(seed, site, n, phase));
      });
      expect(seen).toEqual(expected);
      expect(inj.calls()).toEqual(counts);
    }),
  );
});
