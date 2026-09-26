/** The Node middleware, under a real Express 5 server: the same table, the same seed header. */

import { afterAll, expect, test } from "bun:test";
import express, { type ErrorRequestHandler, type Express } from "express";
import { createServer, type RequestListener, type Server } from "node:http";
import type { AddressInfo } from "node:net";
import { connect } from "node:net";

import { current } from "../src/injection.ts";
import { FAULT_HEADER, SEED_HEADER } from "../src/middleware.ts";
import { indefiniteMiddleware, nodeMiddleware } from "../src/node-middleware.ts";
import { indefinite } from "../src/site.ts";
import { captured, discard, SEEDS, specRows } from "./helpers.ts";

const servers: Server[] = [];
afterAll(() => servers.forEach((s) => s.close()));

/** Listens on a free port; the app's error handler stays quiet in test output. */
async function listen(app: Express | RequestListener): Promise<number> {
  if ("use" in app) {
    app.use(((error, _req, res, next) => {
      if (res.headersSent) {
        return next(error); // Express drops the connection
      }
      res.status(500).json({ error: String(error) }); // what a real app's handler does
    }) as ErrorRequestHandler);
  }
  const server = createServer(app as RequestListener);
  servers.push(server);
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  return (server.address() as AddressInfo).port;
}

/** An Express app that writes 1 to its ledger, then answers 201. */
function writingApp(log = discard): { ledger: number[]; app: Express } {
  const ledger: number[] = [];
  const write = indefinite("app.write", async (x: number) => {
    ledger.push(x);
  });
  const app = express();
  app.use(nodeMiddleware(log));
  app.post("/deposits", async (_req, res) => {
    await write(1);
    res.status(201).set("x-app", "yes").send("created");
  });
  return { ledger, app };
}

async function post(port: number, seed?: bigint | string): Promise<Response> {
  const headers: Record<string, string> = seed === undefined ? {} : { [SEED_HEADER]: String(seed) };
  const response = await fetch(`http://127.0.0.1:${port}/deposits`, { method: "POST", headers });
  await response.clone().arrayBuffer();
  return response;
}

/** A header value as the UTF-8 its bytes spell: fetch reads each byte as a character. */
function utf8(value: string | null): string | null {
  return value === null ? null : new TextDecoder().decode(Uint8Array.from(value, (c) => c.charCodeAt(0)));
}

function summary(response: Response): [number, string | null] {
  return [response.status, utf8(response.headers.get(FAULT_HEADER))];
}

// --- The table ------------------------------------------------------------------

test("rows match the table", async () => {
  // No fault: the real response. before: 500, unchanged. after: 500, changed.
  const { ledger, app } = writingApp();
  const port = await listen(app);
  const rows = new Set<string>();
  for (const seed of SEEDS.slice(0, 100)) {
    ledger.length = 0;
    const [status, fault] = summary(await post(port, seed));
    rows.add(`${status} ${fault?.split(" ")[0] ?? "none"} ${ledger.length === 1 ? "changed" : "unchanged"}`);
    if (fault !== null) {
      expect(fault).toEndWith(`(seed=${seed})`);
    }
  }
  expect([...rows].sort()).toEqual(["201 none changed", "500 after changed", "500 before unchanged"]);
});

test("the same seed faults as it does behind the fetch middleware", async () => {
  // One contract: app.write, seed 13, the same answer in either adapter.
  const { app } = writingApp();
  const port = await listen(app);
  const { middleware } = await import("../src/middleware.ts");
  const write = indefinite("app.write", async () => {});
  const fetchApp = middleware(async () => {
    await write();
    return new Response("created", { status: 201 });
  }, discard);
  for (const seed of SEEDS.slice(0, 100)) {
    const viaFetch = await fetchApp(new Request("http://x/", { headers: { [SEED_HEADER]: String(seed) } }));
    expect(summary(await post(port, seed))).toEqual([viaFetch.status, viaFetch.headers.get(FAULT_HEADER)]);
  }
});

test("the fault response is fresh", async () => {
  // An empty body, and none of the headers the lost response had.
  const { app } = writingApp();
  const port = await listen(app);
  for (const seed of SEEDS) {
    const response = await post(port, seed);
    if (response.status === 500) {
      expect(response.headers.get("x-app")).toBeNull();
      expect(response.headers.get("content-type")).toBeNull();
      expect(await response.text()).toBe("");
      return;
    }
  }
  throw new Error("no seed faulted");
});

