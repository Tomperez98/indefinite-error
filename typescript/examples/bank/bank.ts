/**
 * A tiny bank: deposits into accounts, stored in SQLite.
 *
 * Two ways to deposit. `POST /deposits/unkeyed` just adds the amount;
 * `POST /deposits` carries an idempotency key and applies each key once.
 * Under indefinite errors a retrying client double-counts with the first and
 * never with the second, which `bank.test.ts` shows.
 *
 * Run it with faults on, and send a seed per request:
 *
 *     INDEFINITE_ERRORS=1 bun examples/bank/bank.ts
 */

import { Database } from "bun:sqlite";

import { indefinite, withIndefinite } from "../../src/index.ts";

/** The durable state. Its writes are the boundary where outcomes get lost. */
export class Store {
  readonly #db: Database;

  constructor(path = ":memory:") {
    this.#db = new Database(path);
    this.#db.run("CREATE TABLE IF NOT EXISTS accounts (id TEXT PRIMARY KEY, balance INT)");
    this.#db.run("CREATE TABLE IF NOT EXISTS applied (key TEXT PRIMARY KEY)");
  }

  // Mark each write that talks to the outside world. A fault lands right
  // before it (the write never happened) or right after it (it committed, but
  // the response is lost).

  @indefinite("store.deposit")
  deposit(account: string, amount: number): void {
    this.#add(account, amount);
  }

  @indefinite("store.deposit_once")
  depositOnce(key: string, account: string, amount: number): void {
    // One transaction: the key and the money, or neither.
    this.#db.transaction(() => {
      const applied = this.#db.run("INSERT INTO applied VALUES (?) ON CONFLICT DO NOTHING", [key]);
      if (applied.changes === 1) {
        this.#add(account, amount);
      }
    })();
  }

  balance(account: string): number {
    const row = this.#db.query<{ balance: number }, [string]>("SELECT balance FROM accounts WHERE id = ?").get(account);
    return row?.balance ?? 0;
  }

  #add(account: string, amount: number): void {
    this.#db.run(
      "INSERT INTO accounts VALUES (?, ?) ON CONFLICT (id) DO UPDATE SET balance = balance + excluded.balance",
      [account, amount],
    );
  }
}

interface Deposit {
  account: string;
  amount: number;
  key?: string;
}

export function createApp(store: Store, options: { indefiniteErrors: boolean }): (request: Request) => Promise<Response> {
  const app = async (request: Request): Promise<Response> => {
    const { pathname } = new URL(request.url);
    if (request.method === "POST" && pathname === "/deposits/unkeyed") {
      const body = (await request.json()) as Deposit;
      store.deposit(body.account, body.amount);
      return Response.json({ status: "ok" });
    }
    if (request.method === "POST" && pathname === "/deposits") {
      const body = (await request.json()) as Deposit;
      if (body.key === undefined) {
        return Response.json({ error: "a keyed deposit needs a key" }, { status: 422 });
      }
      store.depositOnce(body.key, body.account, body.amount);
      return Response.json({ status: "ok" });
    }
    const account = /^\/accounts\/([^/]+)$/.exec(pathname)?.[1];
    if (request.method === "GET" && account !== undefined) {
      return Response.json({ balance: store.balance(decodeURIComponent(account)) });
    }
    return new Response("not found", { status: 404 });
  };
  // Never in production: any caller could fault the server.
  return options.indefiniteErrors ? withIndefinite(app) : app;
}

// The one place the environment is read.
if (import.meta.main) {
  const server = Bun.serve({
    port: Number(process.env["PORT"] ?? 8000),
    fetch: createApp(new Store(process.env["BANK_DB"] ?? ":memory:"), {
      indefiniteErrors: process.env["INDEFINITE_ERRORS"] === "1",
    }),
  });
  console.log(`bank listening on ${server.url}`);
}
