# The indefinite-error contract

Every implementation ([Python](../python), [Go](../go), [Rust](../rust),
[TypeScript](../typescript)) must agree on what a seed does. Then one seed
replays the same faults in every service of a system, whatever language each
is written in. This directory is that agreement. Each
implementation's tests read these files, so if one drifts, its gate fails.

| File | Pins | Written by |
|---|---|---|
| [`schedule.tsv`](schedule.tsv) | which calls fault, and when, for each seed and site | generated |
| [`faults.tsv`](faults.tsv) | the fault line and `X-Indefinite-Fault` value | hand |
| [`seed-header.tsv`](seed-header.tsv) | which `X-Indefinite-Seed` values are accepted | hand |

Each file is UTF-8 with tab-separated columns. Lines starting with `#` are
comments. A site is a JSON string, so a NUL or a non-ASCII character in a site
name is unambiguous.

## Seeds

A seed is a signed 64-bit integer. On the wire it is exactly one
`X-Indefinite-Seed` value matching `-?[0-9]{1,19}` that lies in
[-2^63, 2^63). Leading zeros are allowed, and `-0` is `0`. Anything else
gets `400`, and the app never sees the request. That includes a repeated
header, even with the same value twice. A request without the header runs
untouched. The body of the `400` is for humans; only its status is pinned.

## Decisions

Every decision is a function of `(seed, site, n)`, where `n` counts the
calls to `site` within one request, from 0.

`unit(parts...)` is a float in [0, 1):

1. Write each part as UTF-8. Write an integer in decimal, with a leading `-`
   if it's negative.
2. Join the parts with a NUL byte (`\x00`).
3. Hash the result with BLAKE2b, unkeyed, with an 8-byte digest. The digest
   size is part of the BLAKE2b parameter block, so this is not a truncated
   BLAKE2b-512.
4. Read the digest as a big-endian unsigned 64-bit integer, convert it to the
   nearest float64, and divide it by 2^64.

`pick(options, u)` is `options[floor(u * len(options))]`.

For a request seeded `seed`:

- `rate = pick([0.01, 0.05, 0.2, 0.5], unit("rate", seed))`
- `mode(site) = pick([off, before, after, both], unit("mode", seed, site))`
- Call `n` to `site` faults if `mode` isn't `off` and
  `unit("call", seed, site, n) < rate`. Then:
  - in mode `before` or `after`, it faults in that phase;
  - in mode `both`, it faults `before` if `unit("phase", seed, site, n) < 0.5`,
    and `after` otherwise.

## Faults

A fault's payload is `{phase} {site}#{n} (seed={seed})`, for example
`after app.get#4 (seed=13)`. The server writes one line,
`indefinite-error: {payload}\n`, to stderr, in a single write. If the response
hasn't started, the server answers `500` with an empty body and an
`X-Indefinite-Fault: {payload}` header.

A site name must be non-empty and printable, with no whitespace, so that the
line stays parseable. (`schedule.tsv` pins one invalid site, `"a\u0000b"`,
only to show that NUL-joined parts can't collide.)

## Changing the contract

A change to `schedule.tsv` breaks every seed anyone saved from a failing run,
in every language, so make it only on purpose. To regenerate it:

```sh
cd python && uv run python -m tests.test_core --regen
```

Then make every implementation pass again before merging. The hand-written
files change like any spec: edit the file, then fix each implementation until
its tests pass.
