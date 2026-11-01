class VoiceBrowserAudioProcessor extends AudioWorkletProcessor {
    constructor() {
        super();
        this.workerPort = null;
        this.captureEnabled = false;
        this.playbackStreams = new Map();
        this.spatialTargets = new Map();
        this.playbackUnderflows = 0;
        this.playbackDrops = 0;
        this.renderBlocks = 0;

        this.port.onmessage = (event) => {
            const message = event.data || {};

            if (message.type === "attach" && message.port) {
                this.workerPort = message.port;
                this.workerPort.onmessage = (workerEvent) => {
                    this.handleWorkerMessage(workerEvent.data || {});
                };
                this.workerPort.start();
                return;
            }

            if (message.type === "capture_enabled") {
                this.captureEnabled = message.enabled === true;
                return;
            }

            if (message.type === "spatial_state") {
                this.updateSpatialTarget(message);
                return;
            }

            if (message.type === "spatial_remove") {
                const streamId = this.normalizeStreamId(message.streamId);
                if (streamId) this.spatialTargets.delete(streamId);
            }
        };
    }

    normalizeStreamId(value) {
        return String(value || "").trim().toLowerCase();
    }

    updateSpatialTarget(message) {
        const streamId = this.normalizeStreamId(message.streamId);
        if (!streamId) return;

        const target = {
            pan: Math.max(-1, Math.min(1, Number(message.pan) || 0)),
            gain: Math.max(0, Math.min(1, Number(message.gain) || 0)),
            rearAmount: Math.max(
                0,
                Math.min(1, Number(message.rearAmount) || 0))
        };

        this.spatialTargets.set(streamId, target);

        const state = this.playbackStreams.get(streamId);
        if (state) {
            state.targetPan = target.pan;
            state.targetGain = target.gain;
            state.targetRearAmount = target.rearAmount;
        }
    }

    createPlaybackState(streamId) {
        const target = this.spatialTargets.get(streamId) || {
            pan: 0,
            gain: 1,
            rearAmount: 0
        };

        return {
            queue: [],
            offset: 0,
            lastSeenBlock: this.renderBlocks,
            pan: target.pan,
            gain: target.gain,
            rearAmount: target.rearAmount,
            targetPan: target.pan,
            targetGain: target.gain,
            targetRearAmount: target.rearAmount,
            filteredSample: 0
        };
    }

    handleWorkerMessage(message) {
        if (message.type !== "playback" ||
            !(message.pcm instanceof ArrayBuffer)) {
            return;
        }

        const streamId = this.normalizeStreamId(
            message.streamId || "loopback");
        let state = this.playbackStreams.get(streamId);
        if (!state) {
            state = this.createPlaybackState(streamId);
            this.playbackStreams.set(streamId, state);
        }

        if (state.queue.length >= 50) {
            state.queue.shift();
            state.offset = 0;
            this.playbackDrops += 1;
        }

        state.queue.push(new Float32Array(message.pcm));
        state.lastSeenBlock = this.renderBlocks;
    }

    smoothSpatialState(state) {
        const smoothing = 0.30;
        state.pan += (state.targetPan - state.pan) * smoothing;
        state.gain += (state.targetGain - state.gain) * smoothing;
        state.rearAmount +=
            (state.targetRearAmount - state.rearAmount) * smoothing;
    }

    writePlayback(outputs) {
        if (!outputs[0] || outputs[0].length === 0) return;

        const channels = outputs[0];
        const frameCount = channels[0].length;
        for (let channel = 0; channel < channels.length; channel += 1) {
            channels[channel].fill(0);
        }

        let activeStreams = 0;

        for (const [streamId, state] of this.playbackStreams) {
            let written = 0;
            if (state.queue.length > 0) activeStreams += 1;

            this.smoothSpatialState(state);

            const pan = Math.max(-1, Math.min(1, state.pan));
            const panAngle = (pan + 1) * Math.PI * 0.25;
            const leftPanGain = Math.cos(panAngle);
            const rightPanGain = Math.sin(panAngle);
            const distanceGain = Math.max(0, Math.min(1, state.gain));
            const rearAmount = Math.max(
                0,
                Math.min(1, state.rearAmount));

            const nyquistSafeFront = Math.min(22000, sampleRate * 0.45);
            const cutoffHz =
                nyquistSafeFront +
                (6000 - nyquistSafeFront) * rearAmount;
            const lowPassAlpha = 1 - Math.exp(
                -2 * Math.PI * cutoffHz / sampleRate);

            while (written < frameCount && state.queue.length > 0) {
                const current = state.queue[0];
                const available = current.length - state.offset;
                const count = Math.min(frameCount - written, available);

                for (let index = 0; index < count; index += 1) {
                    const inputSample = current[state.offset + index];
                    state.filteredSample +=
                        lowPassAlpha *
                        (inputSample - state.filteredSample);

                    const sample = state.filteredSample * distanceGain;
                    const outputIndex = written + index;

                    if (channels.length === 1) {
                        channels[0][outputIndex] += sample;
                    } else {
                        channels[0][outputIndex] += sample * leftPanGain;
                        channels[1][outputIndex] += sample * rightPanGain;

                        for (let channel = 2; channel < channels.length; channel += 1) {
                            channels[channel][outputIndex] += sample * 0.5;
                        }
                    }
                }

                written += count;
                state.offset += count;

                if (state.offset >= current.length) {
                    state.queue.shift();
                    state.offset = 0;
                }
            }

            if (state.queue.length === 0 &&
                this.renderBlocks - state.lastSeenBlock > 750) {
                this.playbackStreams.delete(streamId);
            }
        }

        for (let channel = 0; channel < channels.length; channel += 1) {
            const output = channels[channel];
            for (let index = 0; index < output.length; index += 1) {
                output[index] = Math.max(-1, Math.min(1, output[index]));
            }
        }

        if (activeStreams === 0) this.playbackUnderflows += 1;
    }

    process(inputs, outputs) {
        this.writePlayback(outputs);

        const input = inputs[0];
        if (this.captureEnabled &&
            this.workerPort &&
            input &&
            input.length > 0 &&
            input[0].length > 0) {
            const mono = new Float32Array(input[0].length);
            mono.set(input[0]);
            this.workerPort.postMessage(
                { type: "capture", pcm: mono.buffer },
                [mono.buffer]);
        }

        this.renderBlocks += 1;
        if (this.workerPort && this.renderBlocks % 375 === 0) {
            this.workerPort.postMessage({
                type: "audio_stats",
                playbackUnderflows: this.playbackUnderflows,
                playbackDrops: this.playbackDrops
            });
        }

        return true;
    }
}

registerProcessor(
    "voice-browser-audio-processor",
    VoiceBrowserAudioProcessor);