test("a fault the route swallows still ends the request", async () => {
  // A retry loop that catches everything: the response it sends is still lost.
  const ledger: number[] = [];
  const write = indefinite("app.write", async () => {
    ledger.push(1);
  });
  const app = express();
  app.use(nodeMiddleware(discard));
  app.post("/deposits", async (_req, res) => {
    for (let attempt = 0; attempt < 3; attempt++) {
      try {
        await write();
        res.status(201).send("created");
        return;
      } catch {
        // retry
      }
    }
    res.status(503).json({ error: "gave up" });
  });
  const port = await listen(app);
  const rows = new Set<string>();
  for (const seed of SEEDS.slice(0, 100)) {
    ledger.length = 0;
    const [status, fault] = summary(await post(port, seed));
    rows.add(`${status} ${fault?.split(" ")[0] ?? "none"} ${ledger.length}`);
  }
  expect([...rows].sort()).toEqual(["201 none 1", "500 after 1", "500 before 0"]); // no retry reran it
});

test("every write after the fault goes nowhere, and still calls back", async () => {
  // The first write answers for the fault; the rest are the lost response's.
  const write = indefinite("app.write", () => {});
  const called: string[] = [];
  const app = express();
  app.use(nodeMiddleware(discard));
  app.post("/deposits", (_req, res) => {
    try {
      write();
    } catch {
      // swallowed
    }
    res.write("a", () => called.push("write"));
    res.write("b");
    res.end("c", () => called.push("end"));
  });
  const port = await listen(app);
  for (const seed of SEEDS) {
    called.length = 0;
    const response = await post(port, seed);
    if (response.status === 500) {
      expect([await response.text(), called.sort()]).toEqual(["", ["end", "write"]]);
      return;
    }
    expect(await response.text()).toBe("abc");
  }
  throw new Error("no seed faulted");
});

test("Express's own error handler answers with the fault", async () => {
  // A rejected async route goes to the error handler; its 500 becomes the fault's.
  const write = indefinite("app.write", async () => {});
  const app = express();
  app.use(nodeMiddleware(discard));
  app.post("/deposits", async (_req, res) => {
    await write();
    res.send("ok");
  });
  const server = createServer(app); // no error handler of ours: Express's finalhandler
  servers.push(server);
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  const port = (server.address() as AddressInfo).port;
  const statuses = new Set<string>();
  for (const seed of SEEDS.slice(0, 60)) {
    const response = await post(port, seed);
    statuses.add(`${response.status} ${response.headers.has(FAULT_HEADER)} ${(await response.text()).length}`);
  }
  expect([...statuses].sort()).toEqual(["200 false 2", "500 true 0"]);
});

test("request faults write the line", async () => {
  const { log, text } = captured();
  const { app } = writingApp(log);
  const port = await listen(app);
  const faults = [];
  for (const seed of SEEDS.slice(0, 100)) {
    const [, fault] = summary(await post(port, seed));
    if (fault !== null) {
      faults.push(`indefinite-error: ${fault}\n`);
    }
  }
  expect(faults.length).toBeGreaterThan(0);
  expect(text()).toBe(faults.join(""));
});

test("the fault header is the payload's UTF-8", async () => {
  const site = "中.ünïcode";
  const op = indefinite(site, () => {});
  const app = express();
  app.use(nodeMiddleware(discard));
  app.post("/deposits", (_req, res) => {
    op();
    res.send("ok");
  });
  const port = await listen(app);
  for (const seed of SEEDS) {
    const [status, fault] = summary(await post(port, seed));
    if (status === 500) {
      expect(fault).toMatch(new RegExp(`^(before|after) ${site}#0 \\(seed=${seed}\\)$`));
      return;
    }
  }
  throw new Error("no seed faulted");
});

// --- The injection follows the request --------------------------------------------

/** One HTTP/1.1 request on its own connection, read until the server closes it. */
function wire(port: number, head: string, body: string[] = [], pause = 0): Promise<{ status: number; text: string; dropped: boolean }> {
  return new Promise((resolve) => {
    const socket = connect(port, "127.0.0.1");
    const chunks: Buffer[] = [];
    let dropped = false;
    socket.on("data", (chunk: Buffer) => chunks.push(chunk));
    socket.on("error", () => (dropped = true));
    socket.on("close", () => {
      const text = Buffer.concat(chunks).toString("latin1");
      resolve({ status: Number(text.split(" ")[1] ?? 0), text, dropped });
    });
    socket.write(head);
    body.forEach((part, i) => setTimeout(() => socket.write(part), pause * (i + 1)));
  });
}

test("a slow body through express.json() keeps the injection", async () => {
  // The route runs from the body parser's stream callbacks, later.
  const seen: boolean[] = [];
  const app = express();
  app.use(nodeMiddleware(discard));
  app.use(express.json());
  app.post("/", (req, res) => {
    seen.push(current() !== undefined);
    res.json(req.body);
  });
  const port = await listen(app);
  const head = `POST / HTTP/1.1\r\nhost: x\r\n${SEED_HEADER}: 1\r\ncontent-type: application/json\r\ncontent-length: 7\r\nconnection: close\r\n\r\n`;
  const response = await wire(port, head, ['{"a"', ":1}"], 20);
  expect([response.status, response.text.endsWith('{"a":1}'), seen]).toEqual([200, true, [true]]);
});

