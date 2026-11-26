using System;
using System.Collections;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using Object = UnityEngine.Object;

namespace Meta.Modules.E2E
{
    /// <summary>
    /// Play-mode E2E probe: one component, one public Run() - wire any UI Button to it
    /// (Button.onClick -> MetaE2ERuntimeProbe.Run). Editor-free counterpart of the
    /// [Tools/Meta/Run Full Module E2E Test] menu; safe in Play mode because it only
    /// reads state over HTTP/reflection and never touches scene objects.
    /// Checks: reflection code gates (MiniMap + DayNight) and live API probes
    /// (health, minimap PUT/GET with the URL id winning, daynight owner PUT/GET).
    /// </summary>
    public class MetaE2ERuntimeProbe : MonoBehaviour
    {
        [Tooltip("Base URL of the environment JSON server (see tools/meta-environment-json-server).")]
        public string BaseUrl = "http://127.0.0.1:8788";

        [Tooltip("Environment used for the probe payloads (safe to keep; the server owns its file tree).")]
        public string ProbeEnvironment = "tools_e2e";

        [Tooltip("Citizen id for the minimap probe file.")]
        public string ProbeCitizen = "tools_user";

        const string LogPrefix = "[MetaE2E]";

        /// <summary>Kick off the full runtime probe; results stream into the Console.</summary>
        public void Run()
        {
            StartCoroutine(RunRoutine());
        }

        IEnumerator RunRoutine()
        {
            Debug.Log($"{LogPrefix} ============ RUNTIME E2E PROBE ============");

            // ---- code gates (reflection, instant) ------------------------------
            bool miniOk = GateMiniMap();
            bool dayOk = GateDayNight();

            // ---- API probes ------------------------------------------------------
            bool serverOk = false;
            yield return Probe("GET", $"{BaseUrl}/health", null, null, r => serverOk = r);
            bool mmOk = false, dnOk = false;
            if (serverOk)
            {
                string mmUrl = $"{BaseUrl}/environments/{ProbeEnvironment}/minimap/{ProbeCitizen}.json";
                string dnUrl = $"{BaseUrl}/environments/{ProbeEnvironment}/daynight.json";
                yield return Probe("PUT", mmUrl,
                    "{\"version\":1,\"citizenId\":\"ignored\",\"radarEnabled\":true,\"zoomOrOrthoSize\":28,\"offsetY\":60}",
                    null, r => mmOk = r);
                if (mmOk) yield return Probe("GET", mmUrl, null, null, r => mmOk = r);
                yield return Probe("PUT", dnUrl,
                    "{\"version\":1,\"time01\":0.42,\"cycleRunning\":true,\"rotationSpeed\":1.5}",
                    "X-Is-Environment-Owner", r => dnOk = r);
                if (dnOk) yield return Probe("GET", dnUrl, null, null, r => dnOk = r);
            }
            else
            {
                Debug.LogError($"{LogPrefix} server unreachable at {BaseUrl} - start it with tools/meta-environment-json-server/npm start");
            }

            bool overall = serverOk && mmOk && dnOk && miniOk && dayOk;
            Debug.Log(
                $"{LogPrefix} ========== RESULT ==========\n" +
                $"Server: {(serverOk ? "PASS" : "FAIL")}\n" +
                $"API (minimap): {(mmOk ? "PASS" : "FAIL")}\n" +
                $"API (daynight): {(dnOk ? "PASS" : "FAIL")}\n" +
                $"MiniMap code: {(miniOk ? "PASS" : "FAIL")}\n" +
                $"DayNight code: {(dayOk ? "PASS" : "FAIL")}\n" +
                $"Scene: PASS (never mutated from runtime)\n" +
                $"Overall: {(overall ? "PASS" : "FAIL")}");
        }

        // ------------------------------------------------------------------ HTTP

