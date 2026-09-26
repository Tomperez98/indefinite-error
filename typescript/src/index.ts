/**
 * Reproducible injection of indefinite errors into the requests of a server.
 *
 * ```ts
 * import { indefinite, withIndefinite } from "indefinite-error";
 *
 * const commit = indefinite("db.commit", (tx: Tx) => tx.commit());
 *
 * Bun.serve({ fetch: withIndefinite(app.fetch) }); // tests only
 * ```
 *
 * Outside a request carrying a seed, a marked function just runs. Inside
 * one, a call may fault BEFORE the function runs (it never happened) or AFTER
 * it settles (it happened; nobody was told). A call that isn't faulted
 * returns, throws, or rejects exactly as the function did.
 *
 * A fault writes one line to stderr, then ends its request: the call throws,
 * every later marked call in the request throws the same fault without
 * running, and the middleware answers `500` whatever the handler does. The
 * server and its other requests carry on.
 *
 * Every decision is derived from the request's seed alone, an int64: the same
 * seed replays the same faults, in any process and in any language that keeps
 * the contract in the repository's `spec/`.
 *
 * @module
 */

export { indefinite, type MethodDecorator } from "./site.ts";
export { FAULT_HEADER, SEED_HEADER, withIndefinite, type FetchHandler } from "./middleware.ts";
