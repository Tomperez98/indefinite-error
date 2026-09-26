//! The tower middleware: each request carrying [`SEED_HEADER`] runs inside its
//! own injection, driven by that seed.

use std::error::Error;
use std::fmt;
use std::future::Future;
use std::pin::Pin;
use std::task::{Context, Poll, ready};

use bytes::Bytes;
use http::header::{CONTENT_TYPE, HeaderName, HeaderValue};
use http::{HeaderMap, Request, Response, StatusCode};
use http_body::{Body, Frame, SizeHint};
use pin_project_lite::pin_project;
use tower_layer::Layer;
use tower_service::Service;

use crate::injection::{self, Fault, Log, Scope};

/// Carries a request's seed: one decimal `i64`, such as `13` or `-7`.
pub const SEED_HEADER: HeaderName = HeaderName::from_static("x-indefinite-seed");

/// Names the fault that ended a request: `after db.commit#0 (seed=13)`.
/// It is for debugging; a test's assertions must not read it.
pub const FAULT_HEADER: HeaderName = HeaderName::from_static("x-indefinite-fault");

type BoxError = Box<dyn Error + Send + Sync>;

/// Applies [`Indefinite`] to a service: faults for every request that
/// carries [`SEED_HEADER`].
///
/// ```
/// use axum::{Router, routing::post};
/// use indefinite_error::IndefiniteLayer;
///
/// let faults = std::env::var_os("INDEFINITE_ERRORS").is_some(); // never in production
/// let app = Router::new().route("/deposits", post(|| async { "ok" }));
/// let app = if faults { app.layer(IndefiniteLayer::new()) } else { app };
/// # let _: Router = app;
/// ```
#[derive(Clone)]
pub struct IndefiniteLayer {
    log: Log,
}

impl IndefiniteLayer {
    /// A layer that writes fault lines to stderr.
    #[must_use]
    pub fn new() -> Self {
        IndefiniteLayer {
            log: injection::stderr(),
        }
    }

    #[cfg(test)]
    pub(crate) fn with_log(log: Log) -> Self {
        IndefiniteLayer { log }
    }
}

impl Default for IndefiniteLayer {
    fn default() -> Self {
        Self::new()
    }
}

impl fmt::Debug for IndefiniteLayer {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("IndefiniteLayer").finish_non_exhaustive()
    }
}

impl<S> Layer<S> for IndefiniteLayer {
    type Service = Indefinite<S>;

    fn layer(&self, inner: S) -> Indefinite<S> {
        Indefinite {
            inner,
            log: self.log.clone(),
        }
    }
}

/// Runs each request carrying [`SEED_HEADER`] inside its own injection, so
/// [`Site::run`](crate::Site::run) calls made while serving it can fault.
///
/// Requests without the header pass through untouched. A malformed seed --
/// anything but exactly one value matching `-?[0-9]{1,19}` that fits an
/// `i64` -- gets a `400`, and the inner service never sees the request.
///
/// A fault ends its request: `Indefinite` drops the inner service's future,
/// so destructors run as they would for a cancelled request, and answers
/// `500` with an empty body and a [`FAULT_HEADER`] naming the fault. That is
/// the closest a service can come to losing a request or its response. If
/// the response has already been returned, and the fault fires while its body
/// streams, the body yields an error instead, and the server drops the
/// connection mid-response.
///
/// Never install it in production: any caller could fault your server.
///
/// # Panics
///
/// On a seeded request, if another `Indefinite` is already serving it:
/// the layer is installed twice.
#[derive(Clone)]
pub struct Indefinite<S> {
    inner: S,
    log: Log,
}

impl<S> Indefinite<S> {
    /// Wraps `inner`, writing fault lines to stderr.
    pub fn new(inner: S) -> Self {
        IndefiniteLayer::new().layer(inner)
    }

    /// The wrapped service.
    pub fn get_ref(&self) -> &S {
        &self.inner
    }

    /// The wrapped service.
    pub fn get_mut(&mut self) -> &mut S {
        &mut self.inner
    }

    /// Unwraps the service.
    pub fn into_inner(self) -> S {
        self.inner
    }
}

impl<S: fmt::Debug> fmt::Debug for Indefinite<S> {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("Indefinite")
            .field("inner", &self.inner)
            .finish_non_exhaustive()
    }
}

