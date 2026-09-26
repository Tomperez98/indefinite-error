//! The tower layer: one injection per request, and the table it answers with.

use std::collections::BTreeSet;
use std::convert::Infallible;
use std::net::SocketAddr;
use std::pin::Pin;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering::SeqCst};
use std::sync::{Arc, Mutex};
use std::task::{Context, Poll};
use std::time::Duration;

use axum::Router;
use axum::routing::any;
use bytes::Bytes;
use http::{HeaderMap, HeaderValue, Request, Response, StatusCode};
use http_body::{Body, Frame};
use http_body_util::{BodyExt, Full};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tower::{Layer, Service, ServiceBuilder, ServiceExt};

use super::*;
use crate::injection::current;
use crate::{FAULT_HEADER, Indefinite, IndefiniteBody, IndefiniteLayer, SEED_HEADER};

type BoxError = Box<dyn std::error::Error + Send + Sync>;

static APP_WRITE: Site = Site::new("app.write");
static APP_STREAM: Site = Site::new("app.stream");

/// The state a writing app changes.
#[derive(Clone, Default)]
struct Ledger(Arc<Mutex<Vec<u64>>>);

impl Ledger {
    fn len(&self) -> usize {
        self.0.lock().unwrap().len()
    }

    fn clear(&self) {
        self.0.lock().unwrap().clear();
    }
}

type App = tower::util::BoxCloneSyncService<Request<()>, Response<Full<Bytes>>, Infallible>;

/// Writes 1 to its ledger, then answers 201.
fn writing_app() -> (Ledger, App) {
    let ledger = Ledger::default();
    let l = ledger.clone();
    let app = tower::service_fn(move |_req: Request<()>| {
        let l = l.clone();
        async move {
            APP_WRITE.run(async { l.0.lock().unwrap().push(1) }).await;
            Ok(Response::builder()
                .status(201)
                .body(Full::from("created"))
                .unwrap())
        }
    });
    (ledger, App::new(app))
}

fn layer() -> IndefiniteLayer {
    IndefiniteLayer::with_log(discard())
}

fn seeded(seed: impl ToString) -> Request<()> {
    Request::post("/")
        .header(SEED_HEADER, seed.to_string())
        .body(())
        .unwrap()
}

struct Answer {
    status: StatusCode,
    headers: HeaderMap,
    body: Result<Bytes, BoxError>,
}

impl Answer {
    fn fault(&self) -> &str {
        self.headers
            .get(FAULT_HEADER)
            .map_or("", |v| v.to_str().unwrap())
    }

    /// Status and fault header: what a client can tell about the request.
    fn summary(&self) -> String {
        format!("{} {}", self.status.as_u16(), self.fault())
    }
}

async fn serve<S, B>(svc: &S, req: Request<()>) -> Answer
where
    S: Service<Request<()>, Response = Response<B>, Error = Infallible> + Clone,
    B: Body<Data = Bytes, Error = BoxError>,
{
    let (parts, body) = svc.clone().oneshot(req).await.unwrap().into_parts();
    let body = body.collect().await.map(|c| c.to_bytes());
    Answer {
        status: parts.status,
        headers: parts.headers,
        body,
    }
}

// --- The table ---

#[tokio::test]
async fn rows_match_the_table() {
    // No fault: the real response; before: 500, unchanged; after: 500, changed.
    let (ledger, app) = writing_app();
    let h = layer().layer(app);
    let mut rows = BTreeSet::new();
    for seed in 0..NUM_SEEDS {
        ledger.clear();
        let answer = serve(&h, seeded(seed)).await;
        let phase = answer.fault().split(' ').next().unwrap().to_owned();
        rows.insert(format!(
            "{} {phase} changed={}",
            answer.status.as_u16(),
            ledger.len() == 1
        ));
        if !answer.fault().is_empty() {
            assert!(
                answer.fault().ends_with(&format!("(seed={seed})")),
                "{} doesn't name seed {seed}",
                answer.fault()
            );
        }
    }
    let want = [
        "201  changed=true",
        "500 after changed=true",
        "500 before changed=false",
    ];
    assert_eq!(rows, BTreeSet::from(want.map(String::from)));
}

