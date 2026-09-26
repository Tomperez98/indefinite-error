//! The pure core: every decision is a function of (seed, site, n).

use std::collections::HashMap;

use proptest::prelude::*;

use super::*;
use crate::core::{MODES, Mode, Part, Phase, RATES, decide, mode_of, pick, rate_of, unit};
use crate::injection::Draw;

fn symbol(phase: Option<Phase>) -> char {
    match phase {
        None => '.',
        Some(Phase::Before) => 'b',
        Some(Phase::After) => 'a',
    }
}

fn call_draw(kind: &str, seed: i64, site: &str, n: u64) -> f64 {
    unit(&[kind.into(), seed.into(), site.into(), n.into()])
}

// --- The schedule: saved seeds replay the same faults, in any language ---

#[test]
fn the_schedule_matches_the_spec() {
    // A change here breaks every seed users saved from a failing run, in every language.
    for row in spec_rows("schedule.tsv") {
        assert_eq!(row.len(), 5, "malformed schedule row {row:?}");
        let seed: i64 = row[0].parse().unwrap();
        let site: String = spec_json(&row[1]);
        let (rate, mode) = (rate_of(seed), mode_of(seed, &site));
        let calls: String = (0..row[4].chars().count() as u64)
            .map(|n| symbol(decide(seed, rate, mode, &site, n)))
            .collect();
        let got = [
            row[0].clone(),
            row[1].clone(),
            mode.to_string(),
            rate.to_string(),
            calls,
        ];
        assert_eq!(
            got.as_slice(),
            row.as_slice(),
            "seed {seed}, site {site:?} drifted"
        );
    }
}

// --- Fault lines: spec/faults.tsv ---

#[test]
fn fault_lines_match_the_spec() {
    // Call n of site faults with the spec's payload, and writes it on the line.
    for row in spec_rows("faults.tsv") {
        assert_eq!(row.len(), 4, "malformed faults row {row:?}");
        let seed: i64 = row[0].parse().unwrap();
        let site = Site::new(leak(spec_json::<String>(&row[1])));
        let n: u64 = row[2].parse().unwrap();
        let payload = &row[3];
        let lines = Lines::default();
        let scope = Scope::new(seed, lines.log());
        for _ in 0..n {
            let _ = catch(&scope, site.run(async {}));
        }
        let fault = catch(&scope, site.run(async {})).expect_err("the last call didn't fault");
        assert_eq!(
            (fault.n, fault.to_string()),
            (n, payload.clone()),
            "{site} at seed {seed}"
        );
        assert_eq!(
            lines.text().lines().last(),
            Some(format!("indefinite-error: {payload}").as_str())
        );
    }
}

#[test]
fn parts_cannot_run_together() {
    // Parts are separated before hashing, so ("1", "2x") differs from ("12", "x").
    assert_ne!(
        unit(&["mode".into(), "1".into(), "2x".into()]),
        unit(&["mode".into(), "12".into(), "x".into()])
    );
    assert_ne!(
        call_draw("call", 0, "a", 11),
        unit(&["call".into(), 0i64.into(), "a1".into(), 1u64.into()])
    );
}

#[test]
fn integers_are_written_in_decimal() {
    assert_eq!(unit(&[Part::from(-7i64)]), unit(&["-7".into()]));
    assert_eq!(
        unit(&[Part::from(i64::MIN)]),
        unit(&["-9223372036854775808".into()])
    );
    assert_eq!(
        unit(&[Part::from(u64::MAX)]),
        unit(&["18446744073709551615".into()])
    );
}

// --- Decision table: every row, and exactly one row per input ---

/// `want` is a phase, or "coin": before if the phase draw < 0.5, else after.
const ROWS: [(Mode, bool, &str); 8] = [
    (Mode::Off, false, "pass"),
    (Mode::Off, true, "pass"),
    (Mode::Before, false, "pass"),
    (Mode::Before, true, "before"),
    (Mode::After, false, "pass"),
    (Mode::After, true, "after"),
    (Mode::Both, false, "pass"),
    (Mode::Both, true, "coin"),
];

