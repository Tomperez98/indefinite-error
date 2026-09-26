/** The middleware: one injection per request, the table it answers by, and its seed header. */

import { afterAll, expect, test } from "bun:test";

import { current } from "../src/injection.ts";
import { FAULT_HEADER, middleware, parseSeed, SEED_HEADER, TooLate, withIndefinite } from "../src/middleware.ts";
import { indefinite } from "../src/site.ts";
import { captured, DefiniteError, discard, SEEDS, specRows } from "./helpers.ts";

type Handler = (request: Request) => Response | Promise<Response>;

function seeded(seed: bigint | string | undefined): Request {
  const headers = new Headers({ accept: "*/*" });
  if (seed !== undefined) {
    headers.set(SEED_HEADER, String(seed));
  }
  return new Request("http://test/deposits", { method: "POST", headers });
}

/** What a client can tell about a response: its status, and the fault it names. */
function summary(response: Response): [number, string | null] {
  return [response.status, response.headers.get(FAULT_HEADER)];
}

/** An app that writes 1 to its ledger, then answers 201. */
function writingApp(): { ledger: number[]; app: Handler } {
  const ledger: number[] = [];
  const write = indefinite("app.write", async (x: number) => {
    ledger.push(x);
  });
  const app = async () => {
    await write(1);
    return new Response("created", { status: 201, headers: { "x-app": "yes" } });
  };
  return { ledger, app };
}

// --- The table ------------------------------------------------------------------

test("rows match the table", async () => {
  // No fault: the real response. before: 500, unchanged. after: 500, changed.
  const { ledger, app } = writingApp();
  const serve = middleware(app, discard);
  const rows = new Set<string>();
  for (const seed of SEEDS) {
    ledger.length = 0;
    const [status, fault] = summary(await serve(seeded(seed)));
    rows.add(`${status} ${fault?.split(" ")[0] ?? "none"} ${ledger.length === 1 ? "changed" : "unchanged"}`);
    if (fault !== null) {
      expect(fault).toEndWith(`(seed=${seed})`);
    }
  }
  expect([...rows].sort()).toEqual(["201 none changed", "500 after changed", "500 before unchanged"]);
});

test("the fault response is fresh", async () => {
  // An empty body, and none of the headers the lost response had.
  const { app } = writingApp();
  const serve = middleware(app, discard);
  for (const seed of SEEDS) {
    const response = await serve(seeded(seed));
    if (response.status === 500) {
      expect([...response.headers.keys()]).toEqual([FAULT_HEADER]);
      expect(await response.text()).toBe("");
      return;
    }
  }
  throw new Error("no seed faulted");
});

test("the seed is a whole-request input", async () => {
  // The same seed takes the same path at request 1 or request 100.
  const { app } = writingApp();
  const serve = middleware(app, discard);
  const answers = async () => Promise.all(SEEDS.slice(0, 50).map(async (s) => summary(await serve(seeded(s)))));
  const first = await answers();
  for (let other = 1000n; other < 1100n; other++) {
    await serve(seeded(other));
  }
  expect(await answers()).toEqual(first);
});

test("a fault ends one request, not its neighbours", async () => {
  // Concurrent requests: each gets the response it gets alone.
  const { app } = writingApp();
  const serve = middleware(async (request: Request) => {
    await Bun.sleep(Math.random() * 2); // interleave with the other requests
    return app(request);
  }, discard);
  const alone = [];
  for (const seed of SEEDS) {
    alone.push(summary(await serve(seeded(seed))));
  }
  const together = await Promise.all(SEEDS.map(async (seed) => summary(await serve(seeded(seed)))));
  expect(together).toEqual(alone);
});

test("a fault the handler swallows still ends the request", async () => {
  // A catch-all, a retry loop, a framework's error handler: the response is still lost.
  const { ledger, app } = writingApp();
  const serve = middleware(async (request: Request) => {
    for (let attempt = 0; attempt < 3; attempt++) {
      try {
        return await app(request);
      } catch {
        // retry
      }
    }
    return new Response("gave up", { status: 503 });
  }, discard);
  const rows = new Set<string>();
  for (const seed of SEEDS) {
    ledger.length = 0;
    const [status, fault] = summary(await serve(seeded(seed)));
    rows.add(`${status} ${fault?.split(" ")[0] ?? "none"} ${ledger.length}`);
  }
  expect([...rows].sort()).toEqual(["201 none 1", "500 after 1", "500 before 0"]); // no retry reran it
});

test("a fault outranks an error the handler throws after it", async () => {
  const { app } = writingApp();
  const serve = middleware(async (request: Request) => {
    try {
      return await app(request);
    } catch (error) {
      throw new DefiniteError("wrapped", { cause: error });
    }
  }, discard);
  const statuses = new Set<number>();
  for (const seed of SEEDS) {
    statuses.add((await serve(seeded(seed))).status);
  }
  expect([...statuses].sort()).toEqual([201, 500]);
});

