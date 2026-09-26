//! A tiny bank: deposits into accounts, kept in memory.
//!
//! Two ways to deposit. `POST /deposits/unkeyed` just adds the amount;
//! `POST /deposits` carries an idempotency key and applies each key once.
//! Under indefinite errors a retrying client double-counts with the first and
//! never with the second -- which the tests in `tests/bank.rs` show.
//!
//! Run it with faults on, and send a seed per request:
//!
//! ```sh
//! INDEFINITE_ERRORS=1 cargo run
//! ```

use std::collections::{HashMap, HashSet};
use std::sync::{Arc, Mutex};

use axum::extract::{Path, State};
use axum::routing::{get, post};
use axum::{Json, Router};
use indefinite_error::{IndefiniteLayer, Site};
use serde::{Deserialize, Serialize};

/// The boundary write whose outcome can get lost.
pub static DEPOSIT: Site = Site::new("store.deposit");

/// The keyed boundary write: the same money, applied at most once.
pub static DEPOSIT_ONCE: Site = Site::new("store.deposit_once");

/// The bank's state. Its writes are the boundary where outcomes get lost.
#[derive(Clone, Default)]
pub struct Store {
    inner: Arc<Mutex<Inner>>,
}

#[derive(Default)]
struct Inner {
    accounts: HashMap<String, i64>,
    applied: HashSet<String>,
}

impl Store {
    /// An empty bank.
    #[must_use]
    pub fn new() -> Store {
        Store::default()
    }

    /// Adds `amount` to `account`.
    ///
    /// The call runs under [`DEPOSIT`], so under injection a fault lands right
    /// before it (the write never happened) or right after it (it committed,
    /// but the response is lost).
    pub async fn deposit(&self, account: &str, amount: i64) {
        DEPOSIT
            .run(async {
                let mut inner = self.inner.lock().unwrap();
                *inner.accounts.entry(account.to_owned()).or_default() += amount;
            })
            .await;
    }

    /// Applies `key` once: the first call adds `amount` to `account`, and every
    /// later call with the same key adds nothing.
    ///
    /// The key and the money are one critical section, so a retry either finds
    /// the key already recorded or applies both -- never one without the other.
    pub async fn deposit_once(&self, key: &str, account: &str, amount: i64) {
        DEPOSIT_ONCE
            .run(async {
                let mut inner = self.inner.lock().unwrap();
                if inner.applied.insert(key.to_owned()) {
                    *inner.accounts.entry(account.to_owned()).or_default() += amount;
                }
            })
            .await;
    }

    /// The account's balance, or `0` if it was never touched.
    #[must_use]
    pub fn balance(&self, account: &str) -> i64 {
        self.inner
            .lock()
            .unwrap()
            .accounts
            .get(account)
            .copied()
            .unwrap_or(0)
    }
}

#[derive(Deserialize)]
struct DepositBody {
    account: String,
    amount: i64,
}

#[derive(Deserialize)]
struct KeyedDepositBody {
    key: String,
    account: String,
    amount: i64,
}

#[derive(Serialize)]
struct Status {
    status: &'static str,
}

#[derive(Serialize)]
struct Balance {
    balance: i64,
}

/// The bank as an axum router.
///
/// `indefinite_errors` installs the layer that faults requests carrying a
/// seed. It is never used in production: any caller could fault the server.
pub fn app(store: Store, indefinite_errors: bool) -> Router {
    let router = Router::new()
        .route("/deposits/unkeyed", post(deposit_unkeyed))
        .route("/deposits", post(deposit))
        .route("/accounts/{account}", get(balance))
        .with_state(store);
    if indefinite_errors {
        router.layer(IndefiniteLayer::new())
    } else {
        router
    }
}

async fn deposit_unkeyed(
    State(store): State<Store>,
    Json(body): Json<DepositBody>,
) -> Json<Status> {
    store.deposit(&body.account, body.amount).await;
    Json(Status { status: "ok" })
}

async fn deposit(State(store): State<Store>, Json(body): Json<KeyedDepositBody>) -> Json<Status> {
    store
        .deposit_once(&body.key, &body.account, body.amount)
        .await;
    Json(Status { status: "ok" })
}

async fn balance(State(store): State<Store>, Path(account): Path<String>) -> Json<Balance> {
    Json(Balance {
        balance: store.balance(&account),
    })
}
