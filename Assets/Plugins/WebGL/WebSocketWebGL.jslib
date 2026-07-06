mergeInto(LibraryManager.library, {
  WS_Create: function () {
    if (!window.__wsTable) window.__wsTable = {};
    if (!window.__wsNextId) window.__wsNextId = 1;

    var id = window.__wsNextId++;
    window.__wsTable[id] = { ws: null };
    return id;
  },

  WS_Connect: function (id, urlPtr, headersJsonPtr) {
    var url = UTF8ToString(urlPtr);
    var headersJson = UTF8ToString(headersJsonPtr);

    var entry = window.__wsTable[id];
    if (!entry) return;

    // Browser WebSocket: headers واقعی را نمی‌شود ست کرد.
    // پس اگر نیاز داری token بفرستی، باید داخل querystring یا subprotocol یا پیام اول handshake باشد.
    // اینجا فقط نگه می‌داریم برای سازگاری.
    entry.headersJson = headersJson;

    try {
      var ws = new WebSocket(url);
      entry.ws = ws;

      ws.onopen = function () {
        SendMessage('WebSocketWebGLBridge', 'HandleOpen', id + '|open');
      };

      ws.onmessage = function (evt) {
        SendMessage('WebSocketWebGLBridge', 'HandleMessage', id + '|' + evt.data);
      };

      ws.onerror = function () {
        SendMessage('WebSocketWebGLBridge', 'HandleError', id + '|ws_error');
      };

      ws.onclose = function (evt) {
        var code = evt.code || 1000;
        var reason = evt.reason || 'closed';
        SendMessage('WebSocketWebGLBridge', 'HandleClose', id + '|' + code + '|' + reason);
      };
    } catch (e) {
      SendMessage('WebSocketWebGLBridge', 'HandleError', id + '|' + e.toString());
      SendMessage('WebSocketWebGLBridge', 'HandleClose', id + '|1011|exception');
    }
  },

  WS_Send: function (id, msgPtr) {
    var msg = UTF8ToString(msgPtr);
    var entry = window.__wsTable[id];
    if (!entry || !entry.ws) return;

    try {
      entry.ws.send(msg);
    } catch (e) {
      SendMessage('WebSocketWebGLBridge', 'HandleError', id + '|' + e.toString());
    }
  },

  WS_Close: function (id, code, reasonPtr) {
    var reason = UTF8ToString(reasonPtr);
    var entry = window.__wsTable[id];
    if (!entry || !entry.ws) return;

    try {
      entry.ws.close(code, reason);
    } catch (e) {}
  },

  WS_Free: function (id) {
    var entry = window.__wsTable[id];
    if (!entry) return;

    try {
      if (entry.ws) entry.ws.close(1000, 'free');
    } catch (e) {}

    delete window.__wsTable[id];
  }
});
