const TARGET_SAMPLE_RATE = 48000;
const CHANNELS = 1;
const FRAME_SAMPLES = 960;
const MAX_BUFFERED_SAMPLES = FRAME_SAMPLES * 6;
const MAX_PACKET_BYTES = 1112;
const OPUS_APPLICATION_VOIP = 2048;
const OPUS_OK = 0;
const OPUS_SET_BITRATE_REQUEST = 4002;
const OPUS_SET_INBAND_FEC_REQUEST = 4012;
const OPUS_SET_PACKET_LOSS_PERC_REQUEST = 4014;
const PLAYOUT_FRAME_MS = 20;
const MIN_PLAYOUT_DELAY_MS = 40;
const MAX_PLAYOUT_DELAY_MS = 200;
const MAX_REMOTE_FRAMES = 64;
const REMOTE_IDLE_TIMEOUT_MS = 10000;

let opus = null;
let encoder = 0;
let decoder = 0;
let pcmPointer = 0;
let packetPointer = 0;
let errorPointer = 0;
let audioPort = null;
let captureEnabled = false;
let sourceSampleRate = TARGET_SAMPLE_RATE;
let bitrate = 32000;
let fec = false;
let packetLossPercent = 0;
let captureBlocks = 0;
let encodedFrames = 0;
let decodedFrames = 0;
let encodedBytes = 0;
let playbackUnderflows = 0;
let playbackDrops = 0;
let captureDrops = 0;
let lastPacketBytes = 0;
let lastStatsAt = 0;
let initialized = false;
let productionMode = false;
let nextMediaSequence = 1;
let encodeDurationTotalMs = 0;
let encodeDurationMaxMs = 0;
let encodeMeasurements = 0;
let lastEncodeCompletedAtMs = 0;
let cadenceMaxDeviationMs = 0;
let playoutTimer = 0;
let remoteLateDrops = 0;
let remoteDuplicateDrops = 0;
let remoteReorderedPackets = 0;
let remoteMissingFrames = 0;
let remoteOverflowDrops = 0;
let remoteNormalFrames = 0;
let remoteFecFrames = 0;
let remotePlcFrames = 0;

const remoteDecoders = new Map();

const ring = new Float32Array(MAX_BUFFERED_SAMPLES);
let ringRead = 0;
let ringWrite = 0;
let ringCount = 0;
let resampleCarry = new Float32Array(0);
let resamplePosition = 0;

function fail(message) {
    self.postMessage({
        type: "error",
        error: String(message || "browser_audio_worker_failed")
    });
}

function assertOpus(code, operation) {
    if (code !== OPUS_OK) throw new Error(operation + "_failed_" + code);
}

function configureEncoder(nextBitrate, nextFec, nextPacketLossPercent) {
    if (!opus || !encoder) throw new Error("opus_encoder_not_ready");

    const safeBitrate = Math.max(
        6000,
        Math.min(510000, Number(nextBitrate) || 32000));
    const safeLoss = Math.max(
        0,
        Math.min(100, Number(nextPacketLossPercent) || 0));

    assertOpus(
        opus._voice_opus_encoder_ctl_int(
            encoder,
            OPUS_SET_BITRATE_REQUEST,
            safeBitrate),
        "opus_set_bitrate");
    assertOpus(
        opus._voice_opus_encoder_ctl_int(
            encoder,
            OPUS_SET_INBAND_FEC_REQUEST,
            nextFec ? 1 : 0),
        "opus_set_fec");
    assertOpus(
        opus._voice_opus_encoder_ctl_int(
            encoder,
            OPUS_SET_PACKET_LOSS_PERC_REQUEST,
            safeLoss),
        "opus_set_packet_loss");

    bitrate = safeBitrate;
    fec = nextFec === true;
    packetLossPercent = safeLoss;
}

function pushSample(sample) {
    if (ringCount === ring.length) {
        ringRead = (ringRead + 1) % ring.length;
        ringCount -= 1;
        captureDrops += 1;
    }

    ring[ringWrite] = Number.isFinite(sample)
        ? Math.max(-1, Math.min(1, sample))
        : 0;
    ringWrite = (ringWrite + 1) % ring.length;
    ringCount += 1;
}

