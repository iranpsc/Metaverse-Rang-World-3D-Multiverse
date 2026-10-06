mergeInto(LibraryManager.library, {
    VoiceWebGlOpen: function (handle, urlPtr, objectNamePtr) {
        try {
            var url = UTF8ToString(urlPtr);
            var objectName = UTF8ToString(objectNamePtr);
            if (!Module.voiceWebGlSockets) Module.voiceWebGlSockets = {};
            if (Module.voiceWebGlSockets[handle]) return 0;
            var socket = new WebSocket(url);
            socket.binaryType = "arraybuffer";
            Module.voiceWebGlSockets[handle] = socket;
            socket.onopen = function () {
                SendMessage(objectName, "HandleOpen", String(handle));
            };
            socket.onmessage = function (event) {
                var bytes = new Uint8Array(event.data);
                var binary = "";
                var chunk = 0x8000;
                for (var index = 0; index < bytes.length; index += chunk) {
                    binary += String.fromCharCode.apply(null, bytes.subarray(index, Math.min(index + chunk, bytes.length)));
                }
                SendMessage(objectName, "HandlePacket", String(handle) + "|" + btoa(binary));
            };
            socket.onerror = function () {
                SendMessage(objectName, "HandleError", String(handle) + "|Voice WebGL socket error.");
            };
            socket.onclose = function (event) {
                delete Module.voiceWebGlSockets[handle];
                SendMessage(objectName, "HandleClose", String(handle) + "|" + String(event.reason || "remote_closed"));
            };
            return 1;
        } catch (error) {
            return 0;
        }
    },

    VoiceWebGlSend: function (handle, packetPtr, length) {
        var sockets = Module.voiceWebGlSockets || {};
        var socket = sockets[handle];
        if (!socket || socket.readyState !== WebSocket.OPEN || length <= 0) return 0;
        var copy = new Uint8Array(length);
        copy.set(HEAPU8.subarray(packetPtr, packetPtr + length));
        socket.send(copy.buffer);
        return 1;
    },

    VoiceWebGlClose: function (handle, reasonPtr) {
        var sockets = Module.voiceWebGlSockets || {};
        var socket = sockets[handle];
        if (!socket) return;
        var reason = UTF8ToString(reasonPtr);
        socket.close(1000, reason.substring(0, 120));
    }
});
