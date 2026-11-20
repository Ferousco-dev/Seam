use std::io::Write;
use std::process::{Command, Stdio};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::Duration;

use crate::protocol::frames::OutboundFrame;
use crate::transport::multiplexer::SeamTransportMultiplexer;
use crate::transport::SendReliability;

/// Polling interval for the Linux X11 clipboard. `xclip`/`xsel` are external
/// processes, so this is deliberately slower than the input loop while still
/// feeling immediate to a person.
const CLIPBOARD_POLL_INTERVAL: Duration = Duration::from_millis(200);

pub struct ClipboardSyncEngine {
    multiplexer: Arc<SeamTransportMultiplexer>,
    /// Last text observed in either direction. Used to suppress echo loops.
    last_synced_text: Arc<Mutex<String>>,
    running: Arc<AtomicBool>,
    worker_handle: Mutex<Option<thread::JoinHandle<()>>>,
}

impl ClipboardSyncEngine {
    pub fn new(multiplexer: Arc<SeamTransportMultiplexer>) -> Self {
        Self {
            multiplexer,
            last_synced_text: Arc::new(Mutex::new(String::new())),
            running: Arc::new(AtomicBool::new(false)),
            worker_handle: Mutex::new(None),
        }
    }

    pub fn start(&self) {
        if self.running.swap(true, Ordering::SeqCst) {
            return;
        }

        let running = Arc::clone(&self.running);
        let last_text = Arc::clone(&self.last_synced_text);
        let mux = Arc::clone(&self.multiplexer);

        let handle = thread::Builder::new()
            .name("seam-clipboard".to_string())
            .spawn(move || {
                let mut warned_missing_tool = false;
                while running.load(Ordering::SeqCst) {
                    thread::sleep(CLIPBOARD_POLL_INTERVAL);
                    if !running.load(Ordering::SeqCst) {
                        break;
                    }

                    let Some(current_text) = read_linux_clipboard() else {
                        if !warned_missing_tool {
                            warned_missing_tool = true;
                            eprintln!(
                                "[Clipboard] Neither xclip nor xsel is available; \
                                 Linux -> Android clipboard sync is disabled."
                            );
                        }
                        continue;
                    };
                    warned_missing_tool = false;

                    if current_text.is_empty() {
                        continue;
                    }

                    let should_send = match last_text.lock() {
                        Ok(mut lock) => {
                            if *lock == current_text {
                                false
                            } else {
                                *lock = current_text.clone();
                                true
                            }
                        }
                        Err(_) => false,
                    };

                    if should_send {
                        let frame = OutboundFrame::ClipboardSet { text: current_text };
                        if let Err(err) = mux.send(&frame, SendReliability::ReliableOrdered) {
                            eprintln!("[Clipboard] Failed to send local clipboard: {err}");
                        }
                    }
                }
            })
            .ok();

        if let Ok(mut slot) = self.worker_handle.lock() {
            *slot = handle;
        }
    }

    pub fn stop(&self) {
        self.running.store(false, Ordering::SeqCst);
    }

    /// Applies text received from Android to the X11 clipboard.
    ///
    /// The text is recorded as the last-synced value *before* writing so the
    /// polling worker does not immediately send it back to Android.
    pub fn apply_remote_text(&self, text: &str) {
        if text.is_empty() {
            return;
        }
        if let Ok(mut lock) = self.last_synced_text.lock() {
            *lock = text.to_string();
        }
        if !write_linux_clipboard(text) {
            eprintln!("[Clipboard] Could not write remote clipboard (install xclip or xsel).");
        }
    }

    /// Forces the current local clipboard to Android, bypassing change detection.
    /// Used on explicit paste shortcuts in future UI integrations.
    pub fn sync_local_to_android(&self) {
        if let Some(text) = read_linux_clipboard() {
            if text.is_empty() {
                return;
            }
            if let Ok(mut lock) = self.last_synced_text.lock() {
                *lock = text.clone();
            }
            let frame = OutboundFrame::ClipboardSet { text };
            let _ = self.multiplexer.send(&frame, SendReliability::ReliableOrdered);
        }
    }
}

impl Drop for ClipboardSyncEngine {
    fn drop(&mut self) {
        self.stop();
        if let Ok(mut slot) = self.worker_handle.lock() {
            if let Some(handle) = slot.take() {
                let _ = handle.join();
            }
        }
    }
}

fn read_linux_clipboard() -> Option<String> {
    if let Ok(output) = Command::new("xclip")
        .args(["-selection", "clipboard", "-o"])
        .output()
    {
        if output.status.success() {
            return String::from_utf8(output.stdout).ok();
        }
    }
    if let Ok(output) = Command::new("xsel").args(["-b", "-o"]).output() {
        if output.status.success() {
            return String::from_utf8(output.stdout).ok();
        }
    }
    None
}

fn write_linux_clipboard(text: &str) -> bool {
    if write_with_command("xclip", &["-selection", "clipboard", "-i"], text) {
        return true;
    }
    write_with_command("xsel", &["-b", "-i"], text)
}

fn write_with_command(program: &str, args: &[&str], text: &str) -> bool {
    let mut child = match Command::new(program)
        .args(args)
        .stdin(Stdio::piped())
        .spawn()
    {
        Ok(child) => child,
        Err(_) => return false,
    };
    if let Some(mut stdin) = child.stdin.take() {
        if stdin.write_all(text.as_bytes()).is_err() {
            let _ = child.kill();
            let _ = child.wait();
            return false;
        }
    }
    child.wait().map(|status| status.success()).unwrap_or(false)
}