function appendResampled(samples) {
    if (sourceSampleRate === TARGET_SAMPLE_RATE) {
        for (let index = 0; index < samples.length; index += 1) {
            pushSample(samples[index]);
        }
        return;
    }

    const input = new Float32Array(resampleCarry.length + samples.length);
    input.set(resampleCarry, 0);
    input.set(samples, resampleCarry.length);

    const step = sourceSampleRate / TARGET_SAMPLE_RATE;
    while (resamplePosition + 1 < input.length) {
        const index = Math.floor(resamplePosition);
        const fraction = resamplePosition - index;
        pushSample(
            input[index] +
            (input[index + 1] - input[index]) * fraction);
        resamplePosition += step;
    }

    const consumed = Math.floor(resamplePosition);
    resampleCarry = input.slice(consumed);
    resamplePosition -= consumed;
}

function readFrameIntoHeap() {
    const start = pcmPointer >> 2;
    for (let index = 0; index < FRAME_SAMPLES; index += 1) {
        opus.HEAPF32[start + index] = ring[ringRead];
        ringRead = (ringRead + 1) % ring.length;
    }
    ringCount -= FRAME_SAMPLES;
}

function decodePacket(decoderHandle, packet, streamId, decodeFec) {
    const packetBytes = packet ? packet.length : 0;
    if (packetBytes > 0) opus.HEAPU8.set(packet, packetPointer);
    const decoded = opus._opus_decode_float(
        decoderHandle,
        packetBytes > 0 ? packetPointer : 0,
        packetBytes,
        pcmPointer,
        FRAME_SAMPLES,
        decodeFec ? 1 : 0);

    if (decoded < 0) throw new Error("opus_decode_failed_" + decoded);
    if (decoded !== FRAME_SAMPLES) {
        throw new Error("opus_decoded_sample_count_invalid_" + decoded);
    }

    const start = pcmPointer >> 2;
    const pcm = new Float32Array(decoded);
    pcm.set(opus.HEAPF32.subarray(start, start + decoded));
    decodedFrames += 1;

    if (audioPort) {
        audioPort.postMessage(
            {
                type: "playback",
                streamId: String(streamId || "loopback"),
                pcm: pcm.buffer
            },
            [pcm.buffer]);
    }
}

function decodeLoopback(packetBytes) {
    const packet = new Uint8Array(packetBytes);
    packet.set(opus.HEAPU8.subarray(packetPointer, packetPointer + packetBytes));
    decodePacket(decoder, packet, "loopback", false);
}

function getRemoteDecoder(streamId) {
    const key = String(streamId || "").trim().toLowerCase();
    if (!key) throw new Error("remote_stream_id_missing");

    const existing = remoteDecoders.get(key);
    if (existing) return existing;
    if (remoteDecoders.size >= 128) {
        throw new Error("remote_decoder_capacity_exceeded");
    }

    const handle = opus._opus_decoder_create(
        TARGET_SAMPLE_RATE,
        CHANNELS,
        errorPointer);
    assertOpus(
        opus.HEAP32[errorPointer >> 2],
        "remote_opus_decoder_create");
    if (!handle) throw new Error("remote_opus_decoder_create_null");

    const state = {
        handle,
        frames: new Map(),
        initialized: false,
        expectedSequence: 0,
        highestSequence: 0,
        nextPlayoutAtMs: 0,
        lastArrivalAtMs: performance.now(),
        lastTransitMs: null,
        jitterMs: 0,
        targetDelayMs: MIN_PLAYOUT_DELAY_MS,
        playbackId: key
    };
    remoteDecoders.set(key, state);
    return state;
}

function clampPlayoutDelay(value) {
    const bounded = Math.max(
        MIN_PLAYOUT_DELAY_MS,
        Math.min(MAX_PLAYOUT_DELAY_MS, value));
    return Math.ceil(bounded / PLAYOUT_FRAME_MS) * PLAYOUT_FRAME_MS;
}

