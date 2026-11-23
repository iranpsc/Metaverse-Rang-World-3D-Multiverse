mergeInto(LibraryManager.library, {
    G7WebGL_IsMobileBrowser: function () {
        try {
            var userAgent = String(navigator.userAgent || "");
            var uaMobile = /Android|iPhone|iPad|iPod|Mobile/i.test(userAgent);
            var touchMobile =
                Number(navigator.maxTouchPoints || 0) > 1 &&
                Math.min(Number(screen.width || 0), Number(screen.height || 0)) < 1100;

            return (uaMobile || touchMobile) ? 1 : 0;
        } catch (_) {
            return 0;
        }
    }
});
