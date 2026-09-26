/**
 * The same bank as `bank.ts`, served by Express 5. Same store, same sites,
 * same seeds, so the same faults.
 *
 *     INDEFINITE_ERRORS=1 bun examples/bank/express.ts
 */

import express, { type ErrorRequestHandler, type Express } from "express";

import { indefiniteMiddleware } from "../../src/node.ts";
import { Store } from "./bank.ts";

interface Deposit {
  account: string;
  amount: number;
  key?: string;
}

export function createExpressApp(store: Store, options: { indefiniteErrors: boolean }): Express {
  const app = express();
  if (options.indefiniteErrors) {
    app.use(indefiniteMiddleware()); // first, and never in production: any caller could fault the server
  }
  app.use(express.json());

  app.post("/deposits/unkeyed", (req, res) => {
    const body = req.body as Deposit;
    store.deposit(body.account, body.amount);
    res.json({ status: "ok" });
  });

  app.post("/deposits", (req, res) => {
    const body = req.body as Deposit;
    if (body.key === undefined) {
      res.status(422).json({ error: "a keyed deposit needs a key" });
      return;
    }
    store.depositOnce(body.key, body.account, body.amount);
    res.json({ status: "ok" });
  });

  app.get("/accounts/:account", (req, res) => {
    res.json({ balance: store.balance(req.params.account) });
  });

  // An ordinary error handler. Under injection, a fault lands here too, and
  // the middleware turns this response into the fault's.
  app.use(((_error, _req, res, _next) => {
    res.status(500).json({ error: "internal" });
  }) as ErrorRequestHandler);
  return app;
}

// The one place the environment is read.
if (import.meta.main) {
  const port = Number(process.env["PORT"] ?? 8000);
  createExpressApp(new Store(process.env["BANK_DB"] ?? ":memory:"), {
    indefiniteErrors: process.env["INDEFINITE_ERRORS"] === "1",
  }).listen(port, () => console.log(`bank listening on http://localhost:${port}/`));
}