impl<S, ReqBody, ResBody> Service<Request<ReqBody>> for Indefinite<S>
where
    S: Service<Request<ReqBody>, Response = Response<ResBody>>,
    ResBody: Body<Data = Bytes>,
    ResBody::Error: Into<BoxError>,
{
    type Response = Response<IndefiniteBody<ResBody>>;
    type Error = S::Error;
    type Future = ResponseFuture<S::Future>;

    fn poll_ready(&mut self, cx: &mut Context<'_>) -> Poll<Result<(), S::Error>> {
        self.inner.poll_ready(cx)
    }

    fn call(&mut self, req: Request<ReqBody>) -> Self::Future {
        let kind = match seed(req.headers()) {
            Seed::Absent => Kind::Passthrough {
                fut: self.inner.call(req),
            },
            Seed::Malformed => Kind::BadRequest,
            Seed::Valid(seed) => {
                let scope = Scope::new(seed, self.log.clone());
                let fut = scope.enter(|| self.inner.call(req));
                Kind::Seeded {
                    fut,
                    scope: Some(scope),
                }
            }
        };
        ResponseFuture { kind }
    }
}

enum Seed {
    Absent,
    Malformed,
    Valid(i64),
}

/// The request's seed, as `spec/seed-header.tsv` pins it. The header is
/// untrusted input: `+1`, ` 1`, `1_0`, and a repeated header are all malformed.
fn seed(headers: &HeaderMap) -> Seed {
    let mut values = headers.get_all(SEED_HEADER).iter();
    let (Some(value), None) = (values.next(), values.next()) else {
        return if headers.contains_key(SEED_HEADER) {
            Seed::Malformed
        } else {
            Seed::Absent
        };
    };
    let raw = value.as_bytes();
    let digits = raw.strip_prefix(b"-").unwrap_or(raw);
    if !(1..=19).contains(&digits.len()) || !digits.iter().all(u8::is_ascii_digit) {
        return Seed::Malformed;
    }
    // Only a range error is left: -?[0-9]{1,19} is always valid ASCII.
    std::str::from_utf8(raw)
        .ok()
        .and_then(|s| s.parse().ok())
        .map_or(Seed::Malformed, Seed::Valid)
}

pin_project! {
    /// The future [`Indefinite`] returns.
    pub struct ResponseFuture<F> {
        #[pin]
        kind: Kind<F>,
    }
}

