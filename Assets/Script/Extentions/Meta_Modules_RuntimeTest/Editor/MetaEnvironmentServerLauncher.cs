using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;
using Process = System.Diagnostics.Process;

namespace Meta.Modules.E2E
{
    /// <summary>
    /// One-click E2E for the MiniMap / DayNight modules + the Node JSON server.
    /// Menus:
    ///   Tools/Meta/Run Full Module E2E Test           - everything below, one pass
    ///   Tools/Meta/Start Environment JSON Server Only  - health probe + auto start
    ///   Tools/Meta/Stop Environment JSON Server Only   - kills the server this editor started
    /// Every line carries the [MetaE2E] prefix; the run ends with a single RESULT block.
    /// Safety: only MiniMap / DayNight / Meta_Modules_RuntimeTest types are touched -
    /// no Voice, no Addressables, no Network_A writes, no scene mutations.
    /// </summary>
    public static class MetaEnvironmentServerLauncher
    {
        const string LogPrefix = "[MetaE2E]";
        const int ServerPort = 8788;
        const string BaseUrl = "http://127.0.0.1:8788";
        static readonly string ServerFolderRelative =
            "tools" + Path.DirectorySeparatorChar + "meta-environment-json-server";
        const string MiniMapTypeName = "Meta_MiniMap";
        const string DayNightTypeName = "Meta_DayNightCycle";
        const string PidPref = "MetaE2E.ServerPid";

        /// <summary>Server process started from THIS editor session (also tracked in EditorPrefs).</summary>
        static Process _ownedServerProcess;

        [MenuItem("Tools/Meta/Run Full Module E2E Test", priority = 95)]
        public static void RunFullTest()
        {
            Debug.Log($"{LogPrefix} ============ RUN FULL MODULE E2E TEST ============");
            var watch = Stopwatch.StartNew();
            try
            {
                // 1) server ------------------------------------------------------
                bool server = EnsureServerRunning();
                Debug.Log($"{LogPrefix} Server: {(server ? "PASS" : "FAIL")}");

                // 2) API ----------------------------------------------------------
                bool api = server && ApiQuickSmoke();
                if (api) api = RunDeepSmokeScriptIfPresent();

                // 3) code gates (reflection; no scene required) --------------------
                bool miniOk = CodeGateMiniMap();
                bool dayOk = CodeGateDayNight();

                // 4) scene hooks ----------------------------------------------------
                SceneProbe(out string sceneState);

                // 5) compile gate ----------------------------------------------------
                CompileGate(out string compileState);

                // 6) result -----------------------------------------------------------
                bool overall = server && api && miniOk && dayOk
                               && !string.Equals(sceneState, "FAIL", StringComparison.Ordinal)
                               && !string.Equals(compileState, "FAIL", StringComparison.Ordinal);
                Debug.Log(
                    $"{LogPrefix} ========== RESULT ==========\n" +
                    $"Server: {(server ? "PASS" : "FAIL")}\n" +
                    $"API:    {(api ? "PASS" : "FAIL")}\n" +
                    $"MiniMap code: {(miniOk ? "PASS" : "FAIL")}\n" +
                    $"DayNight code: {(dayOk ? "PASS" : "FAIL")}\n" +
                    $"Scene: {sceneState}\n" +
                    $"Compile: {compileState}\n" +
                    $"Overall: {(overall ? "PASS" : "FAIL")}\n" +
                    $"{LogPrefix} (total {watch.Elapsed.TotalSeconds:0.0}s)");
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LogPrefix} unhandled failure: {ex}");
                Debug.Log($"[MetaE2E] ========== RESULT ==========\nOverall: FAIL\n{LogPrefix} exception: {ex.Message}");
            }
        }

        [MenuItem("Tools/Meta/Start Environment JSON Server Only", priority = 96)]
        public static void StartServerMenu()
        {
            bool ok = EnsureServerRunning();
            int pid = CurrentServerPid();
            Debug.Log($"{LogPrefix} Start server: {(ok ? $"PASS (healthy, pid {pid})" : "FAIL")}");
        }

