//! A retrying client must keep the bank's books right under indefinite errors.
//!
//! Each run makes DEPOSITS deposits of 1 against a fresh bank, every request
//! with its own seed, retrying whenever the outcome is indefinite -- as a real
//! client would after a timeout. Then it checks one invariant: the balance is
//! exactly DEPOSITS.
//!
//! ```sh
//! cargo test
//! ```
//!
//! The fault lines the layer writes to stderr are not captured by `cargo test`,
//! so a passing run still prints them; they are what the failing run is read
//! against.

use std::fmt;

use axum::Router;
use axum::body::Body;
use axum::http::{Request, StatusCode};
use http_body_util::BodyExt; // for collect
use indefinite_error::{FAULT_HEADER, SEED_HEADER};
use indefinite_error_bank::{Store, app};
use tower::ServiceExt; // for oneshot

const RUNS: i64 = 50;
const DEPOSITS: i64 = 20;
const ATTEMPTS: i64 = 30; // a seed may fault half its requests: retry until one gets through

/// One seed per request, derived from the run: replay a run, replay its faults.
fn seed(run: i64, step: i64, attempt: i64) -> i64 {
    run * 1_000_000 + step * 1_000 + attempt
}

/// What one run did: its final balance, and every fault the server reported.
#[derive(Default)]
struct Run {
    balance: i64,
    faults: Vec<String>,
}

impl Run {
    fn ok(&self) -> bool {
        self.balance == DEPOSITS
    }
}

impl fmt::Display for Run {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            f,
            "balance {}, expected {DEPOSITS}; faults: {:?}",
            self.balance, self.faults
        )
    }
}

/// One request. Returns the status and the fault header, if any.
async fn call(
    app: &Router,
    method: &str,
    path: &str,
    body: &str,
    seed: i64,
) -> (StatusCode, Option<String>) {
    let request = Request::builder()
        .method(method)
        .uri(path)
        .header("content-type", "application/json")
        .header(SEED_HEADER, seed.to_string())
        .body(Body::from(body.to_owned()))
        .unwrap();
    let response = app.clone().oneshot(request).await.unwrap();
    let status = response.status();
    let fault = response
        .headers()
        .get(FAULT_HEADER)
        .map(|value| value.to_str().unwrap().to_owned());
    // Drain the body: the request ends when its body is finished.
    response.into_body().collect().await.unwrap();
    (status, fault)
}

async fn balance(app: &Router, account: &str) -> i64 {
    let request = Request::builder()
        .uri(format!("/accounts/{account}"))
        .body(Body::empty())
        .unwrap();
    let response = app.clone().oneshot(request).await.unwrap();
    let bytes = response.into_body().collect().await.unwrap().to_bytes();
    let json: serde_json::Value = serde_json::from_slice(&bytes).unwrap();
    json["balance"].as_i64().unwrap()
}

async fn run_deposits(run: i64, path: &str, body: impl Fn(i64) -> String) -> Run {
    let app = app(Store::new(), true);
    let mut result = Run::default();
    for step in 0..DEPOSITS {
        let mut got_through = false;
        for attempt in 0..ATTEMPTS {
            let (status, fault) =
                call(&app, "POST", path, &body(step), seed(run, step, attempt)).await;
            match status {
                StatusCode::OK => {
                    got_through = true;
                    break;
                }
                // Indefinite: it may or may not have happened. The fault header
                // is for us, debugging; the client logic must not read it. Retry.
                StatusCode::INTERNAL_SERVER_ERROR => {
                    result.faults.push(fault.expect("a 500 names its fault"));
                }
                other => panic!("unexpected status {other}"),
            }
        }
        assert!(
            got_through,
            "step {step} never got through in {ATTEMPTS} attempts"
        );
    }
    result.balance = balance(&app, "alice").await;
    result
}

fn keyed(step: i64) -> String {
    // The key is fixed per deposit, not per attempt: a retry is the same deposit.
    format!(r#"{{"key":"deposit-{step}","account":"alice","amount":1}}"#)
}

fn unkeyed(_step: i64) -> String {
    r#"{"account":"alice","amount":1}"#.to_owned()
}

#[tokio::test]
async fn keyed_deposits_count_exactly_once() {
    for run in 0..RUNS {
        let result = run_deposits(run, "/deposits", keyed).await;
        assert!(result.ok(), "run {run}: {result}");
    }
}

#[tokio::test]
async fn unkeyed_deposits_double_count_and_the_faults_say_why() {
    // The bug this library exists to find, found: an AFTER fault, then a retry.
    let mut broken = Vec::new();
    for run in 0..RUNS {
        let result = run_deposits(run, "/deposits/unkeyed", unkeyed).await;
        if !result.ok() {
            broken.push((run, result));
        }
    }
    assert!(
        !broken.is_empty(),
        "no run double-counted: the faults never reached store.deposit?"
    );
    for (run, result) in &broken {
        assert!(
            result.balance > DEPOSITS,
            "run {run}: retries only ever add, got {}",
            result.balance
        );
        assert!(
            result
                .faults
                .iter()
                .any(|fault| fault.starts_with("after store.deposit#")),
            "run {run}: {result}"
        );
    }
}