function decodeRemotePacket(message) {
    if (!initialized || !(message.packet instanceof ArrayBuffer)) return;

    const packet = new Uint8Array(message.packet);
    if (packet.length === 0 || packet.length > MAX_PACKET_BYTES) {
        throw new Error("remote_opus_packet_size_invalid_" + packet.length);
    }

    const streamId = String(message.streamId || "").trim().toLowerCase();
    const senderId = String(message.senderId || "").trim().toLowerCase();
    const mediaSequence = Number(message.mediaSequence) >>> 0;
    const mediaTimestamp100Ns = Number(message.mediaTimestamp100Ns);
    if (mediaSequence === 0 || !Number.isFinite(mediaTimestamp100Ns)) {
        throw new Error("remote_media_timing_invalid");
    }
    const state = getRemoteDecoder(streamId);
    if (senderId) state.playbackId = senderId;
    const now = performance.now();
    state.lastArrivalAtMs = now;

    if (state.initialized && mediaSequence < state.expectedSequence) {
        remoteLateDrops += 1;
        return;
    }
    if (state.frames.has(mediaSequence)) {
        remoteDuplicateDrops += 1;
        return;
    }
    if (state.frames.size >= MAX_REMOTE_FRAMES) {
        remoteOverflowDrops += 1;
        return;
    }
    if (state.highestSequence !== 0 && mediaSequence < state.highestSequence) {
        remoteReorderedPackets += 1;
    }

    const transitMs = now - mediaTimestamp100Ns / 10000;
    if (state.lastTransitMs !== null) {
        const deviation = Math.abs(transitMs - state.lastTransitMs);
        state.jitterMs += (deviation - state.jitterMs) / 16;
        state.targetDelayMs = clampPlayoutDelay(
            MIN_PLAYOUT_DELAY_MS + state.jitterMs * 4);
    }
    state.lastTransitMs = transitMs;
    state.highestSequence = Math.max(state.highestSequence, mediaSequence);
    state.frames.set(mediaSequence, packet);

    if (!state.initialized) {
        state.initialized = true;
        state.expectedSequence = mediaSequence;
        state.nextPlayoutAtMs = now + state.targetDelayMs;
    }
}

function playoutRemoteState(streamId, state, now) {
    if (state.frames.size === 0 &&
        now - state.lastArrivalAtMs > state.targetDelayMs + PLAYOUT_FRAME_MS) {
        state.initialized = false;
        return;
    }

    let safety = 0;
    while (state.initialized && now >= state.nextPlayoutAtMs && safety < 4) {
        const sequence = state.expectedSequence;
        const packet = state.frames.get(sequence);

        if (packet) {
            state.frames.delete(sequence);
            decodePacket(
                state.handle,
                packet,
                state.playbackId || streamId,
                false);
            remoteNormalFrames += 1;
        } else {
            remoteMissingFrames += 1;
            const nextPacket = state.frames.get(sequence + 1);
            if (nextPacket) {
                decodePacket(
                    state.handle,
                    nextPacket,
                    state.playbackId || streamId,
                    true);
                remoteFecFrames += 1;
            } else {
                decodePacket(
                    state.handle,
                    null,
                    state.playbackId || streamId,
                    false);
                remotePlcFrames += 1;
            }
        }

        state.expectedSequence = sequence === 0xffffffff ? 1 : sequence + 1;
        state.nextPlayoutAtMs += PLAYOUT_FRAME_MS;
        safety += 1;
    }
}

function pumpRemoteStreams() {
    if (!initialized) return;
    const now = performance.now();
    for (const [streamId, state] of remoteDecoders) {
        if (now - state.lastArrivalAtMs >= REMOTE_IDLE_TIMEOUT_MS &&
            state.frames.size === 0) {
            opus._opus_decoder_destroy(state.handle);
            remoteDecoders.delete(streamId);
            continue;
        }
        playoutRemoteState(streamId, state, now);
    }
    emitStats(false);
}

function getRemoteMetric(selector) {
    let value = 0;
    for (const state of remoteDecoders.values()) {
        value = Math.max(value, selector(state));
    }
    return Math.round(value);
}

function getBufferedRemoteFrames() {
    let count = 0;
    for (const state of remoteDecoders.values()) count += state.frames.size;
    return count;
}

