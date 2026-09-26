/**
 * The middleware for Node's `http`: Express, Connect, Polka, or a bare
 * `http.createServer`. Each request carrying `X-Indefinite-Seed` runs inside
 * its own injection, driven by that seed.
 *
 * ```ts
 * import { indefiniteMiddleware } from "indefinite-error/node";
 *
 * app.use(indefiniteMiddleware()); // first, and tests only
 * ```
 *
 * @module
 */

export { FAULT_HEADER, SEED_HEADER } from "./middleware.ts";
export { indefiniteMiddleware, type NodeMiddleware } from "./node-middleware.ts";