#[test]
fn the_decision_table() {
    for (mode, draw, want) in ROWS {
        let rate = if draw { 1.0 } else { 0.0 }; // no draw is < 0.0; every draw is < 1.0
        for seed in 0..5 {
            for site in ["a", "b"] {
                for n in 0..100 {
                    let want = match want {
                        "coin" if call_draw("phase", seed, site, n) < 0.5 => "before",
                        "coin" => "after",
                        want => want,
                    };
                    let got = decide(seed, rate, mode, site, n)
                        .map_or("pass".to_owned(), |p| p.to_string());
                    assert_eq!(got, want, "decide({seed}, {rate}, {mode}, {site:?}, {n})");
                }
            }
        }
    }
}

#[test]
fn the_decision_table_is_complete_and_unambiguous() {
    for mode in MODES {
        for draw in [false, true] {
            let matches = ROWS
                .iter()
                .filter(|(m, d, _)| *m == mode && *d == draw)
                .count();
            assert_eq!(matches, 1, "gap or overlap at mode={mode} draw={draw}");
        }
    }
}

#[test]
fn a_draw_under_the_rate_is_what_faults() {
    for seed in 0..5 {
        for n in 0..200 {
            let faulted = decide(seed, 0.3, Mode::Before, "site", n).is_some();
            assert_eq!(
                faulted,
                call_draw("call", seed, "site", n) < 0.3,
                "seed={seed} n={n}"
            );
        }
    }
}

#[test]
fn a_draw_equal_to_the_rate_does_not_fault() {
    // The fault window is [0, rate): rate 0.0 never faults, 1.0 always does.
    let draw = call_draw("call", 0, "site", 0);
    assert_eq!(decide(0, draw, Mode::Before, "site", 0), None);
    assert_eq!(
        decide(0, draw.next_up(), Mode::Before, "site", 0),
        Some(Phase::Before)
    );
}

#[test]
fn pick_covers_the_whole_unit_interval() {
    let options = ["a", "b", "c", "d"];
    assert_eq!(pick(&options, 0.0), "a");
    assert_eq!(pick(&options, 1.0f64.next_down()), "d");
    for (k, want) in options.iter().enumerate() {
        assert_eq!(pick(&options, k as f64 / 4.0), *want);
    }
}

#[test]
fn pick_panics_outside_the_unit_interval() {
    // A draw outside [0, 1) is a broken hash, not a choice: crash at the line.
    for u in [1.0, -0.1, f64::NAN] {
        must_panic("out of [0, 1)", || {
            pick(&["a", "b"], u);
        });
    }
}

// --- Statistics: the seed's choices have the shape the docs promise ---
// Deterministic (the "randomness" is a hash), so these can't flake: they pass
// or fail the same way every run. Bounds are 5 standard deviations.

fn within(observed: usize, total: usize, p: f64) -> bool {
    let sigma = (total as f64 * p * (1.0 - p)).sqrt();
    (observed as f64 - total as f64 * p).abs() <= 5.0 * sigma
}

fn seed_with_rate(rate: f64) -> i64 {
    (0..10_000)
        .find(|&seed| rate_of(seed) == rate)
        .expect("no seed has the rate")
}

#[test]
fn the_observed_fault_rate_matches_the_rate() {
    for rate in RATES {
        let (seed, total) = (seed_with_rate(rate), 20_000);
        let faults = (0..total as u64)
            .filter(|&n| decide(seed, rate, Mode::Before, "site", n).is_some())
            .count();
        assert!(
            within(faults, total, rate),
            "{faults}/{total} faults at rate {rate}"
        );
    }
}

#[test]
fn both_mode_splits_evenly() {
    let seed = seed_with_rate(0.5);
    let mut counts = HashMap::new();
    for n in 0..40_000 {
        if let Some(phase) = decide(seed, 0.5, Mode::Both, "site", n) {
            *counts.entry(phase).or_insert(0) += 1;
        }
    }
    let (before, after) = (counts[&Phase::Before], counts[&Phase::After]);
    assert!(
        within(before, before + after, 0.5),
        "before/after split {counts:?}"
    );
}

