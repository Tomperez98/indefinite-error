#![doc = include_str!("../README.md")]
#![warn(missing_docs, missing_debug_implementations)]

mod core;
mod injection;
mod middleware;
mod site;

pub use middleware::{
    FAULT_HEADER, Indefinite, IndefiniteBody, IndefiniteLayer, ResponseFuture, SEED_HEADER,
};
pub use site::{Run, Site};

#[cfg(test)]
mod tests;
