/**
 * The middleware for Node's `http`. The package exports it as
 * `indefinite-error/node`, from `node.ts`.
 *
 * @module
 */

import type { IncomingMessage, ServerResponse } from "node:http";

import { enter, open, stderr, type Injection, type Log } from "./injection.ts";
import { BAD_SEED, FAULT_HEADER, faultHeader, parseSeed, SEED_HEADER } from "./middleware.ts";

/** A Connect-style middleware, as Express, Connect, and Polka take it. */
export type NodeMiddleware = (request: IncomingMessage, response: ServerResponse, next: (error?: unknown) => void) => void;

/**
 * A middleware that runs each request carrying {@link SEED_HEADER} inside its
 * own injection, so `indefinite` calls made while serving it can fault.
 *
 * Mount it before your routes, and before anything that responds for them:
 * everything `next()` leads to runs inside the injection. Requests without
 * the header pass through untouched. A malformed seed -- anything but exactly
 * one value matching `-?[0-9]{1,19}` that fits an int64 -- gets a `400`, and
 * the rest of the app never sees the request.
 *
 * A fault ends its request. Whoever responds next -- the route that caught
 * it, Express's error handler, yours -- has the response replaced by a fresh
 * `500` with an empty body and a {@link FAULT_HEADER} naming the fault. If the
 * headers had already gone out, the middleware destroys the response
 * instead, and the client sees the connection drop mid-response.
 *
 * Never install it in production: any caller could fault your server.
 */
export function indefiniteMiddleware(): NodeMiddleware {
  return nodeMiddleware(stderr);
}

/** {@link indefiniteMiddleware}, writing fault lines to `log`. */
export function nodeMiddleware(log: Log): NodeMiddleware {
  return (request, response, next) => {
    const values = seedValues(request.rawHeaders);
    if (values.length === 0) {
      next();
      return;
    }
    // Joined as fetch would join them: a repeated header never parses.
    const seed = parseSeed(values.join(", "));
    if (seed === undefined) {
      response.writeHead(400, { "content-type": "text/plain; charset=utf-8" }).end(BAD_SEED);
      return;
    }
    const injection = open(seed, log);
    const close = () => injection.close();
    response.once("finish", close).once("close", close);
    guard(response, injection);
    enter(injection, next);
  };
}

/** Every value of the seed header. Node keeps each raw header, repeats included. */
function seedValues(rawHeaders: readonly string[]): string[] {
  const values = [];
  for (let i = 0; i < rawHeaders.length; i += 2) {
    if (rawHeaders[i]!.toLowerCase() === SEED_HEADER) {
      values.push(rawHeaders[i + 1]!);
    }
  }
  return values;
}

/**
 * Makes `response` answer for the request's fault. Once a fault has fired,
 * the first write, `writeHead`, or `end` sends the fault's `500` instead,
 * and every later one goes nowhere. A fault after the headers went out
 * destroys the response as it fires.
 */
function guard(response: ServerResponse, injection: Injection): void {
  const { writeHead } = response;
  // Their overloads are many; each is called with the arguments it was given.
  const write = response.write as (this: ServerResponse, ...args: unknown[]) => boolean;
  const end = response.end as (this: ServerResponse, ...args: unknown[]) => ServerResponse;
  let lost = false;

  /** Whether the response is lost; the first time it is, answers for the fault. */
  const isLost = (): boolean => {
    if (lost) {
      return true;
    }
    const aborted = injection.aborted;
    if (aborted === undefined) {
      return false;
    }
    lost = true;
    if (response.headersSent) {
      response.destroy(); // too late to answer: drop the connection
      return true;
    }
    // A fresh response: nothing the lost request set survives.
    for (const name of response.getHeaderNames()) {
      response.removeHeader(name);
    }
    writeHead.call(response, 500, { [FAULT_HEADER]: faultHeader(aborted.fault), "content-length": "0" });
    end.call(response);
    return true;
  };

  injection.signal.addEventListener("abort", () => response.headersSent && isLost(), { once: true });

  response.writeHead = function (this: ServerResponse, ...args: Parameters<typeof writeHead>) {
    return isLost() ? this : writeHead.apply(this, args);
  } as typeof writeHead;

  response.write = function (this: ServerResponse, ...args: unknown[]) {
    if (isLost()) {
      callback(args)?.();
      return true;
    }
    return write.apply(this, args);
  } as ServerResponse["write"];

  response.end = function (this: ServerResponse, ...args: unknown[]) {
    if (isLost()) {
      callback(args)?.();
      return this;
    }
    return end.apply(this, args);
  } as ServerResponse["end"];
}

/** The callback `write` or `end` was given, if any: it's their last function argument. */
function callback(args: unknown[]): (() => void) | undefined {
  const last = args.at(-1);
  return typeof last === "function" ? (last as () => void) : undefined;
}