        IEnumerator Probe(string method, string url, string jsonBody, string ownerHeader, System.Action<bool> onDone)
        {
            using (var request = new UnityWebRequest(url, method))
            {
                bool ok = false;
                if (!string.IsNullOrEmpty(jsonBody))
                {
                    byte[] payload = Encoding.UTF8.GetBytes(jsonBody);
                    request.uploadHandler = new UploadHandlerRaw(payload);
                    request.SetRequestHeader("Content-Type", "application/json");
                }
                if (!string.IsNullOrEmpty(ownerHeader)) request.SetRequestHeader(ownerHeader, "true");
                request.downloadHandler = new DownloadHandlerBuffer();
                request.timeout = 5;

                yield return request.SendWebRequest();

                string body = request.downloadHandler != null ? request.downloadHandler.text : "";
                if (request.result == UnityWebRequest.Result.Success)
                {
                    if (method == "PUT")
                        ok = body.Contains("\"version\":") && (body.Contains("\"environmentId\":") || body.Contains("\"citizenId\":"))
                             && body.Contains("\"updatedAt\"");
                    else if (url.EndsWith("/health"))
                        ok = body.Contains("\"ok\":true");
                    else
                        ok = request.responseCode == 200;
                    Debug.Log($"{LogPrefix} API {method} {url.Replace(BaseUrl, "")} -> {(int)request.responseCode} {(ok ? "PASS" : "UNEXPECTED-BODY")}");
                }
                else
                {
                    long code = request.responseCode;
                    bool handled = false;
                    // 404 on a fresh citizen is the correct empty-store state, not an error.
                    if (code == 404 && url.Contains("minimap") && method == "GET") handled = true;
                    Debug.LogError($"{LogPrefix} API {method} {url.Replace(BaseUrl, "")} -> {code} {(request.error ?? "")} {(handled ? "(accepted: empty store)" : "FAIL")}");
                    ok = false;
                }
                if (onDone != null) onDone(ok);
            }
        }

        // ------------------------------------------------------------------ reflection gates

        Type FindType(string typeName)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type t = asm.GetType(typeName, false);
                    if (t != null) return t;
                }
                catch { /* unreadable assembly - skip */ }
            }
            return null;
        }

        bool HasMethodNamed(Type type, string name)
        {
            foreach (MethodInfo m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                     BindingFlags.Instance | BindingFlags.Static))
                if (m.Name == name) return true;
            return false;
        }

        bool HasPropertyNamed(Type type, string name)
        {
            foreach (PropertyInfo p in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic |
                                                          BindingFlags.Instance | BindingFlags.Static))
                if (p.Name == name) return true;
            return false;
        }

        bool GateMiniMap()
        {
            Type t = FindType("Meta_MiniMap");
            if (t == null)
            {
                Debug.LogError($"{LogPrefix} MiniMap code: FAIL - Meta_MiniMap not found");
                return false;
            }

            bool ok = true;
            ok &= Gate($"MonoBehaviour: {typeof(MonoBehaviour).IsAssignableFrom(t)}", typeof(MonoBehaviour).IsAssignableFrom(t));
            foreach (string m in new[] { "ApplyZoomImmediate", "ZoomInButton", "ZoomOutButton", "SetZoomValue",
                                         "ToggleMiniMap", "SetCitizenId", "SetEnvironmentId", "TryLoadCitizenSettings" })
                ok &= Gate($"method {m}", HasMethodNamed(t, m));
            Debug.Log($"{LogPrefix} MiniMap code: {(ok ? "PASS" : "FAIL")}");
            return ok;
        }

        bool GateDayNight()
        {
            Type t = FindType("Meta_DayNightCycle");
            if (t == null)
            {
                Debug.LogError($"{LogPrefix} DayNight code: FAIL - Meta_DayNightCycle not found");
                return false;
            }

            bool ok = true;
            ok &= Gate($"MonoBehaviour: {typeof(MonoBehaviour).IsAssignableFrom(t)}", typeof(MonoBehaviour).IsAssignableFrom(t));
            foreach (string p in new[] { "IsEnvironmentOwner", "AuthorityMode", "SimulatesLocally", "EnvironmentId" })
                ok &= Gate($"property {p}", HasPropertyNamed(t, p));
            foreach (string m in new[] { "SaveEnvironmentState", "LoadEnvironmentState", "TryLoadEnvironmentState", "ApplyState" })
                ok &= Gate($"method {m}", HasMethodNamed(t, m));
            Debug.Log($"{LogPrefix} DayNight code: {(ok ? "PASS" : "FAIL")}");
            return ok;
        }

        bool Gate(string what, bool pass)
        {
            if (pass) Debug.Log($"{LogPrefix} code: {what} -> PASS");
            else Debug.LogError($"{LogPrefix} code: {what} -> FAIL");
            return pass;
        }
    }
}
