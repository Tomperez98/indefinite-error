/**
 * Preloaded before every test file (bunfig.toml).
 *
 * Property tests draw the same examples every run, so a red build is a real
 * regression. For a long run with fresh examples: FC_SEED=random FC_RUNS=100000 bun test
 */

import fc from "fast-check";

const seed = process.env["FC_SEED"] ?? "0";
fc.configureGlobal({
  numRuns: Number(process.env["FC_RUNS"] ?? 200),
  ...(seed === "random" ? {} : { seed: Number(seed) }),
});
