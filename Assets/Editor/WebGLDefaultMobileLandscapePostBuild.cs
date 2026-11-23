#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

public sealed class WebGLDefaultMobileLandscapePostBuild : IPostprocessBuildWithReport
{
    public int callbackOrder => 10000;

    private const string PatchMarker =
        "METARANG_DEFAULT_WEBGL_MOBILE_LANDSCAPE_V2";

    public void OnPostprocessBuild(BuildReport report)
    {
        if (report == null || report.summary.platform != BuildTarget.WebGL)
            return;

        string indexPath =
            Path.Combine(report.summary.outputPath, "index.html");

        if (!File.Exists(indexPath))
        {
            Debug.LogWarning(
                "[MetaRang WebGL] index.html not found: " + indexPath
            );
            return;
        }

        string html = File.ReadAllText(indexPath);

        if (html.Contains(PatchMarker))
        {
            Debug.Log(
                "[MetaRang WebGL] Default-template mobile V2 patch already present."
            );
            return;
        }

        const string headPatch = @"
<!-- METARANG_DEFAULT_WEBGL_MOBILE_LANDSCAPE_V2 -->
<meta name=""mobile-web-app-capable"" content=""yes"">
<meta name=""apple-mobile-web-app-capable"" content=""yes"">
<meta name=""apple-mobile-web-app-status-bar-style"" content=""black-translucent"">
<meta name=""theme-color"" content=""#000000"">

<style id=""metarang-mobile-landscape-v2-style"">
html,
body {
  margin: 0 !important;
  padding: 0 !important;
  width: 100% !important;
  height: 100% !important;
  overflow: hidden !important;
  overscroll-behavior: none;
  background: #000;
}

/*
  مهم:
  قالب Default یونیتی روی دسکتاپ container را با
  left:50%, top:50%, transform:translate(-50%,-50%)
  وسط می‌برد.
  وقتی container را تمام صفحه می‌کنیم باید transform قبلی
  حتماً صفر شود، وگرنه نصف صفحه از viewport بیرون می‌رود.
*/
#unity-container,
#unity-container.unity-desktop,
#unity-container.unity-mobile {
  position: fixed !important;
  left: 0 !important;
  top: 0 !important;
  right: 0 !important;
  bottom: 0 !important;
  inset: 0 !important;
  transform: none !important;
  margin: 0 !important;

  width: 100vw !important;
  height: 100vh !important;
  height: 100dvh !important;

  max-width: none !important;
  max-height: none !important;
  overflow: hidden !important;
  background: #000 !important;
}

#unity-canvas,
#unity-container.unity-desktop #unity-canvas,
#unity-container.unity-mobile #unity-canvas {
  display: block !important;

  width: 100vw !important;
  height: 100vh !important;
  height: 100dvh !important;

  max-width: none !important;
  max-height: none !important;

  margin: 0 !important;
  padding: 0 !important;
  outline: none !important;
}

/* Footer و دکمه Fullscreen پیش‌فرض یونیتی دیگر فضای صفحه را نمی‌گیرند. */
#unity-footer {
  display: none !important;
}

/* دکمه ورود اولیه موبایل */
#metarang-mobile-entry {
  position: fixed;
  z-index: 2147483647;
  inset: 0;

  display: none;
  align-items: center;
  justify-content: center;

  padding: 24px;
  box-sizing: border-box;

  background: #000;
  color: #fff;

  font-family: Tahoma, Arial, sans-serif;
  text-align: center;
}

#metarang-mobile-entry .metarang-card {
  width: min(90vw, 420px);
}

#metarang-mobile-entry button,
#metarang-orientation-warning button {
  width: 100%;
  min-height: 58px;

  border: 0;
  border-radius: 12px;

  background: #fff;
  color: #111;

  font-family: inherit;
  font-size: 18px;
  font-weight: 700;
  cursor: pointer;
}

#metarang-mobile-entry .metarang-hint {
  margin-top: 14px;
  opacity: .78;
  font-size: 13px;
  line-height: 1.9;
}

/* صفحه راهنمای حالت عمودی */
#metarang-orientation-warning {
  position: fixed;
  z-index: 2147483646;
  inset: 0;

  display: none;
  align-items: center;
  justify-content: center;

  padding: 24px;
  box-sizing: border-box;

  background: #111;
  color: #fff;

  font-family: Tahoma, Arial, sans-serif;
  text-align: center;
  font-size: 18px;
  line-height: 1.9;
}

#metarang-orientation-warning .metarang-card {
  width: min(90vw, 420px);
}

#metarang-orientation-warning button {
  margin-top: 18px;
}

