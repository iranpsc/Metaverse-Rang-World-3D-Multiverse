/* VME2 W4 WebRTC Spatial Audio. Bundled from locally supplied mediasoup-client dependencies. */
(function(){ 'use strict';
const __modules = {
0: function(module, exports, __req) {
"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
// Browser side of VME2's gated WebGL/WebRTC migration.
// Browser encodes Opus via RTCPeerConnection; no new microphone is opened.
const mediasoup_client_1 = __req(1);
const connections = new Map();
// WebGL only, receiver-side, per-Voice-connection playback guard.
// No SFU or outgoing producer modification.
const vme2LocallyMutedSenders = new Set();

const REQUEST_TIMEOUT_MS = 12000;
function safeDetail(e) { return String(e?.message || e || 'unknown').replace(/[|\r\n]/g, ' ').slice(0, 150); }
function clampFinite(value, min, max) {
    const number = Number(value);
    return Number.isFinite(number) ? Math.max(min, Math.min(max, number)) : 0;
}
function getSignalUrl(endpoint) {
    // Preserve the already-deployed realtime WSS URL and change ONLY transport selector.
    const stage = new URLSearchParams(location.search).get('vme2SfuUrl');
    if (stage) {
        const testUrl = new URL(stage);
        if (testUrl.protocol !== 'ws:' || !['127.0.0.1', 'localhost'].includes(testUrl.hostname))
            throw new Error('W3_STAGE_OVERRIDE_LOCAL_ONLY');
        return { url: testUrl.href, stage: true };
    }
    const url = new URL(endpoint);
    if (url.protocol !== 'wss:')
        throw new Error('W3_WSS_REQUIRED');
    url.searchParams.set('transport', 'webrtc-w3');
    return { url: url.href, stage: false };
}
async function requestW3Ticket({ accessToken, roomId, controlConnectionId, endpoint }) {
    if (!accessToken)
        throw new Error('W3_ACCESS_TOKEN_MISSING');
    const base = new URL(endpoint);
    if (base.protocol !== 'wss:')
        throw new Error('W3_API_MUST_USE_HTTPS');
    base.protocol = 'https:';
    const response = await fetch(new URL('/game-server-control/client/voice-webrtc-ticket', base), {
        method: 'POST', credentials: 'omit', cache: 'no-store', redirect: 'error',
        headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${accessToken}` },
        body: JSON.stringify({ roomId, controlConnectionId }),
        signal: AbortSignal.timeout(8000)
    });
    if (!response.ok)
        throw new Error(`W3_TICKET_HTTP_${response.status}`);
    const result = await response.json();
    if (!result?.ok || typeof result.ticket !== 'string')
        throw new Error('W3_TICKET_REJECTED');
    return result.ticket;
}
function waitOpen(ws) {
    return new Promise((resolve, reject) => {
        const timer = setTimeout(() => reject(new Error('SIGNAL_CONNECT_TIMEOUT')), REQUEST_TIMEOUT_MS);
        ws.addEventListener('open', () => { clearTimeout(timer); resolve(); }, { once: true });
        ws.addEventListener('error', () => { clearTimeout(timer); reject(new Error('SIGNAL_SOCKET_ERROR')); }, { once: true });
    });
}
function ensureTrack(session) {
    const track = session.pipeline.stream?.getAudioTracks?.()[0];
    if (!track || track.readyState !== 'live')
        throw new Error('EXISTING_MIC_TRACK_UNAVAILABLE');
    return track;
}
class Connection {
    constructor(options) {
        this.handle = options.handle;
        this.pipeline = options.pipeline;
        this.roomId = options.roomId;
        this.controlConnectionId = options.controlConnectionId;
        this.endpoint = options.endpoint;
        this.accessToken = options.accessToken;
        this.onEvent = options.onEvent;
        this.outboundEnabled = false;
        this.playbackEnabled = true;
        this.state = 'new';
        this.ws = null;
        this.device = null;
        this.sendTransport = null;
        this.recvTransport = null;
        this.producer = null;
        // W3: serialize Opus producer creation across repeated Unity outbound callbacks.
        this.producerPromise = null;
        this.consumers = new Map();
        this.spatialBySender = new Map();
        this.pendingProducers = new Set();
        this.closedProducers = new Set();
        this.consumerRetryTimers = new Map();
        this.consumerRetryAttempts = new Map();
        this.transportDisconnectTimers = new Map();
        this.pending = new Map();
        this.requestId = 1;
        this.closed = false;
        this.statsTimer = null;
        this.speakerUnlock = () => {
            const context = this.pipeline.context;
            if (context?.state === 'suspended')
                context.resume().catch(() => { });
            for (const rec of this.consumers.values())
                rec.audio.play().catch(() => { });
        };
    }
    emit(name, values = []) {
        if (!this.closed)
            this.onEvent(name, values);
    }
    rpc(action, data = {}) {
        if (this.closed || this.ws?.readyState !== WebSocket.OPEN)
            return Promise.reject(new Error('SIGNAL_DISCONNECTED'));
        const id = this.requestId++;
        return new Promise((resolve, reject) => {
            const timer = setTimeout(() => {
                this.pending.delete(id);
                reject(new Error(`RPC_TIMEOUT_${action}`));
            }, REQUEST_TIMEOUT_MS);
            this.pending.set(id, { resolve, reject, timer });
            this.ws.send(JSON.stringify({ id, action, data }));
        });
    }
    async initialize() {
        const config = getSignalUrl(this.endpoint);
        const ticket = config.stage ? null : await requestW3Ticket({
            accessToken: this.accessToken, roomId: this.roomId, controlConnectionId: this.controlConnectionId,
            endpoint: this.endpoint
        });
        if (this.closed)
            return;
        this.ws = new WebSocket(config.url);
        this.ws.addEventListener('message', event => this.onMessage(event));
        this.ws.addEventListener('close', () => this.disconnect('signal_closed'));
        this.ws.addEventListener('error', () => { if (this.state === 'ready')
            this.disconnect('signal_socket_error'); });
        await waitOpen(this.ws);
        if (this.closed)
            return;
        this.emit('open');
        const join = await this.rpc('join', { roomId: this.roomId, controlConnectionId: this.controlConnectionId,
            ...(ticket ? { ticket } : {}) });
        if (join.stageOnly !== config.stage)
            throw new Error('W3_STAGE_POLICY_MISMATCH');
        this.device = new mediasoup_client_1.Device();
        await this.device.load({ routerRtpCapabilities: join.rtpCapabilities });
        const send = await this.rpc('createTransport', { direction: 'send' });
        this.sendTransport = this.device.createSendTransport(send);
        this.sendTransport.on('connect', ({ dtlsParameters }, callback, errback) => {
            this.rpc('connectTransport', { transportId: this.sendTransport.id, dtlsParameters })
                .then(() => callback(), errback);
        });
        this.sendTransport.on('produce', ({ kind, rtpParameters }, callback, errback) => {
            this.rpc('produce', { transportId: this.sendTransport.id, kind, rtpParameters })
                .then(({ id }) => callback({ id }), errback);
        });
        const recv = await this.rpc('createTransport', { direction: 'recv' });
        this.recvTransport = this.device.createRecvTransport(recv);
        this.recvTransport.on('connect', ({ dtlsParameters }, callback, errback) => {
            this.rpc('connectTransport', { transportId: this.recvTransport.id, dtlsParameters })
                .then(() => callback(), errback);
        });
        this.watchTransport('send', this.sendTransport);
        this.watchTransport('recv', this.recvTransport);
        this.state = 'ready';
        window.addEventListener('pointerdown', this.speakerUnlock, true);
        this.emit('bound', [this.controlConnectionId]);
        const existing = await this.rpc('listProducers');
        for (const id of existing.producerIds || [])
            this.queueConsumer(id);
        this.statsTimer = setInterval(() => this.collectStats().catch(() => { }), 1000);
        if (this.outboundEnabled)
            await this.setOutbound(true);
    }
    watchTransport(label, transport) {
        transport.on('connectionstatechange', state => {
            if (this.closed) return;
            const timer = this.transportDisconnectTimers.get(label);
            if (state === 'failed' || state === 'closed') {
                if (timer) clearTimeout(timer);
                this.transportDisconnectTimers.delete(label);
                this.disconnect(label + '_transport_' + state);
                return;
            }
            if (state === 'disconnected') {
                if (!timer) {
                    const pending = setTimeout(() => {
                        this.transportDisconnectTimers.delete(label);
                        if (!this.closed) this.disconnect(label + '_transport_disconnected');
                    }, 8000);
                    this.transportDisconnectTimers.set(label, pending);
                }
            } else if (timer) {
                clearTimeout(timer);
                this.transportDisconnectTimers.delete(label);
            }
        });
    }
    clearConsumerRetry(producerId) {
        const timer = this.consumerRetryTimers.get(producerId);
        if (timer) clearTimeout(timer);
        this.consumerRetryTimers.delete(producerId);
        this.consumerRetryAttempts.delete(producerId);
    }
    queueConsumer(producerId) {
        if (this.closed || !producerId || this.closedProducers.has(producerId) ||
            this.consumerRetryTimers.has(producerId) || this.pendingProducers.has(producerId) ||
            this.consumers.has(producerId)) return;
        this.consumeRemote(producerId).then(() => {
            if (this.consumers.has(producerId)) this.clearConsumerRetry(producerId);
        }).catch(error => {
            if (this.closed || this.closedProducers.has(producerId)) return;
            this.removeConsumer(producerId);
            const attempts = (this.consumerRetryAttempts.get(producerId) || 0) + 1;
            this.consumerRetryAttempts.set(producerId, attempts);
            this.emit('failure', ['consumer_retry_' + attempts + '_' + safeDetail(error)]);
            if (attempts >= 4) {
                this.disconnect('consumer_recovery_exhausted');
                return;
            }
            const timer = setTimeout(() => {
                this.consumerRetryTimers.delete(producerId);
                if (!this.closed && !this.closedProducers.has(producerId)) this.queueConsumer(producerId);
            }, 2000);
            this.consumerRetryTimers.set(producerId, timer);
        });
    }
    onMessage(event) {
        let msg;
        try {
            msg = JSON.parse(event.data);
        }
        catch (_) {
            return;
        }
        if (Number.isInteger(msg.id) && this.pending.has(msg.id)) {
            const entry = this.pending.get(msg.id);
            this.pending.delete(msg.id);
            clearTimeout(entry.timer);
            msg.ok ? entry.resolve(msg.data) : entry.reject(new Error(safeDetail(msg.error)));
            return;
        }
        if (msg.event === 'newProducer' && msg.data?.producerId) {
            this.closedProducers.delete(msg.data.producerId);
            this.queueConsumer(msg.data.producerId);
        }
        else if (msg.event === 'producerClosed') {
            this.closedProducers.add(msg.data?.producerId);
            this.clearConsumerRetry(msg.data?.producerId);
            this.removeConsumer(msg.data?.producerId);
        }
    }
    async setOutbound(enabled) {
        this.outboundEnabled = Boolean(enabled);
        if (this.closed || this.state !== 'ready')
            return;
        if (!this.outboundEnabled) {
            this.producer?.pause();
            return;
        }
        if (this.producer && !this.producer.closed) {
            this.producer.resume();
            return;
        }
        // The Unity bridge may call setOutbound(true) again while the first
        // asynchronous sendTransport.produce() is still awaiting its SFU RPC.
        // One peer is allowed one Opus producer: never start a second RPC.
        if (this.producerPromise)
            return;
        const track = ensureTrack(this);
        const pending = this.sendTransport.produce({
            track, stopTracks: false, disableTrackOnPause: false, zeroRtpOnPause: true,
            codecOptions: { opusStereo: false, opusFec: true, opusDtx: true }
        });
        this.producerPromise = pending;
        try {
            const producer = await pending;
            if (this.closed || this.state !== 'ready') {
                producer.close();
                return;
            }
            this.producer = producer;
            // Respect a mute that arrived during the async creation.
            if (!this.outboundEnabled)
                producer.pause();
        }
        finally {
            if (this.producerPromise === pending)
                this.producerPromise = null;
        }
    }
    createSpatialPlayback(audio, senderConnectionId) {
        if (!senderConnectionId)
            return null;
        const context = this.pipeline.context;
        if (!context || context.state === 'closed' ||
            typeof context.createMediaStreamSource !== 'function' ||
            typeof context.createStereoPanner !== 'function' ||
            typeof context.createBiquadFilter !== 'function' ||
            typeof context.createGain !== 'function')
            return null;
        const spatial = { source: null, filter: null, panner: null, gain: null };
        try {
            spatial.source = context.createMediaStreamSource(audio.srcObject);
            spatial.filter = context.createBiquadFilter();
            spatial.filter.type = 'lowpass';
            spatial.panner = context.createStereoPanner();
            spatial.gain = context.createGain();
            spatial.source.connect(spatial.filter);
            spatial.filter.connect(spatial.panner);
            spatial.panner.connect(spatial.gain);
            spatial.gain.connect(context.destination);
            return spatial;
        }
        catch (error) {
            for (const node of Object.values(spatial)) {
                try {
                    node?.disconnect();
                }
                catch (_) { }
            }
            console.warn('[VME2_WebRTC] spatial graph unavailable', safeDetail(error));
            return null;
        }
    }
    applySpatialState(rec) {
        if (!rec.spatial)
            return false;
        const state = this.spatialBySender.get(rec.senderConnectionId) ||
            { pan: 0, gain: 1, rearAmount: 0 };
        const context = this.pipeline.context;
        if (!context || context.state === 'closed')
            return false;
        const now = context.currentTime;
        const frontHz = Math.min(22000, context.sampleRate * 0.45);
        const rearHz = frontHz + (6000 - frontHz) * state.rearAmount;
        rec.spatial.panner.pan.setTargetAtTime(state.pan, now, 0.035);
        rec.spatial.filter.frequency.setTargetAtTime(rearHz, now, 0.05);
        rec.spatial.gain.gain.setTargetAtTime(this.playbackEnabled && !rec.spatialDisabled && !vme2LocallyMutedSenders.has(rec.senderConnectionId) ? state.gain : 0, now, 0.035);
        return true;
    }
    setSpatialState(senderConnectionId, pan, gain, rearAmount) {
        if (this.closed || !senderConnectionId)
            return 0;
        const id = String(senderConnectionId).trim().toLowerCase();
        if (!/^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/.test(id))
            return 0;
        const state = this.spatialBySender.get(id) || { pan: 0, gain: 1, rearAmount: 0 };
        state.pan = clampFinite(pan, -1, 1);
        state.gain = clampFinite(gain, 0, 1);
        state.rearAmount = clampFinite(rearAmount, 0, 1);
        this.spatialBySender.set(id, state);
        let applied = 0;
        for (const rec of this.consumers.values()) {
            if (rec.senderConnectionId !== id || !rec.spatial)
                continue;
            rec.spatialDisabled = false;
            if (this.applySpatialState(rec))
                applied += 1;
        }
        return applied;
    }
    removeSpatialState(senderConnectionId) {
        if (this.closed || !senderConnectionId)
            return 0;
        const id = String(senderConnectionId).trim().toLowerCase();
        if (!/^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/.test(id))
            return 0;
        this.spatialBySender.delete(id);
        let applied = 0;
        for (const rec of this.consumers.values()) {
            if (rec.senderConnectionId !== id || !rec.spatial)
                continue;
            rec.spatialDisabled = true;
            if (this.applySpatialState(rec))
                applied += 1;
        }
        return applied;
    }
    async consumeRemote(producerId) {
        if (this.closed || this.state !== 'ready' || !producerId ||
            this.consumers.has(producerId) || this.pendingProducers.has(producerId))
            return;
        this.pendingProducers.add(producerId);
        try {
            const info = await this.rpc('consume', {
                transportId: this.recvTransport.id,
                producerId, rtpCapabilities: this.device.rtpCapabilities
            });
            if (this.closed)
                return;
            const consumer = await this.recvTransport.consume(info);
            if (this.closed) {
                consumer.close();
                return;
            }
            const audio = document.createElement('audio');
            audio.autoplay = true;
            audio.playsInline = true;
            audio.dataset.vme2Webrtc = '1';
            audio.style.display = 'none';
            audio.srcObject = new MediaStream([consumer.track]);
            const senderConnectionId = typeof info.senderConnectionId === 'string' &&
                /^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/i.test(info.senderConnectionId)
                ? info.senderConnectionId.toLowerCase() : '';
            // Apply previously selected mute BEFORE playback can start.
            consumer.track.enabled = !vme2LocallyMutedSenders.has(senderConnectionId);
            audio.dataset.vme2SenderConnectionId = senderConnectionId;
            const spatial = this.createSpatialPlayback(audio, senderConnectionId);
            audio.muted = spatial ? true : (!this.playbackEnabled || vme2LocallyMutedSenders.has(senderConnectionId));
            document.body.appendChild(audio);
            const rec = { consumer, audio, senderConnectionId, spatial, spatialDisabled: false };
            this.consumers.set(producerId, rec);
            if (spatial)
                this.applySpatialState(rec);
            else
                console.warn('[VME2_WebRTC] spatial fallback', senderConnectionId ? 'audio_graph' : 'sender_id_missing');
            try {
                await this.rpc('resumeConsumer', { consumerId: info.id });
                if (!this.closed)
                    await audio.play().catch(() => { });
            }
            catch (error) {
                this.removeConsumer(producerId);
                throw error;
            }
        }
        finally {
            this.pendingProducers.delete(producerId);
        }
    }
    removeConsumer(producerId) {
        const rec = this.consumers.get(producerId);
        if (!rec)
            return;
        rec.consumer.close();
        for (const node of Object.values(rec.spatial || {})) {
            try {
                node?.disconnect();
            }
            catch (_) { }
        }
        rec.audio.pause();
        rec.audio.srcObject = null;
        rec.audio.remove();
        this.consumers.delete(producerId);
    }
    setLocallyMutedPeer(senderConnectionId, muted) {
        let touched = 0;
        for (const rec of this.consumers.values()) {
            if (rec.senderConnectionId !== senderConnectionId) continue;
            // Disable the actual inbound MediaStreamTrack. This affects WebAudio
            // spatial playback as well as HTMLAudio fallback in this browser.
            rec.consumer.track.enabled = !muted;
            if (rec.spatial) this.applySpatialState(rec);
            else rec.audio.muted = !this.playbackEnabled || muted;
            touched++;
        }
        return touched;
    }
    setPlaybackEnabled(enabled) {
        this.playbackEnabled = Boolean(enabled);
        for (const rec of this.consumers.values()) {
            if (rec.spatial)
                this.applySpatialState(rec);
            else
                rec.audio.muted = !this.playbackEnabled || vme2LocallyMutedSenders.has(rec.senderConnectionId);
        }
    }
    async collectStats() {
        if (this.closed || this.state !== 'ready')
            return;
        // The old Voice microphone selector can replace its MediaStreamTrack.
        // Retain one capture source by following that change instead of calling getUserMedia again.
        if (this.producer && !this.producer.closed) {
            const current = this.pipeline.stream?.getAudioTracks?.()[0];
            if (current && current.readyState === 'live' && current !== this.producer.track) {
                await this.producer.replaceTrack({ track: current });
            }
        }
        let sent = 0, received = 0, lost = 0, jitter = 0, concealed = 0;
        const reports = await Promise.all([this.sendTransport?.getStats(), this.recvTransport?.getStats()]);
        for (const report of reports)
            report?.forEach(item => {
                if (item.type === 'outbound-rtp' && (item.kind === 'audio' || item.mediaType === 'audio'))
                    sent += item.packetsSent || 0;
                if (item.type === 'inbound-rtp' && (item.kind === 'audio' || item.mediaType === 'audio')) {
                    received += item.packetsReceived || 0;
                    lost += item.packetsLost || 0;
                    jitter = Math.max(jitter, item.jitter || 0);
                    concealed += item.concealedSamples || 0;
                }
            });
        window.VME2WebRtcLastStats = { at: new Date().toISOString(), sent, received, lost, jitterSeconds: jitter, concealedSamples: concealed, consumers: this.consumers.size, protocol: 'WebRTC/Opus-RTP' };
        this.emit('stats', ['1', String(sent), String(received), '0', '0', '0', '0', '0', '0']);
    }
    disconnect(reason) {
        if (this.closed)
            return;
        this.emit('disconnected', [reason || 'disconnected', '0']);
        this.close();
    }
    close() {
        if (this.closed)
            return;
        this.closed = true;
        this.state = 'closed';
        if (this.statsTimer)
            clearInterval(this.statsTimer);
        window.removeEventListener('pointerdown', this.speakerUnlock, true);
        for (const pending of this.pending.values()) {
            clearTimeout(pending.timer);
            pending.reject(new Error('CLOSED'));
        }
        this.pending.clear();
        for (const timer of this.transportDisconnectTimers.values()) clearTimeout(timer);
        this.transportDisconnectTimers.clear();
        for (const timer of this.consumerRetryTimers.values()) clearTimeout(timer);
        this.consumerRetryTimers.clear();
        this.spatialBySender.clear();
        for (const id of [...this.consumers.keys()])
            this.removeConsumer(id);
        try {
            this.producer?.close();
        }
        catch (_) { }
        try {
            this.sendTransport?.close();
        }
        catch (_) { }
        try {
            this.recvTransport?.close();
        }
        catch (_) { }
        try {
            this.ws?.close(1000, 'leave');
        }
        catch (_) { }
        connections.delete(this.handle);
    }
}
// W4 read-only, post-gain audio meter. Never opens a second microphone.
const levelMeterStates = new Map();
function meterDispose(node) {
    if (!node) return;
    // Disconnect only the analyser branch; preserve the original audio output.
    try { node.upstream?.disconnect(node.analyser); } catch (_) { }
    try { node.source?.disconnect(); } catch (_) { }
    try { node.analyser?.disconnect(); } catch (_) { }
    try { node.silent?.disconnect(); } catch (_) { }
}
function meterDisposeState(state) {
    meterDispose(state.mic);
    for (const node of state.speakers.values()) meterDispose(node);
    state.speakers.clear();
}
function meterAttach(context, upstream, track, ownSource) {
    if (!context || context.state === 'closed' || !track || track.readyState !== 'live')
        return null;
    let source = null, analyser = null, silent = null;
    try {
        if (ownSource) source = context.createMediaStreamSource(new MediaStream([track]));
        analyser = context.createAnalyser();
        analyser.fftSize = 512;
        analyser.smoothingTimeConstant = 0;
        silent = context.createGain();
        silent.gain.value = 0;
        (source || upstream).connect(analyser);
        analyser.connect(silent);
        silent.connect(context.destination);
        return { context, track, source, upstream, analyser, silent,
            samples: new Float32Array(analyser.fftSize) };
    }
    catch (_) {
        meterDispose({ source, analyser, silent });
        return null;
    }
}
function meterRms(node) {
    if (!node || node.context.state !== 'running' || node.track.readyState !== 'live')
        return 0;
    try {
        node.analyser.getFloatTimeDomainData(node.samples);
        let sum = 0;
        for (let i = 0; i < node.samples.length; ++i) sum += node.samples[i] * node.samples[i];
        return Math.min(1, Math.sqrt(sum / node.samples.length));
    } catch (_) { return 0; }
}
function readWebRtcLevelsPacked() {
    for (const [handle, state] of levelMeterStates) {
        if (!connections.has(handle) || connections.get(handle).closed) {
            meterDisposeState(state);
            levelMeterStates.delete(handle);
        }
    }
    let microphone = 0, speakerSumSq = 0;
    for (const [handle, session] of connections) {
        if (session.closed || session.state !== 'ready') continue;
        const context = session.pipeline?.context;
        if (!context || context.state !== 'running') continue;
        let state = levelMeterStates.get(handle);
        if (!state) {
            state = { mic: null, speakers: new Map() };
            levelMeterStates.set(handle, state);
        }
        const producerTrack = session.producer?.track;
        const publishing = Boolean(session.outboundEnabled && session.producer &&
            session.sendTransport?.connectionState === 'connected' &&
            !session.producer.closed && !session.producer.paused &&
            producerTrack?.readyState === 'live');
        if (state.mic && (state.mic.track !== producerTrack || state.mic.context !== context)) {
            meterDispose(state.mic);
            state.mic = null;
        }
        if (publishing) {
            if (!state.mic) state.mic = meterAttach(context, null, producerTrack, true);
            microphone = Math.max(microphone, meterRms(state.mic));
        }
        else if (state.mic) {
            // Do not continue consuming audio CPU when microphone publishing stops.
            meterDispose(state.mic);
            state.mic = null;
        }
        const active = new Set();
        if (session.playbackEnabled &&
            session.recvTransport?.connectionState === 'connected')
            for (const [producerId, rec] of session.consumers) {
            const track = rec.consumer?.track;
            if (!track || track.readyState !== 'live' || rec.consumer.closed) continue;
            // Spatial branch is analysed AFTER its gain, so proximity and speaker
            // attenuation remain authoritative. Fallback monitors direct track.
            const gain = rec.spatial?.gain || null;
            const ownSource = !gain;
            if (ownSource && (rec.audio?.muted || rec.audio?.paused)) continue;
            active.add(producerId);
            let node = state.speakers.get(producerId);
            if (node && (node.track !== track || node.context !== context || node.upstream !== gain)) {
                meterDispose(node);
                state.speakers.delete(producerId);
                node = null;
            }
            if (!node) {
                node = meterAttach(context, gain, track, ownSource);
                if (node) state.speakers.set(producerId, node);
            }
            const rms = meterRms(node);
            speakerSumSq += rms * rms;
        }
        for (const [producerId, node] of state.speakers) {
            if (!active.has(producerId)) {
                meterDispose(node);
                state.speakers.delete(producerId);
            }
        }
    }
    const speaker = Math.min(1, Math.sqrt(speakerSumSq));
    return (Math.round(microphone * 1023) << 10) | Math.round(speaker * 1023);
}
window.VME2WebRtcVoice = Object.freeze({
    getLevelsPacked() { return readWebRtcLevelsPacked(); },
    connect(opts) {
        if (connections.has(opts.handle))
            throw new Error('HANDLE_ALREADY_CONNECTED');
        const instance = new Connection(opts);
        connections.set(opts.handle, instance);
        instance.initialize().catch(e => {
            if (instance.closed)
                return;
            instance.emit('failure', [safeDetail(e)]);
            instance.disconnect('connect_failed');
        });
        return true;
    },
    setOutbound(handle, enabled) {
        const s = connections.get(handle);
        if (!s || s.closed)
            return false;
        s.setOutbound(Boolean(enabled)).catch(e => {
            s.emit('failure', [safeDetail(e)]);
            s.disconnect('outbound_producer_failed');
        });
        return true;
    },
    setPlaybackEnabled(handle, enabled) {
        const s = connections.get(handle);
        if (s)
            s.setPlaybackEnabled(Boolean(enabled));
        return Boolean(s);
    },
    setLocallyMutedPeer(senderConnectionId, muted) {
        const id = String(senderConnectionId || '').trim().toLowerCase();
        if (!/^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/.test(id)) return -2;
        if (muted) vme2LocallyMutedSenders.add(id);
        else vme2LocallyMutedSenders.delete(id);
        let applied = 0;
        for (const s of connections.values()) {
            if (!s.closed) applied += s.setLocallyMutedPeer(id, Boolean(muted));
        }
        // accepted even if no active consumer yet: mute persisted until its creation
        return applied;
    },
    getLocallyMutedPeerCount() { return vme2LocallyMutedSenders.size; },
    setSpatialState(senderConnectionId, pan, gain, rearAmount) {
        let delivered = 0;
        for (const s of connections.values()) {
            delivered += s.setSpatialState(senderConnectionId, pan, gain, rearAmount);
        }
        return delivered;
    },
    removeSpatialState(senderConnectionId) {
        let delivered = 0;
        for (const s of connections.values()) {
            delivered += s.removeSpatialState(senderConnectionId);
        }
        return delivered;
    },
    disconnect(handle) {
        const s = connections.get(handle);
        if (s)
            s.close();
    }
});

},
1: function(module, exports, __req) {
"use strict";
var __createBinding = (this && this.__createBinding) || (Object.create ? (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    var desc = Object.getOwnPropertyDescriptor(m, k);
    if (!desc || ("get" in desc ? !m.__esModule : desc.writable || desc.configurable)) {
      desc = { enumerable: true, get: function() { return m[k]; } };
    }
    Object.defineProperty(o, k2, desc);
}) : (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    o[k2] = m[k];
}));
var __setModuleDefault = (this && this.__setModuleDefault) || (Object.create ? (function(o, v) {
    Object.defineProperty(o, "default", { enumerable: true, value: v });
}) : function(o, v) {
    o["default"] = v;
});
var __importStar = (this && this.__importStar) || (function () {
    var ownKeys = function(o) {
        ownKeys = Object.getOwnPropertyNames || function (o) {
            var ar = [];
            for (var k in o) if (Object.prototype.hasOwnProperty.call(o, k)) ar[ar.length] = k;
            return ar;
        };
        return ownKeys(o);
    };
    return function (mod) {
        if (mod && mod.__esModule) return mod;
        var result = {};
        if (mod != null) for (var k = ownKeys(mod), i = 0; i < k.length; i++) if (k[i] !== "default") __createBinding(result, mod, k[i]);
        __setModuleDefault(result, mod);
        return result;
    };
})();
var __importDefault = (this && this.__importDefault) || function (mod) {
    return (mod && mod.__esModule) ? mod : { "default": mod };
};
Object.defineProperty(exports, "__esModule", { value: true });
exports.debug = exports.testFakeParameters = exports.FakeHandler = exports.enhancedEvents = exports.ortc = exports.parseScalabilityMode = exports.detectDeviceAsync = exports.detectDevice = exports.Device = exports.version = exports.types = void 0;
const debug_1 = __importDefault(__req(2));
exports.debug = debug_1.default;
/**
 * Expose all types.
 */
exports.types = __importStar(__req(3));
/**
 * Expose mediasoup-client version.
 */
exports.version = '3.24.5';
/**
 * Expose Device class and device detector helpers.
 */
var Device_1 = __req(4);
Object.defineProperty(exports, "Device", { enumerable: true, get: function () { return Device_1.Device; } });
Object.defineProperty(exports, "detectDevice", { enumerable: true, get: function () { return Device_1.detectDevice; } });
Object.defineProperty(exports, "detectDeviceAsync", { enumerable: true, get: function () { return Device_1.detectDeviceAsync; } });
/**
 * Expose parseScalabilityMode() function.
 */
var scalabilityModes_1 = __req(5);
Object.defineProperty(exports, "parseScalabilityMode", { enumerable: true, get: function () { return scalabilityModes_1.parse; } });
/**
 * Expose all ORTC functions.
 */
exports.ortc = __importStar(__req(6));
/**
 * Expose enhanced events.
 */
exports.enhancedEvents = __importStar(__req(7));
/**
 * Expose FakeHandler.
 */
var FakeHandler_1 = __req(8);
Object.defineProperty(exports, "FakeHandler", { enumerable: true, get: function () { return FakeHandler_1.FakeHandler; } });
/**
 * Expose test/fakeParameters utils.
 */
exports.testFakeParameters = __importStar(__req(9));

},
2: function(module, exports, __req) {
/* eslint-env browser */

/**
 * This is the web browser implementation of `debug()`.
 */

exports.formatArgs = formatArgs;
exports.save = save;
exports.load = load;
exports.useColors = useColors;
exports.storage = localstorage();
exports.destroy = (() => {
	let warned = false;

	return () => {
		if (!warned) {
			warned = true;
			console.warn('Instance method `debug.destroy()` is deprecated and no longer does anything. It will be removed in the next major version of `debug`.');
		}
	};
})();

/**
 * Colors.
 */

exports.colors = [
	'#0000CC',
	'#0000FF',
	'#0033CC',
	'#0033FF',
	'#0066CC',
	'#0066FF',
	'#0099CC',
	'#0099FF',
	'#00CC00',
	'#00CC33',
	'#00CC66',
	'#00CC99',
	'#00CCCC',
	'#00CCFF',
	'#3300CC',
	'#3300FF',
	'#3333CC',
	'#3333FF',
	'#3366CC',
	'#3366FF',
	'#3399CC',
	'#3399FF',
	'#33CC00',
	'#33CC33',
	'#33CC66',
	'#33CC99',
	'#33CCCC',
	'#33CCFF',
	'#6600CC',
	'#6600FF',
	'#6633CC',
	'#6633FF',
	'#66CC00',
	'#66CC33',
	'#9900CC',
	'#9900FF',
	'#9933CC',
	'#9933FF',
	'#99CC00',
	'#99CC33',
	'#CC0000',
	'#CC0033',
	'#CC0066',
	'#CC0099',
	'#CC00CC',
	'#CC00FF',
	'#CC3300',
	'#CC3333',
	'#CC3366',
	'#CC3399',
	'#CC33CC',
	'#CC33FF',
	'#CC6600',
	'#CC6633',
	'#CC9900',
	'#CC9933',
	'#CCCC00',
	'#CCCC33',
	'#FF0000',
	'#FF0033',
	'#FF0066',
	'#FF0099',
	'#FF00CC',
	'#FF00FF',
	'#FF3300',
	'#FF3333',
	'#FF3366',
	'#FF3399',
	'#FF33CC',
	'#FF33FF',
	'#FF6600',
	'#FF6633',
	'#FF9900',
	'#FF9933',
	'#FFCC00',
	'#FFCC33'
];

/**
 * Currently only WebKit-based Web Inspectors, Firefox >= v31,
 * and the Firebug extension (any Firefox version) are known
 * to support "%c" CSS customizations.
 *
 * TODO: add a `localStorage` variable to explicitly enable/disable colors
 */

// eslint-disable-next-line complexity
function useColors() {
	// NB: In an Electron preload script, document will be defined but not fully
	// initialized. Since we know we're in Chrome, we'll just detect this case
	// explicitly
	if (typeof window !== 'undefined' && window.process && (window.process.type === 'renderer' || window.process.__nwjs)) {
		return true;
	}

	// Internet Explorer and Edge do not support colors.
	if (typeof navigator !== 'undefined' && navigator.userAgent && navigator.userAgent.toLowerCase().match(/(edge|trident)\/(\d+)/)) {
		return false;
	}

	let m;

	// Is webkit? http://stackoverflow.com/a/16459606/376773
	// document is undefined in react-native: https://github.com/facebook/react-native/pull/1632
	// eslint-disable-next-line no-return-assign
	return (typeof document !== 'undefined' && document.documentElement && document.documentElement.style && document.documentElement.style.WebkitAppearance) ||
		// Is firebug? http://stackoverflow.com/a/398120/376773
		(typeof window !== 'undefined' && window.console && (window.console.firebug || (window.console.exception && window.console.table))) ||
		// Is firefox >= v31?
		// https://developer.mozilla.org/en-US/docs/Tools/Web_Console#Styling_messages
		(typeof navigator !== 'undefined' && navigator.userAgent && (m = navigator.userAgent.toLowerCase().match(/firefox\/(\d+)/)) && parseInt(m[1], 10) >= 31) ||
		// Double check webkit in userAgent just in case we are in a worker
		(typeof navigator !== 'undefined' && navigator.userAgent && navigator.userAgent.toLowerCase().match(/applewebkit\/(\d+)/));
}

/**
 * Colorize log arguments if enabled.
 *
 * @api public
 */

function formatArgs(args) {
	args[0] = (this.useColors ? '%c' : '') +
		this.namespace +
		(this.useColors ? ' %c' : ' ') +
		args[0] +
		(this.useColors ? '%c ' : ' ') +
		'+' + module.exports.humanize(this.diff);

	if (!this.useColors) {
		return;
	}

	const c = 'color: ' + this.color;
	args.splice(1, 0, c, 'color: inherit');

	// The final "%c" is somewhat tricky, because there could be other
	// arguments passed either before or after the %c, so we need to
	// figure out the correct index to insert the CSS into
	let index = 0;
	let lastC = 0;
	args[0].replace(/%[a-zA-Z%]/g, match => {
		if (match === '%%') {
			return;
		}
		index++;
		if (match === '%c') {
			// We only are interested in the *last* %c
			// (the user may have provided their own)
			lastC = index;
		}
	});

	args.splice(lastC, 0, c);
}

/**
 * Invokes `console.debug()` when available.
 * No-op when `console.debug` is not a "function".
 * If `console.debug` is not available, falls back
 * to `console.log`.
 *
 * @api public
 */
exports.log = console.debug || console.log || (() => {});

/**
 * Save `namespaces`.
 *
 * @param {String} namespaces
 * @api private
 */
function save(namespaces) {
	try {
		if (namespaces) {
			exports.storage.setItem('debug', namespaces);
		} else {
			exports.storage.removeItem('debug');
		}
	} catch (error) {
		// Swallow
		// XXX (@Qix-) should we be logging these?
	}
}

/**
 * Load `namespaces`.
 *
 * @return {String} returns the previously persisted debug modes
 * @api private
 */
function load() {
	let r;
	try {
		r = exports.storage.getItem('debug') || exports.storage.getItem('DEBUG') ;
	} catch (error) {
		// Swallow
		// XXX (@Qix-) should we be logging these?
	}

	// If debug isn't set in LS, and we're in Electron, try to load $DEBUG
	if (!r && typeof process !== 'undefined' && 'env' in process) {
		r = process.env.DEBUG;
	}

	return r;
}

/**
 * Localstorage attempts to return the localstorage.
 *
 * This is necessary because safari throws
 * when a user disables cookies/localstorage
 * and you attempt to access it.
 *
 * @return {LocalStorage}
 * @api private
 */

function localstorage() {
	try {
		// TVMLKit (Apple TV JS Runtime) does not have a window object, just localStorage in the global context
		// The Browser also has localStorage in the global context.
		return localStorage;
	} catch (error) {
		// Swallow
		// XXX (@Qix-) should we be logging these?
	}
}

module.exports = __req(10)(exports);

const {formatters} = module.exports;

/**
 * Map %j to `JSON.stringify()`, since no Web Inspectors do that by default.
 */

formatters.j = function (v) {
	try {
		return JSON.stringify(v);
	} catch (error) {
		return '[UnexpectedJSONParseError]: ' + error.message;
	}
};

},
3: function(module, exports, __req) {
"use strict";
Object.defineProperty(exports, "__esModule", { value: true });

},
4: function(module, exports, __req) {
"use strict";
var __createBinding = (this && this.__createBinding) || (Object.create ? (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    var desc = Object.getOwnPropertyDescriptor(m, k);
    if (!desc || ("get" in desc ? !m.__esModule : desc.writable || desc.configurable)) {
      desc = { enumerable: true, get: function() { return m[k]; } };
    }
    Object.defineProperty(o, k2, desc);
}) : (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    o[k2] = m[k];
}));
var __setModuleDefault = (this && this.__setModuleDefault) || (Object.create ? (function(o, v) {
    Object.defineProperty(o, "default", { enumerable: true, value: v });
}) : function(o, v) {
    o["default"] = v;
});
var __importStar = (this && this.__importStar) || (function () {
    var ownKeys = function(o) {
        ownKeys = Object.getOwnPropertyNames || function (o) {
            var ar = [];
            for (var k in o) if (Object.prototype.hasOwnProperty.call(o, k)) ar[ar.length] = k;
            return ar;
        };
        return ownKeys(o);
    };
    return function (mod) {
        if (mod && mod.__esModule) return mod;
        var result = {};
        if (mod != null) for (var k = ownKeys(mod), i = 0; i < k.length; i++) if (k[i] !== "default") __createBinding(result, mod, k[i]);
        __setModuleDefault(result, mod);
        return result;
    };
})();
Object.defineProperty(exports, "__esModule", { value: true });
exports.Device = void 0;
exports.detectDevice = detectDevice;
exports.detectDeviceAsync = detectDeviceAsync;
const Logger_1 = __req(11);
const enhancedEvents_1 = __req(7);
const errors_1 = __req(12);
const utils = __importStar(__req(13));
const ortc = __importStar(__req(6));
const Transport_1 = __req(14);
const Chrome111_1 = __req(15);
const Chrome74_1 = __req(16);
const Firefox120_1 = __req(17);
const Safari12_1 = __req(18);
const ReactNative106_1 = __req(19);
const logger = new Logger_1.Logger('Device');
/**
 * Sync mediasoup-client Handler detection.
 */
function detectDevice(userAgent, userAgentData) {
    logger.debug('detectDevice()');
    if (!userAgent && typeof navigator === 'object') {
        userAgent = navigator.userAgent;
    }
    if (!userAgentData && typeof navigator === 'object') {
        userAgentData = navigator.userAgentData;
    }
    return detectDeviceImpl(userAgent, userAgentData);
}
/**
 * Async mediasoup-client Handler detection.
 *
 * @remarks
 * - Currently it runs same logic than `detectDevice()`.
 * - In the future this function could give better results than
 *   `detectDevice()`.
 */
async function detectDeviceAsync(userAgent, userAgentData) {
    logger.debug('detectDeviceAsync()');
    if (!userAgent && typeof navigator === 'object') {
        userAgent = navigator.userAgent;
    }
    if (!userAgentData && typeof navigator === 'object') {
        userAgentData = navigator.userAgentData;
    }
    return detectDeviceImpl(userAgent, userAgentData);
}
class Device {
    // RTC handler factory.
    _handlerFactory;
    // Handler name.
    _handlerName;
    // Loaded flag.
    _loaded = false;
    // Callback for sending Transports to request sending extended RTP capabilities
    // on demand.
    _getSendExtendedRtpCapabilities;
    // Local RTP capabilities for receiving media.
    _recvRtpCapabilities;
    // Local RTP capabilities for sending media.
    _sendRtpCapabilities;
    // Whether we can produce audio/video based on remote RTP capabilities.
    _canProduceByKind = {
        audio: false,
        video: false,
    };
    // Observer instance.
    _observer = new enhancedEvents_1.EnhancedEventEmitter();
    /**
     * Create a new Device to connect to mediasoup server. It uses a more advanced
     * device detection.
     *
     * @throws {UnsupportedError} if device is not supported.
     */
    static async factory({ handlerName, handlerFactory, forcedRtpExtensions, } = {}) {
        logger.debug('factory()');
        if (handlerName && handlerFactory) {
            throw new TypeError('just one of handlerName or handlerInterface can be given');
        }
        if (!handlerName && !handlerFactory) {
            handlerName = await detectDeviceAsync();
            if (!handlerName) {
                throw new errors_1.UnsupportedError('device not supported');
            }
        }
        return new Device({
            handlerName,
            handlerFactory,
            forcedRtpExtensions,
        });
    }
    /**
     * Create a new Device to connect to mediasoup server.
     *
     * @throws {UnsupportedError} if device is not supported.
     */
    constructor({ handlerName, handlerFactory, forcedRtpExtensions, } = {}) {
        logger.debug('constructor()');
        if (handlerName && handlerFactory) {
            throw new TypeError('just one of handlerName or handlerInterface can be given');
        }
        if (handlerFactory) {
            this._handlerFactory = handlerFactory;
        }
        else {
            if (handlerName) {
                logger.debug('constructor() | handler given: %s', handlerName);
            }
            else {
                handlerName = detectDevice();
                if (handlerName) {
                    logger.debug('constructor() | detected handler: %s', handlerName);
                }
                else {
                    throw new errors_1.UnsupportedError('device not supported');
                }
            }
            switch (handlerName) {
                case 'Chrome111': {
                    this._handlerFactory = Chrome111_1.Chrome111.createFactory({
                        forcedRtpExtensions,
                    });
                    break;
                }
                case 'Chrome74': {
                    this._handlerFactory = Chrome74_1.Chrome74.createFactory({
                        forcedRtpExtensions,
                    });
                    break;
                }
                case 'Firefox120': {
                    this._handlerFactory = Firefox120_1.Firefox120.createFactory({
                        forcedRtpExtensions,
                    });
                    break;
                }
                case 'Safari12': {
                    this._handlerFactory = Safari12_1.Safari12.createFactory({
                        forcedRtpExtensions,
                    });
                    break;
                }
                case 'ReactNative106': {
                    this._handlerFactory = ReactNative106_1.ReactNative106.createFactory({
                        forcedRtpExtensions,
                    });
                    break;
                }
                default: {
                    throw new TypeError(`unknown handlerName "${handlerName}"`);
                }
            }
        }
        this._handlerName = this._handlerFactory.name;
    }
    /**
     * The RTC handler name.
     */
    get handlerName() {
        return this._handlerName;
    }
    /**
     * Whether the Device is loaded.
     */
    get loaded() {
        return this._loaded;
    }
    /**
     * RTP capabilities of the Device for receiving media.
     *
     * @deprecated Use {@link recvRtpCapabilities} instead.
     *
     * @throws {InvalidStateError} if not loaded.
     */
    get rtpCapabilities() {
        return this.recvRtpCapabilities;
    }
    /**
     * RTP capabilities of the Device for receiving media.
     *
     * @throws {InvalidStateError} if not loaded.
     */
    get recvRtpCapabilities() {
        if (!this._loaded) {
            throw new errors_1.InvalidStateError('not loaded');
        }
        return this._recvRtpCapabilities;
    }
    /**
     * RTP capabilities of the Device for sending media.
     *
     * @throws {InvalidStateError} if not loaded.
     */
    get sendRtpCapabilities() {
        if (!this._loaded) {
            throw new errors_1.InvalidStateError('not loaded');
        }
        return this._sendRtpCapabilities;
    }
    get observer() {
        return this._observer;
    }
    /**
     * Initialize the Device.
     */
    async load({ routerRtpCapabilities, preferLocalCodecsOrder = false, }) {
        logger.debug('load() [routerRtpCapabilities:%o]', routerRtpCapabilities);
        if (this._loaded) {
            throw new errors_1.InvalidStateError('already loaded');
        }
        // Clone given router RTP capabilities to not modify input data.
        const clonedRouterRtpCapabilities = utils.clone(routerRtpCapabilities);
        // This may throw.
        ortc.validateAndNormalizeRtpCapabilities(clonedRouterRtpCapabilities);
        const { getNativeRtpCapabilities } = this._handlerFactory;
        const clonedNativeRecvRtpCapabilities = utils.clone(await getNativeRtpCapabilities({ direction: 'recvonly' }));
        logger.debug('load() | got native receiving RTP capabilities:%o', clonedNativeRecvRtpCapabilities);
        // This may throw.
        ortc.validateAndNormalizeRtpCapabilities(clonedNativeRecvRtpCapabilities);
        const clonedNativeSendRtpCapabilities = utils.clone(await getNativeRtpCapabilities({ direction: 'sendonly' }));
        logger.debug('load() | got native sending RTP capabilities:%o', clonedNativeSendRtpCapabilities);
        // This may throw.
        ortc.validateAndNormalizeRtpCapabilities(clonedNativeSendRtpCapabilities);
        this._getSendExtendedRtpCapabilities = (nativeSendRtpCapabilities) => {
            return utils.clone(ortc.getExtendedRtpCapabilities(nativeSendRtpCapabilities, clonedRouterRtpCapabilities, preferLocalCodecsOrder));
        };
        const recvExtendedRtpCapabilities = ortc.getExtendedRtpCapabilities(clonedNativeRecvRtpCapabilities, clonedRouterRtpCapabilities, 
        /* preferLocalCodecsOrder */ false);
        // Generate our receiving RTP capabilities for receiving media.
        this._recvRtpCapabilities = ortc.getRecvRtpCapabilities(recvExtendedRtpCapabilities);
        logger.debug('load() | got receiving RTP capabilities:%o', this._recvRtpCapabilities);
        // This may throw.
        ortc.validateAndNormalizeRtpCapabilities(this._recvRtpCapabilities);
        const sendExtendedRtpCapabilities = ortc.getExtendedRtpCapabilities(clonedNativeSendRtpCapabilities, clonedRouterRtpCapabilities, preferLocalCodecsOrder);
        // Generate our sending RTP capabilities for sending media.
        this._sendRtpCapabilities = ortc.getSendRtpCapabilities(sendExtendedRtpCapabilities);
        logger.debug('load() | got sending RTP capabilities:%o', this._sendRtpCapabilities);
        // This may throw.
        ortc.validateAndNormalizeRtpCapabilities(this._sendRtpCapabilities);
        // Check whether we can produce audio/video.
        this._canProduceByKind.audio = ortc.canSend('audio', this._sendRtpCapabilities);
        this._canProduceByKind.video = ortc.canSend('video', this._sendRtpCapabilities);
        logger.debug('load() succeeded');
        this._loaded = true;
    }
    /**
     * Whether we can produce audio/video.
     *
     * @throws {InvalidStateError} if not loaded.
     * @throws {TypeError} if wrong arguments.
     */
    canProduce(kind) {
        if (!this._loaded) {
            throw new errors_1.InvalidStateError('not loaded');
        }
        else if (kind !== 'audio' && kind !== 'video') {
            throw new TypeError(`invalid kind "${kind}"`);
        }
        return this._canProduceByKind[kind];
    }
    /**
     * Creates a Transport for sending media.
     *
     * @throws {InvalidStateError} if not loaded.
     * @throws {TypeError} if wrong arguments.
     */
    createSendTransport({ id, iceParameters, iceCandidates, dtlsParameters, sctpParameters, iceServers, iceTransportPolicy, additionalSettings, appData, }) {
        logger.debug('createSendTransport()');
        return this.createTransport({
            direction: 'send',
            id,
            iceParameters,
            iceCandidates,
            dtlsParameters,
            sctpParameters,
            iceServers,
            iceTransportPolicy,
            additionalSettings,
            appData,
        });
    }
    /**
     * Creates a Transport for receiving media.
     *
     * @throws {InvalidStateError} if not loaded.
     * @throws {TypeError} if wrong arguments.
     */
    createRecvTransport({ id, iceParameters, iceCandidates, dtlsParameters, sctpParameters, iceServers, iceTransportPolicy, additionalSettings, appData, }) {
        logger.debug('createRecvTransport()');
        return this.createTransport({
            direction: 'recv',
            id,
            iceParameters,
            iceCandidates,
            dtlsParameters,
            sctpParameters,
            iceServers,
            iceTransportPolicy,
            additionalSettings,
            appData,
        });
    }
    createTransport({ direction, id, iceParameters, iceCandidates, dtlsParameters, sctpParameters, iceServers, iceTransportPolicy, additionalSettings, appData, }) {
        if (!this._loaded) {
            throw new errors_1.InvalidStateError('not loaded');
        }
        else if (typeof id !== 'string') {
            throw new TypeError('missing id');
        }
        else if (typeof iceParameters !== 'object') {
            throw new TypeError('missing iceParameters');
        }
        else if (!Array.isArray(iceCandidates)) {
            throw new TypeError('missing iceCandidates');
        }
        else if (typeof dtlsParameters !== 'object') {
            throw new TypeError('missing dtlsParameters');
        }
        else if (sctpParameters && typeof sctpParameters !== 'object') {
            throw new TypeError('wrong sctpParameters');
        }
        else if (appData && typeof appData !== 'object') {
            throw new TypeError('if given, appData must be an object');
        }
        // Create a new Transport.
        const transport = new Transport_1.Transport({
            direction,
            id,
            iceParameters,
            iceCandidates,
            dtlsParameters,
            sctpParameters,
            iceServers,
            iceTransportPolicy,
            additionalSettings,
            appData,
            handlerFactory: this._handlerFactory,
            getSendExtendedRtpCapabilities: this._getSendExtendedRtpCapabilities,
            recvRtpCapabilities: this._recvRtpCapabilities,
            canProduceByKind: this._canProduceByKind,
        });
        // Emit observer event.
        this._observer.safeEmit('newtransport', transport);
        return transport;
    }
}
exports.Device = Device;
function detectDeviceImpl(userAgent, userAgentData) {
    logger.debug('detectDeviceImpl() [userAgent:"%s", userAgentData:%o]', userAgent, userAgentData);
    const chromiumMajorVersion = getChromiumMajorVersion(userAgent, userAgentData);
    if (chromiumMajorVersion) {
        if (chromiumMajorVersion >= 111) {
            logger.debug('detectDeviceImpl() | using Chrome111 handler');
            return 'Chrome111';
        }
        else if (chromiumMajorVersion >= 74) {
            logger.debug('detectDeviceImpl() | using Chrome74 handler');
            return 'Chrome74';
        }
        else {
            logger.warn('detectDeviceImpl() | unsupported Chromium based browser/version');
            return undefined;
        }
    }
    const firefoxMajorVersion = getFirefoxMajorVersion(userAgent);
    if (firefoxMajorVersion) {
        if (firefoxMajorVersion >= 120) {
            logger.debug('detectDeviceImpl() | using Firefox120 handler');
            return 'Firefox120';
        }
        else {
            logger.warn('detectDeviceImpl() | unsupported Firefox browser/version');
            return undefined;
        }
    }
    const macOSWebKitMajorVersion = getMacOSWebKitMajorVersion(userAgent);
    if (macOSWebKitMajorVersion) {
        if (macOSWebKitMajorVersion >= 605) {
            logger.debug('detectDeviceImpl() | using Safari12 handler');
            return 'Safari12';
        }
        else {
            logger.warn('detectDeviceImpl() | unsupported desktop Safari browser/version');
            return undefined;
        }
    }
    const iOSWebKitMajorVersion = getIOSWebKitMajorVersion(userAgent);
    if (iOSWebKitMajorVersion) {
        if (iOSWebKitMajorVersion >= 605) {
            logger.debug('detectDeviceImpl() | using Safari12 handler');
            return 'Safari12';
        }
        else {
            logger.warn('detectDeviceImpl() | unsupported iOS Safari based browser/version');
            return undefined;
        }
    }
    if (isReactNative()) {
        if (typeof RTCPeerConnection !== 'undefined' &&
            typeof RTCRtpTransceiver !== 'undefined') {
            logger.debug('detectDeviceImpl() | using ReactNative106 handler');
            return 'ReactNative106';
        }
        else {
            logger.warn('detectDeviceImpl() | unsupported react-native-webrtc version without RTCPeerConnection or RTCRtpTransceiver, forgot to call registerGlobals() on it?');
            return undefined;
        }
    }
    logger.warn('detectDeviceImpl() | device not supported [userAgent:"%s", userAgentData:%o]', userAgent, userAgentData);
    return undefined;
}
function getChromiumMajorVersion(userAgent, userAgentData) {
    logger.debug('getChromiumMajorVersion()');
    if (isIOS(userAgent, userAgentData)) {
        logger.debug('getChromiumMajorVersion() | this is iOS => undefined');
        return undefined;
    }
    if (isReactNative()) {
        logger.debug('getChromiumMajorVersion() | this is React-Native => undefined');
        return undefined;
    }
    if (userAgentData) {
        // Some nasty browser extensions define their own custom
        // `navigator.userAgentData`` without mandatory `brands` field (or with
        // `brands` with string value instead of array), so let's be ready for it.
        const brands = Array.isArray(userAgentData.brands)
            ? userAgentData.brands
            : [];
        const chromiumBrand = brands.find(b => b.brand === 'Chromium');
        if (chromiumBrand) {
            const majorVersion = Number(chromiumBrand.version);
            logger.debug(`getChromiumMajorVersion() | Chromium major version based on NavigatorUAData => ${majorVersion}`);
            return majorVersion;
        }
    }
    const match = userAgent?.match(/\b(?:Chrome|Chromium)\/(\w+)/i);
    if (match?.[1]) {
        const majorVersion = Number(match[1]);
        logger.debug(`getChromiumMajorVersion() | Chromium major version based on User-Agent => ${majorVersion}`);
        return majorVersion;
    }
    logger.debug('getChromiumMajorVersion() | this is not Chromium => undefined');
    return undefined;
}
function getFirefoxMajorVersion(userAgent) {
    logger.debug('getFirefoxMajorVersion()');
    if (isIOS(userAgent)) {
        logger.debug('getFirefoxMajorVersion() | this is iOS => undefined');
        return undefined;
    }
    if (isReactNative()) {
        logger.debug('getFirefoxMajorVersion() | this is React-Native => undefined');
        return undefined;
    }
    const match = userAgent?.match(/\bFirefox\/(\w+)/i);
    if (match?.[1]) {
        const majorVersion = Number(match[1]);
        logger.debug(`getFirefoxMajorVersion() | Firefox major version based on User-Agent => ${majorVersion}`);
        return majorVersion;
    }
    logger.debug('getFirefoxMajorVersion() | this is not Firefox => undefined');
    return undefined;
}
function getMacOSWebKitMajorVersion(userAgent) {
    logger.debug('getMacOSWebKitMajorVersion()');
    if (isIOS(userAgent)) {
        logger.debug('getMacOSWebKitMajorVersion() | this is iOS => undefined');
        return undefined;
    }
    if (isReactNative()) {
        logger.debug('getMacOSWebKitMajorVersion() | this is React-Native => undefined');
        return undefined;
    }
    const isSafari = userAgent &&
        /\bSafari\b/i.test(userAgent) &&
        !/\bChrome\b/i.test(userAgent) &&
        !/\bChromium\b/i.test(userAgent) &&
        !/\bFirefox\b/i.test(userAgent);
    if (!isSafari) {
        logger.debug('getMacOSWebKitMajorVersion() | this is not Safari => undefined');
        return undefined;
    }
    const match = userAgent.match(/AppleWebKit\/(\w+)/i);
    if (match?.[1]) {
        const majorVersion = Number(match[1]);
        logger.debug(`getMacOSWebKitMajorVersion() | WebKit major version based on User-Agent => ${majorVersion}`);
        return majorVersion;
    }
    logger.debug('getMacOSWebKitMajorVersion() | this is not WebKit => undefined');
    return undefined;
}
function getIOSWebKitMajorVersion(userAgent) {
    logger.debug('getIOSWebKitMajorVersion()');
    if (!isIOS(userAgent)) {
        logger.debug('getIOSWebKitMajorVersion() | this is not iOS => undefined');
        return undefined;
    }
    if (isReactNative()) {
        logger.debug('getIOSWebKitMajorVersion() | this is React-Native => undefined');
        return undefined;
    }
    const match = userAgent?.match(/AppleWebKit\/(\w+)/i);
    if (match?.[1]) {
        const majorVersion = Number(match[1]);
        logger.debug(`getIOSWebKitMajorVersion() | WebKit major version based on User-Agent => ${majorVersion}`);
        return majorVersion;
    }
    logger.debug('getIOSWebKitMajorVersion() | this is not WebKit => undefined');
    return undefined;
}
function isIOS(userAgent, userAgentData) {
    logger.debug('isIOS()');
    if (userAgentData?.platform === 'iOS') {
        logger.debug('isIOS() | this is iOS based on NavigatorUAData.platform => true');
        return true;
    }
    if (userAgentData?.platform) {
        logger.debug('isIOS() | this is not iOS based on NavigatorUAData.platform => false');
        return false;
    }
    if (userAgent && /iPad|iPhone|iPod/.test(userAgent)) {
        logger.debug('isIOS() | this is iOS based on User-Agent => true');
        return true;
    }
    // iPadOS 13+ identifies itself as Mac (to force desktop view mode in some
    // websites) but we know it's iOS if it has touch screen.
    if (typeof navigator === 'object' &&
        navigator.platform === 'MacIntel' &&
        navigator.maxTouchPoints > 1) {
        logger.debug('isIOS() | this is iPadOS 13+ based on User-Agent => true');
        return true;
    }
    logger.debug('isIOS() | this is not iOS => false');
    return false;
}
function isReactNative() {
    logger.debug('isReactNative()');
    if (typeof navigator === 'object' && navigator.product === 'ReactNative') {
        logger.debug('isReactNative() | this is React-Native based on navigator.product');
        return true;
    }
    logger.debug('isReactNative() | this is not React-Native => false');
    return false;
}

},
5: function(module, exports, __req) {
"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.parse = parse;
const ScalabilityModeRegex = new RegExp('^[LS]([1-9]\\d{0,1})T([1-9]\\d{0,1})');
function parse(scalabilityMode) {
    const match = ScalabilityModeRegex.exec(scalabilityMode ?? '');
    if (match) {
        return {
            spatialLayers: Number(match[1]),
            temporalLayers: Number(match[2]),
        };
    }
    else {
        return {
            spatialLayers: 1,
            temporalLayers: 1,
        };
    }
}

},
6: function(module, exports, __req) {
"use strict";
var __createBinding = (this && this.__createBinding) || (Object.create ? (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    var desc = Object.getOwnPropertyDescriptor(m, k);
    if (!desc || ("get" in desc ? !m.__esModule : desc.writable || desc.configurable)) {
      desc = { enumerable: true, get: function() { return m[k]; } };
    }
    Object.defineProperty(o, k2, desc);
}) : (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    o[k2] = m[k];
}));
var __setModuleDefault = (this && this.__setModuleDefault) || (Object.create ? (function(o, v) {
    Object.defineProperty(o, "default", { enumerable: true, value: v });
}) : function(o, v) {
    o["default"] = v;
});
var __importStar = (this && this.__importStar) || (function () {
    var ownKeys = function(o) {
        ownKeys = Object.getOwnPropertyNames || function (o) {
            var ar = [];
            for (var k in o) if (Object.prototype.hasOwnProperty.call(o, k)) ar[ar.length] = k;
            return ar;
        };
        return ownKeys(o);
    };
    return function (mod) {
        if (mod && mod.__esModule) return mod;
        var result = {};
        if (mod != null) for (var k = ownKeys(mod), i = 0; i < k.length; i++) if (k[i] !== "default") __createBinding(result, mod, k[i]);
        __setModuleDefault(result, mod);
        return result;
    };
})();
Object.defineProperty(exports, "__esModule", { value: true });
exports.validateAndNormalizeRtpCapabilities = validateAndNormalizeRtpCapabilities;
exports.validateAndNormalizeRtpParameters = validateAndNormalizeRtpParameters;
exports.validateAndNormalizeSctpStreamParameters = validateAndNormalizeSctpStreamParameters;
exports.getExtendedRtpCapabilities = getExtendedRtpCapabilities;
exports.getRecvRtpCapabilities = getRecvRtpCapabilities;
exports.getSendRtpCapabilities = getSendRtpCapabilities;
exports.getSendingRtpParameters = getSendingRtpParameters;
exports.getSendingRemoteRtpParameters = getSendingRemoteRtpParameters;
exports.reduceCodecs = reduceCodecs;
exports.generateProbatorRtpParameters = generateProbatorRtpParameters;
exports.canSend = canSend;
exports.canReceive = canReceive;
const h264 = __importStar(__req(20));
const utils = __importStar(__req(13));
const RTP_PROBATOR_MID = 'probator';
const RTP_PROBATOR_SSRC = 1234;
const RTP_PROBATOR_CODEC_PAYLOAD_TYPE = 127;
/**
 * Validates RtpCapabilities. It may modify given data by adding missing
 * fields with default values.
 * It throws if invalid.
 */
function validateAndNormalizeRtpCapabilities(caps) {
    if (typeof caps !== 'object') {
        throw new TypeError('caps is not an object');
    }
    // codecs is optional. If unset, fill with an empty array.
    if (caps.codecs && !Array.isArray(caps.codecs)) {
        throw new TypeError('caps.codecs is not an array');
    }
    else if (!caps.codecs) {
        caps.codecs = [];
    }
    for (const codec of caps.codecs) {
        validateAndNormalizeRtpCodecCapability(codec);
    }
    // headerExtensions is optional. If unset, fill with an empty array.
    if (caps.headerExtensions && !Array.isArray(caps.headerExtensions)) {
        throw new TypeError('caps.headerExtensions is not an array');
    }
    else if (!caps.headerExtensions) {
        caps.headerExtensions = [];
    }
    for (const ext of caps.headerExtensions) {
        validateAndNormalizeRtpHeaderExtension(ext);
    }
}
/**
 * Validates RtpParameters. It may modify given data by adding missing
 * fields with default values.
 * It throws if invalid.
 */
function validateAndNormalizeRtpParameters(params) {
    if (typeof params !== 'object') {
        throw new TypeError('params is not an object');
    }
    // mid is optional.
    if (params.mid && typeof params.mid !== 'string') {
        throw new TypeError('params.mid is not a string');
    }
    // codecs is mandatory.
    if (!Array.isArray(params.codecs)) {
        throw new TypeError('missing params.codecs');
    }
    for (const codec of params.codecs) {
        validateAndNormalizeRtpCodecParameters(codec);
    }
    // headerExtensions is optional. If unset, fill with an empty array.
    if (params.headerExtensions && !Array.isArray(params.headerExtensions)) {
        throw new TypeError('params.headerExtensions is not an array');
    }
    else if (!params.headerExtensions) {
        params.headerExtensions = [];
    }
    for (const ext of params.headerExtensions) {
        validateRtpHeaderExtensionParameters(ext);
    }
    // encodings is optional. If unset, fill with an empty array.
    if (params.encodings && !Array.isArray(params.encodings)) {
        throw new TypeError('params.encodings is not an array');
    }
    else if (!params.encodings) {
        params.encodings = [];
    }
    for (const encoding of params.encodings) {
        validateAndNormalizeRtpEncodingParameters(encoding);
    }
    // rtcp is optional. If unset, fill with an empty object.
    if (params.rtcp && typeof params.rtcp !== 'object') {
        throw new TypeError('params.rtcp is not an object');
    }
    else if (!params.rtcp) {
        params.rtcp = {};
    }
    validateAndNormalizeRtcpParameters(params.rtcp);
}
/**
 * Validates SctpStreamParameters. It may modify given data by adding missing
 * fields with default values.
 * It throws if invalid.
 */
function validateAndNormalizeSctpStreamParameters(params) {
    if (typeof params !== 'object') {
        throw new TypeError('params is not an object');
    }
    // streamId is mandatory.
    if (typeof params.streamId !== 'number') {
        throw new TypeError('missing params.streamId');
    }
    // ordered is optional.
    let orderedGiven = false;
    if (typeof params.ordered === 'boolean') {
        orderedGiven = true;
    }
    else {
        params.ordered = true;
    }
    // maxPacketLifeTime is optional.
    if (params.maxPacketLifeTime !== undefined &&
        typeof params.maxPacketLifeTime !== 'number') {
        throw new TypeError('invalid params.maxPacketLifeTime');
    }
    // maxRetransmits is optional.
    if (params.maxRetransmits !== undefined &&
        typeof params.maxRetransmits !== 'number') {
        throw new TypeError('invalid params.maxRetransmits');
    }
    if (params.maxPacketLifeTime !== undefined &&
        params.maxRetransmits !== undefined) {
        throw new TypeError('cannot provide both maxPacketLifeTime and maxRetransmits');
    }
    if (orderedGiven &&
        params.ordered &&
        (params.maxPacketLifeTime !== undefined ||
            params.maxRetransmits !== undefined)) {
        throw new TypeError('cannot be ordered with maxPacketLifeTime or maxRetransmits');
    }
    else if (!orderedGiven &&
        (params.maxPacketLifeTime !== undefined ||
            params.maxRetransmits !== undefined)) {
        params.ordered = false;
    }
    // label is optional.
    if (params.label && typeof params.label !== 'string') {
        throw new TypeError('invalid params.label');
    }
    // protocol is optional.
    if (params.protocol && typeof params.protocol !== 'string') {
        throw new TypeError('invalid params.protocol');
    }
}
/**
 * Generate extended RTP capabilities for sending and receiving.
 *
 * Resulting codecs keep order preferred by local or remote capabilities
 * depending on `preferLocalCodecsOrder`.
 */
function getExtendedRtpCapabilities(localCaps, remoteCaps, preferLocalCodecsOrder) {
    const extendedRtpCapabilities = {
        codecs: [],
        headerExtensions: [],
    };
    // Match media codecs and keep the order preferred by local capabilities.
    if (preferLocalCodecsOrder) {
        for (const localCodec of localCaps.codecs ?? []) {
            if (isRtxCodec(localCodec)) {
                continue;
            }
            const matchingRemoteCodec = (remoteCaps.codecs ?? []).find((remoteCodec) => matchCodecs(remoteCodec, localCodec, { strict: true, modify: true }));
            if (!matchingRemoteCodec) {
                continue;
            }
            const extendedCodec = {
                kind: localCodec.kind,
                mimeType: localCodec.mimeType,
                clockRate: localCodec.clockRate,
                channels: localCodec.channels,
                localPayloadType: localCodec.preferredPayloadType,
                localRtxPayloadType: undefined,
                remotePayloadType: matchingRemoteCodec.preferredPayloadType,
                remoteRtxPayloadType: undefined,
                localParameters: localCodec.parameters ?? {},
                remoteParameters: matchingRemoteCodec.parameters ?? {},
                rtcpFeedback: reduceRtcpFeedback(localCodec, matchingRemoteCodec),
            };
            extendedRtpCapabilities.codecs.push(extendedCodec);
        }
    }
    // Match media codecs and keep the order preferred by remote capabilities.
    else {
        for (const remoteCodec of remoteCaps.codecs ?? []) {
            if (isRtxCodec(remoteCodec)) {
                continue;
            }
            const matchingLocalCodec = (localCaps.codecs ?? []).find((localCodec) => matchCodecs(localCodec, remoteCodec, { strict: true, modify: true }));
            if (!matchingLocalCodec) {
                continue;
            }
            const extendedCodec = {
                kind: matchingLocalCodec.kind,
                mimeType: matchingLocalCodec.mimeType,
                clockRate: matchingLocalCodec.clockRate,
                channels: matchingLocalCodec.channels,
                localPayloadType: matchingLocalCodec.preferredPayloadType,
                localRtxPayloadType: undefined,
                remotePayloadType: remoteCodec.preferredPayloadType,
                remoteRtxPayloadType: undefined,
                localParameters: matchingLocalCodec.parameters ?? {},
                remoteParameters: remoteCodec.parameters ?? {},
                rtcpFeedback: reduceRtcpFeedback(matchingLocalCodec, remoteCodec),
            };
            extendedRtpCapabilities.codecs.push(extendedCodec);
        }
    }
    // Match RTX codecs.
    for (const extendedCodec of extendedRtpCapabilities.codecs) {
        const matchingLocalRtxCodec = localCaps.codecs.find((localCodec) => isRtxCodec(localCodec) &&
            localCodec.parameters?.['apt'] === extendedCodec.localPayloadType);
        const matchingRemoteRtxCodec = remoteCaps.codecs.find((remoteCodec) => isRtxCodec(remoteCodec) &&
            remoteCodec.parameters?.['apt'] === extendedCodec.remotePayloadType);
        if (matchingLocalRtxCodec && matchingRemoteRtxCodec) {
            extendedCodec.localRtxPayloadType =
                matchingLocalRtxCodec.preferredPayloadType;
            extendedCodec.remoteRtxPayloadType =
                matchingRemoteRtxCodec.preferredPayloadType;
        }
    }
    // Match header extensions.
    for (const remoteExt of remoteCaps.headerExtensions) {
        const matchingLocalExt = localCaps.headerExtensions.find((localExt) => matchHeaderExtensions(localExt, remoteExt));
        if (!matchingLocalExt) {
            continue;
        }
        const extendedExt = {
            kind: remoteExt.kind,
            uri: remoteExt.uri,
            sendId: matchingLocalExt.preferredId,
            recvId: remoteExt.preferredId,
            encrypt: matchingLocalExt.preferredEncrypt ?? false,
            direction: 'sendrecv',
        };
        switch (remoteExt.direction) {
            case 'sendrecv': {
                extendedExt.direction = 'sendrecv';
                break;
            }
            case 'recvonly': {
                extendedExt.direction = 'sendonly';
                break;
            }
            case 'sendonly': {
                extendedExt.direction = 'recvonly';
                break;
            }
            case 'inactive': {
                extendedExt.direction = 'inactive';
                break;
            }
        }
        extendedRtpCapabilities.headerExtensions.push(extendedExt);
    }
    return extendedRtpCapabilities;
}
/**
 * Generate RTP capabilities for receiving media based on the given extended
 * RTP capabilities.
 */
function getRecvRtpCapabilities(extendedRtpCapabilities) {
    return getRtpCapabilities({ direction: 'recvonly', extendedRtpCapabilities });
}
/**
 * Generate RTP capabilities for sending media based on the given extended
 * RTP capabilities.
 */
function getSendRtpCapabilities(extendedRtpCapabilities) {
    return getRtpCapabilities({ direction: 'sendonly', extendedRtpCapabilities });
}
/**
 * Generate RTP parameters of the given kind for sending media.
 * NOTE: mid, encodings and rtcp fields are left empty.
 */
function getSendingRtpParameters(kind, extendedRtpCapabilities) {
    const rtpParameters = {
        mid: undefined,
        codecs: [],
        headerExtensions: [],
        encodings: [],
        rtcp: {},
    };
    for (const extendedCodec of extendedRtpCapabilities.codecs) {
        if (extendedCodec.kind !== kind) {
            continue;
        }
        const codec = {
            mimeType: extendedCodec.mimeType,
            payloadType: extendedCodec.localPayloadType,
            clockRate: extendedCodec.clockRate,
            channels: extendedCodec.channels,
            parameters: extendedCodec.localParameters,
            rtcpFeedback: extendedCodec.rtcpFeedback,
        };
        rtpParameters.codecs.push(codec);
        // Add RTX codec.
        if (extendedCodec.localRtxPayloadType) {
            const rtxCodec = {
                mimeType: `${extendedCodec.kind}/rtx`,
                payloadType: extendedCodec.localRtxPayloadType,
                clockRate: extendedCodec.clockRate,
                parameters: {
                    apt: extendedCodec.localPayloadType,
                },
                rtcpFeedback: [],
            };
            rtpParameters.codecs.push(rtxCodec);
        }
    }
    for (const extendedExtension of extendedRtpCapabilities.headerExtensions) {
        // Ignore RTP extensions of a different kind and those not valid for sending.
        if ((extendedExtension.kind && extendedExtension.kind !== kind) ||
            (extendedExtension.direction !== 'sendrecv' &&
                extendedExtension.direction !== 'sendonly')) {
            continue;
        }
        const ext = {
            uri: extendedExtension.uri,
            id: extendedExtension.sendId,
            encrypt: extendedExtension.encrypt,
            parameters: {},
        };
        rtpParameters.headerExtensions.push(ext);
    }
    return rtpParameters;
}
/**
 * Generate RTP parameters of the given kind suitable for the remote SDP answer.
 */
function getSendingRemoteRtpParameters(kind, extendedRtpCapabilities) {
    const rtpParameters = {
        mid: undefined,
        codecs: [],
        headerExtensions: [],
        encodings: [],
        rtcp: {},
    };
    for (const extendedCodec of extendedRtpCapabilities.codecs) {
        if (extendedCodec.kind !== kind) {
            continue;
        }
        const codec = {
            mimeType: extendedCodec.mimeType,
            payloadType: extendedCodec.localPayloadType,
            clockRate: extendedCodec.clockRate,
            channels: extendedCodec.channels,
            parameters: extendedCodec.remoteParameters,
            rtcpFeedback: extendedCodec.rtcpFeedback,
        };
        rtpParameters.codecs.push(codec);
        // Add RTX codec.
        if (extendedCodec.localRtxPayloadType) {
            const rtxCodec = {
                mimeType: `${extendedCodec.kind}/rtx`,
                payloadType: extendedCodec.localRtxPayloadType,
                clockRate: extendedCodec.clockRate,
                parameters: {
                    apt: extendedCodec.localPayloadType,
                },
                rtcpFeedback: [],
            };
            rtpParameters.codecs.push(rtxCodec);
        }
    }
    for (const extendedExtension of extendedRtpCapabilities.headerExtensions) {
        // Ignore RTP extensions of a different kind and those not valid for sending.
        if ((extendedExtension.kind && extendedExtension.kind !== kind) ||
            (extendedExtension.direction !== 'sendrecv' &&
                extendedExtension.direction !== 'sendonly')) {
            continue;
        }
        const ext = {
            uri: extendedExtension.uri,
            id: extendedExtension.sendId,
            encrypt: extendedExtension.encrypt,
            parameters: {},
        };
        rtpParameters.headerExtensions.push(ext);
    }
    // Reduce codecs' RTCP feedback. Use Transport-CC if available, REMB otherwise.
    if (rtpParameters.headerExtensions.some(ext => ext.uri ===
        'http://www.ietf.org/id/draft-holmer-rmcat-transport-wide-cc-extensions-01')) {
        for (const codec of rtpParameters.codecs) {
            codec.rtcpFeedback = (codec.rtcpFeedback ?? []).filter((fb) => fb.type !== 'goog-remb');
        }
    }
    else if (rtpParameters.headerExtensions.some(ext => ext.uri === 'http://www.webrtc.org/experiments/rtp-hdrext/abs-send-time')) {
        for (const codec of rtpParameters.codecs) {
            codec.rtcpFeedback = (codec.rtcpFeedback ?? []).filter(fb => fb.type !== 'transport-cc');
        }
    }
    else {
        for (const codec of rtpParameters.codecs) {
            codec.rtcpFeedback = (codec.rtcpFeedback ?? []).filter((fb) => fb.type !== 'transport-cc' && fb.type !== 'goog-remb');
        }
    }
    return rtpParameters;
}
/**
 * Reduce given codecs by returning an array of codecs "compatible" with the
 * given capability codec. If no capability codec is given, take the first
 * one(s).
 *
 * Given codecs must be generated by ortc.getSendingRtpParameters() or
 * ortc.getSendingRemoteRtpParameters().
 *
 * The returned array of codecs also include a RTX codec if available.
 */
function reduceCodecs(codecs, capCodec) {
    const filteredCodecs = [];
    // If no capability codec is given, take the first one (and RTX).
    if (!capCodec) {
        filteredCodecs.push(codecs[0]);
        if (isRtxCodec(codecs[1])) {
            filteredCodecs.push(codecs[1]);
        }
    }
    // Otherwise look for a compatible set of codecs.
    else {
        for (let idx = 0; idx < codecs.length; ++idx) {
            if (matchCodecs(codecs[idx], capCodec, { strict: true })) {
                filteredCodecs.push(codecs[idx]);
                if (isRtxCodec(codecs[idx + 1])) {
                    filteredCodecs.push(codecs[idx + 1]);
                }
                break;
            }
        }
        if (filteredCodecs.length === 0) {
            throw new TypeError('no matching codec found');
        }
    }
    return filteredCodecs;
}
/**
 * Create RTP parameters for a Consumer for the RTP probator.
 */
function generateProbatorRtpParameters(videoRtpParameters) {
    // Clone given reference video RTP parameters.
    videoRtpParameters = utils.clone(videoRtpParameters);
    // This may throw.
    validateAndNormalizeRtpParameters(videoRtpParameters);
    const rtpParameters = {
        mid: RTP_PROBATOR_MID,
        codecs: [],
        headerExtensions: [],
        encodings: [{ ssrc: RTP_PROBATOR_SSRC }],
        rtcp: { cname: 'probator' },
    };
    rtpParameters.codecs.push(videoRtpParameters.codecs[0]);
    rtpParameters.codecs[0].payloadType = RTP_PROBATOR_CODEC_PAYLOAD_TYPE;
    rtpParameters.headerExtensions = videoRtpParameters.headerExtensions;
    return rtpParameters;
}
/**
 * Whether media can be sent based on the given RTP capabilities.
 */
function canSend(kind, rtpCapabilities) {
    return (rtpCapabilities.codecs ?? []).some(codec => codec.kind === kind);
}
/**
 * Whether the given RTP parameters can be received with the given RTP
 * capabilities.
 */
function canReceive(rtpParameters, rtpCapabilities) {
    // This may throw.
    validateAndNormalizeRtpParameters(rtpParameters);
    if (rtpParameters.codecs.length === 0) {
        return false;
    }
    const firstMediaCodec = rtpParameters.codecs[0];
    return (rtpCapabilities.codecs ?? []).some(codec => codec.preferredPayloadType === firstMediaCodec.payloadType);
}
/**
 * Validates RtpCodecCapability. It may modify given data by adding missing
 * fields with default values.
 * It throws if invalid.
 */
function validateAndNormalizeRtpCodecCapability(codec) {
    const MimeTypeRegex = new RegExp('^(audio|video)/(.+)', 'i');
    if (typeof codec !== 'object') {
        throw new TypeError('codec is not an object');
    }
    // mimeType is mandatory.
    if (!codec.mimeType || typeof codec.mimeType !== 'string') {
        throw new TypeError('missing codec.mimeType');
    }
    const mimeTypeMatch = MimeTypeRegex.exec(codec.mimeType);
    if (!mimeTypeMatch) {
        throw new TypeError('invalid codec.mimeType');
    }
    // Just override kind with media component of mimeType.
    codec.kind = mimeTypeMatch[1].toLowerCase();
    // preferredPayloadType is mandatory.
    if (typeof codec.preferredPayloadType !== 'number') {
        throw new TypeError('missing codec.preferredPayloadType');
    }
    // clockRate is mandatory.
    if (typeof codec.clockRate !== 'number') {
        throw new TypeError('missing codec.clockRate');
    }
    // channels is optional. If unset, set it to 1 (just if audio).
    if (codec.kind === 'audio') {
        if (typeof codec.channels !== 'number') {
            codec.channels = 1;
        }
    }
    else {
        delete codec.channels;
    }
    // parameters is optional. If unset, set it to an empty object.
    if (!codec.parameters || typeof codec.parameters !== 'object') {
        codec.parameters = {};
    }
    for (const key of Object.keys(codec.parameters)) {
        let value = codec.parameters[key];
        if (value === undefined) {
            codec.parameters[key] = '';
            value = '';
        }
        if (typeof value !== 'string' && typeof value !== 'number') {
            throw new TypeError(`invalid codec parameter [key:${key}s, value:${value}]`);
        }
        // Specific parameters validation.
        if (key === 'apt') {
            if (typeof value !== 'number') {
                throw new TypeError('invalid codec apt parameter');
            }
        }
    }
    // rtcpFeedback is optional. If unset, set it to an empty array.
    if (!codec.rtcpFeedback || !Array.isArray(codec.rtcpFeedback)) {
        codec.rtcpFeedback = [];
    }
    for (const fb of codec.rtcpFeedback) {
        validateAndNormalizeRtcpFeedback(fb);
    }
}
/**
 * Validates RtcpFeedback. It may modify given data by adding missing
 * fields with default values.
 * It throws if invalid.
 */
function validateAndNormalizeRtcpFeedback(fb) {
    if (typeof fb !== 'object') {
        throw new TypeError('fb is not an object');
    }
    // type is mandatory.
    if (!fb.type || typeof fb.type !== 'string') {
        throw new TypeError('missing fb.type');
    }
    // parameter is optional. If unset set it to an empty string.
    if (!fb.parameter || typeof fb.parameter !== 'string') {
        fb.parameter = '';
    }
}
/**
 * Validates RtpHeaderExtension. It may modify given data by adding missing
 * fields with default values.
 * It throws if invalid.
 */
function validateAndNormalizeRtpHeaderExtension(ext) {
    if (typeof ext !== 'object') {
        throw new TypeError('ext is not an object');
    }
    // kind is mandatory.
    if (ext.kind !== 'audio' && ext.kind !== 'video') {
        throw new TypeError('invalid ext.kind');
    }
    // uri is mandatory.
    if (!ext.uri || typeof ext.uri !== 'string') {
        throw new TypeError('missing ext.uri');
    }
    // preferredId is mandatory.
    if (typeof ext.preferredId !== 'number') {
        throw new TypeError('missing ext.preferredId');
    }
    // preferredEncrypt is optional. If unset set it to false.
    if (ext.preferredEncrypt && typeof ext.preferredEncrypt !== 'boolean') {
        throw new TypeError('invalid ext.preferredEncrypt');
    }
    else if (!ext.preferredEncrypt) {
        ext.preferredEncrypt = false;
    }
    // direction is optional. If unset set it to sendrecv.
    if (ext.direction && typeof ext.direction !== 'string') {
        throw new TypeError('invalid ext.direction');
    }
    else if (!ext.direction) {
        ext.direction = 'sendrecv';
    }
}
/**
 * Validates RtpCodecParameters. It may modify given data by adding missing
 * fields with default values.
 * It throws if invalid.
 */
function validateAndNormalizeRtpCodecParameters(codec) {
    const MimeTypeRegex = new RegExp('^(audio|video)/(.+)', 'i');
    if (typeof codec !== 'object') {
        throw new TypeError('codec is not an object');
    }
    // mimeType is mandatory.
    if (!codec.mimeType || typeof codec.mimeType !== 'string') {
        throw new TypeError('missing codec.mimeType');
    }
    const mimeTypeMatch = MimeTypeRegex.exec(codec.mimeType);
    if (!mimeTypeMatch) {
        throw new TypeError('invalid codec.mimeType');
    }
    // payloadType is mandatory.
    if (typeof codec.payloadType !== 'number') {
        throw new TypeError('missing codec.payloadType');
    }
    // clockRate is mandatory.
    if (typeof codec.clockRate !== 'number') {
        throw new TypeError('missing codec.clockRate');
    }
    const kind = mimeTypeMatch[1].toLowerCase();
    // channels is optional. If unset, set it to 1 (just if audio).
    if (kind === 'audio') {
        if (typeof codec.channels !== 'number') {
            codec.channels = 1;
        }
    }
    else {
        delete codec.channels;
    }
    // parameters is optional. If unset, set it to an empty object.
    if (!codec.parameters || typeof codec.parameters !== 'object') {
        codec.parameters = {};
    }
    for (const key of Object.keys(codec.parameters)) {
        let value = codec.parameters[key];
        if (value === undefined) {
            codec.parameters[key] = '';
            value = '';
        }
        if (typeof value !== 'string' && typeof value !== 'number') {
            throw new TypeError(`invalid codec parameter [key:${key}s, value:${value}]`);
        }
        // Specific parameters validation.
        if (key === 'apt') {
            if (typeof value !== 'number') {
                throw new TypeError('invalid codec apt parameter');
            }
        }
    }
    // rtcpFeedback is optional. If unset, set it to an empty array.
    if (!codec.rtcpFeedback || !Array.isArray(codec.rtcpFeedback)) {
        codec.rtcpFeedback = [];
    }
    for (const fb of codec.rtcpFeedback) {
        validateAndNormalizeRtcpFeedback(fb);
    }
}
/**
 * Validates RtpHeaderExtensionParameteters. It may modify given data by adding missing
 * fields with default values.
 * It throws if invalid.
 */
function validateRtpHeaderExtensionParameters(ext) {
    if (typeof ext !== 'object') {
        throw new TypeError('ext is not an object');
    }
    // uri is mandatory.
    if (!ext.uri || typeof ext.uri !== 'string') {
        throw new TypeError('missing ext.uri');
    }
    // id is mandatory.
    if (typeof ext.id !== 'number') {
        throw new TypeError('missing ext.id');
    }
    // encrypt is optional. If unset set it to false.
    if (ext.encrypt && typeof ext.encrypt !== 'boolean') {
        throw new TypeError('invalid ext.encrypt');
    }
    else if (!ext.encrypt) {
        ext.encrypt = false;
    }
    // parameters is optional. If unset, set it to an empty object.
    if (!ext.parameters || typeof ext.parameters !== 'object') {
        ext.parameters = {};
    }
    for (const key of Object.keys(ext.parameters)) {
        let value = ext.parameters[key];
        if (value === undefined) {
            ext.parameters[key] = '';
            value = '';
        }
        if (typeof value !== 'string' && typeof value !== 'number') {
            throw new TypeError('invalid header extension parameter');
        }
    }
}
/**
 * Validates RtpEncodingParameters. It may modify given data by adding missing
 * fields with default values.
 * It throws if invalid.
 */
function validateAndNormalizeRtpEncodingParameters(encoding) {
    if (typeof encoding !== 'object') {
        throw new TypeError('encoding is not an object');
    }
    // ssrc is optional.
    if (encoding.ssrc && typeof encoding.ssrc !== 'number') {
        throw new TypeError('invalid encoding.ssrc');
    }
    // rid is optional.
    if (encoding.rid && typeof encoding.rid !== 'string') {
        throw new TypeError('invalid encoding.rid');
    }
    // rtx is optional.
    if (encoding.rtx && typeof encoding.rtx !== 'object') {
        throw new TypeError('invalid encoding.rtx');
    }
    else if (encoding.rtx) {
        // RTX ssrc is mandatory if rtx is present.
        if (typeof encoding.rtx.ssrc !== 'number') {
            throw new TypeError('missing encoding.rtx.ssrc');
        }
    }
    // dtx is optional. If unset set it to false.
    if (!encoding.dtx || typeof encoding.dtx !== 'boolean') {
        encoding.dtx = false;
    }
    // scalabilityMode is optional.
    if (encoding.scalabilityMode &&
        typeof encoding.scalabilityMode !== 'string') {
        throw new TypeError('invalid encoding.scalabilityMode');
    }
}
/**
 * Validates RtcpParameters. It may modify given data by adding missing
 * fields with default values.
 * It throws if invalid.
 */
function validateAndNormalizeRtcpParameters(rtcp) {
    if (typeof rtcp !== 'object') {
        throw new TypeError('rtcp is not an object');
    }
    // cname is optional.
    if (rtcp.cname && typeof rtcp.cname !== 'string') {
        throw new TypeError('invalid rtcp.cname');
    }
    // reducedSize is optional. If unset set it to true.
    if (!rtcp.reducedSize || typeof rtcp.reducedSize !== 'boolean') {
        rtcp.reducedSize = true;
    }
}
/**
 * Generate RTP capabilities for sending or receiving media based on the given
 * extended RTP capabilities.
 */
function getRtpCapabilities({ direction, extendedRtpCapabilities, }) {
    const isSending = direction === 'sendonly';
    const rtpCapabilities = {
        codecs: [],
        headerExtensions: [],
    };
    for (const extendedCodec of extendedRtpCapabilities.codecs) {
        const codec = {
            kind: extendedCodec.kind,
            mimeType: extendedCodec.mimeType,
            preferredPayloadType: isSending
                ? extendedCodec.localPayloadType
                : extendedCodec.remotePayloadType,
            clockRate: extendedCodec.clockRate,
            channels: extendedCodec.channels,
            parameters: extendedCodec.localParameters,
            rtcpFeedback: extendedCodec.rtcpFeedback,
        };
        rtpCapabilities.codecs.push(codec);
        // Add RTX codec.
        if (!extendedCodec.localRtxPayloadType ||
            !extendedCodec.remoteRtxPayloadType) {
            continue;
        }
        const rtxCodec = {
            kind: extendedCodec.kind,
            mimeType: `${extendedCodec.kind}/rtx`,
            preferredPayloadType: isSending
                ? extendedCodec.localRtxPayloadType
                : extendedCodec.remoteRtxPayloadType,
            clockRate: extendedCodec.clockRate,
            parameters: {
                apt: isSending
                    ? extendedCodec.localPayloadType
                    : extendedCodec.remotePayloadType,
            },
            rtcpFeedback: [],
        };
        rtpCapabilities.codecs.push(rtxCodec);
        // TODO: In the future, we need to add FEC, CN, etc, codecs.
    }
    for (const extendedExtension of extendedRtpCapabilities.headerExtensions) {
        // Ignore RTP extensions not valid for the given direction.
        if (extendedExtension.direction !== 'sendrecv' &&
            extendedExtension.direction !== direction) {
            continue;
        }
        const ext = {
            kind: extendedExtension.kind,
            uri: extendedExtension.uri,
            preferredId: isSending
                ? extendedExtension.sendId
                : extendedExtension.recvId,
            preferredEncrypt: extendedExtension.encrypt ?? false,
            direction: extendedExtension.direction,
        };
        rtpCapabilities.headerExtensions.push(ext);
    }
    return rtpCapabilities;
}
function isRtxCodec(codec) {
    if (!codec) {
        return false;
    }
    return /.+\/rtx$/i.test(codec.mimeType);
}
function matchCodecs(aCodec, bCodec, { strict = false, modify = false } = {}) {
    const aMimeType = aCodec.mimeType.toLowerCase();
    const bMimeType = bCodec.mimeType.toLowerCase();
    if (aMimeType !== bMimeType) {
        return false;
    }
    if (aCodec.clockRate !== bCodec.clockRate) {
        return false;
    }
    if (aCodec.channels !== bCodec.channels) {
        return false;
    }
    // Per codec special checks.
    switch (aMimeType) {
        case 'video/h264': {
            if (strict) {
                const aPacketizationMode = aCodec.parameters['packetization-mode'] ?? 0;
                const bPacketizationMode = bCodec.parameters['packetization-mode'] ?? 0;
                if (aPacketizationMode !== bPacketizationMode) {
                    return false;
                }
                if (!h264.isSameProfile(aCodec.parameters, bCodec.parameters)) {
                    return false;
                }
                let selectedProfileLevelId;
                try {
                    selectedProfileLevelId = h264.generateProfileLevelIdStringForAnswer(aCodec.parameters, bCodec.parameters);
                }
                catch (error) {
                    return false;
                }
                if (modify) {
                    if (selectedProfileLevelId) {
                        aCodec.parameters['profile-level-id'] = selectedProfileLevelId;
                        bCodec.parameters['profile-level-id'] = selectedProfileLevelId;
                    }
                    else {
                        delete aCodec.parameters['profile-level-id'];
                        delete bCodec.parameters['profile-level-id'];
                    }
                }
            }
            break;
        }
        case 'video/vp9': {
            if (strict) {
                const aProfileId = aCodec.parameters['profile-id'] ?? 0;
                const bProfileId = bCodec.parameters['profile-id'] ?? 0;
                if (aProfileId !== bProfileId) {
                    return false;
                }
            }
            break;
        }
    }
    return true;
}
function matchHeaderExtensions(aExt, bExt) {
    if (aExt.kind && bExt.kind && aExt.kind !== bExt.kind) {
        return false;
    }
    if (aExt.uri !== bExt.uri) {
        return false;
    }
    return true;
}
function reduceRtcpFeedback(codecA, codecB) {
    const reducedRtcpFeedback = [];
    for (const aFb of codecA.rtcpFeedback ?? []) {
        const matchingBFb = (codecB.rtcpFeedback ?? []).find((bFb) => bFb.type === aFb.type &&
            (bFb.parameter === aFb.parameter || (!bFb.parameter && !aFb.parameter)));
        if (matchingBFb) {
            reducedRtcpFeedback.push(matchingBFb);
        }
    }
    return reducedRtcpFeedback;
}

},
7: function(module, exports, __req) {
"use strict";
/* eslint-disable @typescript-eslint/no-explicit-any */
Object.defineProperty(exports, "__esModule", { value: true });
exports.EnhancedEventEmitter = void 0;
const events_alias_1 = __req(21);
const Logger_1 = __req(11);
const enhancedEventEmitterLogger = new Logger_1.Logger('EnhancedEventEmitter');
class EnhancedEventEmitter extends events_alias_1.EventEmitter {
    constructor() {
        super();
        this.setMaxListeners(Infinity);
    }
    /**
     * Empties all stored event listeners.
     */
    close() {
        super.removeAllListeners();
    }
    emit(eventName, ...args) {
        return super.emit(eventName, ...args);
    }
    /**
     * Special addition to the EventEmitter API.
     */
    safeEmit(eventName, ...args) {
        try {
            return super.emit(eventName, ...args);
        }
        catch (error) {
            enhancedEventEmitterLogger.error('safeEmit() | event listener threw an error [eventName:%s]:%o', eventName, error);
            try {
                super.emit('listenererror', eventName, error);
            }
            catch (error2) {
                // Ignore it.
            }
            return Boolean(super.listenerCount(eventName));
        }
    }
    on(eventName, listener) {
        super.on(eventName, listener);
        return this;
    }
    off(eventName, listener) {
        super.off(eventName, listener);
        return this;
    }
    addListener(eventName, listener) {
        super.on(eventName, listener);
        return this;
    }
    prependListener(eventName, listener) {
        super.prependListener(eventName, listener);
        return this;
    }
    once(eventName, listener) {
        super.once(eventName, listener);
        return this;
    }
    prependOnceListener(eventName, listener) {
        super.prependOnceListener(eventName, listener);
        return this;
    }
    removeListener(eventName, listener) {
        super.off(eventName, listener);
        return this;
    }
    removeAllListeners(eventName) {
        super.removeAllListeners(eventName);
        return this;
    }
    listenerCount(eventName) {
        return super.listenerCount(eventName);
    }
    listeners(eventName) {
        return super.listeners(eventName);
    }
    rawListeners(eventName) {
        return super.rawListeners(eventName);
    }
}
exports.EnhancedEventEmitter = EnhancedEventEmitter;

},
8: function(module, exports, __req) {
"use strict";
var __createBinding = (this && this.__createBinding) || (Object.create ? (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    var desc = Object.getOwnPropertyDescriptor(m, k);
    if (!desc || ("get" in desc ? !m.__esModule : desc.writable || desc.configurable)) {
      desc = { enumerable: true, get: function() { return m[k]; } };
    }
    Object.defineProperty(o, k2, desc);
}) : (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    o[k2] = m[k];
}));
var __setModuleDefault = (this && this.__setModuleDefault) || (Object.create ? (function(o, v) {
    Object.defineProperty(o, "default", { enumerable: true, value: v });
}) : function(o, v) {
    o["default"] = v;
});
var __importStar = (this && this.__importStar) || (function () {
    var ownKeys = function(o) {
        ownKeys = Object.getOwnPropertyNames || function (o) {
            var ar = [];
            for (var k in o) if (Object.prototype.hasOwnProperty.call(o, k)) ar[ar.length] = k;
            return ar;
        };
        return ownKeys(o);
    };
    return function (mod) {
        if (mod && mod.__esModule) return mod;
        var result = {};
        if (mod != null) for (var k = ownKeys(mod), i = 0; i < k.length; i++) if (k[i] !== "default") __createBinding(result, mod, k[i]);
        __setModuleDefault(result, mod);
        return result;
    };
})();
Object.defineProperty(exports, "__esModule", { value: true });
exports.FakeHandler = void 0;
const fake_mediastreamtrack_1 = __req(22);
const enhancedEvents_1 = __req(7);
const Logger_1 = __req(11);
const utils = __importStar(__req(13));
const ortc = __importStar(__req(6));
const errors_1 = __req(12);
const FakeEventTarget_1 = __req(23);
const logger = new Logger_1.Logger('FakeHandler');
const NAME = 'FakeHandler';
class FakeHandler extends enhancedEvents_1.EnhancedEventEmitter {
    // Closed flag.
    _closed = false;
    // Fake parameters source of RTP and SCTP parameters and capabilities.
    _fakeParameters;
    // Callback to request sending extended RTP capabilities on demand.
    _getSendExtendedRtpCapabilities;
    // Local RTCP CNAME.
    _cname = `CNAME-${utils.generateRandomNumber()}`;
    // Default sending MediaStream id.
    _defaultSendStreamId = `${utils.generateRandomNumber()}`;
    // Got transport local and remote parameters.
    _transportReady = false;
    // Next localId.
    _nextLocalId = 1;
    // Sending and receiving tracks indexed by localId.
    _tracks = new Map();
    // DataChannel id value counter. It must be incremented for each new DataChannel.
    _nextSctpStreamId = 0;
    /**
     * Creates a factory function.
     */
    static createFactory(fakeParameters) {
        return {
            name: NAME,
            factory: (options) => new FakeHandler(options, fakeParameters),
            getNativeRtpCapabilities: async ({ direction, }) => {
                logger.debug('getNativeRtpCapabilities() [direction:%o]', direction);
                return FakeHandler.getLocalRtpCapabilities(fakeParameters);
            },
        };
    }
    static getLocalRtpCapabilities(fakeParameters) {
        const nativeRtpCapabilities = fakeParameters.generateNativeRtpCapabilities();
        // Need to validate and normalize native RTP capabilities.
        ortc.validateAndNormalizeRtpCapabilities(nativeRtpCapabilities);
        return nativeRtpCapabilities;
    }
    constructor({ 
    // direction,
    // iceParameters,
    // iceCandidates,
    // dtlsParameters,
    // sctpParameters,
    // iceServers,
    // iceTransportPolicy,
    // additionalSettings,
    getSendExtendedRtpCapabilities, }, fakeParameters) {
        super();
        logger.debug('constructor()');
        this._getSendExtendedRtpCapabilities = getSendExtendedRtpCapabilities;
        this._fakeParameters = fakeParameters;
    }
    get name() {
        return NAME;
    }
    close() {
        logger.debug('close()');
        if (this._closed) {
            return;
        }
        this._closed = true;
        // Invoke close() in EnhancedEventEmitter classes.
        super.close();
    }
    // NOTE: Custom method for simulation purposes.
    setIceGatheringState(iceGatheringState) {
        this.emit('@icegatheringstatechange', iceGatheringState);
    }
    // NOTE: Custom method for simulation purposes.
    setConnectionState(connectionState) {
        this.emit('@connectionstatechange', connectionState);
    }
    // eslint-disable-next-line @typescript-eslint/no-unused-vars
    async updateIceServers(iceServers) {
        this.assertNotClosed();
        logger.debug('updateIceServers()');
    }
    // eslint-disable-next-line @typescript-eslint/no-unused-vars
    async restartIce(iceParameters) {
        this.assertNotClosed();
        logger.debug('restartIce()');
    }
    async getTransportStats() {
        this.assertNotClosed();
        return new Map(); // NOTE: Whatever.
    }
    async send(
    // eslint-disable-next-line @typescript-eslint/no-unused-vars
    { track, streamId, encodings, codecOptions, codec }) {
        this.assertNotClosed();
        logger.debug('send() [kind:%s, track.id:%s]', track.kind, track.id);
        if (!this._transportReady) {
            await this.setupTransport({ localDtlsRole: 'server' });
        }
        const nativeRtpCapabilities = FakeHandler.getLocalRtpCapabilities(this._fakeParameters);
        const sendExtendedRtpCapabilities = this._getSendExtendedRtpCapabilities(nativeRtpCapabilities);
        // Generic sending RTP parameters.
        const sendingRtpParameters = ortc.getSendingRtpParameters(track.kind, sendExtendedRtpCapabilities);
        // This may throw.
        sendingRtpParameters.codecs = ortc.reduceCodecs(sendingRtpParameters.codecs, codec);
        const useRtx = sendingRtpParameters.codecs.some(_codec => /.+\/rtx$/i.test(_codec.mimeType));
        sendingRtpParameters.mid = `mid-${utils.generateRandomNumber()}`;
        sendingRtpParameters.msid = `${streamId ?? '-'} ${track.id}`;
        if (!encodings) {
            encodings = [{}];
        }
        for (const encoding of encodings) {
            encoding.ssrc = utils.generateRandomNumber();
            if (useRtx) {
                encoding.rtx = { ssrc: utils.generateRandomNumber() };
            }
        }
        sendingRtpParameters.encodings = encodings;
        // Fill RTCRtpParameters.rtcp.
        sendingRtpParameters.rtcp = {
            cname: this._cname,
            reducedSize: true,
            mux: true,
        };
        // Set msid.
        sendingRtpParameters.msid = `${streamId ?? this._defaultSendStreamId} ${track.id}`;
        const localId = this._nextLocalId++;
        this._tracks.set(localId, track);
        return { localId: String(localId), rtpParameters: sendingRtpParameters };
    }
    async stopSending(localId) {
        logger.debug('stopSending() [localId:%s]', localId);
        if (this._closed) {
            return;
        }
        if (!this._tracks.has(Number(localId))) {
            throw new Error('local track not found');
        }
        this._tracks.delete(Number(localId));
    }
    // eslint-disable-next-line @typescript-eslint/no-unused-vars
    async pauseSending(localId) {
        this.assertNotClosed();
        // Unimplemented.
    }
    // eslint-disable-next-line @typescript-eslint/no-unused-vars
    async resumeSending(localId) {
        this.assertNotClosed();
        // Unimplemented.
    }
    async replaceTrack(localId, track) {
        this.assertNotClosed();
        if (track) {
            logger.debug('replaceTrack() [localId:%s, track.id:%s]', localId, track.id);
        }
        else {
            logger.debug('replaceTrack() [localId:%s, no track]', localId);
        }
        this._tracks.delete(Number(localId));
        this._tracks.set(Number(localId), track);
    }
    async setMaxSpatialLayer(localId, spatialLayer) {
        this.assertNotClosed();
        logger.debug('setMaxSpatialLayer() [localId:%s, spatialLayer:%s]', localId, spatialLayer);
    }
    async setRtpEncodingParameters(localId, params) {
        this.assertNotClosed();
        logger.debug('setRtpEncodingParameters() [localId:%s, params:%o]', localId, params);
    }
    // eslint-disable-next-line @typescript-eslint/no-unused-vars
    async getSenderStats(localId) {
        this.assertNotClosed();
        return new Map(); // NOTE: Whatever.
    }
    async sendDataChannel({ sctpStreamParameters, }) {
        this.assertNotClosed();
        if (!this._transportReady) {
            await this.setupTransport({ localDtlsRole: 'server' });
        }
        logger.debug('sendDataChannel()');
        const dataChannel = new FakeRTCDataChannel({
            id: this._nextSctpStreamId++,
            ordered: sctpStreamParameters.ordered,
            maxPacketLifeTime: sctpStreamParameters.maxPacketLifeTime,
            maxRetransmits: sctpStreamParameters.maxRetransmits,
            label: sctpStreamParameters.label,
            protocol: sctpStreamParameters.protocol,
        });
        const newSctpStreamParameters = {
            streamId: this._nextSctpStreamId,
            ordered: sctpStreamParameters.ordered,
            maxPacketLifeTime: sctpStreamParameters.maxPacketLifeTime,
            maxRetransmits: sctpStreamParameters.maxRetransmits,
        };
        return { dataChannel, sctpStreamParameters: newSctpStreamParameters };
    }
    async receive(optionsList) {
        this.assertNotClosed();
        const results = [];
        for (const options of optionsList) {
            const { trackId, kind } = options;
            if (!this._transportReady) {
                await this.setupTransport({ localDtlsRole: 'client' });
            }
            logger.debug('receive() [trackId:%s, kind:%s]', trackId, kind);
            const localId = this._nextLocalId++;
            const track = new fake_mediastreamtrack_1.FakeMediaStreamTrack({ kind });
            this._tracks.set(localId, track);
            results.push({ localId: String(localId), track });
        }
        return results;
    }
    async stopReceiving(localIds) {
        if (this._closed) {
            return;
        }
        for (const localId of localIds) {
            logger.debug('stopReceiving() [localId:%s]', localId);
            this._tracks.delete(Number(localId));
        }
    }
    async pauseReceiving(
    // eslint-disable-next-line @typescript-eslint/no-unused-vars
    localIds) {
        this.assertNotClosed();
        // Unimplemented.
    }
    async resumeReceiving(
    // eslint-disable-next-line @typescript-eslint/no-unused-vars
    localIds) {
        this.assertNotClosed();
        // Unimplemented.
    }
    // eslint-disable-next-line @typescript-eslint/no-unused-vars
    async getReceiverStats(localId) {
        this.assertNotClosed();
        return new Map(); //
    }
    async receiveDataChannel({ 
    // maxMessageSize,
    sctpStreamParameters, label, protocol, }) {
        this.assertNotClosed();
        if (!this._transportReady) {
            await this.setupTransport({ localDtlsRole: 'client' });
        }
        logger.debug('receiveDataChannel()');
        const dataChannel = new FakeRTCDataChannel({
            id: sctpStreamParameters.streamId,
            ordered: sctpStreamParameters.ordered,
            maxPacketLifeTime: sctpStreamParameters.maxPacketLifeTime,
            maxRetransmits: sctpStreamParameters.maxRetransmits,
            label,
            protocol,
        });
        return { dataChannel };
    }
    getDataChannelMaxMessageSize() {
        return 500000;
    }
    async setupTransport({ localDtlsRole, 
    // eslint-disable-next-line @typescript-eslint/no-unused-vars
    localSdpObject, }) {
        const dtlsParameters = utils.clone(this._fakeParameters.generateLocalDtlsParameters());
        // Set our DTLS role.
        if (localDtlsRole) {
            dtlsParameters.role = localDtlsRole;
        }
        // Assume we are connecting now.
        this.emit('@connectionstatechange', 'connecting');
        // Need to tell the remote transport about our parameters.
        await new Promise((resolve, reject) => this.emit('@connect', { dtlsParameters }, resolve, reject));
        this._transportReady = true;
    }
    assertNotClosed() {
        if (this._closed) {
            throw new errors_1.InvalidStateError('method called in a closed handler');
        }
    }
}
exports.FakeHandler = FakeHandler;
/**
 * @remarks
 * - We use a custom FakeEventTarget class because Hermes JS engine in
 *   React-Native doesn't implement EventListener.
 */
class FakeRTCDataChannel extends FakeEventTarget_1.FakeEventTarget {
    // Members for RTCDataChannel standard public getters/setters.
    _id;
    _negotiated = true; // mediasoup just uses negotiated DataChannels.
    _ordered;
    _maxPacketLifeTime;
    _maxRetransmits;
    _label;
    _protocol;
    _readyState = 'connecting';
    _bufferedAmount = 0;
    _bufferedAmountLowThreshold = 0;
    _binaryType = 'arraybuffer';
    // Events.
    _onopen = null;
    _onclosing = null;
    _onclose = null;
    _onmessage = null;
    _onbufferedamountlow = null;
    _onerror = null;
    constructor({ id, ordered = true, maxPacketLifeTime = null, maxRetransmits = null, label = '', protocol = '', }) {
        super();
        logger.debug(`constructor() [id:${id}, ordered:${ordered}, maxPacketLifeTime:${maxPacketLifeTime}, maxRetransmits:${maxRetransmits}, label:${label}, protocol:${protocol}`);
        this._id = id;
        this._ordered = ordered;
        this._maxPacketLifeTime = maxPacketLifeTime;
        this._maxRetransmits = maxRetransmits;
        this._label = label;
        this._protocol = protocol;
    }
    get id() {
        return this._id;
    }
    get negotiated() {
        return this._negotiated;
    }
    get ordered() {
        return this._ordered;
    }
    get maxPacketLifeTime() {
        return this._maxPacketLifeTime;
    }
    get maxRetransmits() {
        return this._maxRetransmits;
    }
    get label() {
        return this._label;
    }
    get protocol() {
        return this._protocol;
    }
    get readyState() {
        return this._readyState;
    }
    get bufferedAmount() {
        return this._bufferedAmount;
    }
    get bufferedAmountLowThreshold() {
        return this._bufferedAmountLowThreshold;
    }
    set bufferedAmountLowThreshold(value) {
        this._bufferedAmountLowThreshold = value;
    }
    get binaryType() {
        return this._binaryType;
    }
    set binaryType(binaryType) {
        this._binaryType = binaryType;
    }
    get onopen() {
        return this._onopen;
    }
    set onopen(handler) {
        if (this._onopen) {
            this.removeEventListener('open', this._onopen);
        }
        this._onopen = handler;
        if (handler) {
            this.addEventListener('open', handler);
        }
    }
    get onclosing() {
        return this._onclosing;
    }
    set onclosing(handler) {
        if (this._onclosing) {
            this.removeEventListener('closing', this._onclosing);
        }
        this._onclosing = handler;
        if (handler) {
            this.addEventListener('closing', handler);
        }
    }
    get onclose() {
        return this._onclose;
    }
    set onclose(handler) {
        if (this._onclose) {
            this.removeEventListener('close', this._onclose);
        }
        this._onclose = handler;
        if (handler) {
            this.addEventListener('close', handler);
        }
    }
    get onmessage() {
        return this._onmessage;
    }
    set onmessage(handler) {
        if (this._onmessage) {
            this.removeEventListener('message', this._onmessage);
        }
        this._onmessage = handler;
        if (handler) {
            this.addEventListener('message', handler);
        }
    }
    get onbufferedamountlow() {
        return this._onbufferedamountlow;
    }
    set onbufferedamountlow(handler) {
        if (this._onbufferedamountlow) {
            this.removeEventListener('bufferedamountlow', this._onbufferedamountlow);
        }
        this._onbufferedamountlow = handler;
        if (handler) {
            this.addEventListener('bufferedamountlow', handler);
        }
    }
    get onerror() {
        return this._onerror;
    }
    set onerror(handler) {
        if (this._onerror) {
            this.removeEventListener('error', this._onerror);
        }
        this._onerror = handler;
        if (handler) {
            this.addEventListener('error', handler);
        }
    }
    addEventListener(type, listener, options) {
        super.addEventListener(type, listener, options);
    }
    removeEventListener(type, listener, options) {
        super.removeEventListener(type, listener, options);
    }
    close() {
        if (['closing', 'closed'].includes(this._readyState)) {
            return;
        }
        this._readyState = 'closed';
    }
    /**
     * We extend the definition of send() to allow Node Buffer. However
     * ArrayBufferView and Blob do not exist in Node.
     */
    // eslint-disable-next-line @typescript-eslint/no-unused-vars
    send(data) {
        if (this._readyState !== 'open') {
            throw new errors_1.InvalidStateError('not open');
        }
    }
}

},
9: function(module, exports, __req) {
"use strict";
var __createBinding = (this && this.__createBinding) || (Object.create ? (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    var desc = Object.getOwnPropertyDescriptor(m, k);
    if (!desc || ("get" in desc ? !m.__esModule : desc.writable || desc.configurable)) {
      desc = { enumerable: true, get: function() { return m[k]; } };
    }
    Object.defineProperty(o, k2, desc);
}) : (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    o[k2] = m[k];
}));
var __setModuleDefault = (this && this.__setModuleDefault) || (Object.create ? (function(o, v) {
    Object.defineProperty(o, "default", { enumerable: true, value: v });
}) : function(o, v) {
    o["default"] = v;
});
var __importStar = (this && this.__importStar) || (function () {
    var ownKeys = function(o) {
        ownKeys = Object.getOwnPropertyNames || function (o) {
            var ar = [];
            for (var k in o) if (Object.prototype.hasOwnProperty.call(o, k)) ar[ar.length] = k;
            return ar;
        };
        return ownKeys(o);
    };
    return function (mod) {
        if (mod && mod.__esModule) return mod;
        var result = {};
        if (mod != null) for (var k = ownKeys(mod), i = 0; i < k.length; i++) if (k[i] !== "default") __createBinding(result, mod, k[i]);
        __setModuleDefault(result, mod);
        return result;
    };
})();
Object.defineProperty(exports, "__esModule", { value: true });
exports.generateRouterRtpCapabilities = generateRouterRtpCapabilities;
exports.generateNativeRtpCapabilities = generateNativeRtpCapabilities;
exports.generateLocalDtlsParameters = generateLocalDtlsParameters;
exports.generateTransportRemoteParameters = generateTransportRemoteParameters;
exports.generateProducerRemoteParameters = generateProducerRemoteParameters;
exports.generateConsumerRemoteParameters = generateConsumerRemoteParameters;
exports.generateDataProducerRemoteParameters = generateDataProducerRemoteParameters;
exports.generateDataConsumerRemoteParameters = generateDataConsumerRemoteParameters;
const utils = __importStar(__req(13));
function generateFakeUuid() {
    return String(utils.generateRandomNumber());
}
function generateRouterRtpCapabilities() {
    return utils.deepFreeze({
        codecs: [
            {
                mimeType: 'audio/opus',
                kind: 'audio',
                preferredPayloadType: 100,
                clockRate: 48000,
                channels: 2,
                rtcpFeedback: [{ type: 'transport-cc' }],
                parameters: {
                    useinbandfec: 1,
                    foo: 'bar',
                },
            },
            {
                mimeType: 'video/VP8',
                kind: 'video',
                preferredPayloadType: 101,
                clockRate: 90000,
                rtcpFeedback: [
                    { type: 'nack' },
                    { type: 'nack', parameter: 'pli' },
                    { type: 'ccm', parameter: 'fir' },
                    { type: 'goog-remb' },
                    { type: 'transport-cc' },
                ],
                parameters: {
                    'x-google-start-bitrate': 1500,
                },
            },
            {
                mimeType: 'video/rtx',
                kind: 'video',
                preferredPayloadType: 102,
                clockRate: 90000,
                rtcpFeedback: [],
                parameters: {
                    apt: 101,
                },
            },
            {
                mimeType: 'video/H264',
                kind: 'video',
                preferredPayloadType: 103,
                clockRate: 90000,
                rtcpFeedback: [
                    { type: 'nack' },
                    { type: 'nack', parameter: 'pli' },
                    { type: 'ccm', parameter: 'fir' },
                    { type: 'goog-remb' },
                    { type: 'transport-cc' },
                ],
                parameters: {
                    'level-asymmetry-allowed': 1,
                    'packetization-mode': 1,
                    'profile-level-id': '42e01f',
                },
            },
            {
                mimeType: 'video/rtx',
                kind: 'video',
                preferredPayloadType: 104,
                clockRate: 90000,
                rtcpFeedback: [],
                parameters: {
                    apt: 103,
                },
            },
            {
                mimeType: 'video/VP9',
                kind: 'video',
                preferredPayloadType: 105,
                clockRate: 90000,
                rtcpFeedback: [
                    { type: 'nack' },
                    { type: 'nack', parameter: 'pli' },
                    { type: 'ccm', parameter: 'fir' },
                    { type: 'goog-remb' },
                    { type: 'transport-cc' },
                ],
                parameters: {
                    'profile-id': 0,
                    'x-google-start-bitrate': 1500,
                },
            },
            {
                mimeType: 'video/rtx',
                kind: 'video',
                preferredPayloadType: 106,
                clockRate: 90000,
                rtcpFeedback: [],
                parameters: {
                    apt: 105,
                },
            },
        ],
        headerExtensions: [
            {
                kind: 'audio',
                uri: 'urn:ietf:params:rtp-hdrext:sdes:mid',
                preferredId: 1,
                preferredEncrypt: false,
                direction: 'sendrecv',
            },
            {
                kind: 'video',
                uri: 'urn:ietf:params:rtp-hdrext:sdes:mid',
                preferredId: 1,
                preferredEncrypt: false,
                direction: 'sendrecv',
            },
            {
                kind: 'video',
                uri: 'urn:ietf:params:rtp-hdrext:sdes:rtp-stream-id',
                preferredId: 2,
                preferredEncrypt: false,
                direction: 'recvonly',
            },
            {
                kind: 'video',
                uri: 'urn:ietf:params:rtp-hdrext:sdes:repaired-rtp-stream-id',
                preferredId: 3,
                preferredEncrypt: false,
                direction: 'recvonly',
            },
            {
                kind: 'audio',
                uri: 'http://www.webrtc.org/experiments/rtp-hdrext/abs-send-time',
                preferredId: 4,
                preferredEncrypt: false,
                direction: 'sendrecv',
            },
            {
                kind: 'video',
                uri: 'http://www.webrtc.org/experiments/rtp-hdrext/abs-send-time',
                preferredId: 4,
                preferredEncrypt: false,
                direction: 'sendrecv',
            },
            {
                kind: 'audio',
                uri: 'http://www.ietf.org/id/draft-holmer-rmcat-transport-wide-cc-extensions-01',
                preferredId: 5,
                preferredEncrypt: false,
                direction: 'recvonly',
            },
            {
                kind: 'video',
                uri: 'http://www.ietf.org/id/draft-holmer-rmcat-transport-wide-cc-extensions-01',
                preferredId: 5,
                preferredEncrypt: false,
                direction: 'sendrecv',
            },
            {
                kind: 'audio',
                uri: 'urn:ietf:params:rtp-hdrext:ssrc-audio-level',
                preferredId: 10,
                preferredEncrypt: false,
                direction: 'sendrecv',
            },
            {
                kind: 'video',
                uri: 'urn:3gpp:video-orientation',
                preferredId: 11,
                preferredEncrypt: false,
                direction: 'sendrecv',
            },
        ],
    });
}
// NOTE: We don't freeze these RTP capabilities because we do need to normalize
// them as we do in real browser handlers (this is an object supposed to be
// generated internally so it's ok).
function generateNativeRtpCapabilities() {
    return {
        codecs: [
            {
                mimeType: 'audio/opus',
                kind: 'audio',
                preferredPayloadType: 111,
                clockRate: 48000,
                channels: 2,
                rtcpFeedback: [{ type: 'transport-cc' }],
                parameters: {
                    minptime: 10,
                    useinbandfec: 1,
                },
            },
            {
                mimeType: 'audio/ISAC',
                kind: 'audio',
                preferredPayloadType: 103,
                clockRate: 16000,
                channels: 1,
                rtcpFeedback: [{ type: 'transport-cc' }],
                parameters: {},
            },
            {
                mimeType: 'audio/CN',
                kind: 'audio',
                preferredPayloadType: 106,
                clockRate: 32000,
                channels: 1,
                rtcpFeedback: [{ type: 'transport-cc' }],
                parameters: {},
            },
            {
                mimeType: 'audio/foo',
                kind: 'audio',
                preferredPayloadType: 107,
                clockRate: 90000,
                channels: 4,
                rtcpFeedback: [{ type: 'foo-qwe-qwe' }],
                parameters: {
                    foo: 'lalala',
                },
            },
            {
                mimeType: 'video/BAZCODEC',
                kind: 'video',
                preferredPayloadType: 100,
                clockRate: 90000,
                rtcpFeedback: [
                    { type: 'foo' },
                    { type: 'transport-cc' },
                    { type: 'ccm', parameter: 'fir' },
                    { type: 'nack' },
                    { type: 'nack', parameter: 'pli' },
                ],
                parameters: {
                    baz: '1234abcd',
                },
            },
            {
                mimeType: 'video/rtx',
                kind: 'video',
                preferredPayloadType: 101,
                clockRate: 90000,
                rtcpFeedback: [],
                parameters: {
                    apt: 100,
                },
            },
            {
                mimeType: 'video/VP8',
                kind: 'video',
                preferredPayloadType: 96,
                clockRate: 90000,
                rtcpFeedback: [
                    { type: 'goog-remb' },
                    { type: 'transport-cc' },
                    { type: 'ccm', parameter: 'fir' },
                    { type: 'nack' },
                    { type: 'nack', parameter: 'pli' },
                ],
                parameters: {
                    baz: '1234abcd',
                },
            },
            {
                mimeType: 'video/rtx',
                kind: 'video',
                preferredPayloadType: 97,
                clockRate: 90000,
                rtcpFeedback: [],
                parameters: {
                    apt: 96,
                },
            },
            {
                mimeType: 'video/VP9',
                kind: 'video',
                preferredPayloadType: 98,
                clockRate: 90000,
                rtcpFeedback: [
                    { type: 'goog-remb' },
                    { type: 'transport-cc' },
                    { type: 'ccm', parameter: 'fir' },
                    { type: 'nack' },
                    { type: 'nack', parameter: 'pli' },
                ],
                parameters: {
                    'profile-id': 0,
                },
            },
            {
                mimeType: 'video/rtx',
                kind: 'video',
                preferredPayloadType: 99,
                clockRate: 90000,
                rtcpFeedback: [],
                parameters: {
                    apt: 98,
                },
            },
        ],
        headerExtensions: [
            {
                kind: 'audio',
                uri: 'urn:ietf:params:rtp-hdrext:sdes:mid',
                preferredId: 1,
            },
            {
                kind: 'video',
                uri: 'urn:ietf:params:rtp-hdrext:sdes:mid',
                preferredId: 1,
            },
            {
                kind: 'video',
                uri: 'http://www.webrtc.org/experiments/rtp-hdrext/abs-send-time',
                preferredId: 3,
            },
            {
                kind: 'video',
                uri: 'urn:3gpp:video-orientation',
                preferredId: 4,
            },
            {
                kind: 'video',
                uri: 'http://www.ietf.org/id/draft-holmer-rmcat-transport-wide-cc-extensions-01',
                preferredId: 5,
            },
            {
                kind: 'video',
                uri: 'http://www.webrtc.org/experiments/rtp-hdrext/playout-delay',
                preferredId: 6,
            },
            {
                kind: 'video',
                // @ts-expect-error --- ON purpose.
                uri: 'http://www.webrtc.org/experiments/rtp-hdrext/video-content-type',
                preferredId: 7,
            },
            {
                kind: 'video',
                // @ts-expect-error --- ON purpose.
                uri: 'http://www.webrtc.org/experiments/rtp-hdrext/video-timing',
                preferredId: 8,
            },
            {
                kind: 'audio',
                uri: 'urn:ietf:params:rtp-hdrext:ssrc-audio-level',
                preferredId: 10,
            },
        ],
    };
}
function generateLocalDtlsParameters() {
    return utils.deepFreeze({
        fingerprints: [
            {
                algorithm: 'sha-256',
                value: '82:5A:68:3D:36:C3:0A:DE:AF:E7:32:43:D2:88:83:57:AC:2D:65:E5:80:C4:B6:FB:AF:1A:A0:21:9F:6D:0C:AD',
            },
        ],
        role: 'auto',
    });
}
function generateTransportRemoteParameters() {
    return {
        id: generateFakeUuid(),
        iceParameters: utils.deepFreeze({
            iceLite: true,
            password: 'yku5ej8nvfaor28lvtrabcx0wkrpkztz',
            usernameFragment: 'h3hk1iz6qqlnqlne',
        }),
        iceCandidates: utils.deepFreeze([
            {
                foundation: 'udpcandidate',
                address: '9.9.9.9',
                ip: '9.9.9.9',
                port: 40533,
                priority: 1078862079,
                protocol: 'udp',
                type: 'host',
                tcpType: 'passive',
            },
            {
                foundation: 'udpcandidate',
                address: '9.9.9.9',
                ip: '9:9:9:9:9:9',
                port: 41333,
                priority: 1078862089,
                protocol: 'udp',
                type: 'host',
                tcpType: 'passive',
            },
        ]),
        dtlsParameters: utils.deepFreeze({
            fingerprints: [
                {
                    algorithm: 'sha-256',
                    value: 'A9:F4:E0:D2:74:D3:0F:D9:CA:A5:2F:9F:7F:47:FA:F0:C4:72:DD:73:49:D0:3B:14:90:20:51:30:1B:90:8E:71',
                },
                {
                    algorithm: 'sha-384',
                    value: '03:D9:0B:87:13:98:F6:6D:BC:FC:92:2E:39:D4:E1:97:32:61:30:56:84:70:81:6E:D1:82:97:EA:D9:C1:21:0F:6B:C5:E7:7F:E1:97:0C:17:97:6E:CF:B3:EF:2E:74:B0',
                },
                {
                    algorithm: 'sha-512',
                    value: '84:27:A4:28:A4:73:AF:43:02:2A:44:68:FF:2F:29:5C:3B:11:9A:60:F4:A8:F0:F5:AC:A0:E3:49:3E:B1:34:53:A9:85:CE:51:9B:ED:87:5E:B8:F4:8E:3D:FA:20:51:B8:96:EE:DA:56:DC:2F:5C:62:79:15:23:E0:21:82:2B:2C',
                },
            ],
            role: 'auto',
        }),
        sctpParameters: utils.deepFreeze({
            port: 5000,
            maxSendMessageSize: 50000,
            maxReceiveMessageSize: 50000,
        }),
    };
}
function generateProducerRemoteParameters() {
    return utils.deepFreeze({
        id: generateFakeUuid(),
    });
}
function generateConsumerRemoteParameters({ id, codecMimeType, } = {}) {
    switch (codecMimeType) {
        case 'audio/opus': {
            return {
                id: id ?? generateFakeUuid(),
                producerId: generateFakeUuid(),
                kind: 'audio',
                rtpParameters: utils.deepFreeze({
                    codecs: [
                        {
                            mimeType: 'audio/opus',
                            payloadType: 100,
                            clockRate: 48000,
                            channels: 2,
                            rtcpFeedback: [{ type: 'transport-cc' }],
                            parameters: {
                                useinbandfec: 1,
                                foo: 'bar',
                            },
                        },
                    ],
                    encodings: [
                        {
                            ssrc: 46687003,
                        },
                    ],
                    headerExtensions: [
                        {
                            uri: 'urn:ietf:params:rtp-hdrext:sdes:mid',
                            id: 1,
                        },
                        {
                            uri: 'http://www.ietf.org/id/draft-holmer-rmcat-transport-wide-cc-extensions-01',
                            id: 5,
                        },
                        {
                            uri: 'urn:ietf:params:rtp-hdrext:ssrc-audio-level',
                            id: 10,
                        },
                    ],
                    rtcp: {
                        cname: 'wB4Ql4lrsxYLjzuN',
                        reducedSize: true,
                        mux: true,
                    },
                }),
            };
        }
        case 'audio/ISAC': {
            return {
                id: id ?? generateFakeUuid(),
                producerId: generateFakeUuid(),
                kind: 'audio',
                rtpParameters: utils.deepFreeze({
                    codecs: [
                        {
                            mimeType: 'audio/ISAC',
                            payloadType: 111,
                            clockRate: 16000,
                            channels: 1,
                            rtcpFeedback: [{ type: 'transport-cc' }],
                            parameters: {},
                        },
                    ],
                    encodings: [
                        {
                            ssrc: 46687004,
                        },
                    ],
                    headerExtensions: [
                        {
                            uri: 'urn:ietf:params:rtp-hdrext:sdes:mid',
                            id: 1,
                        },
                        {
                            uri: 'http://www.ietf.org/id/draft-holmer-rmcat-transport-wide-cc-extensions-01',
                            id: 5,
                        },
                    ],
                    rtcp: {
                        cname: 'wB4Ql4lrsxYLjzuN',
                        reducedSize: true,
                        mux: true,
                    },
                }),
            };
        }
        case 'video/VP8': {
            return {
                id: id ?? generateFakeUuid(),
                producerId: generateFakeUuid(),
                kind: 'video',
                rtpParameters: utils.deepFreeze({
                    codecs: [
                        {
                            mimeType: 'video/VP8',
                            payloadType: 101,
                            clockRate: 90000,
                            rtcpFeedback: [
                                { type: 'nack' },
                                { type: 'nack', parameter: 'pli' },
                                { type: 'ccm', parameter: 'fir' },
                                { type: 'goog-remb' },
                                { type: 'transport-cc' },
                            ],
                            parameters: {
                                'x-google-start-bitrate': 1500,
                            },
                        },
                        {
                            mimeType: 'video/rtx',
                            payloadType: 102,
                            clockRate: 90000,
                            rtcpFeedback: [],
                            parameters: {
                                apt: 101,
                            },
                        },
                    ],
                    encodings: [
                        {
                            ssrc: 99991111,
                            rtx: {
                                ssrc: 99991112,
                            },
                        },
                    ],
                    headerExtensions: [
                        {
                            uri: 'urn:ietf:params:rtp-hdrext:sdes:mid',
                            id: 1,
                        },
                        {
                            uri: 'http://www.webrtc.org/experiments/rtp-hdrext/abs-send-time',
                            id: 4,
                        },
                        {
                            uri: 'http://www.ietf.org/id/draft-holmer-rmcat-transport-wide-cc-extensions-01',
                            id: 5,
                        },
                        {
                            uri: 'urn:3gpp:video-orientation',
                            id: 11,
                        },
                    ],
                    rtcp: {
                        cname: 'wB4Ql4lrsxYLjzuN',
                        reducedSize: true,
                        mux: true,
                    },
                }),
            };
        }
        case 'video/H264': {
            return {
                id: id ?? generateFakeUuid(),
                producerId: generateFakeUuid(),
                kind: 'video',
                rtpParameters: utils.deepFreeze({
                    codecs: [
                        {
                            mimeType: 'video/H264',
                            payloadType: 103,
                            clockRate: 90000,
                            rtcpFeedback: [
                                { type: 'nack' },
                                { type: 'nack', parameter: 'pli' },
                                { type: 'ccm', parameter: 'fir' },
                                { type: 'goog-remb' },
                                { type: 'transport-cc' },
                            ],
                            parameters: {
                                'level-asymmetry-allowed': 1,
                                'packetization-mode': 1,
                                'profile-level-id': '42e01f',
                            },
                        },
                        {
                            mimeType: 'video/rtx',
                            payloadType: 104,
                            clockRate: 90000,
                            rtcpFeedback: [],
                            parameters: {
                                apt: 103,
                            },
                        },
                    ],
                    encodings: [
                        {
                            ssrc: 99991113,
                            rtx: {
                                ssrc: 99991114,
                            },
                        },
                    ],
                    headerExtensions: [
                        {
                            uri: 'urn:ietf:params:rtp-hdrext:sdes:mid',
                            id: 1,
                        },
                        {
                            uri: 'http://www.webrtc.org/experiments/rtp-hdrext/abs-send-time',
                            id: 4,
                        },
                        {
                            uri: 'http://www.ietf.org/id/draft-holmer-rmcat-transport-wide-cc-extensions-01',
                            id: 5,
                        },
                        {
                            uri: 'urn:3gpp:video-orientation',
                            id: 11,
                        },
                    ],
                    rtcp: {
                        cname: 'wB4Ql4lrsxYLjzuN',
                        reducedSize: true,
                        mux: true,
                    },
                }),
            };
        }
        default: {
            throw new TypeError(`unknown codecMimeType '${codecMimeType}'`);
        }
    }
}
function generateDataProducerRemoteParameters() {
    return utils.deepFreeze({
        id: generateFakeUuid(),
    });
}
function generateDataConsumerRemoteParameters({ id, } = {}) {
    return {
        id: id ?? generateFakeUuid(),
        dataProducerId: generateFakeUuid(),
        sctpStreamParameters: utils.deepFreeze({
            streamId: 666,
            maxPacketLifeTime: 5000,
            maxRetransmits: undefined,
        }),
    };
}

},
10: function(module, exports, __req) {

/**
 * This is the common logic for both the Node.js and web browser
 * implementations of `debug()`.
 */

function setup(env) {
	createDebug.debug = createDebug;
	createDebug.default = createDebug;
	createDebug.coerce = coerce;
	createDebug.disable = disable;
	createDebug.enable = enable;
	createDebug.enabled = enabled;
	createDebug.humanize = __req(24);
	createDebug.destroy = destroy;

	Object.keys(env).forEach(key => {
		createDebug[key] = env[key];
	});

	/**
	* The currently active debug mode names, and names to skip.
	*/

	createDebug.names = [];
	createDebug.skips = [];

	/**
	* Map of special "%n" handling functions, for the debug "format" argument.
	*
	* Valid key names are a single, lower or upper-case letter, i.e. "n" and "N".
	*/
	createDebug.formatters = {};

	/**
	* Selects a color for a debug namespace
	* @param {String} namespace The namespace string for the debug instance to be colored
	* @return {Number|String} An ANSI color code for the given namespace
	* @api private
	*/
	function selectColor(namespace) {
		let hash = 0;

		for (let i = 0; i < namespace.length; i++) {
			hash = ((hash << 5) - hash) + namespace.charCodeAt(i);
			hash |= 0; // Convert to 32bit integer
		}

		return createDebug.colors[Math.abs(hash) % createDebug.colors.length];
	}
	createDebug.selectColor = selectColor;

	/**
	* Create a debugger with the given `namespace`.
	*
	* @param {String} namespace
	* @return {Function}
	* @api public
	*/
	function createDebug(namespace) {
		let prevTime;
		let enableOverride = null;
		let namespacesCache;
		let enabledCache;

		function debug(...args) {
			// Disabled?
			if (!debug.enabled) {
				return;
			}

			const self = debug;

			// Set `diff` timestamp
			const curr = Number(new Date());
			const ms = curr - (prevTime || curr);
			self.diff = ms;
			self.prev = prevTime;
			self.curr = curr;
			prevTime = curr;

			args[0] = createDebug.coerce(args[0]);

			if (typeof args[0] !== 'string') {
				// Anything else let's inspect with %O
				args.unshift('%O');
			}

			// Apply any `formatters` transformations
			let index = 0;
			args[0] = args[0].replace(/%([a-zA-Z%])/g, (match, format) => {
				// If we encounter an escaped % then don't increase the array index
				if (match === '%%') {
					return '%';
				}
				index++;
				const formatter = createDebug.formatters[format];
				if (typeof formatter === 'function') {
					const val = args[index];
					match = formatter.call(self, val);

					// Now we need to remove `args[index]` since it's inlined in the `format`
					args.splice(index, 1);
					index--;
				}
				return match;
			});

			// Apply env-specific formatting (colors, etc.)
			createDebug.formatArgs.call(self, args);

			const logFn = self.log || createDebug.log;
			logFn.apply(self, args);
		}

		debug.namespace = namespace;
		debug.useColors = createDebug.useColors();
		debug.color = createDebug.selectColor(namespace);
		debug.extend = extend;
		debug.destroy = createDebug.destroy; // XXX Temporary. Will be removed in the next major release.

		Object.defineProperty(debug, 'enabled', {
			enumerable: true,
			configurable: false,
			get: () => {
				if (enableOverride !== null) {
					return enableOverride;
				}
				if (namespacesCache !== createDebug.namespaces) {
					namespacesCache = createDebug.namespaces;
					enabledCache = createDebug.enabled(namespace);
				}

				return enabledCache;
			},
			set: v => {
				enableOverride = v;
			}
		});

		// Env-specific initialization logic for debug instances
		if (typeof createDebug.init === 'function') {
			createDebug.init(debug);
		}

		return debug;
	}

	function extend(namespace, delimiter) {
		const newDebug = createDebug(this.namespace + (typeof delimiter === 'undefined' ? ':' : delimiter) + namespace);
		newDebug.log = this.log;
		return newDebug;
	}

	/**
	* Enables a debug mode by namespaces. This can include modes
	* separated by a colon and wildcards.
	*
	* @param {String} namespaces
	* @api public
	*/
	function enable(namespaces) {
		createDebug.save(namespaces);
		createDebug.namespaces = namespaces;

		createDebug.names = [];
		createDebug.skips = [];

		const split = (typeof namespaces === 'string' ? namespaces : '')
			.trim()
			.replace(/\s+/g, ',')
			.split(',')
			.filter(Boolean);

		for (const ns of split) {
			if (ns[0] === '-') {
				createDebug.skips.push(ns.slice(1));
			} else {
				createDebug.names.push(ns);
			}
		}
	}

	/**
	 * Checks if the given string matches a namespace template, honoring
	 * asterisks as wildcards.
	 *
	 * @param {String} search
	 * @param {String} template
	 * @return {Boolean}
	 */
	function matchesTemplate(search, template) {
		let searchIndex = 0;
		let templateIndex = 0;
		let starIndex = -1;
		let matchIndex = 0;

		while (searchIndex < search.length) {
			if (templateIndex < template.length && (template[templateIndex] === search[searchIndex] || template[templateIndex] === '*')) {
				// Match character or proceed with wildcard
				if (template[templateIndex] === '*') {
					starIndex = templateIndex;
					matchIndex = searchIndex;
					templateIndex++; // Skip the '*'
				} else {
					searchIndex++;
					templateIndex++;
				}
			} else if (starIndex !== -1) { // eslint-disable-line no-negated-condition
				// Backtrack to the last '*' and try to match more characters
				templateIndex = starIndex + 1;
				matchIndex++;
				searchIndex = matchIndex;
			} else {
				return false; // No match
			}
		}

		// Handle trailing '*' in template
		while (templateIndex < template.length && template[templateIndex] === '*') {
			templateIndex++;
		}

		return templateIndex === template.length;
	}

	/**
	* Disable debug output.
	*
	* @return {String} namespaces
	* @api public
	*/
	function disable() {
		const namespaces = [
			...createDebug.names,
			...createDebug.skips.map(namespace => '-' + namespace)
		].join(',');
		createDebug.enable('');
		return namespaces;
	}

	/**
	* Returns true if the given mode name is enabled, false otherwise.
	*
	* @param {String} name
	* @return {Boolean}
	* @api public
	*/
	function enabled(name) {
		for (const skip of createDebug.skips) {
			if (matchesTemplate(name, skip)) {
				return false;
			}
		}

		for (const ns of createDebug.names) {
			if (matchesTemplate(name, ns)) {
				return true;
			}
		}

		return false;
	}

	/**
	* Coerce `val`.
	*
	* @param {Mixed} val
	* @return {Mixed}
	* @api private
	*/
	function coerce(val) {
		if (val instanceof Error) {
			return val.stack || val.message;
		}
		return val;
	}

	/**
	* XXX DO NOT USE. This is a temporary stub function.
	* XXX It WILL be removed in the next major release.
	*/
	function destroy() {
		console.warn('Instance method `debug.destroy()` is deprecated and no longer does anything. It will be removed in the next major version of `debug`.');
	}

	createDebug.enable(createDebug.load());

	return createDebug;
}

module.exports = setup;

},
11: function(module, exports, __req) {
"use strict";
var __importDefault = (this && this.__importDefault) || function (mod) {
    return (mod && mod.__esModule) ? mod : { "default": mod };
};
Object.defineProperty(exports, "__esModule", { value: true });
exports.Logger = void 0;
const debug_1 = __importDefault(__req(2));
const APP_NAME = 'mediasoup-client';
class Logger {
    _debug;
    _warn;
    _error;
    constructor(prefix) {
        if (prefix) {
            this._debug = (0, debug_1.default)(`${APP_NAME}:${prefix}`);
            this._warn = (0, debug_1.default)(`${APP_NAME}:WARN:${prefix}`);
            this._error = (0, debug_1.default)(`${APP_NAME}:ERROR:${prefix}`);
        }
        else {
            this._debug = (0, debug_1.default)(APP_NAME);
            this._warn = (0, debug_1.default)(`${APP_NAME}:WARN`);
            this._error = (0, debug_1.default)(`${APP_NAME}:ERROR`);
        }
        /* eslint-disable no-console */
        this._debug.log = console.info.bind(console);
        this._warn.log = console.warn.bind(console);
        this._error.log = console.error.bind(console);
        /* eslint-enable no-console */
    }
    get debug() {
        return this._debug;
    }
    get warn() {
        return this._warn;
    }
    get error() {
        return this._error;
    }
}
exports.Logger = Logger;

},
12: function(module, exports, __req) {
"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.InvalidStateError = exports.UnsupportedError = void 0;
/**
 * Error indicating not support for something.
 */
class UnsupportedError extends Error {
    constructor(message) {
        super(message);
        this.name = 'UnsupportedError';
        if (Error.hasOwnProperty('captureStackTrace')) {
            Error.captureStackTrace(this, UnsupportedError);
        }
        else {
            this.stack = new Error(message).stack;
        }
    }
}
exports.UnsupportedError = UnsupportedError;
/**
 * Error produced when calling a method in an invalid state.
 */
class InvalidStateError extends Error {
    constructor(message) {
        super(message);
        this.name = 'InvalidStateError';
        if (Error.hasOwnProperty('captureStackTrace')) {
            // Just in V8.
            Error.captureStackTrace(this, InvalidStateError);
        }
        else {
            this.stack = new Error(message).stack;
        }
    }
}
exports.InvalidStateError = InvalidStateError;

},
13: function(module, exports, __req) {
"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.clone = clone;
exports.generateRandomNumber = generateRandomNumber;
exports.deepFreeze = deepFreeze;
/**
 * Clones the given value.
 */
function clone(value) {
    if (value === undefined) {
        return undefined;
    }
    else if (Number.isNaN(value)) {
        return NaN;
    }
    else if (typeof structuredClone === 'function') {
        // Available in Node >= 18.
        return structuredClone(value);
    }
    else {
        return JSON.parse(JSON.stringify(value));
    }
}
/**
 * Generates a random positive integer.
 */
function generateRandomNumber() {
    return Math.round(Math.random() * 10000000);
}
/**
 * Make an object or array recursively immutable.
 * https://developer.mozilla.org/en-US/docs/Web/JavaScript/Reference/Global_Objects/Object/freeze.
 */
function deepFreeze(data) {
    // Retrieve the property names defined on object.
    const propNames = Reflect.ownKeys(data);
    // Freeze properties before freezing self.
    for (const name of propNames) {
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
        const value = data[name];
        if ((value && typeof value === 'object') || typeof value === 'function') {
            deepFreeze(value);
        }
    }
    return Object.freeze(data);
}

},
14: function(module, exports, __req) {
"use strict";
var __createBinding = (this && this.__createBinding) || (Object.create ? (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    var desc = Object.getOwnPropertyDescriptor(m, k);
    if (!desc || ("get" in desc ? !m.__esModule : desc.writable || desc.configurable)) {
      desc = { enumerable: true, get: function() { return m[k]; } };
    }
    Object.defineProperty(o, k2, desc);
}) : (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    o[k2] = m[k];
}));
var __setModuleDefault = (this && this.__setModuleDefault) || (Object.create ? (function(o, v) {
    Object.defineProperty(o, "default", { enumerable: true, value: v });
}) : function(o, v) {
    o["default"] = v;
});
var __importStar = (this && this.__importStar) || (function () {
    var ownKeys = function(o) {
        ownKeys = Object.getOwnPropertyNames || function (o) {
            var ar = [];
            for (var k in o) if (Object.prototype.hasOwnProperty.call(o, k)) ar[ar.length] = k;
            return ar;
        };
        return ownKeys(o);
    };
    return function (mod) {
        if (mod && mod.__esModule) return mod;
        var result = {};
        if (mod != null) for (var k = ownKeys(mod), i = 0; i < k.length; i++) if (k[i] !== "default") __createBinding(result, mod, k[i]);
        __setModuleDefault(result, mod);
        return result;
    };
})();
Object.defineProperty(exports, "__esModule", { value: true });
exports.Transport = void 0;
const awaitqueue_1 = __req(25);
const Logger_1 = __req(11);
const enhancedEvents_1 = __req(7);
const errors_1 = __req(12);
const utils = __importStar(__req(13));
const ortc = __importStar(__req(6));
const Producer_1 = __req(26);
const Consumer_1 = __req(27);
const DataProducer_1 = __req(28);
const DataConsumer_1 = __req(29);
const logger = new Logger_1.Logger('Transport');
class ConsumerCreationTask {
    consumerOptions;
    promise;
    resolve;
    reject;
    constructor(consumerOptions) {
        this.consumerOptions = consumerOptions;
        this.promise = new Promise((resolve, reject) => {
            this.resolve = resolve;
            this.reject = reject;
        });
    }
}
class Transport extends enhancedEvents_1.EnhancedEventEmitter {
    // Id.
    _id;
    // Closed flag.
    _closed = false;
    // Direction.
    _direction;
    // Callback for sending Transports to request sending extended RTP capabilities
    // on demand.
    _getSendExtendedRtpCapabilities;
    // Recv RTP capabilities.
    _recvRtpCapabilities;
    // Whether we can produce audio/video based on computed extended RTP
    // capabilities.
    _canProduceByKind;
    // SCTP parameters of the mediasoup transport.
    _sctpParameters;
    // RTC handler isntance.
    _handler;
    // Transport ICE gathering state.
    _iceGatheringState = 'new';
    // Transport connection state.
    _connectionState = 'new';
    // App custom data.
    _appData;
    // Map of Producers indexed by id.
    _producers = new Map();
    // Map of Consumers indexed by id.
    _consumers = new Map();
    // Map of DataProducers indexed by id.
    _dataProducers = new Map();
    // Map of DataConsumers indexed by id.
    _dataConsumers = new Map();
    // Whether the Consumer for RTP probation has been created.
    _probatorConsumerCreated = false;
    // AwaitQueue instance to make async tasks happen sequentially.
    _awaitQueue = new awaitqueue_1.AwaitQueue();
    // Consumer creation tasks awaiting to be processed.
    _pendingConsumerTasks = [];
    // Consumer creation in progress flag.
    _consumerCreationInProgress = false;
    // Consumers pending to be paused.
    _pendingPauseConsumers = new Map();
    // Consumer pause in progress flag.
    _consumerPauseInProgress = false;
    // Consumers pending to be resumed.
    _pendingResumeConsumers = new Map();
    // Consumer resume in progress flag.
    _consumerResumeInProgress = false;
    // Consumers pending to be closed.
    _pendingCloseConsumers = new Map();
    // Consumer close in progress flag.
    _consumerCloseInProgress = false;
    // Observer instance.
    _observer = new enhancedEvents_1.EnhancedEventEmitter();
    constructor({ direction, id, iceParameters, iceCandidates, dtlsParameters, sctpParameters, iceServers, iceTransportPolicy, additionalSettings, appData, handlerFactory, getSendExtendedRtpCapabilities, recvRtpCapabilities, canProduceByKind, }) {
        super();
        logger.debug('constructor() [id:%s, direction:%s]', id, direction);
        this._id = id;
        this._direction = direction;
        this._getSendExtendedRtpCapabilities = getSendExtendedRtpCapabilities;
        this._recvRtpCapabilities = recvRtpCapabilities;
        this._canProduceByKind = canProduceByKind;
        // NOTE: Let's clone received SctpParameters and adapt them to be backwards
        // compatible with old mediasoup server versions.
        this._sctpParameters = utils.clone(sctpParameters);
        if (this._sctpParameters) {
            this._sctpParameters.maxSendMessageSize =
                this._sctpParameters.maxSendMessageSize ??
                    this._sctpParameters.maxMessageSize;
            this._sctpParameters.maxReceiveMessageSize =
                this._sctpParameters.maxReceiveMessageSize ??
                    this._sctpParameters.maxMessageSize;
        }
        // Clone and sanitize additionalSettings.
        const clonedAdditionalSettings = utils.clone(additionalSettings) ?? {};
        delete clonedAdditionalSettings.iceServers;
        delete clonedAdditionalSettings.iceTransportPolicy;
        delete clonedAdditionalSettings.bundlePolicy;
        delete clonedAdditionalSettings.rtcpMuxPolicy;
        this._handler = handlerFactory.factory({
            direction,
            iceParameters,
            iceCandidates,
            dtlsParameters,
            sctpParameters: this._sctpParameters,
            iceServers,
            iceTransportPolicy,
            additionalSettings: clonedAdditionalSettings,
            getSendExtendedRtpCapabilities: this._getSendExtendedRtpCapabilities,
        });
        this._appData = appData ?? {};
        this.handleHandler();
    }
    /**
     * Transport id.
     */
    get id() {
        return this._id;
    }
    /**
     * Whether the Transport is closed.
     */
    get closed() {
        return this._closed;
    }
    /**
     * Transport direction.
     */
    get direction() {
        return this._direction;
    }
    /**
     * RTC handler instance.
     */
    get handler() {
        return this._handler;
    }
    /**
     * ICE gathering state.
     */
    get iceGatheringState() {
        return this._iceGatheringState;
    }
    /**
     * Connection state.
     */
    get connectionState() {
        return this._connectionState;
    }
    /**
     * App custom data.
     */
    get appData() {
        return this._appData;
    }
    /**
     * App custom data setter.
     */
    set appData(appData) {
        this._appData = appData;
    }
    get observer() {
        return this._observer;
    }
    /**
     * Close the Transport.
     */
    close() {
        if (this._closed) {
            return;
        }
        logger.debug('close()');
        this._closed = true;
        for (const task of this._pendingConsumerTasks) {
            task.reject(new errors_1.InvalidStateError('closed'));
        }
        this._pendingConsumerTasks = [];
        // Stop the AwaitQueue.
        this._awaitQueue.stop();
        // Close the handler.
        this._handler.close();
        // Change connection state to 'closed' since the handler may not emit
        // '@connectionstatechange' event.
        this._connectionState = 'closed';
        // Close all Producers.
        for (const producer of this._producers.values()) {
            producer.transportClosed();
        }
        this._producers.clear();
        // Close all Consumers.
        for (const consumer of this._consumers.values()) {
            consumer.transportClosed();
        }
        this._consumers.clear();
        // Close all DataProducers.
        for (const dataProducer of this._dataProducers.values()) {
            dataProducer.transportClosed();
        }
        this._dataProducers.clear();
        // Close all DataConsumers.
        for (const dataConsumer of this._dataConsumers.values()) {
            dataConsumer.transportClosed();
        }
        this._dataConsumers.clear();
        // Emit observer event.
        this._observer.safeEmit('close');
        // Invoke close() in EnhancedEventEmitter classes.
        super.close();
        this._observer.close();
    }
    /**
     * Get associated Transport (RTCPeerConnection) stats.
     *
     * @returns {RTCStatsReport}
     */
    async getStats() {
        if (this._closed) {
            throw new errors_1.InvalidStateError('closed');
        }
        return this._handler.getTransportStats();
    }
    /**
     * Restart ICE connection.
     */
    async restartIce({ iceParameters, }) {
        logger.debug('restartIce()');
        if (this._closed) {
            throw new errors_1.InvalidStateError('closed');
        }
        else if (!iceParameters) {
            throw new TypeError('missing iceParameters');
        }
        // Enqueue command.
        await this._awaitQueue.push(async () => {
            await this._handler.restartIce(iceParameters);
        }, 'transport.restartIce()');
    }
    /**
     * Update ICE servers.
     */
    async updateIceServers({ iceServers, } = {}) {
        logger.debug('updateIceServers()');
        if (this._closed) {
            throw new errors_1.InvalidStateError('closed');
        }
        else if (!Array.isArray(iceServers)) {
            throw new TypeError('missing iceServers');
        }
        // Enqueue command.
        await this._awaitQueue.push(async () => {
            await this._handler.updateIceServers(iceServers);
        }, 'transport.updateIceServers()');
    }
    /**
     * Create a Producer.
     */
    async produce({ track, streamId, encodings, codecOptions, codec, stopTracks = true, disableTrackOnPause = true, zeroRtpOnPause = false, onRtpSender, appData = {}, } = {}) {
        logger.debug('produce() [track:%o]', track);
        if (this._closed) {
            // This must be done here. Otherwise there is no chance to stop the given
            // track.
            if (track && stopTracks) {
                try {
                    track.stop();
                }
                catch (error) { }
            }
            throw new errors_1.InvalidStateError('closed');
        }
        else if (!track) {
            throw new TypeError('missing track');
        }
        else if (this._direction !== 'send') {
            throw new errors_1.UnsupportedError('not a sending transport');
        }
        else if (!this._canProduceByKind[track.kind]) {
            throw new errors_1.UnsupportedError(`cannot produce ${track.kind}`);
        }
        else if (track.readyState === 'ended') {
            throw new errors_1.InvalidStateError('track ended');
        }
        else if (this.listenerCount('connect') === 0 &&
            this._connectionState === 'new') {
            throw new TypeError('no "connect" listener set into this transport');
        }
        else if (this.listenerCount('produce') === 0) {
            throw new TypeError('no "produce" listener set into this transport');
        }
        else if (appData && typeof appData !== 'object') {
            throw new TypeError('if given, appData must be an object');
        }
        // Enqueue command.
        return (this._awaitQueue
            .push(async () => {
            let normalizedEncodings;
            if (encodings && !Array.isArray(encodings)) {
                throw TypeError('encodings must be an array');
            }
            else if (encodings?.length === 0) {
                normalizedEncodings = undefined;
            }
            else if (encodings) {
                normalizedEncodings = encodings.map(encoding => {
                    const normalizedEncoding = {
                        active: true,
                    };
                    if (encoding.active === false) {
                        normalizedEncoding.active = false;
                    }
                    if (typeof encoding.dtx === 'boolean') {
                        normalizedEncoding.dtx = encoding.dtx;
                    }
                    if (typeof encoding.scalabilityMode === 'string') {
                        normalizedEncoding.scalabilityMode = encoding.scalabilityMode;
                    }
                    if (typeof encoding.scaleResolutionDownBy === 'number') {
                        normalizedEncoding.scaleResolutionDownBy =
                            encoding.scaleResolutionDownBy;
                    }
                    if (typeof encoding.maxBitrate === 'number') {
                        normalizedEncoding.maxBitrate = encoding.maxBitrate;
                    }
                    if (typeof encoding.maxFramerate === 'number') {
                        normalizedEncoding.maxFramerate = encoding.maxFramerate;
                    }
                    if (typeof encoding.adaptivePtime === 'boolean') {
                        normalizedEncoding.adaptivePtime = encoding.adaptivePtime;
                    }
                    if (typeof encoding.priority === 'string') {
                        normalizedEncoding.priority = encoding.priority;
                    }
                    if (typeof encoding.networkPriority === 'string') {
                        normalizedEncoding.networkPriority = encoding.networkPriority;
                    }
                    return normalizedEncoding;
                });
            }
            const { localId, rtpParameters, rtpSender } = await this._handler.send({
                track,
                streamId,
                encodings: normalizedEncodings,
                codecOptions,
                codec,
                onRtpSender,
            });
            try {
                // This will fill rtpParameters's missing fields with default values.
                ortc.validateAndNormalizeRtpParameters(rtpParameters);
                const { id } = await new Promise((resolve, reject) => {
                    this.safeEmit('produce', {
                        kind: track.kind,
                        rtpParameters,
                        appData,
                    }, resolve, reject);
                });
                const producer = new Producer_1.Producer({
                    id,
                    localId,
                    rtpSender,
                    track,
                    rtpParameters,
                    stopTracks,
                    disableTrackOnPause,
                    zeroRtpOnPause,
                    appData,
                });
                this._producers.set(producer.id, producer);
                this.handleProducer(producer);
                // Emit observer event.
                this._observer.safeEmit('newproducer', producer);
                return producer;
            }
            catch (error) {
                this._handler.stopSending(localId).catch(() => { });
                throw error;
            }
        }, 'transport.produce()')
            // This catch is needed to stop the given track if the command above
            // failed due to closed Transport.
            .catch((error) => {
            if (stopTracks) {
                try {
                    track.stop();
                }
                catch (error2) { }
            }
            throw error;
        }));
    }
    /**
     * Create a Consumer to consume a remote Producer.
     */
    async consume({ id, producerId, kind, rtpParameters, streamId, onRtpReceiver, appData = {}, }) {
        logger.debug('consume()');
        if (this._closed) {
            throw new errors_1.InvalidStateError('closed');
        }
        else if (this._direction !== 'recv') {
            throw new errors_1.UnsupportedError('not a receiving transport');
        }
        else if (typeof id !== 'string') {
            throw new TypeError('missing id');
        }
        else if (typeof producerId !== 'string') {
            throw new TypeError('missing producerId');
        }
        else if (kind !== 'audio' && kind !== 'video') {
            throw new TypeError(`invalid kind '${kind}'`);
        }
        else if (this.listenerCount('connect') === 0 &&
            this._connectionState === 'new') {
            throw new TypeError('no "connect" listener set into this transport');
        }
        else if (appData && typeof appData !== 'object') {
            throw new TypeError('if given, appData must be an object');
        }
        // Clone given RTP parameters to not modify input data.
        const clonedRtpParameters = utils.clone(rtpParameters);
        // Ensure the device can consume it.
        const canConsume = ortc.canReceive(clonedRtpParameters, this._recvRtpCapabilities);
        if (!canConsume) {
            throw new errors_1.UnsupportedError('cannot consume this Producer');
        }
        const consumerCreationTask = new ConsumerCreationTask({
            id,
            producerId,
            kind,
            rtpParameters: clonedRtpParameters,
            streamId,
            onRtpReceiver,
            appData,
        });
        // Store the Consumer creation task.
        this._pendingConsumerTasks.push(consumerCreationTask);
        // There is no Consumer creation in progress, create it now.
        queueMicrotask(() => {
            if (this._closed) {
                return;
            }
            if (this._consumerCreationInProgress === false) {
                this.createPendingConsumers();
            }
        });
        return consumerCreationTask.promise;
    }
    /**
     * Create a DataProducer
     */
    async produceData({ ordered = true, maxPacketLifeTime, maxRetransmits, label = '', protocol = '', appData = {}, } = {}) {
        logger.debug('produceData()');
        if (this._closed) {
            throw new errors_1.InvalidStateError('closed');
        }
        else if (this._direction !== 'send') {
            throw new errors_1.UnsupportedError('not a sending transport');
        }
        else if (!this._sctpParameters) {
            throw new errors_1.UnsupportedError('SCTP not enabled by remote transport');
        }
        else if (this.listenerCount('connect') === 0 &&
            this._connectionState === 'new') {
            throw new TypeError('no "connect" listener set into this transport');
        }
        else if (this.listenerCount('producedata') === 0) {
            throw new TypeError('no "producedata" listener set into this transport');
        }
        else if (appData && typeof appData !== 'object') {
            throw new TypeError('if given, appData must be an object');
        }
        if (maxPacketLifeTime !== undefined || maxRetransmits !== undefined) {
            ordered = false;
        }
        // Enqueue command.
        return this._awaitQueue.push(async () => {
            const { dataChannel, sctpStreamParameters } = await this._handler.sendDataChannel({
                sctpStreamParameters: {
                    ordered,
                    maxPacketLifeTime,
                    maxRetransmits,
                    label,
                    protocol,
                },
            });
            let id;
            try {
                // This will fill sctpStreamParameters's missing fields with default values.
                ortc.validateAndNormalizeSctpStreamParameters(sctpStreamParameters);
                ({ id } = await new Promise((resolve, reject) => {
                    this.safeEmit('producedata', {
                        sctpStreamParameters,
                        label,
                        protocol,
                        appData,
                    }, resolve, reject);
                }));
            }
            catch (error) {
                // Do not leave the DataChannel open if the DataProducer is not created.
                try {
                    dataChannel.close();
                }
                catch (error2) { }
                throw error;
            }
            const dataProducer = new DataProducer_1.DataProducer({
                id,
                dataChannel,
                sctpStreamParameters,
                appData,
            });
            this._dataProducers.set(dataProducer.id, dataProducer);
            this.handleDataProducer(dataProducer);
            // Emit observer event.
            this._observer.safeEmit('newdataproducer', dataProducer);
            return dataProducer;
        }, 'transport.produceData()');
    }
    /**
     * Create a DataConsumer
     */
    async consumeData({ id, dataProducerId, sctpStreamParameters, label = '', protocol = '', appData = {}, }) {
        logger.debug('consumeData()');
        if (this._closed) {
            throw new errors_1.InvalidStateError('closed');
        }
        else if (this._direction !== 'recv') {
            throw new errors_1.UnsupportedError('not a receiving transport');
        }
        else if (!this._sctpParameters) {
            throw new errors_1.UnsupportedError('SCTP not enabled by remote transport');
        }
        else if (typeof id !== 'string') {
            throw new TypeError('missing id');
        }
        else if (typeof dataProducerId !== 'string') {
            throw new TypeError('missing dataProducerId');
        }
        else if (this.listenerCount('connect') === 0 &&
            this._connectionState === 'new') {
            throw new TypeError('no "connect" listener set into this transport');
        }
        else if (appData && typeof appData !== 'object') {
            throw new TypeError('if given, appData must be an object');
        }
        // Clone given SCTP stream parameters to not modify input data.
        const clonedSctpStreamParameters = utils.clone(sctpStreamParameters);
        // This may throw.
        ortc.validateAndNormalizeSctpStreamParameters(clonedSctpStreamParameters);
        // Enqueue command.
        return this._awaitQueue.push(async () => {
            const { dataChannel } = await this._handler.receiveDataChannel({
                // NOTE: Mirror it.
                maxMessageSize: this._sctpParameters?.maxSendMessageSize,
                sctpStreamParameters: clonedSctpStreamParameters,
                label,
                protocol,
            });
            const dataConsumer = new DataConsumer_1.DataConsumer({
                id,
                dataProducerId,
                dataChannel,
                sctpStreamParameters: clonedSctpStreamParameters,
                appData,
            });
            this._dataConsumers.set(dataConsumer.id, dataConsumer);
            this.handleDataConsumer(dataConsumer);
            // Emit observer event.
            this._observer.safeEmit('newdataconsumer', dataConsumer);
            return dataConsumer;
        }, 'transport.consumeData()');
    }
    getDataChannelMaxMessageSize() {
        return this._handler.getDataChannelMaxMessageSize();
    }
    // This method is guaranteed to never throw.
    createPendingConsumers() {
        this._consumerCreationInProgress = true;
        this._awaitQueue
            .push(async () => {
            if (this._pendingConsumerTasks.length === 0) {
                logger.debug('createPendingConsumers() | there is no Consumer to be created');
                return;
            }
            const pendingConsumerTasks = [...this._pendingConsumerTasks];
            // Clear pending Consumer tasks.
            this._pendingConsumerTasks = [];
            // Video Consumer in order to create the probator.
            let videoConsumerForProbator = undefined;
            // Fill options list.
            const optionsList = [];
            for (const task of pendingConsumerTasks) {
                const { id, kind, rtpParameters, streamId, onRtpReceiver } = task.consumerOptions;
                optionsList.push({
                    trackId: id,
                    kind: kind,
                    rtpParameters,
                    streamId,
                    onRtpReceiver,
                });
            }
            try {
                const results = await this._handler.receive(optionsList);
                for (let idx = 0; idx < results.length; ++idx) {
                    const task = pendingConsumerTasks[idx];
                    const result = results[idx];
                    const { id, producerId, kind, rtpParameters, appData } = task.consumerOptions;
                    const { localId, rtpReceiver, track } = result;
                    const consumer = new Consumer_1.Consumer({
                        id,
                        localId,
                        producerId,
                        rtpReceiver,
                        track,
                        rtpParameters,
                        appData: appData,
                    });
                    this._consumers.set(consumer.id, consumer);
                    this.handleConsumer(consumer);
                    // If this is the first video Consumer and the Consumer for RTP probation
                    // has not yet been created, it's time to create it.
                    if (!this._probatorConsumerCreated &&
                        !videoConsumerForProbator &&
                        kind === 'video') {
                        videoConsumerForProbator = consumer;
                    }
                    // Emit observer event.
                    this._observer.safeEmit('newconsumer', consumer);
                    task.resolve(consumer);
                }
            }
            catch (error) {
                for (const task of pendingConsumerTasks) {
                    task.reject(error);
                }
            }
            // If RTP probation must be handled, do it now.
            if (videoConsumerForProbator) {
                try {
                    const probatorRtpParameters = ortc.generateProbatorRtpParameters(videoConsumerForProbator.rtpParameters);
                    await this._handler.receive([
                        {
                            trackId: 'probator',
                            kind: 'video',
                            rtpParameters: probatorRtpParameters,
                        },
                    ]);
                    logger.debug('createPendingConsumers() | Consumer for RTP probation created');
                    this._probatorConsumerCreated = true;
                }
                catch (error) {
                    logger.error('createPendingConsumers() | failed to create Consumer for RTP probation:%o', error);
                }
            }
        }, 'transport.createPendingConsumers()')
            .then(() => {
            this._consumerCreationInProgress = false;
            // There are pending Consumer tasks, enqueue their creation.
            if (this._pendingConsumerTasks.length > 0) {
                this.createPendingConsumers();
            }
        })
            // NOTE: We only get here when the await queue is closed.
            .catch(() => { });
    }
    pausePendingConsumers() {
        this._consumerPauseInProgress = true;
        this._awaitQueue
            .push(async () => {
            if (this._pendingPauseConsumers.size === 0) {
                logger.debug('pausePendingConsumers() | there is no Consumer to be paused');
                return;
            }
            const pendingPauseConsumers = Array.from(this._pendingPauseConsumers.values());
            // Clear pending pause Consumer map.
            this._pendingPauseConsumers.clear();
            try {
                const localIds = pendingPauseConsumers.map(consumer => consumer.localId);
                await this._handler.pauseReceiving(localIds);
            }
            catch (error) {
                logger.error('pausePendingConsumers() | failed to pause Consumers:', error);
            }
        }, 'transport.pausePendingConsumers()')
            .then(() => {
            this._consumerPauseInProgress = false;
            // There are pending Consumers to be paused, do it.
            if (this._pendingPauseConsumers.size > 0) {
                this.pausePendingConsumers();
            }
        })
            // NOTE: We only get here when the await queue is closed.
            .catch(() => { });
    }
    resumePendingConsumers() {
        this._consumerResumeInProgress = true;
        this._awaitQueue
            .push(async () => {
            if (this._pendingResumeConsumers.size === 0) {
                logger.debug('resumePendingConsumers() | there is no Consumer to be resumed');
                return;
            }
            const pendingResumeConsumers = Array.from(this._pendingResumeConsumers.values());
            // Clear pending resume Consumer map.
            this._pendingResumeConsumers.clear();
            try {
                const localIds = pendingResumeConsumers.map(consumer => consumer.localId);
                await this._handler.resumeReceiving(localIds);
            }
            catch (error) {
                logger.error('resumePendingConsumers() | failed to resume Consumers:', error);
            }
        }, 'transport.resumePendingConsumers()')
            .then(() => {
            this._consumerResumeInProgress = false;
            // There are pending Consumer to be resumed, do it.
            if (this._pendingResumeConsumers.size > 0) {
                this.resumePendingConsumers();
            }
        })
            // NOTE: We only get here when the await queue is closed.
            .catch(() => { });
    }
    closePendingConsumers() {
        this._consumerCloseInProgress = true;
        this._awaitQueue
            .push(async () => {
            if (this._pendingCloseConsumers.size === 0) {
                logger.debug('closePendingConsumers() | there is no Consumer to be closed');
                return;
            }
            const pendingCloseConsumers = Array.from(this._pendingCloseConsumers.values());
            // Clear pending close Consumer map.
            this._pendingCloseConsumers.clear();
            try {
                await this._handler.stopReceiving(pendingCloseConsumers.map(consumer => consumer.localId));
            }
            catch (error) {
                logger.error('closePendingConsumers() | failed to close Consumers:', error);
            }
        }, 'transport.closePendingConsumers()')
            .then(() => {
            this._consumerCloseInProgress = false;
            // There are pending Consumer to be resumed, do it.
            if (this._pendingCloseConsumers.size > 0) {
                this.closePendingConsumers();
            }
        })
            // NOTE: We only get here when the await queue is closed.
            .catch(() => { });
    }
    handleHandler() {
        const handler = this._handler;
        handler.on('@connect', ({ dtlsParameters }, callback, errback) => {
            if (this._closed) {
                errback(new errors_1.InvalidStateError('closed'));
                return;
            }
            this.safeEmit('connect', { dtlsParameters }, callback, errback);
        });
        handler.on('@icegatheringstatechange', (iceGatheringState) => {
            if (iceGatheringState === this._iceGatheringState) {
                return;
            }
            logger.debug('ICE gathering state changed to %s', iceGatheringState);
            this._iceGatheringState = iceGatheringState;
            if (!this._closed) {
                this.safeEmit('icegatheringstatechange', iceGatheringState);
            }
        });
        handler.on('@icecandidateerror', (event) => {
            logger.debug(`ICE candidate error [url:${event.url}, localAddress:${event.address}, localPort:${event.port}]: ${event.errorCode} "${event.errorText}"`);
            this.safeEmit('icecandidateerror', event);
        });
        handler.on('@connectionstatechange', (connectionState) => {
            if (connectionState === this._connectionState) {
                return;
            }
            logger.debug('connection state changed to %s', connectionState);
            this._connectionState = connectionState;
            if (!this._closed) {
                this.safeEmit('connectionstatechange', connectionState);
            }
        });
    }
    handleProducer(producer) {
        producer.on('@close', () => {
            this._producers.delete(producer.id);
            if (this._closed) {
                return;
            }
            this._awaitQueue
                .push(async () => {
                await this._handler.stopSending(producer.localId);
            }, 'producer @close event')
                .catch((error) => logger.warn('producer closure failed:%o', error));
        });
        producer.on('@pause', (callback, errback) => {
            this._awaitQueue
                .push(async () => {
                await this._handler.pauseSending(producer.localId);
            }, 'producer @pause event')
                .then(callback)
                .catch(errback);
        });
        producer.on('@resume', (callback, errback) => {
            this._awaitQueue
                .push(async () => {
                await this._handler.resumeSending(producer.localId);
            }, 'producer @resume event')
                .then(callback)
                .catch(errback);
        });
        producer.on('@replacetrack', (track, callback, errback) => {
            this._awaitQueue
                .push(async () => {
                await this._handler.replaceTrack(producer.localId, track);
            }, 'producer @replacetrack event')
                .then(callback)
                .catch(errback);
        });
        producer.on('@setmaxspatiallayer', (spatialLayer, callback, errback) => {
            this._awaitQueue
                .push(async () => {
                await this._handler.setMaxSpatialLayer(producer.localId, spatialLayer);
            }, 'producer @setmaxspatiallayer event')
                .then(callback)
                .catch(errback);
        });
        producer.on('@setrtpencodingparameters', (params, callback, errback) => {
            this._awaitQueue
                .push(async () => {
                await this._handler.setRtpEncodingParameters(producer.localId, params);
            }, 'producer @setrtpencodingparameters event')
                .then(callback)
                .catch(errback);
        });
        producer.on('@getstats', (callback, errback) => {
            if (this._closed) {
                return errback(new errors_1.InvalidStateError('closed'));
            }
            this._handler
                .getSenderStats(producer.localId)
                .then(callback)
                .catch(errback);
        });
    }
    handleConsumer(consumer) {
        consumer.on('@close', () => {
            this._consumers.delete(consumer.id);
            this._pendingPauseConsumers.delete(consumer.id);
            this._pendingResumeConsumers.delete(consumer.id);
            if (this._closed) {
                return;
            }
            // Store the Consumer into the close list.
            this._pendingCloseConsumers.set(consumer.id, consumer);
            // There is no Consumer close in progress, do it now.
            if (this._consumerCloseInProgress === false) {
                this.closePendingConsumers();
            }
        });
        consumer.on('@pause', () => {
            // If Consumer is pending to be resumed, remove from pending resume list.
            if (this._pendingResumeConsumers.has(consumer.id)) {
                this._pendingResumeConsumers.delete(consumer.id);
            }
            // Store the Consumer into the pending list.
            this._pendingPauseConsumers.set(consumer.id, consumer);
            // There is no Consumer pause in progress, do it now.
            queueMicrotask(() => {
                if (this._closed) {
                    return;
                }
                if (this._consumerPauseInProgress === false) {
                    this.pausePendingConsumers();
                }
            });
        });
        consumer.on('@resume', () => {
            // If Consumer is pending to be paused, remove from pending pause list.
            if (this._pendingPauseConsumers.has(consumer.id)) {
                this._pendingPauseConsumers.delete(consumer.id);
            }
            // Store the Consumer into the pending list.
            this._pendingResumeConsumers.set(consumer.id, consumer);
            // There is no Consumer resume in progress, do it now.
            queueMicrotask(() => {
                if (this._closed) {
                    return;
                }
                if (this._consumerResumeInProgress === false) {
                    this.resumePendingConsumers();
                }
            });
        });
        consumer.on('@getstats', (callback, errback) => {
            if (this._closed) {
                return errback(new errors_1.InvalidStateError('closed'));
            }
            this._handler
                .getReceiverStats(consumer.localId)
                .then(callback)
                .catch(errback);
        });
    }
    handleDataProducer(dataProducer) {
        dataProducer.on('@close', () => {
            this._dataProducers.delete(dataProducer.id);
        });
    }
    handleDataConsumer(dataConsumer) {
        dataConsumer.on('@close', () => {
            this._dataConsumers.delete(dataConsumer.id);
        });
    }
}
exports.Transport = Transport;

},
15: function(module, exports, __req) {
"use strict";
var __createBinding = (this && this.__createBinding) || (Object.create ? (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    var desc = Object.getOwnPropertyDescriptor(m, k);
    if (!desc || ("get" in desc ? !m.__esModule : desc.writable || desc.configurable)) {
      desc = { enumerable: true, get: function() { return m[k]; } };
    }
    Object.defineProperty(o, k2, desc);
}) : (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    o[k2] = m[k];
}));
var __setModuleDefault = (this && this.__setModuleDefault) || (Object.create ? (function(o, v) {
    Object.defineProperty(o, "default", { enumerable: true, value: v });
}) : function(o, v) {
    o["default"] = v;
});
var __importStar = (this && this.__importStar) || (function () {
    var ownKeys = function(o) {
        ownKeys = Object.getOwnPropertyNames || function (o) {
            var ar = [];
            for (var k in o) if (Object.prototype.hasOwnProperty.call(o, k)) ar[ar.length] = k;
            return ar;
        };
        return ownKeys(o);
    };
    return function (mod) {
        if (mod && mod.__esModule) return mod;
        var result = {};
        if (mod != null) for (var k = ownKeys(mod), i = 0; i < k.length; i++) if (k[i] !== "default") __createBinding(result, mod, k[i]);
        __setModuleDefault(result, mod);
        return result;
    };
})();
Object.defineProperty(exports, "__esModule", { value: true });
exports.Chrome111 = void 0;
const sdpTransform = __importStar(__req(30));
const enhancedEvents_1 = __req(7);
const Logger_1 = __req(11);
const ortc = __importStar(__req(6));
const errors_1 = __req(12);
const scalabilityModes_1 = __req(5);
const RemoteSdp_1 = __req(31);
const sdpCommonUtils = __importStar(__req(32));
const sdpUnifiedPlanUtils = __importStar(__req(33));
const ortcUtils = __importStar(__req(34));
const logger = new Logger_1.Logger('Chrome111');
const NAME = 'Chrome111';
class Chrome111 extends enhancedEvents_1.EnhancedEventEmitter {
    // Closed flag.
    _closed = false;
    // Handler direction.
    _direction;
    // DataChannel max receive message size.
    _sctpMaxReceiveMessageSize;
    // Remote SDP handler.
    _remoteSdp;
    // Callback to request sending extended RTP capabilities on demand.
    _getSendExtendedRtpCapabilities;
    _forcedRtpExtensions;
    // Initial server side DTLS role. If not 'auto', it will force the opposite
    // value in client side.
    _forcedLocalDtlsRole;
    // RTCPeerConnection instance.
    _pc;
    // Map of RTCTransceivers indexed by MID.
    _mapMidTransceiver = new Map();
    // Default local stream for sending if no `streamId` is given in send().
    _sendStream = new MediaStream();
    // Whether a DataChannel m=application section has been created.
    _hasDataChannelMediaSection = false;
    // Sending DataChannel id value counter. Incremented for each new DataChannel.
    _nextSendSctpStreamId = 0;
    // Got transport local and remote parameters.
    _transportReady = false;
    /**
     * Creates a factory function.
     */
    static createFactory({ forcedRtpExtensions, }) {
        return {
            name: NAME,
            factory: (options) => new Chrome111({ ...options, forcedRtpExtensions }),
            getNativeRtpCapabilities: async ({ direction, }) => {
                logger.debug('getNativeRtpCapabilities() [direction:%o]', direction);
                let pc = new RTCPeerConnection({
                    iceServers: [],
                    iceTransportPolicy: 'all',
                    bundlePolicy: 'max-bundle',
                    rtcpMuxPolicy: 'require',
                });
                try {
                    const audioTransceiver = pc.addTransceiver('audio', { direction });
                    if (forcedRtpExtensions) {
                        ortcUtils.applyForcedRtpExtensions(audioTransceiver, forcedRtpExtensions);
                    }
                    // Create video transceiver with scalability mode in order to retrieve
                    // Dependency Descriptor header extension.
                    const videoTransceiver = pc.addTransceiver('video', {
                        direction,
                        sendEncodings: [{ scalabilityMode: 'L3T3' }],
                    });
                    if (forcedRtpExtensions) {
                        ortcUtils.applyForcedRtpExtensions(videoTransceiver, forcedRtpExtensions);
                    }
                    const offer = await pc.createOffer();
                    try {
                        pc.close();
                    }
                    catch (error) { }
                    pc = undefined;
                    const sdpObject = sdpTransform.parse(offer.sdp);
                    const nativeRtpCapabilities = Chrome111.getLocalRtpCapabilities(sdpObject);
                    return nativeRtpCapabilities;
                }
                catch (error) {
                    try {
                        pc?.close();
                    }
                    catch (error2) { }
                    // eslint-disable-next-line no-useless-assignment
                    pc = undefined;
                    throw error;
                }
            },
        };
    }
    static getLocalRtpCapabilities(localSdpObject) {
        const nativeRtpCapabilities = sdpCommonUtils.extractRtpCapabilities({
            sdpObject: localSdpObject,
        });
        // Need to validate and normalize native RTP capabilities.
        ortc.validateAndNormalizeRtpCapabilities(nativeRtpCapabilities);
        // libwebrtc supports NACK for OPUS but doesn't announce it.
        ortcUtils.addNackSupportForOpus(nativeRtpCapabilities);
        return nativeRtpCapabilities;
    }
    constructor({ direction, iceParameters, iceCandidates, dtlsParameters, sctpParameters, iceServers, iceTransportPolicy, additionalSettings, getSendExtendedRtpCapabilities, forcedRtpExtensions, }) {
        super();
        logger.debug('constructor()');
        this._direction = direction;
        // NOTE: Mirror it.
        this._sctpMaxReceiveMessageSize = sctpParameters?.maxSendMessageSize;
        this._remoteSdp = new RemoteSdp_1.RemoteSdp({
            iceParameters,
            iceCandidates,
            dtlsParameters,
            sctpParameters,
        });
        this._getSendExtendedRtpCapabilities = getSendExtendedRtpCapabilities;
        this._forcedRtpExtensions = forcedRtpExtensions;
        if (dtlsParameters.role && dtlsParameters.role !== 'auto') {
            this._forcedLocalDtlsRole =
                dtlsParameters.role === 'server' ? 'client' : 'server';
        }
        this._pc = new RTCPeerConnection({
            iceServers: iceServers ?? [],
            iceTransportPolicy: iceTransportPolicy ?? 'all',
            bundlePolicy: 'max-bundle',
            rtcpMuxPolicy: 'require',
            ...additionalSettings,
        });
        this._pc.addEventListener('icegatheringstatechange', this.onIceGatheringStateChange);
        this._pc.addEventListener('icecandidateerror', this.onIceCandidateError);
        if (this._pc.connectionState) {
            this._pc.addEventListener('connectionstatechange', this.onConnectionStateChange);
        }
        else {
            logger.warn('run() | pc.connectionState not supported, using pc.iceConnectionState');
            this._pc.addEventListener('iceconnectionstatechange', this.onIceConnectionStateChange);
        }
    }
    get name() {
        return NAME;
    }
    close() {
        logger.debug('close()');
        if (this._closed) {
            return;
        }
        this._closed = true;
        // Close RTCPeerConnection.
        try {
            this._pc.close();
        }
        catch (error) { }
        this._pc.removeEventListener('icegatheringstatechange', this.onIceGatheringStateChange);
        this._pc.removeEventListener('icecandidateerror', this.onIceCandidateError);
        this._pc.removeEventListener('connectionstatechange', this.onConnectionStateChange);
        this._pc.removeEventListener('iceconnectionstatechange', this.onIceConnectionStateChange);
        this.emit('@close');
        // Invoke close() in EnhancedEventEmitter classes.
        super.close();
    }
    async updateIceServers(iceServers) {
        this.assertNotClosed();
        logger.debug('updateIceServers()');
        const configuration = this._pc.getConfiguration();
        configuration.iceServers = iceServers;
        this._pc.setConfiguration(configuration);
    }
    async restartIce(iceParameters) {
        this.assertNotClosed();
        logger.debug('restartIce()');
        // Provide the remote SDP handler with new remote ICE parameters.
        this._remoteSdp.updateIceParameters(iceParameters);
        if (!this._transportReady) {
            return;
        }
        if (this._direction === 'send') {
            const offer = await this._pc.createOffer({ iceRestart: true });
            logger.debug('restartIce() | calling pc.setLocalDescription() [offer:%o]', offer);
            await this._pc.setLocalDescription(offer);
            const answer = {
                type: 'answer',
                sdp: this._remoteSdp.getSdp(),
            };
            logger.debug('restartIce() | calling pc.setRemoteDescription() [answer:%o]', answer);
            await this._pc.setRemoteDescription(answer);
        }
        else {
            const offer = {
                type: 'offer',
                sdp: this._remoteSdp.getSdp(),
            };
            logger.debug('restartIce() | calling pc.setRemoteDescription() [offer:%o]', offer);
            await this._pc.setRemoteDescription(offer);
            const answer = await this._pc.createAnswer();
            logger.debug('restartIce() | calling pc.setLocalDescription() [answer:%o]', answer);
            await this._pc.setLocalDescription(answer);
        }
    }
    async getTransportStats() {
        this.assertNotClosed();
        return this._pc.getStats();
    }
    async send({ track, streamId, encodings, codecOptions, codec, onRtpSender, }) {
        this.assertNotClosed();
        this.assertSendDirection();
        logger.debug('send() [kind:%s, track.id:%s, streamId:%s]', track.kind, track.id, streamId);
        if (encodings && encodings.length > 1) {
            // Set rid and verify scalabilityMode in each encoding.
            // NOTE: Even if WebRTC allows different scalabilityMode (different number
            // of temporal layers) per simulcast stream, we need that those are the
            // same in all them, so let's pick up the highest value.
            // NOTE: If scalabilityMode is not given, Chrome will use L1T3.
            let maxTemporalLayers = 1;
            for (const encoding of encodings) {
                const temporalLayers = encoding.scalabilityMode
                    ? (0, scalabilityModes_1.parse)(encoding.scalabilityMode).temporalLayers
                    : 3;
                if (temporalLayers > maxTemporalLayers) {
                    maxTemporalLayers = temporalLayers;
                }
            }
            encodings.forEach((encoding, idx) => {
                encoding.rid = `r${idx}`;
                encoding.scalabilityMode = `L1T${maxTemporalLayers}`;
            });
        }
        const mediaSectionIdx = this._remoteSdp.getNextMediaSectionIdx();
        const transceiver = this._pc.addTransceiver(track, {
            direction: 'sendonly',
            streams: [this._sendStream],
            sendEncodings: encodings,
        });
        try {
            if (this._forcedRtpExtensions) {
                ortcUtils.applyForcedRtpExtensions(transceiver, this._forcedRtpExtensions);
            }
            if (onRtpSender) {
                onRtpSender(transceiver.sender);
            }
            const offer = await this._pc.createOffer();
            let localSdpObject = sdpTransform.parse(offer.sdp);
            if (localSdpObject.extmapAllowMixed) {
                this._remoteSdp.setSessionExtmapAllowMixed();
            }
            const nativeRtpCapabilities = Chrome111.getLocalRtpCapabilities(localSdpObject);
            const sendExtendedRtpCapabilities = this._getSendExtendedRtpCapabilities(nativeRtpCapabilities);
            // Generic sending RTP parameters.
            const sendingRtpParameters = ortc.getSendingRtpParameters(track.kind, sendExtendedRtpCapabilities);
            // This may throw.
            sendingRtpParameters.codecs = ortc.reduceCodecs(sendingRtpParameters.codecs, codec);
            // Generic sending RTP parameters suitable for the SDP remote answer.
            const sendingRemoteRtpParameters = ortc.getSendingRemoteRtpParameters(track.kind, sendExtendedRtpCapabilities);
            // This may throw.
            sendingRemoteRtpParameters.codecs = ortc.reduceCodecs(sendingRemoteRtpParameters.codecs, codec);
            if (!this._transportReady) {
                await this.setupTransport({
                    localDtlsRole: this._forcedLocalDtlsRole ?? 'client',
                    localSdpObject,
                });
            }
            logger.debug('send() | calling pc.setLocalDescription() [offer:%o]', offer);
            await this._pc.setLocalDescription(offer);
            // We can now get the transceiver.mid.
            const localId = transceiver.mid;
            // Set MID.
            sendingRtpParameters.mid = localId;
            localSdpObject = sdpTransform.parse(this._pc.localDescription.sdp);
            const offerMediaObject = localSdpObject.media[mediaSectionIdx.idx];
            // Set RTCP CNAME.
            sendingRtpParameters.rtcp.cname = sdpCommonUtils.getCname({
                offerMediaObject,
            });
            // Set msid.
            sendingRtpParameters.msid = `${streamId ?? this._sendStream.id} ${track.id}`;
            // Set RTP encodings by parsing the SDP offer if no encodings are given.
            if (!encodings) {
                sendingRtpParameters.encodings = sdpUnifiedPlanUtils.getRtpEncodings({
                    offerMediaObject,
                    codecs: sendingRtpParameters.codecs,
                });
            }
            // Set RTP encodings by parsing the SDP offer and complete them with given
            // one if just a single encoding has been given.
            else if (encodings.length === 1) {
                const newEncodings = sdpUnifiedPlanUtils.getRtpEncodings({
                    offerMediaObject,
                    codecs: sendingRtpParameters.codecs,
                });
                Object.assign(newEncodings[0], encodings[0]);
                sendingRtpParameters.encodings = newEncodings;
            }
            // Otherwise if more than 1 encoding are given use them verbatim.
            else {
                sendingRtpParameters.encodings = encodings;
            }
            this._remoteSdp.send({
                offerMediaObject,
                reuseMid: mediaSectionIdx.reuseMid,
                offerRtpParameters: sendingRtpParameters,
                answerRtpParameters: sendingRemoteRtpParameters,
                codecOptions,
            });
            const answer = {
                type: 'answer',
                sdp: this._remoteSdp.getSdp(),
            };
            logger.debug('send() | calling pc.setRemoteDescription() [answer:%o]', answer);
            await this._pc.setRemoteDescription(answer);
            // Store in the map.
            this._mapMidTransceiver.set(localId, transceiver);
            return {
                localId,
                rtpParameters: sendingRtpParameters,
                rtpSender: transceiver.sender,
            };
        }
        catch (error) {
            // Undo the changes done in the PeerConnection, otherwise it gets out of
            // sync with the remote SDP and later calls to send() fail.
            try {
                if (this._pc.signalingState === 'have-local-offer') {
                    try {
                        await this._pc.setLocalDescription({ type: 'rollback' });
                    }
                    catch (error2) { }
                }
                transceiver.stop();
            }
            catch (error3) { }
            throw error;
        }
    }
    async stopSending(localId) {
        this.assertSendDirection();
        logger.debug('stopSending() [localId:%s]', localId);
        if (this._closed) {
            return;
        }
        const transceiver = this._mapMidTransceiver.get(localId);
        if (!transceiver) {
            throw new Error('associated RTCRtpTransceiver not found');
        }
        void transceiver.sender.replaceTrack(null);
        this._pc.removeTrack(transceiver.sender);
        const mediaSectionClosed = this._remoteSdp.closeMediaSection(transceiver.mid);
        if (mediaSectionClosed) {
            try {
                transceiver.stop();
            }
            catch (error) { }
        }
        const offer = await this._pc.createOffer();
        logger.debug('stopSending() | calling pc.setLocalDescription() [offer:%o]', offer);
        await this._pc.setLocalDescription(offer);
        const answer = {
            type: 'answer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('stopSending() | calling pc.setRemoteDescription() [answer:%o]', answer);
        await this._pc.setRemoteDescription(answer);
        this._mapMidTransceiver.delete(localId);
    }
    async pauseSending(localId) {
        this.assertNotClosed();
        this.assertSendDirection();
        logger.debug('pauseSending() [localId:%s]', localId);
        const transceiver = this._mapMidTransceiver.get(localId);
        if (!transceiver) {
            throw new Error('associated RTCRtpTransceiver not found');
        }
        transceiver.direction = 'inactive';
        this._remoteSdp.pauseMediaSection(localId);
        const offer = await this._pc.createOffer();
        logger.debug('pauseSending() | calling pc.setLocalDescription() [offer:%o]', offer);
        await this._pc.setLocalDescription(offer);
        const answer = {
            type: 'answer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('pauseSending() | calling pc.setRemoteDescription() [answer:%o]', answer);
        await this._pc.setRemoteDescription(answer);
    }
    async resumeSending(localId) {
        this.assertNotClosed();
        this.assertSendDirection();
        logger.debug('resumeSending() [localId:%s]', localId);
        const transceiver = this._mapMidTransceiver.get(localId);
        this._remoteSdp.resumeSendingMediaSection(localId);
        if (!transceiver) {
            throw new Error('associated RTCRtpTransceiver not found');
        }
        transceiver.direction = 'sendonly';
        const offer = await this._pc.createOffer();
        logger.debug('resumeSending() | calling pc.setLocalDescription() [offer:%o]', offer);
        await this._pc.setLocalDescription(offer);
        const answer = {
            type: 'answer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('resumeSending() | calling pc.setRemoteDescription() [answer:%o]', answer);
        await this._pc.setRemoteDescription(answer);
    }
    async replaceTrack(localId, track) {
        this.assertNotClosed();
        this.assertSendDirection();
        if (track) {
            logger.debug('replaceTrack() [localId:%s, track.id:%s]', localId, track.id);
        }
        else {
            logger.debug('replaceTrack() [localId:%s, no track]', localId);
        }
        const transceiver = this._mapMidTransceiver.get(localId);
        if (!transceiver) {
            throw new Error('associated RTCRtpTransceiver not found');
        }
        await transceiver.sender.replaceTrack(track);
    }
    async setMaxSpatialLayer(localId, spatialLayer) {
        this.assertNotClosed();
        this.assertSendDirection();
        logger.debug('setMaxSpatialLayer() [localId:%s, spatialLayer:%s]', localId, spatialLayer);
        const transceiver = this._mapMidTransceiver.get(localId);
        if (!transceiver) {
            throw new Error('associated RTCRtpTransceiver not found');
        }
        const parameters = transceiver.sender.getParameters();
        parameters.encodings.forEach((encoding, idx) => {
            if (idx <= spatialLayer) {
                encoding.active = true;
            }
            else {
                encoding.active = false;
            }
        });
        await transceiver.sender.setParameters(parameters);
        this._remoteSdp.muxMediaSectionSimulcast(localId, parameters.encodings);
        const offer = await this._pc.createOffer();
        logger.debug('setMaxSpatialLayer() | calling pc.setLocalDescription() [offer:%o]', offer);
        await this._pc.setLocalDescription(offer);
        const answer = {
            type: 'answer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('setMaxSpatialLayer() | calling pc.setRemoteDescription() [answer:%o]', answer);
        await this._pc.setRemoteDescription(answer);
    }
    async setRtpEncodingParameters(localId, params) {
        this.assertNotClosed();
        this.assertSendDirection();
        logger.debug('setRtpEncodingParameters() [localId:%s, params:%o]', localId, params);
        const transceiver = this._mapMidTransceiver.get(localId);
        if (!transceiver) {
            throw new Error('associated RTCRtpTransceiver not found');
        }
        const parameters = transceiver.sender.getParameters();
        parameters.encodings.forEach((encoding, idx) => {
            parameters.encodings[idx] = { ...encoding, ...params };
        });
        await transceiver.sender.setParameters(parameters);
        this._remoteSdp.muxMediaSectionSimulcast(localId, parameters.encodings);
        const offer = await this._pc.createOffer();
        logger.debug('setRtpEncodingParameters() | calling pc.setLocalDescription() [offer:%o]', offer);
        await this._pc.setLocalDescription(offer);
        const answer = {
            type: 'answer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('setRtpEncodingParameters() | calling pc.setRemoteDescription() [answer:%o]', answer);
        await this._pc.setRemoteDescription(answer);
    }
    async getSenderStats(localId) {
        this.assertNotClosed();
        this.assertSendDirection();
        const transceiver = this._mapMidTransceiver.get(localId);
        if (!transceiver) {
            throw new Error('associated RTCRtpTransceiver not found');
        }
        return transceiver.sender.getStats();
    }
    async sendDataChannel({ sctpStreamParameters, }) {
        this.assertNotClosed();
        this.assertSendDirection();
        const options = {
            negotiated: true,
            id: this._nextSendSctpStreamId,
            ordered: sctpStreamParameters.ordered,
            maxPacketLifeTime: sctpStreamParameters.maxPacketLifeTime,
            maxRetransmits: sctpStreamParameters.maxRetransmits,
            protocol: sctpStreamParameters.protocol,
        };
        logger.debug('sendDataChannel() [options:%o]', options);
        const dataChannel = this._pc.createDataChannel(sctpStreamParameters.label, options);
        // Increase next id.
        this._nextSendSctpStreamId =
            ++this._nextSendSctpStreamId % (this._pc.sctp?.maxChannels ?? 65536);
        // If this is the first DataChannel we need to create the SDP answer with
        // m=application section.
        if (!this._hasDataChannelMediaSection) {
            try {
                const offer = await this._pc.createOffer();
                const localSdpObject = sdpTransform.parse(offer.sdp);
                const offerMediaObject = localSdpObject.media.find(m => m.type === 'application');
                if (!this._transportReady) {
                    await this.setupTransport({
                        localDtlsRole: this._forcedLocalDtlsRole ?? 'client',
                        localSdpObject,
                    });
                }
                logger.debug('sendDataChannel() | calling pc.setLocalDescription() [offer:%o]', offer);
                await this._pc.setLocalDescription(offer);
                this._remoteSdp.sendSctpAssociation({ offerMediaObject });
                const answer = {
                    type: 'answer',
                    sdp: this._remoteSdp.getSdp(),
                };
                logger.debug('sendDataChannel() | calling pc.setRemoteDescription() [answer:%o]', answer);
                await this._pc.setRemoteDescription(answer);
                this._hasDataChannelMediaSection = true;
            }
            catch (error) {
                // Undo the changes done in the PeerConnection, otherwise it gets out of
                // sync with the remote SDP and later calls to send() and
                // sendDataChannel() fail.
                try {
                    if (this._pc.signalingState === 'have-local-offer') {
                        try {
                            await this._pc.setLocalDescription({ type: 'rollback' });
                        }
                        catch (error2) { }
                    }
                    dataChannel.close();
                }
                catch (error3) { }
                throw error;
            }
        }
        const newSctpStreamParameters = {
            streamId: options.id,
            ordered: options.ordered,
            maxPacketLifeTime: options.maxPacketLifeTime,
            maxRetransmits: options.maxRetransmits,
        };
        return { dataChannel, sctpStreamParameters: newSctpStreamParameters };
    }
    async receive(optionsList) {
        this.assertNotClosed();
        this.assertRecvDirection();
        const results = [];
        const mapLocalId = new Map();
        for (const options of optionsList) {
            const { trackId, kind, rtpParameters, streamId } = options;
            logger.debug('receive() [trackId:%s, kind:%s]', trackId, kind);
            const localId = rtpParameters.mid ?? String(this._mapMidTransceiver.size);
            mapLocalId.set(trackId, localId);
            // We ignore MSID `trackId` when consuming and always use our computed
            // `trackId` which matches the `consumer.id`.
            const { msidStreamId } = ortcUtils.getMsidStreamIdAndTrackId(rtpParameters.msid);
            this._remoteSdp.receive({
                mid: localId,
                kind,
                offerRtpParameters: rtpParameters,
                streamId: streamId ?? msidStreamId ?? rtpParameters.rtcp?.cname ?? '-',
                trackId,
            });
        }
        const offer = {
            type: 'offer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('receive() | calling pc.setRemoteDescription() [offer:%o]', offer);
        await this._pc.setRemoteDescription(offer);
        for (const options of optionsList) {
            const { trackId, onRtpReceiver } = options;
            if (onRtpReceiver) {
                const localId = mapLocalId.get(trackId);
                const transceiver = this._pc
                    .getTransceivers()
                    .find((t) => t.mid === localId);
                if (!transceiver) {
                    throw new Error('transceiver not found');
                }
                if (this._forcedRtpExtensions) {
                    ortcUtils.applyForcedRtpExtensions(transceiver, this._forcedRtpExtensions);
                }
                onRtpReceiver(transceiver.receiver);
            }
        }
        let answer = await this._pc.createAnswer();
        const localSdpObject = sdpTransform.parse(answer.sdp);
        for (const options of optionsList) {
            const { trackId, rtpParameters } = options;
            const localId = mapLocalId.get(trackId);
            const answerMediaObject = localSdpObject.media.find(m => String(m.mid) === localId);
            // May need to modify codec parameters in the answer based on codec
            // parameters in the offer.
            sdpCommonUtils.applyCodecParameters({
                offerRtpParameters: rtpParameters,
                answerMediaObject,
            });
        }
        answer = {
            type: 'answer',
            sdp: sdpTransform.write(localSdpObject),
        };
        if (!this._transportReady) {
            await this.setupTransport({
                localDtlsRole: this._forcedLocalDtlsRole ?? 'client',
                localSdpObject,
            });
        }
        logger.debug('receive() | calling pc.setLocalDescription() [answer:%o]', answer);
        await this._pc.setLocalDescription(answer);
        for (const options of optionsList) {
            const { trackId } = options;
            const localId = mapLocalId.get(trackId);
            const transceiver = this._pc
                .getTransceivers()
                .find((t) => t.mid === localId);
            if (!transceiver) {
                throw new Error('new RTCRtpTransceiver not found');
            }
            else {
                // Store in the map.
                this._mapMidTransceiver.set(localId, transceiver);
                results.push({
                    localId,
                    track: transceiver.receiver.track,
                    rtpReceiver: transceiver.receiver,
                });
            }
        }
        return results;
    }
    async stopReceiving(localIds) {
        this.assertRecvDirection();
        if (this._closed) {
            return;
        }
        for (const localId of localIds) {
            logger.debug('stopReceiving() [localId:%s]', localId);
            const transceiver = this._mapMidTransceiver.get(localId);
            if (!transceiver) {
                throw new Error('associated RTCRtpTransceiver not found');
            }
            this._remoteSdp.closeMediaSection(transceiver.mid);
        }
        const offer = {
            type: 'offer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('stopReceiving() | calling pc.setRemoteDescription() [offer:%o]', offer);
        await this._pc.setRemoteDescription(offer);
        const answer = await this._pc.createAnswer();
        logger.debug('stopReceiving() | calling pc.setLocalDescription() [answer:%o]', answer);
        await this._pc.setLocalDescription(answer);
        for (const localId of localIds) {
            this._mapMidTransceiver.delete(localId);
        }
    }
    async pauseReceiving(localIds) {
        this.assertNotClosed();
        this.assertRecvDirection();
        for (const localId of localIds) {
            logger.debug('pauseReceiving() [localId:%s]', localId);
            const transceiver = this._mapMidTransceiver.get(localId);
            if (!transceiver) {
                throw new Error('associated RTCRtpTransceiver not found');
            }
            transceiver.direction = 'inactive';
            this._remoteSdp.pauseMediaSection(localId);
        }
        const offer = {
            type: 'offer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('pauseReceiving() | calling pc.setRemoteDescription() [offer:%o]', offer);
        await this._pc.setRemoteDescription(offer);
        const answer = await this._pc.createAnswer();
        logger.debug('pauseReceiving() | calling pc.setLocalDescription() [answer:%o]', answer);
        await this._pc.setLocalDescription(answer);
    }
    async resumeReceiving(localIds) {
        this.assertNotClosed();
        this.assertRecvDirection();
        for (const localId of localIds) {
            logger.debug('resumeReceiving() [localId:%s]', localId);
            const transceiver = this._mapMidTransceiver.get(localId);
            if (!transceiver) {
                throw new Error('associated RTCRtpTransceiver not found');
            }
            transceiver.direction = 'recvonly';
            this._remoteSdp.resumeReceivingMediaSection(localId);
        }
        const offer = {
            type: 'offer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('resumeReceiving() | calling pc.setRemoteDescription() [offer:%o]', offer);
        await this._pc.setRemoteDescription(offer);
        const answer = await this._pc.createAnswer();
        logger.debug('resumeReceiving() | calling pc.setLocalDescription() [answer:%o]', answer);
        await this._pc.setLocalDescription(answer);
    }
    async getReceiverStats(localId) {
        this.assertNotClosed();
        this.assertRecvDirection();
        const transceiver = this._mapMidTransceiver.get(localId);
        if (!transceiver) {
            throw new Error('associated RTCRtpTransceiver not found');
        }
        return transceiver.receiver.getStats();
    }
    async receiveDataChannel({ maxMessageSize, sctpStreamParameters, label, protocol, }) {
        this.assertNotClosed();
        this.assertRecvDirection();
        const { streamId, ordered, maxPacketLifeTime, maxRetransmits, } = sctpStreamParameters;
        const options = {
            negotiated: true,
            id: streamId,
            ordered,
            maxPacketLifeTime,
            maxRetransmits,
            protocol,
        };
        logger.debug('receiveDataChannel() [options:%o]', options);
        const dataChannel = this._pc.createDataChannel(label, options);
        // If this is the first DataChannel we need to create the SDP offer with
        // m=application section.
        if (!this._hasDataChannelMediaSection) {
            this._remoteSdp.receiveSctpAssociation();
            const offer = {
                type: 'offer',
                sdp: this._remoteSdp.getSdp(),
            };
            logger.debug('receiveDataChannel() | calling pc.setRemoteDescription() [offer:%o]', offer);
            await this._pc.setRemoteDescription(offer);
            let answer = await this._pc.createAnswer();
            const localSdpObject = sdpTransform.parse(answer.sdp);
            const answerMediaObject = localSdpObject.media.find(m => m.type === 'application');
            if (typeof maxMessageSize === 'number') {
                answerMediaObject.maxMessageSize = maxMessageSize;
            }
            if (!this._transportReady) {
                await this.setupTransport({
                    localDtlsRole: this._forcedLocalDtlsRole ?? 'client',
                    localSdpObject,
                });
            }
            answer = {
                type: 'answer',
                sdp: sdpTransform.write(localSdpObject),
            };
            logger.debug('receiveDataChannel() | calling pc.setLocalDescription() [answer:%o]', answer);
            await this._pc.setLocalDescription(answer);
            this._hasDataChannelMediaSection = true;
        }
        return { dataChannel };
    }
    getDataChannelMaxMessageSize() {
        return this._direction === 'send'
            ? this._pc.sctp?.maxMessageSize
            : this._sctpMaxReceiveMessageSize;
    }
    async setupTransport({ localDtlsRole, localSdpObject, }) {
        if (!localSdpObject) {
            localSdpObject = sdpTransform.parse(this._pc.localDescription.sdp);
        }
        // Get our local DTLS parameters.
        const dtlsParameters = sdpCommonUtils.extractDtlsParameters({
            sdpObject: localSdpObject,
        });
        // Set our DTLS role.
        dtlsParameters.role = localDtlsRole;
        // Update the remote DTLS role in the SDP.
        this._remoteSdp.updateDtlsRole(localDtlsRole === 'client' ? 'server' : 'client');
        // Need to tell the remote transport about our parameters.
        await new Promise((resolve, reject) => {
            this.safeEmit('@connect', { dtlsParameters }, resolve, reject);
        });
        this._transportReady = true;
    }
    onIceGatheringStateChange = () => {
        this.emit('@icegatheringstatechange', this._pc.iceGatheringState);
    };
    onIceCandidateError = (event) => {
        this.emit('@icecandidateerror', event);
    };
    onConnectionStateChange = () => {
        this.emit('@connectionstatechange', this._pc.connectionState);
    };
    onIceConnectionStateChange = () => {
        switch (this._pc.iceConnectionState) {
            case 'checking': {
                this.emit('@connectionstatechange', 'connecting');
                break;
            }
            case 'connected':
            case 'completed': {
                this.emit('@connectionstatechange', 'connected');
                break;
            }
            case 'failed': {
                this.emit('@connectionstatechange', 'failed');
                break;
            }
            case 'disconnected': {
                this.emit('@connectionstatechange', 'disconnected');
                break;
            }
            case 'closed': {
                this.emit('@connectionstatechange', 'closed');
                break;
            }
        }
    };
    assertNotClosed() {
        if (this._closed) {
            throw new errors_1.InvalidStateError('method called in a closed handler');
        }
    }
    assertSendDirection() {
        if (this._direction !== 'send') {
            throw new Error('method can just be called for handlers with "send" direction');
        }
    }
    assertRecvDirection() {
        if (this._direction !== 'recv') {
            throw new Error('method can just be called for handlers with "recv" direction');
        }
    }
}
exports.Chrome111 = Chrome111;

},
16: function(module, exports, __req) {
"use strict";
var __createBinding = (this && this.__createBinding) || (Object.create ? (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    var desc = Object.getOwnPropertyDescriptor(m, k);
    if (!desc || ("get" in desc ? !m.__esModule : desc.writable || desc.configurable)) {
      desc = { enumerable: true, get: function() { return m[k]; } };
    }
    Object.defineProperty(o, k2, desc);
}) : (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    o[k2] = m[k];
}));
var __setModuleDefault = (this && this.__setModuleDefault) || (Object.create ? (function(o, v) {
    Object.defineProperty(o, "default", { enumerable: true, value: v });
}) : function(o, v) {
    o["default"] = v;
});
var __importStar = (this && this.__importStar) || (function () {
    var ownKeys = function(o) {
        ownKeys = Object.getOwnPropertyNames || function (o) {
            var ar = [];
            for (var k in o) if (Object.prototype.hasOwnProperty.call(o, k)) ar[ar.length] = k;
            return ar;
        };
        return ownKeys(o);
    };
    return function (mod) {
        if (mod && mod.__esModule) return mod;
        var result = {};
        if (mod != null) for (var k = ownKeys(mod), i = 0; i < k.length; i++) if (k[i] !== "default") __createBinding(result, mod, k[i]);
        __setModuleDefault(result, mod);
        return result;
    };
})();
Object.defineProperty(exports, "__esModule", { value: true });
exports.Chrome74 = void 0;
const sdpTransform = __importStar(__req(30));
const Logger_1 = __req(11);
const enhancedEvents_1 = __req(7);
const ortc = __importStar(__req(6));
const errors_1 = __req(12);
const scalabilityModes_1 = __req(5);
const RemoteSdp_1 = __req(31);
const sdpCommonUtils = __importStar(__req(32));
const sdpUnifiedPlanUtils = __importStar(__req(33));
const ortcUtils = __importStar(__req(34));
const logger = new Logger_1.Logger('Chrome74');
const NAME = 'Chrome74';
class Chrome74 extends enhancedEvents_1.EnhancedEventEmitter {
    // Closed flag.
    _closed = false;
    // Handler direction.
    _direction;
    // DataChannel max receive message size.
    _sctpMaxReceiveMessageSize;
    // Remote SDP handler.
    _remoteSdp;
    // Callback to request sending extended RTP capabilities on demand.
    _getSendExtendedRtpCapabilities;
    _forcedRtpExtensions;
    // Initial server side DTLS role. If not 'auto', it will force the opposite
    // value in client side.
    _forcedLocalDtlsRole;
    // RTCPeerConnection instance.
    _pc;
    // Map of RTCTransceivers indexed by MID.
    _mapMidTransceiver = new Map();
    // Default local stream for sending if no `streamId` is given in send().
    _sendStream = new MediaStream();
    // Whether a DataChannel m=application section has been created.
    _hasDataChannelMediaSection = false;
    // Sending DataChannel id value counter. Incremented for each new DataChannel.
    _nextSendSctpStreamId = 0;
    // Got transport local and remote parameters.
    _transportReady = false;
    /**
     * Creates a factory function.
     */
    static createFactory({ forcedRtpExtensions, }) {
        return {
            name: NAME,
            factory: (options) => new Chrome74({ ...options, forcedRtpExtensions }),
            getNativeRtpCapabilities: async ({ direction, }) => {
                logger.debug('getNativeRtpCapabilities() [direction:%o]', direction);
                let pc = new RTCPeerConnection({
                    iceServers: [],
                    iceTransportPolicy: 'all',
                    bundlePolicy: 'max-bundle',
                    rtcpMuxPolicy: 'require',
                });
                try {
                    const audioTransceiver = pc.addTransceiver('audio', { direction });
                    if (forcedRtpExtensions) {
                        ortcUtils.applyForcedRtpExtensions(audioTransceiver, forcedRtpExtensions);
                    }
                    const videoTransceiver = pc.addTransceiver('video', { direction });
                    if (forcedRtpExtensions) {
                        ortcUtils.applyForcedRtpExtensions(videoTransceiver, forcedRtpExtensions);
                    }
                    const offer = await pc.createOffer();
                    try {
                        pc.close();
                    }
                    catch (error) { }
                    pc = undefined;
                    const sdpObject = sdpTransform.parse(offer.sdp);
                    const nativeRtpCapabilities = Chrome74.getLocalRtpCapabilities(sdpObject);
                    return nativeRtpCapabilities;
                }
                catch (error) {
                    try {
                        pc?.close();
                    }
                    catch (error2) { }
                    // eslint-disable-next-line no-useless-assignment
                    pc = undefined;
                    throw error;
                }
            },
        };
    }
    static getLocalRtpCapabilities(localSdpObject) {
        const nativeRtpCapabilities = sdpCommonUtils.extractRtpCapabilities({
            sdpObject: localSdpObject,
        });
        // Need to validate and normalize native RTP capabilities.
        ortc.validateAndNormalizeRtpCapabilities(nativeRtpCapabilities);
        // libwebrtc supports NACK for OPUS but doesn't announce it.
        ortcUtils.addNackSupportForOpus(nativeRtpCapabilities);
        return nativeRtpCapabilities;
    }
    constructor({ direction, iceParameters, iceCandidates, dtlsParameters, sctpParameters, iceServers, iceTransportPolicy, additionalSettings, getSendExtendedRtpCapabilities, forcedRtpExtensions, }) {
        super();
        logger.debug('constructor()');
        this._direction = direction;
        // NOTE: Mirror it.
        this._sctpMaxReceiveMessageSize = sctpParameters?.maxSendMessageSize;
        this._remoteSdp = new RemoteSdp_1.RemoteSdp({
            iceParameters,
            iceCandidates,
            dtlsParameters,
            sctpParameters,
        });
        this._getSendExtendedRtpCapabilities = getSendExtendedRtpCapabilities;
        this._forcedRtpExtensions = forcedRtpExtensions;
        if (dtlsParameters.role && dtlsParameters.role !== 'auto') {
            this._forcedLocalDtlsRole =
                dtlsParameters.role === 'server' ? 'client' : 'server';
        }
        this._pc = new RTCPeerConnection({
            iceServers: iceServers ?? [],
            iceTransportPolicy: iceTransportPolicy ?? 'all',
            bundlePolicy: 'max-bundle',
            rtcpMuxPolicy: 'require',
            ...additionalSettings,
        });
        this._pc.addEventListener('icegatheringstatechange', this.onIceGatheringStateChange);
        this._pc.addEventListener('icecandidateerror', this.onIceCandidateError);
        if (this._pc.connectionState) {
            this._pc.addEventListener('connectionstatechange', this.onConnectionStateChange);
        }
        else {
            logger.warn('run() | pc.connectionState not supported, using pc.iceConnectionState');
            this._pc.addEventListener('iceconnectionstatechange', this.onIceConnectionStateChange);
        }
    }
    get name() {
        return NAME;
    }
    close() {
        logger.debug('close()');
        if (this._closed) {
            return;
        }
        this._closed = true;
        // Close RTCPeerConnection.
        try {
            this._pc.close();
        }
        catch (error) { }
        this._pc.removeEventListener('icegatheringstatechange', this.onIceGatheringStateChange);
        this._pc.removeEventListener('icecandidateerror', this.onIceCandidateError);
        this._pc.removeEventListener('connectionstatechange', this.onConnectionStateChange);
        this._pc.removeEventListener('iceconnectionstatechange', this.onIceConnectionStateChange);
        this.emit('@close');
        // Invoke close() in EnhancedEventEmitter classes.
        super.close();
    }
    async updateIceServers(iceServers) {
        this.assertNotClosed();
        logger.debug('updateIceServers()');
        const configuration = this._pc.getConfiguration();
        configuration.iceServers = iceServers;
        this._pc.setConfiguration(configuration);
    }
    async restartIce(iceParameters) {
        this.assertNotClosed();
        logger.debug('restartIce()');
        // Provide the remote SDP handler with new remote ICE parameters.
        this._remoteSdp.updateIceParameters(iceParameters);
        if (!this._transportReady) {
            return;
        }
        if (this._direction === 'send') {
            const offer = await this._pc.createOffer({ iceRestart: true });
            logger.debug('restartIce() | calling pc.setLocalDescription() [offer:%o]', offer);
            await this._pc.setLocalDescription(offer);
            const answer = {
                type: 'answer',
                sdp: this._remoteSdp.getSdp(),
            };
            logger.debug('restartIce() | calling pc.setRemoteDescription() [answer:%o]', answer);
            await this._pc.setRemoteDescription(answer);
        }
        else {
            const offer = {
                type: 'offer',
                sdp: this._remoteSdp.getSdp(),
            };
            logger.debug('restartIce() | calling pc.setRemoteDescription() [offer:%o]', offer);
            await this._pc.setRemoteDescription(offer);
            const answer = await this._pc.createAnswer();
            logger.debug('restartIce() | calling pc.setLocalDescription() [answer:%o]', answer);
            await this._pc.setLocalDescription(answer);
        }
    }
    async getTransportStats() {
        this.assertNotClosed();
        return this._pc.getStats();
    }
    async send({ track, streamId, encodings, codecOptions, codec, onRtpSender, }) {
        this.assertNotClosed();
        this.assertSendDirection();
        logger.debug('send() [kind:%s, track.id:%s, streamId:%s]', track.kind, track.id, streamId);
        if (encodings && encodings.length > 1) {
            encodings.forEach((encoding, idx) => {
                encoding.rid = `r${idx}`;
            });
        }
        const mediaSectionIdx = this._remoteSdp.getNextMediaSectionIdx();
        const transceiver = this._pc.addTransceiver(track, {
            direction: 'sendonly',
            streams: [this._sendStream],
            sendEncodings: encodings,
        });
        try {
            if (this._forcedRtpExtensions) {
                ortcUtils.applyForcedRtpExtensions(transceiver, this._forcedRtpExtensions);
            }
            if (onRtpSender) {
                onRtpSender(transceiver.sender);
            }
            let offer = await this._pc.createOffer();
            let localSdpObject = sdpTransform.parse(offer.sdp);
            if (localSdpObject.extmapAllowMixed) {
                this._remoteSdp.setSessionExtmapAllowMixed();
            }
            const nativeRtpCapabilities = Chrome74.getLocalRtpCapabilities(localSdpObject);
            const sendExtendedRtpCapabilities = this._getSendExtendedRtpCapabilities(nativeRtpCapabilities);
            // Generic sending RTP parameters.
            const sendingRtpParameters = ortc.getSendingRtpParameters(track.kind, sendExtendedRtpCapabilities);
            // This may throw.
            sendingRtpParameters.codecs = ortc.reduceCodecs(sendingRtpParameters.codecs, codec);
            // Generic sending RTP parameters suitable for the SDP remote answer.
            const sendingRemoteRtpParameters = ortc.getSendingRemoteRtpParameters(track.kind, sendExtendedRtpCapabilities);
            // This may throw.
            sendingRemoteRtpParameters.codecs = ortc.reduceCodecs(sendingRemoteRtpParameters.codecs, codec);
            if (!this._transportReady) {
                await this.setupTransport({
                    localDtlsRole: this._forcedLocalDtlsRole ?? 'client',
                    localSdpObject,
                });
            }
            // Special case for VP9 with SVC.
            let hackVp9Svc = false;
            const layers = (0, scalabilityModes_1.parse)((encodings ?? [{}])[0].scalabilityMode);
            let offerMediaObject;
            if (encodings?.length === 1 &&
                layers.spatialLayers > 1 &&
                sendingRtpParameters.codecs[0].mimeType.toLowerCase() === 'video/vp9') {
                logger.debug('send() | enabling legacy simulcast for VP9 SVC');
                hackVp9Svc = true;
                localSdpObject = sdpTransform.parse(offer.sdp);
                offerMediaObject = localSdpObject.media[mediaSectionIdx.idx];
                sdpUnifiedPlanUtils.addLegacySimulcast({
                    offerMediaObject,
                    numStreams: layers.spatialLayers,
                });
                offer = {
                    type: 'offer',
                    sdp: sdpTransform.write(localSdpObject),
                };
            }
            logger.debug('send() | calling pc.setLocalDescription() [offer:%o]', offer);
            await this._pc.setLocalDescription(offer);
            // We can now get the transceiver.mid.
            const localId = transceiver.mid;
            // Set MID.
            sendingRtpParameters.mid = localId;
            localSdpObject = sdpTransform.parse(this._pc.localDescription.sdp);
            offerMediaObject = localSdpObject.media[mediaSectionIdx.idx];
            // Set RTCP CNAME.
            sendingRtpParameters.rtcp.cname = sdpCommonUtils.getCname({
                offerMediaObject,
            });
            // Set msid.
            sendingRtpParameters.msid = `${streamId ?? this._sendStream.id} ${track.id}`;
            // Set RTP encodings by parsing the SDP offer if no encodings are given.
            if (!encodings) {
                sendingRtpParameters.encodings = sdpUnifiedPlanUtils.getRtpEncodings({
                    offerMediaObject,
                    codecs: sendingRtpParameters.codecs,
                });
            }
            // Set RTP encodings by parsing the SDP offer and complete them with given
            // one if just a single encoding has been given.
            else if (encodings.length === 1) {
                let newEncodings = sdpUnifiedPlanUtils.getRtpEncodings({
                    offerMediaObject,
                    codecs: sendingRtpParameters.codecs,
                });
                Object.assign(newEncodings[0], encodings[0]);
                // Hack for VP9 SVC.
                if (hackVp9Svc) {
                    newEncodings = [newEncodings[0]];
                }
                sendingRtpParameters.encodings = newEncodings;
            }
            // Otherwise if more than 1 encoding are given use them verbatim.
            else {
                sendingRtpParameters.encodings = encodings;
            }
            // If VP8 or H264 and there is effective simulcast, add scalabilityMode to
            // each encoding.
            if (sendingRtpParameters.encodings.length > 1 &&
                (sendingRtpParameters.codecs[0].mimeType.toLowerCase() ===
                    'video/vp8' ||
                    sendingRtpParameters.codecs[0].mimeType.toLowerCase() ===
                        'video/h264')) {
                for (const encoding of sendingRtpParameters.encodings) {
                    if (encoding.scalabilityMode) {
                        encoding.scalabilityMode = `L1T${layers.temporalLayers}`;
                    }
                    else {
                        encoding.scalabilityMode = 'L1T3';
                    }
                }
            }
            this._remoteSdp.send({
                offerMediaObject,
                reuseMid: mediaSectionIdx.reuseMid,
                offerRtpParameters: sendingRtpParameters,
                answerRtpParameters: sendingRemoteRtpParameters,
                codecOptions,
            });
            const answer = {
                type: 'answer',
                sdp: this._remoteSdp.getSdp(),
            };
            logger.debug('send() | calling pc.setRemoteDescription() [answer:%o]', answer);
            await this._pc.setRemoteDescription(answer);
            // Store in the map.
            this._mapMidTransceiver.set(localId, transceiver);
            return {
                localId,
                rtpParameters: sendingRtpParameters,
                rtpSender: transceiver.sender,
            };
        }
        catch (error) {
            // Undo the changes done in the PeerConnection, otherwise it gets out of
            // sync with the remote SDP and later calls to send() fail.
            try {
                if (this._pc.signalingState === 'have-local-offer') {
                    try {
                        await this._pc.setLocalDescription({ type: 'rollback' });
                    }
                    catch (error2) { }
                }
                transceiver.stop();
            }
            catch (error3) { }
            throw error;
        }
    }
    async stopSending(localId) {
        this.assertSendDirection();
        logger.debug('stopSending() [localId:%s]', localId);
        if (this._closed) {
            return;
        }
        const transceiver = this._mapMidTransceiver.get(localId);
        if (!transceiver) {
            throw new Error('associated RTCRtpTransceiver not found');
        }
        void transceiver.sender.replaceTrack(null);
        this._pc.removeTrack(transceiver.sender);
        const mediaSectionClosed = this._remoteSdp.closeMediaSection(transceiver.mid);
        if (mediaSectionClosed) {
            try {
                transceiver.stop();
            }
            catch (error) { }
        }
        const offer = await this._pc.createOffer();
        logger.debug('stopSending() | calling pc.setLocalDescription() [offer:%o]', offer);
        await this._pc.setLocalDescription(offer);
        const answer = {
            type: 'answer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('stopSending() | calling pc.setRemoteDescription() [answer:%o]', answer);
        await this._pc.setRemoteDescription(answer);
        this._mapMidTransceiver.delete(localId);
    }
    async pauseSending(localId) {
        this.assertNotClosed();
        this.assertSendDirection();
        logger.debug('pauseSending() [localId:%s]', localId);
        const transceiver = this._mapMidTransceiver.get(localId);
        if (!transceiver) {
            throw new Error('associated RTCRtpTransceiver not found');
        }
        transceiver.direction = 'inactive';
        this._remoteSdp.pauseMediaSection(localId);
        const offer = await this._pc.createOffer();
        logger.debug('pauseSending() | calling pc.setLocalDescription() [offer:%o]', offer);
        await this._pc.setLocalDescription(offer);
        const answer = {
            type: 'answer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('pauseSending() | calling pc.setRemoteDescription() [answer:%o]', answer);
        await this._pc.setRemoteDescription(answer);
    }
    async resumeSending(localId) {
        this.assertNotClosed();
        this.assertSendDirection();
        logger.debug('resumeSending() [localId:%s]', localId);
        const transceiver = this._mapMidTransceiver.get(localId);
        this._remoteSdp.resumeSendingMediaSection(localId);
        if (!transceiver) {
            throw new Error('associated RTCRtpTransceiver not found');
        }
        transceiver.direction = 'sendonly';
        const offer = await this._pc.createOffer();
        logger.debug('resumeSending() | calling pc.setLocalDescription() [offer:%o]', offer);
        await this._pc.setLocalDescription(offer);
        const answer = {
            type: 'answer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('resumeSending() | calling pc.setRemoteDescription() [answer:%o]', answer);
        await this._pc.setRemoteDescription(answer);
    }
    async replaceTrack(localId, track) {
        this.assertNotClosed();
        this.assertSendDirection();
        if (track) {
            logger.debug('replaceTrack() [localId:%s, track.id:%s]', localId, track.id);
        }
        else {
            logger.debug('replaceTrack() [localId:%s, no track]', localId);
        }
        const transceiver = this._mapMidTransceiver.get(localId);
        if (!transceiver) {
            throw new Error('associated RTCRtpTransceiver not found');
        }
        await transceiver.sender.replaceTrack(track);
    }
    async setMaxSpatialLayer(localId, spatialLayer) {
        this.assertNotClosed();
        this.assertSendDirection();
        logger.debug('setMaxSpatialLayer() [localId:%s, spatialLayer:%s]', localId, spatialLayer);
        const transceiver = this._mapMidTransceiver.get(localId);
        if (!transceiver) {
            throw new Error('associated RTCRtpTransceiver not found');
        }
        const parameters = transceiver.sender.getParameters();
        parameters.encodings.forEach((encoding, idx) => {
            if (idx <= spatialLayer) {
                encoding.active = true;
            }
            else {
                encoding.active = false;
            }
        });
        await transceiver.sender.setParameters(parameters);
        this._remoteSdp.muxMediaSectionSimulcast(localId, parameters.encodings);
        const offer = await this._pc.createOffer();
        logger.debug('setMaxSpatialLayer() | calling pc.setLocalDescription() [offer:%o]', offer);
        await this._pc.setLocalDescription(offer);
        const answer = {
            type: 'answer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('setMaxSpatialLayer() | calling pc.setRemoteDescription() [answer:%o]', answer);
        await this._pc.setRemoteDescription(answer);
    }
    async setRtpEncodingParameters(localId, params) {
        this.assertNotClosed();
        this.assertSendDirection();
        logger.debug('setRtpEncodingParameters() [localId:%s, params:%o]', localId, params);
        const transceiver = this._mapMidTransceiver.get(localId);
        if (!transceiver) {
            throw new Error('associated RTCRtpTransceiver not found');
        }
        const parameters = transceiver.sender.getParameters();
        parameters.encodings.forEach((encoding, idx) => {
            parameters.encodings[idx] = { ...encoding, ...params };
        });
        await transceiver.sender.setParameters(parameters);
        this._remoteSdp.muxMediaSectionSimulcast(localId, parameters.encodings);
        const offer = await this._pc.createOffer();
        logger.debug('setRtpEncodingParameters() | calling pc.setLocalDescription() [offer:%o]', offer);
        await this._pc.setLocalDescription(offer);
        const answer = {
            type: 'answer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('setRtpEncodingParameters() | calling pc.setRemoteDescription() [answer:%o]', answer);
        await this._pc.setRemoteDescription(answer);
    }
    async getSenderStats(localId) {
        this.assertNotClosed();
        this.assertSendDirection();
        const transceiver = this._mapMidTransceiver.get(localId);
        if (!transceiver) {
            throw new Error('associated RTCRtpTransceiver not found');
        }
        return transceiver.sender.getStats();
    }
    async sendDataChannel({ sctpStreamParameters, }) {
        this.assertNotClosed();
        this.assertSendDirection();
        const options = {
            negotiated: true,
            id: this._nextSendSctpStreamId,
            ordered: sctpStreamParameters.ordered,
            maxPacketLifeTime: sctpStreamParameters.maxPacketLifeTime,
            maxRetransmits: sctpStreamParameters.maxRetransmits,
            protocol: sctpStreamParameters.protocol,
        };
        logger.debug('sendDataChannel() [options:%o]', options);
        const dataChannel = this._pc.createDataChannel(sctpStreamParameters.label, options);
        // Increase next id.
        this._nextSendSctpStreamId =
            ++this._nextSendSctpStreamId % (this._pc.sctp?.maxChannels ?? 65536);
        // If this is the first DataChannel we need to create the SDP answer with
        // m=application section.
        if (!this._hasDataChannelMediaSection) {
            try {
                const offer = await this._pc.createOffer();
                const localSdpObject = sdpTransform.parse(offer.sdp);
                const offerMediaObject = localSdpObject.media.find(m => m.type === 'application');
                if (!this._transportReady) {
                    await this.setupTransport({
                        localDtlsRole: this._forcedLocalDtlsRole ?? 'client',
                        localSdpObject,
                    });
                }
                logger.debug('sendDataChannel() | calling pc.setLocalDescription() [offer:%o]', offer);
                await this._pc.setLocalDescription(offer);
                this._remoteSdp.sendSctpAssociation({ offerMediaObject });
                const answer = {
                    type: 'answer',
                    sdp: this._remoteSdp.getSdp(),
                };
                logger.debug('sendDataChannel() | calling pc.setRemoteDescription() [answer:%o]', answer);
                await this._pc.setRemoteDescription(answer);
                this._hasDataChannelMediaSection = true;
            }
            catch (error) {
                // Undo the changes done in the PeerConnection, otherwise it gets out of
                // sync with the remote SDP and later calls to send() and
                // sendDataChannel() fail.
                try {
                    if (this._pc.signalingState === 'have-local-offer') {
                        try {
                            await this._pc.setLocalDescription({ type: 'rollback' });
                        }
                        catch (error2) { }
                    }
                    dataChannel.close();
                }
                catch (error3) { }
                throw error;
            }
        }
        const newSctpStreamParameters = {
            streamId: options.id,
            ordered: options.ordered,
            maxPacketLifeTime: options.maxPacketLifeTime,
            maxRetransmits: options.maxRetransmits,
        };
        return { dataChannel, sctpStreamParameters: newSctpStreamParameters };
    }
    async receive(optionsList) {
        this.assertNotClosed();
        this.assertRecvDirection();
        const results = [];
        const mapLocalId = new Map();
        for (const options of optionsList) {
            const { trackId, kind, rtpParameters, streamId } = options;
            logger.debug('receive() [trackId:%s, kind:%s]', trackId, kind);
            const localId = rtpParameters.mid ?? String(this._mapMidTransceiver.size);
            mapLocalId.set(trackId, localId);
            // We ignore MSID `trackId` when consuming and always use our computed
            // `trackId` which matches the `consumer.id`.
            const { msidStreamId } = ortcUtils.getMsidStreamIdAndTrackId(rtpParameters.msid);
            this._remoteSdp.receive({
                mid: localId,
                kind,
                offerRtpParameters: rtpParameters,
                streamId: streamId ?? msidStreamId ?? rtpParameters.rtcp?.cname ?? '-',
                trackId,
            });
        }
        const offer = {
            type: 'offer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('receive() | calling pc.setRemoteDescription() [offer:%o]', offer);
        await this._pc.setRemoteDescription(offer);
        for (const options of optionsList) {
            const { trackId, onRtpReceiver } = options;
            if (onRtpReceiver) {
                const localId = mapLocalId.get(trackId);
                const transceiver = this._pc
                    .getTransceivers()
                    .find((t) => t.mid === localId);
                if (!transceiver) {
                    throw new Error('transceiver not found');
                }
                if (this._forcedRtpExtensions) {
                    ortcUtils.applyForcedRtpExtensions(transceiver, this._forcedRtpExtensions);
                }
                onRtpReceiver(transceiver.receiver);
            }
        }
        let answer = await this._pc.createAnswer();
        const localSdpObject = sdpTransform.parse(answer.sdp);
        for (const options of optionsList) {
            const { trackId, rtpParameters } = options;
            const localId = mapLocalId.get(trackId);
            const answerMediaObject = localSdpObject.media.find(m => String(m.mid) === localId);
            // May need to modify codec parameters in the answer based on codec
            // parameters in the offer.
            sdpCommonUtils.applyCodecParameters({
                offerRtpParameters: rtpParameters,
                answerMediaObject,
            });
        }
        answer = {
            type: 'answer',
            sdp: sdpTransform.write(localSdpObject),
        };
        if (!this._transportReady) {
            await this.setupTransport({
                localDtlsRole: this._forcedLocalDtlsRole ?? 'client',
                localSdpObject,
            });
        }
        logger.debug('receive() | calling pc.setLocalDescription() [answer:%o]', answer);
        await this._pc.setLocalDescription(answer);
        for (const options of optionsList) {
            const { trackId } = options;
            const localId = mapLocalId.get(trackId);
            const transceiver = this._pc
                .getTransceivers()
                .find((t) => t.mid === localId);
            if (!transceiver) {
                throw new Error('new RTCRtpTransceiver not found');
            }
            else {
                // Store in the map.
                this._mapMidTransceiver.set(localId, transceiver);
                results.push({
                    localId,
                    track: transceiver.receiver.track,
                    rtpReceiver: transceiver.receiver,
                });
            }
        }
        return results;
    }
    async stopReceiving(localIds) {
        this.assertRecvDirection();
        if (this._closed) {
            return;
        }
        for (const localId of localIds) {
            logger.debug('stopReceiving() [localId:%s]', localId);
            const transceiver = this._mapMidTransceiver.get(localId);
            if (!transceiver) {
                throw new Error('associated RTCRtpTransceiver not found');
            }
            this._remoteSdp.closeMediaSection(transceiver.mid);
        }
        const offer = {
            type: 'offer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('stopReceiving() | calling pc.setRemoteDescription() [offer:%o]', offer);
        await this._pc.setRemoteDescription(offer);
        const answer = await this._pc.createAnswer();
        logger.debug('stopReceiving() | calling pc.setLocalDescription() [answer:%o]', answer);
        await this._pc.setLocalDescription(answer);
        for (const localId of localIds) {
            this._mapMidTransceiver.delete(localId);
        }
    }
    async pauseReceiving(localIds) {
        this.assertNotClosed();
        this.assertRecvDirection();
        for (const localId of localIds) {
            logger.debug('pauseReceiving() [localId:%s]', localId);
            const transceiver = this._mapMidTransceiver.get(localId);
            if (!transceiver) {
                throw new Error('associated RTCRtpTransceiver not found');
            }
            transceiver.direction = 'inactive';
            this._remoteSdp.pauseMediaSection(localId);
        }
        const offer = {
            type: 'offer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('pauseReceiving() | calling pc.setRemoteDescription() [offer:%o]', offer);
        await this._pc.setRemoteDescription(offer);
        const answer = await this._pc.createAnswer();
        logger.debug('pauseReceiving() | calling pc.setLocalDescription() [answer:%o]', answer);
        await this._pc.setLocalDescription(answer);
    }
    async resumeReceiving(localIds) {
        this.assertNotClosed();
        this.assertRecvDirection();
        for (const localId of localIds) {
            logger.debug('resumeReceiving() [localId:%s]', localId);
            const transceiver = this._mapMidTransceiver.get(localId);
            if (!transceiver) {
                throw new Error('associated RTCRtpTransceiver not found');
            }
            transceiver.direction = 'recvonly';
            this._remoteSdp.resumeReceivingMediaSection(localId);
        }
        const offer = {
            type: 'offer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('resumeReceiving() | calling pc.setRemoteDescription() [offer:%o]', offer);
        await this._pc.setRemoteDescription(offer);
        const answer = await this._pc.createAnswer();
        logger.debug('resumeReceiving() | calling pc.setLocalDescription() [answer:%o]', answer);
        await this._pc.setLocalDescription(answer);
    }
    async getReceiverStats(localId) {
        this.assertNotClosed();
        this.assertRecvDirection();
        const transceiver = this._mapMidTransceiver.get(localId);
        if (!transceiver) {
            throw new Error('associated RTCRtpTransceiver not found');
        }
        return transceiver.receiver.getStats();
    }
    async receiveDataChannel({ maxMessageSize, sctpStreamParameters, label, protocol, }) {
        this.assertNotClosed();
        this.assertRecvDirection();
        const { streamId, ordered, maxPacketLifeTime, maxRetransmits, } = sctpStreamParameters;
        const options = {
            negotiated: true,
            id: streamId,
            ordered,
            maxPacketLifeTime,
            maxRetransmits,
            protocol,
        };
        logger.debug('receiveDataChannel() [options:%o]', options);
        const dataChannel = this._pc.createDataChannel(label, options);
        // If this is the first DataChannel we need to create the SDP offer with
        // m=application section.
        if (!this._hasDataChannelMediaSection) {
            this._remoteSdp.receiveSctpAssociation();
            const offer = {
                type: 'offer',
                sdp: this._remoteSdp.getSdp(),
            };
            logger.debug('receiveDataChannel() | calling pc.setRemoteDescription() [offer:%o]', offer);
            await this._pc.setRemoteDescription(offer);
            let answer = await this._pc.createAnswer();
            const localSdpObject = sdpTransform.parse(answer.sdp);
            const answerMediaObject = localSdpObject.media.find(m => m.type === 'application');
            if (typeof maxMessageSize === 'number') {
                answerMediaObject.maxMessageSize = maxMessageSize;
            }
            if (!this._transportReady) {
                await this.setupTransport({
                    localDtlsRole: this._forcedLocalDtlsRole ?? 'client',
                    localSdpObject,
                });
            }
            answer = {
                type: 'answer',
                sdp: sdpTransform.write(localSdpObject),
            };
            logger.debug('receiveDataChannel() | calling pc.setLocalDescription() [answer:%o]', answer);
            await this._pc.setLocalDescription(answer);
            this._hasDataChannelMediaSection = true;
        }
        return { dataChannel };
    }
    getDataChannelMaxMessageSize() {
        return this._direction === 'send'
            ? this._pc.sctp?.maxMessageSize
            : this._sctpMaxReceiveMessageSize;
    }
    async setupTransport({ localDtlsRole, localSdpObject, }) {
        if (!localSdpObject) {
            localSdpObject = sdpTransform.parse(this._pc.localDescription.sdp);
        }
        // Get our local DTLS parameters.
        const dtlsParameters = sdpCommonUtils.extractDtlsParameters({
            sdpObject: localSdpObject,
        });
        // Set our DTLS role.
        dtlsParameters.role = localDtlsRole;
        // Update the remote DTLS role in the SDP.
        this._remoteSdp.updateDtlsRole(localDtlsRole === 'client' ? 'server' : 'client');
        // Need to tell the remote transport about our parameters.
        await new Promise((resolve, reject) => {
            this.safeEmit('@connect', { dtlsParameters }, resolve, reject);
        });
        this._transportReady = true;
    }
    onIceGatheringStateChange = () => {
        this.emit('@icegatheringstatechange', this._pc.iceGatheringState);
    };
    onIceCandidateError = (event) => {
        this.emit('@icecandidateerror', event);
    };
    onConnectionStateChange = () => {
        this.emit('@connectionstatechange', this._pc.connectionState);
    };
    onIceConnectionStateChange = () => {
        switch (this._pc.iceConnectionState) {
            case 'checking': {
                this.emit('@connectionstatechange', 'connecting');
                break;
            }
            case 'connected':
            case 'completed': {
                this.emit('@connectionstatechange', 'connected');
                break;
            }
            case 'failed': {
                this.emit('@connectionstatechange', 'failed');
                break;
            }
            case 'disconnected': {
                this.emit('@connectionstatechange', 'disconnected');
                break;
            }
            case 'closed': {
                this.emit('@connectionstatechange', 'closed');
                break;
            }
        }
    };
    assertNotClosed() {
        if (this._closed) {
            throw new errors_1.InvalidStateError('method called in a closed handler');
        }
    }
    assertSendDirection() {
        if (this._direction !== 'send') {
            throw new Error('method can just be called for handlers with "send" direction');
        }
    }
    assertRecvDirection() {
        if (this._direction !== 'recv') {
            throw new Error('method can just be called for handlers with "recv" direction');
        }
    }
}
exports.Chrome74 = Chrome74;

},
17: function(module, exports, __req) {
"use strict";
var __createBinding = (this && this.__createBinding) || (Object.create ? (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    var desc = Object.getOwnPropertyDescriptor(m, k);
    if (!desc || ("get" in desc ? !m.__esModule : desc.writable || desc.configurable)) {
      desc = { enumerable: true, get: function() { return m[k]; } };
    }
    Object.defineProperty(o, k2, desc);
}) : (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    o[k2] = m[k];
}));
var __setModuleDefault = (this && this.__setModuleDefault) || (Object.create ? (function(o, v) {
    Object.defineProperty(o, "default", { enumerable: true, value: v });
}) : function(o, v) {
    o["default"] = v;
});
var __importStar = (this && this.__importStar) || (function () {
    var ownKeys = function(o) {
        ownKeys = Object.getOwnPropertyNames || function (o) {
            var ar = [];
            for (var k in o) if (Object.prototype.hasOwnProperty.call(o, k)) ar[ar.length] = k;
            return ar;
        };
        return ownKeys(o);
    };
    return function (mod) {
        if (mod && mod.__esModule) return mod;
        var result = {};
        if (mod != null) for (var k = ownKeys(mod), i = 0; i < k.length; i++) if (k[i] !== "default") __createBinding(result, mod, k[i]);
        __setModuleDefault(result, mod);
        return result;
    };
})();
Object.defineProperty(exports, "__esModule", { value: true });
exports.Firefox120 = void 0;
const sdpTransform = __importStar(__req(30));
const enhancedEvents_1 = __req(7);
const Logger_1 = __req(11);
const errors_1 = __req(12);
const ortc = __importStar(__req(6));
const scalabilityModes_1 = __req(5);
const RemoteSdp_1 = __req(31);
const sdpCommonUtils = __importStar(__req(32));
const sdpUnifiedPlanUtils = __importStar(__req(33));
const ortcUtils = __importStar(__req(34));
const logger = new Logger_1.Logger('Firefox120');
const NAME = 'Firefox120';
class Firefox120 extends enhancedEvents_1.EnhancedEventEmitter {
    // Closed flag.
    _closed = false;
    // Handler direction.
    _direction;
    // DataChannel max receive message size.
    _sctpMaxReceiveMessageSize;
    // Remote SDP handler.
    _remoteSdp;
    // Callback to request sending extended RTP capabilities on demand.
    _getSendExtendedRtpCapabilities;
    _forcedRtpExtensions;
    // RTCPeerConnection instance.
    _pc;
    // Map of RTCTransceivers indexed by MID.
    _mapMidTransceiver = new Map();
    // Default local stream for sending if no `streamId` is given in send().
    _sendStream = new MediaStream();
    // Whether a DataChannel m=application section has been created.
    _hasDataChannelMediaSection = false;
    // Sending DataChannel id value counter. Incremented for each new DataChannel.
    _nextSendSctpStreamId = 0;
    // Got transport local and remote parameters.
    _transportReady = false;
    /**
     * Creates a factory function.
     */
    static createFactory({ forcedRtpExtensions, }) {
        return {
            name: NAME,
            factory: (options) => new Firefox120({ ...options, forcedRtpExtensions }),
            getNativeRtpCapabilities: async ({ direction, }) => {
                logger.debug('getNativeRtpCapabilities() [direction:%o]', direction);
                let pc = new RTCPeerConnection({
                    iceServers: [],
                    iceTransportPolicy: 'all',
                    bundlePolicy: 'max-bundle',
                    rtcpMuxPolicy: 'require',
                });
                // NOTE: We need to add a real video track to get the RID extension mapping,
                // otherwiser Firefox doesn't include it in the SDP.
                const canvas = document.createElement('canvas');
                // NOTE: Otherwise Firefox fails in next line.
                canvas.getContext('2d');
                const fakeStream = canvas.captureStream();
                const fakeVideoTrack = fakeStream.getVideoTracks()[0];
                try {
                    const audioTransceiver = pc.addTransceiver('audio', { direction });
                    if (forcedRtpExtensions) {
                        ortcUtils.applyForcedRtpExtensions(audioTransceiver, forcedRtpExtensions);
                    }
                    const videoTransceiver = pc.addTransceiver(fakeVideoTrack, {
                        direction,
                        sendEncodings: [
                            { rid: 'r0', maxBitrate: 100000 },
                            { rid: 'r1', maxBitrate: 500000 },
                        ],
                    });
                    if (forcedRtpExtensions) {
                        ortcUtils.applyForcedRtpExtensions(videoTransceiver, forcedRtpExtensions);
                    }
                    const offer = await pc.createOffer();
                    try {
                        canvas.remove();
                    }
                    catch (error) { }
                    try {
                        fakeVideoTrack.stop();
                    }
                    catch (error) { }
                    try {
                        pc.close();
                    }
                    catch (error) { }
                    pc = undefined;
                    const sdpObject = sdpTransform.parse(offer.sdp);
                    const nativeRtpCapabilities = Firefox120.getLocalRtpCapabilities(sdpObject);
                    return nativeRtpCapabilities;
                }
                catch (error) {
                    try {
                        canvas.remove();
                    }
                    catch (error2) { }
                    try {
                        fakeVideoTrack.stop();
                    }
                    catch (error2) { }
                    try {
                        pc?.close();
                    }
                    catch (error2) { }
                    // eslint-disable-next-line no-useless-assignment
                    pc = undefined;
                    throw error;
                }
            },
        };
    }
    static getLocalRtpCapabilities(localSdpObject) {
        const nativeRtpCapabilities = sdpCommonUtils.extractRtpCapabilities({
            sdpObject: localSdpObject,
        });
        // Need to validate and normalize native RTP capabilities.
        ortc.validateAndNormalizeRtpCapabilities(nativeRtpCapabilities);
        return nativeRtpCapabilities;
    }
    constructor({ direction, iceParameters, iceCandidates, dtlsParameters, sctpParameters, iceServers, iceTransportPolicy, additionalSettings, getSendExtendedRtpCapabilities, forcedRtpExtensions, }) {
        super();
        logger.debug('constructor()');
        this._direction = direction;
        // NOTE: Mirror it.
        this._sctpMaxReceiveMessageSize = sctpParameters?.maxSendMessageSize;
        this._remoteSdp = new RemoteSdp_1.RemoteSdp({
            iceParameters,
            iceCandidates,
            dtlsParameters,
            sctpParameters,
        });
        this._getSendExtendedRtpCapabilities = getSendExtendedRtpCapabilities;
        this._forcedRtpExtensions = forcedRtpExtensions;
        this._pc = new RTCPeerConnection({
            iceServers: iceServers ?? [],
            iceTransportPolicy: iceTransportPolicy ?? 'all',
            bundlePolicy: 'max-bundle',
            rtcpMuxPolicy: 'require',
            ...additionalSettings,
        });
        this._pc.addEventListener('icegatheringstatechange', this.onIceGatheringStateChange);
        this._pc.addEventListener('icecandidateerror', this.onIceCandidateError);
        if (this._pc.connectionState) {
            this._pc.addEventListener('connectionstatechange', this.onConnectionStateChange);
        }
        else {
            logger.warn('run() | pc.connectionState not supported, using pc.iceConnectionState');
            this._pc.addEventListener('iceconnectionstatechange', this.onIceConnectionStateChange);
        }
    }
    get name() {
        return NAME;
    }
    close() {
        logger.debug('close()');
        if (this._closed) {
            return;
        }
        this._closed = true;
        // Close RTCPeerConnection.
        try {
            this._pc.close();
        }
        catch (error) { }
        this._pc.removeEventListener('icegatheringstatechange', this.onIceGatheringStateChange);
        this._pc.removeEventListener('icecandidateerror', this.onIceCandidateError);
        this._pc.removeEventListener('connectionstatechange', this.onConnectionStateChange);
        this._pc.removeEventListener('iceconnectionstatechange', this.onIceConnectionStateChange);
        this.emit('@close');
        // Invoke close() in EnhancedEventEmitter classes.
        super.close();
    }
    // eslint-disable-next-line @typescript-eslint/no-unused-vars
    async updateIceServers(iceServers) {
        this.assertNotClosed();
        // NOTE: Firefox does not implement pc.setConfiguration().
        throw new errors_1.UnsupportedError('not supported');
    }
    async restartIce(iceParameters) {
        this.assertNotClosed();
        logger.debug('restartIce()');
        // Provide the remote SDP handler with new remote ICE parameters.
        this._remoteSdp.updateIceParameters(iceParameters);
        if (!this._transportReady) {
            return;
        }
        if (this._direction === 'send') {
            const offer = await this._pc.createOffer({ iceRestart: true });
            logger.debug('restartIce() | calling pc.setLocalDescription() [offer:%o]', offer);
            await this._pc.setLocalDescription(offer);
            const answer = {
                type: 'answer',
                sdp: this._remoteSdp.getSdp(),
            };
            logger.debug('restartIce() | calling pc.setRemoteDescription() [answer:%o]', answer);
            await this._pc.setRemoteDescription(answer);
        }
        else {
            const offer = {
                type: 'offer',
                sdp: this._remoteSdp.getSdp(),
            };
            logger.debug('restartIce() | calling pc.setRemoteDescription() [offer:%o]', offer);
            await this._pc.setRemoteDescription(offer);
            const answer = await this._pc.createAnswer();
            logger.debug('restartIce() | calling pc.setLocalDescription() [answer:%o]', answer);
            await this._pc.setLocalDescription(answer);
        }
    }
    async getTransportStats() {
        this.assertNotClosed();
        return this._pc.getStats();
    }
    async send({ track, streamId, encodings, codecOptions, codec, onRtpSender, }) {
        this.assertNotClosed();
        this.assertSendDirection();
        logger.debug('send() [kind:%s, track.id:%s, streamId:%s]', track.kind, track.id, streamId);
        if (encodings && encodings.length > 1) {
            encodings.forEach((encoding, idx) => {
                encoding.rid = `r${idx}`;
            });
        }
        // NOTE: Firefox fails sometimes to properly anticipate the closed media
        // section that it should use, so don't reuse closed media sections.
        //   https://github.com/versatica/mediasoup-client/issues/104
        //
        // const mediaSectionIdx = this._remoteSdp.getNextMediaSectionIdx();
        const transceiver = this._pc.addTransceiver(track, {
            direction: 'sendonly',
            streams: [this._sendStream],
            sendEncodings: encodings,
        });
        try {
            if (this._forcedRtpExtensions) {
                ortcUtils.applyForcedRtpExtensions(transceiver, this._forcedRtpExtensions);
            }
            if (onRtpSender) {
                onRtpSender(transceiver.sender);
            }
            const offer = await this._pc.createOffer();
            let localSdpObject = sdpTransform.parse(offer.sdp);
            if (localSdpObject.extmapAllowMixed) {
                this._remoteSdp.setSessionExtmapAllowMixed();
            }
            const nativeRtpCapabilities = Firefox120.getLocalRtpCapabilities(localSdpObject);
            const sendExtendedRtpCapabilities = this._getSendExtendedRtpCapabilities(nativeRtpCapabilities);
            // Generic sending RTP parameters.
            const sendingRtpParameters = ortc.getSendingRtpParameters(track.kind, sendExtendedRtpCapabilities);
            // This may throw.
            sendingRtpParameters.codecs = ortc.reduceCodecs(sendingRtpParameters.codecs, codec);
            // Generic sending RTP parameters suitable for the SDP remote answer.
            const sendingRemoteRtpParameters = ortc.getSendingRemoteRtpParameters(track.kind, sendExtendedRtpCapabilities);
            // This may throw.
            sendingRemoteRtpParameters.codecs = ortc.reduceCodecs(sendingRemoteRtpParameters.codecs, codec);
            // In Firefox use DTLS role client even if we are the "offerer" since
            // Firefox does not respect ICE-Lite.
            if (!this._transportReady) {
                await this.setupTransport({ localDtlsRole: 'client', localSdpObject });
            }
            const layers = (0, scalabilityModes_1.parse)((encodings ?? [{}])[0].scalabilityMode);
            logger.debug('send() | calling pc.setLocalDescription() [offer:%o]', offer);
            await this._pc.setLocalDescription(offer);
            // We can now get the transceiver.mid.
            const localId = transceiver.mid;
            // Set MID.
            sendingRtpParameters.mid = localId;
            localSdpObject = sdpTransform.parse(this._pc.localDescription.sdp);
            const offerMediaObject = localSdpObject.media[localSdpObject.media.length - 1];
            // Set RTCP CNAME.
            sendingRtpParameters.rtcp.cname = sdpCommonUtils.getCname({
                offerMediaObject,
            });
            // Set msid.
            sendingRtpParameters.msid = `${streamId ?? this._sendStream.id} ${track.id}`;
            // Set RTP encodings by parsing the SDP offer if no encodings are given.
            if (!encodings) {
                sendingRtpParameters.encodings = sdpUnifiedPlanUtils.getRtpEncodings({
                    offerMediaObject,
                    codecs: sendingRtpParameters.codecs,
                });
            }
            // Set RTP encodings by parsing the SDP offer and complete them with given
            // one if just a single encoding has been given.
            else if (encodings.length === 1) {
                const newEncodings = sdpUnifiedPlanUtils.getRtpEncodings({
                    offerMediaObject,
                    codecs: sendingRtpParameters.codecs,
                });
                Object.assign(newEncodings[0], encodings[0]);
                sendingRtpParameters.encodings = newEncodings;
            }
            // Otherwise if more than 1 encoding are given use them verbatim.
            else {
                sendingRtpParameters.encodings = encodings;
            }
            // If VP8 or H264 and there is effective simulcast, add scalabilityMode to
            // each encoding.
            if (sendingRtpParameters.encodings.length > 1 &&
                (sendingRtpParameters.codecs[0].mimeType.toLowerCase() ===
                    'video/vp8' ||
                    sendingRtpParameters.codecs[0].mimeType.toLowerCase() ===
                        'video/h264')) {
                for (const encoding of sendingRtpParameters.encodings) {
                    if (encoding.scalabilityMode) {
                        encoding.scalabilityMode = `L1T${layers.temporalLayers}`;
                    }
                    else {
                        encoding.scalabilityMode = 'L1T3';
                    }
                }
            }
            this._remoteSdp.send({
                offerMediaObject,
                offerRtpParameters: sendingRtpParameters,
                answerRtpParameters: sendingRemoteRtpParameters,
                codecOptions,
            });
            const answer = {
                type: 'answer',
                sdp: this._remoteSdp.getSdp(),
            };
            logger.debug('send() | calling pc.setRemoteDescription() [answer:%o]', answer);
            await this._pc.setRemoteDescription(answer);
            // Store in the map.
            this._mapMidTransceiver.set(localId, transceiver);
            return {
                localId,
                rtpParameters: sendingRtpParameters,
                rtpSender: transceiver.sender,
            };
        }
        catch (error) {
            // Undo the changes done in the PeerConnection, otherwise it gets out of
            // sync with the remote SDP and later calls to send() fail.
            try {
                if (this._pc.signalingState === 'have-local-offer') {
                    try {
                        await this._pc.setLocalDescription({ type: 'rollback' });
                    }
                    catch (error2) { }
                }
                transceiver.stop();
            }
            catch (error3) { }
            throw error;
        }
    }
    async stopSending(localId) {
        this.assertSendDirection();
        logger.debug('stopSending() [localId:%s]', localId);
        if (this._closed) {
            return;
        }
        const transceiver = this._mapMidTransceiver.get(localId);
        if (!transceiver) {
            throw new Error('associated transceiver not found');
        }
        void transceiver.sender.replaceTrack(null);
        // NOTE: Cannot use stop() the transceiver due to the the note above in
        // send() method.
        // try
        // {
        // 	transceiver.stop();
        // }
        // catch (error)
        // {}
        this._pc.removeTrack(transceiver.sender);
        // NOTE: Cannot use closeMediaSection() due to the the note above in send()
        // method.
        // this._remoteSdp.closeMediaSection(transceiver.mid);
        this._remoteSdp.disableMediaSection(transceiver.mid);
        const offer = await this._pc.createOffer();
        logger.debug('stopSending() | calling pc.setLocalDescription() [offer:%o]', offer);
        await this._pc.setLocalDescription(offer);
        const answer = {
            type: 'answer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('stopSending() | calling pc.setRemoteDescription() [answer:%o]', answer);
        await this._pc.setRemoteDescription(answer);
        this._mapMidTransceiver.delete(localId);
    }
    async pauseSending(localId) {
        this.assertNotClosed();
        this.assertSendDirection();
        logger.debug('pauseSending() [localId:%s]', localId);
        const transceiver = this._mapMidTransceiver.get(localId);
        if (!transceiver) {
            throw new Error('associated RTCRtpTransceiver not found');
        }
        transceiver.direction = 'inactive';
        this._remoteSdp.pauseMediaSection(localId);
        const offer = await this._pc.createOffer();
        logger.debug('pauseSending() | calling pc.setLocalDescription() [offer:%o]', offer);
        await this._pc.setLocalDescription(offer);
        const answer = {
            type: 'answer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('pauseSending() | calling pc.setRemoteDescription() [answer:%o]', answer);
        await this._pc.setRemoteDescription(answer);
    }
    async resumeSending(localId) {
        this.assertNotClosed();
        this.assertSendDirection();
        logger.debug('resumeSending() [localId:%s]', localId);
        const transceiver = this._mapMidTransceiver.get(localId);
        if (!transceiver) {
            throw new Error('associated RTCRtpTransceiver not found');
        }
        transceiver.direction = 'sendonly';
        this._remoteSdp.resumeSendingMediaSection(localId);
        const offer = await this._pc.createOffer();
        logger.debug('resumeSending() | calling pc.setLocalDescription() [offer:%o]', offer);
        await this._pc.setLocalDescription(offer);
        const answer = {
            type: 'answer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('resumeSending() | calling pc.setRemoteDescription() [answer:%o]', answer);
        await this._pc.setRemoteDescription(answer);
    }
    async replaceTrack(localId, track) {
        this.assertNotClosed();
        this.assertSendDirection();
        if (track) {
            logger.debug('replaceTrack() [localId:%s, track.id:%s]', localId, track.id);
        }
        else {
            logger.debug('replaceTrack() [localId:%s, no track]', localId);
        }
        const transceiver = this._mapMidTransceiver.get(localId);
        if (!transceiver) {
            throw new Error('associated RTCRtpTransceiver not found');
        }
        await transceiver.sender.replaceTrack(track);
    }
    async setMaxSpatialLayer(localId, spatialLayer) {
        this.assertNotClosed();
        this.assertSendDirection();
        logger.debug('setMaxSpatialLayer() [localId:%s, spatialLayer:%s]', localId, spatialLayer);
        const transceiver = this._mapMidTransceiver.get(localId);
        if (!transceiver) {
            throw new Error('associated transceiver not found');
        }
        const parameters = transceiver.sender.getParameters();
        parameters.encodings.forEach((encoding, idx) => {
            if (idx <= spatialLayer) {
                encoding.active = true;
            }
            else {
                encoding.active = false;
            }
        });
        await transceiver.sender.setParameters(parameters);
        this._remoteSdp.muxMediaSectionSimulcast(localId, parameters.encodings);
        const offer = await this._pc.createOffer();
        logger.debug('setMaxSpatialLayer() | calling pc.setLocalDescription() [offer:%o]', offer);
        await this._pc.setLocalDescription(offer);
        const answer = {
            type: 'answer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('setMaxSpatialLayer() | calling pc.setRemoteDescription() [answer:%o]', answer);
        await this._pc.setRemoteDescription(answer);
    }
    async setRtpEncodingParameters(localId, params) {
        this.assertNotClosed();
        this.assertSendDirection();
        logger.debug('setRtpEncodingParameters() [localId:%s, params:%o]', localId, params);
        const transceiver = this._mapMidTransceiver.get(localId);
        if (!transceiver) {
            throw new Error('associated RTCRtpTransceiver not found');
        }
        const parameters = transceiver.sender.getParameters();
        parameters.encodings.forEach((encoding, idx) => {
            parameters.encodings[idx] = { ...encoding, ...params };
        });
        await transceiver.sender.setParameters(parameters);
        this._remoteSdp.muxMediaSectionSimulcast(localId, parameters.encodings);
        const offer = await this._pc.createOffer();
        logger.debug('setRtpEncodingParameters() | calling pc.setLocalDescription() [offer:%o]', offer);
        await this._pc.setLocalDescription(offer);
        const answer = {
            type: 'answer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('setRtpEncodingParameters() | calling pc.setRemoteDescription() [answer:%o]', answer);
        await this._pc.setRemoteDescription(answer);
    }
    async getSenderStats(localId) {
        this.assertNotClosed();
        this.assertSendDirection();
        const transceiver = this._mapMidTransceiver.get(localId);
        if (!transceiver) {
            throw new Error('associated RTCRtpTransceiver not found');
        }
        return transceiver.sender.getStats();
    }
    async sendDataChannel({ sctpStreamParameters, }) {
        this.assertNotClosed();
        this.assertSendDirection();
        const options = {
            negotiated: true,
            id: this._nextSendSctpStreamId,
            ordered: sctpStreamParameters.ordered,
            maxPacketLifeTime: sctpStreamParameters.maxPacketLifeTime,
            maxRetransmits: sctpStreamParameters.maxRetransmits,
            protocol: sctpStreamParameters.protocol,
        };
        logger.debug('sendDataChannel() [options:%o]', options);
        const dataChannel = this._pc.createDataChannel(sctpStreamParameters.label, options);
        // Increase next id.
        this._nextSendSctpStreamId =
            ++this._nextSendSctpStreamId % (this._pc.sctp?.maxChannels ?? 65536);
        // If this is the first DataChannel we need to create the SDP answer with
        // m=application section.
        if (!this._hasDataChannelMediaSection) {
            try {
                const offer = await this._pc.createOffer();
                const localSdpObject = sdpTransform.parse(offer.sdp);
                const offerMediaObject = localSdpObject.media.find(m => m.type === 'application');
                if (!this._transportReady) {
                    await this.setupTransport({
                        localDtlsRole: 'client',
                        localSdpObject,
                    });
                }
                logger.debug('sendDataChannel() | calling pc.setLocalDescription() [offer:%o]', offer);
                await this._pc.setLocalDescription(offer);
                this._remoteSdp.sendSctpAssociation({ offerMediaObject });
                const answer = {
                    type: 'answer',
                    sdp: this._remoteSdp.getSdp(),
                };
                logger.debug('sendDataChannel() | calling pc.setRemoteDescription() [answer:%o]', answer);
                await this._pc.setRemoteDescription(answer);
                this._hasDataChannelMediaSection = true;
            }
            catch (error) {
                // Undo the changes done in the PeerConnection, otherwise it gets out of
                // sync with the remote SDP and later calls to send() and
                // sendDataChannel() fail.
                try {
                    if (this._pc.signalingState === 'have-local-offer') {
                        try {
                            await this._pc.setLocalDescription({ type: 'rollback' });
                        }
                        catch (error2) { }
                    }
                    dataChannel.close();
                }
                catch (error3) { }
                throw error;
            }
        }
        const newSctpStreamParameters = {
            streamId: options.id,
            ordered: options.ordered,
            maxPacketLifeTime: options.maxPacketLifeTime,
            maxRetransmits: options.maxRetransmits,
        };
        return { dataChannel, sctpStreamParameters: newSctpStreamParameters };
    }
    async receive(optionsList) {
        this.assertNotClosed();
        this.assertRecvDirection();
        const results = [];
        const mapLocalId = new Map();
        for (const options of optionsList) {
            const { trackId, kind, rtpParameters, streamId } = options;
            logger.debug('receive() [trackId:%s, kind:%s]', trackId, kind);
            const localId = rtpParameters.mid ?? String(this._mapMidTransceiver.size);
            mapLocalId.set(trackId, localId);
            // We ignore MSID `trackId` when consuming and always use our computed
            // `trackId` which matches the `consumer.id`.
            const { msidStreamId } = ortcUtils.getMsidStreamIdAndTrackId(rtpParameters.msid);
            this._remoteSdp.receive({
                mid: localId,
                kind,
                offerRtpParameters: rtpParameters,
                streamId: streamId ?? msidStreamId ?? rtpParameters.rtcp?.cname ?? '-',
                trackId,
            });
        }
        const offer = {
            type: 'offer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('receive() | calling pc.setRemoteDescription() [offer:%o]', offer);
        await this._pc.setRemoteDescription(offer);
        for (const options of optionsList) {
            const { trackId, onRtpReceiver } = options;
            if (onRtpReceiver) {
                const localId = mapLocalId.get(trackId);
                const transceiver = this._pc
                    .getTransceivers()
                    .find((t) => t.mid === localId);
                if (!transceiver) {
                    throw new Error('transceiver not found');
                }
                if (this._forcedRtpExtensions) {
                    ortcUtils.applyForcedRtpExtensions(transceiver, this._forcedRtpExtensions);
                }
                onRtpReceiver(transceiver.receiver);
            }
        }
        let answer = await this._pc.createAnswer();
        const localSdpObject = sdpTransform.parse(answer.sdp);
        for (const options of optionsList) {
            const { trackId, rtpParameters } = options;
            const localId = mapLocalId.get(trackId);
            const answerMediaObject = localSdpObject.media.find(m => String(m.mid) === localId);
            // May need to modify codec parameters in the answer based on codec
            // parameters in the offer.
            sdpCommonUtils.applyCodecParameters({
                offerRtpParameters: rtpParameters,
                answerMediaObject,
            });
            answer = {
                type: 'answer',
                sdp: sdpTransform.write(localSdpObject),
            };
        }
        if (!this._transportReady) {
            await this.setupTransport({ localDtlsRole: 'client', localSdpObject });
        }
        logger.debug('receive() | calling pc.setLocalDescription() [answer:%o]', answer);
        await this._pc.setLocalDescription(answer);
        for (const options of optionsList) {
            const { trackId } = options;
            const localId = mapLocalId.get(trackId);
            const transceiver = this._pc
                .getTransceivers()
                .find((t) => t.mid === localId);
            if (!transceiver) {
                throw new Error('new RTCRtpTransceiver not found');
            }
            // Store in the map.
            this._mapMidTransceiver.set(localId, transceiver);
            results.push({
                localId,
                track: transceiver.receiver.track,
                rtpReceiver: transceiver.receiver,
            });
        }
        return results;
    }
    async stopReceiving(localIds) {
        this.assertRecvDirection();
        if (this._closed) {
            return;
        }
        for (const localId of localIds) {
            logger.debug('stopReceiving() [localId:%s]', localId);
            const transceiver = this._mapMidTransceiver.get(localId);
            if (!transceiver) {
                throw new Error('associated RTCRtpTransceiver not found');
            }
            this._remoteSdp.closeMediaSection(transceiver.mid);
        }
        const offer = {
            type: 'offer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('stopReceiving() | calling pc.setRemoteDescription() [offer:%o]', offer);
        await this._pc.setRemoteDescription(offer);
        const answer = await this._pc.createAnswer();
        logger.debug('stopReceiving() | calling pc.setLocalDescription() [answer:%o]', answer);
        await this._pc.setLocalDescription(answer);
        for (const localId of localIds) {
            this._mapMidTransceiver.delete(localId);
        }
    }
    async pauseReceiving(localIds) {
        this.assertNotClosed();
        this.assertRecvDirection();
        for (const localId of localIds) {
            logger.debug('pauseReceiving() [localId:%s]', localId);
            const transceiver = this._mapMidTransceiver.get(localId);
            if (!transceiver) {
                throw new Error('associated RTCRtpTransceiver not found');
            }
            transceiver.direction = 'inactive';
            this._remoteSdp.pauseMediaSection(localId);
        }
        const offer = {
            type: 'offer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('pauseReceiving() | calling pc.setRemoteDescription() [offer:%o]', offer);
        await this._pc.setRemoteDescription(offer);
        const answer = await this._pc.createAnswer();
        logger.debug('pauseReceiving() | calling pc.setLocalDescription() [answer:%o]', answer);
        await this._pc.setLocalDescription(answer);
    }
    async resumeReceiving(localIds) {
        this.assertNotClosed();
        this.assertRecvDirection();
        for (const localId of localIds) {
            logger.debug('resumeReceiving() [localId:%s]', localId);
            const transceiver = this._mapMidTransceiver.get(localId);
            if (!transceiver) {
                throw new Error('associated RTCRtpTransceiver not found');
            }
            transceiver.direction = 'recvonly';
            this._remoteSdp.resumeReceivingMediaSection(localId);
        }
        const offer = {
            type: 'offer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('resumeReceiving() | calling pc.setRemoteDescription() [offer:%o]', offer);
        await this._pc.setRemoteDescription(offer);
        const answer = await this._pc.createAnswer();
        logger.debug('resumeReceiving() | calling pc.setLocalDescription() [answer:%o]', answer);
        await this._pc.setLocalDescription(answer);
    }
    async getReceiverStats(localId) {
        this.assertRecvDirection();
        const transceiver = this._mapMidTransceiver.get(localId);
        if (!transceiver) {
            throw new Error('associated RTCRtpTransceiver not found');
        }
        return transceiver.receiver.getStats();
    }
    async receiveDataChannel({ maxMessageSize, sctpStreamParameters, label, protocol, }) {
        this.assertNotClosed();
        this.assertRecvDirection();
        const { streamId, ordered, maxPacketLifeTime, maxRetransmits, } = sctpStreamParameters;
        const options = {
            negotiated: true,
            id: streamId,
            ordered,
            maxPacketLifeTime,
            maxRetransmits,
            protocol,
        };
        logger.debug('receiveDataChannel() [options:%o]', options);
        const dataChannel = this._pc.createDataChannel(label, options);
        // If this is the first DataChannel we need to create the SDP offer with
        // m=application section.
        if (!this._hasDataChannelMediaSection) {
            this._remoteSdp.receiveSctpAssociation();
            const offer = {
                type: 'offer',
                sdp: this._remoteSdp.getSdp(),
            };
            logger.debug('receiveDataChannel() | calling pc.setRemoteDescription() [offer:%o]', offer);
            await this._pc.setRemoteDescription(offer);
            let answer = await this._pc.createAnswer();
            const localSdpObject = sdpTransform.parse(answer.sdp);
            const answerMediaObject = localSdpObject.media.find(m => m.type === 'application');
            if (typeof maxMessageSize === 'number') {
                answerMediaObject.maxMessageSize = maxMessageSize;
            }
            if (!this._transportReady) {
                await this.setupTransport({ localDtlsRole: 'client', localSdpObject });
            }
            answer = {
                type: 'answer',
                sdp: sdpTransform.write(localSdpObject),
            };
            logger.debug('receiveDataChannel() | calling pc.setLocalDescription() [answer:%o]', answer);
            await this._pc.setLocalDescription(answer);
            this._hasDataChannelMediaSection = true;
        }
        return { dataChannel };
    }
    getDataChannelMaxMessageSize() {
        return this._direction === 'send'
            ? this._pc.sctp?.maxMessageSize
            : this._sctpMaxReceiveMessageSize;
    }
    async setupTransport({ localDtlsRole, localSdpObject, }) {
        if (!localSdpObject) {
            localSdpObject = sdpTransform.parse(this._pc.localDescription.sdp);
        }
        // Get our local DTLS parameters.
        const dtlsParameters = sdpCommonUtils.extractDtlsParameters({
            sdpObject: localSdpObject,
        });
        // Set our DTLS role.
        dtlsParameters.role = localDtlsRole;
        // Update the remote DTLS role in the SDP.
        this._remoteSdp.updateDtlsRole(localDtlsRole === 'client' ? 'server' : 'client');
        // Need to tell the remote transport about our parameters.
        await new Promise((resolve, reject) => {
            this.safeEmit('@connect', { dtlsParameters }, resolve, reject);
        });
        this._transportReady = true;
    }
    onIceGatheringStateChange = () => {
        this.emit('@icegatheringstatechange', this._pc.iceGatheringState);
    };
    onIceCandidateError = (event) => {
        this.emit('@icecandidateerror', event);
    };
    onConnectionStateChange = () => {
        this.emit('@connectionstatechange', this._pc.connectionState);
    };
    onIceConnectionStateChange = () => {
        switch (this._pc.iceConnectionState) {
            case 'checking': {
                this.emit('@connectionstatechange', 'connecting');
                break;
            }
            case 'connected':
            case 'completed': {
                this.emit('@connectionstatechange', 'connected');
                break;
            }
            case 'failed': {
                this.emit('@connectionstatechange', 'failed');
                break;
            }
            case 'disconnected': {
                this.emit('@connectionstatechange', 'disconnected');
                break;
            }
            case 'closed': {
                this.emit('@connectionstatechange', 'closed');
                break;
            }
        }
    };
    assertNotClosed() {
        if (this._closed) {
            throw new errors_1.InvalidStateError('method called in a closed handler');
        }
    }
    assertSendDirection() {
        if (this._direction !== 'send') {
            throw new Error('method can just be called for handlers with "send" direction');
        }
    }
    assertRecvDirection() {
        if (this._direction !== 'recv') {
            throw new Error('method can just be called for handlers with "recv" direction');
        }
    }
}
exports.Firefox120 = Firefox120;

},
18: function(module, exports, __req) {
"use strict";
var __createBinding = (this && this.__createBinding) || (Object.create ? (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    var desc = Object.getOwnPropertyDescriptor(m, k);
    if (!desc || ("get" in desc ? !m.__esModule : desc.writable || desc.configurable)) {
      desc = { enumerable: true, get: function() { return m[k]; } };
    }
    Object.defineProperty(o, k2, desc);
}) : (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    o[k2] = m[k];
}));
var __setModuleDefault = (this && this.__setModuleDefault) || (Object.create ? (function(o, v) {
    Object.defineProperty(o, "default", { enumerable: true, value: v });
}) : function(o, v) {
    o["default"] = v;
});
var __importStar = (this && this.__importStar) || (function () {
    var ownKeys = function(o) {
        ownKeys = Object.getOwnPropertyNames || function (o) {
            var ar = [];
            for (var k in o) if (Object.prototype.hasOwnProperty.call(o, k)) ar[ar.length] = k;
            return ar;
        };
        return ownKeys(o);
    };
    return function (mod) {
        if (mod && mod.__esModule) return mod;
        var result = {};
        if (mod != null) for (var k = ownKeys(mod), i = 0; i < k.length; i++) if (k[i] !== "default") __createBinding(result, mod, k[i]);
        __setModuleDefault(result, mod);
        return result;
    };
})();
Object.defineProperty(exports, "__esModule", { value: true });
exports.Safari12 = void 0;
const sdpTransform = __importStar(__req(30));
const enhancedEvents_1 = __req(7);
const Logger_1 = __req(11);
const ortc = __importStar(__req(6));
const errors_1 = __req(12);
const scalabilityModes_1 = __req(5);
const RemoteSdp_1 = __req(31);
const sdpCommonUtils = __importStar(__req(32));
const sdpUnifiedPlanUtils = __importStar(__req(33));
const ortcUtils = __importStar(__req(34));
const logger = new Logger_1.Logger('Safari12');
const NAME = 'Safari12';
class Safari12 extends enhancedEvents_1.EnhancedEventEmitter {
    // Closed flag.
    _closed = false;
    // Handler direction.
    _direction;
    // DataChannel max receive message size.
    _sctpMaxReceiveMessageSize;
    // Remote SDP handler.
    _remoteSdp;
    // Callback to request sending extended RTP capabilities on demand.
    _getSendExtendedRtpCapabilities;
    _forcedRtpExtensions;
    // Initial server side DTLS role. If not 'auto', it will force the opposite
    // value in client side.
    _forcedLocalDtlsRole;
    // RTCPeerConnection instance.
    _pc;
    // Map of RTCTransceivers indexed by MID.
    _mapMidTransceiver = new Map();
    // Default local stream for sending if no `streamId` is given in send().
    _sendStream = new MediaStream();
    // Whether a DataChannel m=application section has been created.
    _hasDataChannelMediaSection = false;
    // Sending DataChannel id value counter. Incremented for each new DataChannel.
    _nextSendSctpStreamId = 0;
    // Got transport local and remote parameters.
    _transportReady = false;
    /**
     * Creates a factory function.
     */
    static createFactory({ forcedRtpExtensions, }) {
        return {
            name: NAME,
            factory: (options) => new Safari12({ ...options, forcedRtpExtensions }),
            getNativeRtpCapabilities: async ({ direction, }) => {
                logger.debug('getNativeRtpCapabilities() [direction:%o]', direction);
                let pc = new RTCPeerConnection({
                    iceServers: [],
                    iceTransportPolicy: 'all',
                    bundlePolicy: 'max-bundle',
                    rtcpMuxPolicy: 'require',
                });
                try {
                    const audioTransceiver = pc.addTransceiver('audio', { direction });
                    if (forcedRtpExtensions) {
                        ortcUtils.applyForcedRtpExtensions(audioTransceiver, forcedRtpExtensions);
                    }
                    const videoTransceiver = pc.addTransceiver('video', { direction });
                    if (forcedRtpExtensions) {
                        ortcUtils.applyForcedRtpExtensions(videoTransceiver, forcedRtpExtensions);
                    }
                    const offer = await pc.createOffer();
                    try {
                        pc.close();
                    }
                    catch (error) { }
                    pc = undefined;
                    const sdpObject = sdpTransform.parse(offer.sdp);
                    const nativeRtpCapabilities = Safari12.getLocalRtpCapabilities(sdpObject);
                    return nativeRtpCapabilities;
                }
                catch (error) {
                    try {
                        pc?.close();
                    }
                    catch (error2) { }
                    // eslint-disable-next-line no-useless-assignment
                    pc = undefined;
                    throw error;
                }
            },
        };
    }
    static getLocalRtpCapabilities(localSdpObject) {
        const nativeRtpCapabilities = sdpCommonUtils.extractRtpCapabilities({
            sdpObject: localSdpObject,
        });
        // Need to validate and normalize native RTP capabilities.
        ortc.validateAndNormalizeRtpCapabilities(nativeRtpCapabilities);
        // libwebrtc supports NACK for OPUS but doesn't announce it.
        ortcUtils.addNackSupportForOpus(nativeRtpCapabilities);
        return nativeRtpCapabilities;
    }
    constructor({ direction, iceParameters, iceCandidates, dtlsParameters, sctpParameters, iceServers, iceTransportPolicy, additionalSettings, getSendExtendedRtpCapabilities, forcedRtpExtensions, }) {
        super();
        logger.debug('constructor()');
        this._direction = direction;
        // NOTE: Mirror it.
        this._sctpMaxReceiveMessageSize = sctpParameters?.maxSendMessageSize;
        this._remoteSdp = new RemoteSdp_1.RemoteSdp({
            iceParameters,
            iceCandidates,
            dtlsParameters,
            sctpParameters,
        });
        this._getSendExtendedRtpCapabilities = getSendExtendedRtpCapabilities;
        this._forcedRtpExtensions = forcedRtpExtensions;
        if (dtlsParameters.role && dtlsParameters.role !== 'auto') {
            this._forcedLocalDtlsRole =
                dtlsParameters.role === 'server' ? 'client' : 'server';
        }
        this._pc = new RTCPeerConnection({
            iceServers: iceServers ?? [],
            iceTransportPolicy: iceTransportPolicy ?? 'all',
            bundlePolicy: 'max-bundle',
            rtcpMuxPolicy: 'require',
            ...additionalSettings,
        });
        this._pc.addEventListener('icegatheringstatechange', () => {
            this.emit('@icegatheringstatechange', this._pc.iceGatheringState);
        });
        this._pc.addEventListener('icecandidateerror', (event) => {
            this.emit('@icecandidateerror', event);
        });
        this._pc.addEventListener('icegatheringstatechange', this.onIceGatheringStateChange);
        this._pc.addEventListener('icecandidateerror', this.onIceCandidateError);
        if (this._pc.connectionState) {
            this._pc.addEventListener('connectionstatechange', this.onConnectionStateChange);
        }
        else {
            logger.warn('run() | pc.connectionState not supported, using pc.iceConnectionState');
            this._pc.addEventListener('iceconnectionstatechange', this.onIceConnectionStateChange);
        }
    }
    get name() {
        return NAME;
    }
    close() {
        logger.debug('close()');
        if (this._closed) {
            return;
        }
        this._closed = true;
        // Close RTCPeerConnection.
        try {
            this._pc.close();
        }
        catch (error) { }
        this._pc.removeEventListener('icegatheringstatechange', this.onIceGatheringStateChange);
        this._pc.removeEventListener('icecandidateerror', this.onIceCandidateError);
        this._pc.removeEventListener('connectionstatechange', this.onConnectionStateChange);
        this._pc.removeEventListener('iceconnectionstatechange', this.onIceConnectionStateChange);
        this.emit('@close');
        // Invoke close() in EnhancedEventEmitter classes.
        super.close();
    }
    async updateIceServers(iceServers) {
        this.assertNotClosed();
        logger.debug('updateIceServers()');
        const configuration = this._pc.getConfiguration();
        configuration.iceServers = iceServers;
        this._pc.setConfiguration(configuration);
    }
    async restartIce(iceParameters) {
        this.assertNotClosed();
        logger.debug('restartIce()');
        // Provide the remote SDP handler with new remote ICE parameters.
        this._remoteSdp.updateIceParameters(iceParameters);
        if (!this._transportReady) {
            return;
        }
        if (this._direction === 'send') {
            const offer = await this._pc.createOffer({ iceRestart: true });
            logger.debug('restartIce() | calling pc.setLocalDescription() [offer:%o]', offer);
            await this._pc.setLocalDescription(offer);
            const answer = {
                type: 'answer',
                sdp: this._remoteSdp.getSdp(),
            };
            logger.debug('restartIce() | calling pc.setRemoteDescription() [answer:%o]', answer);
            await this._pc.setRemoteDescription(answer);
        }
        else {
            const offer = {
                type: 'offer',
                sdp: this._remoteSdp.getSdp(),
            };
            logger.debug('restartIce() | calling pc.setRemoteDescription() [offer:%o]', offer);
            await this._pc.setRemoteDescription(offer);
            const answer = await this._pc.createAnswer();
            logger.debug('restartIce() | calling pc.setLocalDescription() [answer:%o]', answer);
            await this._pc.setLocalDescription(answer);
        }
    }
    async getTransportStats() {
        this.assertNotClosed();
        return this._pc.getStats();
    }
    async send({ track, streamId, encodings, codecOptions, codec, onRtpSender, }) {
        this.assertNotClosed();
        this.assertSendDirection();
        logger.debug('send() [kind:%s, track.id:%s, streamId:%s]', track.kind, track.id, streamId);
        if (encodings && encodings.length > 1) {
            // Set rid and verify scalabilityMode in each encoding.
            // NOTE: Even if WebRTC allows different scalabilityMode (different number
            // of temporal layers) per simulcast stream, we need that those are the
            // same in all them, so let's pick up the highest value.
            // NOTE: If scalabilityMode is not given, Safari will use L1T3.
            let maxTemporalLayers = 1;
            for (const encoding of encodings) {
                const temporalLayers = encoding.scalabilityMode
                    ? (0, scalabilityModes_1.parse)(encoding.scalabilityMode).temporalLayers
                    : 3;
                if (temporalLayers > maxTemporalLayers) {
                    maxTemporalLayers = temporalLayers;
                }
            }
            encodings.forEach((encoding, idx) => {
                encoding.rid = `r${idx}`;
                encoding.scalabilityMode = `L1T${maxTemporalLayers}`;
            });
        }
        const mediaSectionIdx = this._remoteSdp.getNextMediaSectionIdx();
        const transceiver = this._pc.addTransceiver(track, {
            direction: 'sendonly',
            streams: [this._sendStream],
            sendEncodings: encodings,
        });
        try {
            if (this._forcedRtpExtensions) {
                ortcUtils.applyForcedRtpExtensions(transceiver, this._forcedRtpExtensions);
            }
            if (onRtpSender) {
                onRtpSender(transceiver.sender);
            }
            const offer = await this._pc.createOffer();
            let localSdpObject = sdpTransform.parse(offer.sdp);
            if (localSdpObject.extmapAllowMixed) {
                this._remoteSdp.setSessionExtmapAllowMixed();
            }
            const nativeRtpCapabilities = Safari12.getLocalRtpCapabilities(localSdpObject);
            const sendExtendedRtpCapabilities = this._getSendExtendedRtpCapabilities(nativeRtpCapabilities);
            // Generic sending RTP parameters.
            const sendingRtpParameters = ortc.getSendingRtpParameters(track.kind, sendExtendedRtpCapabilities);
            // This may throw.
            sendingRtpParameters.codecs = ortc.reduceCodecs(sendingRtpParameters.codecs, codec);
            // Generic sending RTP parameters suitable for the SDP remote answer.
            const sendingRemoteRtpParameters = ortc.getSendingRemoteRtpParameters(track.kind, sendExtendedRtpCapabilities);
            // This may throw.
            sendingRemoteRtpParameters.codecs = ortc.reduceCodecs(sendingRemoteRtpParameters.codecs, codec);
            if (!this._transportReady) {
                await this.setupTransport({
                    localDtlsRole: this._forcedLocalDtlsRole ?? 'client',
                    localSdpObject,
                });
            }
            logger.debug('send() | calling pc.setLocalDescription() [offer:%o]', offer);
            await this._pc.setLocalDescription(offer);
            // We can now get the transceiver.mid.
            const localId = transceiver.mid;
            // Set MID.
            sendingRtpParameters.mid = localId;
            localSdpObject = sdpTransform.parse(this._pc.localDescription.sdp);
            const offerMediaObject = localSdpObject.media[mediaSectionIdx.idx];
            // Set RTCP CNAME.
            sendingRtpParameters.rtcp.cname = sdpCommonUtils.getCname({
                offerMediaObject,
            });
            // Set msid.
            sendingRtpParameters.msid = `${streamId ?? this._sendStream.id} ${track.id}`;
            // Set RTP encodings by parsing the SDP offer if no encodings are given.
            if (!encodings) {
                sendingRtpParameters.encodings = sdpUnifiedPlanUtils.getRtpEncodings({
                    offerMediaObject,
                    codecs: sendingRtpParameters.codecs,
                });
            }
            // Set RTP encodings by parsing the SDP offer and complete them with given
            // one if just a single encoding has been given.
            else if (encodings.length === 1) {
                const newEncodings = sdpUnifiedPlanUtils.getRtpEncodings({
                    offerMediaObject,
                    codecs: sendingRtpParameters.codecs,
                });
                Object.assign(newEncodings[0], encodings[0]);
                sendingRtpParameters.encodings = newEncodings;
            }
            // Otherwise if more than 1 encoding are given use them verbatim.
            else {
                sendingRtpParameters.encodings = encodings;
            }
            this._remoteSdp.send({
                offerMediaObject,
                reuseMid: mediaSectionIdx.reuseMid,
                offerRtpParameters: sendingRtpParameters,
                answerRtpParameters: sendingRemoteRtpParameters,
                codecOptions,
            });
            const answer = {
                type: 'answer',
                sdp: this._remoteSdp.getSdp(),
            };
            logger.debug('send() | calling pc.setRemoteDescription() [answer:%o]', answer);
            await this._pc.setRemoteDescription(answer);
            // Store in the map.
            this._mapMidTransceiver.set(localId, transceiver);
            return {
                localId,
                rtpParameters: sendingRtpParameters,
                rtpSender: transceiver.sender,
            };
        }
        catch (error) {
            // Undo the changes done in the PeerConnection, otherwise it gets out of
            // sync with the remote SDP and later calls to send() fail.
            try {
                if (this._pc.signalingState === 'have-local-offer') {
                    try {
                        await this._pc.setLocalDescription({ type: 'rollback' });
                    }
                    catch (error2) { }
                }
                transceiver.stop();
            }
            catch (error3) { }
            throw error;
        }
    }
    async stopSending(localId) {
        this.assertSendDirection();
        if (this._closed) {
            return;
        }
        logger.debug('stopSending() [localId:%s]', localId);
        const transceiver = this._mapMidTransceiver.get(localId);
        if (!transceiver) {
            throw new Error('associated RTCRtpTransceiver not found');
        }
        void transceiver.sender.replaceTrack(null);
        this._pc.removeTrack(transceiver.sender);
        const mediaSectionClosed = this._remoteSdp.closeMediaSection(transceiver.mid);
        if (mediaSectionClosed) {
            try {
                transceiver.stop();
            }
            catch (error) { }
        }
        const offer = await this._pc.createOffer();
        logger.debug('stopSending() | calling pc.setLocalDescription() [offer:%o]', offer);
        await this._pc.setLocalDescription(offer);
        const answer = {
            type: 'answer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('stopSending() | calling pc.setRemoteDescription() [answer:%o]', answer);
        await this._pc.setRemoteDescription(answer);
        this._mapMidTransceiver.delete(localId);
    }
    async pauseSending(localId) {
        this.assertNotClosed();
        this.assertSendDirection();
        logger.debug('pauseSending() [localId:%s]', localId);
        const transceiver = this._mapMidTransceiver.get(localId);
        if (!transceiver) {
            throw new Error('associated RTCRtpTransceiver not found');
        }
        transceiver.direction = 'inactive';
        this._remoteSdp.pauseMediaSection(localId);
        const offer = await this._pc.createOffer();
        logger.debug('pauseSending() | calling pc.setLocalDescription() [offer:%o]', offer);
        await this._pc.setLocalDescription(offer);
        const answer = {
            type: 'answer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('pauseSending() | calling pc.setRemoteDescription() [answer:%o]', answer);
        await this._pc.setRemoteDescription(answer);
    }
    async resumeSending(localId) {
        this.assertNotClosed();
        this.assertSendDirection();
        logger.debug('resumeSending() [localId:%s]', localId);
        const transceiver = this._mapMidTransceiver.get(localId);
        if (!transceiver) {
            throw new Error('associated RTCRtpTransceiver not found');
        }
        transceiver.direction = 'sendonly';
        this._remoteSdp.resumeSendingMediaSection(localId);
        const offer = await this._pc.createOffer();
        logger.debug('resumeSending() | calling pc.setLocalDescription() [offer:%o]', offer);
        await this._pc.setLocalDescription(offer);
        const answer = {
            type: 'answer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('resumeSending() | calling pc.setRemoteDescription() [answer:%o]', answer);
        await this._pc.setRemoteDescription(answer);
    }
    async replaceTrack(localId, track) {
        this.assertNotClosed();
        this.assertSendDirection();
        if (track) {
            logger.debug('replaceTrack() [localId:%s, track.id:%s]', localId, track.id);
        }
        else {
            logger.debug('replaceTrack() [localId:%s, no track]', localId);
        }
        const transceiver = this._mapMidTransceiver.get(localId);
        if (!transceiver) {
            throw new Error('associated RTCRtpTransceiver not found');
        }
        await transceiver.sender.replaceTrack(track);
    }
    async setMaxSpatialLayer(localId, spatialLayer) {
        this.assertNotClosed();
        this.assertSendDirection();
        logger.debug('setMaxSpatialLayer() [localId:%s, spatialLayer:%s]', localId, spatialLayer);
        const transceiver = this._mapMidTransceiver.get(localId);
        if (!transceiver) {
            throw new Error('associated RTCRtpTransceiver not found');
        }
        const parameters = transceiver.sender.getParameters();
        parameters.encodings.forEach((encoding, idx) => {
            if (idx <= spatialLayer) {
                encoding.active = true;
            }
            else {
                encoding.active = false;
            }
        });
        await transceiver.sender.setParameters(parameters);
        this._remoteSdp.muxMediaSectionSimulcast(localId, parameters.encodings);
        const offer = await this._pc.createOffer();
        logger.debug('setMaxSpatialLayer() | calling pc.setLocalDescription() [offer:%o]', offer);
        await this._pc.setLocalDescription(offer);
        const answer = {
            type: 'answer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('setMaxSpatialLayer() | calling pc.setRemoteDescription() [answer:%o]', answer);
        await this._pc.setRemoteDescription(answer);
    }
    async setRtpEncodingParameters(localId, params) {
        this.assertNotClosed();
        this.assertSendDirection();
        logger.debug('setRtpEncodingParameters() [localId:%s, params:%o]', localId, params);
        const transceiver = this._mapMidTransceiver.get(localId);
        if (!transceiver) {
            throw new Error('associated RTCRtpTransceiver not found');
        }
        const parameters = transceiver.sender.getParameters();
        parameters.encodings.forEach((encoding, idx) => {
            parameters.encodings[idx] = { ...encoding, ...params };
        });
        await transceiver.sender.setParameters(parameters);
        this._remoteSdp.muxMediaSectionSimulcast(localId, parameters.encodings);
        const offer = await this._pc.createOffer();
        logger.debug('setRtpEncodingParameters() | calling pc.setLocalDescription() [offer:%o]', offer);
        await this._pc.setLocalDescription(offer);
        const answer = {
            type: 'answer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('setRtpEncodingParameters() | calling pc.setRemoteDescription() [answer:%o]', answer);
        await this._pc.setRemoteDescription(answer);
    }
    async getSenderStats(localId) {
        this.assertNotClosed();
        this.assertSendDirection();
        const transceiver = this._mapMidTransceiver.get(localId);
        if (!transceiver) {
            throw new Error('associated RTCRtpTransceiver not found');
        }
        return transceiver.sender.getStats();
    }
    async sendDataChannel({ sctpStreamParameters, }) {
        this.assertNotClosed();
        this.assertSendDirection();
        const options = {
            negotiated: true,
            id: this._nextSendSctpStreamId,
            ordered: sctpStreamParameters.ordered,
            maxPacketLifeTime: sctpStreamParameters.maxPacketLifeTime,
            maxRetransmits: sctpStreamParameters.maxRetransmits,
            protocol: sctpStreamParameters.protocol,
        };
        logger.debug('sendDataChannel() [options:%o]', options);
        const dataChannel = this._pc.createDataChannel(sctpStreamParameters.label, options);
        // Increase next id.
        this._nextSendSctpStreamId =
            ++this._nextSendSctpStreamId % (this._pc.sctp?.maxChannels ?? 65536);
        // If this is the first DataChannel we need to create the SDP answer with
        // m=application section.
        if (!this._hasDataChannelMediaSection) {
            try {
                const offer = await this._pc.createOffer();
                const localSdpObject = sdpTransform.parse(offer.sdp);
                const offerMediaObject = localSdpObject.media.find(m => m.type === 'application');
                if (!this._transportReady) {
                    await this.setupTransport({
                        localDtlsRole: this._forcedLocalDtlsRole ?? 'client',
                        localSdpObject,
                    });
                }
                logger.debug('sendDataChannel() | calling pc.setLocalDescription() [offer:%o]', offer);
                await this._pc.setLocalDescription(offer);
                this._remoteSdp.sendSctpAssociation({ offerMediaObject });
                const answer = {
                    type: 'answer',
                    sdp: this._remoteSdp.getSdp(),
                };
                logger.debug('sendDataChannel() | calling pc.setRemoteDescription() [answer:%o]', answer);
                await this._pc.setRemoteDescription(answer);
                this._hasDataChannelMediaSection = true;
            }
            catch (error) {
                // Undo the changes done in the PeerConnection, otherwise it gets out of
                // sync with the remote SDP and later calls to send() and
                // sendDataChannel() fail.
                try {
                    if (this._pc.signalingState === 'have-local-offer') {
                        try {
                            await this._pc.setLocalDescription({ type: 'rollback' });
                        }
                        catch (error2) { }
                    }
                    dataChannel.close();
                }
                catch (error3) { }
                throw error;
            }
        }
        const newSctpStreamParameters = {
            streamId: options.id,
            ordered: options.ordered,
            maxPacketLifeTime: options.maxPacketLifeTime,
            maxRetransmits: options.maxRetransmits,
        };
        return { dataChannel, sctpStreamParameters: newSctpStreamParameters };
    }
    async receive(optionsList) {
        this.assertNotClosed();
        this.assertRecvDirection();
        const results = [];
        const mapLocalId = new Map();
        for (const options of optionsList) {
            const { trackId, kind, rtpParameters, streamId } = options;
            logger.debug('receive() [trackId:%s, kind:%s]', trackId, kind);
            const localId = rtpParameters.mid ?? String(this._mapMidTransceiver.size);
            mapLocalId.set(trackId, localId);
            // We ignore MSID `trackId` when consuming and always use our computed
            // `trackId` which matches the `consumer.id`.
            const { msidStreamId } = ortcUtils.getMsidStreamIdAndTrackId(rtpParameters.msid);
            this._remoteSdp.receive({
                mid: localId,
                kind,
                offerRtpParameters: rtpParameters,
                streamId: streamId ?? msidStreamId ?? rtpParameters.rtcp?.cname ?? '-',
                trackId,
            });
        }
        const offer = {
            type: 'offer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('receive() | calling pc.setRemoteDescription() [offer:%o]', offer);
        await this._pc.setRemoteDescription(offer);
        for (const options of optionsList) {
            const { trackId, onRtpReceiver } = options;
            if (onRtpReceiver) {
                const localId = mapLocalId.get(trackId);
                const transceiver = this._pc
                    .getTransceivers()
                    .find((t) => t.mid === localId);
                if (!transceiver) {
                    throw new Error('transceiver not found');
                }
                if (this._forcedRtpExtensions) {
                    ortcUtils.applyForcedRtpExtensions(transceiver, this._forcedRtpExtensions);
                }
                onRtpReceiver(transceiver.receiver);
            }
        }
        let answer = await this._pc.createAnswer();
        const localSdpObject = sdpTransform.parse(answer.sdp);
        for (const options of optionsList) {
            const { trackId, rtpParameters } = options;
            const localId = mapLocalId.get(trackId);
            const answerMediaObject = localSdpObject.media.find(m => String(m.mid) === localId);
            // May need to modify codec parameters in the answer based on codec
            // parameters in the offer.
            sdpCommonUtils.applyCodecParameters({
                offerRtpParameters: rtpParameters,
                answerMediaObject,
            });
        }
        answer = {
            type: 'answer',
            sdp: sdpTransform.write(localSdpObject),
        };
        if (!this._transportReady) {
            await this.setupTransport({
                localDtlsRole: this._forcedLocalDtlsRole ?? 'client',
                localSdpObject,
            });
        }
        logger.debug('receive() | calling pc.setLocalDescription() [answer:%o]', answer);
        await this._pc.setLocalDescription(answer);
        for (const options of optionsList) {
            const { trackId } = options;
            const localId = mapLocalId.get(trackId);
            const transceiver = this._pc
                .getTransceivers()
                .find((t) => t.mid === localId);
            if (!transceiver) {
                throw new Error('new RTCRtpTransceiver not found');
            }
            // Store in the map.
            this._mapMidTransceiver.set(localId, transceiver);
            results.push({
                localId,
                track: transceiver.receiver.track,
                rtpReceiver: transceiver.receiver,
            });
        }
        return results;
    }
    async stopReceiving(localIds) {
        this.assertRecvDirection();
        if (this._closed) {
            return;
        }
        for (const localId of localIds) {
            logger.debug('stopReceiving() [localId:%s]', localId);
            const transceiver = this._mapMidTransceiver.get(localId);
            if (!transceiver) {
                throw new Error('associated RTCRtpTransceiver not found');
            }
            this._remoteSdp.closeMediaSection(transceiver.mid);
        }
        const offer = {
            type: 'offer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('stopReceiving() | calling pc.setRemoteDescription() [offer:%o]', offer);
        await this._pc.setRemoteDescription(offer);
        const answer = await this._pc.createAnswer();
        logger.debug('stopReceiving() | calling pc.setLocalDescription() [answer:%o]', answer);
        await this._pc.setLocalDescription(answer);
        for (const localId of localIds) {
            this._mapMidTransceiver.delete(localId);
        }
    }
    async pauseReceiving(localIds) {
        this.assertNotClosed();
        this.assertRecvDirection();
        for (const localId of localIds) {
            logger.debug('pauseReceiving() [localId:%s]', localId);
            const transceiver = this._mapMidTransceiver.get(localId);
            if (!transceiver) {
                throw new Error('associated RTCRtpTransceiver not found');
            }
            transceiver.direction = 'inactive';
            this._remoteSdp.pauseMediaSection(localId);
        }
        const offer = {
            type: 'offer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('pauseReceiving() | calling pc.setRemoteDescription() [offer:%o]', offer);
        await this._pc.setRemoteDescription(offer);
        const answer = await this._pc.createAnswer();
        logger.debug('pauseReceiving() | calling pc.setLocalDescription() [answer:%o]', answer);
        await this._pc.setLocalDescription(answer);
    }
    async resumeReceiving(localIds) {
        this.assertNotClosed();
        this.assertRecvDirection();
        for (const localId of localIds) {
            logger.debug('resumeReceiving() [localId:%s]', localId);
            const transceiver = this._mapMidTransceiver.get(localId);
            if (!transceiver) {
                throw new Error('associated RTCRtpTransceiver not found');
            }
            transceiver.direction = 'recvonly';
            this._remoteSdp.resumeReceivingMediaSection(localId);
        }
        const offer = {
            type: 'offer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('resumeReceiving() | calling pc.setRemoteDescription() [offer:%o]', offer);
        await this._pc.setRemoteDescription(offer);
        const answer = await this._pc.createAnswer();
        logger.debug('resumeReceiving() | calling pc.setLocalDescription() [answer:%o]', answer);
        await this._pc.setLocalDescription(answer);
    }
    async getReceiverStats(localId) {
        this.assertNotClosed();
        this.assertRecvDirection();
        const transceiver = this._mapMidTransceiver.get(localId);
        if (!transceiver) {
            throw new Error('associated RTCRtpTransceiver not found');
        }
        return transceiver.receiver.getStats();
    }
    async receiveDataChannel({ maxMessageSize, sctpStreamParameters, label, protocol, }) {
        this.assertNotClosed();
        this.assertRecvDirection();
        const { streamId, ordered, maxPacketLifeTime, maxRetransmits, } = sctpStreamParameters;
        const options = {
            negotiated: true,
            id: streamId,
            ordered,
            maxPacketLifeTime,
            maxRetransmits,
            protocol,
        };
        logger.debug('receiveDataChannel() [options:%o]', options);
        const dataChannel = this._pc.createDataChannel(label, options);
        // If this is the first DataChannel we need to create the SDP offer with
        // m=application section.
        if (!this._hasDataChannelMediaSection) {
            this._remoteSdp.receiveSctpAssociation();
            const offer = {
                type: 'offer',
                sdp: this._remoteSdp.getSdp(),
            };
            logger.debug('receiveDataChannel() | calling pc.setRemoteDescription() [offer:%o]', offer);
            await this._pc.setRemoteDescription(offer);
            let answer = await this._pc.createAnswer();
            const localSdpObject = sdpTransform.parse(answer.sdp);
            const answerMediaObject = localSdpObject.media.find(m => m.type === 'application');
            if (typeof maxMessageSize === 'number') {
                answerMediaObject.maxMessageSize = maxMessageSize;
            }
            if (!this._transportReady) {
                await this.setupTransport({
                    localDtlsRole: this._forcedLocalDtlsRole ?? 'client',
                    localSdpObject,
                });
            }
            answer = {
                type: 'answer',
                sdp: sdpTransform.write(localSdpObject),
            };
            logger.debug('receiveDataChannel() | calling pc.setLocalDescription() [answer:%o]', answer);
            await this._pc.setLocalDescription(answer);
            this._hasDataChannelMediaSection = true;
        }
        return { dataChannel };
    }
    getDataChannelMaxMessageSize() {
        return this._direction === 'send'
            ? this._pc.sctp?.maxMessageSize
            : this._sctpMaxReceiveMessageSize;
    }
    async setupTransport({ localDtlsRole, localSdpObject, }) {
        if (!localSdpObject) {
            localSdpObject = sdpTransform.parse(this._pc.localDescription.sdp);
        }
        // Get our local DTLS parameters.
        const dtlsParameters = sdpCommonUtils.extractDtlsParameters({
            sdpObject: localSdpObject,
        });
        // Set our DTLS role.
        dtlsParameters.role = localDtlsRole;
        // Update the remote DTLS role in the SDP.
        this._remoteSdp.updateDtlsRole(localDtlsRole === 'client' ? 'server' : 'client');
        // Need to tell the remote transport about our parameters.
        await new Promise((resolve, reject) => {
            this.safeEmit('@connect', { dtlsParameters }, resolve, reject);
        });
        this._transportReady = true;
    }
    onIceGatheringStateChange = () => {
        this.emit('@icegatheringstatechange', this._pc.iceGatheringState);
    };
    onIceCandidateError = (event) => {
        this.emit('@icecandidateerror', event);
    };
    onConnectionStateChange = () => {
        this.emit('@connectionstatechange', this._pc.connectionState);
    };
    onIceConnectionStateChange = () => {
        switch (this._pc.iceConnectionState) {
            case 'checking': {
                this.emit('@connectionstatechange', 'connecting');
                break;
            }
            case 'connected':
            case 'completed': {
                this.emit('@connectionstatechange', 'connected');
                break;
            }
            case 'failed': {
                this.emit('@connectionstatechange', 'failed');
                break;
            }
            case 'disconnected': {
                this.emit('@connectionstatechange', 'disconnected');
                break;
            }
            case 'closed': {
                this.emit('@connectionstatechange', 'closed');
                break;
            }
        }
    };
    assertNotClosed() {
        if (this._closed) {
            throw new errors_1.InvalidStateError('method called in a closed handler');
        }
    }
    assertSendDirection() {
        if (this._direction !== 'send') {
            throw new Error('method can just be called for handlers with "send" direction');
        }
    }
    assertRecvDirection() {
        if (this._direction !== 'recv') {
            throw new Error('method can just be called for handlers with "recv" direction');
        }
    }
}
exports.Safari12 = Safari12;

},
19: function(module, exports, __req) {
"use strict";
var __createBinding = (this && this.__createBinding) || (Object.create ? (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    var desc = Object.getOwnPropertyDescriptor(m, k);
    if (!desc || ("get" in desc ? !m.__esModule : desc.writable || desc.configurable)) {
      desc = { enumerable: true, get: function() { return m[k]; } };
    }
    Object.defineProperty(o, k2, desc);
}) : (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    o[k2] = m[k];
}));
var __setModuleDefault = (this && this.__setModuleDefault) || (Object.create ? (function(o, v) {
    Object.defineProperty(o, "default", { enumerable: true, value: v });
}) : function(o, v) {
    o["default"] = v;
});
var __importStar = (this && this.__importStar) || (function () {
    var ownKeys = function(o) {
        ownKeys = Object.getOwnPropertyNames || function (o) {
            var ar = [];
            for (var k in o) if (Object.prototype.hasOwnProperty.call(o, k)) ar[ar.length] = k;
            return ar;
        };
        return ownKeys(o);
    };
    return function (mod) {
        if (mod && mod.__esModule) return mod;
        var result = {};
        if (mod != null) for (var k = ownKeys(mod), i = 0; i < k.length; i++) if (k[i] !== "default") __createBinding(result, mod, k[i]);
        __setModuleDefault(result, mod);
        return result;
    };
})();
Object.defineProperty(exports, "__esModule", { value: true });
exports.ReactNative106 = void 0;
const sdpTransform = __importStar(__req(30));
const enhancedEvents_1 = __req(7);
const Logger_1 = __req(11);
const ortc = __importStar(__req(6));
const errors_1 = __req(12);
const scalabilityModes_1 = __req(5);
const RemoteSdp_1 = __req(31);
const sdpCommonUtils = __importStar(__req(32));
const sdpUnifiedPlanUtils = __importStar(__req(33));
const ortcUtils = __importStar(__req(34));
const logger = new Logger_1.Logger('ReactNative106');
const NAME = 'ReactNative106';
class ReactNative106 extends enhancedEvents_1.EnhancedEventEmitter {
    // Closed flag.
    _closed = false;
    // Handler direction.
    _direction;
    // DataChannel max receive message size.
    _sctpMaxReceiveMessageSize;
    // Remote SDP handler.
    _remoteSdp;
    // Callback to request sending extended RTP capabilities on demand.
    _getSendExtendedRtpCapabilities;
    _forcedRtpExtensions;
    // Initial server side DTLS role. If not 'auto', it will force the opposite
    // value in client side.
    _forcedLocalDtlsRole;
    // RTCPeerConnection instance.
    _pc;
    // Map of RTCTransceivers indexed by MID.
    _mapMidTransceiver = new Map();
    // Default local stream for sending if no `streamId` is given in send().
    _sendStream = new MediaStream();
    // Whether a DataChannel m=application section has been created.
    _hasDataChannelMediaSection = false;
    // Sending DataChannel id value counter. Incremented for each new DataChannel.
    _nextSendSctpStreamId = 0;
    // Got transport local and remote parameters.
    _transportReady = false;
    /**
     * Creates a factory function.
     */
    static createFactory({ forcedRtpExtensions, }) {
        return {
            name: NAME,
            factory: (options) => new ReactNative106({ ...options, forcedRtpExtensions }),
            getNativeRtpCapabilities: async ({ direction, }) => {
                logger.debug('getNativeRtpCapabilities() [direction:%o]', direction);
                let pc = new RTCPeerConnection({
                    iceServers: [],
                    iceTransportPolicy: 'all',
                    bundlePolicy: 'max-bundle',
                    rtcpMuxPolicy: 'require',
                });
                try {
                    const audioTransceiver = pc.addTransceiver('audio', { direction });
                    if (forcedRtpExtensions) {
                        ortcUtils.applyForcedRtpExtensions(audioTransceiver, forcedRtpExtensions);
                    }
                    const videoTransceiver = pc.addTransceiver('video', { direction });
                    if (forcedRtpExtensions) {
                        ortcUtils.applyForcedRtpExtensions(videoTransceiver, forcedRtpExtensions);
                    }
                    const offer = await pc.createOffer();
                    try {
                        pc.close();
                    }
                    catch (error) { }
                    pc = undefined;
                    const sdpObject = sdpTransform.parse(offer.sdp);
                    const nativeRtpCapabilities = ReactNative106.getLocalRtpCapabilities(sdpObject);
                    return nativeRtpCapabilities;
                }
                catch (error) {
                    try {
                        pc?.close();
                    }
                    catch (error2) { }
                    // eslint-disable-next-line no-useless-assignment
                    pc = undefined;
                    throw error;
                }
            },
        };
    }
    static getLocalRtpCapabilities(localSdpObject) {
        const nativeRtpCapabilities = sdpCommonUtils.extractRtpCapabilities({
            sdpObject: localSdpObject,
        });
        // Need to validate and normalize native RTP capabilities.
        ortc.validateAndNormalizeRtpCapabilities(nativeRtpCapabilities);
        // libwebrtc supports NACK for OPUS but doesn't announce it.
        ortcUtils.addNackSupportForOpus(nativeRtpCapabilities);
        return nativeRtpCapabilities;
    }
    constructor({ direction, iceParameters, iceCandidates, dtlsParameters, sctpParameters, iceServers, iceTransportPolicy, additionalSettings, getSendExtendedRtpCapabilities, forcedRtpExtensions, }) {
        super();
        logger.debug('constructor()');
        this._direction = direction;
        // NOTE: Mirror it.
        this._sctpMaxReceiveMessageSize = sctpParameters?.maxSendMessageSize;
        this._remoteSdp = new RemoteSdp_1.RemoteSdp({
            iceParameters,
            iceCandidates,
            dtlsParameters,
            sctpParameters,
        });
        this._getSendExtendedRtpCapabilities = getSendExtendedRtpCapabilities;
        this._forcedRtpExtensions = forcedRtpExtensions;
        if (dtlsParameters.role && dtlsParameters.role !== 'auto') {
            this._forcedLocalDtlsRole =
                dtlsParameters.role === 'server' ? 'client' : 'server';
        }
        this._pc = new RTCPeerConnection({
            iceServers: iceServers ?? [],
            iceTransportPolicy: iceTransportPolicy ?? 'all',
            bundlePolicy: 'max-bundle',
            rtcpMuxPolicy: 'require',
            ...additionalSettings,
        });
        this._pc.addEventListener('icegatheringstatechange', this.onIceGatheringStateChange);
        this._pc.addEventListener('icecandidateerror', this.onIceCandidateError);
        if (this._pc.connectionState) {
            this._pc.addEventListener('connectionstatechange', this.onConnectionStateChange);
        }
        else {
            logger.warn('run() | pc.connectionState not supported, using pc.iceConnectionState');
            this._pc.addEventListener('iceconnectionstatechange', this.onIceConnectionStateChange);
        }
    }
    get name() {
        return NAME;
    }
    close() {
        logger.debug('close()');
        if (this._closed) {
            return;
        }
        this._closed = true;
        // Free/dispose native MediaStream but DO NOT free/dispose native
        // MediaStreamTracks (that is parent's business).
        // @ts-expect-error --- Proprietary API in react-native-webrtc.
        this._sendStream.release(/* releaseTracks */ false);
        // Close RTCPeerConnection.
        try {
            this._pc.close();
        }
        catch (error) { }
        this._pc.removeEventListener('icegatheringstatechange', this.onIceGatheringStateChange);
        this._pc.removeEventListener('icecandidateerror', this.onIceCandidateError);
        this._pc.removeEventListener('connectionstatechange', this.onConnectionStateChange);
        this._pc.removeEventListener('iceconnectionstatechange', this.onIceConnectionStateChange);
        this.emit('@close');
        // Invoke close() in EnhancedEventEmitter classes.
        super.close();
    }
    async updateIceServers(iceServers) {
        this.assertNotClosed();
        logger.debug('updateIceServers()');
        const configuration = this._pc.getConfiguration();
        configuration.iceServers = iceServers;
        this._pc.setConfiguration(configuration);
    }
    async restartIce(iceParameters) {
        this.assertNotClosed();
        logger.debug('restartIce()');
        // Provide the remote SDP handler with new remote ICE parameters.
        this._remoteSdp.updateIceParameters(iceParameters);
        if (!this._transportReady) {
            return;
        }
        if (this._direction === 'send') {
            const offer = await this._pc.createOffer({ iceRestart: true });
            logger.debug('restartIce() | calling pc.setLocalDescription() [offer:%o]', offer);
            await this._pc.setLocalDescription(offer);
            const answer = {
                type: 'answer',
                sdp: this._remoteSdp.getSdp(),
            };
            logger.debug('restartIce() | calling pc.setRemoteDescription() [answer:%o]', answer);
            await this._pc.setRemoteDescription(answer);
        }
        else {
            const offer = {
                type: 'offer',
                sdp: this._remoteSdp.getSdp(),
            };
            logger.debug('restartIce() | calling pc.setRemoteDescription() [offer:%o]', offer);
            await this._pc.setRemoteDescription(offer);
            const answer = await this._pc.createAnswer();
            logger.debug('restartIce() | calling pc.setLocalDescription() [answer:%o]', answer);
            await this._pc.setLocalDescription(answer);
        }
    }
    async getTransportStats() {
        this.assertNotClosed();
        return this._pc.getStats();
    }
    async send({ track, streamId, encodings, codecOptions, codec, onRtpSender, }) {
        this.assertNotClosed();
        this.assertSendDirection();
        logger.debug('send() [kind:%s, track.id:%s, streamId:%s]', track.kind, track.id, streamId);
        if (encodings && encodings.length > 1) {
            encodings.forEach((encoding, idx) => {
                encoding.rid = `r${idx}`;
            });
        }
        const mediaSectionIdx = this._remoteSdp.getNextMediaSectionIdx();
        const transceiver = this._pc.addTransceiver(track, {
            direction: 'sendonly',
            streams: [this._sendStream],
            sendEncodings: encodings,
        });
        try {
            if (this._forcedRtpExtensions) {
                ortcUtils.applyForcedRtpExtensions(transceiver, this._forcedRtpExtensions);
            }
            if (onRtpSender) {
                onRtpSender(transceiver.sender);
            }
            let offer = await this._pc.createOffer();
            let localSdpObject = sdpTransform.parse(offer.sdp);
            if (localSdpObject.extmapAllowMixed) {
                this._remoteSdp.setSessionExtmapAllowMixed();
            }
            const nativeRtpCapabilities = ReactNative106.getLocalRtpCapabilities(localSdpObject);
            const sendExtendedRtpCapabilities = this._getSendExtendedRtpCapabilities(nativeRtpCapabilities);
            // Generic sending RTP parameters.
            const sendingRtpParameters = ortc.getSendingRtpParameters(track.kind, sendExtendedRtpCapabilities);
            // This may throw.
            sendingRtpParameters.codecs = ortc.reduceCodecs(sendingRtpParameters.codecs, codec);
            // Generic sending RTP parameters suitable for the SDP remote answer.
            const sendingRemoteRtpParameters = ortc.getSendingRemoteRtpParameters(track.kind, sendExtendedRtpCapabilities);
            // This may throw.
            sendingRemoteRtpParameters.codecs = ortc.reduceCodecs(sendingRemoteRtpParameters.codecs, codec);
            if (!this._transportReady) {
                await this.setupTransport({
                    localDtlsRole: this._forcedLocalDtlsRole ?? 'client',
                    localSdpObject,
                });
            }
            // Special case for VP9 with SVC.
            let hackVp9Svc = false;
            const layers = (0, scalabilityModes_1.parse)((encodings ?? [{}])[0].scalabilityMode);
            let offerMediaObject;
            if (encodings?.length === 1 &&
                layers.spatialLayers > 1 &&
                sendingRtpParameters.codecs[0].mimeType.toLowerCase() === 'video/vp9') {
                logger.debug('send() | enabling legacy simulcast for VP9 SVC');
                hackVp9Svc = true;
                localSdpObject = sdpTransform.parse(offer.sdp);
                offerMediaObject = localSdpObject.media[mediaSectionIdx.idx];
                sdpUnifiedPlanUtils.addLegacySimulcast({
                    offerMediaObject,
                    numStreams: layers.spatialLayers,
                });
                offer = {
                    type: 'offer',
                    sdp: sdpTransform.write(localSdpObject),
                };
            }
            logger.debug('send() | calling pc.setLocalDescription() [offer:%o]', offer);
            await this._pc.setLocalDescription(offer);
            // We can now get the transceiver.mid.
            // NOTE: We cannot read generated MID on iOS react-native-webrtc 111.0.0
            // because transceiver.mid is not available until setRemoteDescription()
            // is called, so this is best effort.
            // Issue: https://github.com/react-native-webrtc/react-native-webrtc/issues/1404
            // NOTE: So let's fill MID in sendingRtpParameters later.
            // NOTE: This is fixed in react-native-webrtc 111.0.3.
            let localId = transceiver.mid ?? undefined;
            if (!localId) {
                logger.warn('send() | missing transceiver.mid (bug in react-native-webrtc, using a workaround');
            }
            // Set MID.
            // NOTE: As per above, it could be unset yet.
            sendingRtpParameters.mid = localId;
            localSdpObject = sdpTransform.parse(this._pc.localDescription.sdp);
            offerMediaObject = localSdpObject.media[mediaSectionIdx.idx];
            // Set RTCP CNAME.
            sendingRtpParameters.rtcp.cname = sdpCommonUtils.getCname({
                offerMediaObject,
            });
            // Set msid.
            sendingRtpParameters.msid = `${streamId ?? this._sendStream.id} ${track.id}`;
            // Set RTP encodings by parsing the SDP offer if no encodings are given.
            if (!encodings) {
                sendingRtpParameters.encodings = sdpUnifiedPlanUtils.getRtpEncodings({
                    offerMediaObject,
                    codecs: sendingRtpParameters.codecs,
                });
            }
            // Set RTP encodings by parsing the SDP offer and complete them with given
            // one if just a single encoding has been given.
            else if (encodings.length === 1) {
                let newEncodings = sdpUnifiedPlanUtils.getRtpEncodings({
                    offerMediaObject,
                    codecs: sendingRtpParameters.codecs,
                });
                Object.assign(newEncodings[0], encodings[0]);
                // Hack for VP9 SVC.
                if (hackVp9Svc) {
                    newEncodings = [newEncodings[0]];
                }
                sendingRtpParameters.encodings = newEncodings;
            }
            // Otherwise if more than 1 encoding are given use them verbatim.
            else {
                sendingRtpParameters.encodings = encodings;
            }
            // If VP8 or H264 and there is effective simulcast, add scalabilityMode to
            // each encoding.
            if (sendingRtpParameters.encodings.length > 1 &&
                (sendingRtpParameters.codecs[0].mimeType.toLowerCase() ===
                    'video/vp8' ||
                    sendingRtpParameters.codecs[0].mimeType.toLowerCase() ===
                        'video/h264')) {
                for (const encoding of sendingRtpParameters.encodings) {
                    if (encoding.scalabilityMode) {
                        encoding.scalabilityMode = `L1T${layers.temporalLayers}`;
                    }
                    else {
                        encoding.scalabilityMode = 'L1T3';
                    }
                }
            }
            this._remoteSdp.send({
                offerMediaObject,
                reuseMid: mediaSectionIdx.reuseMid,
                offerRtpParameters: sendingRtpParameters,
                answerRtpParameters: sendingRemoteRtpParameters,
                codecOptions,
            });
            const answer = {
                type: 'answer',
                sdp: this._remoteSdp.getSdp(),
            };
            logger.debug('send() | calling pc.setRemoteDescription() [answer:%o]', answer);
            await this._pc.setRemoteDescription(answer);
            // Follow up of iOS react-native-webrtc 111.0.0 issue told above. Now yes,
            // we can read generated MID (if not done above) and fill sendingRtpParameters.
            // NOTE: This is fixed in react-native-webrtc 111.0.3 so this block isn't
            // needed starting from that version.
            if (!localId) {
                localId = transceiver.mid;
                sendingRtpParameters.mid = localId;
            }
            // Store in the map.
            this._mapMidTransceiver.set(localId, transceiver);
            return {
                localId,
                rtpParameters: sendingRtpParameters,
                rtpSender: transceiver.sender,
            };
        }
        catch (error) {
            // Undo the changes done in the PeerConnection, otherwise it gets out of
            // sync with the remote SDP and later calls to send() fail.
            try {
                if (this._pc.signalingState === 'have-local-offer') {
                    try {
                        await this._pc.setLocalDescription({ type: 'rollback' });
                    }
                    catch (error2) { }
                }
                transceiver.stop();
            }
            catch (error3) { }
            throw error;
        }
    }
    async stopSending(localId) {
        this.assertSendDirection();
        if (this._closed) {
            return;
        }
        logger.debug('stopSending() [localId:%s]', localId);
        const transceiver = this._mapMidTransceiver.get(localId);
        if (!transceiver) {
            throw new Error('associated RTCRtpTransceiver not found');
        }
        void transceiver.sender.replaceTrack(null);
        this._pc.removeTrack(transceiver.sender);
        const mediaSectionClosed = this._remoteSdp.closeMediaSection(transceiver.mid);
        if (mediaSectionClosed) {
            try {
                transceiver.stop();
            }
            catch (error) { }
        }
        const offer = await this._pc.createOffer();
        logger.debug('stopSending() | calling pc.setLocalDescription() [offer:%o]', offer);
        await this._pc.setLocalDescription(offer);
        const answer = {
            type: 'answer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('stopSending() | calling pc.setRemoteDescription() [answer:%o]', answer);
        await this._pc.setRemoteDescription(answer);
        this._mapMidTransceiver.delete(localId);
    }
    async pauseSending(localId) {
        this.assertNotClosed();
        this.assertSendDirection();
        logger.debug('pauseSending() [localId:%s]', localId);
        const transceiver = this._mapMidTransceiver.get(localId);
        if (!transceiver) {
            throw new Error('associated RTCRtpTransceiver not found');
        }
        transceiver.direction = 'inactive';
        this._remoteSdp.pauseMediaSection(localId);
        const offer = await this._pc.createOffer();
        logger.debug('pauseSending() | calling pc.setLocalDescription() [offer:%o]', offer);
        await this._pc.setLocalDescription(offer);
        const answer = {
            type: 'answer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('pauseSending() | calling pc.setRemoteDescription() [answer:%o]', answer);
        await this._pc.setRemoteDescription(answer);
    }
    async resumeSending(localId) {
        this.assertNotClosed();
        this.assertSendDirection();
        logger.debug('resumeSending() [localId:%s]', localId);
        const transceiver = this._mapMidTransceiver.get(localId);
        this._remoteSdp.resumeSendingMediaSection(localId);
        if (!transceiver) {
            throw new Error('associated RTCRtpTransceiver not found');
        }
        transceiver.direction = 'sendonly';
        const offer = await this._pc.createOffer();
        logger.debug('resumeSending() | calling pc.setLocalDescription() [offer:%o]', offer);
        await this._pc.setLocalDescription(offer);
        const answer = {
            type: 'answer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('resumeSending() | calling pc.setRemoteDescription() [answer:%o]', answer);
        await this._pc.setRemoteDescription(answer);
    }
    async replaceTrack(localId, track) {
        this.assertNotClosed();
        this.assertSendDirection();
        if (track) {
            logger.debug('replaceTrack() [localId:%s, track.id:%s]', localId, track.id);
        }
        else {
            logger.debug('replaceTrack() [localId:%s, no track]', localId);
        }
        const transceiver = this._mapMidTransceiver.get(localId);
        if (!transceiver) {
            throw new Error('associated RTCRtpTransceiver not found');
        }
        await transceiver.sender.replaceTrack(track);
    }
    async setMaxSpatialLayer(localId, spatialLayer) {
        this.assertNotClosed();
        this.assertSendDirection();
        logger.debug('setMaxSpatialLayer() [localId:%s, spatialLayer:%s]', localId, spatialLayer);
        const transceiver = this._mapMidTransceiver.get(localId);
        if (!transceiver) {
            throw new Error('associated RTCRtpTransceiver not found');
        }
        const parameters = transceiver.sender.getParameters();
        parameters.encodings.forEach((encoding, idx) => {
            if (idx <= spatialLayer) {
                encoding.active = true;
            }
            else {
                encoding.active = false;
            }
        });
        await transceiver.sender.setParameters(parameters);
        this._remoteSdp.muxMediaSectionSimulcast(localId, parameters.encodings);
        const offer = await this._pc.createOffer();
        logger.debug('setMaxSpatialLayer() | calling pc.setLocalDescription() [offer:%o]', offer);
        await this._pc.setLocalDescription(offer);
        const answer = {
            type: 'answer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('setMaxSpatialLayer() | calling pc.setRemoteDescription() [answer:%o]', answer);
        await this._pc.setRemoteDescription(answer);
    }
    async setRtpEncodingParameters(localId, params) {
        this.assertNotClosed();
        this.assertSendDirection();
        logger.debug('setRtpEncodingParameters() [localId:%s, params:%o]', localId, params);
        const transceiver = this._mapMidTransceiver.get(localId);
        if (!transceiver) {
            throw new Error('associated RTCRtpTransceiver not found');
        }
        const parameters = transceiver.sender.getParameters();
        parameters.encodings.forEach((encoding, idx) => {
            parameters.encodings[idx] = { ...encoding, ...params };
        });
        await transceiver.sender.setParameters(parameters);
        this._remoteSdp.muxMediaSectionSimulcast(localId, parameters.encodings);
        const offer = await this._pc.createOffer();
        logger.debug('setRtpEncodingParameters() | calling pc.setLocalDescription() [offer:%o]', offer);
        await this._pc.setLocalDescription(offer);
        const answer = {
            type: 'answer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('setRtpEncodingParameters() | calling pc.setRemoteDescription() [answer:%o]', answer);
        await this._pc.setRemoteDescription(answer);
    }
    async getSenderStats(localId) {
        this.assertNotClosed();
        this.assertSendDirection();
        const transceiver = this._mapMidTransceiver.get(localId);
        if (!transceiver) {
            throw new Error('associated RTCRtpTransceiver not found');
        }
        return transceiver.sender.getStats();
    }
    async sendDataChannel({ sctpStreamParameters, }) {
        this.assertNotClosed();
        this.assertSendDirection();
        const options = {
            negotiated: true,
            id: this._nextSendSctpStreamId,
            ordered: sctpStreamParameters.ordered,
            maxPacketLifeTime: sctpStreamParameters.maxPacketLifeTime,
            maxRetransmits: sctpStreamParameters.maxRetransmits,
            protocol: sctpStreamParameters.protocol,
        };
        logger.debug('sendDataChannel() [options:%o]', options);
        const dataChannel = this._pc.createDataChannel(sctpStreamParameters.label, options);
        // Increase next id.
        this._nextSendSctpStreamId =
            ++this._nextSendSctpStreamId % (this._pc.sctp?.maxChannels ?? 65536);
        // If this is the first DataChannel we need to create the SDP answer with
        // m=application section.
        if (!this._hasDataChannelMediaSection) {
            try {
                const offer = await this._pc.createOffer();
                const localSdpObject = sdpTransform.parse(offer.sdp);
                const offerMediaObject = localSdpObject.media.find(m => m.type === 'application');
                if (!this._transportReady) {
                    await this.setupTransport({
                        localDtlsRole: this._forcedLocalDtlsRole ?? 'client',
                        localSdpObject,
                    });
                }
                logger.debug('sendDataChannel() | calling pc.setLocalDescription() [offer:%o]', offer);
                await this._pc.setLocalDescription(offer);
                this._remoteSdp.sendSctpAssociation({ offerMediaObject });
                const answer = {
                    type: 'answer',
                    sdp: this._remoteSdp.getSdp(),
                };
                logger.debug('sendDataChannel() | calling pc.setRemoteDescription() [answer:%o]', answer);
                await this._pc.setRemoteDescription(answer);
                this._hasDataChannelMediaSection = true;
            }
            catch (error) {
                // Undo the changes done in the PeerConnection, otherwise it gets out of
                // sync with the remote SDP and later calls to send() and
                // sendDataChannel() fail.
                try {
                    if (this._pc.signalingState === 'have-local-offer') {
                        try {
                            await this._pc.setLocalDescription({ type: 'rollback' });
                        }
                        catch (error2) { }
                    }
                    dataChannel.close();
                }
                catch (error3) { }
                throw error;
            }
        }
        const newSctpStreamParameters = {
            streamId: options.id,
            ordered: options.ordered,
            maxPacketLifeTime: options.maxPacketLifeTime,
            maxRetransmits: options.maxRetransmits,
        };
        return { dataChannel, sctpStreamParameters: newSctpStreamParameters };
    }
    async receive(optionsList) {
        this.assertNotClosed();
        this.assertRecvDirection();
        const results = [];
        const mapLocalId = new Map();
        for (const options of optionsList) {
            const { trackId, kind, rtpParameters, streamId } = options;
            logger.debug('receive() [trackId:%s, kind:%s]', trackId, kind);
            const localId = rtpParameters.mid ?? String(this._mapMidTransceiver.size);
            mapLocalId.set(trackId, localId);
            // We ignore MSID `trackId` when consuming and always use our computed
            // `trackId` which matches the `consumer.id`.
            const { msidStreamId } = ortcUtils.getMsidStreamIdAndTrackId(rtpParameters.msid);
            this._remoteSdp.receive({
                mid: localId,
                kind,
                offerRtpParameters: rtpParameters,
                streamId: streamId ?? msidStreamId ?? rtpParameters.rtcp?.cname ?? '-',
                trackId,
            });
        }
        const offer = {
            type: 'offer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('receive() | calling pc.setRemoteDescription() [offer:%o]', offer);
        await this._pc.setRemoteDescription(offer);
        for (const options of optionsList) {
            const { trackId, onRtpReceiver } = options;
            if (onRtpReceiver) {
                const localId = mapLocalId.get(trackId);
                const transceiver = this._pc
                    .getTransceivers()
                    .find((t) => t.mid === localId);
                if (!transceiver) {
                    throw new Error('transceiver not found');
                }
                if (this._forcedRtpExtensions) {
                    ortcUtils.applyForcedRtpExtensions(transceiver, this._forcedRtpExtensions);
                }
                onRtpReceiver(transceiver.receiver);
            }
        }
        let answer = await this._pc.createAnswer();
        const localSdpObject = sdpTransform.parse(answer.sdp);
        for (const options of optionsList) {
            const { trackId, rtpParameters } = options;
            const localId = mapLocalId.get(trackId);
            const answerMediaObject = localSdpObject.media.find(m => String(m.mid) === localId);
            // May need to modify codec parameters in the answer based on codec
            // parameters in the offer.
            sdpCommonUtils.applyCodecParameters({
                offerRtpParameters: rtpParameters,
                answerMediaObject,
            });
        }
        answer = {
            type: 'answer',
            sdp: sdpTransform.write(localSdpObject),
        };
        if (!this._transportReady) {
            await this.setupTransport({
                localDtlsRole: this._forcedLocalDtlsRole ?? 'client',
                localSdpObject,
            });
        }
        logger.debug('receive() | calling pc.setLocalDescription() [answer:%o]', answer);
        await this._pc.setLocalDescription(answer);
        for (const options of optionsList) {
            const { trackId } = options;
            const localId = mapLocalId.get(trackId);
            const transceiver = this._pc
                .getTransceivers()
                .find((t) => t.mid === localId);
            if (!transceiver) {
                throw new Error('new RTCRtpTransceiver not found');
            }
            else {
                // Store in the map.
                this._mapMidTransceiver.set(localId, transceiver);
                results.push({
                    localId,
                    track: transceiver.receiver.track,
                    rtpReceiver: transceiver.receiver,
                });
            }
        }
        return results;
    }
    async stopReceiving(localIds) {
        this.assertRecvDirection();
        if (this._closed) {
            return;
        }
        for (const localId of localIds) {
            logger.debug('stopReceiving() [localId:%s]', localId);
            const transceiver = this._mapMidTransceiver.get(localId);
            if (!transceiver) {
                throw new Error('associated RTCRtpTransceiver not found');
            }
            this._remoteSdp.closeMediaSection(transceiver.mid);
        }
        const offer = {
            type: 'offer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('stopReceiving() | calling pc.setRemoteDescription() [offer:%o]', offer);
        await this._pc.setRemoteDescription(offer);
        const answer = await this._pc.createAnswer();
        logger.debug('stopReceiving() | calling pc.setLocalDescription() [answer:%o]', answer);
        await this._pc.setLocalDescription(answer);
        for (const localId of localIds) {
            this._mapMidTransceiver.delete(localId);
        }
    }
    async pauseReceiving(localIds) {
        this.assertNotClosed();
        this.assertRecvDirection();
        for (const localId of localIds) {
            logger.debug('pauseReceiving() [localId:%s]', localId);
            const transceiver = this._mapMidTransceiver.get(localId);
            if (!transceiver) {
                throw new Error('associated RTCRtpTransceiver not found');
            }
            transceiver.direction = 'inactive';
            this._remoteSdp.pauseMediaSection(localId);
        }
        const offer = {
            type: 'offer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('pauseReceiving() | calling pc.setRemoteDescription() [offer:%o]', offer);
        await this._pc.setRemoteDescription(offer);
        const answer = await this._pc.createAnswer();
        logger.debug('pauseReceiving() | calling pc.setLocalDescription() [answer:%o]', answer);
        await this._pc.setLocalDescription(answer);
    }
    async resumeReceiving(localIds) {
        this.assertNotClosed();
        this.assertRecvDirection();
        for (const localId of localIds) {
            logger.debug('resumeReceiving() [localId:%s]', localId);
            const transceiver = this._mapMidTransceiver.get(localId);
            if (!transceiver) {
                throw new Error('associated RTCRtpTransceiver not found');
            }
            transceiver.direction = 'recvonly';
            this._remoteSdp.resumeReceivingMediaSection(localId);
        }
        const offer = {
            type: 'offer',
            sdp: this._remoteSdp.getSdp(),
        };
        logger.debug('resumeReceiving() | calling pc.setRemoteDescription() [offer:%o]', offer);
        await this._pc.setRemoteDescription(offer);
        const answer = await this._pc.createAnswer();
        logger.debug('resumeReceiving() | calling pc.setLocalDescription() [answer:%o]', answer);
        await this._pc.setLocalDescription(answer);
    }
    async getReceiverStats(localId) {
        this.assertNotClosed();
        this.assertRecvDirection();
        const transceiver = this._mapMidTransceiver.get(localId);
        if (!transceiver) {
            throw new Error('associated RTCRtpTransceiver not found');
        }
        return transceiver.receiver.getStats();
    }
    async receiveDataChannel({ maxMessageSize, sctpStreamParameters, label, protocol, }) {
        this.assertNotClosed();
        this.assertRecvDirection();
        const { streamId, ordered, maxPacketLifeTime, maxRetransmits, } = sctpStreamParameters;
        const options = {
            negotiated: true,
            id: streamId,
            ordered,
            maxPacketLifeTime,
            maxRetransmits,
            protocol,
        };
        logger.debug('receiveDataChannel() [options:%o]', options);
        const dataChannel = this._pc.createDataChannel(label, options);
        // If this is the first DataChannel we need to create the SDP offer with
        // m=application section.
        if (!this._hasDataChannelMediaSection) {
            this._remoteSdp.receiveSctpAssociation();
            const offer = {
                type: 'offer',
                sdp: this._remoteSdp.getSdp(),
            };
            logger.debug('receiveDataChannel() | calling pc.setRemoteDescription() [offer:%o]', offer);
            await this._pc.setRemoteDescription(offer);
            let answer = await this._pc.createAnswer();
            const localSdpObject = sdpTransform.parse(answer.sdp);
            const answerMediaObject = localSdpObject.media.find(m => m.type === 'application');
            if (typeof maxMessageSize === 'number') {
                answerMediaObject.maxMessageSize = maxMessageSize;
            }
            if (!this._transportReady) {
                await this.setupTransport({
                    localDtlsRole: this._forcedLocalDtlsRole ?? 'client',
                    localSdpObject,
                });
            }
            answer = {
                type: 'answer',
                sdp: sdpTransform.write(localSdpObject),
            };
            logger.debug('receiveDataChannel() | calling pc.setLocalDescription() [answer:%o]', answer);
            await this._pc.setLocalDescription(answer);
            this._hasDataChannelMediaSection = true;
        }
        return { dataChannel };
    }
    getDataChannelMaxMessageSize() {
        return this._direction === 'send'
            ? this._pc.sctp?.maxMessageSize
            : this._sctpMaxReceiveMessageSize;
    }
    async setupTransport({ localDtlsRole, localSdpObject, }) {
        if (!localSdpObject) {
            localSdpObject = sdpTransform.parse(this._pc.localDescription.sdp);
        }
        // Get our local DTLS parameters.
        const dtlsParameters = sdpCommonUtils.extractDtlsParameters({
            sdpObject: localSdpObject,
        });
        // Set our DTLS role.
        dtlsParameters.role = localDtlsRole;
        // Update the remote DTLS role in the SDP.
        this._remoteSdp.updateDtlsRole(localDtlsRole === 'client' ? 'server' : 'client');
        // Need to tell the remote transport about our parameters.
        await new Promise((resolve, reject) => {
            this.safeEmit('@connect', { dtlsParameters }, resolve, reject);
        });
        this._transportReady = true;
    }
    onIceGatheringStateChange = () => {
        this.emit('@icegatheringstatechange', this._pc.iceGatheringState);
    };
    onIceCandidateError = (event) => {
        this.emit('@icecandidateerror', event);
    };
    onConnectionStateChange = () => {
        this.emit('@connectionstatechange', this._pc.connectionState);
    };
    onIceConnectionStateChange = () => {
        switch (this._pc.iceConnectionState) {
            case 'checking': {
                this.emit('@connectionstatechange', 'connecting');
                break;
            }
            case 'connected':
            case 'completed': {
                this.emit('@connectionstatechange', 'connected');
                break;
            }
            case 'failed': {
                this.emit('@connectionstatechange', 'failed');
                break;
            }
            case 'disconnected': {
                this.emit('@connectionstatechange', 'disconnected');
                break;
            }
            case 'closed': {
                this.emit('@connectionstatechange', 'closed');
                break;
            }
        }
    };
    assertNotClosed() {
        if (this._closed) {
            throw new errors_1.InvalidStateError('method called in a closed handler');
        }
    }
    assertSendDirection() {
        if (this._direction !== 'send') {
            throw new Error('method can just be called for handlers with "send" direction');
        }
    }
    assertRecvDirection() {
        if (this._direction !== 'recv') {
            throw new Error('method can just be called for handlers with "recv" direction');
        }
    }
}
exports.ReactNative106 = ReactNative106;

},
20: function(module, exports, __req) {
"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.ProfileLevelId = exports.Level = exports.Profile = void 0;
exports.parseProfileLevelId = parseProfileLevelId;
exports.profileLevelIdToString = profileLevelIdToString;
exports.profileToString = profileToString;
exports.levelToString = levelToString;
exports.parseSdpProfileLevelId = parseSdpProfileLevelId;
exports.isSameProfile = isSameProfile;
exports.isSameProfileAndLevel = isSameProfileAndLevel;
exports.generateProfileLevelIdStringForAnswer = generateProfileLevelIdStringForAnswer;
exports.supportedLevel = supportedLevel;
const Logger_1 = __req(35);
const logger = new Logger_1.Logger();
/**
 * Supported profiles.
 */
var Profile;
(function (Profile) {
    Profile[Profile["ConstrainedBaseline"] = 1] = "ConstrainedBaseline";
    Profile[Profile["Baseline"] = 2] = "Baseline";
    Profile[Profile["Main"] = 3] = "Main";
    Profile[Profile["ConstrainedHigh"] = 4] = "ConstrainedHigh";
    Profile[Profile["High"] = 5] = "High";
    Profile[Profile["PredictiveHigh444"] = 6] = "PredictiveHigh444";
})(Profile || (exports.Profile = Profile = {}));
/**
 * Supported levels.
 */
var Level;
(function (Level) {
    Level[Level["L1_b"] = 0] = "L1_b";
    Level[Level["L1"] = 10] = "L1";
    Level[Level["L1_1"] = 11] = "L1_1";
    Level[Level["L1_2"] = 12] = "L1_2";
    Level[Level["L1_3"] = 13] = "L1_3";
    Level[Level["L2"] = 20] = "L2";
    Level[Level["L2_1"] = 21] = "L2_1";
    Level[Level["L2_2"] = 22] = "L2_2";
    Level[Level["L3"] = 30] = "L3";
    Level[Level["L3_1"] = 31] = "L3_1";
    Level[Level["L3_2"] = 32] = "L3_2";
    Level[Level["L4"] = 40] = "L4";
    Level[Level["L4_1"] = 41] = "L4_1";
    Level[Level["L4_2"] = 42] = "L4_2";
    Level[Level["L5"] = 50] = "L5";
    Level[Level["L5_1"] = 51] = "L5_1";
    Level[Level["L5_2"] = 52] = "L5_2";
})(Level || (exports.Level = Level = {}));
/**
 * Represents a parsed h264 profile-level-id value.
 */
class ProfileLevelId {
    profile;
    level;
    constructor(profile, level) {
        this.profile = profile;
        this.level = level;
    }
}
exports.ProfileLevelId = ProfileLevelId;
// Default ProfileLevelId.
//
// TODO: The default should really be profile Baseline and level 1 according to
// the spec: https://tools.ietf.org/html/rfc6184#section-8.1. In order to not
// break backwards compatibility with older versions of WebRTC where external
// codecs don't have any parameters, use profile ConstrainedBaseline level 3_1
// instead. This workaround will only be done in an interim period to allow
// external clients to update their code.
//
// http://crbug/webrtc/6337.
const DefaultProfileLevelId = new ProfileLevelId(Profile.ConstrainedBaseline, Level.L3_1);
/**
 * Class for matching bit patterns such as "x1xx0000" where 'x' is allowed to
 * be either 0 or 1.
 */
class BitPattern {
    mask;
    masked_value;
    constructor(str) {
        this.mask = ~byteMaskString('x', str);
        this.masked_value = byteMaskString('1', str);
    }
    isMatch(value) {
        return this.masked_value === (value & this.mask);
    }
}
/**
 * Class for converting between profile_idc/profile_iop to Profile.
 */
class ProfilePattern {
    profile_idc;
    profile_iop;
    profile;
    constructor(profile_idc, profile_iop, profile) {
        this.profile_idc = profile_idc;
        this.profile_iop = profile_iop;
        this.profile = profile;
    }
}
// This is from https://tools.ietf.org/html/rfc6184#section-8.1.
const ProfilePatterns = [
    new ProfilePattern(0x42, new BitPattern('x1xx0000'), Profile.ConstrainedBaseline),
    new ProfilePattern(0x4d, new BitPattern('1xxx0000'), Profile.ConstrainedBaseline),
    new ProfilePattern(0x58, new BitPattern('11xx0000'), Profile.ConstrainedBaseline),
    new ProfilePattern(0x42, new BitPattern('x0xx0000'), Profile.Baseline),
    new ProfilePattern(0x58, new BitPattern('10xx0000'), Profile.Baseline),
    new ProfilePattern(0x4d, new BitPattern('0x0x0000'), Profile.Main),
    new ProfilePattern(0x64, new BitPattern('00000000'), Profile.High),
    new ProfilePattern(0x64, new BitPattern('00001100'), Profile.ConstrainedHigh),
    new ProfilePattern(0xf4, new BitPattern('00000000'), Profile.PredictiveHigh444),
];
// This is from ITU-T H.264 (02/2016) Table A-1 – Level limits.
const LevelConstraints = [
    {
        max_macroblocks_per_second: 1485,
        max_macroblock_frame_size: 99,
        level: Level.L1,
    },
    {
        max_macroblocks_per_second: 1485,
        max_macroblock_frame_size: 99,
        level: Level.L1_b,
    },
    {
        max_macroblocks_per_second: 3000,
        max_macroblock_frame_size: 396,
        level: Level.L1_1,
    },
    {
        max_macroblocks_per_second: 6000,
        max_macroblock_frame_size: 396,
        level: Level.L1_2,
    },
    {
        max_macroblocks_per_second: 11880,
        max_macroblock_frame_size: 396,
        level: Level.L1_3,
    },
    {
        max_macroblocks_per_second: 11880,
        max_macroblock_frame_size: 396,
        level: Level.L2,
    },
    {
        max_macroblocks_per_second: 19800,
        max_macroblock_frame_size: 792,
        level: Level.L2_1,
    },
    {
        max_macroblocks_per_second: 20250,
        max_macroblock_frame_size: 1620,
        level: Level.L2_2,
    },
    {
        max_macroblocks_per_second: 40500,
        max_macroblock_frame_size: 1620,
        level: Level.L3,
    },
    {
        max_macroblocks_per_second: 108000,
        max_macroblock_frame_size: 3600,
        level: Level.L3_1,
    },
    {
        max_macroblocks_per_second: 216000,
        max_macroblock_frame_size: 5120,
        level: Level.L3_2,
    },
    {
        max_macroblocks_per_second: 245760,
        max_macroblock_frame_size: 8192,
        level: Level.L4,
    },
    {
        max_macroblocks_per_second: 245760,
        max_macroblock_frame_size: 8192,
        level: Level.L4_1,
    },
    {
        max_macroblocks_per_second: 522240,
        max_macroblock_frame_size: 8704,
        level: Level.L4_2,
    },
    {
        max_macroblocks_per_second: 589824,
        max_macroblock_frame_size: 22080,
        level: Level.L5,
    },
    {
        max_macroblocks_per_second: 983040,
        max_macroblock_frame_size: 36864,
        level: Level.L5_1,
    },
    {
        max_macroblocks_per_second: 2073600,
        max_macroblock_frame_size: 36864,
        level: Level.L5_2,
    },
];
/**
 * Parse profile level id that is represented as a string of 3 hex bytes.
 * Nothing will be returned if the string is not a recognized H264 profile
 * level id.
 */
function parseProfileLevelId(str) {
    // For level_idc=11 and profile_idc=0x42, 0x4D, or 0x58, the constraint set3
    // flag specifies if level 1b or level 1.1 is used.
    const ConstraintSet3Flag = 0x10;
    // The string should consist of 3 bytes in hexadecimal format.
    if (typeof str !== 'string' || str.length !== 6) {
        return undefined;
    }
    const profile_level_id_numeric = parseInt(str, 16);
    if (profile_level_id_numeric === 0) {
        return undefined;
    }
    // Separate into three bytes.
    const level_idc = profile_level_id_numeric & 0xff;
    const profile_iop = (profile_level_id_numeric >> 8) & 0xff;
    const profile_idc = (profile_level_id_numeric >> 16) & 0xff;
    // Parse level based on level_idc and constraint set 3 flag.
    let level;
    switch (level_idc) {
        case Level.L1_1: {
            level =
                (profile_iop & ConstraintSet3Flag) !== 0 ? Level.L1_b : Level.L1_1;
            break;
        }
        case Level.L1:
        case Level.L1_2:
        case Level.L1_3:
        case Level.L2:
        case Level.L2_1:
        case Level.L2_2:
        case Level.L3:
        case Level.L3_1:
        case Level.L3_2:
        case Level.L4:
        case Level.L4_1:
        case Level.L4_2:
        case Level.L5:
        case Level.L5_1:
        case Level.L5_2: {
            level = level_idc;
            break;
        }
        // Unrecognized level_idc.
        default: {
            logger.warn(`parseProfileLevelId() | unrecognized level_idc [str:${str}, level_idc:${level_idc}]`);
            return undefined;
        }
    }
    // Parse profile_idc/profile_iop into a Profile enum.
    for (const pattern of ProfilePatterns) {
        if (profile_idc === pattern.profile_idc &&
            pattern.profile_iop.isMatch(profile_iop)) {
            logger.debug(`parseProfileLevelId() | result [str:${str}, profile:${pattern.profile}, level:${level}]`);
            return new ProfileLevelId(pattern.profile, level);
        }
    }
    logger.warn(`parseProfileLevelId() | unrecognized profile_idc/profile_iop combination [str:${str}, profile_idc:${profile_idc}, profile_iop:${profile_iop}]`);
    return undefined;
}
/**
 * Returns canonical string representation as three hex bytes of the profile
 * level id, or returns nothing for invalid profile level ids.
 */
function profileLevelIdToString(profile_level_id) {
    // Handle special case level == 1b.
    if (profile_level_id.level == Level.L1_b) {
        switch (profile_level_id.profile) {
            case Profile.ConstrainedBaseline: {
                return '42f00b';
            }
            case Profile.Baseline: {
                return '42100b';
            }
            case Profile.Main: {
                return '4d100b';
            }
            // Level 1_b is not allowed for other profiles.
            default: {
                logger.warn(`profileLevelIdToString() | Level 1_b not is allowed for profile ${profile_level_id.profile}`);
                return undefined;
            }
        }
    }
    let profile_idc_iop_string;
    switch (profile_level_id.profile) {
        case Profile.ConstrainedBaseline: {
            profile_idc_iop_string = '42e0';
            break;
        }
        case Profile.Baseline: {
            profile_idc_iop_string = '4200';
            break;
        }
        case Profile.Main: {
            profile_idc_iop_string = '4d00';
            break;
        }
        case Profile.ConstrainedHigh: {
            profile_idc_iop_string = '640c';
            break;
        }
        case Profile.High: {
            profile_idc_iop_string = '6400';
            break;
        }
        case Profile.PredictiveHigh444: {
            profile_idc_iop_string = 'f400';
            break;
        }
        default: {
            logger.warn(`profileLevelIdToString() | unrecognized profile ${profile_level_id.profile}`);
            return undefined;
        }
    }
    let levelStr = profile_level_id.level.toString(16);
    if (levelStr.length === 1) {
        levelStr = `0${levelStr}`;
    }
    return `${profile_idc_iop_string}${levelStr}`;
}
/**
 * Returns a human friendly name for the given profile.
 */
function profileToString(profile) {
    switch (profile) {
        case Profile.ConstrainedBaseline: {
            return 'ConstrainedBaseline';
        }
        case Profile.Baseline: {
            return 'Baseline';
        }
        case Profile.Main: {
            return 'Main';
        }
        case Profile.ConstrainedHigh: {
            return 'ConstrainedHigh';
        }
        case Profile.High: {
            return 'High';
        }
        case Profile.PredictiveHigh444: {
            return 'PredictiveHigh444';
        }
        default: {
            logger.warn(`profileToString() | unrecognized profile ${profile}`);
            return undefined;
        }
    }
}
/**
 * Returns a human friendly name for the given level.
 */
function levelToString(level) {
    switch (level) {
        case Level.L1_b: {
            return '1b';
        }
        case Level.L1: {
            return '1';
        }
        case Level.L1_1: {
            return '1.1';
        }
        case Level.L1_2: {
            return '1.2';
        }
        case Level.L1_3: {
            return '1.3';
        }
        case Level.L2: {
            return '2';
        }
        case Level.L2_1: {
            return '2.1';
        }
        case Level.L2_2: {
            return '2.2';
        }
        case Level.L3: {
            return '3';
        }
        case Level.L3_1: {
            return '3.1';
        }
        case Level.L3_2: {
            return '3.2';
        }
        case Level.L4: {
            return '4';
        }
        case Level.L4_1: {
            return '4.1';
        }
        case Level.L4_2: {
            return '4.2';
        }
        case Level.L5: {
            return '5';
        }
        case Level.L5_1: {
            return '5.1';
        }
        case Level.L5_2: {
            return '5.2';
        }
        default: {
            logger.warn(`levelToString() | unrecognized level ${level}`);
            return undefined;
        }
    }
}
/**
 * Parse profile level id that is represented as a string of 3 hex bytes
 * contained in an SDP key-value map. A default profile level id will be
 * returned if the profile-level-id key is missing. Nothing will be returned
 * if the key is present but the string is invalid.
 */
function parseSdpProfileLevelId(params = {}) {
    const profile_level_id = params['profile-level-id'];
    return profile_level_id
        ? parseProfileLevelId(profile_level_id)
        : DefaultProfileLevelId;
}
/**
 * Returns true if the codec parameters have the same H264 profile, i.e. the
 * same H264 profile (Baseline, High, etc).
 */
function isSameProfile(params1 = {}, params2 = {}) {
    const profile_level_id_1 = parseSdpProfileLevelId(params1);
    const profile_level_id_2 = parseSdpProfileLevelId(params2);
    // Compare H264 profiles, but not levels.
    return Boolean(profile_level_id_1 &&
        profile_level_id_1.profile === profile_level_id_2?.profile);
}
/**
 * Returns true if the codec parameters have the same H264 profile, i.e. the
 * same H264 profile (Baseline, High, etc) and same level.
 */
function isSameProfileAndLevel(params1 = {}, params2 = {}) {
    const profile_level_id_1 = parseSdpProfileLevelId(params1);
    const profile_level_id_2 = parseSdpProfileLevelId(params2);
    // Compare H264 profiles, but not levels.
    return Boolean(profile_level_id_1 &&
        profile_level_id_1.profile === profile_level_id_2?.profile &&
        profile_level_id_1.level == profile_level_id_2?.level);
}
/**
 * Generate codec parameters that will be used as answer in an SDP negotiation
 * based on local supported parameters and remote offered parameters. Both
 * local_supported_params and remote_offered_params represent sendrecv media
 * descriptions, i.e they are a mix of both encode and decode capabilities. In
 * theory, when the profile in local_supported_params represent a strict
 * superset of the profile in remote_offered_params, we could limit the profile
 * in the answer to the profile in remote_offered_params.
 *
 * However, to simplify the code, each supported H264 profile should be listed
 * explicitly in the list of local supported codecs, even if they are redundant.
 * Then each local codec in the list should be tested one at a time against the
 * remote codec, and only when the profiles are equal should this function be
 * called. Therefore, this function does not need to handle profile intersection,
 * and the profile of local_supported_params and remote_offered_params must be
 * equal before calling this function. The parameters that are used when
 * negotiating are the level part of profile-level-id and
 * level-asymmetry-allowed.
 */
function generateProfileLevelIdStringForAnswer(local_supported_params = {}, remote_offered_params = {}) {
    // If both local and remote params do not contain profile-level-id, they are
    // both using the default profile. In this case, don't return anything.
    if (!local_supported_params['profile-level-id'] &&
        !remote_offered_params['profile-level-id']) {
        logger.warn('generateProfileLevelIdStringForAnswer() | profile-level-id missing in local and remote params');
        return undefined;
    }
    // Parse profile-level-ids.
    const local_profile_level_id = parseSdpProfileLevelId(local_supported_params);
    const remote_profile_level_id = parseSdpProfileLevelId(remote_offered_params);
    // The local and remote codec must have valid and equal H264 Profiles.
    if (!local_profile_level_id) {
        throw new TypeError('invalid local_profile_level_id');
    }
    if (!remote_profile_level_id) {
        throw new TypeError('invalid remote_profile_level_id');
    }
    if (local_profile_level_id.profile !== remote_profile_level_id.profile) {
        throw new TypeError('H264 Profile mismatch');
    }
    // Parse level information.
    const level_asymmetry_allowed = isLevelAsymmetryAllowed(local_supported_params) &&
        isLevelAsymmetryAllowed(remote_offered_params);
    const local_level = local_profile_level_id.level;
    const remote_level = remote_profile_level_id.level;
    const min_level = minLevel(local_level, remote_level);
    // Determine answer level. When level asymmetry is not allowed, level upgrade
    // is not allowed, i.e., the level in the answer must be equal to or lower
    // than the level in the offer.
    const answer_level = level_asymmetry_allowed ? local_level : min_level;
    logger.debug(`generateProfileLevelIdStringForAnswer() | result [profile:${local_profile_level_id.profile}, level:${answer_level}]`);
    // Return the resulting profile-level-id for the answer parameters.
    return profileLevelIdToString(new ProfileLevelId(local_profile_level_id.profile, answer_level));
}
/**
 * Given that a decoder supports up to a given frame size (in pixels) at up to
 * a given number of frames per second, return the highest H264 level where it
 * can guarantee that it will be able to support all valid encoded streams that
 * are within that level.
 */
function supportedLevel(max_frame_pixel_count, max_fps) {
    const PixelsPerMacroblock = 16 * 16;
    for (let i = LevelConstraints.length - 1; i >= 0; --i) {
        const level_constraint = LevelConstraints[i];
        if (level_constraint.max_macroblock_frame_size * PixelsPerMacroblock <=
            max_frame_pixel_count &&
            level_constraint.max_macroblocks_per_second <=
                max_fps * level_constraint.max_macroblock_frame_size) {
            logger.debug(`supportedLevel() | result [max_frame_pixel_count:${max_frame_pixel_count}, max_fps:${max_fps}, level:${level_constraint.level}]`);
            return level_constraint.level;
        }
    }
    // No level supported.
    logger.warn(`supportedLevel() | no level supported [max_frame_pixel_count:${max_frame_pixel_count}, max_fps:${max_fps}]`);
    return undefined;
}
/**
 * Convert a string of 8 characters into a byte where the positions containing
 * character c will have their bit set. For example, c = 'x', str = "x1xx0000"
 * will return 0b10110000.
 */
function byteMaskString(c, str) {
    return ((Number(str[0] === c) << 7) |
        (Number(str[1] === c) << 6) |
        (Number(str[2] === c) << 5) |
        (Number(str[3] === c) << 4) |
        (Number(str[4] === c) << 3) |
        (Number(str[5] === c) << 2) |
        (Number(str[6] === c) << 1) |
        (Number(str[7] === c) << 0));
}
// Compare H264 levels and handle the level 1b case.
function isLessLevel(a, b) {
    if (a === Level.L1_b) {
        return b !== Level.L1 && b !== Level.L1_b;
    }
    if (b === Level.L1_b) {
        return a !== Level.L1;
    }
    return a < b;
}
function minLevel(a, b) {
    return isLessLevel(a, b) ? a : b;
}
function isLevelAsymmetryAllowed(params = {}) {
    const level_asymmetry_allowed = params['level-asymmetry-allowed'];
    return (level_asymmetry_allowed === true ||
        level_asymmetry_allowed === 1 ||
        level_asymmetry_allowed === '1');
}

},
21: function(module, exports, __req) {
// Copyright Joyent, Inc. and other Node contributors.
//
// Permission is hereby granted, free of charge, to any person obtaining a
// copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to permit
// persons to whom the Software is furnished to do so, subject to the
// following conditions:
//
// The above copyright notice and this permission notice shall be included
// in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS
// OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN
// NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
// DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE
// USE OR OTHER DEALINGS IN THE SOFTWARE.

'use strict';

var R = typeof Reflect === 'object' ? Reflect : null
var ReflectApply = R && typeof R.apply === 'function'
  ? R.apply
  : function ReflectApply(target, receiver, args) {
    return Function.prototype.apply.call(target, receiver, args);
  }

var ReflectOwnKeys
if (R && typeof R.ownKeys === 'function') {
  ReflectOwnKeys = R.ownKeys
} else if (Object.getOwnPropertySymbols) {
  ReflectOwnKeys = function ReflectOwnKeys(target) {
    return Object.getOwnPropertyNames(target)
      .concat(Object.getOwnPropertySymbols(target));
  };
} else {
  ReflectOwnKeys = function ReflectOwnKeys(target) {
    return Object.getOwnPropertyNames(target);
  };
}

function ProcessEmitWarning(warning) {
  if (console && console.warn) console.warn(warning);
}

var NumberIsNaN = Number.isNaN || function NumberIsNaN(value) {
  return value !== value;
}

function EventEmitter() {
  EventEmitter.init.call(this);
}
module.exports = EventEmitter;
module.exports.once = once;

// Backwards-compat with node 0.10.x
EventEmitter.EventEmitter = EventEmitter;

EventEmitter.prototype._events = undefined;
EventEmitter.prototype._eventsCount = 0;
EventEmitter.prototype._maxListeners = undefined;

// By default EventEmitters will print a warning if more than 10 listeners are
// added to it. This is a useful default which helps finding memory leaks.
var defaultMaxListeners = 10;

function checkListener(listener) {
  if (typeof listener !== 'function') {
    throw new TypeError('The "listener" argument must be of type Function. Received type ' + typeof listener);
  }
}

Object.defineProperty(EventEmitter, 'defaultMaxListeners', {
  enumerable: true,
  get: function() {
    return defaultMaxListeners;
  },
  set: function(arg) {
    if (typeof arg !== 'number' || arg < 0 || NumberIsNaN(arg)) {
      throw new RangeError('The value of "defaultMaxListeners" is out of range. It must be a non-negative number. Received ' + arg + '.');
    }
    defaultMaxListeners = arg;
  }
});

EventEmitter.init = function() {

  if (this._events === undefined ||
      this._events === Object.getPrototypeOf(this)._events) {
    this._events = Object.create(null);
    this._eventsCount = 0;
  }

  this._maxListeners = this._maxListeners || undefined;
};

// Obviously not all Emitters should be limited to 10. This function allows
// that to be increased. Set to zero for unlimited.
EventEmitter.prototype.setMaxListeners = function setMaxListeners(n) {
  if (typeof n !== 'number' || n < 0 || NumberIsNaN(n)) {
    throw new RangeError('The value of "n" is out of range. It must be a non-negative number. Received ' + n + '.');
  }
  this._maxListeners = n;
  return this;
};

function _getMaxListeners(that) {
  if (that._maxListeners === undefined)
    return EventEmitter.defaultMaxListeners;
  return that._maxListeners;
}

EventEmitter.prototype.getMaxListeners = function getMaxListeners() {
  return _getMaxListeners(this);
};

EventEmitter.prototype.emit = function emit(type) {
  var args = [];
  for (var i = 1; i < arguments.length; i++) args.push(arguments[i]);
  var doError = (type === 'error');

  var events = this._events;
  if (events !== undefined)
    doError = (doError && events.error === undefined);
  else if (!doError)
    return false;

  // If there is no 'error' event listener then throw.
  if (doError) {
    var er;
    if (args.length > 0)
      er = args[0];
    if (er instanceof Error) {
      // Note: The comments on the `throw` lines are intentional, they show
      // up in Node's output if this results in an unhandled exception.
      throw er; // Unhandled 'error' event
    }
    // At least give some kind of context to the user
    var err = new Error('Unhandled error.' + (er ? ' (' + er.message + ')' : ''));
    err.context = er;
    throw err; // Unhandled 'error' event
  }

  var handler = events[type];

  if (handler === undefined)
    return false;

  if (typeof handler === 'function') {
    ReflectApply(handler, this, args);
  } else {
    var len = handler.length;
    var listeners = arrayClone(handler, len);
    for (var i = 0; i < len; ++i)
      ReflectApply(listeners[i], this, args);
  }

  return true;
};

function _addListener(target, type, listener, prepend) {
  var m;
  var events;
  var existing;

  checkListener(listener);

  events = target._events;
  if (events === undefined) {
    events = target._events = Object.create(null);
    target._eventsCount = 0;
  } else {
    // To avoid recursion in the case that type === "newListener"! Before
    // adding it to the listeners, first emit "newListener".
    if (events.newListener !== undefined) {
      target.emit('newListener', type,
                  listener.listener ? listener.listener : listener);

      // Re-assign `events` because a newListener handler could have caused the
      // this._events to be assigned to a new object
      events = target._events;
    }
    existing = events[type];
  }

  if (existing === undefined) {
    // Optimize the case of one listener. Don't need the extra array object.
    existing = events[type] = listener;
    ++target._eventsCount;
  } else {
    if (typeof existing === 'function') {
      // Adding the second element, need to change to array.
      existing = events[type] =
        prepend ? [listener, existing] : [existing, listener];
      // If we've already got an array, just append.
    } else if (prepend) {
      existing.unshift(listener);
    } else {
      existing.push(listener);
    }

    // Check for listener leak
    m = _getMaxListeners(target);
    if (m > 0 && existing.length > m && !existing.warned) {
      existing.warned = true;
      // No error code for this since it is a Warning
      // eslint-disable-next-line no-restricted-syntax
      var w = new Error('Possible EventEmitter memory leak detected. ' +
                          existing.length + ' ' + String(type) + ' listeners ' +
                          'added. Use emitter.setMaxListeners() to ' +
                          'increase limit');
      w.name = 'MaxListenersExceededWarning';
      w.emitter = target;
      w.type = type;
      w.count = existing.length;
      ProcessEmitWarning(w);
    }
  }

  return target;
}

EventEmitter.prototype.addListener = function addListener(type, listener) {
  return _addListener(this, type, listener, false);
};

EventEmitter.prototype.on = EventEmitter.prototype.addListener;

EventEmitter.prototype.prependListener =
    function prependListener(type, listener) {
      return _addListener(this, type, listener, true);
    };

function onceWrapper() {
  if (!this.fired) {
    this.target.removeListener(this.type, this.wrapFn);
    this.fired = true;
    if (arguments.length === 0)
      return this.listener.call(this.target);
    return this.listener.apply(this.target, arguments);
  }
}

function _onceWrap(target, type, listener) {
  var state = { fired: false, wrapFn: undefined, target: target, type: type, listener: listener };
  var wrapped = onceWrapper.bind(state);
  wrapped.listener = listener;
  state.wrapFn = wrapped;
  return wrapped;
}

EventEmitter.prototype.once = function once(type, listener) {
  checkListener(listener);
  this.on(type, _onceWrap(this, type, listener));
  return this;
};

EventEmitter.prototype.prependOnceListener =
    function prependOnceListener(type, listener) {
      checkListener(listener);
      this.prependListener(type, _onceWrap(this, type, listener));
      return this;
    };

// Emits a 'removeListener' event if and only if the listener was removed.
EventEmitter.prototype.removeListener =
    function removeListener(type, listener) {
      var list, events, position, i, originalListener;

      checkListener(listener);

      events = this._events;
      if (events === undefined)
        return this;

      list = events[type];
      if (list === undefined)
        return this;

      if (list === listener || list.listener === listener) {
        if (--this._eventsCount === 0)
          this._events = Object.create(null);
        else {
          delete events[type];
          if (events.removeListener)
            this.emit('removeListener', type, list.listener || listener);
        }
      } else if (typeof list !== 'function') {
        position = -1;

        for (i = list.length - 1; i >= 0; i--) {
          if (list[i] === listener || list[i].listener === listener) {
            originalListener = list[i].listener;
            position = i;
            break;
          }
        }

        if (position < 0)
          return this;

        if (position === 0)
          list.shift();
        else {
          spliceOne(list, position);
        }

        if (list.length === 1)
          events[type] = list[0];

        if (events.removeListener !== undefined)
          this.emit('removeListener', type, originalListener || listener);
      }

      return this;
    };

EventEmitter.prototype.off = EventEmitter.prototype.removeListener;

EventEmitter.prototype.removeAllListeners =
    function removeAllListeners(type) {
      var listeners, events, i;

      events = this._events;
      if (events === undefined)
        return this;

      // not listening for removeListener, no need to emit
      if (events.removeListener === undefined) {
        if (arguments.length === 0) {
          this._events = Object.create(null);
          this._eventsCount = 0;
        } else if (events[type] !== undefined) {
          if (--this._eventsCount === 0)
            this._events = Object.create(null);
          else
            delete events[type];
        }
        return this;
      }

      // emit removeListener for all listeners on all events
      if (arguments.length === 0) {
        var keys = Object.keys(events);
        var key;
        for (i = 0; i < keys.length; ++i) {
          key = keys[i];
          if (key === 'removeListener') continue;
          this.removeAllListeners(key);
        }
        this.removeAllListeners('removeListener');
        this._events = Object.create(null);
        this._eventsCount = 0;
        return this;
      }

      listeners = events[type];

      if (typeof listeners === 'function') {
        this.removeListener(type, listeners);
      } else if (listeners !== undefined) {
        // LIFO order
        for (i = listeners.length - 1; i >= 0; i--) {
          this.removeListener(type, listeners[i]);
        }
      }

      return this;
    };

function _listeners(target, type, unwrap) {
  var events = target._events;

  if (events === undefined)
    return [];

  var evlistener = events[type];
  if (evlistener === undefined)
    return [];

  if (typeof evlistener === 'function')
    return unwrap ? [evlistener.listener || evlistener] : [evlistener];

  return unwrap ?
    unwrapListeners(evlistener) : arrayClone(evlistener, evlistener.length);
}

EventEmitter.prototype.listeners = function listeners(type) {
  return _listeners(this, type, true);
};

EventEmitter.prototype.rawListeners = function rawListeners(type) {
  return _listeners(this, type, false);
};

EventEmitter.listenerCount = function(emitter, type) {
  if (typeof emitter.listenerCount === 'function') {
    return emitter.listenerCount(type);
  } else {
    return listenerCount.call(emitter, type);
  }
};

EventEmitter.prototype.listenerCount = listenerCount;
function listenerCount(type) {
  var events = this._events;

  if (events !== undefined) {
    var evlistener = events[type];

    if (typeof evlistener === 'function') {
      return 1;
    } else if (evlistener !== undefined) {
      return evlistener.length;
    }
  }

  return 0;
}

EventEmitter.prototype.eventNames = function eventNames() {
  return this._eventsCount > 0 ? ReflectOwnKeys(this._events) : [];
};

function arrayClone(arr, n) {
  var copy = new Array(n);
  for (var i = 0; i < n; ++i)
    copy[i] = arr[i];
  return copy;
}

function spliceOne(list, index) {
  for (; index + 1 < list.length; index++)
    list[index] = list[index + 1];
  list.pop();
}

function unwrapListeners(arr) {
  var ret = new Array(arr.length);
  for (var i = 0; i < ret.length; ++i) {
    ret[i] = arr[i].listener || arr[i];
  }
  return ret;
}

function once(emitter, name) {
  return new Promise(function (resolve, reject) {
    function errorListener(err) {
      emitter.removeListener(name, resolver);
      reject(err);
    }

    function resolver() {
      if (typeof emitter.removeListener === 'function') {
        emitter.removeListener('error', errorListener);
      }
      resolve([].slice.call(arguments));
    };

    eventTargetAgnosticAddListener(emitter, name, resolver, { once: true });
    if (name !== 'error') {
      addErrorHandlerIfEventEmitter(emitter, errorListener, { once: true });
    }
  });
}

function addErrorHandlerIfEventEmitter(emitter, handler, flags) {
  if (typeof emitter.on === 'function') {
    eventTargetAgnosticAddListener(emitter, 'error', handler, flags);
  }
}

function eventTargetAgnosticAddListener(emitter, name, listener, flags) {
  if (typeof emitter.on === 'function') {
    if (flags.once) {
      emitter.once(name, listener);
    } else {
      emitter.on(name, listener);
    }
  } else if (typeof emitter.addEventListener === 'function') {
    // EventTarget does not have `error` event semantics like Node
    // EventEmitters, we do not listen for `error` events here.
    emitter.addEventListener(name, function wrapListener(arg) {
      // IE does not have builtin `{ once: true }` support so we
      // have to do it manually.
      if (flags.once) {
        emitter.removeEventListener(name, wrapListener);
      }
      listener(arg);
    });
  } else {
    throw new TypeError('The "emitter" argument must be of type EventEmitter. Received type ' + typeof emitter);
  }
}

},
22: function(module, exports, __req) {
"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.FakeMediaStreamTrack = void 0;
const uuid_1 = __req(36);
const FakeEventTarget_1 = __req(37);
const FakeEvent_1 = __req(38);
const utils_1 = __req(39);
class FakeMediaStreamTrack extends FakeEventTarget_1.FakeEventTarget {
    #id;
    #kind;
    #label;
    #readyState;
    #enabled;
    #muted;
    #contentHint;
    #capabilities;
    #constraints;
    #settings;
    #data;
    // Events.
    #onmute = null;
    #onunmute = null;
    #onended = null;
    // Custom events.
    #onenabledchange = null;
    #onstopped = null;
    constructor({ kind, id, label, contentHint, enabled, muted, readyState, capabilities, constraints, settings, data, }) {
        super();
        this.#id = id ?? (0, uuid_1.v4)();
        this.#kind = kind;
        this.#label = label ?? '';
        this.#contentHint = contentHint ?? '';
        this.#enabled = enabled ?? true;
        this.#muted = muted ?? false;
        this.#readyState = readyState ?? 'live';
        this.#capabilities = capabilities ?? {};
        this.#constraints = constraints ?? {};
        this.#settings = settings ?? {};
        this.#data = data ?? {};
    }
    get id() {
        return this.#id;
    }
    get kind() {
        return this.#kind;
    }
    get label() {
        return this.#label;
    }
    get contentHint() {
        return this.#contentHint;
    }
    set contentHint(contentHint) {
        this.#contentHint = contentHint;
    }
    get enabled() {
        return this.#enabled;
    }
    /**
     * Changes `enabled` member value and fires a custom "enabledchange" event.
     */
    set enabled(enabled) {
        const changed = this.#enabled !== enabled;
        this.#enabled = enabled;
        if (changed) {
            this.dispatchEvent(new FakeEvent_1.FakeEvent('enabledchange'));
        }
    }
    get muted() {
        return this.#muted;
    }
    get readyState() {
        return this.#readyState;
    }
    /**
     * Application custom data getter.
     */
    get data() {
        return this.#data;
    }
    /**
     * Application custom data setter.
     */
    set data(data) {
        this.#data = data;
    }
    get onmute() {
        return this.#onmute;
    }
    set onmute(handler) {
        if (this.#onmute) {
            this.removeEventListener('mute', this.#onmute);
        }
        this.#onmute = handler;
        if (handler) {
            this.addEventListener('mute', handler);
        }
    }
    get onunmute() {
        return this.#onunmute;
    }
    set onunmute(handler) {
        if (this.#onunmute) {
            this.removeEventListener('unmute', this.#onunmute);
        }
        this.#onunmute = handler;
        if (handler) {
            this.addEventListener('unmute', handler);
        }
    }
    get onended() {
        return this.#onended;
    }
    set onended(handler) {
        if (this.#onended) {
            this.removeEventListener('ended', this.#onended);
        }
        this.#onended = handler;
        if (handler) {
            this.addEventListener('ended', handler);
        }
    }
    get onenabledchange() {
        return this.#onenabledchange;
    }
    set onenabledchange(handler) {
        if (this.#onenabledchange) {
            this.removeEventListener('enabledchange', this.#onenabledchange);
        }
        this.#onenabledchange = handler;
        if (handler) {
            this.addEventListener('enabledchange', handler);
        }
    }
    get onstopped() {
        return this.#onstopped;
    }
    set onstopped(handler) {
        if (this.#onstopped) {
            this.removeEventListener('stopped', this.#onstopped);
        }
        this.#onstopped = handler;
        if (handler) {
            this.addEventListener('stopped', handler);
        }
    }
    addEventListener(type, listener, options) {
        super.addEventListener(type, listener, options);
    }
    removeEventListener(type, listener, options) {
        super.removeEventListener(type, listener, options);
    }
    /**
     * Changes `readyState` member to "ended" and fires a custom "stopped" event
     * (if not already stopped).
     */
    stop() {
        if (this.#readyState === 'ended') {
            return;
        }
        this.#readyState = 'ended';
        this.dispatchEvent(new FakeEvent_1.FakeEvent('stopped'));
    }
    /**
     * Clones current track into another FakeMediaStreamTrack. `id` and `data`
     * can be optionally given.
     */
    clone({ id, data, } = {}) {
        return new FakeMediaStreamTrack({
            id: id ?? (0, uuid_1.v4)(),
            kind: this.#kind,
            label: this.#label,
            contentHint: this.#contentHint,
            enabled: this.#enabled,
            muted: this.#muted,
            readyState: this.#readyState,
            capabilities: (0, utils_1.clone)(this.#capabilities),
            constraints: (0, utils_1.clone)(this.#constraints),
            settings: (0, utils_1.clone)(this.#settings),
            data: data ?? (0, utils_1.clone)(this.#data),
        });
    }
    getCapabilities() {
        return this.#capabilities;
    }
    getConstraints() {
        return this.#constraints;
    }
    async applyConstraints(constraints = {}) {
        this.#constraints = constraints;
        // To make it be "more" async so ESLint doesn't complain.
        return Promise.resolve();
    }
    getSettings() {
        return this.#settings;
    }
    /**
     * Simulates a remotely triggered stop. It fires a custom "stopped" event and
     * the standard "ended" event (if the track was not already stopped).
     */
    remoteStop() {
        if (this.#readyState === 'ended') {
            return;
        }
        this.#readyState = 'ended';
        this.dispatchEvent(new FakeEvent_1.FakeEvent('stopped'));
        this.dispatchEvent(new FakeEvent_1.FakeEvent('ended'));
    }
    /**
     * Simulates a remotely triggered mute. It fires a "mute" event (if the track
     * was not already muted).
     */
    remoteMute() {
        if (this.#muted) {
            return;
        }
        this.#muted = true;
        this.dispatchEvent(new FakeEvent_1.FakeEvent('mute'));
    }
    /**
     * Simulates a remotely triggered unmute. It fires an "unmute" event (if the
     * track was muted).
     */
    remoteUnmute() {
        if (!this.#muted) {
            return;
        }
        this.#muted = false;
        this.dispatchEvent(new FakeEvent_1.FakeEvent('unmute'));
    }
}
exports.FakeMediaStreamTrack = FakeMediaStreamTrack;

},
23: function(module, exports, __req) {
"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.FakeEventTarget = void 0;
class FakeEventTarget {
    listeners = {};
    addEventListener(type, callback, options) {
        if (!callback) {
            return;
        }
        this.listeners[type] = this.listeners[type] ?? [];
        this.listeners[type].push({
            callback: typeof callback === 'function' ? callback : callback.handleEvent,
            once: typeof options === 'object' && options.once === true,
        });
    }
    removeEventListener(type, callback, 
    // eslint-disable-next-line @typescript-eslint/no-unused-vars
    options) {
        if (!this.listeners[type]) {
            return;
        }
        if (!callback) {
            return;
        }
        this.listeners[type] = this.listeners[type].filter(listener => listener.callback !==
            (typeof callback === 'function' ? callback : callback.handleEvent));
    }
    dispatchEvent(event) {
        if (!event || typeof event.type !== 'string') {
            throw new Error('invalid event object');
        }
        const entries = this.listeners[event.type];
        if (!entries) {
            return true;
        }
        for (const listener of [...entries]) {
            try {
                listener.callback.call(this, event);
            }
            catch (error) {
                // Avoid that the error breaks the iteration.
                setTimeout(() => {
                    throw error;
                }, 0);
            }
            if (listener.once) {
                this.removeEventListener(event.type, listener.callback);
            }
        }
        return !event.defaultPrevented;
    }
}
exports.FakeEventTarget = FakeEventTarget;

},
24: function(module, exports, __req) {
/**
 * Helpers.
 */

var s = 1000;
var m = s * 60;
var h = m * 60;
var d = h * 24;
var w = d * 7;
var y = d * 365.25;

/**
 * Parse or format the given `val`.
 *
 * Options:
 *
 *  - `long` verbose formatting [false]
 *
 * @param {String|Number} val
 * @param {Object} [options]
 * @throws {Error} throw an error if val is not a non-empty string or a number
 * @return {String|Number}
 * @api public
 */

module.exports = function (val, options) {
  options = options || {};
  var type = typeof val;
  if (type === 'string' && val.length > 0) {
    return parse(val);
  } else if (type === 'number' && isFinite(val)) {
    return options.long ? fmtLong(val) : fmtShort(val);
  }
  throw new Error(
    'val is not a non-empty string or a valid number. val=' +
      JSON.stringify(val)
  );
};

/**
 * Parse the given `str` and return milliseconds.
 *
 * @param {String} str
 * @return {Number}
 * @api private
 */

function parse(str) {
  str = String(str);
  if (str.length > 100) {
    return;
  }
  var match = /^(-?(?:\d+)?\.?\d+) *(milliseconds?|msecs?|ms|seconds?|secs?|s|minutes?|mins?|m|hours?|hrs?|h|days?|d|weeks?|w|years?|yrs?|y)?$/i.exec(
    str
  );
  if (!match) {
    return;
  }
  var n = parseFloat(match[1]);
  var type = (match[2] || 'ms').toLowerCase();
  switch (type) {
    case 'years':
    case 'year':
    case 'yrs':
    case 'yr':
    case 'y':
      return n * y;
    case 'weeks':
    case 'week':
    case 'w':
      return n * w;
    case 'days':
    case 'day':
    case 'd':
      return n * d;
    case 'hours':
    case 'hour':
    case 'hrs':
    case 'hr':
    case 'h':
      return n * h;
    case 'minutes':
    case 'minute':
    case 'mins':
    case 'min':
    case 'm':
      return n * m;
    case 'seconds':
    case 'second':
    case 'secs':
    case 'sec':
    case 's':
      return n * s;
    case 'milliseconds':
    case 'millisecond':
    case 'msecs':
    case 'msec':
    case 'ms':
      return n;
    default:
      return undefined;
  }
}

/**
 * Short format for `ms`.
 *
 * @param {Number} ms
 * @return {String}
 * @api private
 */

function fmtShort(ms) {
  var msAbs = Math.abs(ms);
  if (msAbs >= d) {
    return Math.round(ms / d) + 'd';
  }
  if (msAbs >= h) {
    return Math.round(ms / h) + 'h';
  }
  if (msAbs >= m) {
    return Math.round(ms / m) + 'm';
  }
  if (msAbs >= s) {
    return Math.round(ms / s) + 's';
  }
  return ms + 'ms';
}

/**
 * Long format for `ms`.
 *
 * @param {Number} ms
 * @return {String}
 * @api private
 */

function fmtLong(ms) {
  var msAbs = Math.abs(ms);
  if (msAbs >= d) {
    return plural(ms, msAbs, d, 'day');
  }
  if (msAbs >= h) {
    return plural(ms, msAbs, h, 'hour');
  }
  if (msAbs >= m) {
    return plural(ms, msAbs, m, 'minute');
  }
  if (msAbs >= s) {
    return plural(ms, msAbs, s, 'second');
  }
  return ms + ' ms';
}

/**
 * Pluralization helper.
 */

function plural(ms, msAbs, n, name) {
  var isPlural = msAbs >= n * 1.5;
  return Math.round(ms / n) + ' ' + name + (isPlural ? 's' : '');
}

},
25: function(module, exports, __req) {
"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.AwaitQueueRemovedTaskError = exports.AwaitQueueStoppedError = exports.AwaitQueue = void 0;
var AwaitQueue_1 = __req(40);
Object.defineProperty(exports, "AwaitQueue", { enumerable: true, get: function () { return AwaitQueue_1.AwaitQueue; } });
var errors_1 = __req(41);
Object.defineProperty(exports, "AwaitQueueStoppedError", { enumerable: true, get: function () { return errors_1.AwaitQueueStoppedError; } });
Object.defineProperty(exports, "AwaitQueueRemovedTaskError", { enumerable: true, get: function () { return errors_1.AwaitQueueRemovedTaskError; } });

},
26: function(module, exports, __req) {
"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.Producer = void 0;
const Logger_1 = __req(11);
const enhancedEvents_1 = __req(7);
const errors_1 = __req(12);
const logger = new Logger_1.Logger('Producer');
class Producer extends enhancedEvents_1.EnhancedEventEmitter {
    // Id.
    _id;
    // Local id.
    _localId;
    // Closed flag.
    _closed = false;
    // Associated RTCRtpSender.
    _rtpSender;
    // Local track.
    _track;
    // Producer kind.
    _kind;
    // RTP parameters.
    _rtpParameters;
    // Paused flag.
    _paused;
    // Video max spatial layer.
    _maxSpatialLayer;
    // Whether the Producer should call stop() in given tracks.
    _stopTracks;
    // Whether the Producer should set track.enabled = false when paused.
    _disableTrackOnPause;
    // Whether we should mark the transceiver as inactive when paused.
    _zeroRtpOnPause;
    // App custom data.
    _appData;
    // Observer instance.
    _observer = new enhancedEvents_1.EnhancedEventEmitter();
    constructor({ id, localId, rtpSender, track, rtpParameters, stopTracks, disableTrackOnPause, zeroRtpOnPause, appData, }) {
        super();
        logger.debug('constructor()');
        this._id = id;
        this._localId = localId;
        this._rtpSender = rtpSender;
        this._track = track;
        this._kind = track.kind;
        this._rtpParameters = rtpParameters;
        this._paused = disableTrackOnPause ? !track.enabled : false;
        this._maxSpatialLayer = undefined;
        this._stopTracks = stopTracks;
        this._disableTrackOnPause = disableTrackOnPause;
        this._zeroRtpOnPause = zeroRtpOnPause;
        this._appData = appData ?? {};
        this.onTrackEnded = this.onTrackEnded.bind(this);
        // NOTE: Minor issue. If zeroRtpOnPause is true, we cannot emit the
        // '@replacetrack' event here.
        this.handleTrack();
    }
    /**
     * Producer id.
     */
    get id() {
        return this._id;
    }
    /**
     * Local id.
     */
    get localId() {
        return this._localId;
    }
    /**
     * Whether the Producer is closed.
     */
    get closed() {
        return this._closed;
    }
    /**
     * Media kind.
     */
    get kind() {
        return this._kind;
    }
    /**
     * Associated RTCRtpSender.
     */
    get rtpSender() {
        return this._rtpSender;
    }
    /**
     * The associated track.
     */
    get track() {
        return this._track;
    }
    /**
     * RTP parameters.
     */
    get rtpParameters() {
        return this._rtpParameters;
    }
    /**
     * Whether the Producer is paused.
     */
    get paused() {
        return this._paused;
    }
    /**
     * Max spatial layer.
     *
     * @type {Number | undefined}
     */
    get maxSpatialLayer() {
        return this._maxSpatialLayer;
    }
    /**
     * App custom data.
     */
    get appData() {
        return this._appData;
    }
    /**
     * App custom data setter.
     */
    set appData(appData) {
        this._appData = appData;
    }
    get observer() {
        return this._observer;
    }
    /**
     * Closes the Producer.
     */
    close() {
        if (this._closed) {
            return;
        }
        logger.debug('close()');
        this._closed = true;
        this.destroyTrack();
        this.emit('@close');
        // Emit observer event.
        this._observer.safeEmit('close');
        // Invoke close() in EnhancedEventEmitter classes.
        super.close();
        this._observer.close();
    }
    /**
     * Transport was closed.
     */
    transportClosed() {
        if (this._closed) {
            return;
        }
        logger.debug('transportClosed()');
        this._closed = true;
        this.destroyTrack();
        this.safeEmit('transportclose');
        // Emit observer event.
        this._observer.safeEmit('close');
        // Invoke close() in EnhancedEventEmitter classes.
        super.close();
        this._observer.close();
    }
    /**
     * Get associated RTCRtpSender stats.
     */
    async getStats() {
        if (this._closed) {
            throw new errors_1.InvalidStateError('closed');
        }
        return new Promise((resolve, reject) => {
            this.safeEmit('@getstats', resolve, reject);
        });
    }
    /**
     * Pauses sending media.
     */
    pause() {
        logger.debug('pause()');
        if (this._closed) {
            logger.error('pause() | Producer closed');
            return;
        }
        this._paused = true;
        if (this._track && this._disableTrackOnPause) {
            this._track.enabled = false;
        }
        if (this._zeroRtpOnPause) {
            new Promise((resolve, reject) => {
                this.safeEmit('@pause', resolve, reject);
            }).catch(() => { });
        }
        // Emit observer event.
        this._observer.safeEmit('pause');
    }
    /**
     * Resumes sending media.
     */
    resume() {
        logger.debug('resume()');
        if (this._closed) {
            logger.error('resume() | Producer closed');
            return;
        }
        this._paused = false;
        if (this._track && this._disableTrackOnPause) {
            this._track.enabled = true;
        }
        if (this._zeroRtpOnPause) {
            new Promise((resolve, reject) => {
                this.safeEmit('@resume', resolve, reject);
            }).catch(() => { });
        }
        // Emit observer event.
        this._observer.safeEmit('resume');
    }
    /**
     * Replaces the current track with a new one or null.
     */
    async replaceTrack({ track, }) {
        logger.debug('replaceTrack() [track:%o]', track);
        if (this._closed) {
            // This must be done here. Otherwise there is no chance to stop the given
            // track.
            if (track && this._stopTracks) {
                try {
                    track.stop();
                }
                catch (error) { }
            }
            throw new errors_1.InvalidStateError('closed');
        }
        else if (track?.readyState === 'ended') {
            throw new errors_1.InvalidStateError('track ended');
        }
        // Do nothing if this is the same track as the current handled one.
        if (track === this._track) {
            logger.debug('replaceTrack() | same track, ignored');
            return;
        }
        try {
            await new Promise((resolve, reject) => {
                this.safeEmit('@replacetrack', track, resolve, reject);
            });
            if (this._closed) {
                throw new errors_1.InvalidStateError('closed');
            }
        }
        catch (error) {
            if (track && this._stopTracks) {
                try {
                    track.stop();
                }
                catch (error2) { }
            }
            throw error;
        }
        // Destroy the previous track.
        this.destroyTrack();
        // Set the new track.
        this._track = track;
        // If this Producer was paused/resumed and the state of the new
        // track does not match, fix it.
        if (this._track && this._disableTrackOnPause) {
            if (!this._paused) {
                this._track.enabled = true;
            }
            else if (this._paused) {
                this._track.enabled = false;
            }
        }
        // Handle the effective track.
        this.handleTrack();
    }
    /**
     * Sets the video max spatial layer to be sent.
     */
    async setMaxSpatialLayer(spatialLayer) {
        if (this._closed) {
            throw new errors_1.InvalidStateError('closed');
        }
        else if (this._kind !== 'video') {
            throw new errors_1.UnsupportedError('not a video Producer');
        }
        else if (typeof spatialLayer !== 'number') {
            throw new TypeError('invalid spatialLayer');
        }
        if (spatialLayer === this._maxSpatialLayer) {
            return;
        }
        await new Promise((resolve, reject) => {
            this.safeEmit('@setmaxspatiallayer', spatialLayer, resolve, reject);
        });
        this._maxSpatialLayer = spatialLayer;
    }
    async setRtpEncodingParameters(params) {
        if (this._closed) {
            throw new errors_1.InvalidStateError('closed');
        }
        else if (typeof params !== 'object') {
            throw new TypeError('invalid params');
        }
        await new Promise((resolve, reject) => {
            this.safeEmit('@setrtpencodingparameters', params, resolve, reject);
        });
    }
    onTrackEnded() {
        logger.debug('track "ended" event');
        this.safeEmit('trackended');
        // Emit observer event.
        this._observer.safeEmit('trackended');
    }
    handleTrack() {
        if (!this._track) {
            return;
        }
        this._track.addEventListener('ended', this.onTrackEnded);
    }
    destroyTrack() {
        if (!this._track) {
            return;
        }
        try {
            this._track.removeEventListener('ended', this.onTrackEnded);
            // Just stop the track unless the app set stopTracks: false.
            if (this._stopTracks) {
                this._track.stop();
            }
        }
        catch (error) { }
    }
}
exports.Producer = Producer;

},
27: function(module, exports, __req) {
"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.Consumer = void 0;
const Logger_1 = __req(11);
const enhancedEvents_1 = __req(7);
const errors_1 = __req(12);
const logger = new Logger_1.Logger('Consumer');
class Consumer extends enhancedEvents_1.EnhancedEventEmitter {
    // Id.
    _id;
    // Local id.
    _localId;
    // Associated Producer id.
    _producerId;
    // Closed flag.
    _closed = false;
    // Associated RTCRtpReceiver.
    _rtpReceiver;
    // Remote track.
    _track;
    // RTP parameters.
    _rtpParameters;
    // Paused flag.
    _paused;
    // App custom data.
    _appData;
    // Observer instance.
    _observer = new enhancedEvents_1.EnhancedEventEmitter();
    constructor({ id, localId, producerId, rtpReceiver, track, rtpParameters, appData, }) {
        super();
        logger.debug('constructor()');
        this._id = id;
        this._localId = localId;
        this._producerId = producerId;
        this._rtpReceiver = rtpReceiver;
        this._track = track;
        this._rtpParameters = rtpParameters;
        this._paused = !track.enabled;
        this._appData = appData ?? {};
        this.onTrackEnded = this.onTrackEnded.bind(this);
        this.handleTrack();
    }
    /**
     * Consumer id.
     */
    get id() {
        return this._id;
    }
    /**
     * Local id.
     */
    get localId() {
        return this._localId;
    }
    /**
     * Associated Producer id.
     */
    get producerId() {
        return this._producerId;
    }
    /**
     * Whether the Consumer is closed.
     */
    get closed() {
        return this._closed;
    }
    /**
     * Media kind.
     */
    get kind() {
        return this._track.kind;
    }
    /**
     * Associated RTCRtpReceiver.
     */
    get rtpReceiver() {
        return this._rtpReceiver;
    }
    /**
     * The associated track.
     */
    get track() {
        return this._track;
    }
    /**
     * RTP parameters.
     */
    get rtpParameters() {
        return this._rtpParameters;
    }
    /**
     * Whether the Consumer is paused.
     */
    get paused() {
        return this._paused;
    }
    /**
     * App custom data.
     */
    get appData() {
        return this._appData;
    }
    /**
     * App custom data setter.
     */
    set appData(appData) {
        this._appData = appData;
    }
    get observer() {
        return this._observer;
    }
    /**
     * Closes the Consumer.
     */
    close() {
        if (this._closed) {
            return;
        }
        logger.debug('close()');
        this._closed = true;
        this.destroyTrack();
        this.emit('@close');
        // Emit observer event.
        this._observer.safeEmit('close');
        // Invoke close() in EnhancedEventEmitter classes.
        super.close();
        this._observer.close();
    }
    /**
     * Transport was closed.
     */
    transportClosed() {
        if (this._closed) {
            return;
        }
        logger.debug('transportClosed()');
        this._closed = true;
        this.destroyTrack();
        this.safeEmit('transportclose');
        // Emit observer event.
        this._observer.safeEmit('close');
        // Invoke close() in EnhancedEventEmitter classes.
        super.close();
        this._observer.close();
    }
    /**
     * Get associated RTCRtpReceiver stats.
     */
    async getStats() {
        if (this._closed) {
            throw new errors_1.InvalidStateError('closed');
        }
        return new Promise((resolve, reject) => {
            this.safeEmit('@getstats', resolve, reject);
        });
    }
    /**
     * Pauses receiving media.
     */
    pause() {
        logger.debug('pause()');
        if (this._closed) {
            logger.error('pause() | Consumer closed');
            return;
        }
        if (this._paused) {
            logger.debug('pause() | Consumer is already paused');
            return;
        }
        this._paused = true;
        this._track.enabled = false;
        this.emit('@pause');
        // Emit observer event.
        this._observer.safeEmit('pause');
    }
    /**
     * Resumes receiving media.
     */
    resume() {
        logger.debug('resume()');
        if (this._closed) {
            logger.error('resume() | Consumer closed');
            return;
        }
        if (!this._paused) {
            logger.debug('resume() | Consumer is already resumed');
            return;
        }
        this._paused = false;
        this._track.enabled = true;
        this.emit('@resume');
        // Emit observer event.
        this._observer.safeEmit('resume');
    }
    onTrackEnded() {
        logger.debug('track "ended" event');
        this.safeEmit('trackended');
        // Emit observer event.
        this._observer.safeEmit('trackended');
    }
    handleTrack() {
        this._track.addEventListener('ended', this.onTrackEnded);
    }
    destroyTrack() {
        try {
            this._track.removeEventListener('ended', this.onTrackEnded);
            this._track.stop();
        }
        catch (error) { }
    }
}
exports.Consumer = Consumer;

},
28: function(module, exports, __req) {
"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.DataProducer = void 0;
const Logger_1 = __req(11);
const enhancedEvents_1 = __req(7);
const errors_1 = __req(12);
const logger = new Logger_1.Logger('DataProducer');
class DataProducer extends enhancedEvents_1.EnhancedEventEmitter {
    // Id.
    _id;
    // The underlying RTCDataChannel instance.
    _dataChannel;
    // Closed flag.
    _closed = false;
    // SCTP stream parameters.
    _sctpStreamParameters;
    // App custom data.
    _appData;
    // Observer instance.
    _observer = new enhancedEvents_1.EnhancedEventEmitter();
    constructor({ id, dataChannel, sctpStreamParameters, appData, }) {
        super();
        logger.debug('constructor()');
        this._id = id;
        this._dataChannel = dataChannel;
        this._sctpStreamParameters = sctpStreamParameters;
        this._appData = appData ?? {};
        this.handleDataChannel();
    }
    /**
     * DataProducer id.
     */
    get id() {
        return this._id;
    }
    /**
     * Whether the DataProducer is closed.
     */
    get closed() {
        return this._closed;
    }
    /**
     * SCTP stream parameters.
     */
    get sctpStreamParameters() {
        return this._sctpStreamParameters;
    }
    /**
     * DataChannel readyState.
     */
    get readyState() {
        return this._dataChannel.readyState;
    }
    /**
     * DataChannel label.
     */
    get label() {
        return this._dataChannel.label;
    }
    /**
     * DataChannel protocol.
     */
    get protocol() {
        return this._dataChannel.protocol;
    }
    /**
     * DataChannel bufferedAmount.
     */
    get bufferedAmount() {
        return this._dataChannel.bufferedAmount;
    }
    /**
     * DataChannel bufferedAmountLowThreshold.
     */
    get bufferedAmountLowThreshold() {
        return this._dataChannel.bufferedAmountLowThreshold;
    }
    /**
     * Set DataChannel bufferedAmountLowThreshold.
     */
    set bufferedAmountLowThreshold(bufferedAmountLowThreshold) {
        this._dataChannel.bufferedAmountLowThreshold = bufferedAmountLowThreshold;
    }
    /**
     * App custom data.
     */
    get appData() {
        return this._appData;
    }
    /**
     * App custom data setter.
     */
    set appData(appData) {
        this._appData = appData;
    }
    get observer() {
        return this._observer;
    }
    /**
     * Closes the DataProducer.
     */
    close() {
        if (this._closed) {
            return;
        }
        logger.debug('close()');
        this._closed = true;
        this._dataChannel.close();
        this.emit('@close');
        // Emit observer event.
        this._observer.safeEmit('close');
        // Invoke close() in EnhancedEventEmitter classes.
        super.close();
        this._observer.close();
    }
    /**
     * Transport was closed.
     */
    transportClosed() {
        if (this._closed) {
            return;
        }
        logger.debug('transportClosed()');
        this._closed = true;
        this._dataChannel.close();
        this.safeEmit('transportclose');
        // Emit observer event.
        this._observer.safeEmit('close');
        // Invoke close() in EnhancedEventEmitter classes.
        super.close();
        this._observer.close();
    }
    /**
     * Send a message.
     *
     * @param {String|Blob|ArrayBuffer|ArrayBufferView} data.
     */
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    send(data) {
        logger.debug('send()');
        if (this._closed) {
            throw new errors_1.InvalidStateError('closed');
        }
        this._dataChannel.send(data);
    }
    handleDataChannel() {
        this._dataChannel.addEventListener('open', () => {
            if (this._closed) {
                return;
            }
            logger.debug('DataChannel "open" event');
            this.safeEmit('open');
        });
        this._dataChannel.addEventListener('error', event => {
            if (this._closed) {
                return;
            }
            const error = event.error ?? new Error('unknown DataChannel error');
            if (event.error?.errorDetail === 'sctp-failure') {
                logger.error('DataChannel SCTP error [sctpCauseCode:%s]: %s', event.error?.sctpCauseCode, event.error.message);
            }
            else {
                logger.error('DataChannel "error" event: %o', error);
            }
            this.safeEmit('error', error);
        });
        this._dataChannel.addEventListener('close', () => {
            if (this._closed) {
                return;
            }
            logger.debug('DataChannel "close" event');
            this._closed = true;
            this._dataChannel.close();
            this.emit('@close');
            this.safeEmit('close');
            // Emit observer event.
            this._observer.safeEmit('close');
            // Invoke close() in EnhancedEventEmitter classes.
            super.close();
            this._observer.close();
        });
        this._dataChannel.addEventListener('message', () => {
            if (this._closed) {
                return;
            }
            logger.warn('DataChannel "message" event in a DataProducer, message discarded');
        });
        this._dataChannel.addEventListener('bufferedamountlow', () => {
            if (this._closed) {
                return;
            }
            this.safeEmit('bufferedamountlow');
        });
    }
}
exports.DataProducer = DataProducer;

},
29: function(module, exports, __req) {
"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.DataConsumer = void 0;
const Logger_1 = __req(11);
const enhancedEvents_1 = __req(7);
const logger = new Logger_1.Logger('DataConsumer');
class DataConsumer extends enhancedEvents_1.EnhancedEventEmitter {
    // Id.
    _id;
    // Associated DataProducer Id.
    _dataProducerId;
    // The underlying RTCDataChannel instance.
    _dataChannel;
    // Closed flag.
    _closed = false;
    // SCTP stream parameters.
    _sctpStreamParameters;
    // App custom data.
    _appData;
    // Observer instance.
    _observer = new enhancedEvents_1.EnhancedEventEmitter();
    constructor({ id, dataProducerId, dataChannel, sctpStreamParameters, appData, }) {
        super();
        logger.debug('constructor()');
        this._id = id;
        this._dataProducerId = dataProducerId;
        this._dataChannel = dataChannel;
        this._sctpStreamParameters = sctpStreamParameters;
        this._appData = appData ?? {};
        this.handleDataChannel();
    }
    /**
     * DataConsumer id.
     */
    get id() {
        return this._id;
    }
    /**
     * Associated DataProducer id.
     */
    get dataProducerId() {
        return this._dataProducerId;
    }
    /**
     * Whether the DataConsumer is closed.
     */
    get closed() {
        return this._closed;
    }
    /**
     * SCTP stream parameters.
     */
    get sctpStreamParameters() {
        return this._sctpStreamParameters;
    }
    /**
     * DataChannel readyState.
     */
    get readyState() {
        return this._dataChannel.readyState;
    }
    /**
     * DataChannel label.
     */
    get label() {
        return this._dataChannel.label;
    }
    /**
     * DataChannel protocol.
     */
    get protocol() {
        return this._dataChannel.protocol;
    }
    /**
     * DataChannel binaryType.
     */
    get binaryType() {
        return this._dataChannel.binaryType;
    }
    /**
     * Set DataChannel binaryType.
     */
    set binaryType(binaryType) {
        this._dataChannel.binaryType = binaryType;
    }
    /**
     * App custom data.
     */
    get appData() {
        return this._appData;
    }
    /**
     * App custom data setter.
     */
    set appData(appData) {
        this._appData = appData;
    }
    get observer() {
        return this._observer;
    }
    /**
     * Closes the DataConsumer.
     */
    close() {
        if (this._closed) {
            return;
        }
        logger.debug('close()');
        this._closed = true;
        this._dataChannel.close();
        this.emit('@close');
        // Emit observer event.
        this._observer.safeEmit('close');
        // Invoke close() in EnhancedEventEmitter classes.
        super.close();
        this._observer.close();
    }
    /**
     * Transport was closed.
     */
    transportClosed() {
        if (this._closed) {
            return;
        }
        logger.debug('transportClosed()');
        this._closed = true;
        this._dataChannel.close();
        this.safeEmit('transportclose');
        // Emit observer event.
        this._observer.safeEmit('close');
        // Invoke close() in EnhancedEventEmitter classes.
        super.close();
        this._observer.close();
    }
    handleDataChannel() {
        this._dataChannel.addEventListener('open', () => {
            if (this._closed) {
                return;
            }
            logger.debug('DataChannel "open" event');
            this.safeEmit('open');
        });
        this._dataChannel.addEventListener('error', event => {
            if (this._closed) {
                return;
            }
            const error = event.error ?? new Error('unknown DataChannel error');
            if (event.error?.errorDetail === 'sctp-failure') {
                logger.error('DataChannel SCTP error [sctpCauseCode:%s]: %s', event.error?.sctpCauseCode, event.error.message);
            }
            else {
                logger.error('DataChannel "error" event: %o', error);
            }
            this.safeEmit('error', error);
        });
        this._dataChannel.addEventListener('close', () => {
            if (this._closed) {
                return;
            }
            logger.debug('DataChannel "close" event');
            this._closed = true;
            this._dataChannel.close();
            this.emit('@close');
            this.safeEmit('close');
            // Emit observer event.
            this._observer.safeEmit('close');
            // Invoke close() in EnhancedEventEmitter classes.
            super.close();
            this._observer.close();
        });
        this._dataChannel.addEventListener('message', event => {
            if (this._closed) {
                return;
            }
            this.safeEmit('message', event.data);
        });
    }
}
exports.DataConsumer = DataConsumer;

},
30: function(module, exports, __req) {
var parser = __req(42);
var writer = __req(43);
var grammar = __req(44);

exports.grammar = grammar;
exports.write = writer;
exports.parse = parser.parse;
exports.parseParams = parser.parseParams;
exports.parseFmtpConfig = parser.parseFmtpConfig; // Alias of parseParams().
exports.parsePayloads = parser.parsePayloads;
exports.parseRemoteCandidates = parser.parseRemoteCandidates;
exports.parseImageAttributes = parser.parseImageAttributes;
exports.parseSimulcastStreamList = parser.parseSimulcastStreamList;

},
31: function(module, exports, __req) {
"use strict";
var __createBinding = (this && this.__createBinding) || (Object.create ? (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    var desc = Object.getOwnPropertyDescriptor(m, k);
    if (!desc || ("get" in desc ? !m.__esModule : desc.writable || desc.configurable)) {
      desc = { enumerable: true, get: function() { return m[k]; } };
    }
    Object.defineProperty(o, k2, desc);
}) : (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    o[k2] = m[k];
}));
var __setModuleDefault = (this && this.__setModuleDefault) || (Object.create ? (function(o, v) {
    Object.defineProperty(o, "default", { enumerable: true, value: v });
}) : function(o, v) {
    o["default"] = v;
});
var __importStar = (this && this.__importStar) || (function () {
    var ownKeys = function(o) {
        ownKeys = Object.getOwnPropertyNames || function (o) {
            var ar = [];
            for (var k in o) if (Object.prototype.hasOwnProperty.call(o, k)) ar[ar.length] = k;
            return ar;
        };
        return ownKeys(o);
    };
    return function (mod) {
        if (mod && mod.__esModule) return mod;
        var result = {};
        if (mod != null) for (var k = ownKeys(mod), i = 0; i < k.length; i++) if (k[i] !== "default") __createBinding(result, mod, k[i]);
        __setModuleDefault(result, mod);
        return result;
    };
})();
Object.defineProperty(exports, "__esModule", { value: true });
exports.RemoteSdp = void 0;
const sdpTransform = __importStar(__req(30));
const Logger_1 = __req(11);
const RemoteMediaSection_1 = __req(45);
const __1 = __req(1);
const DependencyDescriptorCodecs = ['av1', 'h264'];
const logger = new Logger_1.Logger('RemoteSdp');
class RemoteSdp {
    // Remote ICE parameters.
    _iceParameters;
    // Remote ICE candidates.
    _iceCandidates;
    // Remote DTLS parameters.
    _dtlsParameters;
    // Remote SCTP parameters.
    _sctpParameters;
    // Parameters for plain RTP (no SRTP nor DTLS no BUNDLE).
    _plainRtpParameters;
    // RemoteMediaSection instances with same order as in the SDP.
    _mediaSections = [];
    // RemoteMediaSection indices indexed by MID.
    _midToIndex = new Map();
    // First MID.
    _firstMid;
    // SDP object.
    _sdpObject;
    constructor({ iceParameters, iceCandidates, dtlsParameters, sctpParameters, plainRtpParameters, }) {
        this._iceParameters = iceParameters;
        this._iceCandidates = iceCandidates;
        this._dtlsParameters = dtlsParameters;
        this._sctpParameters = sctpParameters;
        this._plainRtpParameters = plainRtpParameters;
        this._sdpObject = {
            version: 0,
            origin: {
                address: '0.0.0.0',
                ipVer: 4,
                netType: 'IN',
                sessionId: '10000',
                sessionVersion: 0,
                username: `mediasoup-client-v${__1.version}`,
            },
            name: '-',
            timing: { start: 0, stop: 0 },
            media: [],
        };
        // Indicate support of RFC 8445 (ICE bis / ice2).
        this._sdpObject.iceOptions = 'ice2';
        // If ICE parameters are given, add ICE-Lite indicator.
        if (iceParameters?.iceLite) {
            this._sdpObject.icelite = 'ice-lite';
        }
        // If DTLS parameters are given, assume WebRTC and BUNDLE.
        if (dtlsParameters) {
            // NOTE: This is not standard anymore (it was removed in RFC 8830),
            // however some WebRTC clients still rely on it.
            this._sdpObject.msidSemantic = { semantic: 'WMS', token: '*' };
            // NOTE: We take the latest fingerprint.
            const numFingerprints = this._dtlsParameters.fingerprints.length;
            this._sdpObject.fingerprint = {
                type: dtlsParameters.fingerprints[numFingerprints - 1].algorithm,
                hash: dtlsParameters.fingerprints[numFingerprints - 1].value,
            };
            this._sdpObject.groups = [{ type: 'BUNDLE', mids: '' }];
        }
        // If there are plain RPT parameters, override SDP origin.
        if (plainRtpParameters) {
            this._sdpObject.origin.address = plainRtpParameters.ip;
            this._sdpObject.origin.ipVer = plainRtpParameters.ipVersion;
        }
    }
    updateIceParameters(iceParameters) {
        logger.debug('updateIceParameters() [iceParameters:%o]', iceParameters);
        this._iceParameters = iceParameters;
        this._sdpObject.icelite = iceParameters.iceLite ? 'ice-lite' : undefined;
        for (const mediaSection of this._mediaSections) {
            mediaSection.setIceParameters(iceParameters);
        }
    }
    updateDtlsRole(role) {
        logger.debug('updateDtlsRole() [role:%s]', role);
        this._dtlsParameters.role = role;
        for (const mediaSection of this._mediaSections) {
            mediaSection.setDtlsRole(role);
        }
    }
    /**
     * Set session level a=extmap-allow-mixed attibute.
     */
    setSessionExtmapAllowMixed() {
        logger.debug('setSessionExtmapAllowMixed()');
        this._sdpObject.extmapAllowMixed = 'extmap-allow-mixed';
    }
    getNextMediaSectionIdx() {
        // If a closed media section is found, return its index.
        for (let idx = 0; idx < this._mediaSections.length; ++idx) {
            const mediaSection = this._mediaSections[idx];
            if (mediaSection.closed) {
                return { idx, reuseMid: mediaSection.mid };
            }
        }
        // If no closed media section is found, return next one.
        return { idx: this._mediaSections.length };
    }
    send({ offerMediaObject, reuseMid, offerRtpParameters, answerRtpParameters, codecOptions, }) {
        const mediaSection = new RemoteMediaSection_1.RemoteAnswerMediaSection({
            iceParameters: this._iceParameters,
            iceCandidates: this._iceCandidates,
            dtlsParameters: this._dtlsParameters,
            plainRtpParameters: this._plainRtpParameters,
            offerMediaObject,
            offerRtpParameters,
            answerRtpParameters,
            codecOptions,
        });
        const mediaObject = mediaSection.getObject();
        // Remove Dependency Descriptor extension unless there is support for
        // the codec in mediasoup.
        const ddCodec = mediaObject.rtp.find(rtp => DependencyDescriptorCodecs.includes(rtp.codec.toLowerCase()));
        if (!ddCodec) {
            mediaObject.ext = mediaObject.ext?.filter(extmap => extmap.uri !==
                'https://aomediacodec.github.io/av1-rtp-spec/#dependency-descriptor-rtp-header-extension');
        }
        // Unified-Plan with closed media section replacement.
        if (reuseMid) {
            this.replaceMediaSection(mediaSection, reuseMid);
        }
        // Unified-Plan or Plan-B with different media kind.
        else if (!this._midToIndex.has(mediaSection.mid)) {
            this.addMediaSection(mediaSection);
        }
        // Plan-B with same media kind.
        else {
            this.replaceMediaSection(mediaSection);
        }
    }
    receive({ mid, kind, offerRtpParameters, streamId, trackId, }) {
        // Allow both 1 byte and 2 bytes length header extensions since
        // mediasoup can send both at any time.
        this.setSessionExtmapAllowMixed();
        const mediaSection = new RemoteMediaSection_1.RemoteOfferMediaSection({
            iceParameters: this._iceParameters,
            iceCandidates: this._iceCandidates,
            dtlsParameters: this._dtlsParameters,
            plainRtpParameters: this._plainRtpParameters,
            mid,
            kind,
            offerRtpParameters,
            streamId,
            trackId,
        });
        // Let's try to recycle a closed media section (if any).
        // NOTE: We cannot recycle a closed m=audio section as m=video (or vice
        // versa). Firefox rejects the SDP when the media type of a recycled m-line
        // changes.
        const oldMediaSection = this._mediaSections.find(m => m.closed && m.getObject().type === kind);
        if (oldMediaSection) {
            this.replaceMediaSection(mediaSection, oldMediaSection.mid);
        }
        else {
            this.addMediaSection(mediaSection);
        }
    }
    pauseMediaSection(mid) {
        const mediaSection = this.findMediaSection(mid);
        mediaSection.pause();
    }
    resumeSendingMediaSection(mid) {
        const mediaSection = this.findMediaSection(mid);
        mediaSection.resume();
    }
    resumeReceivingMediaSection(mid) {
        const mediaSection = this.findMediaSection(mid);
        mediaSection.resume();
    }
    disableMediaSection(mid) {
        const mediaSection = this.findMediaSection(mid);
        mediaSection.disable();
    }
    /**
     * Closes media section. Returns true if the given MID corresponds to a m
     * section that has been indeed closed. False otherwise.
     *
     * NOTE: Closing the first m section is a pain since it invalidates the bundled
     * transport, so instead closing it we just disable it.
     */
    closeMediaSection(mid) {
        const mediaSection = this.findMediaSection(mid);
        // NOTE: Closing the first m section is a pain since it invalidates the
        // bundled transport, so let's avoid it.
        if (mid === this._firstMid) {
            logger.debug('closeMediaSection() | cannot close first media section, disabling it instead [mid:%s]', mid);
            this.disableMediaSection(mid);
            return false;
        }
        mediaSection.close();
        // Regenerate BUNDLE mids.
        this.regenerateBundleMids();
        return true;
    }
    muxMediaSectionSimulcast(mid, encodings) {
        const mediaSection = this.findMediaSection(mid);
        mediaSection.muxSimulcastStreams(encodings);
        this.replaceMediaSection(mediaSection);
    }
    sendSctpAssociation({ offerMediaObject, }) {
        const mediaSection = new RemoteMediaSection_1.RemoteAnswerMediaSection({
            iceParameters: this._iceParameters,
            iceCandidates: this._iceCandidates,
            dtlsParameters: this._dtlsParameters,
            sctpParameters: this._sctpParameters,
            plainRtpParameters: this._plainRtpParameters,
            offerMediaObject,
        });
        this.addMediaSection(mediaSection);
    }
    receiveSctpAssociation() {
        const mediaSection = new RemoteMediaSection_1.RemoteOfferMediaSection({
            iceParameters: this._iceParameters,
            iceCandidates: this._iceCandidates,
            dtlsParameters: this._dtlsParameters,
            sctpParameters: this._sctpParameters,
            plainRtpParameters: this._plainRtpParameters,
            mid: 'datachannel',
            kind: 'application',
        });
        this.addMediaSection(mediaSection);
    }
    getSdp() {
        // Increase SDP version.
        this._sdpObject.origin.sessionVersion++;
        return sdpTransform.write(this._sdpObject);
    }
    addMediaSection(newMediaSection) {
        if (!this._firstMid) {
            this._firstMid = newMediaSection.mid;
        }
        // Add to the vector.
        this._mediaSections.push(newMediaSection);
        // Add to the map.
        this._midToIndex.set(newMediaSection.mid, this._mediaSections.length - 1);
        // Add to the SDP object.
        this._sdpObject.media.push(newMediaSection.getObject());
        // Regenerate BUNDLE mids.
        this.regenerateBundleMids();
    }
    replaceMediaSection(newMediaSection, reuseMid) {
        // Store it in the map.
        if (typeof reuseMid === 'string') {
            const idx = this._midToIndex.get(reuseMid);
            if (idx === undefined) {
                throw new Error(`no media section found for reuseMid '${reuseMid}'`);
            }
            const oldMediaSection = this._mediaSections[idx];
            // Replace the index in the vector with the new media section.
            this._mediaSections[idx] = newMediaSection;
            // Update the map.
            this._midToIndex.delete(oldMediaSection.mid);
            this._midToIndex.set(newMediaSection.mid, idx);
            // Update the SDP object.
            this._sdpObject.media[idx] = newMediaSection.getObject();
            // Regenerate BUNDLE mids.
            this.regenerateBundleMids();
        }
        else {
            const idx = this._midToIndex.get(newMediaSection.mid);
            if (idx === undefined) {
                throw new Error(`no media section found with mid '${newMediaSection.mid}'`);
            }
            // Replace the index in the vector with the new media section.
            this._mediaSections[idx] = newMediaSection;
            // Update the SDP object.
            this._sdpObject.media[idx] = newMediaSection.getObject();
        }
    }
    findMediaSection(mid) {
        const idx = this._midToIndex.get(mid);
        if (idx === undefined) {
            throw new Error(`no media section found with mid '${mid}'`);
        }
        return this._mediaSections[idx];
    }
    regenerateBundleMids() {
        if (!this._dtlsParameters) {
            return;
        }
        this._sdpObject.groups[0].mids = this._mediaSections
            .filter((mediaSection) => !mediaSection.closed)
            .map((mediaSection) => mediaSection.mid)
            .join(' ');
    }
}
exports.RemoteSdp = RemoteSdp;

},
32: function(module, exports, __req) {
"use strict";
var __createBinding = (this && this.__createBinding) || (Object.create ? (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    var desc = Object.getOwnPropertyDescriptor(m, k);
    if (!desc || ("get" in desc ? !m.__esModule : desc.writable || desc.configurable)) {
      desc = { enumerable: true, get: function() { return m[k]; } };
    }
    Object.defineProperty(o, k2, desc);
}) : (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    o[k2] = m[k];
}));
var __setModuleDefault = (this && this.__setModuleDefault) || (Object.create ? (function(o, v) {
    Object.defineProperty(o, "default", { enumerable: true, value: v });
}) : function(o, v) {
    o["default"] = v;
});
var __importStar = (this && this.__importStar) || (function () {
    var ownKeys = function(o) {
        ownKeys = Object.getOwnPropertyNames || function (o) {
            var ar = [];
            for (var k in o) if (Object.prototype.hasOwnProperty.call(o, k)) ar[ar.length] = k;
            return ar;
        };
        return ownKeys(o);
    };
    return function (mod) {
        if (mod && mod.__esModule) return mod;
        var result = {};
        if (mod != null) for (var k = ownKeys(mod), i = 0; i < k.length; i++) if (k[i] !== "default") __createBinding(result, mod, k[i]);
        __setModuleDefault(result, mod);
        return result;
    };
})();
Object.defineProperty(exports, "__esModule", { value: true });
exports.extractRtpCapabilities = extractRtpCapabilities;
exports.extractDtlsParameters = extractDtlsParameters;
exports.getCname = getCname;
exports.applyCodecParameters = applyCodecParameters;
const sdpTransform = __importStar(__req(30));
/**
 * This function extracs RTP capabilities from the given SDP.
 *
 * BUNDLE is assumed so, as per spec, all media sections in the SDP must share
 * same ids for codecs and RTP extensions.
 */
function extractRtpCapabilities({ sdpObject, }) {
    // Map of RtpCodecParameters indexed by payload type.
    const codecsMap = new Map();
    // Map of RtpHeaderExtensions indexed by preferred id.
    const headerExtensionMap = new Map();
    for (const m of sdpObject.media) {
        const kind = m.type;
        switch (kind) {
            case 'audio':
            case 'video': {
                break;
            }
            default: {
                continue;
            }
        }
        // Get codecs.
        for (const rtp of m.rtp) {
            const codec = {
                kind: kind,
                mimeType: `${kind}/${rtp.codec}`,
                preferredPayloadType: rtp.payload,
                clockRate: rtp.rate,
                channels: rtp.encoding,
                parameters: {},
                rtcpFeedback: [],
            };
            codecsMap.set(codec.preferredPayloadType, codec);
        }
        // Get codec parameters.
        for (const fmtp of m.fmtp ?? []) {
            const parameters = sdpTransform.parseParams(fmtp.config);
            const codec = codecsMap.get(fmtp.payload);
            if (!codec) {
                continue;
            }
            // Specials cases to convert parameter value to string.
            if (parameters?.hasOwnProperty('profile-level-id')) {
                parameters['profile-level-id'] = String(parameters['profile-level-id']);
            }
            codec.parameters = parameters;
        }
        // Get RTCP feedback for each codec.
        for (const fb of m.rtcpFb ?? []) {
            const feedback = {
                type: fb.type,
                parameter: fb.subtype,
            };
            if (!feedback.parameter) {
                delete feedback.parameter;
            }
            // rtcp-fb payload is not '*', so just apply it to its corresponding
            // codec.
            if (fb.payload !== '*') {
                const codec = codecsMap.get(Number(fb.payload));
                if (!codec) {
                    continue;
                }
                codec.rtcpFeedback.push(feedback);
            }
            // If rtcp-fb payload is '*' it must be applied to all codecs with same
            // kind (with some exceptions such as RTX codec).
            else {
                for (const codec of codecsMap.values()) {
                    if (codec.kind === kind && !/.+\/rtx$/i.test(codec.mimeType)) {
                        codec.rtcpFeedback.push(feedback);
                    }
                }
            }
        }
        // Get RTP header extensions.
        for (const ext of m.ext ?? []) {
            // Ignore encrypted extensions (not yet supported in mediasoup).
            if (ext['encrypt-uri']) {
                continue;
            }
            const headerExtension = {
                kind: kind,
                uri: ext.uri,
                preferredId: ext.value,
            };
            headerExtensionMap.set(headerExtension.preferredId, headerExtension);
        }
    }
    const rtpCapabilities = {
        codecs: Array.from(codecsMap.values()),
        headerExtensions: Array.from(headerExtensionMap.values()),
    };
    return rtpCapabilities;
}
function extractDtlsParameters({ sdpObject, }) {
    let setup = sdpObject.setup;
    let fingerprint = sdpObject.fingerprint;
    if (!setup || !fingerprint) {
        const mediaObject = (sdpObject.media ?? []).find((m) => m.port !== 0);
        if (mediaObject) {
            setup = setup ?? mediaObject.setup;
            fingerprint = fingerprint ?? mediaObject.fingerprint;
        }
    }
    if (!setup) {
        throw new Error('no a=setup found at SDP session or media level');
    }
    else if (!fingerprint) {
        throw new Error('no a=fingerprint found at SDP session or media level');
    }
    let role;
    switch (setup) {
        case 'active': {
            role = 'client';
            break;
        }
        case 'passive': {
            role = 'server';
            break;
        }
        case 'actpass': {
            role = 'auto';
            break;
        }
    }
    const dtlsParameters = {
        role,
        fingerprints: [
            {
                algorithm: fingerprint.type,
                value: fingerprint.hash,
            },
        ],
    };
    return dtlsParameters;
}
function getCname({ offerMediaObject, }) {
    const ssrcCnameLine = (offerMediaObject.ssrcs ?? []).find((line) => line.attribute === 'cname');
    if (!ssrcCnameLine) {
        return '';
    }
    return ssrcCnameLine.value;
}
/**
 * Apply codec parameters in the given SDP m= section answer based on the
 * given RTP parameters of an offer.
 */
function applyCodecParameters({ offerRtpParameters, answerMediaObject, }) {
    for (const codec of offerRtpParameters.codecs) {
        const mimeType = codec.mimeType.toLowerCase();
        // Avoid parsing codec parameters for unhandled codecs.
        if (mimeType !== 'audio/opus') {
            continue;
        }
        const rtp = (answerMediaObject.rtp ?? []).find((r) => r.payload === codec.payloadType);
        if (!rtp) {
            continue;
        }
        // Just in case.
        answerMediaObject.fmtp = answerMediaObject.fmtp ?? [];
        let fmtp = answerMediaObject.fmtp.find((f) => f.payload === codec.payloadType);
        if (!fmtp) {
            fmtp = { payload: codec.payloadType, config: '' };
            answerMediaObject.fmtp.push(fmtp);
        }
        const parameters = sdpTransform.parseParams(fmtp.config);
        switch (mimeType) {
            case 'audio/opus': {
                const spropStereo = codec.parameters?.['sprop-stereo'];
                if (spropStereo !== undefined) {
                    parameters['stereo'] = Number(spropStereo) ? 1 : 0;
                }
                break;
            }
        }
        // Write the codec fmtp.config back.
        fmtp.config = '';
        for (const key of Object.keys(parameters)) {
            if (fmtp.config) {
                fmtp.config += ';';
            }
            fmtp.config += `${key}=${parameters[key]}`;
        }
    }
}

},
33: function(module, exports, __req) {
"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.getRtpEncodings = getRtpEncodings;
exports.addLegacySimulcast = addLegacySimulcast;
function getRtpEncodings({ offerMediaObject, codecs, }) {
    const ssrcs = new Set();
    for (const line of offerMediaObject.ssrcs ?? []) {
        const ssrc = line.id;
        if (ssrc) {
            ssrcs.add(ssrc);
        }
    }
    if (ssrcs.size === 0) {
        throw new Error('no a=ssrc lines found');
    }
    const ssrcToRtxSsrc = new Map();
    // First assume RTX is used.
    for (const line of offerMediaObject.ssrcGroups ?? []) {
        if (line.semantics !== 'FID') {
            continue;
        }
        const ssrcsStr = line.ssrcs.split(/\s+/);
        const ssrc = Number(ssrcsStr[0]);
        const rtxSsrc = Number(ssrcsStr[1]);
        if (ssrcs.has(ssrc)) {
            // Remove both the SSRC and RTX SSRC from the set so later we know
            // that they are already handled.
            ssrcs.delete(ssrc);
            ssrcs.delete(rtxSsrc);
            // Add to the map.
            ssrcToRtxSsrc.set(ssrc, rtxSsrc);
        }
    }
    // If the set of SSRCs is not empty it means that RTX is not being used, so
    // take media SSRCs from there.
    for (const ssrc of ssrcs) {
        // Add to the map.
        ssrcToRtxSsrc.set(ssrc, undefined);
    }
    const encodings = [];
    for (const [ssrc, rtxSsrc] of ssrcToRtxSsrc) {
        const encoding = { ssrc };
        // Only add RTX SSRC if there are more than 1 codec.
        if (rtxSsrc && codecs.length > 1) {
            encoding.rtx = { ssrc: rtxSsrc };
        }
        encodings.push(encoding);
    }
    return encodings;
}
/**
 * Adds multi-ssrc based simulcast into the given SDP media section offer.
 */
function addLegacySimulcast({ offerMediaObject, numStreams, }) {
    if (numStreams <= 1) {
        throw new TypeError('numStreams must be greater than 1');
    }
    // Get the SSRC.
    const ssrcMsidLine = (offerMediaObject.ssrcs ?? []).find(line => line.attribute === 'msid');
    if (!ssrcMsidLine) {
        throw new Error('a=ssrc line with msid information not found');
    }
    const [streamId, trackId] = ssrcMsidLine.value.split(' ');
    const firstSsrc = Number(ssrcMsidLine.id);
    let firstRtxSsrc;
    // Get the SSRC for RTX.
    (offerMediaObject.ssrcGroups ?? []).some(line => {
        if (line.semantics !== 'FID') {
            return false;
        }
        const ssrcs = line.ssrcs.split(/\s+/);
        if (Number(ssrcs[0]) === firstSsrc) {
            firstRtxSsrc = Number(ssrcs[1]);
            return true;
        }
        else {
            return false;
        }
    });
    const ssrcCnameLine = (offerMediaObject.ssrcs ?? []).find(line => line.attribute === 'cname');
    if (!ssrcCnameLine) {
        throw new Error('a=ssrc line with cname information not found');
    }
    const cname = ssrcCnameLine.value;
    const ssrcs = [];
    const rtxSsrcs = [];
    for (let i = 0; i < numStreams; ++i) {
        ssrcs.push(firstSsrc + i);
        if (firstRtxSsrc) {
            rtxSsrcs.push(firstRtxSsrc + i);
        }
    }
    offerMediaObject.ssrcGroups = [];
    offerMediaObject.ssrcs = [];
    offerMediaObject.ssrcGroups.push({
        semantics: 'SIM',
        ssrcs: ssrcs.join(' '),
    });
    for (const ssrc of ssrcs) {
        offerMediaObject.ssrcs.push({
            id: ssrc,
            attribute: 'cname',
            value: cname,
        });
        offerMediaObject.ssrcs.push({
            id: ssrc,
            attribute: 'msid',
            value: `${streamId} ${trackId}`,
        });
    }
    for (let i = 0; i < rtxSsrcs.length; ++i) {
        const ssrc = ssrcs[i];
        const rtxSsrc = rtxSsrcs[i];
        offerMediaObject.ssrcs.push({
            id: rtxSsrc,
            attribute: 'cname',
            value: cname,
        });
        offerMediaObject.ssrcs.push({
            id: rtxSsrc,
            attribute: 'msid',
            value: `${streamId} ${trackId}`,
        });
        offerMediaObject.ssrcGroups.push({
            semantics: 'FID',
            ssrcs: `${ssrc} ${rtxSsrc}`,
        });
    }
}

},
34: function(module, exports, __req) {
"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.addNackSupportForOpus = addNackSupportForOpus;
exports.getMsidStreamIdAndTrackId = getMsidStreamIdAndTrackId;
exports.applyForcedRtpExtensions = applyForcedRtpExtensions;
/**
 * This function adds RTCP NACK support for OPUS codec in given capabilities.
 */
function addNackSupportForOpus(rtpCapabilities) {
    for (const codec of rtpCapabilities.codecs ?? []) {
        if ((codec.mimeType.toLowerCase() === 'audio/opus' ||
            codec.mimeType.toLowerCase() === 'audio/multiopus') &&
            !codec.rtcpFeedback?.some(fb => fb.type === 'nack' && !fb.parameter)) {
            if (!codec.rtcpFeedback) {
                codec.rtcpFeedback = [];
            }
            codec.rtcpFeedback.push({ type: 'nack' });
        }
    }
}
function getMsidStreamIdAndTrackId(msid) {
    if (!msid || typeof msid !== 'string') {
        return { msidStreamId: undefined, msidTrackId: undefined };
    }
    /**
     * `msidStreamId` must be an id or '-' (no stream).
     * `msidTrackId` is an optional id.
     */
    const [msidStreamId, msidTrackId] = msid.trim().split(/\s+/);
    if (!msidStreamId) {
        return { msidStreamId: undefined, msidTrackId: undefined };
    }
    return { msidStreamId, msidTrackId };
}
/**
 * Apply given desired RTP extension to the given RTCRtpTransceiver.
 *
 * @see https://w3c.github.io/webrtc-extensions/#rtp-header-extension-control
 */
function applyForcedRtpExtensions(transceiver, forcedRtpExtensions) {
    // If the RTP header extension control API is not available then abort.
    if (!transceiver.getHeaderExtensionsToNegotiate ||
        !transceiver.setHeaderExtensionsToNegotiate) {
        return false;
    }
    if (Object.keys(forcedRtpExtensions).length === 0) {
        return false;
    }
    let extensionsToNegotiate = transceiver.getHeaderExtensionsToNegotiate();
    extensionsToNegotiate = extensionsToNegotiate.map((extenCap) => {
        const uri = extenCap.uri;
        const enabled = forcedRtpExtensions[uri];
        if (enabled === true) {
            extenCap.direction = 'sendrecv';
        }
        else if (enabled === false) {
            extenCap.direction = 'stopped';
        }
        return extenCap;
    });
    transceiver.setHeaderExtensionsToNegotiate(extensionsToNegotiate);
    return true;
}

},
35: function(module, exports, __req) {
"use strict";
var __importDefault = (this && this.__importDefault) || function (mod) {
    return (mod && mod.__esModule) ? mod : { "default": mod };
};
Object.defineProperty(exports, "__esModule", { value: true });
exports.Logger = void 0;
const debug_1 = __importDefault(__req(2));
const APP_NAME = 'h264-profile-level-id';
class Logger {
    _debug;
    _warn;
    _error;
    constructor(prefix) {
        if (prefix) {
            this._debug = (0, debug_1.default)(`${APP_NAME}:${prefix}`);
            this._warn = (0, debug_1.default)(`${APP_NAME}:WARN:${prefix}`);
            this._error = (0, debug_1.default)(`${APP_NAME}:ERROR:${prefix}`);
        }
        else {
            this._debug = (0, debug_1.default)(APP_NAME);
            this._warn = (0, debug_1.default)(`${APP_NAME}:WARN`);
            this._error = (0, debug_1.default)(`${APP_NAME}:ERROR`);
        }
        /* eslint-disable no-console */
        this._debug.log = console.info.bind(console);
        this._warn.log = console.warn.bind(console);
        this._error.log = console.error.bind(console);
        /* eslint-enable no-console */
    }
    get debug() {
        return this._debug;
    }
    get warn() {
        return this._warn;
    }
    get error() {
        return this._error;
    }
}
exports.Logger = Logger;

},
36: function(module, exports, __req) {
var IDX=256, HEX=[], BUFFER;
while (IDX--) HEX[IDX] = (IDX + 256).toString(16).substring(1);

function v4() {
	var i=0, num, out='';

	if (!BUFFER || ((IDX + 16) > 256)) {
		BUFFER = Array(i=256);
		while (i--) BUFFER[i] = 256 * Math.random() | 0;
		i = IDX = 0;
	}

	for (; i < 16; i++) {
		num = BUFFER[IDX + i];
		if (i==6) out += HEX[num & 15 | 64];
		else if (i==8) out += HEX[num & 63 | 128];
		else out += HEX[num];

		if (i & 1 && i > 1 && i < 11) out += '-';
	}

	IDX++;
	return out;
}

exports.v4 = v4;
},
37: function(module, exports, __req) {
"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.FakeEventTarget = void 0;
class FakeEventTarget {
    listeners = {};
    addEventListener(type, callback, options) {
        if (!callback) {
            return;
        }
        this.listeners[type] = this.listeners[type] ?? [];
        this.listeners[type].push({
            callback: 
            // eslint-disable-next-line @typescript-eslint/unbound-method
            typeof callback === 'function' ? callback : callback.handleEvent,
            once: typeof options === 'object' && options.once === true,
        });
    }
    removeEventListener(type, callback, 
    // eslint-disable-next-line @typescript-eslint/no-unused-vars
    options) {
        if (!this.listeners[type]) {
            return;
        }
        if (!callback) {
            return;
        }
        this.listeners[type] = this.listeners[type].filter(listener => listener.callback !==
            // eslint-disable-next-line @typescript-eslint/unbound-method
            (typeof callback === 'function' ? callback : callback.handleEvent));
    }
    dispatchEvent(event) {
        if (!event || typeof event.type !== 'string') {
            throw new Error('invalid event object');
        }
        const entries = this.listeners[event.type];
        if (!entries) {
            return true;
        }
        for (const listener of [...entries]) {
            try {
                listener.callback.call(this, event);
            }
            catch (error) {
                // Avoid that the error breaks the iteration.
                setTimeout(() => {
                    throw error;
                }, 0);
            }
            if (listener.once) {
                this.removeEventListener(event.type, listener.callback);
            }
        }
        return !event.defaultPrevented;
    }
}
exports.FakeEventTarget = FakeEventTarget;

},
38: function(module, exports, __req) {
"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.FakeEvent = void 0;
// NOTE: Do not use our FakeEventTarget type inside this class, otherwise TS
// will complain because "Property 'listeners' is missing in type 'EventTarget'
// but required in type 'FakeEventTarget'".
class FakeEvent {
    /**
     * Constants.
     */
    NONE = 0;
    CAPTURING_PHASE = 1;
    AT_TARGET = 2;
    BUBBLING_PHASE = 3;
    /**
     * Members.
     */
    type;
    bubbles;
    cancelable;
    defaultPrevented = false;
    composed = false;
    currentTarget = null;
    // Not implemented.
    eventPhase = this.NONE;
    isTrusted = true;
    target = null;
    timeStamp = 0;
    // Deprecated.
    cancelBubble = false;
    returnValue = true;
    srcElement = null;
    constructor(type, options = {}) {
        this.type = type;
        this.bubbles = options.bubbles ?? false;
        this.cancelable = options.cancelable ?? false;
    }
    preventDefault() {
        if (this.cancelable) {
            this.defaultPrevented = true;
        }
    }
    /**
     * Not implemented.
     */
    stopPropagation() { }
    /**
     * Not implemented.
     */
    stopImmediatePropagation() { }
    /**
     * Not implemented.
     */
    composedPath() {
        return [];
    }
    /**
     * Not implemented.
     * @deprecated
     */
    // eslint-disable-next-line @typescript-eslint/no-unused-vars
    initEvent(type, bubbles, cancelable) {
        // Not implemented.
    }
}
exports.FakeEvent = FakeEvent;

},
39: function(module, exports, __req) {
"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.clone = clone;
/**
 * Clones the given value.
 */
function clone(value) {
    if (value === undefined) {
        return undefined;
    }
    else if (Number.isNaN(value)) {
        return NaN;
    }
    else if (typeof structuredClone === 'function') {
        // Available in Node >= 18.
        return structuredClone(value);
    }
    else {
        return JSON.parse(JSON.stringify(value));
    }
}

},
40: function(module, exports, __req) {
"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.AwaitQueue = void 0;
const Logger_1 = __req(46);
const errors_1 = __req(41);
const logger = new Logger_1.Logger('AwaitQueue');
class AwaitQueue {
    // Queue of pending tasks (map of PendingTasks indexed by id).
    pendingTasks = new Map();
    // Incrementing PendingTask id.
    nextTaskId = 0;
    constructor() {
        logger.debug('constructor()');
    }
    get size() {
        return this.pendingTasks.size;
    }
    async push(task, name, options) {
        name = name ?? task.name;
        logger.debug(`push() [name:${name}, options:%o]`, options);
        if (typeof task !== 'function') {
            throw new TypeError('given task is not a function');
        }
        if (name) {
            try {
                Object.defineProperty(task, 'name', { value: name });
            }
            catch (error) { }
        }
        return new Promise((resolve, reject) => {
            if (name && options?.removeOngoingTasksWithSameName) {
                for (const pendingTask of this.pendingTasks.values()) {
                    if (pendingTask.name === name) {
                        pendingTask.reject(new errors_1.AwaitQueueRemovedTaskError(), {
                            canExecuteNextTask: false,
                        });
                    }
                }
            }
            const pendingTask = {
                id: this.nextTaskId++,
                task: task,
                name: name,
                enqueuedAt: Date.now(),
                executedAt: undefined,
                completed: false,
                resolve: (result) => {
                    // pendingTask.resolve() can only be called in execute() method. Since
                    // resolve() was called it means that the task successfully completed.
                    // However the task may have been stopped before it completed (via
                    // stop() or remove()) so its completed flag was already set. If this
                    // is the case, abort here since next task (if any) is already being
                    // executed.
                    if (pendingTask.completed) {
                        return;
                    }
                    pendingTask.completed = true;
                    // Remove the task from the queue.
                    this.pendingTasks.delete(pendingTask.id);
                    logger.debug(`resolving task [name:${pendingTask.name}]`);
                    // Resolve the task with the obtained result.
                    resolve(result);
                    // Execute the next pending task (if any).
                    const [nextPendingTask] = this.pendingTasks.values();
                    // NOTE: During the resolve() callback the user app may have interacted
                    // with the queue. For instance, the app may have pushed a task while
                    // the queue was empty so such a task is already being executed. If so,
                    // don't execute it twice.
                    if (nextPendingTask && !nextPendingTask.executedAt) {
                        void this.execute(nextPendingTask);
                    }
                },
                reject: (error, { canExecuteNextTask }) => {
                    // pendingTask.reject() can be called within execute() method if the
                    // task completed with error. However it may have also been called in
                    // stop() or remove() methods (before or while being executed) so its
                    // completed flag was already set. If so, abort here since next task
                    // (if any) is already being executed.
                    if (pendingTask.completed) {
                        return;
                    }
                    pendingTask.completed = true;
                    // Remove the task from the queue.
                    this.pendingTasks.delete(pendingTask.id);
                    logger.debug(`rejecting task [name:${pendingTask.name}]: %s`, String(error));
                    // Reject the task with the obtained error.
                    reject(error);
                    // May execute next pending task (if any).
                    if (canExecuteNextTask) {
                        const [nextPendingTask] = this.pendingTasks.values();
                        // NOTE: During the reject() callback the user app may have interacted
                        // with the queue. For instance, the app may have pushed a task while
                        // the queue was empty so such a task is already being executed. If so,
                        // don't execute it twice.
                        if (nextPendingTask && !nextPendingTask.executedAt) {
                            void this.execute(nextPendingTask);
                        }
                    }
                },
            };
            // Append task to the queue.
            this.pendingTasks.set(pendingTask.id, pendingTask);
            // And execute it if this is the only task in the queue.
            if (this.pendingTasks.size === 1) {
                void this.execute(pendingTask);
            }
        });
    }
    stop() {
        logger.debug('stop()');
        for (const pendingTask of this.pendingTasks.values()) {
            logger.debug(`stop() | stopping task [name:${pendingTask.name}]`);
            pendingTask.reject(new errors_1.AwaitQueueStoppedError(), {
                canExecuteNextTask: false,
            });
        }
    }
    remove(taskIdx) {
        logger.debug(`remove() [taskIdx:${taskIdx}]`);
        const pendingTask = Array.from(this.pendingTasks.values())[taskIdx];
        if (!pendingTask) {
            logger.debug(`stop() | no task with given idx [taskIdx:${taskIdx}]`);
            return;
        }
        pendingTask.reject(new errors_1.AwaitQueueRemovedTaskError(), {
            canExecuteNextTask: true,
        });
    }
    dump() {
        const now = Date.now();
        let idx = 0;
        return Array.from(this.pendingTasks.values()).map(pendingTask => ({
            idx: idx++,
            task: pendingTask.task,
            name: pendingTask.name,
            enqueuedTime: pendingTask.executedAt
                ? pendingTask.executedAt - pendingTask.enqueuedAt
                : now - pendingTask.enqueuedAt,
            executionTime: pendingTask.executedAt ? now - pendingTask.executedAt : 0,
        }));
    }
    async execute(pendingTask) {
        logger.debug(`execute() [name:${pendingTask.name}]`);
        if (pendingTask.executedAt) {
            throw new Error('task already being executed');
        }
        pendingTask.executedAt = Date.now();
        try {
            const result = await pendingTask.task();
            // Resolve the task with its resolved result (if any).
            pendingTask.resolve(result);
        }
        catch (error) {
            // Reject the task with its rejected error.
            pendingTask.reject(error, { canExecuteNextTask: true });
        }
    }
}
exports.AwaitQueue = AwaitQueue;

},
41: function(module, exports, __req) {
"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.AwaitQueueRemovedTaskError = exports.AwaitQueueStoppedError = void 0;
/**
 * Custom Error derived class used to reject pending tasks once stop() method
 * has been called.
 */
class AwaitQueueStoppedError extends Error {
    constructor(message) {
        super(message ?? 'queue stopped');
        this.name = 'AwaitQueueStoppedError';
        if (typeof Error.captureStackTrace === 'function') {
            Error.captureStackTrace(this, AwaitQueueStoppedError);
        }
    }
}
exports.AwaitQueueStoppedError = AwaitQueueStoppedError;
/**
 * Custom Error derived class used to reject pending tasks once removeTask()
 * method has been called.
 */
class AwaitQueueRemovedTaskError extends Error {
    constructor(message) {
        super(message ?? 'queue task removed');
        this.name = 'AwaitQueueRemovedTaskError';
        if (typeof Error.captureStackTrace === 'function') {
            Error.captureStackTrace(this, AwaitQueueRemovedTaskError);
        }
    }
}
exports.AwaitQueueRemovedTaskError = AwaitQueueRemovedTaskError;

},
42: function(module, exports, __req) {
var toIntIfInt = function (v) {
  return String(Number(v)) === v ? Number(v) : v;
};

var attachProperties = function (match, location, names, rawName) {
  if (rawName && !names) {
    location[rawName] = toIntIfInt(match[1]);
  }
  else {
    for (var i = 0; i < names.length; i += 1) {
      if (match[i+1] != null) {
        location[names[i]] = toIntIfInt(match[i+1]);
      }
    }
  }
};

var parseReg = function (obj, location, content) {
  var needsBlank = obj.name && obj.names;
  if (obj.push && !location[obj.push]) {
    location[obj.push] = [];
  }
  else if (needsBlank && !location[obj.name]) {
    location[obj.name] = {};
  }
  var keyLocation = obj.push ?
    {} :  // blank object that will be pushed
    needsBlank ? location[obj.name] : location; // otherwise, named location or root

  attachProperties(content.match(obj.reg), keyLocation, obj.names, obj.name);

  if (obj.push) {
    location[obj.push].push(keyLocation);
  }
};

var grammar = __req(44);
var validLine = RegExp.prototype.test.bind(/^([a-z])=(.*)/);

exports.parse = function (sdp) {
  var session = {}
    , media = []
    , location = session; // points at where properties go under (one of the above)

  // parse lines we understand
  sdp.split(/(\r\n|\r|\n)/).filter(validLine).forEach(function (l) {
    var type = l[0];
    var content = l.slice(2);
    if (type === 'm') {
      media.push({rtp: [], fmtp: []});
      location = media[media.length-1]; // point at latest media line
    }

    for (var j = 0; j < (grammar[type] || []).length; j += 1) {
      var obj = grammar[type][j];
      if (obj.reg.test(content)) {
        return parseReg(obj, location, content);
      }
    }
  });

  session.media = media; // link it up
  return session;
};

var paramReducer = function (acc, expr) {
  var s = expr.split(/=(.+)/, 2);
  if (s.length === 2) {
    acc[s[0]] = toIntIfInt(s[1]);
  } else if (s.length === 1 && expr.length > 1) {
    acc[s[0]] = undefined;
  }
  return acc;
};

exports.parseParams = function (str) {
  return str.split(/;\s?/).reduce(paramReducer, {});
};

// For backward compatibility - alias will be removed in 3.0.0
exports.parseFmtpConfig = exports.parseParams;

exports.parsePayloads = function (str) {
  return str.toString().split(' ').map(Number);
};

exports.parseRemoteCandidates = function (str) {
  var candidates = [];
  var parts = str.split(' ').map(toIntIfInt);
  for (var i = 0; i < parts.length; i += 3) {
    candidates.push({
      component: parts[i],
      ip: parts[i + 1],
      port: parts[i + 2]
    });
  }
  return candidates;
};

exports.parseImageAttributes = function (str) {
  return str.split(' ').map(function (item) {
    return item.substring(1, item.length-1).split(',').reduce(paramReducer, {});
  });
};

exports.parseSimulcastStreamList = function (str) {
  return str.split(';').map(function (stream) {
    return stream.split(',').map(function (format) {
      var scid, paused = false;

      if (format[0] !== '~') {
        scid = toIntIfInt(format);
      } else {
        scid = toIntIfInt(format.substring(1, format.length));
        paused = true;
      }

      return {
        scid: scid,
        paused: paused
      };
    });
  });
};

},
43: function(module, exports, __req) {
var grammar = __req(44);

// customized util.format - discards excess arguments and can void middle ones
var formatRegExp = /%[sdv%]/g;
var format = function (formatStr) {
  var i = 1;
  var args = arguments;
  var len = args.length;
  return formatStr.replace(formatRegExp, function (x) {
    if (i >= len) {
      return x; // missing argument
    }
    var arg = args[i];
    i += 1;
    switch (x) {
    case '%%':
      return '%';
    case '%s':
      return String(arg);
    case '%d':
      return Number(arg);
    case '%v':
      return '';
    }
  });
  // NB: we discard excess arguments - they are typically undefined from makeLine
};

var makeLine = function (type, obj, location) {
  var str = obj.format instanceof Function ?
    (obj.format(obj.push ? location : location[obj.name])) :
    obj.format;

  var args = [type + '=' + str];
  if (obj.names) {
    for (var i = 0; i < obj.names.length; i += 1) {
      var n = obj.names[i];
      if (obj.name) {
        args.push(location[obj.name][n]);
      }
      else { // for mLine and push attributes
        args.push(location[obj.names[i]]);
      }
    }
  }
  else {
    args.push(location[obj.name]);
  }
  return format.apply(null, args);
};

// RFC specified order
// TODO: extend this with all the rest
var defaultOuterOrder = [
  'v', 'o', 's', 'i',
  'u', 'e', 'p', 'c',
  'b', 't', 'r', 'z', 'a'
];
var defaultInnerOrder = ['i', 'c', 'b', 'a'];


module.exports = function (session, opts) {
  opts = opts || {};
  // ensure certain properties exist
  if (session.version == null) {
    session.version = 0; // 'v=0' must be there (only defined version atm)
  }
  if (session.name == null) {
    session.name = ' '; // 's= ' must be there if no meaningful name set
  }
  session.media.forEach(function (mLine) {
    if (mLine.payloads == null) {
      mLine.payloads = '';
    }
  });

  var outerOrder = opts.outerOrder || defaultOuterOrder;
  var innerOrder = opts.innerOrder || defaultInnerOrder;
  var sdp = [];

  // loop through outerOrder for matching properties on session
  outerOrder.forEach(function (type) {
    grammar[type].forEach(function (obj) {
      if (obj.name in session && session[obj.name] != null) {
        sdp.push(makeLine(type, obj, session));
      }
      else if (obj.push in session && session[obj.push] != null) {
        session[obj.push].forEach(function (el) {
          sdp.push(makeLine(type, obj, el));
        });
      }
    });
  });

  // then for each media line, follow the innerOrder
  session.media.forEach(function (mLine) {
    sdp.push(makeLine('m', grammar.m[0], mLine));

    innerOrder.forEach(function (type) {
      grammar[type].forEach(function (obj) {
        if (obj.name in mLine && mLine[obj.name] != null) {
          sdp.push(makeLine(type, obj, mLine));
        }
        else if (obj.push in mLine && mLine[obj.push] != null) {
          mLine[obj.push].forEach(function (el) {
            sdp.push(makeLine(type, obj, el));
          });
        }
      });
    });
  });

  return sdp.join('\r\n') + '\r\n';
};

},
44: function(module, exports, __req) {
var grammar = module.exports = {
  v: [{
    name: 'version',
    reg: /^(\d*)$/
  }],
  o: [{
    // o=- 20518 0 IN IP4 203.0.113.1
    // NB: sessionId will be a String in most cases because it is huge
    name: 'origin',
    reg: /^(\S*) (\d*) (\d*) (\S*) IP(\d) (\S*)/,
    names: ['username', 'sessionId', 'sessionVersion', 'netType', 'ipVer', 'address'],
    format: '%s %s %d %s IP%d %s'
  }],
  // default parsing of these only (though some of these feel outdated)
  s: [{ name: 'name' }],
  i: [{ name: 'description' }],
  u: [{ name: 'uri' }],
  e: [{ name: 'email' }],
  p: [{ name: 'phone' }],
  z: [{ name: 'timezones' }], // TODO: this one can actually be parsed properly...
  r: [{ name: 'repeats' }],   // TODO: this one can also be parsed properly
  // k: [{}], // outdated thing ignored
  t: [{
    // t=0 0
    name: 'timing',
    reg: /^(\d*) (\d*)/,
    names: ['start', 'stop'],
    format: '%d %d'
  }],
  c: [{
    // c=IN IP4 10.47.197.26
    name: 'connection',
    reg: /^IN IP(\d) (\S*)/,
    names: ['version', 'ip'],
    format: 'IN IP%d %s'
  }],
  b: [{
    // b=AS:4000
    push: 'bandwidth',
    reg: /^(TIAS|AS|CT|RR|RS):(\d*)/,
    names: ['type', 'limit'],
    format: '%s:%s'
  }],
  m: [{
    // m=video 51744 RTP/AVP 126 97 98 34 31
    // NB: special - pushes to session
    // TODO: rtp/fmtp should be filtered by the payloads found here?
    reg: /^(\w*) (\d*) ([\w/]*)(?: (.*))?/,
    names: ['type', 'port', 'protocol', 'payloads'],
    format: '%s %d %s %s'
  }],
  a: [
    {
      // a=rtpmap:110 opus/48000/2
      push: 'rtp',
      reg: /^rtpmap:(\d*) ([\w\-.]*)(?:\s*\/(\d*)(?:\s*\/(\S*))?)?/,
      names: ['payload', 'codec', 'rate', 'encoding'],
      format: function (o) {
        return (o.encoding)
          ? 'rtpmap:%d %s/%s/%s'
          : o.rate
            ? 'rtpmap:%d %s/%s'
            : 'rtpmap:%d %s';
      }
    },
    {
      // a=fmtp:108 profile-level-id=24;object=23;bitrate=64000
      // a=fmtp:111 minptime=10; useinbandfec=1
      push: 'fmtp',
      reg: /^fmtp:(\d*) ([\S| ]*)/,
      names: ['payload', 'config'],
      format: 'fmtp:%d %s'
    },
    {
      // a=control:streamid=0
      name: 'control',
      reg: /^control:(.*)/,
      format: 'control:%s'
    },
    {
      // a=rtcp:65179 IN IP4 193.84.77.194
      name: 'rtcp',
      reg: /^rtcp:(\d*)(?: (\S*) IP(\d) (\S*))?/,
      names: ['port', 'netType', 'ipVer', 'address'],
      format: function (o) {
        return (o.address != null)
          ? 'rtcp:%d %s IP%d %s'
          : 'rtcp:%d';
      }
    },
    {
      // a=rtcp-fb:98 trr-int 100
      push: 'rtcpFbTrrInt',
      reg: /^rtcp-fb:(\*|\d*) trr-int (\d*)/,
      names: ['payload', 'value'],
      format: 'rtcp-fb:%s trr-int %d'
    },
    {
      // a=rtcp-fb:98 nack rpsi
      push: 'rtcpFb',
      reg: /^rtcp-fb:(\*|\d*) ([\w-_]*)(?: ([\w-_]*))?/,
      names: ['payload', 'type', 'subtype'],
      format: function (o) {
        return (o.subtype != null)
          ? 'rtcp-fb:%s %s %s'
          : 'rtcp-fb:%s %s';
      }
    },
    {
      // a=extmap:2 urn:ietf:params:rtp-hdrext:toffset
      // a=extmap:1/recvonly URI-gps-string
      // a=extmap:3 urn:ietf:params:rtp-hdrext:encrypt urn:ietf:params:rtp-hdrext:smpte-tc 25@600/24
      push: 'ext',
      reg: /^extmap:(\d+)(?:\/(\w+))?(?: (urn:ietf:params:rtp-hdrext:encrypt))? (\S*)(?: (\S*))?/,
      names: ['value', 'direction', 'encrypt-uri', 'uri', 'config'],
      format: function (o) {
        return (
          'extmap:%d' +
          (o.direction ? '/%s' : '%v') +
          (o['encrypt-uri'] ? ' %s' : '%v') +
          ' %s' +
          (o.config ? ' %s' : '')
        );
      }
    },
    {
      // a=extmap-allow-mixed
      name: 'extmapAllowMixed',
      reg: /^(extmap-allow-mixed)/
    },
    {
      // a=crypto:1 AES_CM_128_HMAC_SHA1_80 inline:PS1uQCVeeCFCanVmcjkpPywjNWhcYD0mXXtxaVBR|2^20|1:32
      push: 'crypto',
      reg: /^crypto:(\d*) ([\w_]*) (\S*)(?: (\S*))?/,
      names: ['id', 'suite', 'config', 'sessionConfig'],
      format: function (o) {
        return (o.sessionConfig != null)
          ? 'crypto:%d %s %s %s'
          : 'crypto:%d %s %s';
      }
    },
    {
      // a=setup:actpass
      name: 'setup',
      reg: /^setup:(\w*)/,
      format: 'setup:%s'
    },
    {
      // a=connection:new
      name: 'connectionType',
      reg: /^connection:(new|existing)/,
      format: 'connection:%s'
    },
    {
      // a=mid:1
      name: 'mid',
      reg: /^mid:([^\s]*)/,
      format: 'mid:%s'
    },
    {
      // a=msid:0c8b064d-d807-43b4-b434-f92a889d8587 98178685-d409-46e0-8e16-7ef0db0db64a
      push: 'msid',
      reg: /^msid:([\w-]+)(?: ([\w-]+))?/,
      names: ['id', 'appdata'],
      format: 'msid:%s %s'
    },
    {
      // a=ptime:20
      name: 'ptime',
      reg: /^ptime:(\d*(?:\.\d*)*)/,
      format: 'ptime:%d'
    },
    {
      // a=maxptime:60
      name: 'maxptime',
      reg: /^maxptime:(\d*(?:\.\d*)*)/,
      format: 'maxptime:%d'
    },
    {
      // a=sendrecv
      name: 'direction',
      reg: /^(sendrecv|recvonly|sendonly|inactive)/
    },
    {
      // a=ice-lite
      name: 'icelite',
      reg: /^(ice-lite)/
    },
    {
      // a=ice-ufrag:F7gI
      name: 'iceUfrag',
      reg: /^ice-ufrag:(\S*)/,
      format: 'ice-ufrag:%s'
    },
    {
      // a=ice-pwd:x9cml/YzichV2+XlhiMu8g
      name: 'icePwd',
      reg: /^ice-pwd:(\S*)/,
      format: 'ice-pwd:%s'
    },
    {
      // a=fingerprint:SHA-1 00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33
      name: 'fingerprint',
      reg: /^fingerprint:(\S*) (\S*)/,
      names: ['type', 'hash'],
      format: 'fingerprint:%s %s'
    },
    {
      // a=candidate:0 1 UDP 2113667327 203.0.113.1 54400 typ host
      // a=candidate:1162875081 1 udp 2113937151 192.168.34.75 60017 typ host generation 0 network-id 3 network-cost 10
      // a=candidate:3289912957 2 udp 1845501695 193.84.77.194 60017 typ srflx raddr 192.168.34.75 rport 60017 generation 0 network-id 3 network-cost 10
      // a=candidate:229815620 1 tcp 1518280447 192.168.150.19 60017 typ host tcptype active generation 0 network-id 3 network-cost 10
      // a=candidate:3289912957 2 tcp 1845501695 193.84.77.194 60017 typ srflx raddr 192.168.34.75 rport 60017 tcptype passive generation 0 network-id 3 network-cost 10
      push:'candidates',
      reg: /^candidate:(\S*) (\d*) (\S*) (\d*) (\S*) (\d*) typ (\S*)(?: raddr (\S*) rport (\d*))?(?: tcptype (\S*))?(?: generation (\d*))?(?: network-id (\d*))?(?: network-cost (\d*))?/,
      names: ['foundation', 'component', 'transport', 'priority', 'ip', 'port', 'type', 'raddr', 'rport', 'tcptype', 'generation', 'network-id', 'network-cost'],
      format: function (o) {
        var str = 'candidate:%s %d %s %d %s %d typ %s';

        str += (o.raddr != null) ? ' raddr %s rport %d' : '%v%v';

        // NB: candidate has three optional chunks, so %void middles one if it's missing
        str += (o.tcptype != null) ? ' tcptype %s' : '%v';

        if (o.generation != null) {
          str += ' generation %d';
        }

        str += (o['network-id'] != null) ? ' network-id %d' : '%v';
        str += (o['network-cost'] != null) ? ' network-cost %d' : '%v';
        return str;
      }
    },
    {
      // a=end-of-candidates (keep after the candidates line for readability)
      name: 'endOfCandidates',
      reg: /^(end-of-candidates)/
    },
    {
      // a=remote-candidates:1 203.0.113.1 54400 2 203.0.113.1 54401 ...
      name: 'remoteCandidates',
      reg: /^remote-candidates:(.*)/,
      format: 'remote-candidates:%s'
    },
    {
      // a=ice-options:google-ice
      name: 'iceOptions',
      reg: /^ice-options:(\S*)/,
      format: 'ice-options:%s'
    },
    {
      // a=ssrc:2566107569 cname:t9YU8M1UxTF8Y1A1
      push: 'ssrcs',
      reg: /^ssrc:(\d*) ([\w_-]*)(?::(.*))?/,
      names: ['id', 'attribute', 'value'],
      format: function (o) {
        var str = 'ssrc:%d';
        if (o.attribute != null) {
          str += ' %s';
          if (o.value != null) {
            str += ':%s';
          }
        }
        return str;
      }
    },
    {
      // a=ssrc-group:FEC 1 2
      // a=ssrc-group:FEC-FR 3004364195 1080772241
      push: 'ssrcGroups',
      // token-char = %x21 / %x23-27 / %x2A-2B / %x2D-2E / %x30-39 / %x41-5A / %x5E-7E
      reg: /^ssrc-group:([\x21\x23\x24\x25\x26\x27\x2A\x2B\x2D\x2E\w]*) (.*)/,
      names: ['semantics', 'ssrcs'],
      format: 'ssrc-group:%s %s'
    },
    {
      // a=msid-semantic: WMS Jvlam5X3SX1OP6pn20zWogvaKJz5Hjf9OnlV
      name: 'msidSemantic',
      reg: /^msid-semantic:\s?(\w*) (\S*)/,
      names: ['semantic', 'token'],
      format: 'msid-semantic: %s %s' // space after ':' is not accidental
    },
    {
      // a=group:BUNDLE audio video
      push: 'groups',
      reg: /^group:(\w*) (.*)/,
      names: ['type', 'mids'],
      format: 'group:%s %s'
    },
    {
      // a=rtcp-mux
      name: 'rtcpMux',
      reg: /^(rtcp-mux)/
    },
    {
      // a=rtcp-rsize
      name: 'rtcpRsize',
      reg: /^(rtcp-rsize)/
    },
    {
      // a=sctpmap:5000 webrtc-datachannel 1024
      name: 'sctpmap',
      reg: /^sctpmap:([\w_/]*) (\S*)(?: (\S*))?/,
      names: ['sctpmapNumber', 'app', 'maxMessageSize'],
      format: function (o) {
        return (o.maxMessageSize != null)
          ? 'sctpmap:%s %s %s'
          : 'sctpmap:%s %s';
      }
    },
    {
      // a=x-google-flag:conference
      name: 'xGoogleFlag',
      reg: /^x-google-flag:([^\s]*)/,
      format: 'x-google-flag:%s'
    },
    {
      // a=rid:1 send max-width=1280;max-height=720;max-fps=30;depend=0
      push: 'rids',
      reg: /^rid:([\d\w]+) (\w+)(?: ([\S| ]*))?/,
      names: ['id', 'direction', 'params'],
      format: function (o) {
        return (o.params) ? 'rid:%s %s %s' : 'rid:%s %s';
      }
    },
    {
      // a=imageattr:97 send [x=800,y=640,sar=1.1,q=0.6] [x=480,y=320] recv [x=330,y=250]
      // a=imageattr:* send [x=800,y=640] recv *
      // a=imageattr:100 recv [x=320,y=240]
      push: 'imageattrs',
      reg: new RegExp(
        // a=imageattr:97
        '^imageattr:(\\d+|\\*)' +
        // send [x=800,y=640,sar=1.1,q=0.6] [x=480,y=320]
        '[\\s\\t]+(send|recv)[\\s\\t]+(\\*|\\[\\S+\\](?:[\\s\\t]+\\[\\S+\\])*)' +
        // recv [x=330,y=250]
        '(?:[\\s\\t]+(recv|send)[\\s\\t]+(\\*|\\[\\S+\\](?:[\\s\\t]+\\[\\S+\\])*))?'
      ),
      names: ['pt', 'dir1', 'attrs1', 'dir2', 'attrs2'],
      format: function (o) {
        return 'imageattr:%s %s %s' + (o.dir2 ? ' %s %s' : '');
      }
    },
    {
      // a=simulcast:send 1,2,3;~4,~5 recv 6;~7,~8
      // a=simulcast:recv 1;4,5 send 6;7
      name: 'simulcast',
      reg: new RegExp(
        // a=simulcast:
        '^simulcast:' +
        // send 1,2,3;~4,~5
        '(send|recv) ([a-zA-Z0-9\\-_~;,]+)' +
        // space + recv 6;~7,~8
        '(?:\\s?(send|recv) ([a-zA-Z0-9\\-_~;,]+))?' +
        // end
        '$'
      ),
      names: ['dir1', 'list1', 'dir2', 'list2'],
      format: function (o) {
        return 'simulcast:%s %s' + (o.dir2 ? ' %s %s' : '');
      }
    },
    {
      // old simulcast draft 03 (implemented by Firefox)
      //   https://tools.ietf.org/html/draft-ietf-mmusic-sdp-simulcast-03
      // a=simulcast: recv pt=97;98 send pt=97
      // a=simulcast: send rid=5;6;7 paused=6,7
      name: 'simulcast_03',
      reg: /^simulcast:[\s\t]+([\S+\s\t]+)$/,
      names: ['value'],
      format: 'simulcast: %s'
    },
    {
      // a=framerate:25
      // a=framerate:29.97
      name: 'framerate',
      reg: /^framerate:(\d+(?:$|\.\d+))/,
      format: 'framerate:%s'
    },
    {
      // RFC4570
      // a=source-filter: incl IN IP4 239.5.2.31 10.1.15.5
      name: 'sourceFilter',
      reg: /^source-filter: *(excl|incl) (\S*) (IP4|IP6|\*) (\S*) (.*)/,
      names: ['filterMode', 'netType', 'addressTypes', 'destAddress', 'srcList'],
      format: 'source-filter: %s %s %s %s %s'
    },
    {
      // a=bundle-only
      name: 'bundleOnly',
      reg: /^(bundle-only)/
    },
    {
      // a=label:1
      name: 'label',
      reg: /^label:(.+)/,
      format: 'label:%s'
    },
    {
      // RFC version 26 for SCTP over DTLS
      // https://tools.ietf.org/html/draft-ietf-mmusic-sctp-sdp-26#section-5
      name: 'sctpPort',
      reg: /^sctp-port:(\d+)$/,
      format: 'sctp-port:%s'
    },
    {
      // RFC version 26 for SCTP over DTLS
      // https://tools.ietf.org/html/draft-ietf-mmusic-sctp-sdp-26#section-6
      name: 'maxMessageSize',
      reg: /^max-message-size:(\d+)$/,
      format: 'max-message-size:%s'
    },
    {
      // RFC7273
      // a=ts-refclk:ptp=IEEE1588-2008:39-A7-94-FF-FE-07-CB-D0:37
      push:'tsRefClocks',
      reg: /^ts-refclk:([^\s=]*)(?:=(\S*))?/,
      names: ['clksrc', 'clksrcExt'],
      format: function (o) {
        return 'ts-refclk:%s' + (o.clksrcExt != null ? '=%s' : '');
      }
    },
    {
      // RFC7273
      // a=mediaclk:direct=963214424
      name:'mediaClk',
      reg: /^mediaclk:(?:id=(\S*))? *([^\s=]*)(?:=(\S*))?(?: *rate=(\d+)\/(\d+))?/,
      names: ['id', 'mediaClockName', 'mediaClockValue', 'rateNumerator', 'rateDenominator'],
      format: function (o) {
        var str = 'mediaclk:';
        str += (o.id != null ? 'id=%s %s' : '%v%s');
        str += (o.mediaClockValue != null ? '=%s' : '');
        str += (o.rateNumerator != null ? ' rate=%s' : '');
        str += (o.rateDenominator != null ? '/%s' : '');
        return str;
      }
    },
    {
      // a=keywds:keywords
      name: 'keywords',
      reg: /^keywds:(.+)$/,
      format: 'keywds:%s'
    },
    {
      // a=content:main
      name: 'content',
      reg: /^content:(.+)/,
      format: 'content:%s'
    },
    // BFCP https://tools.ietf.org/html/rfc4583
    {
      // a=floorctrl:c-s
      name: 'bfcpFloorCtrl',
      reg: /^floorctrl:(c-only|s-only|c-s)/,
      format: 'floorctrl:%s'
    },
    {
      // a=confid:1
      name: 'bfcpConfId',
      reg: /^confid:(\d+)/,
      format: 'confid:%s'
    },
    {
      // a=userid:1
      name: 'bfcpUserId',
      reg: /^userid:(\d+)/,
      format: 'userid:%s'
    },
    {
      // a=floorid:1
      name: 'bfcpFloorId',
      reg: /^floorid:(.+) (?:m-stream|mstrm):(.+)/,
      names: ['id', 'mStream'],
      format: 'floorid:%s mstrm:%s'
    },
    {
      // any a= that we don't understand is kept verbatim on media.invalid
      push: 'invalid',
      names: ['value']
    }
  ]
};

// set sensible defaults to avoid polluting the grammar with boring details
Object.keys(grammar).forEach(function (key) {
  var objs = grammar[key];
  objs.forEach(function (obj) {
    if (!obj.reg) {
      obj.reg = /(.*)/;
    }
    if (!obj.format) {
      obj.format = '%s';
    }
  });
});

},
45: function(module, exports, __req) {
"use strict";
var __createBinding = (this && this.__createBinding) || (Object.create ? (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    var desc = Object.getOwnPropertyDescriptor(m, k);
    if (!desc || ("get" in desc ? !m.__esModule : desc.writable || desc.configurable)) {
      desc = { enumerable: true, get: function() { return m[k]; } };
    }
    Object.defineProperty(o, k2, desc);
}) : (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    o[k2] = m[k];
}));
var __setModuleDefault = (this && this.__setModuleDefault) || (Object.create ? (function(o, v) {
    Object.defineProperty(o, "default", { enumerable: true, value: v });
}) : function(o, v) {
    o["default"] = v;
});
var __importStar = (this && this.__importStar) || (function () {
    var ownKeys = function(o) {
        ownKeys = Object.getOwnPropertyNames || function (o) {
            var ar = [];
            for (var k in o) if (Object.prototype.hasOwnProperty.call(o, k)) ar[ar.length] = k;
            return ar;
        };
        return ownKeys(o);
    };
    return function (mod) {
        if (mod && mod.__esModule) return mod;
        var result = {};
        if (mod != null) for (var k = ownKeys(mod), i = 0; i < k.length; i++) if (k[i] !== "default") __createBinding(result, mod, k[i]);
        __setModuleDefault(result, mod);
        return result;
    };
})();
Object.defineProperty(exports, "__esModule", { value: true });
exports.RemoteOfferMediaSection = exports.RemoteAnswerMediaSection = exports.RemoteMediaSection = void 0;
const sdpTransform = __importStar(__req(30));
const utils = __importStar(__req(13));
class RemoteMediaSection {
    // SDP media object.
    _mediaObject;
    constructor({ iceParameters, iceCandidates, dtlsParameters, }) {
        this._mediaObject = {
            type: '',
            port: 0,
            protocol: '',
            payloads: '',
            rtp: [],
            fmtp: [],
        };
        if (iceParameters) {
            this.setIceParameters(iceParameters);
        }
        if (iceCandidates) {
            this._mediaObject.candidates = [];
            for (const candidate of iceCandidates) {
                const candidateObject = {
                    foundation: candidate.foundation,
                    // mediasoup does mandates rtcp-mux so candidates component is always
                    // RTP (1).
                    component: 1,
                    // Be ready for new candidate.address field in mediasoup server side
                    // field and keep backward compatibility with deprecated candidate.ip.
                    ip: candidate.address ?? candidate.ip,
                    port: candidate.port,
                    priority: candidate.priority,
                    transport: candidate.protocol,
                    type: candidate.type,
                };
                if (candidate.tcpType) {
                    candidateObject.tcptype = candidate.tcpType;
                }
                this._mediaObject.candidates.push(candidateObject);
            }
            this._mediaObject.endOfCandidates = 'end-of-candidates';
            this._mediaObject.iceOptions = 'renomination';
        }
        if (dtlsParameters) {
            this.setDtlsRole(dtlsParameters.role);
        }
    }
    get mid() {
        return String(this._mediaObject.mid);
    }
    get closed() {
        return this._mediaObject.port === 0;
    }
    getObject() {
        return this._mediaObject;
    }
    setIceParameters(iceParameters) {
        this._mediaObject.iceUfrag = iceParameters.usernameFragment;
        this._mediaObject.icePwd = iceParameters.password;
    }
    pause() {
        this._mediaObject.direction = 'inactive';
    }
    disable() {
        this.pause();
    }
    close() {
        this.disable();
        // Set port in m= line to 0, which means that the media sction is closed.
        this._mediaObject.port = 0;
        // NOTE: Do not remove header extensions since it's controversial in the spec.
        delete this._mediaObject.candidates;
        delete this._mediaObject.endOfCandidates;
        delete this._mediaObject.iceUfrag;
        delete this._mediaObject.icePwd;
        delete this._mediaObject.iceOptions;
        this._mediaObject.rtp = [];
        this._mediaObject.fmtp = [];
        delete this._mediaObject.rtcp;
        delete this._mediaObject.rtcpFb;
        delete this._mediaObject.ssrcs;
        delete this._mediaObject.ssrcGroups;
        delete this._mediaObject.simulcast;
        delete this._mediaObject.simulcast_03;
        delete this._mediaObject.rids;
        delete this._mediaObject.extmapAllowMixed;
    }
}
exports.RemoteMediaSection = RemoteMediaSection;
class RemoteAnswerMediaSection extends RemoteMediaSection {
    constructor({ iceParameters, iceCandidates, dtlsParameters, sctpParameters, plainRtpParameters, offerMediaObject, offerRtpParameters, answerRtpParameters, codecOptions, }) {
        super({ iceParameters, iceCandidates, dtlsParameters });
        this._mediaObject.mid = String(offerMediaObject.mid);
        this._mediaObject.type = offerMediaObject.type;
        this._mediaObject.protocol = offerMediaObject.protocol;
        if (!plainRtpParameters) {
            this._mediaObject.connection = { ip: '127.0.0.1', version: 4 };
            this._mediaObject.port = 7;
        }
        else {
            this._mediaObject.connection = {
                ip: plainRtpParameters.ip,
                version: plainRtpParameters.ipVersion,
            };
            this._mediaObject.port = plainRtpParameters.port;
        }
        switch (offerMediaObject.type) {
            case 'audio':
            case 'video': {
                this._mediaObject.direction = 'recvonly';
                this._mediaObject.rtp = [];
                this._mediaObject.rtcpFb = [];
                this._mediaObject.fmtp = [];
                for (const codec of answerRtpParameters.codecs) {
                    const rtp = {
                        payload: codec.payloadType,
                        codec: getCodecName(codec),
                        rate: codec.clockRate,
                    };
                    if (codec.channels > 1) {
                        rtp.encoding = codec.channels;
                    }
                    this._mediaObject.rtp.push(rtp);
                    const codecParameters = utils.clone(codec.parameters) ?? {};
                    let codecRtcpFeedback = utils.clone(codec.rtcpFeedback) ?? [];
                    if (codecOptions) {
                        const { opusStereo, opusFec, opusDtx, opusMaxPlaybackRate, opusMaxAverageBitrate, opusPtime, opusNack, videoGoogleStartBitrate, videoGoogleMaxBitrate, videoGoogleMinBitrate, } = codecOptions;
                        const offerCodec = offerRtpParameters.codecs.find((c) => c.payloadType === codec.payloadType);
                        switch (codec.mimeType.toLowerCase()) {
                            case 'audio/opus':
                            case 'audio/multiopus': {
                                if (opusStereo !== undefined) {
                                    offerCodec.parameters['sprop-stereo'] = opusStereo ? 1 : 0;
                                    codecParameters['stereo'] = opusStereo ? 1 : 0;
                                }
                                if (opusFec !== undefined) {
                                    offerCodec.parameters['useinbandfec'] = opusFec ? 1 : 0;
                                    codecParameters['useinbandfec'] = opusFec ? 1 : 0;
                                }
                                if (opusDtx !== undefined) {
                                    offerCodec.parameters['usedtx'] = opusDtx ? 1 : 0;
                                    codecParameters['usedtx'] = opusDtx ? 1 : 0;
                                }
                                if (opusMaxPlaybackRate !== undefined) {
                                    codecParameters['maxplaybackrate'] = opusMaxPlaybackRate;
                                }
                                if (opusMaxAverageBitrate !== undefined) {
                                    codecParameters['maxaveragebitrate'] = opusMaxAverageBitrate;
                                }
                                if (opusPtime !== undefined) {
                                    offerCodec.parameters['ptime'] = opusPtime;
                                    codecParameters['ptime'] = opusPtime;
                                }
                                // If opusNack is not set, we must remove NACK support for OPUS.
                                // Otherwise it would be enabled for those handlers that artificially
                                // announce it in their RTP capabilities.
                                if (!opusNack) {
                                    offerCodec.rtcpFeedback = offerCodec.rtcpFeedback.filter(fb => fb.type !== 'nack' || fb.parameter);
                                    codecRtcpFeedback = codecRtcpFeedback.filter(fb => fb.type !== 'nack' || fb.parameter);
                                }
                                break;
                            }
                            case 'video/vp8':
                            case 'video/vp9':
                            case 'video/h264':
                            case 'video/h265':
                            case 'video/av1': {
                                if (videoGoogleStartBitrate !== undefined) {
                                    codecParameters['x-google-start-bitrate'] =
                                        videoGoogleStartBitrate;
                                }
                                if (videoGoogleMaxBitrate !== undefined) {
                                    codecParameters['x-google-max-bitrate'] =
                                        videoGoogleMaxBitrate;
                                }
                                if (videoGoogleMinBitrate !== undefined) {
                                    codecParameters['x-google-min-bitrate'] =
                                        videoGoogleMinBitrate;
                                }
                                break;
                            }
                        }
                    }
                    const fmtp = {
                        payload: codec.payloadType,
                        config: '',
                    };
                    for (const key of Object.keys(codecParameters)) {
                        if (fmtp.config) {
                            fmtp.config += ';';
                        }
                        fmtp.config += `${key}=${codecParameters[key]}`;
                    }
                    if (fmtp.config) {
                        this._mediaObject.fmtp.push(fmtp);
                    }
                    for (const fb of codecRtcpFeedback) {
                        this._mediaObject.rtcpFb.push({
                            payload: codec.payloadType,
                            type: fb.type,
                            subtype: fb.parameter,
                        });
                    }
                }
                this._mediaObject.payloads = answerRtpParameters.codecs
                    .map((codec) => codec.payloadType)
                    .join(' ');
                this._mediaObject.ext = [];
                for (const ext of answerRtpParameters.headerExtensions) {
                    // Don't add a header extension if not present in the offer.
                    const found = (offerMediaObject.ext ?? []).some((localExt) => localExt.uri === ext.uri);
                    if (!found) {
                        continue;
                    }
                    this._mediaObject.ext.push({
                        uri: ext.uri,
                        value: ext.id,
                    });
                }
                // Allow both 1 byte and 2 bytes length header extensions since
                // mediasoup can receive both at any time.
                if (offerMediaObject.extmapAllowMixed === 'extmap-allow-mixed') {
                    this._mediaObject.extmapAllowMixed = 'extmap-allow-mixed';
                }
                // Simulcast.
                if (offerMediaObject.simulcast) {
                    this._mediaObject.simulcast = {
                        dir1: 'recv',
                        list1: offerMediaObject.simulcast.list1,
                    };
                    this._mediaObject.rids = [];
                    for (const rid of offerMediaObject.rids ?? []) {
                        if (rid.direction !== 'send') {
                            continue;
                        }
                        this._mediaObject.rids.push({
                            id: rid.id,
                            direction: 'recv',
                        });
                    }
                }
                // Simulcast (draft version 03).
                else if (offerMediaObject.simulcast_03) {
                    this._mediaObject.simulcast_03 = {
                        value: offerMediaObject.simulcast_03.value.replace(/send/g, 'recv'),
                    };
                    this._mediaObject.rids = [];
                    for (const rid of offerMediaObject.rids ?? []) {
                        if (rid.direction !== 'send') {
                            continue;
                        }
                        this._mediaObject.rids.push({
                            id: rid.id,
                            direction: 'recv',
                        });
                    }
                }
                this._mediaObject.rtcpMux = 'rtcp-mux';
                this._mediaObject.rtcpRsize = 'rtcp-rsize';
                break;
            }
            case 'application': {
                // New spec.
                if (typeof offerMediaObject.sctpPort === 'number') {
                    this._mediaObject.payloads = 'webrtc-datachannel';
                    this._mediaObject.sctpPort = sctpParameters.port;
                    this._mediaObject.maxMessageSize =
                        sctpParameters.maxReceiveMessageSize;
                }
                // Old spec.
                else if (offerMediaObject.sctpmap) {
                    this._mediaObject.payloads = String(sctpParameters.port);
                    this._mediaObject.sctpmap = {
                        app: 'webrtc-datachannel',
                        sctpmapNumber: sctpParameters.port,
                        maxMessageSize: sctpParameters.maxReceiveMessageSize,
                    };
                }
                break;
            }
        }
    }
    setDtlsRole(role) {
        switch (role) {
            case 'client': {
                this._mediaObject.setup = 'active';
                break;
            }
            case 'server': {
                this._mediaObject.setup = 'passive';
                break;
            }
            case 'auto': {
                this._mediaObject.setup = 'actpass';
                break;
            }
        }
    }
    resume() {
        this._mediaObject.direction = 'recvonly';
    }
    muxSimulcastStreams(encodings) {
        if (!this._mediaObject.simulcast?.list1) {
            return;
        }
        const layers = {};
        for (const encoding of encodings) {
            if (encoding.rid) {
                layers[encoding.rid] = encoding;
            }
        }
        const raw = this._mediaObject.simulcast.list1;
        const simulcastStreams = sdpTransform.parseSimulcastStreamList(raw);
        for (const simulcastStream of simulcastStreams) {
            for (const simulcastFormat of simulcastStream) {
                simulcastFormat.paused = !layers[simulcastFormat.scid]?.active;
            }
        }
        this._mediaObject.simulcast.list1 = simulcastStreams
            .map(simulcastFormats => simulcastFormats.map(f => `${f.paused ? '~' : ''}${f.scid}`).join(','))
            .join(';');
    }
}
exports.RemoteAnswerMediaSection = RemoteAnswerMediaSection;
class RemoteOfferMediaSection extends RemoteMediaSection {
    constructor({ iceParameters, iceCandidates, dtlsParameters, sctpParameters, plainRtpParameters, mid, kind, offerRtpParameters, streamId, trackId, }) {
        super({ iceParameters, iceCandidates, dtlsParameters });
        this._mediaObject.mid = String(mid);
        this._mediaObject.type = kind;
        if (!plainRtpParameters) {
            this._mediaObject.connection = { ip: '127.0.0.1', version: 4 };
            if (!sctpParameters) {
                this._mediaObject.protocol = 'UDP/TLS/RTP/SAVPF';
            }
            else {
                this._mediaObject.protocol = 'UDP/DTLS/SCTP';
            }
            this._mediaObject.port = 7;
        }
        else {
            this._mediaObject.connection = {
                ip: plainRtpParameters.ip,
                version: plainRtpParameters.ipVersion,
            };
            this._mediaObject.protocol = 'RTP/AVP';
            this._mediaObject.port = plainRtpParameters.port;
        }
        // Allow both 1 byte and 2 bytes length header extensions since
        // mediasoup can send both at any time.
        this._mediaObject.extmapAllowMixed = 'extmap-allow-mixed';
        switch (kind) {
            case 'audio':
            case 'video': {
                this._mediaObject.direction = 'sendonly';
                this._mediaObject.rtp = [];
                this._mediaObject.rtcpFb = [];
                this._mediaObject.fmtp = [];
                // @ts-expect-error --- @types/sdp-transform 2.15.0 is not ready for
                // sdp-transform 3.0.0.
                this._mediaObject.msid = [{ id: streamId, appdata: trackId }];
                for (const codec of offerRtpParameters.codecs) {
                    const rtp = {
                        payload: codec.payloadType,
                        codec: getCodecName(codec),
                        rate: codec.clockRate,
                    };
                    if (codec.channels > 1) {
                        rtp.encoding = codec.channels;
                    }
                    this._mediaObject.rtp.push(rtp);
                    const fmtp = {
                        payload: codec.payloadType,
                        config: '',
                    };
                    for (const key of Object.keys(codec.parameters ?? {})) {
                        if (fmtp.config) {
                            fmtp.config += ';';
                        }
                        fmtp.config += `${key}=${codec.parameters[key]}`;
                    }
                    if (fmtp.config) {
                        this._mediaObject.fmtp.push(fmtp);
                    }
                    for (const fb of codec.rtcpFeedback) {
                        this._mediaObject.rtcpFb.push({
                            payload: codec.payloadType,
                            type: fb.type,
                            subtype: fb.parameter,
                        });
                    }
                }
                this._mediaObject.payloads = offerRtpParameters.codecs
                    .map((codec) => codec.payloadType)
                    .join(' ');
                this._mediaObject.ext = [];
                for (const ext of offerRtpParameters.headerExtensions) {
                    this._mediaObject.ext.push({
                        uri: ext.uri,
                        value: ext.id,
                    });
                }
                this._mediaObject.rtcpMux = 'rtcp-mux';
                this._mediaObject.rtcpRsize = 'rtcp-rsize';
                const encoding = offerRtpParameters.encodings[0];
                const ssrc = encoding.ssrc;
                const rtxSsrc = encoding.rtx?.ssrc;
                this._mediaObject.ssrcs = [];
                this._mediaObject.ssrcGroups = [];
                if (ssrc && offerRtpParameters.rtcp.cname) {
                    this._mediaObject.ssrcs.push({
                        id: ssrc,
                        attribute: 'cname',
                        value: offerRtpParameters.rtcp.cname,
                    });
                }
                if (rtxSsrc) {
                    if (offerRtpParameters.rtcp.cname) {
                        this._mediaObject.ssrcs.push({
                            id: rtxSsrc,
                            attribute: 'cname',
                            value: offerRtpParameters.rtcp.cname,
                        });
                    }
                    // Associate original and retransmission SSRCs.
                    if (ssrc) {
                        this._mediaObject.ssrcGroups.push({
                            semantics: 'FID',
                            ssrcs: `${ssrc} ${rtxSsrc}`,
                        });
                    }
                }
                break;
            }
            case 'application': {
                this._mediaObject.payloads = 'webrtc-datachannel';
                this._mediaObject.sctpPort = sctpParameters.port;
                this._mediaObject.maxMessageSize =
                    sctpParameters.maxReceiveMessageSize;
                break;
            }
        }
    }
    // eslint-disable-next-line @typescript-eslint/no-unused-vars
    setDtlsRole(role) {
        // Always 'actpass'.
        this._mediaObject.setup = 'actpass';
    }
    resume() {
        this._mediaObject.direction = 'sendonly';
    }
}
exports.RemoteOfferMediaSection = RemoteOfferMediaSection;
function getCodecName(codec) {
    const MimeTypeRegex = new RegExp('^(audio|video)/(.+)', 'i');
    const mimeTypeMatch = MimeTypeRegex.exec(codec.mimeType);
    if (!mimeTypeMatch) {
        throw new TypeError('invalid codec.mimeType');
    }
    return mimeTypeMatch[2];
}

},
46: function(module, exports, __req) {
"use strict";
var __importDefault = (this && this.__importDefault) || function (mod) {
    return (mod && mod.__esModule) ? mod : { "default": mod };
};
Object.defineProperty(exports, "__esModule", { value: true });
exports.Logger = void 0;
const debug_1 = __importDefault(__req(2));
const LIB_NAME = 'awaitqueue';
class Logger {
    _debug;
    _warn;
    _error;
    constructor(prefix) {
        if (prefix) {
            this._debug = (0, debug_1.default)(`${LIB_NAME}:${prefix}`);
            this._warn = (0, debug_1.default)(`${LIB_NAME}:WARN:${prefix}`);
            this._error = (0, debug_1.default)(`${LIB_NAME}:ERROR:${prefix}`);
        }
        else {
            this._debug = (0, debug_1.default)(LIB_NAME);
            this._warn = (0, debug_1.default)(`${LIB_NAME}:WARN`);
            this._error = (0, debug_1.default)(`${LIB_NAME}:ERROR`);
        }
        /* eslint-disable no-console */
        this._debug.log = console.info.bind(console);
        this._warn.log = console.warn.bind(console);
        this._error.log = console.error.bind(console);
        /* eslint-enable no-console */
    }
    get debug() {
        return this._debug;
    }
    get warn() {
        return this._warn;
    }
    get error() {
        return this._error;
    }
}
exports.Logger = Logger;

}
};
const __cache = Object.create(null);
function __req(id) {
  if (Object.prototype.hasOwnProperty.call(__cache,id)) return __cache[id].exports;
  const factory = __modules[id];
  if (!factory) throw new Error('Bundled module not found: '+id);
  const module = {exports:{}};
  __cache[id] = module;
  factory(module,module.exports,__req);
  return module.exports;
}
__req(0);
})();
