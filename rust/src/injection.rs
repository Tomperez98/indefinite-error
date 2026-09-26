//! One request's injection: what its seed chose, and which sites it reached.
//!
//! The only state is a call counter per site, and whether a fault has ended
//! the request. It travels with the request's future: [`Scope::poll_in`] makes
//! it current on the polling thread for the length of one poll, as
//! `tokio::task_local!` or `tracing::Instrument` would, so any future polled
//! inside -- `join!`, `select!`, nested `async` blocks -- sees it, and a task
//! spawned onto the runtime doesn't.

use std::cell::RefCell;
use std::collections::HashMap;
use std::fmt;
use std::future::Future;
use std::io::Write;
use std::pin::Pin;
use std::sync::{Arc, Mutex, MutexGuard};
use std::task::{Context, Poll};

use crate::core::{Mode, Phase, decide, mode_of, rate_of};

/// One injected fault: the `n`-th call to `site` in the request seeded `seed`.
#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) struct Fault {
    pub(crate) seed: i64,
    pub(crate) site: &'static str,
    pub(crate) n: u64,
    pub(crate) phase: Phase,
}

/// The fault line's payload: `after app.get#4 (seed=13)`.
impl fmt::Display for Fault {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            f,
            "{} {}#{} (seed={})",
            self.phase, self.site, self.n, self.seed
        )
    }
}

/// Where fault lines go: stderr, outside tests.
pub(crate) type Log = Arc<dyn Fn(&str) + Send + Sync>;

/// One unbuffered write per line, so lines from concurrent requests don't
/// interleave. A lost line is fine: the response still names the fault.
pub(crate) fn stderr() -> Log {
    Arc::new(|line| {
        let _ = std::io::stderr().lock().write_all(line.as_bytes());
    })
}

/// What a site call draws when it starts.
#[derive(Debug, PartialEq, Eq)]
pub(crate) enum Draw {
    /// Run the call and return what it returns.
    Pass,
    /// Fault it.
    Fault(Fault),
    /// A fault already ended this request: the call must never run.
    Over,
}

#[derive(Default)]
struct State {
    calls: HashMap<&'static str, u64>,
    modes: HashMap<&'static str, Mode>, // a cache of mode_of(seed, site)
    aborted: Option<Fault>,
    closed: bool,
}

pub(crate) struct Injection {
    pub(crate) seed: i64,
    pub(crate) rate: f64,
    log: Log,
    state: Mutex<State>,
}

impl Injection {
    fn state(&self) -> MutexGuard<'_, State> {
        // A panic under this lock leaves the counters consistent: every
        // critical section is a few map operations that can't half-finish.
        self.state
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
    }

    /// Counts one call to `site`, and draws its outcome.
    pub(crate) fn next(&self, site: &'static str) -> Draw {
        let mut state = self.state();
        if state.closed {
            return Draw::Pass;
        }
        if state.aborted.is_some() {
            return Draw::Over;
        }
        let calls = state.calls.entry(site).or_insert(0);
        let n = *calls;
        *calls += 1;
        let seed = self.seed;
        let mode = *state
            .modes
            .entry(site)
            .or_insert_with(|| mode_of(seed, site));
        match decide(seed, self.rate, mode, site, n) {
            None => Draw::Pass,
            Some(phase) => Draw::Fault(Fault {
                seed,
                site,
                n,
                phase,
            }),
        }
    }

    /// Ends the request with `fault`, writing its line, unless the request is
    /// already over. Returns false if the request has ended and there is
    /// nothing left to fault: the caller carries on.
    pub(crate) fn abort(&self, fault: Fault) -> bool {
        let mut state = self.state();
        if state.closed {
            return false;
        }
        if state.aborted.is_none() {
            (self.log)(&format!("indefinite-error: {fault}\n"));
            state.aborted = Some(fault);
        }
        true
    }

    pub(crate) fn aborted(&self) -> Option<Fault> {
        self.state().aborted.clone()
    }

    pub(crate) fn is_closed(&self) -> bool {
        self.state().closed
    }

    /// Forgets the fault that ended the request, keeping the counters: tests
    /// make one call per "request" without resetting the call numbers.
    #[cfg(test)]
    pub(crate) fn resume(&self) {
        self.state().aborted = None;
    }

    /// Calls per site so far, for tests. A missing site was never reached.
    #[cfg(test)]
    pub(crate) fn calls(&self) -> HashMap<&'static str, u64> {
        self.state().calls.clone()
    }
}

thread_local! {
    static CURRENT: RefCell<Option<Arc<Injection>>> = const { RefCell::new(None) };
}

/// The injection of the request being polled on this thread, if any.
pub(crate) fn current() -> Option<Arc<Injection>> {
    CURRENT.with(|current| current.borrow().clone())
}

/// Runs `f` with `injection` current, open or closed: as if a future that
/// outlived its request were polled later.
#[cfg(test)]
pub(crate) fn with_current<R>(injection: &Arc<Injection>, f: impl FnOnce() -> R) -> R {
    let previous = CURRENT.with(|current| current.replace(Some(injection.clone())));
    let _restore = Entered(previous);
    f()
}

/// Restores the previous current injection, even if the poll panics.
struct Entered(Option<Arc<Injection>>);

impl Drop for Entered {
    fn drop(&mut self) {
        CURRENT.with(|current| *current.borrow_mut() = self.0.take());
    }
}

/// A request's hold on its injection. Dropping it closes the injection: a
/// site call that outlives the request calls through with no faults.
pub(crate) struct Scope(Arc<Injection>);

impl Scope {
    /// Opens an injection seeded `seed`. Panics inside another open one: the
    /// middleware is installed twice.
    pub(crate) fn new(seed: i64, log: Log) -> Scope {
        if let Some(active) = current().filter(|active| !active.is_closed()) {
            panic!(
                "indefinite: a request seeded {seed} inside one seeded {}: \
                 is IndefiniteLayer installed twice?",
                active.seed
            );
        }
        Scope(Arc::new(Injection {
            seed,
            rate: rate_of(seed),
            log,
            state: Mutex::default(),
        }))
    }

    #[cfg(test)]
    pub(crate) fn injection(&self) -> &Arc<Injection> {
        &self.0
    }

    /// Runs `f` with this injection current on this thread.
    pub(crate) fn enter<R>(&self, f: impl FnOnce() -> R) -> R {
        let previous = CURRENT.with(|current| current.replace(Some(self.0.clone())));
        let _restore = Entered(previous);
        f()
    }

    /// Polls `fut` inside this injection. `Ready(Err(fault))` once a fault has
    /// ended the request: then drop `fut` without polling it again, so its
    /// destructors run, as they would for a cancelled request.
    pub(crate) fn poll_in<F: Future + ?Sized>(
        &self,
        fut: Pin<&mut F>,
        cx: &mut Context<'_>,
    ) -> Poll<Result<F::Output, Fault>> {
        self.poll_with(|| fut.poll(cx))
    }

    /// [`Scope::poll_in`] for any poll: a future's, a body's, a stream's.
    pub(crate) fn poll_with<T>(&self, poll: impl FnOnce() -> Poll<T>) -> Poll<Result<T, Fault>> {
        let poll = self.enter(poll);
        // A fault outranks whatever the request did in the same poll.
        match (self.0.aborted(), poll) {
            (Some(fault), _) => Poll::Ready(Err(fault)),
            (None, Poll::Ready(out)) => Poll::Ready(Ok(out)),
            (None, Poll::Pending) => Poll::Pending,
        }
    }
}

impl Drop for Scope {
    fn drop(&mut self) {
        self.0.state().closed = true;
    }
}
