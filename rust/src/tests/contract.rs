//! The contract every site call keeps. `drive` and `catch` stand in for the
//! middleware; http.rs has the real one.

use std::collections::{BTreeSet, HashMap};
use std::sync::{Arc, Mutex};

use super::*;

#[derive(Debug, PartialEq)]
struct Definite; // a failure the operation itself reports

// --- Before never ran; after ran; outside a request nothing happens ---

#[test]
fn inert_outside_a_request() {
    let rec = Recorder::new("op");
    for i in 0..CALLS {
        assert_eq!(block_on(rec.call(i)), i * 2);
    }
    assert_eq!(rec.ran().len() as u64, CALLS);
}

#[test]
fn before_never_runs_after_always_runs() {
    for seed in 0..NUM_SEEDS {
        let rec = Recorder::new("op");
        let want: Vec<u64> = (0..CALLS)
            .zip(run(seed, &rec, CALLS))
            .filter(|(_, o)| o != "before")
            .map(|(i, _)| i)
            .collect();
        assert_eq!(rec.ran(), want, "seed {seed}");
    }
}

// --- Definite outcomes pass through; a panic outranks the fault ---

#[test]
fn a_definite_error_passes_through_unless_faulted() {
    let site = Site::new("boom");
    let mut seen = BTreeSet::new();
    for seed in 0..NUM_SEEDS {
        match drive(&scope(seed), site.run(async { Err::<(), _>(Definite) })) {
            Ok(result) => {
                assert_eq!(result, Err(Definite), "seed {seed}");
                seen.insert("definite".to_owned());
            }
            Err(fault) => {
                seen.insert(fault.phase.to_string());
            }
        }
    }
    assert_eq!(
        seen,
        BTreeSet::from(["after", "before", "definite"].map(String::from))
    );
}

#[test]
fn a_panic_outranks_after() {
    // A panic isn't an outcome: it propagates unchanged, and AFTER stays silent.
    let site = Site::new("panics");
    let lines = Lines::default();
    let mut seen = BTreeSet::new();
    for seed in 0..NUM_SEEDS {
        let scope = Scope::new(seed, lines.log());
        let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            drive(&scope, site.run(async { panic!("fatal") }))
        }));
        match result {
            Ok(Ok(())) => unreachable!("the op always panics"),
            Ok(Err(fault)) => seen.insert(fault.phase.to_string()),
            Err(payload) => seen.insert((*payload.downcast::<&str>().unwrap()).to_owned()),
        };
    }
    assert_eq!(seen, BTreeSet::from(["before", "fatal"].map(String::from)));
    assert!(
        !lines.text().contains("after"),
        "an after fault wrote its line although op panicked:\n{}",
        lines.text()
    );
}

// --- The fault ends the request ---

#[test]
fn the_fault_is_invisible_to_error_checks() {
    // A retry loop can't retry past it: nothing after the fault runs.
    let rec = Recorder::new("op");
    let mut faulted = 0;
    for seed in 0..NUM_SEEDS {
        let attempts = Mutex::new(Vec::new());
        let handler = async {
            for n in 0..3 {
                attempts.lock().unwrap().push(n);
                if rec.call(n).await != n * 2 {
                    continue;
                }
                return "done";
            }
            "gave up"
        };
        if drive(&scope(seed), handler).is_err() {
            faulted += 1;
        }
        assert_eq!(
            attempts.into_inner().unwrap(),
            [0],
            "seed {seed}: the loop reached a second attempt"
        );
    }
    assert!(faulted > 0, "no seed faulted");
}

#[test]
fn a_fault_ends_the_request_where_it_fires() {
    // Nothing after the faulted call runs: not the next line, not a retry.
    let site = Site::new("op");
    for seed in 0..NUM_SEEDS {
        let reached = Mutex::new(Vec::new());
        let result = drive(&scope(seed), async {
            for n in 0..3 {
                reached.lock().unwrap().push(n);
                let _ = site.run(async { Err::<(), _>(Definite) }).await;
            }
        });
        let reached = reached.into_inner().unwrap();
        if let Err(fault) = result {
            assert_eq!(
                reached.len() as u64,
                fault.n + 1,
                "seed {seed}: ran past call {}",
                fault.n
            );
        } else {
            assert_eq!(reached, [0, 1, 2]);
        }
    }
}