#[tokio::test]
async fn the_fault_response_is_fresh() {
    // An empty body, and none of the headers the lost request set -- but the
    // ones middleware outside the layer sets survive.
    let inner = tower::service_fn(|_req: Request<()>| async {
        APP_WRITE.run(async {}).await;
        let res = Response::builder()
            .header("content-type", "application/json")
            .header("x-outer", "overwritten");
        Ok::<_, Infallible>(res.body(Full::from("{}")).unwrap())
    });
    let h = ServiceBuilder::new()
        .map_response(|mut res: Response<_>| {
            res.headers_mut()
                .insert("x-outer", HeaderValue::from_static("kept"));
            res
        })
        .layer(layer())
        .service(inner);
    for seed in 0..NUM_SEEDS {
        let answer = serve(&h, seeded(seed)).await;
        if answer.status == StatusCode::INTERNAL_SERVER_ERROR {
            assert_eq!(answer.body.unwrap(), "");
            assert_eq!(answer.headers.get("content-type"), None);
            assert_eq!(answer.headers.get("x-outer").unwrap(), "kept");
            return;
        }
    }
    panic!("no seed faulted");
}

#[tokio::test]
async fn the_seed_is_a_whole_request_input() {
    // The same seed takes the same path at request 1 or request 100.
    let (_, app) = writing_app();
    let h = layer().layer(app);
    let mut first = Vec::new();
    for seed in 0..50 {
        first.push(serve(&h, seeded(seed)).await.summary());
    }
    for other in 0..100 {
        serve(&h, seeded(1000 + other)).await;
    }
    for (seed, want) in first.iter().enumerate() {
        assert_eq!(&serve(&h, seeded(seed)).await.summary(), want);
    }
}

#[tokio::test]
async fn request_faults_write_the_line() {
    let (_, app) = writing_app();
    let lines = Lines::default();
    let h = IndefiniteLayer::with_log(lines.log()).layer(app);
    let mut want = String::new();
    for seed in 0..NUM_SEEDS {
        let answer = serve(&h, seeded(seed)).await;
        if !answer.fault().is_empty() {
            want.push_str(&format!("indefinite-error: {}\n", answer.fault()));
        }
    }
    assert!(!want.is_empty());
    assert_eq!(lines.text(), want);
}

// --- Over the wire ---

/// A real server on a free port.
async fn listen(app: Router) -> SocketAddr {
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let addr = listener.local_addr().unwrap();
    tokio::spawn(async move { axum::serve(listener, app).await.unwrap() });
    addr
}

struct Wire {
    status: u16,
    fault: String,
    body: Vec<u8>,
    content_length: Option<usize>,
}

/// One HTTP/1.1 request on its own connection, read until the server closes it.
async fn wire(addr: SocketAddr, seed: i64) -> Wire {
    let mut conn = tokio::net::TcpStream::connect(addr).await.unwrap();
    let req = format!(
        "POST / HTTP/1.1\r\nhost: test\r\nconnection: close\r\ncontent-length: 0\r\n{SEED_HEADER}: {seed}\r\n\r\n"
    );
    conn.write_all(req.as_bytes()).await.unwrap();
    let mut raw = Vec::new();
    let _ = conn.read_to_end(&mut raw).await; // a reset is a cut connection too
    let split = raw
        .windows(4)
        .position(|w| w == b"\r\n\r\n")
        .expect("no end of headers");
    let head = String::from_utf8(raw[..split].to_vec()).unwrap();
    let header = |name: &str| {
        head.lines().skip(1).find_map(|line| {
            let (k, v) = line.split_once(':')?;
            k.eq_ignore_ascii_case(name).then(|| v.trim().to_owned())
        })
    };
    Wire {
        status: head.split(' ').nth(1).unwrap().parse().unwrap(),
        fault: header(FAULT_HEADER.as_str()).unwrap_or_default(),
        content_length: header("content-length").map(|v| v.parse().unwrap()),
        body: raw[split + 4..].to_vec(),
    }
}

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn a_fault_ends_one_request_not_its_neighbours() {
    // Concurrent requests to a real server: each gets the response it gets alone.
    let (_, app) = writing_app();
    let h = layer().layer(app.clone());
    let mut alone = Vec::new();
    for seed in 0..NUM_SEEDS {
        alone.push(serve(&h, seeded(seed)).await.summary());
    }

    // Every request waits until two have been in the app at once, so the
    // requests provably overlap; a stuck wait means they ran one at a time.
    let in_app = Arc::new(AtomicUsize::new(0));
    let overlapped = Arc::new((AtomicBool::new(false), tokio::sync::Notify::new()));
    let handler = any(move |req: axum::extract::Request| {
        let (app, in_app, overlapped) = (app.clone(), in_app.clone(), overlapped.clone());
        async move {
            let notified = overlapped.1.notified();
            tokio::pin!(notified);
            notified.as_mut().enable();
            if in_app.fetch_add(1, SeqCst) + 1 >= 2 {
                overlapped.0.store(true, SeqCst);
                overlapped.1.notify_waiters();
            }
            if !overlapped.0.load(SeqCst) {
                tokio::time::timeout(Duration::from_secs(5), notified)
                    .await
                    .expect("no two requests were ever in the app at once");
            }
            let res = app.oneshot(req.map(|_| ())).await.unwrap();
            in_app.fetch_sub(1, SeqCst);
            res
        }
    });
    let addr = listen(Router::new().route("/", handler).layer(layer())).await;

    let slots = Arc::new(tokio::sync::Semaphore::new(32)); // stay under the listen backlog
    let requests: Vec<_> = (0..NUM_SEEDS)
        .map(|seed| {
            let slots = slots.clone();
            tokio::spawn(async move {
                let _slot = slots.acquire().await.unwrap();
                let w = wire(addr, seed).await;
                format!("{} {}", w.status, w.fault)
            })
        })
        .collect();
    let mut together = Vec::new();
    for r in requests {
        together.push(r.await.unwrap());
    }
    assert_eq!(together, alone);
}

