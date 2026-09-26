/**
 * The middleware: each request carrying `X-Indefinite-Seed` runs inside its
 * own injection, driven by that seed.
 *
 * @module
 */

import { ignore, isInt64 } from "./core.ts";
import { enter, open, stderr, type Fault, type Injection, type Log } from "./injection.ts";

/** Carries a request's seed: one decimal int64, such as `13` or `-7`. */
export const SEED_HEADER = "x-indefinite-seed";

/**
 * Names the fault that ended a request: `after db.commit#0 (seed=13)`, as
 * UTF-8 bytes. It is for debugging; a test's assertions must not read it.
 */
export const FAULT_HEADER = "x-indefinite-fault";

/** A fetch handler: what `Bun.serve`, `Deno.serve`, Hono, and Workers call. */
export type FetchHandler<This, A extends unknown[], R> = (this: This, request: Request, ...rest: A) => R | Promise<R>;

/**
 * Wraps a fetch handler so each request carrying {@link SEED_HEADER} runs
 * inside its own injection, and `indefinite` calls made while serving it can
 * fault.
 *
 * ```ts
 * Bun.serve({ fetch: withIndefinite(app.fetch) }); // tests only
 * ```
 *
 * Requests without the header pass through untouched. A malformed seed --
 * anything but exactly one value matching `-?[0-9]{1,19}` that fits an int64
 * -- gets a `400`, and the handler never sees the request.
 *
 * A fault ends its request: whatever the handler returns or throws, the
 * response is a fresh `500` with an empty body and a {@link FAULT_HEADER}
 * naming the fault. That is the closest a handler can come to losing a
 * request or its response. If the handler had already returned its response,
 * and the fault fires while the body streams, the body errors instead, and
 * the server drops the connection mid-response.
 *
 * Never install it in production: any caller could fault your server.
 *
 * @throws {Error} On a seeded request, if another `withIndefinite` is already
 *   serving it: the middleware is installed twice.
 */
export function withIndefinite<This, A extends unknown[], R extends Response | void | undefined>(
  handler: FetchHandler<This, A, R>,
): (this: This, request: Request, ...rest: A) => Promise<R | Response> {
  return middleware(handler, stderr);
}

/** {@link withIndefinite}, writing fault lines to `log`. */
export function middleware<This, A extends unknown[], R extends Response | void | undefined>(
  handler: FetchHandler<This, A, R>,
  log: Log,
): (this: This, request: Request, ...rest: A) => Promise<R | Response> {
  return async function (this: This, request: Request, ...rest: A): Promise<R | Response> {
    const raw = request.headers.get(SEED_HEADER);
    if (raw === null) {
      return handler.call(this, request, ...rest);
    }
    const seed = parseSeed(raw);
    if (seed === undefined) {
      return new Response(BAD_SEED, {
        status: 400,
        headers: { "content-type": "text/plain; charset=utf-8" },
      });
    }

    const injection = open(seed, log);
    let response: R | undefined;
    try {
      response = await enter(injection, () => handler.call(this, request, ...rest));
    } catch (error) {
      if (injection.aborted === undefined) {
        injection.close();
        throw error; // not ours to answer
      }
    }
    const aborted = injection.aborted;
    if (aborted !== undefined) {
      injection.close();
      if (response instanceof Response) {
        response.body?.cancel().catch(ignore); // the lost response: release it
      }
      return faulted(aborted.fault);
    }
    if (!(response instanceof Response) || response.body === null) {
      injection.close();
      return response as R; // no body left to stream: the request is over
    }
    return live(response, injection);
  };
}

/** The body of a malformed seed's `400`: for humans, as the spec says. */
export const BAD_SEED = "X-Indefinite-Seed must be a decimal int64";

/**
 * The seed, if `raw` is exactly one decimal int64 (`spec/seed-header.tsv`).
 * A repeated header arrives joined by `", "`, so it never parses.
 */
export function parseSeed(raw: string): bigint | undefined {
  if (!/^-?[0-9]{1,19}$/.test(raw)) {
    return undefined;
  }
  const seed = BigInt(raw);
  return isInt64(seed) ? seed : undefined;
}

/** A fresh response: nothing the lost request did survives. */
function faulted(fault: Fault): Response {
  return new Response(null, { status: 500, headers: { [FAULT_HEADER]: faultHeader(fault) } });
}

/**
 * The {@link FAULT_HEADER} value: the payload as UTF-8, one byte per
 * character. A header value is a byte string, so a site like `中` goes on the
 * wire as its UTF-8 bytes, as the spec pins.
 */
export function faultHeader(fault: Fault): string {
  return String.fromCharCode(...new TextEncoder().encode(String(fault)));
}

/**
 * `response`, with its body streaming inside the request's injection. The
 * injection closes when the body ends, errors, or is cancelled. A fault
 * while it streams errors the body: too late to answer.
 */
function live(response: Response, injection: Injection): Response {
  const reader = (response.body as ReadableStream<Uint8Array>).getReader();
  const body = new ReadableStream<Uint8Array>({
    async pull(controller) {
      const read = await enter(injection, () => reader.read()).then(
        (chunk) => ({ chunk }),
        (error: unknown) => ({ error }),
      );
      // A fault outranks whatever the body did in the same read.
      const aborted = injection.aborted;
      if (aborted !== undefined) {
        injection.close();
        reader.cancel().catch(ignore);
        controller.error(new TooLate(aborted.fault));
      } else if ("error" in read) {
        injection.close();
        controller.error(read.error); // the body's own error: not ours
      } else if (read.chunk.done) {
        injection.close();
        controller.close();
      } else {
        controller.enqueue(read.chunk.value);
      }
    },
    cancel(reason) {
      injection.close();
      return reader.cancel(reason);
    },
  });
  return new Response(body, { status: response.status, statusText: response.statusText, headers: response.headers });
}

/** The body error of a fault that fired after the response started. */
export class TooLate extends Error {
  override readonly name = "TooLate";

  constructor(readonly fault: Fault) {
    super(`indefinite-error: ${fault}: too late to answer; dropping the connection`);
  }
}
