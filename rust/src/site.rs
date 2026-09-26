use std::fmt;
use std::future::{Future, IntoFuture};
use std::pin::Pin;
use std::sync::Arc;
use std::task::{Context, Poll};

use pin_project_lite::pin_project;
use unicode_properties::{GeneralCategoryGroup, UnicodeGeneralCategory};

use crate::core::Phase;
use crate::injection::{self, Draw, Fault, Injection};

/// An operation whose outcome can be indefinite: a database commit, a call to
/// another service, a message you publish.
///
/// Its name goes on the fault line, and each name draws its own faults, so
/// calls to one site never shift another's. Two `Site`s with the same name
/// share one fault stream.
///
/// Declare sites once, as statics:
///
/// ```
/// use indefinite_error::Site;
///
/// static COMMIT: Site = Site::new("db.commit");
/// ```
#[derive(Clone, Copy, PartialEq, Eq, Hash)]
pub struct Site {
    name: &'static str,
}

impl Site {
    /// The site called `name`.
    ///
    /// The name goes on the fault line, which must stay unambiguous to parse,
    /// so it must be non-empty and printable, with no whitespace. In a
    /// `static` or `const`, an empty name or an ASCII space or control
    /// character fails to compile. The rest of Unicode is checked when the
    /// site is first run: a bad name panics there, in or out of a request.
    ///
    /// ```compile_fail
    /// # use indefinite_error::Site;
    /// static BAD: Site = Site::new("db commit");
    /// ```
    #[must_use]
    pub const fn new(name: &'static str) -> Site {
        let bytes = name.as_bytes();
        assert!(
            !bytes.is_empty(),
            "indefinite: a site name must be non-empty"
        );
        let mut i = 0;
        while i < bytes.len() {
            let b = bytes[i];
            assert!(
                !b.is_ascii() || b.is_ascii_graphic(),
                "indefinite: a site name must be printable, with no whitespace"
            );
            i += 1;
        }
        Site { name }
    }

    /// The site's name.
    #[must_use]
    pub const fn name(&self) -> &'static str {
        self.name
    }

    /// Runs `op`, unless the request being served faults this call.
    ///
    /// Outside a request that carries a seed, this is `op.await`. Inside one,
    /// the call either passes, returning what `op` returned, or faults:
    ///
    /// - **before**: `op` is dropped without being polled, so it never
    ///   happened, and the request ends.
    /// - **after**: `op` runs to completion, its output is discarded, and the
    ///   request ends: it happened, and nobody was told.
    ///
    /// A request ends the way any future is cancelled: this call never
    /// returns, the middleware drops the request's future, and destructors
    /// run on the way out. No `Result`, `?`, or retry loop can see the fault.
    /// Once a fault fires, no later call in that request runs.
    ///
    /// A panic in `op` outranks the fault and propagates unchanged.
    ///
    /// For synchronous work, run it in an `async` block:
    /// `COMMIT.run(async { tx.commit() }).await`.
    ///
    /// # Panics
    ///
    /// On the first poll, if the site's name isn't printable with no
    /// whitespace: see [`Site::new`].
    pub fn run<F: IntoFuture>(&self, op: F) -> Run<F::IntoFuture> {
        Run {
            site: self.name,
            op: op.into_future(),
            state: State::Start,
        }
    }
}

impl fmt::Debug for Site {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_tuple("Site").field(&self.name).finish()
    }
}

/// The site's name.
impl fmt::Display for Site {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(self.name)
    }
}

/// A site name must be non-empty, with only letters, marks, numbers,
/// punctuation, and symbols: printable, with no whitespace.
pub(crate) fn check_site(name: &str) -> Result<(), String> {
    let printable = |c: char| {
        !matches!(
            c.general_category_group(),
            GeneralCategoryGroup::Separator | GeneralCategoryGroup::Other
        )
    };
    if name.is_empty() || !name.chars().all(printable) {
        return Err(format!(
            "indefinite: a site name must be non-empty and printable, with no whitespace, got {name:?}"
        ));
    }
    Ok(())
}

enum State {
    Start,
    Pass,
    After(Arc<Injection>, Fault),
    Over,
}

pin_project! {
    /// The future [`Site::run`] returns.
    #[must_use = "futures do nothing unless you `.await` or poll them"]
    pub struct Run<F> {
        site: &'static str,
        #[pin]
        op: F,
        state: State,
    }
}

impl<F: Future> Future for Run<F> {
    type Output = F::Output;

    fn poll(self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<F::Output> {
        let this = self.project();
        if let State::Start = this.state {
            // Site::new, const or not, already rejected every bad ASCII name.
            if !this.site.is_ascii()
                && let Err(msg) = check_site(this.site)
            {
                panic!("{msg}");
            }
            // The call starts at its first poll: that's where it runs.
            *this.state = match injection::current().map(|i| (i.next(this.site), i)) {
                None | Some((Draw::Pass, _)) => State::Pass,
                Some((Draw::Over, _)) => State::Over,
                Some((Draw::Fault(fault), i)) if fault.phase == Phase::Before => {
                    if i.abort(fault) {
                        State::Over
                    } else {
                        State::Pass
                    }
                }
                Some((Draw::Fault(fault), i)) => State::After(i, fault),
            };
        }
        match this.state {
            State::Start => unreachable!("started above"),
            State::Pass => this.op.poll(cx),
            // Pending, with no wake-up: the middleware sees the fault as soon
            // as this poll returns, and drops the request.
            State::Over => Poll::Pending,
            State::After(injection, fault) => {
                let out = std::task::ready!(this.op.poll(cx));
                if injection.abort(fault.clone()) {
                    drop(out); // it happened: its outcome is what gets lost
                    *this.state = State::Over;
                    Poll::Pending
                } else {
                    // The request already ended: there's nothing left to fault.
                    *this.state = State::Pass;
                    Poll::Ready(out)
                }
            }
        }
    }
}

impl<F> fmt::Debug for Run<F> {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("Run")
            .field("site", &self.site)
            .finish_non_exhaustive()
    }
}