// --- Too late to answer ---

/// "chunk", a yield (so the server flushes it), a call to app.stream, then
/// "chunk": a site call mid-response.
struct Streaming {
    step: u8,
    call: Pin<Box<crate::Run<std::future::Ready<()>>>>,
}

impl Streaming {
    fn new() -> Streaming {
        Streaming {
            step: 0,
            call: Box::pin(APP_STREAM.run(std::future::ready(()))),
        }
    }
}

impl Body for Streaming {
    type Data = Bytes;
    type Error = Infallible;

    fn poll_frame(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
    ) -> Poll<Option<Result<Frame<Bytes>, Infallible>>> {
        self.step += 1;
        match self.step {
            1 => Poll::Ready(Some(Ok(Frame::data(Bytes::from_static(b"chunk"))))),
            2 => {
                cx.waker().wake_by_ref();
                Poll::Pending
            }
            3 => {
                std::task::ready!(self.call.as_mut().poll(cx));
                Poll::Ready(Some(Ok(Frame::data(Bytes::from_static(b"chunk")))))
            }
            _ => Poll::Ready(None),
        }
    }
}

use std::future::Future;

#[tokio::test]
async fn too_late_to_answer_fails_the_body() {
    let h = layer().layer(tower::service_fn(|_req: Request<()>| async {
        Ok::<_, Infallible>(Response::new(Streaming::new()))
    }));
    let mut outcomes = BTreeSet::new();
    for seed in 0..NUM_SEEDS {
        let answer = serve(&h, seeded(seed)).await;
        assert_eq!(answer.status, StatusCode::OK, "the response had started");
        match answer.body {
            Ok(body) => {
                assert_eq!(body, "chunkchunk");
                outcomes.insert("whole".to_owned());
            }
            Err(e) => {
                let msg = e.to_string();
                assert!(
                    msg.starts_with("indefinite-error: ")
                        && msg.contains(&format!("app.stream#0 (seed={seed})")),
                    "{msg}"
                );
                assert!(
                    msg.ends_with("too late to answer; dropping the connection"),
                    "{msg}"
                );
                outcomes.insert("cut".to_owned());
            }
        }
    }
    assert_eq!(
        outcomes,
        BTreeSet::from(["cut".to_owned(), "whole".to_owned()])
    );
}