/*
  اگر بعد از چرخش گوشی مرورگر از Fullscreen خارج شود،
  بازی دوباره نمایش داده می‌شود و این دکمه کوچک برای ورود مجدد
  به Fullscreen ظاهر می‌شود.
*/
#metarang-fullscreen-reopen {
  position: fixed;
  z-index: 2147483645;

  right: 12px;
  bottom: 12px;

  display: none;

  min-width: 118px;
  min-height: 44px;

  padding: 8px 14px;

  border: 0;
  border-radius: 10px;

  background: rgba(0, 0, 0, .78);
  color: #fff;

  font-family: Tahoma, Arial, sans-serif;
  font-size: 14px;
  font-weight: 700;

  cursor: pointer;
}
</style>
";

        const string bodyPatch = @"
<div id=""metarang-mobile-entry"">
  <div class=""metarang-card"">
    <button id=""metarang-enter-game"" type=""button"">
      ورود تمام‌صفحه به بازی
    </button>

    <div class=""metarang-hint"">
      با ورود به بازی، مرورگر تمام‌صفحه می‌شود و حالت افقی فعال می‌شود.
    </div>
  </div>
</div>

<div id=""metarang-orientation-warning"">
  <div class=""metarang-card"">
    <div>
      این بازی برای حالت افقی طراحی شده است.<br>
      گوشی را افقی کنید.
    </div>

    <button id=""metarang-orientation-fullscreen"" type=""button"">
      بازگشت تمام‌صفحه
    </button>
  </div>
</div>

<button id=""metarang-fullscreen-reopen"" type=""button"">
  تمام‌صفحه
</button>
";

        const string scriptPatch = @"
