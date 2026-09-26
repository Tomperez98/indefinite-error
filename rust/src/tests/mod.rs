//! Shared test helpers. They fail the test themselves; none returns an error.

use std::future::Future;
use std::path::PathBuf;
use std::pin::pin;
use std::sync::{Arc, Mutex};
use std::task::{Context, Poll, Waker};

use crate::Site;
use crate::injection::{Fault, Log, Scope};

mod contract;
mod core;
mod injection;
mod layer;
mod readme;

pub(crate) const NUM_SEEDS: i64 = 300; // seeds 0..299
pub(crate) const CALLS: u64 = 50;

/// A `&'static str` for a site name built at runtime.
pub(crate) fn leak(name: impl Into<String>) -> &'static str {
    Box::leak(name.into().into_boxed_str())
}

/// A log that keeps every line, for tests to read.
#[derive(Clone, Default)]
pub(crate) struct Lines(Arc<Mutex<String>>);

impl Lines {
    pub(crate) fn log(&self) -> Log {
        let lines = self.0.clone();
        Arc::new(move |line| lines.lock().unwrap().push_str(line))
    }

    pub(crate) fn text(&self) -> String {
        self.0.lock().unwrap().clone()
    }
}

pub(crate) fn discard() -> Log {
    Arc::new(|_| {})
}

pub(crate) fn scope(seed: i64) -> Scope {
    Scope::new(seed, discard())
}

/// Polls `fut` to completion outside any request. Test futures never wait.
pub(crate) fn block_on<F: Future>(fut: F) -> F::Output {
    let mut fut = pin!(fut);
    match fut.as_mut().poll(&mut Context::from_waker(Waker::noop())) {
        Poll::Ready(out) => out,
        Poll::Pending => panic!("pending outside a request"),
    }
}

/// Polls `fut` inside `scope`, as the middleware would: its output, or the
/// fault that ended it. The future is dropped before this returns.
pub(crate) fn drive<F: Future>(scope: &Scope, fut: F) -> Result<F::Output, Fault> {
    let mut fut = pin!(fut);
    match scope.poll_in(fut.as_mut(), &mut Context::from_waker(Waker::noop())) {
        Poll::Ready(out) => out,
        Poll::Pending => panic!("pending without a fault"),
    }
}

/// [`drive`], then carry on in the same injection, as if each call were its
/// own request that kept the call numbers. Stands in for Go's `recover` or
/// Python's `except _Abort`, which let a test go on after a fault.
pub(crate) fn catch<F: Future>(scope: &Scope, fut: F) -> Result<F::Output, Fault> {
    let result = drive(scope, fut);
    scope.injection().resume();
    result
}

/// A site that records each real execution of a call, and returns `i * 2`.
#[derive(Clone)]
pub(crate) struct Recorder {
    pub(crate) site: Site,
    pub(crate) ran: Arc<Mutex<Vec<u64>>>,
}

impl Recorder {
    pub(crate) fn new(site: &str) -> Recorder {
        Recorder {
            site: Site::new(leak(site)),
            ran: Arc::default(),
        }
    }

    pub(crate) fn call(&self, i: u64) -> impl Future<Output = u64> + use<> {
        let ran = self.ran.clone();
        self.site.run(async move {
            ran.lock().unwrap().push(i);
            i * 2
        })
    }

    pub(crate) fn ran(&self) -> Vec<u64> {
        self.ran.lock().unwrap().clone()
    }
}

/// "ok" or the injected phase: one call is one request.
pub(crate) fn outcome<T>(result: Result<T, Fault>) -> String {
    match result {
        Ok(_) => "ok".to_owned(),
        Err(fault) => fault.phase.to_string(),
    }
}

pub(crate) fn attempt(scope: &Scope, rec: &Recorder, i: u64) -> String {
    let result = catch(scope, rec.call(i));
    if let Ok(got) = result {
        assert_eq!(got, i * 2, "call {i} returned the wrong value");
    }
    outcome(result)
}

/// The outcomes of `n` calls to `rec` in one injection.
pub(crate) fn run(seed: i64, rec: &Recorder, n: u64) -> Vec<String> {
    let scope = scope(seed);
    (0..n).map(|i| attempt(&scope, rec, i)).collect()
}

/// The repository's `spec/`: the contract every implementation is tested
/// against. A crate downloaded from a registry has no `spec/` beside it, so
/// there the spec tests pass vacuously. Set `INDEFINITE_ERROR_SPEC=required`
/// in CI, so in the repository a missing spec fails.
fn spec_path(name: &str) -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("../spec")
        .join(name)
}

/// The tab-separated rows of `spec/<name>`, without its `#` comments.
pub(crate) fn spec_rows(name: &str) -> Vec<Vec<String>> {
    let path = spec_path(name);
    let text = match std::fs::read_to_string(&path) {
        Ok(text) => text,
        Err(e)
            if e.kind() == std::io::ErrorKind::NotFound
                && std::env::var("INDEFINITE_ERROR_SPEC").as_deref() != Ok("required") =>
        {
            eprintln!(
                "skipping: no {}: not in a repository checkout",
                path.display()
            );
            return Vec::new();
        }
        Err(e) => panic!("{}: {e}", path.display()),
    };
    let rows: Vec<Vec<String>> = text
        .lines()
        .filter(|line| !line.is_empty() && !line.starts_with('#'))
        .map(|line| line.split('\t').map(str::to_owned).collect())
        .collect();
    assert!(!rows.is_empty(), "spec/{name} has no rows");
    rows
}

/// Decodes one JSON cell of a spec row.
pub(crate) fn spec_json<T: serde::de::DeserializeOwned>(cell: &str) -> T {
    serde_json::from_str(cell).unwrap_or_else(|e| panic!("spec cell {cell}: {e}"))
}

/// The panic message of `f`, which must panic.
pub(crate) fn panic_message(f: impl FnOnce()) -> String {
    let payload =
        std::panic::catch_unwind(std::panic::AssertUnwindSafe(f)).expect_err("did not panic");
    payload
        .downcast_ref::<String>()
        .cloned()
        .or_else(|| payload.downcast_ref::<&str>().map(|s| (*s).to_owned()))
        .unwrap_or_default()
}

#[track_caller]
pub(crate) fn must_panic(substr: &str, f: impl FnOnce()) {
    let msg = panic_message(f);
    assert!(
        msg.contains(substr),
        "panic {msg:?} does not contain {substr:?}"
    );
}