test("too late to answer: the connection drops", async () => {
  // "chunk", a pause (so the headers go out), a marked call, then "chunk".
  const chunk = indefinite("app.stream", () => "chunk");
  const app = express();
  app.use(nodeMiddleware(discard));
  app.get("/", async (_req, res) => {
    res.write("chunk");
    await Bun.sleep(5);
    res.end(chunk());
  });
  const port = await listen(app);
  const outcomes = new Set<string>();
  for (let seed = 0n; outcomes.size < 2 && seed < 40n; seed++) {
    const response = await wire(port, `GET / HTTP/1.1\r\nhost: x\r\n${SEED_HEADER}: ${seed}\r\nconnection: close\r\n\r\n`);
    const complete = response.text.endsWith("\r\n\r\n5\r\nchunk\r\n5\r\nchunk\r\n0\r\n\r\n"); // both chunks, then the end
    outcomes.add(`${response.status} ${complete ? "complete" : "cut"}`);
  }
  expect([...outcomes].sort()).toEqual(["200 complete", "200 cut"]);
});

test("the injection closes when the response finishes", async () => {
  // A timer that outlives the response calls through with no faults.
  const ran: number[] = [];
  const late = indefinite("late", (i: number) => ran.push(i));
  let leaked: Promise<void> | undefined;
  const app = express();
  app.use(nodeMiddleware(discard));
  app.post("/deposits", (_req, res) => {
    leaked = Bun.sleep(5).then(() => {
      for (let i = 0; i < 50; i++) late(i);
    });
    res.send("ok");
  });
  const port = await listen(app);
  for (const seed of SEEDS.slice(0, 20)) {
    ran.length = 0;
    expect((await post(port, seed)).status).toBe(200);
    await leaked;
    expect(ran).toHaveLength(50);
  }
});

// --- What the middleware passes through untouched ---------------------------------------

test("passes through without a seed", async () => {
  const { ledger, app } = writingApp();
  const port = await listen(app);
  for (let i = 0; i < 20; i++) {
    const response = await post(port);
    expect([...summary(response), response.headers.get("x-app")]).toEqual([201, null, "yes"]);
  }
  expect(ledger).toHaveLength(20);
});

test("works on a bare http server", async () => {
  const mw = indefiniteMiddleware();
  const bare: RequestListener = (req, res) => mw(req, res, () => res.end(current() === undefined ? "outside" : "inside"));
  const port = await listen(bare);
  expect(await (await post(port, 0n)).text()).toBe("inside");
  expect(await (await post(port)).text()).toBe("outside");
});

// --- The seed header: spec/seed-header.tsv ---------------------------------------------

test("the seed header matches the spec, byte for byte on the wire", async () => {
  // HTTP strips a value's surrounding whitespace, so " 1" and "1 " reach the app as "1".
  const seeds: bigint[] = [];
  const app = express();
  app.use(nodeMiddleware(discard));
  app.get("/", (_req, res) => {
    seeds.push(current()!.seed);
    res.status(204).end();
  });
  const port = await listen(app);
  for (const [valuesCell, outcome] of specRows("seed-header.tsv")) {
    const values: string[] = JSON.parse(valuesCell!);
    const lines = values.map((v) => `${SEED_HEADER}: ${Buffer.from(v, "utf8").toString("latin1")}\r\n`).join("");
    seeds.length = 0;
    const response = await wire(port, `GET / HTTP/1.1\r\nhost: x\r\n${lines}connection: close\r\n\r\n`);
    const trimmed = values.length === 1 && values[0] !== values[0]!.trim();
    const want = outcome === "400" && !trimmed ? [400, []] : [204, [trimmed ? BigInt(values[0]!.trim()) : BigInt(outcome!)]];
    expect([values, response.status, seeds]).toEqual([values, ...want]);
  }
});

test("a malformed seed is a 400, and the app never runs", async () => {
  const { ledger, app } = writingApp();
  const port = await listen(app);
  for (const raw of ["abc", "1".repeat(65), "1.5"]) {
    const response = await post(port, raw);
    expect([response.status, response.headers.get("content-type"), await response.text()]).toEqual([
      400,
      "text/plain; charset=utf-8",
      "X-Indefinite-Seed must be a decimal int64",
    ]);
  }
  expect(ledger).toEqual([]);
});

test("installed twice is a loud misconfiguration", async () => {
  const { app } = writingApp();
  const twice = express();
  twice.use(nodeMiddleware(discard));
  twice.use(app);
  const port = await listen(twice);
  const response = await post(port, 1n);
  expect([response.status, await response.json()]).toEqual([
    500,
    { error: "Error: indefinite: a request seeded 1 inside one seeded 1: is withIndefinite installed twice?" },
  ]);
});
