// The built package on real Node: `bun run test:node`, with `node` on PATH.
//
// bun test runs everything under Bun. This runs the built dist/ under Node,
// where Express apps actually live: the fetch middleware, the Express bank
// (with an in-memory store, since bun:sqlite is Bun's), and a fault mid-stream.
// A seed must do exactly what it does under Bun.

import assert from "node:assert/strict";
import { connect } from "node:net";

import express from "express";

import { indefinite, withIndefinite } from "../dist/index.js";
import { indefiniteMiddleware } from "../dist/node.js";

async function listen(app) {
  const server = app.listen(0, "127.0.0.1");
  await new Promise((resolve) => server.once("listening", resolve));
  return server;
}

// --- The fetch middleware: the README's three seeds ------------------------------

{
  const ledger = [];
  const add = indefinite("ledger.add", (amount) => ledger.push(amount));
  const service = withIndefinite(async () => {
    add(1);
    return new Response("ok");
  });
  const seen = [];
  for (const seed of ["70", "74", "0"]) {
    ledger.length = 0;
    const { status } = await service(new Request("http://bank/", { headers: { "x-indefinite-seed": seed } }));
    seen.push([seed, status, [...ledger]]);
  }
  assert.deepEqual(seen, [
    ["70", 500, []],
    ["74", 500, [1]],
    ["0", 200, [1]],
  ]);
}

// --- The Express bank: 39 of 50 unkeyed runs wrong, none keyed -------------------

function bank() {
  const balances = new Map();
  const applied = new Set();
  const add = (account, amount) => balances.set(account, (balances.get(account) ?? 0) + amount);
  const deposit = indefinite("store.deposit", add);
  const depositOnce = indefinite("store.deposit_once", (key, account, amount) => {
    if (!applied.has(key)) {
      applied.add(key);
      add(account, amount);
    }
  });
  const app = express();
  app.use(indefiniteMiddleware());
  app.use(express.json());
  app.post("/deposits/unkeyed", async (req, res) => {
    await null; // the injection survives an await
    deposit(req.body.account, req.body.amount);
    res.json({ status: "ok" });
  });
  app.post("/deposits", (req, res) => {
    depositOnce(req.body.key, req.body.account, req.body.amount);
    res.json({ status: "ok" });
  });
  app.get("/accounts/:account", (req, res) => res.json({ balance: balances.get(req.params.account) ?? 0 }));
  app.use((_error, _req, res, _next) => res.status(500).json({ error: "internal" }));
  return app;
}

async function runs(path, body) {
  const results = [];
  for (let run = 0; run < 50; run++) {
    const server = await listen(bank());
    const base = `http://127.0.0.1:${server.address().port}`;
    const faults = [];
    for (let step = 0; step < 20; step++) {
      for (let attempt = 0; attempt < 30; attempt++) {
        const response = await fetch(base + path, {
          method: "POST",
          headers: { "content-type": "application/json", "x-indefinite-seed": String(run * 1e6 + step * 1e3 + attempt) },
          body: JSON.stringify(body(step)),
        });
        await response.arrayBuffer();
        if (response.status === 200) break;
        assert.equal(response.status, 500);
        faults.push(response.headers.get("x-indefinite-fault"));
      }
    }
    const { balance } = await (await fetch(`${base}/accounts/alice`)).json();
    results.push({ balance, faults });
    server.close();
  }
  return results;
}

const unkeyed = await runs("/deposits/unkeyed", () => ({ account: "alice", amount: 1 }));
assert.equal(unkeyed.filter((r) => r.balance !== 20).length, 39);
assert.deepEqual(unkeyed[1], { balance: 21, faults: ["after store.deposit#0 (seed=1004000)"] });
const keyed = await runs("/deposits", (step) => ({ key: `deposit-${step}`, account: "alice", amount: 1 }));
assert.equal(keyed.filter((r) => r.balance !== 20).length, 0);

// --- Too late to answer: a fault mid-stream drops the connection -------------------

{
  const chunk = indefinite("app.stream", () => "chunk");
  const app = express();
  app.use(indefiniteMiddleware());
  app.get("/", async (_req, res) => {
    res.write("chunk");
    await new Promise((resolve) => setTimeout(resolve, 5));
    res.end(chunk());
  });
  app.use((_error, _req, _res, _next) => {}); // the middleware already dropped the connection
  const server = await listen(app);
  const outcomes = new Set();
  for (let seed = 0; seed < 40 && outcomes.size < 2; seed++) {
    outcomes.add(
      await new Promise((resolve) => {
        const socket = connect(server.address().port, "127.0.0.1");
        let text = "";
        socket.on("data", (data) => (text += data));
        socket.on("error", () => {});
        socket.on("close", () => resolve(text.endsWith("5\r\nchunk\r\n5\r\nchunk\r\n0\r\n\r\n") ? "complete" : "cut"));
        socket.write(`GET / HTTP/1.1\r\nhost: x\r\nx-indefinite-seed: ${seed}\r\nconnection: close\r\n\r\n`);
      }),
    );
  }
  server.close();
  assert.deepEqual([...outcomes].sort(), ["complete", "cut"]);
}

console.log(`ok: node ${process.version}`);
