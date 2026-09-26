//! Serves the bank with faults on, so a client can `curl` it by hand.
//!
//! ```sh
//! INDEFINITE_ERRORS=1 cargo run
//! ```

use std::env;

use indefinite_error_bank::{Store, app};

#[tokio::main]
async fn main() {
    let faults = env::var_os("INDEFINITE_ERRORS").is_some();
    let listener = tokio::net::TcpListener::bind("127.0.0.1:8000")
        .await
        .expect("bind 127.0.0.1:8000");
    println!(
        "bank listening on http://{}",
        listener.local_addr().unwrap()
    );
    if faults {
        println!("indefinite errors on: requests with x-indefinite-seed get faults");
    }
    axum::serve(listener, app(Store::new(), faults))
        .await
        .expect("serve");
}