function encodeAvailableFrames() {
    while (ringCount >= FRAME_SAMPLES) {
        readFrameIntoHeap();

        const encodeStartedAtMs = performance.now();
        const packetBytes = opus._opus_encode_float(
            encoder,
            pcmPointer,
            FRAME_SAMPLES,
            packetPointer,
            MAX_PACKET_BYTES);
        const encodeCompletedAtMs = performance.now();
        const encodeDurationMs = Math.max(
            0,
            encodeCompletedAtMs - encodeStartedAtMs);
        encodeDurationTotalMs += encodeDurationMs;
        encodeDurationMaxMs = Math.max(
            encodeDurationMaxMs,
            encodeDurationMs);
        encodeMeasurements += 1;

        if (lastEncodeCompletedAtMs > 0) {
            cadenceMaxDeviationMs = Math.max(
                cadenceMaxDeviationMs,
                Math.abs(
                    (encodeCompletedAtMs - lastEncodeCompletedAtMs) - 20));
        }
        lastEncodeCompletedAtMs = encodeCompletedAtMs;

        if (packetBytes < 0) {
            throw new Error("opus_encode_failed_" + packetBytes);
        }
        if (packetBytes === 0 || packetBytes > MAX_PACKET_BYTES) {
            throw new Error("opus_packet_size_invalid_" + packetBytes);
        }

        encodedFrames += 1;
        encodedBytes += packetBytes;
        lastPacketBytes = packetBytes;

        if (productionMode) {
            const packet = new Uint8Array(packetBytes);
            packet.set(
                opus.HEAPU8.subarray(
                    packetPointer,
                    packetPointer + packetBytes));

            const mediaSequence = nextMediaSequence >>> 0;
            nextMediaSequence = mediaSequence === 0xffffffff
                ? 1
                : mediaSequence + 1;

            self.postMessage({
                type: "encoded_packet",
                packet: packet.buffer,
                mediaSequence,
                mediaTimestamp100Ns: String(
                    Math.max(0, mediaSequence - 1) * 200000)
            }, [packet.buffer]);
        } else {
            decodeLoopback(packetBytes);
        }
    }
}

function emitStats(force) {
    const now = Date.now();
    if (!force && now - lastStatsAt < 1000) return;
    lastStatsAt = now;

    self.postMessage({
        type: "stats",
        captureBlocks,
        encodedFrames,
        decodedFrames,
        encodedBytes,
        playbackUnderflows,
        captureDrops,
        lastPacketBytes,
        bitrate,
        fec,
        packetLossPercent,
        encodeAverageMicros: encodeMeasurements > 0
            ? Math.round(
                encodeDurationTotalMs * 1000 / encodeMeasurements)
            : 0,
        encodeMaxMicros: Math.round(encodeDurationMaxMs * 1000),
        cadenceMaxDeviationMicros: Math.round(
            cadenceMaxDeviationMs * 1000),
        playbackDrops,
        remoteStreams: remoteDecoders.size,
        remoteBufferedFrames: getBufferedRemoteFrames(),
        remoteTargetDelayMs: getRemoteMetric(
            (state) => state.targetDelayMs),
        remoteJitterMicros: getRemoteMetric(
            (state) => state.jitterMs * 1000),
        remoteLateDrops,
        remoteDuplicateDrops,
        remoteReorderedPackets,
        remoteMissingFrames,
        remoteOverflowDrops,
        remoteNormalFrames,
        remoteFecFrames,
        remotePlcFrames
    });
}

function resetCaptureWindow() {
    ringRead = 0;
    ringWrite = 0;
    ringCount = 0;
    resampleCarry = new Float32Array(0);
    resamplePosition = 0;
    encodeDurationTotalMs = 0;
    encodeDurationMaxMs = 0;
    encodeMeasurements = 0;
    lastEncodeCompletedAtMs = 0;
    cadenceMaxDeviationMs = 0;
}

function setCaptureEnabled(enabled) {
    const nextEnabled = enabled === true;
    if (captureEnabled === nextEnabled) return;
    resetCaptureWindow();
    captureEnabled = nextEnabled;
}

function handleAudioMessage(message) {
    if (message.type === "audio_stats") {
        playbackUnderflows = Number(message.playbackUnderflows) || 0;
        playbackDrops = Number(message.playbackDrops) || 0;
        emitStats(false);
        return;
    }

    if (message.type !== "capture" ||
        !captureEnabled ||
        !(message.pcm instanceof ArrayBuffer)) {
        return;
    }

    captureBlocks += 1;
    appendResampled(new Float32Array(message.pcm));
    encodeAvailableFrames();
    emitStats(false);
}