<script id=""metarang-mobile-landscape-v2-script"">
(function () {
  ""use strict"";

  var mobileEntry =
    document.getElementById(""metarang-mobile-entry"");

  var enterButton =
    document.getElementById(""metarang-enter-game"");

  var orientationWarning =
    document.getElementById(""metarang-orientation-warning"");

  var orientationFullscreenButton =
    document.getElementById(""metarang-orientation-fullscreen"");

  var fullscreenReopenButton =
    document.getElementById(""metarang-fullscreen-reopen"");

  var unityContainer =
    document.getElementById(""unity-container"");

  var unityCanvas =
    document.getElementById(""unity-canvas"");

  var isMobile =
    /Android|iPhone|iPad|iPod|Mobile/i.test(navigator.userAgent) ||
    (
      navigator.maxTouchPoints > 1 &&
      Math.min(screen.width || 0, screen.height || 0) < 1100
    );

  var entryCompleted = false;

  function isFullscreen() {
    return !!(
      document.fullscreenElement ||
      document.webkitFullscreenElement
    );
  }

  function isLandscape() {
    return window.innerWidth > window.innerHeight;
  }

  function updateViewport() {
    var h =
      (window.visualViewport && window.visualViewport.height) ||
      window.innerHeight ||
      document.documentElement.clientHeight;

    var w =
      (window.visualViewport && window.visualViewport.width) ||
      window.innerWidth ||
      document.documentElement.clientWidth;

    if (unityContainer) {
      unityContainer.style.left = ""0px"";
      unityContainer.style.top = ""0px"";
      unityContainer.style.transform = ""none"";

      if (w > 0)
        unityContainer.style.width = w + ""px"";

      if (h > 0)
        unityContainer.style.height = h + ""px"";
    }

    if (unityCanvas) {
      if (w > 0)
        unityCanvas.style.width = w + ""px"";

      if (h > 0)
        unityCanvas.style.height = h + ""px"";
    }
  }

  function refreshMobileUi() {
    if (!isMobile) {
      mobileEntry.style.display = ""none"";
      orientationWarning.style.display = ""none"";
      fullscreenReopenButton.style.display = ""none"";
      return;
    }

    if (!entryCompleted) {
      mobileEntry.style.display = ""flex"";
      orientationWarning.style.display = ""none"";
      fullscreenReopenButton.style.display = ""none"";
      return;
    }

    mobileEntry.style.display = ""none"";

    if (!isLandscape()) {
      orientationWarning.style.display = ""flex"";
      fullscreenReopenButton.style.display = ""none"";
      return;
    }

    /*
      مهم:
      به محض اینکه کاربر گوشی را دوباره افقی کند،
      Overlay عمودی خودکار حذف می‌شود و خود بازی دوباره دیده می‌شود.
    */
    orientationWarning.style.display = ""none"";

    /*
      بعضی مرورگرها هنگام چرخش از Fullscreen خارج می‌شوند.
      Fullscreen دوباره بدون gesture قابل اجبار نیست؛
      بنابراین خود بازی نمایش داده می‌شود و فقط یک دکمه کوچک
      برای بازگشت به Fullscreen نشان داده می‌شود.
    */
    fullscreenReopenButton.style.display =
      isFullscreen() ? ""none"" : ""block"";
  }

  async function requestFullscreenNow() {
    var root = document.documentElement;

    if (isFullscreen())
      return true;

    try {
      if (root.requestFullscreen) {
        await root.requestFullscreen({
          navigationUI: ""hide""
        });

        return true;
      }

      if (root.webkitRequestFullscreen) {
        root.webkitRequestFullscreen();
        return true;
      }
    } catch (error) {
      console.warn(
        ""[MetaRang] Fullscreen request rejected:"",
        error
      );
    }

    return false;
  }

  async function lockLandscapeNow() {
    try {
      if (screen.orientation && screen.orientation.lock) {
        try {
          await screen.orientation.lock(""landscape-primary"");
          return true;
        } catch (_) {
          await screen.orientation.lock(""landscape"");
          return true;
        }
      }
    } catch (error) {
      console.warn(
        ""[MetaRang] Landscape lock rejected:"",
        error
      );
    }

    return false;
  }

  async function requestFullscreenAndLandscape() {
    var fullscreenOk = await requestFullscreenNow();

    var landscapeOk =
      await lockLandscapeNow();

    if (!landscapeOk && fullscreenOk) {
      await new Promise(function (resolve) {
        setTimeout(resolve, 120);
      });

      await lockLandscapeNow();
    }

    updateViewport();
    refreshMobileUi();
  }

  async function enterGame() {
    enterButton.disabled = true;

    await requestFullscreenAndLandscape();

    entryCompleted = true;

    updateViewport();
    refreshMobileUi();
  }

  async function reopenFullscreen() {
    await requestFullscreenAndLandscape();

    updateViewport();
    refreshMobileUi();
  }

  async function handleFullscreenChange() {
    if (
      isMobile &&
      entryCompleted &&
      isFullscreen() &&
      isLandscape()
    ) {
      await lockLandscapeNow();
    }

    updateViewport();
    refreshMobileUi();
  }

  function handleOrientationOrResize() {
    /*
      مرورگرهای موبایل viewport را طی چند فریم عوض می‌کنند.
      دو مرحله refresh می‌کنیم تا Overlay در landscape گیر نکند.
    */
    updateViewport();
    refreshMobileUi();

    setTimeout(function () {
      updateViewport();
      refreshMobileUi();
    }, 120);

    setTimeout(function () {
      updateViewport();
      refreshMobileUi();
    }, 420);
  }

  enterButton.addEventListener(
    ""click"",
    enterGame,
    { once: true }
  );

  orientationFullscreenButton.addEventListener(
    ""click"",
    reopenFullscreen
  );

  fullscreenReopenButton.addEventListener(
    ""click"",
    reopenFullscreen
  );

  document.addEventListener(
    ""fullscreenchange"",
    handleFullscreenChange
  );

  document.addEventListener(
    ""webkitfullscreenchange"",
    handleFullscreenChange
  );

  window.addEventListener(
    ""resize"",
    handleOrientationOrResize
  );

  window.addEventListener(
    ""orientationchange"",
    handleOrientationOrResize
  );

  window.addEventListener(
    ""pageshow"",
    handleOrientationOrResize
  );

  document.addEventListener(
    ""visibilitychange"",
    function () {
      if (!document.hidden)
        handleOrientationOrResize();
    }
  );

  if (window.visualViewport) {
    window.visualViewport.addEventListener(
      ""resize"",
      handleOrientationOrResize
    );
  }

  updateViewport();

  if (isMobile)
    mobileEntry.style.display = ""flex"";

  refreshMobileUi();

  window.MetaRangEnterMobileGame =
    enterGame;

  window.MetaRangRefreshMobileLayout =
    handleOrientationOrResize;
})();
</script>
";

        html = InjectBefore(
            html,
            "</head>",
            headPatch
        );

        html = InjectAfterBodyOpen(
            html,
            bodyPatch
        );

        html = InjectBefore(
            html,
            "</body>",
            scriptPatch
        );

        File.WriteAllText(
            indexPath,
            html
        );

        Debug.Log(
            "[MetaRang WebGL] Default template mobile V2 patch applied | " +
            "desktopTransformNeutralized=true | " +
            "landscapeAutoReturn=true | " +
            "fullscreenReopenButton=true"
        );
    }

    private static string InjectBefore(
        string html,
        string closingTag,
        string patch)
    {
        int index =
            html.IndexOf(
                closingTag,
                StringComparison.OrdinalIgnoreCase
            );

        return index >= 0
            ? html.Insert(
                index,
                patch + Environment.NewLine
              )
            : patch + Environment.NewLine + html;
    }

    private static string InjectAfterBodyOpen(
        string html,
        string patch)
    {
        int bodyStart =
            html.IndexOf(
                "<body",
                StringComparison.OrdinalIgnoreCase
            );

        if (bodyStart < 0)
            return html + Environment.NewLine + patch;

        int bodyEnd =
            html.IndexOf(
                ">",
                bodyStart,
                StringComparison.OrdinalIgnoreCase
            );

        if (bodyEnd < 0)
            return html + Environment.NewLine + patch;

        return html.Insert(
            bodyEnd + 1,
            Environment.NewLine +
            patch +
            Environment.NewLine
        );
    }
}
#endif