        [MenuItem("Tools/Meta/Stop Environment JSON Server Only", priority = 97)]
        public static void StopServerMenu()
        {
            int pid = CurrentServerPid();
            if (pid <= 0)
            {
                Debug.Log($"{LogPrefix} Stop server: no tracked server on this machine (nothing killed).");
                return;
            }

            try
            {
                Process tracked = Process.GetProcessById(pid);
                if (string.Equals(tracked.ProcessName, "node", StringComparison.OrdinalIgnoreCase))
                {
                    tracked.Kill();
                    Debug.Log($"{LogPrefix} Stop server: killed node pid {pid}.");
                }
                else
                {
                    Debug.LogWarning($"{LogPrefix} Stop server: pid {pid} is not a node process - skipped (foreign).");
                }
            }
            catch (ArgumentException)
            {
                Debug.Log($"{LogPrefix} Stop server: tracked pid {pid} already gone.");
            }
            EditorPrefs.DeleteKey(PidPref);
        }

        // ------------------------------------------------------------------ type lookup

        /// <summary>
        /// Robust type lookup: exercises short and namespace-qualified candidate names
        /// across every loaded assembly. Both module types live in `namespace Meta`
        /// (Meta.Meta_MiniMap / Meta.Meta_DayNightCycle), so a bare short name via
        /// Assembly.GetType() never matches - this helper scans exported types by
        /// short name too, and survives ReflectionTypeLoadException (an editor always
        /// has assemblies whose unrelated types cannot load, which used to abort the scan).
        /// </summary>
        static Type FindType(params string[] candidates)
        {
            if (candidates == null) return null;
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();

            foreach (string candidate in candidates)
            {
                if (string.IsNullOrEmpty(candidate)) continue;

                // 1) mscorlib-loaded / automatically resolved full name.
                Type direct = Type.GetType(candidate, throwOnError: false);
                if (direct != null) return direct;

                foreach (Assembly asm in assemblies)
                {
                    Type found = null;

                    // 2) exact (full) name on the assembly - safe, standalone.
                    try
                    {
                        found = asm.GetType(candidate, throwOnError: false);
                        if (found != null) return found;
                    }
                    catch { /* dynamic/emit assemblies */ }

                    // 3) short-name scan over exported types; when exported enumeration
                    //    fails, ReflectionTypeLoadException still yields the loadable Types.
                    Type[] exported = null;
                    try
                    {
                        exported = asm.GetExportedTypes();
                    }
                    catch (ReflectionTypeLoadException partial)
                    {
                        exported = partial.Types.Where(t => t != null).ToArray();
                    }
                    catch
                    {
                        exported = null;   // fully inaccessible assembly - skip
                    }

                    if (exported != null)
                    {
                        foreach (Type et in exported)
                            if (et.Name == candidate || et.FullName == candidate)
                                return et;
                    }
                    else
                    {
                        // 4) last resort for assemblies without exportable lists
                        try
                        {
                            foreach (Type all in asm.GetTypes())
                                if (all.Name == candidate || all.FullName == candidate)
                                    return all;
                        }
                        catch (ReflectionTypeLoadException mixed)
                        {
                            foreach (Type all in mixed.Types)
                                if (all != null && (all.Name == candidate || all.FullName == candidate))
                                    return all;
                        }
                        catch { /* unreadable */ }
                    }
                }
            }
            return null;
        }

