# indefinite-error

Reproducibly inject indefinite errors (did it happen or not?) into the
boundaries of your system.

- [Python](python): `@indefinite` and an ASGI middleware.
- [Go](go): `indefinite.Site` and a `net/http` middleware.
- [Rust](rust): `Site::run` and a tower layer, for axum, hyper, and tonic.

All are tested against [`spec/`](spec), the contract they share: a seed
replays the same faults in any of these languages.
