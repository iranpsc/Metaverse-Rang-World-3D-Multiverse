mergeInto(LibraryManager.library, {
    RealtimeWebGLWebSocketConnect: function (handle, urlPtr) {
        Module.RealtimeWebGLSockets = Module.RealtimeWebGLSockets || {};
        Module.RealtimeWebGLWebSocketEncode = Module.RealtimeWebGLWebSocketEncode || function (value) {
            try { return btoa(unescape(encodeURIComponent(value || ""))); }
            catch (e) { return btoa(value || ""); }
        };

        Module.RealtimeWebGLVisibilityDiagnostics = Module.RealtimeWebGLVisibilityDiagnostics || {
            installed: false,
            handlers: null,
            hiddenStartedAtMs: 0,
            sequence: 0,
            visibilityRevision: 0,
            visibilityConfigs: {},

            nowMs: function () {
                return Date.now();
            },

            sanitizeUrl: function (value) {
                var url = value ? String(value) : "";
                if (!url) return "";

                try {
                    if (typeof URL !== "undefined") {
                        var parsed = new URL(url);
                        return parsed.protocol + "//" + parsed.host + parsed.pathname;
                    }
                } catch (e) {}

                var queryIndex = url.indexOf("?");
                if (queryIndex >= 0) url = url.substring(0, queryIndex);

                var fragmentIndex = url.indexOf("#");
                if (fragmentIndex >= 0) url = url.substring(0, fragmentIndex);

                return url;
            },

            getHiddenState: function () {
                try {
                    if (typeof document === "undefined") return false;
                    return document.hidden === true;
                } catch (e) {
                    return false;
                }
            },

            getVisibilityState: function () {
                try {
                    if (typeof document === "undefined") return "unknown";
                    return document.visibilityState || "unknown";
                } catch (e) {
                    return "unknown";
                }
            },

            getFocusState: function () {
                try {
                    if (typeof document === "undefined" || !document.hasFocus) return true;
                    return document.hasFocus() === true;
                } catch (e) {
                    return true;
                }
            },

            getOnlineState: function () {
                try {
                    if (typeof navigator === "undefined") return true;
                    return navigator.onLine !== false;
                } catch (e) {
                    return true;
                }
            },

            getSocketSnapshots: function () {
                var snapshots = [];
                var sockets = Module.RealtimeWebGLSockets || {};

                for (var socketHandle in sockets) {
                    if (!Object.prototype.hasOwnProperty.call(sockets, socketHandle)) continue;

                    var socket = sockets[socketHandle];
                    if (!socket) continue;

                    snapshots.push({
                        handle: String(socketHandle),
                        readyState: typeof socket.readyState === "number" ? socket.readyState : -1,
                        url: this.sanitizeUrl(socket.url ? String(socket.url) : "")
                    });
                }

                return snapshots;
            },

            getVisibilityRevision: function () {
                return this.visibilityRevision || 0;
            },

            isCurrentSocket: function (handle, socket) {
                return !!(Module.RealtimeWebGLSockets &&
                    Module.RealtimeWebGLSockets[handle] === socket);
            },

            findSocketByUrl: function (url) {
                var targetUrl = this.sanitizeUrl(url || "");
                var sockets = Module.RealtimeWebGLSockets || {};

                for (var socketHandle in sockets) {
                    if (!Object.prototype.hasOwnProperty.call(sockets, socketHandle)) continue;

                    var socket = sockets[socketHandle];
                    if (!socket) continue;

                    var socketUrl = this.sanitizeUrl(socket.url ? String(socket.url) : "");
                    if (socketUrl === targetUrl) return socket;
                }

                return null;
            },

            configureVisibilityMessages: function (url, hiddenMessage, visibleMessage) {
                var key = this.sanitizeUrl(url || "");
                if (!key) return;

                this.visibilityConfigs[key] = {
                    url: key,
                    hiddenMessage: hiddenMessage || "",
                    visibleMessage: visibleMessage || "",
                    lastSentHidden: null
                };

                this.sendConfiguredVisibilityState("configured");
            },

            clearVisibilityMessages: function (url) {
                var key = this.sanitizeUrl(url || "");
                if (!key) return;
                delete this.visibilityConfigs[key];
            },

            sendConfiguredVisibilityState: function (source) {
                var hidden = this.getHiddenState();
                var configs = this.visibilityConfigs || {};

                for (var key in configs) {
                    if (!Object.prototype.hasOwnProperty.call(configs, key)) continue;

                    var config = configs[key];
                    if (!config) continue;
                    if (config.lastSentHidden === hidden) continue;

                    var socket = this.findSocketByUrl(config.url);
                    var message = hidden ? config.hiddenMessage : config.visibleMessage;

                    if (!socket || socket.readyState !== 1 || !message) {
                        this.log("visibility_message_skipped", {
                            source: source || "unknown",
                            hidden: hidden,
                            url: config.url,
                            readyState: socket && typeof socket.readyState === "number"
                                ? socket.readyState
                                : -1
                        });
                        continue;
                    }

                    try {
                        socket.send(message);
                        config.lastSentHidden = hidden;
                        this.log("visibility_message_sent", {
                            source: source || "unknown",
                            hidden: hidden,
                            url: config.url,
                            readyState: socket.readyState
                        });
                    } catch (e) {
                        this.log("visibility_message_send_exception", {
                            source: source || "unknown",
                            hidden: hidden,
                            url: config.url,
                            message: e && e.message ? e.message : "visibility send failed"
                        });
                    }
                }
            },

            log: function (eventName, details) {
                var now = this.nowMs();
                var hidden = this.getHiddenState();
                var hiddenDurationMs = 0;

                if (hidden) {
                    if (this.hiddenStartedAtMs <= 0) this.hiddenStartedAtMs = now;
                    hiddenDurationMs = Math.max(0, now - this.hiddenStartedAtMs);
                } else if (this.hiddenStartedAtMs > 0) {
                    hiddenDurationMs = Math.max(0, now - this.hiddenStartedAtMs);
                }

                this.sequence++;

                var payload = {
                    sequence: this.sequence,
                    event: eventName || "unknown",
                    hidden: hidden,
                    visibilityState: this.getVisibilityState(),
                    focused: this.getFocusState(),
                    online: this.getOnlineState(),
                    hiddenDurationMs: Math.round(hiddenDurationMs),
                    epochMs: Date.now(),
                    sockets: this.getSocketSnapshots(),
                    details: details || null
                };

                try {
                    console.log("[RealtimeWebGLVisibility] " + JSON.stringify(payload));
                } catch (e) {}

                if (!hidden) this.hiddenStartedAtMs = 0;
            },

            install: function () {
                if (this.installed) return;
                if (typeof document === "undefined" || typeof window === "undefined") return;

                var diagnostics = this;
                this.handlers = {
                    visibilitychange: function () {
                        diagnostics.visibilityRevision++;
                        diagnostics.log("visibilitychange");
                        diagnostics.sendConfiguredVisibilityState("visibilitychange");
                    },
                    pagehide: function (event) {
                        diagnostics.visibilityRevision++;
                        diagnostics.log("pagehide", {
                            persisted: !!(event && event.persisted)
                        });
                        diagnostics.sendConfiguredVisibilityState("pagehide");
                    },
                    pageshow: function (event) {
                        diagnostics.visibilityRevision++;
                        diagnostics.log("pageshow", {
                            persisted: !!(event && event.persisted)
                        });
                        diagnostics.sendConfiguredVisibilityState("pageshow");
                    },
                    focus: function () { diagnostics.log("focus"); },
                    blur: function () { diagnostics.log("blur"); },
                    online: function () { diagnostics.log("online"); },
                    offline: function () { diagnostics.log("offline"); }
                };

                document.addEventListener("visibilitychange", this.handlers.visibilitychange, true);
                window.addEventListener("pagehide", this.handlers.pagehide, true);
                window.addEventListener("pageshow", this.handlers.pageshow, true);
                window.addEventListener("focus", this.handlers.focus, true);
                window.addEventListener("blur", this.handlers.blur, true);
                window.addEventListener("online", this.handlers.online, true);
                window.addEventListener("offline", this.handlers.offline, true);

                this.installed = true;
                if (this.getHiddenState()) this.hiddenStartedAtMs = this.nowMs();
                this.log("diagnostics_installed");
            },

            uninstallIfUnused: function () {
                var sockets = Module.RealtimeWebGLSockets || {};
                for (var socketHandle in sockets) {
                    if (!Object.prototype.hasOwnProperty.call(sockets, socketHandle)) continue;
                    if (sockets[socketHandle]) return;
                }

                if (!this.installed || !this.handlers) return;
                if (typeof document === "undefined" || typeof window === "undefined") return;

                document.removeEventListener("visibilitychange", this.handlers.visibilitychange, true);
                window.removeEventListener("pagehide", this.handlers.pagehide, true);
                window.removeEventListener("pageshow", this.handlers.pageshow, true);
                window.removeEventListener("focus", this.handlers.focus, true);
                window.removeEventListener("blur", this.handlers.blur, true);
                window.removeEventListener("online", this.handlers.online, true);
                window.removeEventListener("offline", this.handlers.offline, true);

                this.installed = false;
                this.handlers = null;
                this.hiddenStartedAtMs = 0;
            }
        };

        Module.RealtimeWebGLVisibilityDiagnostics.install();

        try {
            var url = UTF8ToString(urlPtr);
            var oldSocket = Module.RealtimeWebGLSockets[handle];

            Module.RealtimeWebGLVisibilityDiagnostics.log("socket_connect_requested", {
                handle: String(handle),
                url: Module.RealtimeWebGLVisibilityDiagnostics.sanitizeUrl(url),
                replacingExistingSocket: !!oldSocket
            });

            if (oldSocket) {
                try {
                    oldSocket.onopen = null;
                    oldSocket.onmessage = null;
                    oldSocket.onerror = null;
                    oldSocket.onclose = null;
                    oldSocket.close(1000, "Reconnect cleanup");
                } catch (e) {}
                delete Module.RealtimeWebGLSockets[handle];
            }

            var socket = new WebSocket(url);
            Module.RealtimeWebGLSockets[handle] = socket;

            socket.onopen = function () {
                if (!Module.RealtimeWebGLVisibilityDiagnostics.isCurrentSocket(handle, socket)) return;

                Module.RealtimeWebGLVisibilityDiagnostics.log("socket_open", {
                    handle: String(handle),
                    url: Module.RealtimeWebGLVisibilityDiagnostics.sanitizeUrl(socket.url ? String(socket.url) : url),
                    readyState: socket.readyState
                });
                SendMessage("RealtimeWebGLWebSocketBridge", "HandleWebSocketOpen", String(handle));
            };

            socket.onmessage = function (event) {
                if (!Module.RealtimeWebGLVisibilityDiagnostics.isCurrentSocket(handle, socket)) return;

                var message = event && event.data ? String(event.data) : "";
                SendMessage("RealtimeWebGLWebSocketBridge", "HandleWebSocketMessage", String(handle) + "|" + Module.RealtimeWebGLWebSocketEncode(message));
            };

            socket.onerror = function () {
                if (!Module.RealtimeWebGLVisibilityDiagnostics.isCurrentSocket(handle, socket)) return;

                Module.RealtimeWebGLVisibilityDiagnostics.log("socket_error", {
                    handle: String(handle),
                    url: Module.RealtimeWebGLVisibilityDiagnostics.sanitizeUrl(socket.url ? String(socket.url) : url),
                    readyState: socket.readyState
                });
                SendMessage("RealtimeWebGLWebSocketBridge", "HandleWebSocketError", String(handle) + "|" + Module.RealtimeWebGLWebSocketEncode("Browser WebSocket error"));
            };

            socket.onclose = function (event) {
                if (!Module.RealtimeWebGLVisibilityDiagnostics.isCurrentSocket(handle, socket)) return;

                var reason = event && event.reason ? event.reason : "Browser WebSocket closed";
                var code = event && event.code ? event.code : 0;

                Module.RealtimeWebGLVisibilityDiagnostics.log("socket_close", {
                    handle: String(handle),
                    url: Module.RealtimeWebGLVisibilityDiagnostics.sanitizeUrl(socket.url ? String(socket.url) : url),
                    readyState: socket.readyState,
                    code: code,
                    reason: reason,
                    wasClean: !!(event && event.wasClean)
                });

                Module.RealtimeWebGLVisibilityDiagnostics.clearVisibilityMessages(
                    socket.url ? String(socket.url) : url);

                SendMessage("RealtimeWebGLWebSocketBridge", "HandleWebSocketClose", String(handle) + "|" + String(code) + "|" + Module.RealtimeWebGLWebSocketEncode(reason));
                delete Module.RealtimeWebGLSockets[handle];
            };

            return 1;
        } catch (e) {
            var message = e && e.message ? e.message : "WebSocket connect exception";
            Module.RealtimeWebGLVisibilityDiagnostics.log("socket_connect_exception", {
                handle: String(handle),
                message: message
            });
            SendMessage("RealtimeWebGLWebSocketBridge", "HandleWebSocketError", String(handle) + "|" + Module.RealtimeWebGLWebSocketEncode(message));
            return 0;
        }
    },

    RealtimeWebGLWebSocketConfigureVisibilityMessages: function (urlPtr, hiddenMessagePtr, visibleMessagePtr) {
        try {
            if (!Module.RealtimeWebGLVisibilityDiagnostics) return;

            var url = UTF8ToString(urlPtr);
            var hiddenMessage = UTF8ToString(hiddenMessagePtr);
            var visibleMessage = UTF8ToString(visibleMessagePtr);

            Module.RealtimeWebGLVisibilityDiagnostics.configureVisibilityMessages(
                url,
                hiddenMessage,
                visibleMessage);
        } catch (e) {}
    },

    RealtimeWebGLWebSocketClearVisibilityMessages: function (urlPtr) {
        try {
            if (!Module.RealtimeWebGLVisibilityDiagnostics) return;
            Module.RealtimeWebGLVisibilityDiagnostics.clearVisibilityMessages(
                UTF8ToString(urlPtr));
        } catch (e) {}
    },

    RealtimeWebGLWebSocketGetDocumentHiddenState: function () {
        try {
            if (Module.RealtimeWebGLVisibilityDiagnostics) {
                return Module.RealtimeWebGLVisibilityDiagnostics.getHiddenState() ? 1 : 0;
            }

            return typeof document !== "undefined" && document.hidden === true ? 1 : 0;
        } catch (e) {
            return 0;
        }
    },

    RealtimeWebGLWebSocketGetVisibilityRevision: function () {
        try {
            if (!Module.RealtimeWebGLVisibilityDiagnostics) return 0;
            return Module.RealtimeWebGLVisibilityDiagnostics.getVisibilityRevision();
        } catch (e) {
            return 0;
        }
    },

    RealtimeWebGLWebSocketSend: function (handle, messagePtr) {
        Module.RealtimeWebGLWebSocketEncode = Module.RealtimeWebGLWebSocketEncode || function (value) {
            try { return btoa(unescape(encodeURIComponent(value || ""))); }
            catch (e) { return btoa(value || ""); }
        };

        try {
            var socket = Module.RealtimeWebGLSockets && Module.RealtimeWebGLSockets[handle];
            if (!socket || socket.readyState !== 1) return 0;
            socket.send(UTF8ToString(messagePtr));
            return 1;
        } catch (e) {
            var message = e && e.message ? e.message : "WebSocket send exception";
            if (Module.RealtimeWebGLVisibilityDiagnostics) {
                Module.RealtimeWebGLVisibilityDiagnostics.log("socket_send_exception", {
                    handle: String(handle),
                    message: message
                });
            }
            SendMessage("RealtimeWebGLWebSocketBridge", "HandleWebSocketError", String(handle) + "|" + Module.RealtimeWebGLWebSocketEncode(message));
            return 0;
        }
    },

    RealtimeWebGLWebSocketClose: function (handle, code, reasonPtr) {
        try {
            var socket = Module.RealtimeWebGLSockets && Module.RealtimeWebGLSockets[handle];
            if (!socket) return;
            var reason = UTF8ToString(reasonPtr);

            if (Module.RealtimeWebGLVisibilityDiagnostics) {
                Module.RealtimeWebGLVisibilityDiagnostics.log("socket_close_requested", {
                    handle: String(handle),
                    code: code || 1000,
                    reason: reason || "Client disconnect",
                    readyState: socket.readyState,
                    url: Module.RealtimeWebGLVisibilityDiagnostics.sanitizeUrl(socket.url ? String(socket.url) : "")
                });
            }

            socket.close(code || 1000, reason || "Client disconnect");
        } catch (e) {}
    },

    RealtimeWebGLWebSocketGetReadyState: function (handle) {
        var socket = Module.RealtimeWebGLSockets && Module.RealtimeWebGLSockets[handle];
        return socket ? socket.readyState : 3;
    },

    RealtimeWebGLWebSocketGetBrowserOnlineState: function () {
        try {
            if (typeof navigator === "undefined") return 1;
            return navigator.onLine === false ? 0 : 1;
        } catch (e) {
            return 1;
        }
    },

    RealtimeWebGLWebSocketDispose: function (handle) {
        try {
            var socket = Module.RealtimeWebGLSockets && Module.RealtimeWebGLSockets[handle];
            if (socket) {
                if (Module.RealtimeWebGLVisibilityDiagnostics) {
                    Module.RealtimeWebGLVisibilityDiagnostics.clearVisibilityMessages(
                        socket.url ? String(socket.url) : "");

                    Module.RealtimeWebGLVisibilityDiagnostics.log("socket_dispose", {
                        handle: String(handle),
                        readyState: socket.readyState,
                        url: Module.RealtimeWebGLVisibilityDiagnostics.sanitizeUrl(socket.url ? String(socket.url) : "")
                    });
                }

                socket.onopen = null;
                socket.onmessage = null;
                socket.onerror = null;
                socket.onclose = null;
                if (socket.readyState === 0 || socket.readyState === 1) socket.close(1000, "Disposed");
            }

            if (Module.RealtimeWebGLSockets) delete Module.RealtimeWebGLSockets[handle];
            if (Module.RealtimeWebGLVisibilityDiagnostics) Module.RealtimeWebGLVisibilityDiagnostics.uninstallIfUnused();
        } catch (e) {}
    }
});