#[tokio::test]
async fn too_late_to_answer_over_the_wire() {
    // The server closes the connection mid-response; the client sees it cut.
    let handler = any(|| async {
        Response::builder()
            .header("content-length", "10")
            .body(Streaming::new())
            .unwrap()
    });
    let addr = listen(Router::new().route("/", handler).layer(layer())).await;
    let mut outcomes = BTreeSet::new();
    for seed in 0..NUM_SEEDS {
        let w = wire(addr, seed).await;
        assert_eq!((w.status, w.content_length), (200, Some(10)));
        match w.body.as_slice() {
            b"chunkchunk" => outcomes.insert("whole"),
            b"chunk" => outcomes.insert("cut"),
            body => panic!("seed {seed}: body {:?}", String::from_utf8_lossy(body)),
        };
    }
    assert_eq!(outcomes, BTreeSet::from(["cut", "whole"]));
}

// --- What the layer passes through untouched ---

#[tokio::test]
async fn passes_through_without_a_seed() {
    let (ledger, app) = writing_app();
    let h = layer().layer(tower::service_fn(move |req| {
        assert!(
            current().is_none(),
            "a request without a seed got an injection"
        );
        app.clone().oneshot(req)
    }));
    for _ in 0..50 {
        let answer = serve(&h, Request::post("/").body(()).unwrap()).await;
        assert_eq!(
            (answer.status, answer.body.unwrap()),
            (StatusCode::CREATED, Bytes::from("created"))
        );
    }
    assert_eq!(ledger.len(), 50);
}

#[tokio::test]
async fn an_app_error_or_panic_is_not_ours_to_answer() {
    let failing = layer().layer(tower::service_fn(|_req: Request<()>| async {
        Err::<Response<Full<Bytes>>, _>("app error")
    }));
    assert_eq!(failing.oneshot(seeded(1)).await.unwrap_err(), "app error");

    let panicking = layer().layer(tower::service_fn(|_req: Request<()>| async {
        panic!("app panic");
        #[allow(unreachable_code)]
        Ok::<Response<Full<Bytes>>, Infallible>(Response::default())
    }));
    let err = tokio::spawn(panicking.oneshot(seeded(1)))
        .await
        .unwrap_err();
    assert_eq!(*err.into_panic().downcast::<&str>().unwrap(), "app panic");
}

#[tokio::test]
async fn the_seed_header_matches_the_spec() {
    // A request runs under the spec's seed, or gets a 400 and never reaches the app.
    for row in spec_rows("seed-header.tsv") {
        assert_eq!(row.len(), 2, "malformed seed-header row {row:?}");
        let seeds = Arc::new(Mutex::new(Vec::new()));
        let s = seeds.clone();
        let h = layer().layer(tower::service_fn(move |_req: Request<()>| {
            s.lock()
                .unwrap()
                .push(current().expect("no injection").seed);
            async { Ok::<_, Infallible>(Response::new(Full::<Bytes>::default())) }
        }));
        let mut req = Request::post("/").body(()).unwrap();
        for value in spec_json::<Vec<String>>(&row[0]) {
            req.headers_mut().append(
                SEED_HEADER,
                HeaderValue::from_bytes(value.as_bytes()).unwrap(),
            );
        }
        let status = serve(&h, req).await.status.as_u16();
        let got = format!("{status} {:?}", seeds.lock().unwrap());
        let want = if row[1] == "400" {
            "400 []".to_owned()
        } else {
            format!("200 [{}]", row[1])
        };
        assert_eq!(got, want, "{}", row[0]);
    }
}

#[tokio::test]
async fn a_malformed_seed_is_a_400() {
    // Bytes the spec's UTF-8 can't carry get a 400 too; the body says why.
    for raw in [&b"abc"[..], b"\xff", &[b'1'; 65]] {
        let (ledger, app) = writing_app();
        let mut req = Request::post("/").body(()).unwrap();
        req.headers_mut()
            .insert(SEED_HEADER, HeaderValue::from_bytes(raw).unwrap());
        let answer = serve(&layer().layer(app), req).await;
        assert_eq!(answer.status, StatusCode::BAD_REQUEST, "{raw:?}");
        assert_eq!(
            answer.body.unwrap(),
            "X-Indefinite-Seed must be a decimal int64"
        );
        assert_eq!(
            answer.headers.get("content-type").unwrap(),
            "text/plain; charset=utf-8"
        );
        assert_eq!(ledger.len(), 0, "{raw:?}: the app ran");
    }
}

#[tokio::test]
async fn installed_twice_is_a_loud_misconfiguration() {
    let (_, app) = writing_app();
    let h = layer().layer(layer().layer(app));
    must_panic(
        "a request seeded 1 inside one seeded 1: is IndefiniteLayer installed twice?",
        || {
            drop(h.clone().call(seeded(1)));
        },
    );
}

