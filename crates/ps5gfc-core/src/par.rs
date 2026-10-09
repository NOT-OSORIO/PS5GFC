// PS5GFC — PS5 Game Format Converter
// Copyright (C) 2026 OSØRIO
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, version 3.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

use std::collections::HashMap;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Condvar, Mutex};

use crate::ctl::Cancel;
use crate::sys::lower_thread_priority;
use crate::{Error, Result};

struct State<T> {
    results: HashMap<u64, T>,
    consumed: u64,
    error: Option<Error>,
    stop: bool,
}

pub fn ordered_map<T, P, C>(
    n: u64,
    workers: usize,
    window: usize,
    cancel: &Cancel,
    produce: P,
    mut consume: C,
) -> Result<()>
where
    T: Send,
    P: Fn(u64) -> Result<T> + Sync,
    C: FnMut(u64, T) -> Result<()>,
{
    if n == 0 {
        return Ok(());
    }
    let workers = workers.max(1).min(n as usize);
    let window = window.max(workers) as u64;
    let state = Mutex::new(State {
        results: HashMap::new(),
        consumed: 0,
        error: None,
        stop: false,
    });
    let cv_consumer = Condvar::new();
    let cv_worker = Condvar::new();
    let next = AtomicU64::new(0);

    std::thread::scope(|s| {
        for _ in 0..workers {
            s.spawn(|| {
                lower_thread_priority();
                loop {
                    let i = next.fetch_add(1, Ordering::Relaxed);
                    if i >= n {
                        break;
                    }
                    {
                        let mut st = state.lock().unwrap();
                        while !st.stop && i >= st.consumed + window {
                            st = cv_worker.wait(st).unwrap();
                        }
                        if st.stop {
                            break;
                        }
                    }
                    if cancel.is_cancelled() {
                        let mut st = state.lock().unwrap();
                        st.error.get_or_insert(Error::Cancelled);
                        st.stop = true;
                        cv_consumer.notify_all();
                        cv_worker.notify_all();
                        break;
                    }
                    match produce(i) {
                        Ok(v) => {
                            let mut st = state.lock().unwrap();
                            st.results.insert(i, v);
                            cv_consumer.notify_one();
                        }
                        Err(e) => {
                            let mut st = state.lock().unwrap();
                            st.error.get_or_insert(e);
                            st.stop = true;
                            cv_consumer.notify_all();
                            cv_worker.notify_all();
                            break;
                        }
                    }
                }
            });
        }

        let mut idx = 0u64;
        while idx < n {
            let item = {
                let mut st = state.lock().unwrap();
                loop {
                    if let Some(v) = st.results.remove(&idx) {
                        st.consumed = idx + 1;
                        cv_worker.notify_all();
                        break Some(v);
                    }
                    if st.stop {
                        break None;
                    }
                    st = cv_consumer.wait(st).unwrap();
                }
            };
            match item {
                Some(v) => {
                    if let Err(e) = consume(idx, v) {
                        let mut st = state.lock().unwrap();
                        st.error.get_or_insert(e);
                        st.stop = true;
                        cv_worker.notify_all();
                        break;
                    }
                    idx += 1;
                }
                None => break,
            }
        }

        let mut st = state.lock().unwrap();
        if idx < n {
            st.stop = true;
        }
        cv_worker.notify_all();
    });

    let mut st = state.lock().unwrap();
    match st.error.take() {
        Some(e) => Err(e),
        None => Ok(()),
    }
}

pub fn parallel_for<F>(n: u64, workers: usize, cancel: &Cancel, f: F) -> Result<()>
where
    F: Fn(u64) -> Result<()> + Sync,
{
    if n == 0 {
        return Ok(());
    }
    let workers = workers.max(1).min(n as usize);
    let next = AtomicU64::new(0);
    let err: Mutex<Option<Error>> = Mutex::new(None);
    std::thread::scope(|s| {
        for _ in 0..workers {
            s.spawn(|| {
                lower_thread_priority();
                loop {
                    if err.lock().unwrap().is_some() {
                        break;
                    }
                    if cancel.is_cancelled() {
                        err.lock().unwrap().get_or_insert(Error::Cancelled);
                        break;
                    }
                    let i = next.fetch_add(1, Ordering::Relaxed);
                    if i >= n {
                        break;
                    }
                    if let Err(e) = f(i) {
                        err.lock().unwrap().get_or_insert(e);
                        break;
                    }
                }
            });
        }
    });
    match err.into_inner().unwrap() {
        Some(e) => Err(e),
        None => Ok(()),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ordered_delivery() {
        let cancel = Cancel::new();
        let mut seen = Vec::new();
        ordered_map(
            1000,
            8,
            16,
            &cancel,
            |i| {
                if i % 7 == 0 {
                    std::thread::sleep(std::time::Duration::from_micros(300));
                }
                Ok(i * 2)
            },
            |i, v| {
                assert_eq!(v, i * 2);
                seen.push(i);
                Ok(())
            },
        )
        .unwrap();
        assert_eq!(seen, (0..1000).collect::<Vec<_>>());
    }

    #[test]
    fn error_propagates() {
        let cancel = Cancel::new();
        let r = ordered_map(
            100,
            4,
            8,
            &cancel,
            |i| if i == 37 { Err(Error::invalid("boom")) } else { Ok(i) },
            |_, _| Ok(()),
        );
        assert!(matches!(r, Err(Error::Invalid(_))));
    }

    #[test]
    fn cancel_stops() {
        let cancel = Cancel::new();
        let c2 = cancel.clone();
        let r = ordered_map(
            10_000,
            4,
            8,
            &cancel,
            |i| Ok(i),
            move |i, _| {
                if i == 50 {
                    c2.cancel();
                }
                Ok(())
            },
        );
        assert!(matches!(r, Err(Error::Cancelled)));
    }

    #[test]
    fn parallel_for_runs_all() {
        use std::sync::atomic::AtomicU64;
        let cancel = Cancel::new();
        let sum = AtomicU64::new(0);
        parallel_for(100, 6, &cancel, |i| {
            sum.fetch_add(i, Ordering::Relaxed);
            Ok(())
        })
        .unwrap();
        assert_eq!(sum.load(Ordering::Relaxed), 4950);
    }
}
