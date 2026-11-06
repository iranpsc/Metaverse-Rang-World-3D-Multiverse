mergeInto(LibraryManager.library, {
  WebGLInputSelectionInstall: function () {
    try {
      if (typeof HTMLInputElement === "undefined") return 0;

      var prototype = HTMLInputElement.prototype;
      if (prototype.__webglSelectionCompatibilityInstalled) return 1;

      var originalSetSelectionRange = prototype.setSelectionRange;
      if (typeof originalSetSelectionRange !== "function") return 0;

      Object.defineProperty(prototype, "setSelectionRange", {
        configurable: true,
        writable: true,
        value: function (start, end, direction) {
          try {
            return originalSetSelectionRange.call(
              this,
              start,
              end,
              direction
            );
          } catch (error) {
            var inputType = String(this.type || "").toLowerCase();
            var unsupportedSelectionType =
              inputType === "email" ||
              inputType === "number" ||
              inputType === "date" ||
              inputType === "time" ||
              inputType === "month" ||
              inputType === "week" ||
              inputType === "color";

            if (
              unsupportedSelectionType &&
              error &&
              error.name === "InvalidStateError"
            ) {
              return;
            }

            throw error;
          }
        }
      });

      Object.defineProperty(
        prototype,
        "__webglSelectionCompatibilityInstalled",
        {
          configurable: false,
          writable: false,
          value: true
        }
      );

      return 1;
    } catch (_error) {
      return 0;
    }
  }
});
