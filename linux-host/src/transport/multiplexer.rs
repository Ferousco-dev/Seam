use std::collections::HashMap;
use std::io;
use std::sync::{Arc, Mutex};

use crate::protocol::auth::Authenticator;
use crate::protocol::frames::{InboundFrame, OutboundFrame};
use crate::transport::{
    SeamEndpoint, SeamTransport, SendReliability, TransportDelegate, TransportKind, TransportState,
};

pub struct SeamTransportMultiplexer {
    transports: Arc<Mutex<HashMap<TransportKind, Box<dyn SeamTransport>>>>,
    active_primary: Arc<Mutex<Option<TransportKind>>>,
    delegate: Arc<Mutex<Option<Arc<dyn TransportDelegate>>>>,
}

impl SeamTransportMultiplexer {
    pub fn new() -> Self {
        Self {
            transports: Arc::new(Mutex::new(HashMap::new())),
            active_primary: Arc::new(Mutex::new(None)),
            delegate: Arc::new(Mutex::new(None)),
        }
    }

    pub fn set_delegate(&self, delegate: Arc<dyn TransportDelegate>) {
        if let Ok(mut lock) = self.delegate.lock() {
            *lock = Some(Arc::clone(&delegate));
        }
        // Propagate to transports that were registered before the delegate was
        // installed (for example a Wi-Fi standby transport added first).
        if let Ok(mut transports) = self.transports.lock() {
            for transport in transports.values_mut() {
                transport.set_delegate(Arc::clone(&delegate));
            }
        }
    }

    pub fn active_primary_kind(&self) -> Option<TransportKind> {
        self.active_primary.lock().ok().and_then(|p| *p)
    }

    pub fn add_and_connect_transport(
        &self,
        mut transport: Box<dyn SeamTransport>,
        endpoint: &SeamEndpoint,
        authenticator: &Authenticator,
        make_primary: bool,
    ) -> io::Result<()> {
        let kind = transport.kind();
        let delegate = self
            .delegate
            .lock()
            .ok()
            .and_then(|lock| lock.as_ref().map(Arc::clone));
        if let Some(delegate) = delegate {
            transport.set_delegate(delegate);
        }
        transport.connect(endpoint, authenticator)?;

        let mut t_lock = self
            .transports
            .lock()
            .map_err(|_| io::Error::other("Transport mutex poisoned"))?;
        t_lock.insert(kind, transport);

        let mut p_lock = self
            .active_primary
            .lock()
            .map_err(|_| io::Error::other("Primary mutex poisoned"))?;
        if p_lock.is_none() || make_primary {
            *p_lock = Some(kind);
            println!("[Multiplexer] Active Primary Transport -> {}", kind);
        } else {
            println!("[Multiplexer] Registered Standby Transport -> {}", kind);
        }

        Ok(())
    }

    pub fn switch_active_transport(&self, target_kind: TransportKind) -> io::Result<()> {
        let t_lock = self
            .transports
            .lock()
            .map_err(|_| io::Error::other("Transport mutex poisoned"))?;
        let target = t_lock.get(&target_kind).ok_or_else(|| {
            io::Error::new(
                io::ErrorKind::NotFound,
                format!("Transport {} not found", target_kind),
            )
        })?;

        if !matches!(target.state(), TransportState::Ready { .. }) {
            return Err(io::Error::new(
                io::ErrorKind::NotConnected,
                format!("Transport {} is not in Ready state", target_kind),
            ));
        }

        let mut p_lock = self
            .active_primary
            .lock()
            .map_err(|_| io::Error::other("Primary mutex poisoned"))?;
        let prev = *p_lock;
        *p_lock = Some(target_kind);

        if prev != Some(target_kind) {
            println!(
                "[Multiplexer] Switched ACTIVE_PRIMARY: {:?} -> {}",
                prev, target_kind
            );
        }

        Ok(())
    }

    pub fn send(&self, frame: &OutboundFrame, reliability: SendReliability) -> io::Result<()> {
        let primary_kind = self.active_primary_kind().ok_or_else(|| {
            io::Error::new(io::ErrorKind::NotConnected, "No active primary transport")
        })?;

        let t_lock = self
            .transports
            .lock()
            .map_err(|_| io::Error::other("Transport mutex poisoned"))?;

        if let Some(transport) = t_lock.get(&primary_kind) {
            match transport.send(frame, reliability) {
                Ok(()) => return Ok(()),
                Err(e) => {
                    eprintln!(
                        "[Multiplexer] Primary transport {} failed ({}), attempting failover...",
                        primary_kind, e
                    );
                }
            }
        }

        // Primary failed! Attempt zero-downtime hot-standby failover
        let mut new_primary = None;
        for (&kind, candidate) in t_lock.iter() {
            if kind != primary_kind && matches!(candidate.state(), TransportState::Ready { .. }) {
                new_primary = Some(kind);
                break;
            }
        }

        if let Some(fallback_kind) = new_primary {
            println!(
                "[Multiplexer] Hot-failover: {} -> {}",
                primary_kind, fallback_kind
            );
            if let Ok(mut p_lock) = self.active_primary.lock() {
                *p_lock = Some(fallback_kind);
            }
            if let Some(fallback) = t_lock.get(&fallback_kind) {
                return fallback.send(frame, reliability);
            }
        }

        Err(io::Error::new(
            io::ErrorKind::NotConnected,
            "Primary transport failed and no ready standby available",
        ))
    }

    pub fn disconnect_transport(&self, kind: TransportKind, reason: &str) {
        let mut t_lock = self.transports.lock().unwrap();
        if let Some(mut t) = t_lock.remove(&kind) {
            t.disconnect(reason);
        }
        // Promote standby if we just disconnected the primary
        let mut p_lock = self.active_primary.lock().unwrap();
        if *p_lock == Some(kind) {
            *p_lock = None;
            for (&k, candidate) in t_lock.iter() {
                if matches!(candidate.state(), TransportState::Ready { .. }) {
                    *p_lock = Some(k);
                    println!(
                        "[Multiplexer] Promoted standby {} to primary after disconnect",
                        k
                    );
                    break;
                }
            }
        }
    }

    pub fn disconnect_all(&self, reason: &str) {
        let mut t_lock = self.transports.lock().unwrap();
        let mut p_lock = self.active_primary.lock().unwrap();
        *p_lock = None;
        for (_, mut t) in t_lock.drain() {
            t.disconnect(reason);
        }
    }
}

impl Default for SeamTransportMultiplexer {
    fn default() -> Self {
        Self::new()
    }
}

pub struct ForwardingDelegate {
    target: Arc<dyn TransportDelegate>,
}

impl ForwardingDelegate {
    pub fn new(target: Arc<dyn TransportDelegate>) -> Self {
        Self { target }
    }
}

impl TransportDelegate for ForwardingDelegate {
    fn on_state_changed(&self, kind: TransportKind, state: TransportState) {
        self.target.on_state_changed(kind, state);
    }

    fn on_frame_received(&self, kind: TransportKind, frame: InboundFrame) {
        self.target.on_frame_received(kind, frame);
    }

    fn on_rtt_measured(&self, kind: TransportKind, rtt_us: u64) {
        self.target.on_rtt_measured(kind, rtt_us);
    }
}
