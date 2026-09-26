/** The README is a contract: its code runs, and prints what it shows. */

import { expect, test } from "bun:test";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { join } from "node:path";

import { enter, IndefiniteFault } from "../src/injection.ts";
import { indefinite } from "../src/site.ts";
import { captured, injection } from "./helpers.ts";

const ROOT = join(import.meta.dir, "..");
const README = readFileSync(join(ROOT, "README.md"), "utf8");
const blocks = (lang: string) => [...README.matchAll(new RegExp("```" + lang + "\\n(.*?)```", "gs"))].map((m) => m[1]!);

function block(lang: string, marker: string): string {
  const matches = blocks(lang).filter((b) => b.includes(marker));
  expect(matches).toHaveLength(1);
  return matches[0]!;
}

test("the fault line example is real", () => {
  // The README's line is what seed 13 writes on the fifth call to app.get.
  const get = indefinite("app.get", () => {});
  const { log, text } = captured();
  const inj = injection(13n, log);
  for (let i = 0; i < 4; i++) {
    enter(inj, get);
  }
  expect(() => enter(inj, get)).toThrow(IndefiniteFault);
  expect(block("text", "app.get#4")).toBe(text());
});

test("try it prints what it shows", async () => {
  // Run as a user would: its own file, importing the package by name.
  const dir = mkdtempSync(join(ROOT, ".readme-"));
  try {
    const file = join(dir, "try-it.ts");
    writeFileSync(file, block("ts", 'for (const seed of ["70", "74", "0"])'));
    const proc = Bun.spawn(["bun", file], { cwd: ROOT, stdout: "pipe", stderr: "pipe" });
    const [stdout, stderr, code] = await Promise.all([
      new Response(proc.stdout).text(),
      new Response(proc.stderr).text(),
      proc.exited,
    ]);
    expect([code, stderr]).toEqual([0, expect.any(String)]);
    const shown = block("text", "seed=70: status=500").split("\n");
    expect(stdout).toBe(shown.filter((l) => l.startsWith("seed=")).join("\n") + "\n");
    expect(stderr).toBe(shown.filter((l) => l.startsWith("indefinite-error:")).join("\n") + "\n");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});