fn assert_even<T: std::hash::Hash + Eq + std::fmt::Debug>(
    counts: &HashMap<T, usize>,
    options: &[T],
) {
    let total = counts.values().sum();
    for option in options {
        let observed = counts.get(option).copied().unwrap_or(0);
        assert!(
            within(observed, total, 1.0 / options.len() as f64),
            "uneven: {counts:?}"
        );
    }
}

#[test]
fn modes_and_rates_are_spread_evenly() {
    // Swarm testing needs every mode and every rate, about equally often.
    let mut modes = HashMap::new();
    for seed in 0..1000 {
        for k in 0..4 {
            *modes.entry(mode_of(seed, &format!("site{k}"))).or_insert(0) += 1;
        }
    }
    assert_even(&modes, &MODES);
    let mut rates = HashMap::new();
    for seed in 0..4000 {
        *rates.entry(rate_of(seed).to_bits()).or_insert(0) += 1;
    }
    assert_even(&rates, &RATES.map(f64::to_bits));
}

// --- Properties ---

/// A valid site name of 1 to 8 characters, some of them non-ASCII.
fn site_name() -> impl Strategy<Value = String> {
    "[abcxyz.:/#\\-_09üïéλ中]{1,8}"
}

proptest! {
    #[test]
    fn unit_is_in_the_unit_interval(a in any::<String>(), b in any::<String>(), seed in any::<i64>()) {
        let u = unit(&[a.as_str().into(), seed.into(), b.as_str().into()]);
        prop_assert!((0.0..1.0).contains(&u), "unit = {u}");
    }

    #[test]
    fn decide_only_returns_phases_the_mode_allows(
        seed in any::<i64>(), site in any::<String>(), n in any::<u64>(),
        rate in 0.0f64..=1.0, mode in prop::sample::select(MODES.to_vec()),
    ) {
        let allowed: &[Option<Phase>] = match mode {
            Mode::Off => &[None],
            Mode::Before => &[None, Some(Phase::Before)],
            Mode::After => &[None, Some(Phase::After)],
            Mode::Both => &[None, Some(Phase::Before), Some(Phase::After)],
        };
        let got = decide(seed, rate, mode, &site, n);
        prop_assert!(allowed.contains(&got), "mode {mode} decided {got:?}");
    }

    /// Any interleaving of calls across sites: the faults are exactly the
    /// model's. The model keeps one counter per site and asks the pure core,
    /// so a site's faults can't depend on its neighbours, or on anything but
    /// the seed.
    #[test]
    fn the_injection_matches_a_reference_model(
        seed in any::<i64>(),
        names in prop::collection::hash_set(site_name(), 1..5),
        program in prop::collection::vec(any::<prop::sample::Index>(), 0..60),
    ) {
        let sites: Vec<&'static str> = names.into_iter().map(leak).collect();
        let program: Vec<usize> = program.iter().map(|i| i.index(sites.len())).collect();
        let scope = scope(seed);
        let seen: Vec<String> = program.iter().map(|&k| {
            match scope.injection().next(sites[k]) {
                Draw::Pass => "-".to_owned(),
                Draw::Fault(fault) => fault.to_string(),
                Draw::Over => unreachable!("nothing aborts"),
            }
        }).collect();

        let mut counts = HashMap::new();
        let want: Vec<String> = program.iter().map(|&k| {
            let site = sites[k];
            let n = counts.entry(site).or_insert(0);
            let phase = decide(seed, rate_of(seed), mode_of(seed, site), site, *n);
            let line = phase.map_or("-".to_owned(), |phase| format!("{phase} {site}#{n} (seed={seed})"));
            *n += 1;
            line
        }).collect();
        prop_assert_eq!(seen, want);
        prop_assert_eq!(scope.injection().calls(), counts);
    }
}
