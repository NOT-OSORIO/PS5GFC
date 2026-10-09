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

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, serde::Serialize, serde::Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum WorkerPriority {
    Normal,

    Low,

    #[default]
    LowPerf,
}

impl WorkerPriority {
    pub fn parse(s: &str) -> Option<Self> {
        match s {
            "normal" => Some(Self::Normal),
            "low" => Some(Self::Low),
            "low_perf" => Some(Self::LowPerf),
            _ => None,
        }
    }
}

static PRIORITY: std::sync::atomic::AtomicU8 = std::sync::atomic::AtomicU8::new(2);

pub fn set_worker_priority(p: WorkerPriority) {
    PRIORITY.store(p as u8, std::sync::atomic::Ordering::Relaxed);
}

pub fn worker_priority() -> WorkerPriority {
    if let Some(p) = std::env::var("PS5GFC_PRIO")
        .ok()
        .and_then(|v| WorkerPriority::parse(&v))
    {
        return p;
    }
    match PRIORITY.load(std::sync::atomic::Ordering::Relaxed) {
        0 => WorkerPriority::Normal,
        1 => WorkerPriority::Low,
        _ => WorkerPriority::LowPerf,
    }
}

pub fn lower_thread_priority() {
    #[cfg(windows)]
    unsafe {
        use windows_sys::Win32::System::Threading::{
            GetCurrentThread, SetThreadInformation, SetThreadPriority, ThreadPowerThrottling,
            THREAD_POWER_THROTTLING_CURRENT_VERSION, THREAD_POWER_THROTTLING_EXECUTION_SPEED,
            THREAD_POWER_THROTTLING_STATE, THREAD_PRIORITY_BELOW_NORMAL,
        };
        let policy = worker_priority();
        if policy != WorkerPriority::Normal {
            let _ = SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_BELOW_NORMAL);
        }
        if policy != WorkerPriority::Low {
            let state = THREAD_POWER_THROTTLING_STATE {
                Version: THREAD_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask: THREAD_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask: 0,
            };
            let _ = SetThreadInformation(
                GetCurrentThread(),
                ThreadPowerThrottling,
                &state as *const _ as *const core::ffi::c_void,
                core::mem::size_of::<THREAD_POWER_THROTTLING_STATE>() as u32,
            );
        }
    }
}

pub fn lower_process_priority() {
    #[cfg(windows)]
    unsafe {
        use windows_sys::Win32::System::Threading::{GetCurrentProcess, SetPriorityClass, BELOW_NORMAL_PRIORITY_CLASS};
        let _ = SetPriorityClass(GetCurrentProcess(), BELOW_NORMAL_PRIORITY_CLASS);
    }
}

pub fn logical_cpus() -> usize {
    std::thread::available_parallelism().map(|n| n.get()).unwrap_or(1)
}

pub fn free_space(path: &std::path::Path) -> Option<u64> {
    let mut p = path.to_path_buf();
    while !p.exists() {
        if !p.pop() {
            return None;
        }
    }
    #[cfg(windows)]
    {
        use std::os::windows::ffi::OsStrExt;
        use windows_sys::Win32::Storage::FileSystem::GetDiskFreeSpaceExW;
        let abs = std::fs::canonicalize(&p).ok()?;
        let wide: Vec<u16> = abs.as_os_str().encode_wide().chain(std::iter::once(0)).collect();
        let mut avail: u64 = 0;
        let mut total: u64 = 0;
        let mut free: u64 = 0;

        let ok = unsafe { GetDiskFreeSpaceExW(wide.as_ptr(), &mut avail, &mut total, &mut free) };
        if ok != 0 {
            Some(avail)
        } else {
            None
        }
    }
    #[cfg(not(windows))]
    {
        None
    }
}

pub struct AwakeGuard;

impl AwakeGuard {
    pub fn new() -> Self {
        #[cfg(windows)]
        unsafe {
            use windows_sys::Win32::System::Power::{SetThreadExecutionState, ES_CONTINUOUS, ES_SYSTEM_REQUIRED};
            SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);
        }
        AwakeGuard
    }
}

impl Default for AwakeGuard {
    fn default() -> Self {
        Self::new()
    }
}

impl Drop for AwakeGuard {
    fn drop(&mut self) {
        #[cfg(windows)]
        unsafe {
            use windows_sys::Win32::System::Power::{SetThreadExecutionState, ES_CONTINUOUS};
            SetThreadExecutionState(ES_CONTINUOUS);
        }
    }
}

pub fn total_memory() -> Option<u64> {
    #[cfg(windows)]
    {
        use windows_sys::Win32::System::SystemInformation::{GlobalMemoryStatusEx, MEMORYSTATUSEX};
        let mut st: MEMORYSTATUSEX = unsafe { std::mem::zeroed() };
        st.dwLength = std::mem::size_of::<MEMORYSTATUSEX>() as u32;

        if unsafe { GlobalMemoryStatusEx(&mut st) } != 0 {
            return Some(st.ullTotalPhys);
        }
        None
    }
    #[cfg(not(windows))]
    {
        None
    }
}
