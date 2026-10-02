mergeInto(LibraryManager.library, {
  VoiceRecordingDownloadSaveBase64: function (fileNamePtr, base64Ptr, mimeTypePtr) {
    var fileName = UTF8ToString(fileNamePtr);
    var base64Data = UTF8ToString(base64Ptr);
    var mimeType = UTF8ToString(mimeTypePtr);

    var byteCharacters = atob(base64Data);
    var byteNumbers = new Array(byteCharacters.length);

    for (var i = 0; i < byteCharacters.length; i++) {
      byteNumbers[i] = byteCharacters.charCodeAt(i);
    }

    var byteArray = new Uint8Array(byteNumbers);
    var blob = new Blob([byteArray], { type: mimeType || "audio/ogg" });

    var url = URL.createObjectURL(blob);
    var link = document.createElement("a");

    link.href = url;
    link.download = fileName || "voice-recording.ogg";

    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);

    setTimeout(function () {
      URL.revokeObjectURL(url);
    }, 1000);
  }
});