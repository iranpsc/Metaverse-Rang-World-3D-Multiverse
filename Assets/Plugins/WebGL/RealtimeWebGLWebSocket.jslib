mergeInto(LibraryManager.library, {
    RealtimeWebGLWebSocketConnect: function (handle, urlPtr) {
        Module.RealtimeWebGLSockets = Module.RealtimeWebGLSockets || {};
        Module.RealtimeWebGLWebSocketEncode = Module.RealtimeWebGLWebSocketEncode || function (value) {
            try { return btoa(unescape(encodeURIComponent(value || ""))); }
            catch (e) { return btoa(value || ""); }
        };

        try {
            var url = UTF8ToString(urlPtr);
            var oldSocket = Module.RealtimeWebGLSockets[handle];

            if (oldSocket) {
                try { oldSocket.close(1000, "Reconnect cleanup"); } catch (e) {}
                delete Module.RealtimeWebGLSockets[handle];
            }

            var socket = new WebSocket(url);
            Module.RealtimeWebGLSockets[handle] = socket;

            socket.onopen = function () {
                SendMessage("RealtimeWebGLWebSocketBridge", "HandleWebSocketOpen", String(handle));
            };

            socket.onmessage = function (event) {
                var message = event && event.data ? String(event.data) : "";
                SendMessage("RealtimeWebGLWebSocketBridge", "HandleWebSocketMessage", String(handle) + "|" + Module.RealtimeWebGLWebSocketEncode(message));
            };

            socket.onerror = function () {
                SendMessage("RealtimeWebGLWebSocketBridge", "HandleWebSocketError", String(handle) + "|" + Module.RealtimeWebGLWebSocketEncode("Browser WebSocket error"));
            };

            socket.onclose = function (event) {
                var reason = event && event.reason ? event.reason : "Browser WebSocket closed";
                var code = event && event.code ? event.code : 0;
                SendMessage("RealtimeWebGLWebSocketBridge", "HandleWebSocketClose", String(handle) + "|" + String(code) + "|" + Module.RealtimeWebGLWebSocketEncode(reason));
                delete Module.RealtimeWebGLSockets[handle];
            };

            return 1;
        } catch (e) {
            var message = e && e.message ? e.message : "WebSocket connect exception";
            SendMessage("RealtimeWebGLWebSocketBridge", "HandleWebSocketError", String(handle) + "|" + Module.RealtimeWebGLWebSocketEncode(message));
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
            SendMessage("RealtimeWebGLWebSocketBridge", "HandleWebSocketError", String(handle) + "|" + Module.RealtimeWebGLWebSocketEncode(message));
            return 0;
        }
    },

    RealtimeWebGLWebSocketClose: function (handle, code, reasonPtr) {
        try {
            var socket = Module.RealtimeWebGLSockets && Module.RealtimeWebGLSockets[handle];
            if (!socket) return;
            var reason = UTF8ToString(reasonPtr);
            socket.close(code || 1000, reason || "Client disconnect");
        } catch (e) {}
    },

    RealtimeWebGLWebSocketGetReadyState: function (handle) {
        var socket = Module.RealtimeWebGLSockets && Module.RealtimeWebGLSockets[handle];
        return socket ? socket.readyState : 3;
    },

    RealtimeWebGLWebSocketDispose: function (handle) {
        try {
            var socket = Module.RealtimeWebGLSockets && Module.RealtimeWebGLSockets[handle];
            if (socket) {
                socket.onopen = null;
                socket.onmessage = null;
                socket.onerror = null;
                socket.onclose = null;
                if (socket.readyState === 0 || socket.readyState === 1) socket.close(1000, "Disposed");
            }
            if (Module.RealtimeWebGLSockets) delete Module.RealtimeWebGLSockets[handle];
        } catch (e) {}
    }
});
