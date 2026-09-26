# [indefinite-error](https://antithesis.com/docs/resources/reliability_glossary/?sid=9c642.25&sterm=inde&marks=Inde#indefinite-error)

Reproducibly inject indefinite errors (did it happen or not?) into the
boundaries of your system.

- [Python](python): `@indefinite` and an ASGI middleware.
- [Go](go): `indefinite.Site` and a `net/http` middleware.
- [Rust](rust): `Site::run` and a tower layer, for axum, hyper, and tonic.
- [TypeScript](typescript): `indefinite(name, fn)`, with middleware for fetch
  handlers (`Bun.serve`, Hono) and for Node's `http` (Express, Connect).
- [.NET](dotnet): `IndefiniteSite.RunAsync` and ASP.NET Core middleware
  (`UseIndefiniteErrors`), plus an `HttpClient` handler for outgoing calls.

All are tested against [`spec/`](spec), the contract they share: a seed
replays the same faults in any of these languages.
