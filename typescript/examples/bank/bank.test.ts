/**
 * A retrying client must keep the bank's books right under indefinite errors.
 *
 * Each run makes DEPOSITS deposits of 1 against a fresh bank, every request
 * with its own seed, retrying whenever the outcome is indefinite -- as a real
 * client would after a timeout. Then it checks one invariant: the balance is
 * exactly DEPOSITS. It runs twice: against the fetch handler in `bank.ts`,
 * called directly, and against the Express app in `express.ts`, over HTTP.
 *
 *     bun test ./examples/bank
 */

import { afterAll, describe, expect, test } from "bun:test";
import type { Server } from "node:http";
import type { AddressInfo } from "node:net";

import { createApp, Store } from "./bank.ts";
import { createExpressApp } from "./express.ts";

const RUNS = Array.from({ length: 50 }, (_, run) => run);
const DEPOSITS = 20;
const ATTEMPTS = 30; // a seed may fault half its requests: retry until one gets through

/** One seed per request, derived from the run: replay a run, replay its faults. */
function seed(run: number, step: number, attempt: number): number {
  return run * 1_000_000 + step * 1_000 + attempt;
}

/** A fresh bank with faults on, as a client sees it: a path in, a response out. */
type Bank = (path: string, init?: RequestInit) => Promise<Response>;

const servers: Server[] = [];
afterAll(() => servers.forEach((s) => s.close()));

const BANKS: ReadonlyArray<{ name: string; open: () => Promise<Bank> }> = [
  {
    name: "fetch handler",
    open: async () => {
      const app = createApp(new Store(), { indefiniteErrors: true });
      return (path, init) => app(new Request(`http://bank${path}`, init));
    },
  },
  {
    name: "Express",
    open: async () => {
      const server = createExpressApp(new Store(), { indefiniteErrors: true }).listen(0, "127.0.0.1");
      servers.push(server);
      await new Promise((resolve) => server.once("listening", resolve));
      const { port } = server.address() as AddressInfo;
      return (path, init) => fetch(`http://127.0.0.1:${port}${path}`, init);
    },
  },
];

/** What one run did: its final balance, and every fault the server reported. */
interface Run {
  balance: number;
  faults: string[];
}

function deposit(body: object, seed: number): RequestInit {
  return {
    method: "POST",
    headers: { "content-type": "application/json", "x-indefinite-seed": String(seed) },
    body: JSON.stringify(body),
  };
}

async function runDeposits(bank: Bank, run: number, path: string, body: (step: number) => object): Promise<Run> {
  const faults: string[] = [];
  for (let step = 0; step < DEPOSITS; step++) {
    let attempt = 0;
    for (; attempt < ATTEMPTS; attempt++) {
      const response = await bank(path, deposit(body(step), seed(run, step, attempt)));
      await response.arrayBuffer();
      if (response.status === 200) {
        break;
      }
      // Indefinite: it may or may not have happened. The fault header is for
      // us, debugging; the client logic must not read it. Retry.
      expect(response.status).toBe(500);
      faults.push(response.headers.get("x-indefinite-fault")!);
    }
    if (attempt === ATTEMPTS) {
      throw new Error(`step ${step} never got through in ${ATTEMPTS} attempts`);
    }
  }
  const { balance } = (await (await bank("/accounts/alice")).json()) as { balance: number };
  return { balance, faults };
}

// The key is fixed per deposit, not per attempt: a retry is the same deposit.
const keyed = (step: number) => ({ key: `deposit-${step}`, account: "alice", amount: 1 });
const unkeyed = () => ({ account: "alice", amount: 1 });

describe.each([...BANKS])("$name", ({ open }) => {
  test("keyed deposits count exactly once", async () => {
    for (const run of RUNS) {
      const result = await runDeposits(await open(), run, "/deposits", keyed);
      expect([run, result.balance]).toEqual([run, DEPOSITS]);
    }
  });

  test("unkeyed deposits double-count, and the faults say why", async () => {
    // The bug this library exists to find, found: an AFTER fault, then a retry.
    const broken: Run[] = [];
    for (const run of RUNS) {
      const result = await runDeposits(await open(), run, "/deposits/unkeyed", unkeyed);
      if (result.balance !== DEPOSITS) {
        broken.push(result);
      }
      if (run === 1) {
        // The run the README walks through.
        expect(result).toEqual({ balance: 21, faults: ["after store.deposit#0 (seed=1004000)"] });
      }
    }
    expect(broken.length).toBe(39); // as the Python and Rust banks: one seed, one outcome
    for (const result of broken) {
      expect(result.balance).toBeGreaterThan(DEPOSITS); // retries only ever add
      expect(result.faults.some((f) => f.startsWith("after store.deposit#"))).toBe(true);
    }
  });
});