async function initialize(message) {
    if (initialized) throw new Error("opus_worker_already_initialized");

    sourceSampleRate = Math.max(
        8000,
        Number(message.sourceSampleRate) || TARGET_SAMPLE_RATE);

    importScripts(message.moduleUrl);
    if (typeof createVoiceOpusModule !== "function") {
        throw new Error("opus_module_factory_missing");
    }

    opus = await createVoiceOpusModule({
        locateFile: (path) => path.endsWith(".wasm")
            ? message.wasmUrl
            : path
    });

    const version = opus.UTF8ToString(opus._opus_get_version_string());
    if (version !== "libopus 1.6.1") {
        throw new Error("unexpected_opus_version_" + version);
    }

    errorPointer = opus._malloc(4);
    encoder = opus._opus_encoder_create(
        TARGET_SAMPLE_RATE,
        CHANNELS,
        OPUS_APPLICATION_VOIP,
        errorPointer);
    assertOpus(opus.HEAP32[errorPointer >> 2], "opus_encoder_create");
    if (!encoder) throw new Error("opus_encoder_create_null");

    decoder = opus._opus_decoder_create(
        TARGET_SAMPLE_RATE,
        CHANNELS,
        errorPointer);
    assertOpus(opus.HEAP32[errorPointer >> 2], "opus_decoder_create");
    if (!decoder) throw new Error("opus_decoder_create_null");

    pcmPointer = opus._malloc(FRAME_SAMPLES * 4);
    packetPointer = opus._malloc(MAX_PACKET_BYTES);
    if (!pcmPointer || !packetPointer) {
        throw new Error("opus_buffer_allocation_failed");
    }

    configureEncoder(
        message.bitrate,
        message.fec === true,
        message.packetLossPercent);

    initialized = true;
    if (typeof setInterval === "function") {
        playoutTimer = setInterval(pumpRemoteStreams, 5);
    }
    self.postMessage({
        type: "ready",
        version,
        sourceSampleRate,
        targetSampleRate: TARGET_SAMPLE_RATE
    });
}

function dispose() {
    if (playoutTimer) {
        if (typeof clearInterval === "function") clearInterval(playoutTimer);
        playoutTimer = 0;
    }
    if (audioPort) {
        try { audioPort.close(); } catch (_) {}
        audioPort = null;
    }

    if (opus) {
        for (const state of remoteDecoders.values()) {
            if (state && state.handle) {
                opus._opus_decoder_destroy(state.handle);
            }
        }
        remoteDecoders.clear();
        if (encoder) opus._opus_encoder_destroy(encoder);
        if (decoder) opus._opus_decoder_destroy(decoder);
        if (pcmPointer) opus._free(pcmPointer);
        if (packetPointer) opus._free(packetPointer);
        if (errorPointer) opus._free(errorPointer);
    }

    opus = null;
    encoder = 0;
    decoder = 0;
    pcmPointer = 0;
    packetPointer = 0;
    errorPointer = 0;
    captureEnabled = false;
    initialized = false;
    resetCaptureWindow();
    productionMode = false;
    nextMediaSequence = 1;
}

self.onmessage = async (event) => {
    const message = event.data || {};

    try {
        if (message.type === "initialize") {
            await initialize(message);
            return;
        }

        if (message.type === "attach" && message.port) {
            if (audioPort) audioPort.close();
            audioPort = message.port;
            audioPort.onmessage = (portEvent) => {
                try {
                    handleAudioMessage(portEvent.data || {});
                } catch (error) {
                    fail(error && error.message ? error.message : error);
                }
            };
            audioPort.start();
            return;
        }

        if (message.type === "capture_enabled") {
            setCaptureEnabled(message.enabled);
            return;
        }

        if (message.type === "configure") {
            configureEncoder(
                message.bitrate,
                message.fec === true,
                message.packetLossPercent);
            self.postMessage({
                type: "configured",
                bitrate,
                fec,
                packetLossPercent
            });
            emitStats(true);
            return;
        }

        if (message.type === "production_mode") {
            productionMode = message.enabled === true;
            return;
        }

        if (message.type === "remote_packet") {
            decodeRemotePacket(message);
            emitStats(false);
            return;
        }

        if (message.type === "dispose") {
            dispose();
            self.close();
        }
    } catch (error) {
        if (message.type === "initialize") dispose();
        fail(error && error.message ? error.message : error);
    }
};