#[tokio::test]
async fn the_public_constructors_serve_the_app() {
    // They log to stderr; the rest of the file injects a log to read it.
    let (_, app) = writing_app();
    for h in [
        IndefiniteLayer::new().layer(app.clone()),
        IndefiniteLayer::default().layer(app.clone()),
        Indefinite::new(app),
    ] {
        let answer = serve(&h, Request::post("/").body(()).unwrap()).await;
        assert_eq!(answer.status, StatusCode::CREATED);
    }
}

// --- The body and future adapters hyper relies on ---

/// Polls `body` for its next frame. Test bodies wake themselves when pending.
fn next_frame<B: Body + Unpin>(body: &mut B) -> Option<Result<Frame<B::Data>, B::Error>> {
    let cx = &mut Context::from_waker(std::task::Waker::noop());
    for _ in 0..10 {
        if let Poll::Ready(frame) = Pin::new(&mut *body).poll_frame(cx) {
            return frame;
        }
    }
    panic!("the body never produced a frame");
}

/// (is_end_stream, exact size) of a body; `None` if the size isn't exact.
type Framing = (bool, Option<u64>);

fn framing<B: Body>(body: &B) -> Framing {
    (body.is_end_stream(), body.size_hint().exact())
}

#[test]
fn every_body_reports_its_framing() {
    // hyper sets Content-Length and decides connection reuse from these.
    type B = IndefiniteBody<Full<Bytes>>;
    let full = || Full::from("abc");
    let cases: [(&str, B, Framing); 5] = [
        ("passthrough", B::passthrough(full()), (false, Some(3))),
        ("live", B::live(full(), scope(0)), (false, Some(3))),
        (
            "live, empty",
            B::live(Full::default(), scope(0)),
            (true, Some(0)),
        ),
        (
            "full",
            B::full(Bytes::from_static(b"abc")),
            (false, Some(3)),
        ),
        ("full, empty", B::full(Bytes::new()), (true, Some(0))),
    ];
    for (name, mut body, before) in cases {
        assert_eq!(framing(&body), before, "{name}, unread");
        let mut read: Vec<u8> = Vec::new();
        while let Some(frame) = next_frame(&mut body) {
            read.extend_from_slice(&frame.unwrap().into_data().unwrap());
        }
        assert_eq!(read, &b"abc"[..before.1.unwrap() as usize], "{name}");
        assert_eq!(framing(&body), (true, Some(0)), "{name}, read to the end");
        assert!(
            next_frame(&mut body).is_none(),
            "{name}: a frame after the end"
        );
    }
}

#[test]
fn a_cut_body_ends_after_its_error() {
    let seed = (0..NUM_SEEDS)
        .find(|&seed| {
            let mut body = IndefiniteBody::live(Streaming::new(), scope(seed));
            next_frame(&mut body);
            next_frame(&mut body).unwrap().is_err()
        })
        .expect("no seed faulted app.stream");
    let mut body = IndefiniteBody::live(Streaming::new(), scope(seed));
    assert_eq!(framing(&body), (false, None));
    assert_eq!(
        next_frame(&mut body).unwrap().unwrap().into_data().unwrap(),
        "chunk"
    );
    let err = next_frame(&mut body).unwrap().unwrap_err();
    assert!(err.to_string().contains("too late to answer"), "{err}");
    assert_eq!(framing(&body), (true, Some(0)));
    assert!(next_frame(&mut body).is_none(), "a frame after the cut");
}

#[test]
fn a_response_future_panics_if_polled_after_completion() {
    let (_, app) = writing_app();
    let mut h = layer().layer(app);
    let cx = &mut Context::from_waker(std::task::Waker::noop());
    for req in [seeded(0), seeded("bad")] {
        let mut fut = std::pin::pin!(h.call(req));
        assert!(fut.as_mut().poll(cx).is_ready());
        must_panic("ResponseFuture polled after completion", || {
            let _ = fut.as_mut().poll(cx);
        });
    }
}

#[test]
fn the_wrapped_service_is_reachable() {
    let mut h = Indefinite::new(7);
    assert_eq!(*h.get_ref(), 7);
    *h.get_mut() += 1;
    assert_eq!(h.into_inner(), 8);
}
