//! The README is a contract: `cargo test` runs its examples as doctests, and
//! these check that the output it shows is what they print.

use std::sync::{Arc, Mutex};

use axum::Router;
use axum::body::Body;
use axum::routing::post;
use http::Request;
use tower::ServiceExt;

use super::*;
use crate::{IndefiniteLayer, SEED_HEADER};

const README: &str = include_str!("../../README.md");

fn shown(block: &str) -> bool {
    README.contains(&format!("```text\n{block}```"))
}

#[test]
fn the_fault_line_is_real() {
    // It is what seed 13 writes on the fifth call to app.get, in every language.
    let get = Site::new("app.get");
    let lines = Lines::default();
    let scope = Scope::new(13, lines.log());
    for _ in 0..4 {
        catch(&scope, get.run(async {})).expect("an earlier call faulted");
    }
    catch(&scope, get.run(async {})).expect_err("the fifth call didn't fault");
    assert!(
        shown(&lines.text()),
        "the README doesn't show what seed 13 writes: {:?}",
        lines.text()
    );
}

#[tokio::test]
async fn try_it_prints_what_it_shows() {
    // The Try it example, with its stderr captured.
    static ADD: Site = Site::new("ledger.add");
    let ledger = Arc::new(Mutex::new(Vec::new()));
    let l = ledger.clone();
    let deposit = move || async move {
        ADD.run(async { l.lock().unwrap().push(1) }).await;
        "ok"
    };
    let lines = Lines::default();
    let app = Router::new()
        .route("/deposits", post(deposit))
        .layer(IndefiniteLayer::with_log(lines.log()));

    let mut stdout = String::new();
    for seed in ["70", "74", "0"] {
        ledger.lock().unwrap().clear();
        let request = Request::post("/deposits")
            .header(SEED_HEADER, seed)
            .body(Body::empty())
            .unwrap();
        let status = app.clone().oneshot(request).await.unwrap().status();
        stdout.push_str(&format!(
            "seed={seed}: status={}, ledger={:?}\n",
            status.as_u16(),
            ledger.lock().unwrap()
        ));
    }
    let output = lines.text() + &stdout;
    assert!(
        shown(&output),
        "the README's Try it output isn't what it prints:\n{output}"
    );
}