struct Deferred(Arc<Mutex<Vec<&'static str>>>);

impl Drop for Deferred {
    fn drop(&mut self) {
        self.0.lock().unwrap().push("deferred");
    }
}

#[test]
fn a_fault_runs_destructors() {
    // The server lives on, so destructors run and locks are released.
    let site = Site::new("commit");
    let want = HashMap::from([
        ("ok", vec!["commit", "after", "deferred"]),
        ("before", vec!["deferred"]),
        ("after", vec!["commit", "deferred"]),
    ]);
    for seed in 0..NUM_SEEDS {
        let events = Arc::new(Mutex::new(Vec::new()));
        let result = drive(&scope(seed), async {
            let _deferred = Deferred(events.clone());
            site.run(async { events.lock().unwrap().push("commit") })
                .await;
            events.lock().unwrap().push("after");
        });
        let outcome = outcome(result);
        assert_eq!(
            *events.lock().unwrap(),
            want[outcome.as_str()],
            "seed {seed} ({outcome})"
        );
    }
}

#[test]
fn once_a_fault_fires_no_later_call_runs() {
    // Even one polled in the same pass, beside the faulted one.
    let (first, second) = (Recorder::new("first"), Recorder::new("second"));
    let mut faulted = 0;
    for seed in 0..NUM_SEEDS {
        let scope = scope(seed);
        let result = drive(&scope, async {
            tokio::join!(first.call(0), second.call(0))
        });
        let calls = scope.injection().calls();
        match result {
            Err(fault) if fault.site == "first" => {
                faulted += 1;
                assert_eq!(
                    calls.get("second"),
                    None,
                    "seed {seed}: second ran after first faulted"
                );
            }
            _ => assert_eq!(calls.get("second"), Some(&1)),
        }
    }
    assert!(faulted > 0);
}

#[test]
fn a_fault_writes_the_line() {
    let rec = Recorder::new("logged");
    let lines = Lines::default();
    let mut want = String::new();
    for seed in 0..NUM_SEEDS {
        if let Err(fault) = drive(&Scope::new(seed, lines.log()), rec.call(0)) {
            want.push_str(&format!("indefinite-error: {fault}\n"));
        }
    }
    assert!(!want.is_empty());
    assert_eq!(lines.text(), want);
}

// --- Replay: same seed, same faults; sites don't interfere ---

#[test]
fn same_seed_same_outcomes() {
    for seed in 0..NUM_SEEDS {
        assert_eq!(
            run(seed, &Recorder::new("op"), CALLS),
            run(seed, &Recorder::new("op"), CALLS),
            "seed {seed}"
        );
    }
}

#[test]
fn a_request_depends_on_its_seed_alone() {
    // seed=41 takes the same path in its first request or after 100 others.
    let rec = Recorder::new("op");
    let first = run(41, &rec, CALLS);
    for other in 0..100 {
        run(1000 + other, &rec, CALLS);
    }
    assert_eq!(run(41, &rec, CALLS), first);
}

#[test]
fn sites_are_independent() {
    // Calls to other sites don't shift a site's decisions.
    let (rec, other) = (Recorder::new("op"), Recorder::new("other"));
    for seed in 0..NUM_SEEDS {
        let alone = run(seed, &rec, CALLS);
        let scope = scope(seed);
        let mixed: Vec<String> = (0..CALLS)
            .map(|i| {
                attempt(&scope, &other, i);
                attempt(&scope, &rec, i)
            })
            .collect();
        assert_eq!(mixed, alone, "seed {seed}");
    }
}

#[test]
fn seeds_vary_which_phases_a_site_gets() {
    // Swarm testing: per seed, a site faults never, before-only, after-only, or both.
    let rec = Recorder::new("op");
    let kinds: BTreeSet<String> = (0..NUM_SEEDS)
        .map(|seed| {
            let phases: BTreeSet<String> = run(seed, &rec, CALLS)
                .into_iter()
                .filter(|o| o != "ok")
                .collect();
            phases.into_iter().collect::<Vec<_>>().join("+")
        })
        .collect();
    assert_eq!(
        kinds,
        BTreeSet::from(["", "after", "after+before", "before"].map(String::from))
    );
}

// --- What the fault says ---

#[test]
fn the_fault_names_the_site_call_and_seed() {
    let rec = Recorder::new("db.commit");
    let fault = (0..NUM_SEEDS)
        .find_map(|seed| drive(&scope(seed), rec.call(0)).err())
        .expect("no seed faulted");
    assert_eq!((fault.site, fault.n), ("db.commit", 0));
    assert_eq!(
        fault.to_string(),
        format!("{} db.commit#0 (seed={})", fault.phase, fault.seed)
    );
}

#[test]
fn calls_count_every_call_faulted_or_not() {
    let rec = Recorder::new("counted");
    for seed in 0..NUM_SEEDS {
        let scope = scope(seed);
        for i in 0..CALLS {
            attempt(&scope, &rec, i);
        }
        assert_eq!(
            scope.injection().calls(),
            HashMap::from([("counted", CALLS)]),
            "seed {seed}"
        );
    }
}

#[test]
fn sites_with_one_name_share_one_stream() {
    // A site is its name: two Sites named alike share one call counter.
    let (a, b, c) = (Site::new("shared"), Site::new("shared"), Site::new("own"));
    let scope = scope(0);
    for site in [a, b, c] {
        let _ = catch(&scope, site.run(async {}));
    }
    assert_eq!(
        scope.injection().calls(),
        HashMap::from([("shared", 2), ("own", 1)])
    );
}

// --- Misuse is a bug: it panics ---

#[test]
fn a_site_name_that_would_break_the_fault_line_panics() {
    // ASCII mistakes panic in Site::new (at compile time, in a static); the
    // rest of Unicode at the first run, in or out of a request.
    for name in ["", "a b", "a\tb", "a\nb", "a\0b", "a\x7fb"] {
        must_panic("site name must", || {
            let _ = Site::new(leak(name));
        });
    }
    for name in [
        "a\u{a0}b",
        "a\u{200b}b",
        "a\u{2028}b",
        "a\u{e000}b",
        "a\u{0378}b",
    ] {
        let site = Site::new(leak(name));
        must_panic("site name must", || block_on(site.run(async {})));
        must_panic("site name must", || {
            let _ = drive(&scope(0), site.run(async {}));
        });
    }
    for name in [
        "db.commit",
        "ünïcode.sïte",
        "POST:/deposits#key",
        "中",
        "emoji🎉",
    ] {
        let site = Site::new(leak(name));
        assert_eq!(site.to_string(), name);
        block_on(site.run(async {}));
    }
}