test("request faults write the line", async () => {
  const { app } = writingApp();
  const { log, text } = captured();
  const serve = middleware(app, log);
  const faults = [];
  for (const seed of SEEDS) {
    const [, fault] = summary(await serve(seeded(seed)));
    if (fault !== null) {
      faults.push(`indefinite-error: ${fault}\n`);
    }
  }
  expect(faults.length).toBeGreaterThan(0);
  expect(text()).toBe(faults.join(""));
});

// --- The body streams inside the request ---------------------------------------------

/** "chunk", then a marked call, then "chunk": a site call mid-response. */
function streamingApp(): Handler {
  const chunk = indefinite("app.stream", () => "chunk");
  return () => {
    const encoder = new TextEncoder();
    let sent = 0;
    const body = new ReadableStream<Uint8Array>({
      async pull(controller) {
        if (sent === 2) {
          controller.close();
          return;
        }
        if (sent === 1) {
          await Bun.sleep(1); // so a server flushes the first chunk
        }
        controller.enqueue(encoder.encode(sent === 0 ? "chunk" : chunk()));
        sent++;
      },
    });
    return new Response(body, { status: 200 });
  };
}

test("too late to answer: the body errors", async () => {
  const serve = middleware(streamingApp(), discard);
  let cut = 0;
  for (const seed of SEEDS) {
    const response = await serve(seeded(seed));
    expect(response.status).toBe(200); // the handler returned before the fault
    const reader = response.body!.getReader();
    const decoder = new TextDecoder();
    expect(decoder.decode((await reader.read()).value)).toBe("chunk");
    try {
      expect(decoder.decode((await reader.read()).value)).toBe("chunk");
      expect((await reader.read()).done).toBe(true);
    } catch (error) {
      expect(error).toBeInstanceOf(TooLate);
      expect(String(error)).toMatch(/^TooLate: indefinite-error: (before|after) app\.stream#0 \(seed=\d+\): too late to answer; dropping the connection$/);
      cut++;
    }
  }
  expect(cut).toBeGreaterThan(0);
});

test("the injection closes when the body ends", async () => {
  // A timer that outlives the response calls through with no faults.
  const ran: number[] = [];
  const late = indefinite("late", (i: number) => ran.push(i));
  let leaked: Promise<void> | undefined;
  const serve = middleware(() => {
    leaked = Bun.sleep(5).then(() => {
      for (let i = 0; i < 50; i++) late(i);
    });
    return new Response("ok");
  }, discard);
  for (const seed of SEEDS.slice(0, 30)) {
    ran.length = 0;
    const response = await serve(seeded(seed));
    expect(await response.text()).toBe("ok");
    await leaked;
    expect(ran).toHaveLength(50);
  }
});

test("a cancelled body closes the injection and cancels the handler's", async () => {
  let reason: unknown;
  let inside: boolean | undefined;
  const serve = middleware(() => {
    const body = new ReadableStream({
      pull(controller) {
        inside = current() !== undefined;
        controller.enqueue(new Uint8Array([1]));
      },
      cancel(why) {
        reason = why;
      },
    });
    return new Response(body);
  }, discard);
  const response = await serve(seeded(1n));
  const reader = response.body!.getReader();
  await reader.read();
  await reader.cancel("client left");
  expect([inside, reason]).toEqual([true, "client left"]);
});

test("a body's own error is not ours", async () => {
  const serve = middleware(() => {
    const body = new ReadableStream({
      pull(controller) {
        controller.error(new DefiniteError("disk"));
      },
    });
    return new Response(body);
  }, discard);
  expect((await serve(seeded(1n))).text()).rejects.toThrow("disk");
});

// --- What the middleware passes through untouched --------------------------------------

test("passes through without a seed", async () => {
  const { ledger, app } = writingApp();
  const serve = middleware(app, discard);
  for (let i = 0; i < 50; i++) {
    const response = await serve(seeded(undefined));
    expect([...summary(response), response.headers.get("x-app")]).toEqual([201, null, "yes"]);
  }
  expect(ledger).toHaveLength(50);
});

test("passes this, extra arguments, and a response-less return through", async () => {
  // Bun.serve calls fetch(request, server) on the server; an upgrade returns undefined.
  const calls: unknown[][] = [];
  const serve = withIndefinite(function (this: string, request: Request, server: number) {
    calls.push([this, request.method, server, current() !== undefined]);
    return undefined;
  });
  expect(await serve.call("self", seeded(1n), 7)).toBeUndefined();
  expect(await serve.call("self", seeded(undefined), 8)).toBeUndefined();
  expect(calls).toEqual([
    ["self", "POST", 7, true],
    ["self", "POST", 8, false],
  ]);
});

test("an app error is not ours to answer", async () => {
  const serve = middleware(() => {
    throw new DefiniteError("boom");
  }, discard);
  expect(serve(seeded(1n))).rejects.toThrow(DefiniteError);
  expect(serve(seeded(undefined))).rejects.toThrow(DefiniteError);
});

// --- The seed header: spec/seed-header.tsv ---------------------------------------------

const SEED_HEADER_CASES = specRows("seed-header.tsv").map(([values, outcome]) => ({
  values: JSON.parse(values!) as string[],
  seed: outcome === "400" ? undefined : BigInt(outcome!),
}));

test("the seed parser matches the spec", () => {
  // Headers arrive joined by ", ", as fetch joins a repeated header.
  for (const { values, seed } of SEED_HEADER_CASES) {
    expect([values, parseSeed(values.join(", "))]).toEqual([values, seed]);
  }
});

test("the middleware matches the spec, for every value a Headers can carry", async () => {
  // fetch strips a value's leading and trailing whitespace, as HTTP does, so
  // " 1" and "1 " reach the server as "1"; and a header is bytes, so "٣"
  // arrives as its UTF-8, one character per byte. Every other row goes through.
  const seeds: bigint[] = [];
  const serve = middleware(() => {
    seeds.push(current()!.seed);
    return new Response(null, { status: 204 });
  }, discard);
  const unrepresentable = [];
  for (const { values, seed } of SEED_HEADER_CASES) {
    const headers = new Headers();
    try {
      values.forEach((v) => headers.append(SEED_HEADER, v));
    } catch {
      unrepresentable.push(values); // not a byte string
      continue;
    }
    if (headers.get(SEED_HEADER) !== values.join(", ")) {
      unrepresentable.push(values);
      continue;
    }
    seeds.length = 0;
    const response = await serve(new Request("http://test/", { headers }));
    expect([values, response.status, seeds]).toEqual(
      seed === undefined ? [values, 400, []] : [values, 204, [seed]],
    );
  }
  expect(unrepresentable).toEqual([[" 1"], ["1 "], ["٣"]]);
});

test("a malformed seed is a 400, and the app never runs", async () => {
  const { ledger, app } = writingApp();
  const serve = middleware(app, discard);
  for (const raw of ["abc", "\xff", "1".repeat(65)]) {
    const response = await serve(seeded(raw));
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
  const serve = middleware(middleware(app, discard), discard);
  expect(serve(seeded(1n))).rejects.toThrow(
    "indefinite: a request seeded 1 inside one seeded 1: is withIndefinite installed twice?",
  );
});

// --- Over the wire: Bun.serve -------------------------------------------------------------

const servers: Array<{ stop: (force?: boolean) => unknown }> = [];
afterAll(() => servers.forEach((s) => s.stop(true)));

function listen(handler: Handler): URL {
  const server = Bun.serve({ port: 0, fetch: middleware(handler, discard) });
  servers.push(server);
  return server.url;
}

/** A header value as the UTF-8 its bytes spell: fetch reads each byte as a character. */
function utf8(value: string | null): string | undefined {
  return value === null ? undefined : new TextDecoder().decode(Uint8Array.from(value, (c) => c.charCodeAt(0)));
}

test("over the wire: the fault header is the payload's UTF-8", async () => {
  const site = "中.ünïcode";
  const op = indefinite(site, () => {});
  const url = listen(() => {
    op();
    return new Response("ok");
  });
  for (const seed of SEEDS) {
    const response = await fetch(url, { headers: { [SEED_HEADER]: String(seed) } });
    await response.arrayBuffer();
    if (response.status === 500) {
      expect(utf8(response.headers.get(FAULT_HEADER))).toMatch(new RegExp(`^(before|after) ${site}#0 \\(seed=${seed}\\)$`));
      return;
    }
  }
  throw new Error("no seed faulted");
});

test("over the wire: too late to answer drops the connection", async () => {
  const url = listen(streamingApp());
  const outcomes = new Set<string>();
  // Until both outcomes show up: Bun logs each body error it drops a connection for.
  for (let seed = 0n; outcomes.size < 2 && seed < 40n; seed++) {
    const response = await fetch(url, { headers: { [SEED_HEADER]: String(seed) } });
    try {
      outcomes.add(`${response.status} ${await response.text()}`);
    } catch {
      outcomes.add(`${response.status} dropped`);
    }
  }
  expect([...outcomes].sort()).toEqual(["200 chunkchunk", "200 dropped"]);
});
