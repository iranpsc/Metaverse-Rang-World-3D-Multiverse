mergeInto(LibraryManager.library, {
    // این تابع سوکت مستقل رسانه فاز نه را در مرورگر باز می کند.
    VME2Phase9WebGLMediaOpen: function (handle, urlPtr, objectNamePtr) {
        try {
            var url = UTF8ToString(urlPtr);
            var objectName = UTF8ToString(objectNamePtr);
            if (!Module.vme2Phase9MediaSockets) Module.vme2Phase9MediaSockets = {};
            if (Module.vme2Phase9MediaSockets[handle]) return 0;

            var socket = new WebSocket(url);
            socket.binaryType = "arraybuffer";
            Module.vme2Phase9MediaSockets[handle] = socket;

            // این تابع باز شدن واقعی سوکت رسانه را به یونیتی اعلام می کند.
            socket.onopen = function () {
                SendMessage(objectName, "HandleOpen", String(handle));
            };

            // این تابع بسته باینری رسانه را برای تحویل به یونیتی به متن انتقالی تبدیل می کند.
            socket.onmessage = function (event) {
                var bytes = new Uint8Array(event.data);
                var binary = "";
                var chunk = 0x8000;

                for (var index = 0; index < bytes.length; index += chunk) {
                    binary += String.fromCharCode.apply(null, bytes.subarray(index, Math.min(index + chunk, bytes.length)));
                }

                SendMessage(objectName, "HandlePacket", String(handle) + "|" + btoa(binary));
            };

            // این تابع خطای سوکت رسانه را به یونیتی اعلام می کند.
            socket.onerror = function () {
                SendMessage(objectName, "HandleError", String(handle) + "|phase9_media_socket_error");
            };

            // این تابع بسته شدن سوکت رسانه را به یونیتی اعلام و نمونه مرورگر را پاک می کند.
            socket.onclose = function (event) {
                delete Module.vme2Phase9MediaSockets[handle];
                SendMessage(objectName, "HandleClose", String(handle) + "|" + String(event.reason || "phase9_media_remote_closed"));
            };

            return 1;
        } catch (error) {
            return 0;
        }
    },

    // این تابع بسته باینری را فقط روی سوکت باز رسانه فاز نه ارسال می کند.
    VME2Phase9WebGLMediaSend: function (handle, packetPtr, length) {
        var sockets = Module.vme2Phase9MediaSockets || {};
        var socket = sockets[handle];

        if (!socket || socket.readyState !== WebSocket.OPEN || length <= 0) return 0;

        var copy = new Uint8Array(length);
        copy.set(HEAPU8.subarray(packetPtr, packetPtr + length));
        socket.send(copy.buffer);
        return 1;
    },

    // این تابع سوکت رسانه فاز نه را با علت کوتاه و معتبر می بندد.
    VME2Phase9WebGLMediaClose: function (handle, reasonPtr) {
        var sockets = Module.vme2Phase9MediaSockets || {};
        var socket = sockets[handle];
        if (!socket) return;

        var reason = UTF8ToString(reasonPtr);
        socket.close(1000, reason.substring(0, 120));
    }
});
