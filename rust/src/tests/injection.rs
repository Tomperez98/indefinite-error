//! The injection a request carries: scope, visibility, bookkeeping.

use std::collections::HashMap;
use std::pin::pin;
use std::sync::Barrier;
use std::task::{Context, Poll, Waker};

use super::*;
use crate::core::{Mode, decide, mode_of};
use std::future::Future;
use std::sync::atomic::{AtomicBool, Ordering::Relaxed};

use crate::core::Phase;
use crate::injection::{Draw, current, with_current};

#[test]
fn any_i64_is_a_seed() {
    // Negative, zero, and the extremes: all valid, all replayable.
    let rec = Recorder::new("any");
    for seed in [i64::MIN, -1, 0, 1, i64::MAX] {
        assert_eq!(run(seed, &rec, 20), run(seed, &rec, 20), "seed {seed}");
    }
}

// --- Scope ---

#[test]
fn a_nested_injection_panics() {
    let outer = scope(1);
    must_panic(
        "a request seeded 2 inside one seeded 1: is IndefiniteLayer installed twice?",
        || {
            outer.enter(|| scope(2));
        },
    );
}

#[test]
fn a_closed_injection_injects_nothing() {
    // A call that outlives its request calls through, and may start its own.
    let rec = Recorder::new("op");
    for seed in 0..NUM_SEEDS {
        let injection = scope(seed).injection().clone(); // the scope drops: closed
        rec.ran.lock().unwrap().clear();
        with_current(&injection, || {
            for i in 0..CALLS {
                assert_eq!(block_on(rec.call(i)), i * 2, "seed {seed}");
            }
            let own = scope(seed + 1);
            assert_eq!(own.injection().seed, seed + 1);
        });
        assert_eq!(rec.ran().len() as u64, CALLS);
        assert!(
            injection.calls().is_empty(),
            "seed {seed}: calls {:?}",
            injection.calls()
        );
    }
}

#[test]
fn a_call_after_whose_request_ended_returns_its_output() {
    // AFTER can't end a request that's gone: the op's output comes back.
    let site = Site::new("late");
    let seed = (0..NUM_SEEDS)
        .find(|&seed| matches!(drive(&scope(seed), site.run(async {})), Err(f) if f.phase == Phase::After))
        .expect("no seed faults after");
    let gate = AtomicBool::new(false);
    let op = std::future::poll_fn(|_| {
        if gate.load(Relaxed) {
            Poll::Ready(7)
        } else {
            Poll::Pending
        }
    });
    let mut call = pin!(site.run(op));
    let cx = &mut Context::from_waker(Waker::noop());
    let scope = scope(seed);
    assert!(
        scope.poll_in(call.as_mut(), cx).is_pending(),
        "AFTER waits for the op"
    );
    let injection = scope.injection().clone();
    drop(scope);
    gate.store(true, Relaxed);
    assert_eq!(
        with_current(&injection, || call.as_mut().poll(cx)),
        Poll::Ready(7)
    );
}

#[test]
fn a_call_outside_the_request_is_never_faulted() {
    // The documented trap: std::thread::spawn and tokio::spawn don't carry it.
    let rec = Recorder::new("missed");
    let scope = scope(0);
    scope.enter(|| {
        assert!(current().is_some());
        std::thread::scope(|s| {
            s.spawn(|| {
                assert!(current().is_none());
                for i in 0..CALLS {
                    assert_eq!(block_on(rec.call(i)), i * 2);
                }
            });
        });
    });
    tokio::runtime::Builder::new_current_thread()
        .build()
        .unwrap()
        .block_on(async {
            let spawned = scope.enter(|| tokio::spawn(rec.call(0)));
            assert_eq!(spawned.await.unwrap(), 0);
        });
    assert!(
        scope.injection().calls().is_empty(),
        "calls {:?}",
        scope.injection().calls()
    );
}

#[test]
fn nested_futures_see_the_injection() {
    // async blocks, join!, and select! are polled inside the request's poll.
    let rec = Recorder::new("nested");
    for seed in 0..NUM_SEEDS {
        let want = run(seed, &rec, 3);
        let scope = scope(seed);
        let got = vec![
            outcome(catch(&scope, async { async { rec.call(0).await }.await })),
            outcome(catch(&scope, async {
                tokio::join!(rec.call(1), async {}).0
            })),
            outcome(catch(&scope, async {
                tokio::select! { biased; v = rec.call(2) => v, () = std::future::pending() => 0 }
            })),
        ];
        assert_eq!(got, want, "seed {seed}");
    }
}

// --- Threads ---

#[test]
fn threads_sharing_one_injection_lose_no_calls() {
    // Every concurrent call gets its own n, and the faults are exactly the
    // seed's. (Through Site::run, the first fault would end the request.)
    const SITE: &str = "hot";
    const THREADS: u64 = 8;
    const PER_THREAD: u64 = 5_000;
    let site = Site::new(SITE);
    let seed = (0..NUM_SEEDS)
        .find(|&seed| mode_of(seed, SITE) == Mode::Both)
        .unwrap();
    let scope = scope(seed);
    let start = Barrier::new(THREADS as usize);
    let mut faulted: Vec<u64> = std::thread::scope(|s| {
        let handles: Vec<_> = (0..THREADS)
            .map(|_| {
                s.spawn(|| {
                    start.wait();
                    let injection = scope.injection();
                    let draws = (0..PER_THREAD).map(|_| injection.next(site.name()));
                    draws
                        .filter_map(|d| {
                            if let Draw::Fault(f) = d {
                                Some(f.n)
                            } else {
                                None
                            }
                        })
                        .collect::<Vec<_>>()
                })
            })
            .collect();
        handles
            .into_iter()
            .flat_map(|h| h.join().unwrap())
            .collect()
    });

    let total = THREADS * PER_THREAD;
    assert_eq!(scope.injection().calls(), HashMap::from([(SITE, total)]));
    let rate = scope.injection().rate;
    let want: Vec<u64> = (0..total)
        .filter(|&n| decide(seed, rate, Mode::Both, SITE, n).is_some())
        .collect();
    faulted.sort_unstable();
    assert_eq!(faulted, want);
}

#[test]
fn concurrent_requests_do_not_interfere() {
    // One injection per request, all overlapping: each gets the path it gets alone.
    let rec = Recorder::new("op");
    let alone: Vec<Vec<String>> = (0..NUM_SEEDS).map(|seed| run(seed, &rec, CALLS)).collect();
    let together: Vec<Vec<String>> = std::thread::scope(|s| {
        let handles: Vec<_> = (0..NUM_SEEDS)
            .map(|seed| s.spawn(move || run(seed, &Recorder::new("op"), CALLS)))
            .collect();
        handles.into_iter().map(|h| h.join().unwrap()).collect()
    });
    assert_eq!(together, alone);
}
