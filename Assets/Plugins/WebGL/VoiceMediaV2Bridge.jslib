mergeInto(LibraryManager.library, {
    // این تابع وب سوکت مستقل مسیر رسانه را در مرورگر باز می کند.
    VoiceMediaV2WebGlOpen: function (handle, urlPtr, objectNamePtr) {
        try {
            var url = UTF8ToString(urlPtr);
            var objectName = UTF8ToString(objectNamePtr);
            if (!Module.voiceMediaV2Sockets) Module.voiceMediaV2Sockets = {};
            if (Module.voiceMediaV2Sockets[handle]) return 0;
            var socket = new WebSocket(url);
            socket.binaryType = "arraybuffer";
            Module.voiceMediaV2Sockets[handle] = socket;
            // این تابع باز شدن موفق اتصال مرورگر را به یونیتی اعلام می کند.
            socket.onopen = function () { SendMessage(objectName, "HandleOpen", String(handle)); };
            // این تابع بسته باینری دریافتی را برای تحویل به یونیتی آماده می کند.
            socket.onmessage = function (event) {
                var bytes = new Uint8Array(event.data);
                var binary = "";
                var chunk = 0x8000;
                for (var index = 0; index < bytes.length; index += chunk) binary += String.fromCharCode.apply(null, bytes.subarray(index, Math.min(index + chunk, bytes.length)));
                SendMessage(objectName, "HandlePacket", String(handle) + "|" + btoa(binary));
            };
            // این تابع خطای اتصال مرورگر را به یونیتی اعلام می کند.
            socket.onerror = function () { SendMessage(objectName, "HandleError", String(handle) + "|Voice media WebGL socket error."); };
            // این تابع بسته شدن اتصال مرورگر را به یونیتی اعلام می کند.
            socket.onclose = function (event) { delete Module.voiceMediaV2Sockets[handle]; SendMessage(objectName, "HandleClose", String(handle) + "|" + String(event.reason || "remote_closed")); };
            return 1;
        } catch (error) { return 0; }
    },
    // این تابع بسته باینری را روی اتصال مستقل رسانه ارسال می کند.
    VoiceMediaV2WebGlSend: function (handle, packetPtr, length) {
        var sockets = Module.voiceMediaV2Sockets || {};
        var socket = sockets[handle];
        if (!socket || socket.readyState !== WebSocket.OPEN || length <= 0) return 0;
        var copy = new Uint8Array(length);
        copy.set(HEAPU8.subarray(packetPtr, packetPtr + length));
        socket.send(copy.buffer);
        return 1;
    },
    // این تابع اتصال مستقل رسانه را با علت مشخص می بندد.
    VoiceMediaV2WebGlClose: function (handle, reasonPtr) {
        var sockets = Module.voiceMediaV2Sockets || {};
        var socket = sockets[handle];
        if (!socket) return;
        var reason = UTF8ToString(reasonPtr);
        socket.close(1000, reason.substring(0, 120));
    }
});
