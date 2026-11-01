mergeInto(LibraryManager.library, {
    VoiceBrowserAudioStart: function (handle, objectNamePtr, assetRootPtr) {
        var objectName = UTF8ToString(objectNamePtr);
        var assetRoot = UTF8ToString(assetRootPtr).replace(/\/$/, "");
        var assetRevision = String(Date.now());
        var versionedAsset = function (fileName) {
            return assetRoot + "/" + fileName +
                "?voiceAssetRevision=" + encodeURIComponent(assetRevision);
        };

        if (!Module.voiceBrowserAudioPipelines) Module.voiceBrowserAudioPipelines = {};
        if (Module.voiceBrowserAudioPipelines[handle]) return 0;

        var AudioContextType = window.AudioContext || window.webkitAudioContext;
        if (!window.isSecureContext || !AudioContextType ||
            !navigator.mediaDevices || !navigator.mediaDevices.getUserMedia ||
            !window.AudioWorkletNode || !window.Worker || !window.MessageChannel) {
            return 0;
        }

        var pipeline = {
            handle: handle,
            objectName: objectName,
            stream: null,
            playbackEnabled: true,
            context: null,
            source: null,
            worklet: null,
            silentGain: null,
            worker: null,
            resumeHandler: null,
            deviceSelect: null,
            deviceChangeHandler: null,
            deviceChangeTimer: null,
            deviceSwitching: false,
            selectedDeviceId: "",
            startupTimer: null,
            closed: false
        };

        Module.voiceBrowserAudioPipelines[handle] = pipeline;

        var queueUnityEvent = function (eventName, values) {
            var payload = String(handle) + "|" + String(eventName || "unknown");
            if (values && values.length) payload += "|" + values.join("|");

            window.setTimeout(function () {
                try {
                    SendMessage(objectName, "HandleVoiceBrowserAudioEvent", payload);
                } catch (_) {
                }
            }, 0);
        };

        var reportStage = function (stage) {
            try {
                console.log("[VoiceBrowserAudio] stage=" + String(stage));
            } catch (_) {
            }
        };

        var isWindowsDesktop = function () {
            var userAgent = String(navigator.userAgent || "");
            return /Windows NT/i.test(userAgent) && !/Android/i.test(userAgent);
        };

        var isMobileBrowser = function () {
            var userAgent = String(navigator.userAgent || "");
            return /Android|iPhone|iPad|iPod|Mobile/i.test(userAgent) ||
                (navigator.maxTouchPoints > 1 &&
                 Math.min(screen.width || 0, screen.height || 0) < 1100);
        };

        var buildAudioConstraints = function (deviceId) {
            var windowsDesktop = isWindowsDesktop();
            var mobileBrowser = isMobileBrowser();
            var constraints = windowsDesktop
                ? {
                    channelCount: { ideal: 1 },
                    echoCancellation: false,
                    noiseSuppression: false,
                    autoGainControl: true
                }
                : mobileBrowser
                    ? {
                        channelCount: 1,
                        echoCancellation: true,
                        noiseSuppression: true,
                        autoGainControl: true
                    }
                    : {
                        channelCount: 1,
                        echoCancellation: false,
                        noiseSuppression: false,
                        autoGainControl: false
                    };

            try {
                console.log(
                    "[VoiceBrowserAudio] stage=input_processing_requested" +
                    " | platform=" +
                    (windowsDesktop
                        ? "windows_desktop"
                        : mobileBrowser
                            ? "mobile_browser_aec"
                            : "baseline") +
                    " | autoGainControl=" + String(!!constraints.autoGainControl) +
                    " | echoCancellation=" + String(!!constraints.echoCancellation) +
                    " | noiseSuppression=" + String(!!constraints.noiseSuppression));
            } catch (_) {
            }

            var requestedDeviceId = String(deviceId || "").trim();
            if (requestedDeviceId) {
                constraints.deviceId = { exact: requestedDeviceId };
            }
            return constraints;
        };

        // Automatic headset selection is confined to desktop WebGL.
        // Do not infer an arbitrary USB microphone to be a headset.
        var detectPreferredInput = async function () {
            if (!isWindowsDesktop() || !navigator.mediaDevices.enumerateDevices) {
                return { device: null, inputs: [] };
            }
            var devices = await navigator.mediaDevices.enumerateDevices();
            var inputs = devices.filter(function (device) {
                return device && device.kind === "audioinput" &&
                    device.deviceId && device.deviceId !== "default" &&
                    device.deviceId !== "communications";
            });
            var outputs = devices.filter(function (device) {
                return device && device.kind === "audiooutput";
            });
            var scored = inputs.map(function (device) {
                var label = String(device.label || "").toLowerCase();
                var score = 0;
                if (/headset|headphones?|hands[ -]?free|earbuds?|earphones?|airpods|tws/.test(label)) {
                    score += 100;
                }
                // The already-identified Rapoo device in this deployment is a headset.
                if (/rapoo/.test(label)) score += 80;
                if (device.groupId && outputs.some(function (output) {
                    return output.groupId === device.groupId &&
                        /headset|headphones?|hands[ -]?free|earbuds?|earphones?|airpods/.test(
                            String(output.label || "").toLowerCase());
                })) score += 90;
                if (/microphone array|built[ -]?in|internal microphone|integrated microphone/.test(label)) {
                    score -= 100;
                }
                return { device: device, score: score };
            }).sort(function (a, b) { return b.score - a.score; });
            if (!scored.length || scored[0].score < 80 ||
                (scored.length > 1 && scored[0].score === scored[1].score)) {
                return { device: null, inputs: inputs };
            }
            return { device: scored[0].device, inputs: inputs };
        };

        var stopStream = function (stream) {
            if (!stream) return;
            stream.getTracks().forEach(function (track) {
                try { track.stop(); } catch (_) {}
            });
        };

        var reportSelectedDevice = function (stream, stage) {
            var tracks = stream ? stream.getAudioTracks() : [];
            var track = tracks.length > 0 ? tracks[0] : null;
            var settings = track && track.getSettings
                ? track.getSettings()
                : {};
            pipeline.selectedDeviceId = String(settings.deviceId || "");
            reportStage(
                stage +
                " | deviceId=" + (pipeline.selectedDeviceId || "default") +
                " | sampleRate=" + String(settings.sampleRate || 0) +
                " | channelCount=" + String(settings.channelCount || 0) +
                " | autoGainControl=" + String(settings.autoGainControl) +
                " | echoCancellation=" + String(settings.echoCancellation) +
                " | noiseSuppression=" + String(settings.noiseSuppression));
        };

        var refreshInputDevices = async function () {
            if (pipeline.closed || !pipeline.deviceSelect ||
                !navigator.mediaDevices.enumerateDevices) {
                return;
            }

            var devices = await navigator.mediaDevices.enumerateDevices();
            if (pipeline.closed || !pipeline.deviceSelect) return;

            var inputs = devices.filter(function (device) {
                return device && device.kind === "audioinput";
            });
            var select = pipeline.deviceSelect;
            var selectedId = pipeline.selectedDeviceId;
            while (select.firstChild) select.removeChild(select.firstChild);

            inputs.forEach(function (device, index) {
                var option = document.createElement("option");
                option.value = String(device.deviceId || "");
                option.textContent = device.label ||
                    ("میکروفن " + String(index + 1));
                select.appendChild(option);
            });

            if (selectedId && inputs.some(function (device) {
                    return device.deviceId === selectedId;
                })) {
                select.value = selectedId;
            }

            select.disabled = pipeline.deviceSwitching || inputs.length < 2;
            select.style.display = inputs.length > 0 ? "block" : "none";
            reportStage(
                "input_devices_ready" +
                " | count=" + String(inputs.length) +
                " | selectable=" + String(inputs.length > 1));
        };

        var switchInputDevice = async function (deviceId) {
            if (pipeline.closed || pipeline.deviceSwitching ||
                !pipeline.context || !pipeline.worklet) {
                return false;
            }

            var requestedDeviceId = String(deviceId || "").trim();
            if (!requestedDeviceId ||
                requestedDeviceId === pipeline.selectedDeviceId) {
                return true;
            }

            pipeline.deviceSwitching = true;
            if (pipeline.deviceSelect) pipeline.deviceSelect.disabled = true;
            var replacementStream = null;
            var replacementSource = null;

            try {
                replacementStream = await navigator.mediaDevices.getUserMedia({
                    audio: buildAudioConstraints(requestedDeviceId),
                    video: false
                });

                if (pipeline.closed) {
                    stopStream(replacementStream);
                    return false;
                }

                replacementSource = pipeline.context.createMediaStreamSource(
                    replacementStream);
                replacementSource.connect(pipeline.worklet);

                var previousSource = pipeline.source;
                var previousStream = pipeline.stream;
                pipeline.source = replacementSource;
                pipeline.stream = replacementStream;
                reportSelectedDevice(
                    replacementStream,
                    "input_device_selected");

                if (previousSource) {
                    try { previousSource.disconnect(); } catch (_) {}
                }
                stopStream(previousStream);
                await refreshInputDevices();
                return true;
            } catch (error) {
                if (replacementSource) {
                    try { replacementSource.disconnect(); } catch (_) {}
                }
                stopStream(replacementStream);
                reportStage(
                    "input_device_select_failed" +
                    " | reason=" + String(
                        error && error.name ? error.name : "unknown"));
                return false;
            } finally {
                pipeline.deviceSwitching = false;
                if (pipeline.deviceSelect) {
                    if (pipeline.selectedDeviceId) {
                        pipeline.deviceSelect.value =
                            pipeline.selectedDeviceId;
                    }
                    refreshInputDevices().catch(function (error) {
                        reportStage(
                            "input_devices_refresh_failed" +
                            " | reason=" + String(
                                error && error.name
                                    ? error.name
                                    : "unknown"));
                    });
                }
            }
        };

        var autoSelectInput = async function () {
            if (pipeline.closed || pipeline.deviceSwitching ||
                !pipeline.source || !pipeline.worklet) return;
            var detected = await detectPreferredInput();
            if (pipeline.closed || pipeline.deviceSwitching) return;
            var choice = detected.device;
            if (!choice && pipeline.selectedDeviceId &&
                !detected.inputs.some(function (device) {
                    return device.deviceId === pipeline.selectedDeviceId;
                })) {
                // The previously selected device was physically removed.
                choice = detected.inputs[0] || null;
            }
            if (!choice || choice.deviceId === pipeline.selectedDeviceId) return;
            var applied = await switchInputDevice(choice.deviceId);
            reportStage("input_auto_switch" +
                " | result=" + (applied ? "PASS" : "FAILED") +
                " | label=" + String(choice.label || "unknown"));
        };

        var installAutomaticInputMonitor = function () {
            if (pipeline.closed || !navigator.mediaDevices.enumerateDevices) return;
            pipeline.deviceChangeHandler = function () {
                if (pipeline.deviceChangeTimer) {
                    window.clearTimeout(pipeline.deviceChangeTimer);
                }
                pipeline.deviceChangeTimer = window.setTimeout(function () {
                    pipeline.deviceChangeTimer = null;
                    autoSelectInput().catch(function (error) {
                        reportStage("input_auto_detect_failed" +
                            " | reason=" + String(error && error.name || "unknown"));
                    });
                }, 500);
            };
            if (navigator.mediaDevices.addEventListener) {
                navigator.mediaDevices.addEventListener("devicechange", pipeline.deviceChangeHandler);
            }
            autoSelectInput().catch(function (error) {
                reportStage("input_auto_detect_failed" +
                    " | reason=" + String(error && error.name || "unknown"));
            });
        };

        var installInputDeviceSelector = async function () {
            if (pipeline.closed || pipeline.deviceSelect ||
                !navigator.mediaDevices.enumerateDevices || !document.body) {
                return;
            }

            var select = document.createElement("select");
            select.id = "voice-browser-input-device-" + String(handle);
            select.dir = "rtl";
            select.title = "انتخاب میکروفن";
            select.setAttribute("aria-label", "انتخاب میکروفن");
            select.style.position = "fixed";
            select.style.right = "max(16px, env(safe-area-inset-right))";
            select.style.bottom =
                "calc(140px + env(safe-area-inset-bottom))";
            select.style.width = "min(320px, calc(100vw - 32px))";
            select.style.height = "42px";
            select.style.zIndex = "2147483646";
            select.style.padding = "0 12px";
            select.style.borderRadius = "8px";
            select.style.border = "1px solid rgba(255,255,255,0.65)";
            select.style.background = "rgba(20,20,24,0.92)";
            select.style.color = "white";
            select.style.fontSize = "15px";
            select.style.fontFamily = "sans-serif";
            select.style.direction = "rtl";
            select.style.textAlign = "right";
            select.disabled = true;

            select.addEventListener("change", function () {
                var previousId = pipeline.selectedDeviceId;
                switchInputDevice(select.value).then(function (switched) {
                    if (!switched && pipeline.deviceSelect) {
                        pipeline.deviceSelect.value = previousId;
                    }
                });
            });

            pipeline.deviceSelect = select;
            document.body.appendChild(select);
            pipeline.deviceChangeHandler = function () {
                refreshInputDevices().catch(function (error) {
                    reportStage(
                        "input_devices_refresh_failed" +
                        " | reason=" + String(
                            error && error.name ? error.name : "unknown"));
                });
            };
            navigator.mediaDevices.addEventListener(
                "devicechange",
                pipeline.deviceChangeHandler);
            await refreshInputDevices();
        };

        var cleanup = function () {
            if (pipeline.closed) return;
            pipeline.closed = true;

            if (pipeline.resumeHandler) {
                window.removeEventListener("pointerdown", pipeline.resumeHandler, true);
                window.removeEventListener("keydown", pipeline.resumeHandler, true);
            }
            if (pipeline.deviceChangeHandler && navigator.mediaDevices) {
                navigator.mediaDevices.removeEventListener(
                    "devicechange",
                    pipeline.deviceChangeHandler);
                pipeline.deviceChangeHandler = null;
            }
            if (pipeline.deviceSelect) {
                if (pipeline.deviceSelect.parentNode) {
                    pipeline.deviceSelect.parentNode.removeChild(
                        pipeline.deviceSelect);
                }
                pipeline.deviceSelect = null;
            }
            if (pipeline.startupTimer) {
                window.clearTimeout(pipeline.startupTimer);
                pipeline.startupTimer = null;
            }
            if (pipeline.deviceChangeTimer) {
                window.clearTimeout(pipeline.deviceChangeTimer);
                pipeline.deviceChangeTimer = null;
            }

            if (pipeline.source) {
                try { pipeline.source.disconnect(); } catch (_) {}
            }
            if (pipeline.worklet) {
                try { pipeline.worklet.disconnect(); } catch (_) {}
            }
            if (pipeline.silentGain) {
                try { pipeline.silentGain.disconnect(); } catch (_) {}
            }
            if (pipeline.worker) {
                try { pipeline.worker.postMessage({ type: "dispose" }); } catch (_) {}
                try { pipeline.worker.terminate(); } catch (_) {}
            }
            stopStream(pipeline.stream);
            if (pipeline.context && pipeline.context.state !== "closed") {
                pipeline.context.close().catch(function () {});
            }

            delete Module.voiceBrowserAudioPipelines[handle];
        };

        pipeline.assetRoot = assetRoot;
        pipeline.cleanup = cleanup;
        pipeline.startupTimer = window.setTimeout(function () {
            if (pipeline.closed) return;
            queueUnityEvent("failure", ["browser_audio_javascript_start_timeout"]);
            cleanup();
        }, 90000);

        try {
            // This is deliberately executed before the first await. The native call
            // originates from the explicit Unity button click and therefore retains
            // the browser user-gesture context required by Chrome.
            pipeline.context = new AudioContextType({ latencyHint: "interactive" });
            // Unity processes its GUI click on a later frame, so Chrome may keep
            // this promise pending. Do not block pipeline readiness on it. The
            // installed pointer/keyboard handler resumes the context explicitly.
            pipeline.context.resume().catch(function () {});
            reportStage("context_created");

            (async function () {
                try {
                    var initialInput = null;
                    try {
                        var detectedBeforeCapture = await detectPreferredInput();
                        initialInput = detectedBeforeCapture.device;
                    } catch (error) {
                        reportStage("input_auto_preflight_unavailable" +
                            " | reason=" + String(error && error.name || "unknown"));
                    }
                    try {
                        pipeline.stream = await navigator.mediaDevices.getUserMedia({
                            audio: buildAudioConstraints(initialInput && initialInput.deviceId),
                            video: false
                        });
                    } catch (error) {
                        if (!initialInput) throw error;
                        reportStage("input_auto_initial_fallback" +
                            " | reason=" + String(error && error.name || "unknown"));
                        pipeline.stream = await navigator.mediaDevices.getUserMedia({
                            audio: buildAudioConstraints(""),
                            video: false
                        });
                    }

                    if (pipeline.closed) {
                        stopStream(pipeline.stream);
                        return;
                    }
                    reportStage("microphone_ready");
                    reportSelectedDevice(
                        pipeline.stream,
                        "input_device_active");

                    await pipeline.context.audioWorklet.addModule(
                        versionedAsset("VoiceBrowserAudioProcessor.js"));

                    if (pipeline.closed) return;
                    reportStage("worklet_module_ready");

                    pipeline.worklet = new AudioWorkletNode(
                        pipeline.context,
                        "voice-browser-audio-processor",
                        {
                            numberOfInputs: 1,
                            numberOfOutputs: 1,
                            outputChannelCount: [2],
                            channelCount: 1,
                            channelCountMode: "explicit",
                            channelInterpretation: "speakers"
                        });

                    pipeline.worker = new Worker(
                        versionedAsset("VoiceBrowserAudioWorker.js"));
                    reportStage("worker_created");

                    var workerReady = new Promise(function (resolve, reject) {
                        var settled = false;
                        var timeout = window.setTimeout(function () {
                            if (settled) return;
                            settled = true;
                            reject(new Error("opus_worker_timeout"));
                        }, 15000);

                        pipeline.worker.onmessage = function (event) {
                            var message = event.data || {};

                            if (message.type === "ready" && !settled) {
                                settled = true;
                                window.clearTimeout(timeout);
                                resolve(message);
                                return;
                            }

                            if (message.type === "stats") {
                                queueUnityEvent("stats", [
                                    message.captureBlocks,
                                    message.encodedFrames,
                                    message.decodedFrames,
                                    message.encodedBytes,
                                    message.playbackUnderflows,
                                    message.captureDrops,
                                    message.lastPacketBytes,
                                    message.bitrate,
                                    message.fec ? 1 : 0,
                                    message.packetLossPercent,
                                    Number(message.encodeAverageMicros) || 0,
                                    Number(message.encodeMaxMicros) || 0,
                                    Number(
                                        message.cadenceMaxDeviationMicros) || 0,
                                    Number(message.playbackDrops) || 0,
                                    Number(message.remoteStreams) || 0,
                                    Number(message.remoteBufferedFrames) || 0,
                                    Number(message.remoteTargetDelayMs) || 0,
                                    Number(message.remoteJitterMicros) || 0,
                                    Number(message.remoteLateDrops) || 0,
                                    Number(message.remoteDuplicateDrops) || 0,
                                    Number(message.remoteReorderedPackets) || 0,
                                    Number(message.remoteMissingFrames) || 0,
                                    Number(message.remoteOverflowDrops) || 0,
                                    Number(message.remoteNormalFrames) || 0,
                                    Number(message.remoteFecFrames) || 0,
                                    Number(message.remotePlcFrames) || 0
                                ]);
                                return;
                            }

                            if (message.type === "configured") {
                                queueUnityEvent("configured", [
                                    message.bitrate,
                                    message.fec ? 1 : 0,
                                    message.packetLossPercent
                                ]);
                                return;
                            }

                            if (message.type === "error") {
                                var workerError = new Error(
                                    message.error || "opus_worker_error");

                                if (!settled) {
                                    settled = true;
                                    window.clearTimeout(timeout);
                                    reject(workerError);
                                } else {
                                    queueUnityEvent("failure", [workerError.message]);
                                }
                            }
                        };

                        pipeline.worker.onerror = function (event) {
                            var workerError = new Error(
                                event.message || "audio_worker_failed");

                            if (!settled) {
                                settled = true;
                                window.clearTimeout(timeout);
                                reject(workerError);
                            } else {
                                queueUnityEvent("failure", [workerError.message]);
                            }
                        };
                    });

                    pipeline.worker.postMessage({
                        type: "initialize",
                        moduleUrl: versionedAsset("VoiceOpusModule.js"),
                        wasmUrl: versionedAsset("VoiceOpusModule.wasm"),
                        sourceSampleRate: pipeline.context.sampleRate,
                        bitrate: 32000,
                        fec: false,
                        packetLossPercent: 0
                    });

                    var ready = await workerReady;
                    if (pipeline.closed) return;
                    reportStage("opus_ready");

                    var channel = new MessageChannel();
                    pipeline.worker.postMessage(
                        { type: "attach", port: channel.port1 },
                        [channel.port1]);
                    pipeline.worklet.port.postMessage(
                        { type: "attach", port: channel.port2 },
                        [channel.port2]);

                    pipeline.source = pipeline.context.createMediaStreamSource(
                        pipeline.stream);
                    pipeline.silentGain = pipeline.context.createGain();
                    // The processor output contains only decoded remote audio.
                    // Microphone input is copied to the worker and is never
                    // forwarded to the output, so playback must remain audible.
                    pipeline.silentGain.gain.value = 1.0;
                    pipeline.source.connect(pipeline.worklet);
                    pipeline.worklet.connect(pipeline.silentGain);
                    pipeline.silentGain.connect(pipeline.context.destination);
                    reportStage("playback_output_connected");
                    // No Chrome settings and no additional in-game overlay are required:
                    // prefer a confidently identified headset and monitor hot-plug changes.
                    installAutomaticInputMonitor();
                    reportStage("input_device_selector_ui_disabled_auto_headset_enabled");

                    pipeline.resumeHandler = function () {
                        if (pipeline.context && pipeline.context.state === "suspended") {
                            pipeline.context.resume().catch(function () {});
                        }
                    };

                    window.addEventListener("pointerdown", pipeline.resumeHandler, true);
                    window.addEventListener("keydown", pipeline.resumeHandler, true);

                    if (pipeline.startupTimer) {
                        window.clearTimeout(pipeline.startupTimer);
                        pipeline.startupTimer = null;
                    }
                    reportStage("ready_sent");

                    queueUnityEvent("ready", [
                        pipeline.context.sampleRate,
                        pipeline.context.state,
                        ready.version
                    ]);
                } catch (error) {
                    queueUnityEvent("failure", [
                        error && error.message
                            ? error.message
                            : "browser_audio_start_failed"
                    ]);
                    cleanup();
                }
            })();

            return 1;
        } catch (error) {
            queueUnityEvent("failure", [
                error && error.message
                    ? error.message
                    : "browser_audio_start_failed"
            ]);
            cleanup();
            return 1;
        }
    },

    VoiceBrowserAudioSetCaptureEnabled: function (handle, enabled) {
        var pipeline = Module.voiceBrowserAudioPipelines &&
            Module.voiceBrowserAudioPipelines[handle];

        if (!pipeline || pipeline.closed || !pipeline.worker || !pipeline.worklet) {
            return 0;
        }

        // W4 WebRTC owns microphone publication through the SFU Producer.
        // The legacy Opus Worker must not encode a second outbound stream.
        return 1;
    },

    VoiceBrowserAudioSetPlaybackEnabled: function (handle, enabled) {
        var pipeline = Module.voiceBrowserAudioPipelines &&
            Module.voiceBrowserAudioPipelines[handle];

        if (!pipeline || pipeline.closed || !pipeline.context ||
            !pipeline.silentGain) {
            return 0;
        }

        try {
            var playbackEnabled = enabled !== 0;
            pipeline.playbackEnabled = playbackEnabled;
            if (window.VME2WebRtcVoice) {
                window.VME2WebRtcVoice.setPlaybackEnabled(handle, playbackEnabled);
            }
            return 1;
        } catch (_) {
            return 0;
        }
    },

    VoiceBrowserAudioConfigureEncoder: function (
        handle,
        bitrate,
        fec,
        packetLossPercent) {
        var pipeline = Module.voiceBrowserAudioPipelines &&
            Module.voiceBrowserAudioPipelines[handle];

        if (!pipeline || pipeline.closed || !pipeline.worker) return 0;

        pipeline.worker.postMessage({
            type: "configure",
            bitrate: bitrate,
            fec: fec !== 0,
            packetLossPercent: packetLossPercent
        });
        return 1;
    },

    VoiceBrowserAudioStop: function (handle) {
        var pipeline = Module.voiceBrowserAudioPipelines &&
            Module.voiceBrowserAudioPipelines[handle];

        if (!pipeline) return;
        pipeline.cleanup();

        window.setTimeout(function () {
            try {
                SendMessage(
                    pipeline.objectName,
                    "HandleVoiceBrowserAudioEvent",
                    String(handle) + "|stopped");
            } catch (_) {
            }
        }, 0);
    }
});
