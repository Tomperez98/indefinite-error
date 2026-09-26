//! The pure core: every decision is a function of `(seed, site, n)`.
//!
//! It hashes exactly as every other implementation of `spec/` does, so a seed
//! replays the same faults in a Rust service, a Go one, and a Python one.
//! `spec/schedule.tsv` pins it.

use std::fmt;

use blake2::Blake2bVar;
use blake2::digest::{Update, VariableOutput};

/// When a faulted call ends its request.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub(crate) enum Phase {
    /// The call never runs.
    Before,
    /// The call runs; nobody is told.
    After,
}

impl fmt::Display for Phase {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(match self {
            Phase::Before => "before",
            Phase::After => "after",
        })
    }
}

/// Which phases may fault at one site, for one seed.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub(crate) enum Mode {
    Off,
    Before,
    After,
    Both,
}

impl fmt::Display for Mode {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(match self {
            Mode::Off => "off",
            Mode::Before => "before",
            Mode::After => "after",
            Mode::Both => "both",
        })
    }
}

/// Per-seed fault rates (swarm testing): some seeds are gentle, some brutal.
pub(crate) const RATES: [f64; 4] = [0.01, 0.05, 0.2, 0.5];
pub(crate) const MODES: [Mode; 4] = [Mode::Off, Mode::Before, Mode::After, Mode::Both];

/// One part of a [`unit`] draw: a string, or an integer written in decimal.
#[derive(Clone, Copy, Debug)]
pub(crate) enum Part<'a> {
    Str(&'a str),
    Int(i128),
}

impl fmt::Display for Part<'_> {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Part::Str(s) => f.write_str(s),
            Part::Int(n) => write!(f, "{n}"),
        }
    }
}

impl<'a> From<&'a str> for Part<'a> {
    fn from(s: &'a str) -> Self {
        Part::Str(s)
    }
}

impl From<i64> for Part<'_> {
    fn from(n: i64) -> Self {
        Part::Int(n.into())
    }
}

impl From<u64> for Part<'_> {
    fn from(n: u64) -> Self {
        Part::Int(n.into())
    }
}

/// A uniform float in [0, 1) from `parts`, stable across processes and
/// languages: BLAKE2b with an 8-byte digest over the parts joined by NUL, read
/// big-endian, divided by 2^64.
pub(crate) fn unit(parts: &[Part<'_>]) -> f64 {
    let mut data = String::new();
    for (i, part) in parts.iter().enumerate() {
        if i > 0 {
            data.push('\0');
        }
        data.push_str(&part.to_string());
    }
    // The digest size is in BLAKE2b's parameter block: not a truncated BLAKE2b-512.
    let mut hasher = Blake2bVar::new(8).expect("8 is a valid BLAKE2b digest size");
    hasher.update(data.as_bytes());
    let mut digest = [0u8; 8];
    hasher
        .finalize_variable(&mut digest)
        .expect("the buffer is the digest size");
    // `as` rounds to the nearest f64, as the spec says; dividing by 2^64 is exact.
    u64::from_be_bytes(digest) as f64 / 18_446_744_073_709_551_616.0
}

/// `options[floor(u * len(options))]`. A `u` outside [0, 1) is a broken hash;
/// no options at all doesn't compile.
pub(crate) fn pick<T: Copy, const N: usize>(options: &[T; N], u: f64) -> T {
    const { assert!(N > 0, "indefinite: pick needs at least one option") };
    assert!((0.0..1.0).contains(&u), "indefinite: u={u} out of [0, 1)");
    options[(u * N as f64) as usize]
}

pub(crate) fn rate_of(seed: i64) -> f64 {
    pick(&RATES, unit(&["rate".into(), seed.into()]))
}

pub(crate) fn mode_of(seed: i64, site: &str) -> Mode {
    pick(&MODES, unit(&["mode".into(), seed.into(), site.into()]))
}

/// The phase of the `n`-th call to `site`, if it faults; `rate` and `mode`
/// derive from `seed`.
pub(crate) fn decide(seed: i64, rate: f64, mode: Mode, site: &str, n: u64) -> Option<Phase> {
    let parts = |kind| [Part::Str(kind), seed.into(), site.into(), n.into()];
    if mode == Mode::Off || unit(&parts("call")) >= rate {
        return None;
    }
    Some(match mode {
        Mode::Off => unreachable!("handled above"),
        Mode::Before => Phase::Before,
        Mode::After => Phase::After,
        Mode::Both if unit(&parts("phase")) < 0.5 => Phase::Before,
        Mode::Both => Phase::After,
    })
}