pin_project! {
    #[project = KindProj]
    enum Kind<F> {
        Passthrough { #[pin] fut: F },
        Seeded { #[pin] fut: F, scope: Option<Scope> },
        BadRequest,
        Done,
    }
}

impl<F, B, E> Future for ResponseFuture<F>
where
    F: Future<Output = Result<Response<B>, E>>,
    B: Body<Data = Bytes>,
    B::Error: Into<BoxError>,
{
    type Output = Result<Response<IndefiniteBody<B>>, E>;

    fn poll(self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<Self::Output> {
        let mut kind = self.project().kind;
        let (result, scope) = match kind.as_mut().project() {
            KindProj::Passthrough { fut } => {
                let result = ready!(fut.poll(cx));
                return Poll::Ready(result.map(|res| res.map(IndefiniteBody::passthrough)));
            }
            KindProj::BadRequest => {
                kind.set(Kind::Done);
                return Poll::Ready(Ok(bad_request()));
            }
            KindProj::Done => panic!("ResponseFuture polled after completion"),
            KindProj::Seeded { fut, scope } => {
                let result = ready!(
                    scope
                        .as_ref()
                        .expect("taken only when done")
                        .poll_in(fut, cx)
                );
                (result, scope.take().expect("taken only when done"))
            }
        };
        // Drop the request's future before answering: its destructors run
        // inside the request, as a cancelled request's would.
        kind.set(Kind::Done);
        Poll::Ready(match result {
            Err(fault) => Ok(faulted(&fault)),
            Ok(Err(e)) => Err(e), // not ours to answer
            Ok(Ok(res)) => Ok(res.map(|body| IndefiniteBody::live(body, scope))),
        })
    }
}

fn bad_request<B>() -> Response<IndefiniteBody<B>> {
    let mut res = Response::new(IndefiniteBody::full(Bytes::from_static(
        b"X-Indefinite-Seed must be a decimal int64",
    )));
    *res.status_mut() = StatusCode::BAD_REQUEST;
    res.headers_mut().insert(
        CONTENT_TYPE,
        HeaderValue::from_static("text/plain; charset=utf-8"),
    );
    res
}

/// A fresh response: nothing the lost request did survives.
fn faulted<B>(fault: &Fault) -> Response<IndefiniteBody<B>> {
    let mut res = Response::new(IndefiniteBody::full(Bytes::new()));
    *res.status_mut() = StatusCode::INTERNAL_SERVER_ERROR;
    // A Fault comes only from Injection::next, which Site::run calls after
    // check_site: the site is printable with no whitespace, so the payload
    // has no control bytes (non-ASCII is obs-text, which HeaderValue allows).
    let value = HeaderValue::from_bytes(fault.to_string().as_bytes())
        .expect("a fault payload is printable: site names are checked");
    res.headers_mut().insert(FAULT_HEADER, value);
    res
}

pin_project! {
    /// The response body [`Indefinite`] returns. A seeded request's body
    /// streams inside its injection, and yields an error if a fault fires
    /// mid-response: too late to answer, so the server drops the connection.
    pub struct IndefiniteBody<B> {
        #[pin]
        kind: BodyKind<B>,
    }
}

pin_project! {
    #[project = BodyProj]
    enum BodyKind<B> {
        Passthrough { #[pin] body: B },
        Live { #[pin] body: B, scope: Scope },
        Full { data: Option<Bytes> },
        Done,
    }
}

impl<B> IndefiniteBody<B> {
    pub(crate) fn passthrough(body: B) -> Self {
        IndefiniteBody {
            kind: BodyKind::Passthrough { body },
        }
    }

    pub(crate) fn live(body: B, scope: Scope) -> Self {
        IndefiniteBody {
            kind: BodyKind::Live { body, scope },
        }
    }

    pub(crate) fn full(data: Bytes) -> Self {
        let data = (!data.is_empty()).then_some(data);
        IndefiniteBody {
            kind: BodyKind::Full { data },
        }
    }
}

impl<B> Body for IndefiniteBody<B>
where
    B: Body<Data = Bytes>,
    B::Error: Into<BoxError>,
{
    type Data = Bytes;
    type Error = BoxError;

    fn poll_frame(
        self: Pin<&mut Self>,
        cx: &mut Context<'_>,
    ) -> Poll<Option<Result<Frame<Bytes>, BoxError>>> {
        let mut kind = self.project().kind;
        let frame = match kind.as_mut().project() {
            BodyProj::Passthrough { body } => return body.poll_frame(cx).map_err(Into::into),
            BodyProj::Full { data } => return Poll::Ready(data.take().map(|d| Ok(Frame::data(d)))),
            BodyProj::Done => return Poll::Ready(None),
            BodyProj::Live { body, scope } => ready!(scope.poll_with(|| body.poll_frame(cx))),
        };
        let frame = match frame {
            Ok(Some(frame)) => return Poll::Ready(Some(frame.map_err(Into::into))),
            Ok(None) => None, // the request is over: close its injection
            Err(fault) => Some(Err(Box::new(TooLate(fault)) as BoxError)),
        };
        kind.set(BodyKind::Done);
        Poll::Ready(frame)
    }

    fn is_end_stream(&self) -> bool {
        match &self.kind {
            BodyKind::Passthrough { body } | BodyKind::Live { body, .. } => body.is_end_stream(),
            BodyKind::Full { data } => data.is_none(),
            BodyKind::Done => true,
        }
    }

    fn size_hint(&self) -> SizeHint {
        match &self.kind {
            BodyKind::Passthrough { body } | BodyKind::Live { body, .. } => body.size_hint(),
            BodyKind::Full { data } => {
                SizeHint::with_exact(data.as_ref().map_or(0, |d| d.len() as u64))
            }
            BodyKind::Done => SizeHint::with_exact(0),
        }
    }
}

impl<B> fmt::Debug for IndefiniteBody<B> {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("IndefiniteBody").finish_non_exhaustive()
    }
}

/// The body error of a fault that fired after the response started.
#[derive(Debug)]
struct TooLate(Fault);

impl fmt::Display for TooLate {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            f,
            "indefinite-error: {}: too late to answer; dropping the connection",
            self.0
        )
    }
}

impl Error for TooLate {}

impl<F> fmt::Debug for ResponseFuture<F> {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("ResponseFuture").finish_non_exhaustive()
    }
}