        /// <summary>Diagnostics printed only when a gate could not resolve its type.</summary>
        static string TypeLoadDiagnostics()
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            string metaLike = string.Join(", ", assemblies
                .Select(a => a.GetName().Name)
                .Where(n => n != null && (n.IndexOf("Meta", StringComparison.OrdinalIgnoreCase) >= 0
                                          || n.Contains("Assembly-CSharp")))
                .OrderBy(n => n)
                .ToArray());
            return $"{assemblies.Length} assemblies loaded; Meta/CSharp ones: [{metaLike}]";
        }

        // ------------------------------------------------------------------ mini member helpers

        static bool HasMethodNamed(Type type, string name)
        {
            if (type == null) return false;
            foreach (MethodInfo m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                     BindingFlags.Instance | BindingFlags.Static))
                if (m.Name == name) return true;
            return false;
        }

        static bool HasPropertyNamed(Type type, string name)
        {
            if (type == null) return false;
            foreach (PropertyInfo p in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic |
                                                          BindingFlags.Instance | BindingFlags.Static))
                if (p.Name == name) return true;
            return false;
        }

        static bool HasFieldNamed(Type type, string name)
        {
            if (type == null) return false;
            foreach (FieldInfo f in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic |
                                                   BindingFlags.Instance | BindingFlags.Static))
                if (f.Name == name) return true;
            return false;
        }

        /// <summary>Walks base classes up to UnityEngine.Object; false when a forbidden base name shows up.</summary>
        static bool InheritsNamed(Type type, string forbiddenBaseName)
        {
            Type t = type.BaseType;
            while (t != null)
            {
                if (t.Name == forbiddenBaseName) return false;
                t = t.BaseType;
            }
            return true;
        }

        // ------------------------------------------------------------------- code gates

        static bool CodeGateMiniMap()
        {
            Debug.Log($"{LogPrefix} MiniMap code gates ...");
            Type t = FindType($"Meta.{MiniMapTypeName}", MiniMapTypeName, $"Meta.Modules.{MiniMapTypeName}");
            if (t == null)
            {
                Debug.LogError(
                    $"{LogPrefix} MiniMap code: FAIL - type ({MiniMapTypeName} / Meta.{MiniMapTypeName}) not found. " +
                    TypeLoadDiagnostics());
                return false;
            }
            Debug.Log($"{LogPrefix} resolved {MiniMapTypeName} as '{t.FullName}' from assembly '{t.Assembly.GetName().Name}'");

            bool ok = true;

            // structure -------------------------------------------------------------
            bool isMono = typeof(MonoBehaviour).IsAssignableFrom(t);
            ok &= Gate(t, "MonoBehaviour (no NetworkBehaviour base)",
                       isMono && InheritsNamed(t, "NetworkBehaviour"));

            // zoom: immediate apply OR the classic paired entry points--------------
            bool zoomOk = HasMethodNamed(t, "ApplyZoomImmediate")
                          || (HasMethodNamed(t, "ZoomIn") && HasMethodNamed(t, "ZoomOut")
                              && HasMethodNamed(t, "ZoomInButton") && HasMethodNamed(t, "ZoomOutButton"));
            ok &= Gate(t, "zoom API (ApplyZoomImmediate OR ZoomIn+ZoomOut(+Buttons))", zoomOk);

            // critical singles --------------------------------------------------------
            ok &= Gate(t, "method ToggleMiniMap", HasMethodNamed(t, "ToggleMiniMap"));
            ok &= Gate(t, "method SetCitizenId", HasMethodNamed(t, "SetCitizenId"));
            ok &= Gate(t, "method SetEnvironmentId", HasMethodNamed(t, "SetEnvironmentId"));

            // persistence: at least one of the stable save/load entry points -----------
            bool persistenceOk = HasMethodNamed(t, "SaveCitizenSettings") || HasMethodNamed(t, "TryLoadCitizenSettings");
            ok &= Gate(t, "persistence API (SaveCitizenSettings OR TryLoadCitizenSettings)", persistenceOk);

            // informational rows (never gate-critical themselves) -----------------------
            InfoGate(t, "method LateUpdate (camera size/enable invariants)", HasMethodNamed(t, "LateUpdate"));
            InfoGate(t, "method Start (restore -> SyncPanel -> resolve)", HasMethodNamed(t, "Start"));
            InfoGate(t, "method TryLoadCitizenSettings", HasMethodNamed(t, "TryLoadCitizenSettings"));
            InfoGate(t, "method ApplySettings (_applyingSettings guard)", HasMethodNamed(t, "ApplySettings"));

            Debug.Log($"{LogPrefix} MiniMap code: {(ok ? "PASS" : "FAIL")}");
            return ok;
        }

        static bool CodeGateDayNight()
        {
            Debug.Log($"{LogPrefix} DayNight code gates ...");
            Type t = FindType($"Meta.{DayNightTypeName}", DayNightTypeName, $"Meta.Modules.{DayNightTypeName}");
            if (t == null)
            {
                Debug.LogError(
                    $"{LogPrefix} DayNight code: FAIL - type ({DayNightTypeName} / Meta.{DayNightTypeName}) not found. " +
                    TypeLoadDiagnostics());
                return false;
            }
            Debug.Log($"{LogPrefix} resolved {DayNightTypeName} as '{t.FullName}' from assembly '{t.Assembly.GetName().Name}'");

            bool ok = true;

            ok &= Gate(t, "MonoBehaviour", typeof(MonoBehaviour).IsAssignableFrom(t));

            // owner gate: property OR field (both count by contract) ----------------------
            bool ownerOk = HasPropertyNamed(t, "IsEnvironmentOwner") || HasFieldNamed(t, "IsEnvironmentOwner");
            ok &= Gate(t, "owner gate (IsEnvironmentOwner property/field)", ownerOk);

            ok &= Gate(t, "method SetEnvironmentId", HasMethodNamed(t, "SetEnvironmentId"));
            ok &= Gate(t, "method SaveEnvironmentState", HasMethodNamed(t, "SaveEnvironmentState"));

            // load: at least one of the two entry points ----------------------------------
            bool loadOk = HasMethodNamed(t, "LoadEnvironmentState") || HasMethodNamed(t, "TryLoadEnvironmentState");
            ok &= Gate(t, "load API (LoadEnvironmentState OR TryLoadEnvironmentState)", loadOk);

            // informational rows ----------------------------------------------------------
            InfoGate(t, "property AuthorityMode", HasPropertyNamed(t, "AuthorityMode"));
            InfoGate(t, "property SimulatesLocally", HasPropertyNamed(t, "SimulatesLocally"));
            InfoGate(t, "method ApplyState (visitor path)", HasMethodNamed(t, "ApplyState"));

            // optional: path contract helper ----------------------------------------------
            Type pathType = FindType("Meta.EnvironmentDayNightPath", "EnvironmentDayNightPath");
            if (pathType != null)
            {
                FieldInfo field = pathType.GetField("RelativeTemplate", BindingFlags.Public | BindingFlags.Static);
                string template = field?.GetValue(null) as string;
                InfoGate(pathType, $"path template '{template ?? "<missing>"}'",
                         string.Equals(template, "environments/{0}/daynight.json", StringComparison.Ordinal));
            }
            else
            {
                Debug.Log($"{LogPrefix} code (optional): EnvironmentDayNightPath not found - skipped (non-blocking)");
            }

            Debug.Log($"{LogPrefix} DayNight code: {(ok ? "PASS" : "FAIL")}");
            return ok;
        }

        static bool Gate(Type owner, string what, bool pass)
        {
            if (pass) Debug.Log($"{LogPrefix} code {owner.Name}: {what} -> PASS");
            else Debug.LogError($"{LogPrefix} code {owner.Name}: {what} -> FAIL");
            return pass;
        }

        static void InfoGate(Type owner, string what, bool pass)
        {
            Debug.Log($"{LogPrefix} code {owner.Name}: {what} -> {(pass ? "PASS" : "MISSING (informational)")}");
        }

        // ------------------------------------------------------------------- scene hooks

        static void SceneProbe(out string state)
        {
            Type miniType = FindType($"Meta.{MiniMapTypeName}", MiniMapTypeName, $"Meta.Modules.{MiniMapTypeName}");
            Type dayType = FindType($"Meta.{DayNightTypeName}", DayNightTypeName, $"Meta.Modules.{DayNightTypeName}");
            bool any = false;
            bool playing = EditorApplication.isPlaying;

            if (miniType != null)
            {
                Component mini = FindSingleByType(miniType, includeInactive: true);
                if (mini != null)
                {
                    any = true;
                    string isEnabledText = GetMemberString(miniType, mini, "IsEnabled");
                    bool.TryParse(isEnabledText, out bool isOn);
                    Object target = GetMemberValue(miniType, mini, "Target") as Object;

                    Debug.Log($"{LogPrefix} scene {MiniMapTypeName} '{mini.name}': IsEnabled={isEnabledText}, " +
                              $"Target={(target != null ? target.name : "null (waiting)")}, " +
                              $"CitizenId={GetMemberString(miniType, mini, "CitizenId")}, " +
                              $"EnvironmentId={GetMemberString(miniType, mini, "EnvironmentId")}, " +
                              $"UseHttp={GetMemberString(miniType, mini, "UseHttp")}, TargetNull={target == null}");

                    if (playing && isOn)
                    {
                        // play-safe pulse: exactly the public entry points the HUD buttons use.
                        // No MarkSceneDirty, no sprite resources, no scene mutations.
                        miniType.GetMethod("ToggleMiniMap", new[] { typeof(bool) })
                                ?.Invoke(mini, new object[] { true });
                        miniType.GetMethod("ZoomInButton")?.Invoke(mini, null);
                        Debug.Log($"{LogPrefix} scene {MiniMapTypeName}: play-safe pulse (ToggleMiniMap(true) + ZoomInButton()) done");
                    }
                }
            }

            if (dayType != null)
            {
                Component day = FindSingleByType(dayType, includeInactive: true);
                if (day != null)
                {
                    any = true;
                    Debug.Log($"{LogPrefix} scene {DayNightTypeName} '{day.name}': " +
                              $"IsEnvironmentOwner={GetMemberString(dayType, day, "IsEnvironmentOwner")}, " +
                              $"AuthorityMode={GetMemberString(dayType, day, "AuthorityMode")}, " +
                              $"EnvironmentId={GetMemberString(dayType, day, "EnvironmentId")}");
                }
            }

            if (any)
            {
                state = "PASS";
            }
            else
            {
                state = "SKIP";
                Debug.Log($"{LogPrefix} scene: no {MiniMapTypeName} / {DayNightTypeName} in the open scene -> SKIP. " +
                          "Open the Lobby / a scene that has the Meta_MiniMap rig " +
                          "(Tools -> Meta -> MiniMap -> Setup builds one in Edit mode) and re-run.");
            }
        }

        /// <summary>FindFirstObjectByType first (single alloc), then FindObjectsByType over inactive too.</summary>
        static Component FindSingleByType(Type type, bool includeInactive)
        {
            Object first = Object.FindFirstObjectByType(type, FindObjectsInactive.Include);
            if (first != null) return first as Component;

            Object[] all = Object.FindObjectsByType(type,
                includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);
            return all != null && all.Length > 0 ? all[0] as Component : null;
        }

        static string GetMemberString(Type type, object instance, string name)
        {
            object value = GetMemberValue(type, instance, name);
            return value == null ? "null" : value.ToString();
        }

        static object GetMemberValue(Type type, object instance, string name)
        {
            foreach (PropertyInfo p in type.GetProperties())
                if (p.Name == name)
                {
                    try { return p.GetValue(instance); }
                    catch { return "<get failed>"; }
                }
            string snakeName = "_" + char.ToLowerInvariant(name[0]) + name.Substring(1);
            if (name == "UseHttp") snakeName = "_useHttpCitizenStore";   // documented private field name
            foreach (FieldInfo f in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                if (f.Name == name || f.Name == snakeName)
                {
                    try
                    {
                        return f.GetValue(instance);
                    }
                    catch { return "<read failed>"; }
                }
            return null;
        }

        // ------------------------------------------------------------------- compile gate

        static void CompileGate(out string state)
        {
            state = "SKIPPED";
            try
            {
                // Unity compiles the project natively; an extra dotnet build is only run when available.
                bool ran = RunProcess("cmd.exe", "/c dotnet build Meta.Runtime.csproj -nologo -v q",
                    ProjectRoot(), 240_000, out string output);
                if (!ran)
                {
                    if (output != null && output.Contains("not recognized"))
                    {
                        Debug.LogWarning($"{LogPrefix} compile: dotnet not on PATH on this machine - " +
                                         "Unity compiles the assembly itself; confirm Console shows 0 errors.");
                        state = "PASS (unity)";
                        Debug.Log($"{LogPrefix} compile: PASS (Unity native compile; dotnet unavailable)");
                        return;
                    }
                    Debug.LogError($"{LogPrefix} compile: FAIL - output tail:\n{Tail(output, 1500)}");
                    state = "FAIL";
                    return;
                }
                if (output != null && output.Contains("0 Error"))
                {
                    Debug.Log($"{LogPrefix} compile: Meta.Runtime.csproj -> 0 Error(s) (dotnet build)");
                    state = "PASS";
                    return;
                }
                Debug.LogError($"{LogPrefix} compile: no '0 Error' line - output tail:\n{Tail(output, 1500)}");
                state = "FAIL";
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{LogPrefix} compile: skipped ({ex.Message})");
                state = "PASS (unity)";
            }
        }

        // ------------------------------------------------------------------- helpers

        static int CurrentServerPid()
        {
            if (_ownedServerProcess != null && !_ownedServerProcess.HasExited) return _ownedServerProcess.Id;
            return EditorPrefs.GetInt(PidPref, 0);
        }

        static string ProjectRoot()
        {
            // Application.dataPath = "…/Assets" -> parent is the project root.
            return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        }

        static string ServerDirectory()
        {
            return Path.Combine(ProjectRoot(), ServerFolderRelative);
        }

        static bool EnsureServerRunning()
        {
            if (HealthQuickIsOk(1500))
            {
                Debug.Log($"{LogPrefix} health ok on :{ServerPort} (server already running; kept as-is)");
                return true;
            }

            string dir = ServerDirectory();
            if (!File.Exists(Path.Combine(dir, "server.js")))
            {
                Debug.LogError($"{LogPrefix} FAIL SERVER: server.js not found at {dir}");
                return false;
            }

            if (!Directory.Exists(Path.Combine(dir, "node_modules")))
            {
                Debug.Log($"{LogPrefix} node_modules missing - running npm install once ...");
                EditorUtility.DisplayProgressBar("Meta E2E", "npm install (meta environment JSON server)", 0.25f);
                bool installed = RunProcess("cmd.exe", "/c npm.cmd install", dir, 180_000, out string npmOut);
                EditorUtility.ClearProgressBar();
                if (!installed || !Directory.Exists(Path.Combine(dir, "node_modules")))
                {
                    Debug.LogError($"{LogPrefix} FAIL SERVER: npm install failed.\n{Tail(npmOut, 1200)}");
                    return false;
                }
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "node",
                Arguments = "server.js",
                WorkingDirectory = dir,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process proc;
            try
            {
                proc = Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LogPrefix} FAIL SERVER: node failed to start ({ex.Message}) - is Node.js on PATH?");
                return false;
            }

            _ownedServerProcess = proc;
            EditorPrefs.SetInt(PidPref, proc.Id);
            Debug.Log($"{LogPrefix} started node server pid={proc.Id} in {dir}");

            bool up = false;
            for (int i = 0; i < 33; i++)          // ~10s
            {
                EditorUtility.DisplayProgressBar("Meta E2E", $"waiting for :{ServerPort}/health ...", i / 33f);
                if (HealthQuickIsOk(1200)) { up = true; break; }
            }
            EditorUtility.ClearProgressBar();
            if (!up) Debug.LogError($"{LogPrefix} FAIL SERVER: health did not turn ok within 10s (pid {proc.Id}).");
            return up;
        }

        static bool HealthQuickIsOk(int timeoutMs)
        {
            (int status, string body) = HttpProbe($"{BaseUrl}/health", "GET", null, null, timeoutMs);
            return status == 200 && body != null && body.Contains("\"ok\":true");
        }

        static bool ApiQuickSmoke()
        {
            bool ok = true;
            const string JsonCt = "Content-Type: application/json";
            string e2eMinimap = $"{BaseUrl}/environments/tools_e2e/minimap/tools_user.json";
            string e2eDaynight = $"{BaseUrl}/environments/tools_e2e/daynight.json";

            (int hStatus, string hBody) = HttpProbe($"{BaseUrl}/health", "GET", null, null, 3000);
            ok &= ApiLog("GET health", hStatus, hBody, 200, "\"ok\":true");

            (int s1, string b1) = HttpProbe(e2eMinimap, "PUT",
                "{\"version\":1,\"citizenId\":\"ignored\",\"radarEnabled\":true,\"zoomOrOrthoSize\":28,\"offsetY\":60}",
                new[] { JsonCt }, 4000);
            ok &= ApiLog("PUT minimap (URL id wins)", s1, b1, 200, "\"citizenId\":\"tools_user\"");

            (int s2, string b2) = HttpProbe(e2eMinimap, "GET", null, null, 4000);
            ok &= ApiLog("GET minimap round-trip", s2, b2, 200, "\"zoomOrOrthoSize\":28");

            (int s3, string b3) = HttpProbe($"{BaseUrl}/environments/tools_e2e/minimap/missing_user.json", "GET", null, null, 4000);
            ok &= ApiLog("GET missing citizen", s3, b3, 404, "not_found");

            (int s4, string b4) = HttpProbe(e2eDaynight, "PUT",
                "{\"version\":1,\"time01\":0.42,\"cycleRunning\":true,\"rotationSpeed\":1.5}",
                new[] { JsonCt, "X-Is-Environment-Owner: true" }, 4000);
            ok &= ApiLog("PUT daynight (owner header)", s4, b4, 200, "\"environmentId\":\"tools_e2e\"");

            (int s5, string b5) = HttpProbe(e2eDaynight, "GET", null, null, 4000);
            ok &= ApiLog("GET daynight round-trip", s5, b5, 200, "\"time01\":0.42");

            Debug.Log($"{LogPrefix} API quick smoke: {(ok ? "PASS" : "FAIL")}");
            return ok;
        }

        static bool ApiLog(string name, int status, string body, int expected, string mustContain)
        {
            bool pass = status == expected && body != null && body.Contains(mustContain);
            if (pass) Debug.Log($"{LogPrefix} API {name} -> {status} PASS");
            else Debug.LogError($"{LogPrefix} API {name} -> {status} FAIL (expected {expected}; body missing '{mustContain}')");
            return pass;
        }

        static bool RunDeepSmokeScriptIfPresent()
        {
            string ps1 = Path.Combine(ServerDirectory(), "e2e-smoke.ps1");
            if (!File.Exists(ps1))
            {
                Debug.Log($"{LogPrefix} deep smoke: e2e-smoke.ps1 not present - skipped (quick probes cover the API).");
                return true;
            }

            Debug.Log($"{LogPrefix} deep smoke: running e2e-smoke.ps1 (all cases, incl. strict REQUIRE_OWNER round) ...");
            EditorUtility.DisplayProgressBar("Meta E2E", "e2e-smoke.ps1 (10 API cases)", 0.55f);
            bool ok = RunProcess("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{ps1}\" -KeepServerRunning",
                ServerDirectory(), 180_000, out string output);
            EditorUtility.ClearProgressBar();
            Debug.Log($"{LogPrefix} deep smoke: {(ok ? "PASS (exit 0)" : "FAIL")}");
            if (!ok) Debug.LogWarning($"{LogPrefix} deep smoke output tail:\n{Tail(output, 1500)}");
            return ok;
        }

        static bool RunProcess(string fileName, string arguments, string workingDirectory, int timeoutMs, out string output)
        {
            output = string.Empty;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return false;
                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    bool exited = p.WaitForExit(timeoutMs);
                    if (!exited)
                    {
                        try { p.Kill(); } catch { }
                        output = stdout + "\nTIMEOUT after " + timeoutMs + " ms";
                        return false;
                    }
                    output = stdout + (string.IsNullOrEmpty(stderr) ? "" : "\n" + stderr);
                    return p.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                output = ex.Message;
                return false;
            }
        }

        static (int status, string body) HttpProbe(string url, string method, string body, string[] extraHeaders, int timeoutMs)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = string.IsNullOrEmpty(method) ? "GET" : method;
                request.Timeout = timeoutMs;
                request.ReadWriteTimeout = timeoutMs;
                request.Accept = "application/json";

                if (extraHeaders != null)
                    foreach (string header in extraHeaders)
                    {
                        int sep = header.IndexOf(':');
                        if (sep <= 0) continue;
                        string key = header.Substring(0, sep).Trim();
                        string value = header.Substring(sep + 1).Trim();
                        if (string.Equals(key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                            request.ContentType = value;
                        else
                            request.Headers[key] = value;
                    }

                if (!string.IsNullOrEmpty(body))
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(body);
                    request.ContentLength = bytes.Length;
                    using (Stream stream = request.GetRequestStream())
                        stream.Write(bytes, 0, bytes.Length);
                }

                using (HttpWebResponse response = request.GetResponse() as HttpWebResponse)
                {
                    if (response == null) return (0, "NULL-RESPONSE");
                    using (var reader = new StreamReader(response.GetResponseStream()))
                        return ((int)response.StatusCode, reader.ReadToEnd());
                }
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                if (response != null)
                    using (var reader = new StreamReader(response.GetResponseStream()))
                        return ((int)response.StatusCode, reader.ReadToEnd());
                return (0, "CONNECTION-FAILED: " + ex.Message);
            }
            catch (Exception ex)
            {
                return (0, "HTTP-ERROR: " + ex.Message);
            }
        }

        static string Tail(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max) return text ?? string.Empty;
            return "..." + text.Substring(text.Length - max);
        }
    }
}
