mergeInto(LibraryManager.library, {
    VoiceBrowserMediaConnect: function (
        handle,
        objectNamePtr,
        endpointPtr,
        accessTokenPtr,
        roomIdPtr,
        controlConnectionIdPtr,
        streamIdPtr,
        clientBuildPtr) {
        // W4: WebGL voice media is WebRTC/SFU only (no legacy media fallback).
        // Control/signaling WebSockets are intentionally left untouched.
        if (typeof window !== "undefined") {
            var pipelinesRtc = Module.voiceBrowserAudioPipelines || {};
            var pipelineRtc = pipelinesRtc[handle];
            if (!pipelineRtc || pipelineRtc.closed || !pipelineRtc.stream) return 0;
            Module.voiceWebRtcSessions = Module.voiceWebRtcSessions || {};
            if (Module.voiceWebRtcSessions[handle]) return 0;
            var stateRtc = { closed: false };
            Module.voiceWebRtcSessions[handle] = stateRtc;
            var objectRtc = UTF8ToString(objectNamePtr);
            var controlRtc = UTF8ToString(controlConnectionIdPtr).trim().toLowerCase();
            var roomRtc = UTF8ToString(roomIdPtr);
            var endpointRtc = UTF8ToString(endpointPtr);
            var accessTokenRtc = UTF8ToString(accessTokenPtr);
            var reportRtc = function(name, fields) {
                if (stateRtc.closed) return;
                var message = String(handle) + "|" + name +
                    (fields && fields.length ? "|" + fields.join("|") : "");
                window.setTimeout(function() {
                    if (stateRtc.closed) return;
                    try { SendMessage(objectRtc, "HandleVoiceBrowserMediaEvent", message); }
                    catch (_) {}
                    // WebRTC can disconnect itself (signal closed, ICE failure).
                    // The Unity session then marks itself disconnected, so its
                    // Dispose() need not call VoiceBrowserMediaDisconnect().
                    // Release only THIS generation, otherwise every subsequent
                    // connect is rejected by voiceWebRtcSessions[handle].
                    if (name === "disconnected") {
                        if (Module.voiceWebRtcSessions &&
                            Module.voiceWebRtcSessions[handle] === stateRtc) {
                            delete Module.voiceWebRtcSessions[handle];
                        }
                        stateRtc.closed = true;
                    }
                }, 0);
            };
            var startRtc = function() {
                if (stateRtc.closed) return;
                try {
                    window.VME2WebRtcVoice.connect({
                        handle: handle, pipeline: pipelineRtc,
                        roomId: roomRtc, controlConnectionId: controlRtc,
                        endpoint: endpointRtc, accessToken: accessTokenRtc,
                        onEvent: reportRtc
                    });
                    // Replay speaker intent if it changed before the bundle loaded.
                    window.VME2WebRtcVoice.setPlaybackEnabled(
                        handle, pipelineRtc.playbackEnabled !== false);
                } catch (error) {
                    reportRtc("failure", [String(error && error.message || error)]);
                    reportRtc("disconnected", ["webrtc_init_error", "0"]);
                    delete Module.voiceWebRtcSessions[handle];
                }
            };
            if (window.VME2WebRtcVoice) {
                startRtc();
            } else {
                var assets = String(pipelineRtc.assetRoot || "");
                var scriptUrl = assets.replace(/\/VoiceBrowserAudio\/?$/, "/VME2WebRTC") +
                    "/VME2WebRtcVoice.bundle.js";
                if (!Module.voiceWebRtcScriptPromise) {
                    Module.voiceWebRtcScriptPromise = new Promise(function(resolve, reject) {
                        var script = document.createElement("script");
                        script.src = scriptUrl;
                        script.onload = resolve;
                        script.onerror = function() { reject(new Error("webrtc_bundle_missing")); };
                        document.head.appendChild(script);
                    });
                }
                Module.voiceWebRtcScriptPromise.then(startRtc).catch(function(e) {
                    reportRtc("failure", [String(e && e.message || e)]);
                    reportRtc("disconnected", ["webrtc_bundle_load_failed", "0"]);
                    delete Module.voiceWebRtcSessions[handle];
                });
            }
            return 1;
        }
        try {
            var pipelines = Module.voiceBrowserAudioPipelines || {};
            var pipeline = pipelines[handle];
            if (!pipeline || pipeline.closed || !pipeline.worker) return 0;

            if (!Module.voiceBrowserMediaSessions) {
                Module.voiceBrowserMediaSessions = {};
            }
            if (Module.voiceBrowserMediaSessions[handle]) return 0;
            if (!Module.voiceBrowserMediaGenerations) {
                Module.voiceBrowserMediaGenerations = {};
            }
            var generation =
                (Number(Module.voiceBrowserMediaGenerations[handle]) || 0) + 1;
            Module.voiceBrowserMediaGenerations[handle] = generation;

            var objectName = UTF8ToString(objectNamePtr);
            var endpoint = UTF8ToString(endpointPtr);
            var accessToken = UTF8ToString(accessTokenPtr);
            var roomId = UTF8ToString(roomIdPtr);
            var controlConnectionId = UTF8ToString(controlConnectionIdPtr)
                .trim().toLowerCase();
            var streamId = UTF8ToString(streamIdPtr).trim().toLowerCase();
            var clientBuild = UTF8ToString(clientBuildPtr).trim();
            var emptyUuid = "00000000-0000-0000-0000-000000000000";
            var textEncoder = new TextEncoder();
            var textDecoder = new TextDecoder("utf-8");
            var maximumPacketBytes = 1200;
            var maximumPayloadBytes = 1112;
            var maximumBufferedBytes = maximumPacketBytes * 8;

            var queueUnityEvent = function (eventName, values) {
                var payload = String(handle) + "|" + String(eventName || "unknown");
                if (values && values.length) payload += "|" + values.join("|");
                window.setTimeout(function () {
                    if (!Module.voiceBrowserMediaGenerations ||
                        Module.voiceBrowserMediaGenerations[handle] !== generation) {
                        return;
                    }
                    try {
                        SendMessage(
                            objectName,
                            "HandleVoiceBrowserMediaEvent",
                            payload);
                    } catch (_) {
                    }
                }, 0);
            };

            var safeReason = function (value) {
                return String(value || "browser_media_unknown_failure")
                    .replace(/\|/g, "/")
                    .replace(/[\r\n]+/g, " ")
                    .slice(0, 240);
            };

            var uuidToBytes = function (value) {
                var normalized = String(value || "").trim().toLowerCase();
                var hex = normalized.replace(/-/g, "");
                if (!/^[0-9a-f]{32}$/.test(hex)) {
                    throw new Error("browser_media_uuid_invalid");
                }
                var result = new Uint8Array(16);
                for (var index = 0; index < 16; index += 1) {
                    result[index] = parseInt(hex.substr(index * 2, 2), 16);
                }
                return result;
            };

            var bytesToUuid = function (bytes, offset) {
                var hex = "";
                for (var index = 0; index < 16; index += 1) {
                    hex += bytes[offset + index].toString(16).padStart(2, "0");
                }
                return hex.slice(0, 8) + "-" +
                    hex.slice(8, 12) + "-" +
                    hex.slice(12, 16) + "-" +
                    hex.slice(16, 20) + "-" +
                    hex.slice(20);
            };

            var writeUInt64 = function (view, offset, value) {
                var safe = Math.max(0, Number(value) || 0);
                var high = Math.floor(safe / 4294967296);
                var low = safe - high * 4294967296;
                view.setUint32(offset, high >>> 0, false);
                view.setUint32(offset + 4, low >>> 0, false);
            };

            var readUInt64 = function (view, offset) {
                return view.getUint32(offset, false) * 4294967296 +
                    view.getUint32(offset + 4, false);
            };

            var session = {
                handle: handle,
                pipeline: pipeline,
                socket: null,
                objectName: objectName,
                controlConnectionId: controlConnectionId,
                streamId: streamId,
                nextTransportSequence: 1,
                lastReceivedTransportSequence: 0,
                receivedAckMask: 0,
                hasReceivedTransportSequence: false,
                bound: false,
                outboundEnabled: false,
                intentionalClose: false,
                outboundFrames: 0,
                inboundFrames: 0,
                droppedOutboundFrames: 0,
                backpressureDrops: 0,
                maxBufferedBytes: 0,
                pongCount: 0,
                lastOutboundPacketBytes: 0,
                lastInboundPacketBytes: 0,
                statsTimer: null,
                pingTimer: null,
                heartbeatTimer: null,
                bindTimer: null,
                workerHandler: null,
                lastInboundAt: Date.now(),
                cleaned: false
            };

            var allocateTransportSequence = function () {
                var value = session.nextTransportSequence >>> 0;
                session.nextTransportSequence = value === 0xffffffff
                    ? 1
                    : value + 1;
                return value;
            };

            var observeTransportSequence = function (sequence) {
                var current = sequence >>> 0;
                if (!session.hasReceivedTransportSequence) {
                    session.hasReceivedTransportSequence = true;
                    session.lastReceivedTransportSequence = current;
                    session.receivedAckMask = 0;
                    return;
                }

                var last = session.lastReceivedTransportSequence >>> 0;
                if (current > last) {
                    var delta = current - last;
                    session.receivedAckMask = delta >= 32
                        ? 0
                        : ((session.receivedAckMask << delta) |
                            (1 << (delta - 1))) >>> 0;
                    session.lastReceivedTransportSequence = current;
                    return;
                }

                var distance = last - current;
                if (distance > 0 && distance <= 32) {
                    session.receivedAckMask = (
                        session.receivedAckMask |
                        (1 << (distance - 1))) >>> 0;
                }
            };

            var encodePacket = function (
                kind,
                codec,
                flags,
                mediaSequence,
                mediaTimestamp100Ns,
                packetStreamId,
                payload) {
                var body = payload instanceof Uint8Array
                    ? payload
                    : new Uint8Array(payload || 0);
                if (body.length > maximumPayloadBytes) {
                    throw new Error("browser_media_payload_exceeds_mtu");
                }

                var bytes = new Uint8Array(88 + body.length);
                var view = new DataView(bytes.buffer);
                bytes[0] = 77;
                bytes[1] = 86;
                bytes[2] = 77;
                bytes[3] = 84;
                bytes[4] = 2;
                bytes[5] = kind;
                bytes[6] = codec;
                bytes[7] = flags;
                view.setUint16(8, 88, false);
                view.setUint16(10, body.length, false);
                view.setUint32(12, allocateTransportSequence(), false);
                view.setUint32(16, mediaSequence >>> 0, false);
                writeUInt64(view, 20, mediaTimestamp100Ns);
                view.setUint32(
                    28,
                    session.hasReceivedTransportSequence
                        ? session.lastReceivedTransportSequence >>> 0
                        : 0,
                    false);
                view.setUint32(32, session.receivedAckMask >>> 0, false);
                bytes.set(uuidToBytes(emptyUuid), 36);
                bytes.set(uuidToBytes(packetStreamId || session.streamId), 52);
                bytes.set(uuidToBytes(emptyUuid), 68);
                view.setUint32(84, 0, false);
                bytes.set(body, 88);
                return bytes;
            };

            var sendPacket = function (
                kind,
                codec,
                flags,
                mediaSequence,
                mediaTimestamp100Ns,
                packetStreamId,
                payload) {
                if (!session.socket ||
                    session.socket.readyState !== WebSocket.OPEN) {
                    return false;
                }

                var bufferedBytes = Number(session.socket.bufferedAmount) || 0;
                session.maxBufferedBytes = Math.max(
                    session.maxBufferedBytes,
                    bufferedBytes);
                if (kind === 3 && bufferedBytes > maximumBufferedBytes) {
                    session.backpressureDrops += 1;
                    return false;
                }

                var packet = encodePacket(
                    kind,
                    codec,
                    flags,
                    mediaSequence,
                    mediaTimestamp100Ns,
                    packetStreamId,
                    payload);
                session.socket.send(packet.buffer);
                return true;
            };

            var encodeBindPayload = function () {
                var token = textEncoder.encode(accessToken);
                var room = textEncoder.encode(roomId);
                var build = textEncoder.encode(clientBuild);
                if (!token.length || token.length > 16384 ||
                    !room.length || room.length > 512 ||
                    build.length > 128) {
                    throw new Error("browser_media_bind_field_length_invalid");
                }

                var bytes = new Uint8Array(
                    26 + token.length + room.length + build.length);
                var view = new DataView(bytes.buffer);
                bytes[0] = 1;
                bytes[1] = 1;
                view.setUint16(2, token.length, false);
                view.setUint16(4, room.length, false);
                view.setUint16(6, build.length, false);
                view.setUint16(8, 0, false);
                bytes.set(uuidToBytes(controlConnectionId), 10);
                var cursor = 26;
                bytes.set(token, cursor);
                cursor += token.length;
                bytes.set(room, cursor);
                cursor += room.length;
                bytes.set(build, cursor);
                return bytes;
            };

            var decodePacket = function (input) {
                var bytes = input instanceof Uint8Array
                    ? input
                    : new Uint8Array(input);
                if (bytes.length < 88 || bytes.length > maximumPacketBytes) {
                    throw new Error("browser_media_packet_length_invalid");
                }
                if (bytes[0] !== 77 || bytes[1] !== 86 ||
                    bytes[2] !== 77 || bytes[3] !== 84 || bytes[4] !== 2) {
                    throw new Error("browser_media_packet_header_invalid");
                }

                var view = new DataView(
                    bytes.buffer,
                    bytes.byteOffset,
                    bytes.byteLength);
                var headerBytes = view.getUint16(8, false);
                var payloadBytes = view.getUint16(10, false);
                if (headerBytes !== 88 ||
                    payloadBytes > maximumPayloadBytes ||
                    bytes.length !== headerBytes + payloadBytes) {
                    throw new Error("browser_media_packet_size_invalid");
                }

                return {
                    bytes: bytes,
                    kind: bytes[5],
                    codec: bytes[6],
                    flags: bytes[7],
                    transportSequence: view.getUint32(12, false),
                    mediaSequence: view.getUint32(16, false),
                    mediaTimestamp100Ns: readUInt64(view, 20),
                    streamId: bytesToUuid(bytes, 52),
                    senderId: bytesToUuid(bytes, 68),
                    securityContextId: view.getUint32(84, false),
                    payload: bytes.slice(88)
                };
            };

            var emitStats = function () {
                queueUnityEvent("stats", [
                    session.bound ? 1 : 0,
                    session.outboundFrames,
                    session.inboundFrames,
                    session.droppedOutboundFrames,
                    session.pongCount,
                    session.lastOutboundPacketBytes,
                    session.lastInboundPacketBytes,
                    session.backpressureDrops,
                    session.maxBufferedBytes
                ]);
            };

            var cleanup = function () {
                if (session.cleaned) return;
                session.cleaned = true;
                if (session.statsTimer) {
                    window.clearInterval(session.statsTimer);
                    session.statsTimer = null;
                }
                if (session.pingTimer) {
                    window.clearInterval(session.pingTimer);
                    session.pingTimer = null;
                }
                if (session.heartbeatTimer) {
                    window.clearInterval(session.heartbeatTimer);
                    session.heartbeatTimer = null;
                }
                if (session.bindTimer) {
                    window.clearTimeout(session.bindTimer);
                    session.bindTimer = null;
                }
                if (session.workerHandler && pipeline.worker) {
                    pipeline.worker.removeEventListener(
                        "message",
                        session.workerHandler);
                }
                session.workerHandler = null;
                session.bound = false;
                session.outboundEnabled = false;
                if (pipeline.worker) {
                    pipeline.worker.postMessage({
                        type: "production_mode",
                        enabled: false
                    });
                }
                if (Module.voiceBrowserMediaSessions &&
                    Module.voiceBrowserMediaSessions[handle] === session) {
                    delete Module.voiceBrowserMediaSessions[handle];
                }
            };

            session.cleanup = cleanup;
            session.workerHandler = function (event) {
                var message = event.data || {};
                if (message.type !== "encoded_packet" ||
                    !(message.packet instanceof ArrayBuffer)) {
                    return;
                }

                var payload = new Uint8Array(message.packet);
                if (!session.bound || !session.outboundEnabled) {
                    session.droppedOutboundFrames += 1;
                    return;
                }

                try {
                    var sent = sendPacket(
                        3,
                        1,
                        0,
                        Number(message.mediaSequence) >>> 0,
                        Number(message.mediaTimestamp100Ns) || 0,
                        session.streamId,
                        payload);
                    if (!sent) {
                        session.droppedOutboundFrames += 1;
                        return;
                    }
                    session.outboundFrames += 1;
                    session.lastOutboundPacketBytes = payload.length;
                } catch (error) {
                    session.droppedOutboundFrames += 1;
                    queueUnityEvent("failure", [safeReason(
                        error && error.message ? error.message : error)]);
                }
            };
            pipeline.worker.addEventListener("message", session.workerHandler);

            var processIncoming = function (buffer) {
                var packet = decodePacket(buffer);
                session.lastInboundAt = Date.now();
                observeTransportSequence(packet.transportSequence);

                if (packet.securityContextId !== 0) {
                    throw new Error("browser_media_unexpected_security_context");
                }

                if (packet.kind === 2) {
                    if (packet.payload.length < 22) {
                        throw new Error("browser_media_bind_result_invalid");
                    }
                    var bindView = new DataView(
                        packet.payload.buffer,
                        packet.payload.byteOffset,
                        packet.payload.byteLength);
                    var messageLength = bindView.getUint16(20, false);
                    if (packet.payload.length !== 22 + messageLength) {
                        throw new Error("browser_media_bind_result_size_invalid");
                    }
                    var success = packet.payload[0] === 1;
                    var boundControlId = bytesToUuid(packet.payload, 4);
                    var bindMessage = textDecoder.decode(packet.payload.slice(22));
                    if (!success) {
                        throw new Error(
                            "browser_media_bind_rejected_" + bindMessage);
                    }
                    if (boundControlId !== session.controlConnectionId) {
                        throw new Error("browser_media_bind_connection_mismatch");
                    }

                    session.bound = true;
                    if (session.bindTimer) {
                        window.clearTimeout(session.bindTimer);
                        session.bindTimer = null;
                    }
                    pipeline.worker.postMessage({
                        type: "production_mode",
                        enabled: true
                    });
                    queueUnityEvent("bound", [boundControlId]);
                    return;
                }

                if (packet.kind === 3) {
                    if (!session.bound || packet.codec !== 1 ||
                        packet.payload.length === 0) {
                        throw new Error("browser_media_frame_invalid");
                    }

                    session.inboundFrames += 1;
                    session.lastInboundPacketBytes = packet.payload.length;
                    pipeline.worker.postMessage({
                        type: "remote_packet",
                        packet: packet.payload.buffer,
                        streamId: packet.streamId,
                        mediaSequence: packet.mediaSequence,
                        mediaTimestamp100Ns: String(
                            packet.mediaTimestamp100Ns),
                        senderId: packet.senderId
                    }, [packet.payload.buffer]);
                    return;
                }

                if (packet.kind === 5) {
                    session.pongCount += 1;
                }
            };

            var socket = new WebSocket(endpoint);
            socket.binaryType = "arraybuffer";
            session.socket = socket;
            Module.voiceBrowserMediaSessions[handle] = session;

            socket.onopen = function () {
                try {
                    var bindPayload = encodeBindPayload();
                    if (!sendPacket(
                            1,
                            0,
                            0,
                            0,
                            0,
                            session.streamId,
                            bindPayload)) {
                        throw new Error("browser_media_bind_send_failed");
                    }
                    queueUnityEvent("open", []);
                    session.bindTimer = window.setTimeout(function () {
                        if (session.cleaned || session.bound) return;
                        queueUnityEvent("failure", ["browser_media_bind_timeout"]);
                        try { socket.close(4000, "media_bind_timeout"); } catch (_) {}
                    }, 8000);
                } catch (error) {
                    queueUnityEvent("failure", [safeReason(
                        error && error.message ? error.message : error)]);
                    try { socket.close(1011, "bind_failed"); } catch (_) {}
                }
            };

            socket.onmessage = function (event) {
                var handleBuffer = function (buffer) {
                    try {
                        processIncoming(buffer);
                    } catch (error) {
                        queueUnityEvent("failure", [safeReason(
                            error && error.message ? error.message : error)]);
                        if (!session.bound && session.socket) {
                            try {
                                session.socket.close(1008, "media_bind_failed");
                            } catch (_) {
                            }
                        }
                    }
                };

                if (event.data instanceof ArrayBuffer) {
                    handleBuffer(event.data);
                    return;
                }
                if (event.data && typeof event.data.arrayBuffer === "function") {
                    event.data.arrayBuffer().then(handleBuffer).catch(function (error) {
                        queueUnityEvent("failure", [safeReason(
                            error && error.message ? error.message : error)]);
                    });
                }
            };

            socket.onerror = function () {
                queueUnityEvent("failure", ["browser_media_socket_error"]);
                try { socket.close(4001, "media_socket_error"); } catch (_) {}
            };

            socket.onclose = function (event) {
                var reason = event && event.reason
                    ? event.reason
                    : "remote_closed";
                var intentional = session.intentionalClose;
                cleanup();
                queueUnityEvent("disconnected", [
                    safeReason(reason),
                    intentional ? 1 : 0
                ]);
            };

            session.statsTimer = window.setInterval(emitStats, 1000);
            session.pingTimer = window.setInterval(function () {
                if (!session.bound) return;
                try {
                    sendPacket(
                        4,
                        0,
                        0,
                        0,
                        0,
                        session.streamId,
                        new Uint8Array([14, 2, 1, 4]));
                } catch (error) {
                    queueUnityEvent("failure", [safeReason(
                        error && error.message ? error.message : error)]);
                }
            }, 5000);
            session.heartbeatTimer = window.setInterval(function () {
                if (!session.bound || session.cleaned) return;
                if (Date.now() - session.lastInboundAt <= 15000) return;
                queueUnityEvent("failure", ["browser_media_heartbeat_timeout"]);
                try { socket.close(4002, "media_heartbeat_timeout"); } catch (_) {}
            }, 1000);

            return 1;
        } catch (error) {
            try {
                console.error("[VoiceBrowserMedia] " +
                    String(error && error.message ? error.message : error));
            } catch (_) {
            }
            return 0;
        }
    },

    VoiceBrowserMediaSetOutboundEnabled: function (handle, enabled) {
        if (Module.voiceWebRtcSessions && Module.voiceWebRtcSessions[handle]) {
            return window.VME2WebRtcVoice &&
                window.VME2WebRtcVoice.setOutbound(handle, enabled !== 0) ? 1 : 0;
        }
        var sessions = Module.voiceBrowserMediaSessions || {};
        var session = sessions[handle];
        if (!session || !session.bound) return 0;
        session.outboundEnabled = enabled !== 0;
        return 1;
    },

    VoiceBrowserMediaDisconnect: function (handle, reasonPtr) {
        if (Module.voiceWebRtcSessions && Module.voiceWebRtcSessions[handle]) {
            Module.voiceWebRtcSessions[handle].closed = true;
            if (window.VME2WebRtcVoice) window.VME2WebRtcVoice.disconnect(handle);
            delete Module.voiceWebRtcSessions[handle];
            return;
        }
        var sessions = Module.voiceBrowserMediaSessions || {};
        var session = sessions[handle];
        if (!session) return;
        session.intentionalClose = true;
        session.outboundEnabled = false;
        var reason = UTF8ToString(reasonPtr || 0) || "browser_media_disconnect";
        session.cleanup();
        if (session.socket &&
            (session.socket.readyState === WebSocket.OPEN ||
             session.socket.readyState === WebSocket.CONNECTING)) {
            try {
                session.socket.close(1000, reason.slice(0, 120));
                return;
            } catch (_) {
            }
        }
    }
});
