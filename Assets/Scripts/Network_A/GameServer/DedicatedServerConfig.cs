using System;
using System.Collections.Generic;
using UnityEngine;

namespace Network_A.GameServer
{
    public enum DedicatedServerRunMode
    {
        Disabled = 0,
        WindowsEditorTest = 1,
        LinuxHeadlessServer = 2
    }

    [Serializable]
    public class DedicatedServerConfigData
    {
        public DedicatedServerRunMode runMode;
        public string controlBaseUrl;
        public string serverId;
        public string publicHost;
        public int publicPort;
        public string listenHost;
        public int listenPort;
        public string roomId;
        public string roomName;
        public string region;
        public string zone;
        public int maxPlayers;
        public int tickRate;
        public string buildVersion;
        public string serviceToken;
        public float heartbeatIntervalSeconds;
        public bool autoStart;
        public bool verboseLogs;
    }

    public class DedicatedServerConfig : MonoBehaviour
    {
        [Header("Run Mode")]
        [SerializeField] private DedicatedServerRunMode runMode = DedicatedServerRunMode.WindowsEditorTest;
        [SerializeField] private bool autoStart = false;
        [SerializeField] private bool verboseLogs = true;

        [Header("Runtime Overrides")]
        [SerializeField] private bool allowRuntimeOverrides = true;
        [SerializeField] private bool autoStartWhenServerFlagExists = true;
        [SerializeField] private bool logRuntimeOverrideSource = true;

        [Header("Node Game Server Control")]
        [SerializeField] private string controlBaseUrl = "https://dev-world-3d.metarang.com";
        [SerializeField] private string serviceToken = "";

        [Header("Dedicated Identity")]
        [SerializeField] private string serverId = "";
        [SerializeField] private string roomId = "";
        [SerializeField] private string roomName = "";
        [SerializeField] private string region = "eu-central";
        [SerializeField] private string zone = "de-1";

        [Header("Public Connection")]
        [SerializeField] private string publicHost = "127.0.0.1";
        [SerializeField] private int publicPort = 7777;

        [Header("Local Listener")]
        [SerializeField] private string listenHost = "127.0.0.1";
        [SerializeField] private int listenPort = 7777;

        [Header("Capacity")]
        [SerializeField] private int maxPlayers = 20;
        [SerializeField] private int tickRate = 20;
        [SerializeField] private float heartbeatIntervalSeconds = 5f;

        [Header("Build")]
        [SerializeField] private string buildVersion = "dev-build";

        private bool runtimeOverridesApplied;
        private bool runtimeServerStartSignalDetected;
        private string lastRuntimeOverrideSource = string.Empty;

        //* این تابع نسخه خواندنی کانفیگ ددیکیتد سرور را برای بقیه اسکریپت ها می سازد.
        public DedicatedServerConfigData CreateSnapshot()
        {
            ApplyRuntimeOverridesIfNeeded();

            return new DedicatedServerConfigData
            {
                runMode = runMode,
                controlBaseUrl = SafeTrim(controlBaseUrl),
                serverId = SafeTrim(serverId),
                publicHost = SafeTrim(publicHost),
                publicPort = Mathf.Max(1, publicPort),
                listenHost = SafeTrim(listenHost),
                listenPort = Mathf.Max(1, listenPort),
                roomId = SafeTrim(roomId),
                roomName = SafeTrim(roomName),
                region = SafeTrim(region),
                zone = SafeTrim(zone),
                maxPlayers = Mathf.Max(1, maxPlayers),
                tickRate = Mathf.Max(1, tickRate),
                buildVersion = SafeTrim(buildVersion),
                serviceToken = SafeTrim(serviceToken),
                heartbeatIntervalSeconds = Mathf.Max(1f, heartbeatIntervalSeconds),
                autoStart = autoStart,
                verboseLogs = verboseLogs
            };
        }


        public bool ShouldAutoStartRuntime()
        {
            ApplyRuntimeOverridesIfNeeded();

            if (!autoStart) return false;

#if UNITY_EDITOR
            if (runMode == DedicatedServerRunMode.WindowsEditorTest)
            {
                if (verboseLogs) Debug.Log("[DedicatedServerConfig] Auto start allowed for Windows Editor Test. No external server flag is required.");
                return true;
            }

            if (autoStartWhenServerFlagExists && !runtimeServerStartSignalDetected)
            {
                Debug.LogWarning("[DedicatedServerConfig] Auto start blocked in Unity Editor because a real dedicated server start signal was not detected.");
                return false;
            }
#endif

            return true;
        }

        public bool HasRuntimeServerStartSignal()
        {
            ApplyRuntimeOverridesIfNeeded();
            return runtimeServerStartSignalDetected;
        }

        //* این تابع مشخص می کند که ددیکیتد سرور اجازه شروع شدن دارد یا نه.
        public bool CanRunDedicatedServer()
        {
            return runMode != DedicatedServerRunMode.Disabled;
        }

        //* این تابع مشخص می کند که حالت فعلی برای تست داخل ویندوز ادیتور است یا نه.
        public bool IsWindowsEditorTest()
        {
            return runMode == DedicatedServerRunMode.WindowsEditorTest;
        }

        //* این تابع مشخص می کند که حالت فعلی برای بیلد لینوکس هدلس است یا نه.
        public bool IsLinuxHeadlessServer()
        {
            return runMode == DedicatedServerRunMode.LinuxHeadlessServer;
        }

        //* این تابع آدرس رجیستر را از بیس یو آر ال نود جی اس می سازد.
        public string GetRegisterUrl()
        {
            return BuildControlUrl("/game-server-control/dedicated/register");
        }

        //* این تابع آدرس هارت بیت را از بیس یو آر ال نود جی اس می سازد.
        public string GetHeartbeatUrl()
        {
            return BuildControlUrl("/game-server-control/dedicated/heartbeat");
        }

        //* این تابع آدرس وریفای تیکت را از بیس یو آر ال نود جی اس می سازد.
        public string GetVerifyTicketUrl()
        {
            return BuildControlUrl("/game-server-control/dedicated/verify-ticket");
        }

        //* این تابع آدرس کامل مسیرهای گیم سرور کنترل را با حذف اسلش اضافه می سازد.
        public string BuildControlUrl(string path)
        {
            ApplyRuntimeOverridesIfNeeded();

            string safeBase = SafeTrim(controlBaseUrl).TrimEnd('/');
            string safePath = SafeTrim(path);

            if (!safePath.StartsWith("/")) safePath = "/" + safePath;

            return safeBase + safePath;
        }

        //* این تابع کانفیگ را برای شروع فازهای بعدی اعتبارسنجی می کند.
        public bool ValidateForRuntime(out string error)
        {
            DedicatedServerConfigData snapshot = CreateSnapshot();

            if (snapshot.runMode == DedicatedServerRunMode.Disabled)
            {
                error = "Dedicated server run mode is disabled.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(snapshot.controlBaseUrl))
            {
                error = "Control base url is empty.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(snapshot.serverId))
            {
                error = "Server id is empty.";
                return false;
            }

            // Room id and room name are not required for boot.
            // They are assigned later through ticket verification or room binding.

            if (string.IsNullOrWhiteSpace(snapshot.region))
            {
                error = "Region is empty.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(snapshot.zone))
            {
                error = "Zone is empty.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(snapshot.publicHost))
            {
                error = "Public host is empty.";
                return false;
            }

            if (snapshot.publicPort <= 0)
            {
                error = "Public port is invalid.";
                return false;
            }

            if (snapshot.listenPort <= 0)
            {
                error = "Listen port is invalid.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        //* این تابع خلاصه کانفیگ را برای دیباگ داخل کنسول یونیتی آماده می کند.
        public string ToDebugText()
        {
            DedicatedServerConfigData snapshot = CreateSnapshot();

            return "DedicatedServerConfig" +
                   " | runMode=" + snapshot.runMode +
                   " | serverId=" + snapshot.serverId +
                   " | public=" + snapshot.publicHost + ":" + snapshot.publicPort +
                   " | listen=" + snapshot.listenHost + ":" + snapshot.listenPort +
                   " | roomId=" + snapshot.roomId +
                   " | roomName=" + snapshot.roomName +
                   " | region=" + snapshot.region +
                   " | zone=" + snapshot.zone +
                   " | maxPlayers=" + snapshot.maxPlayers +
                   " | tickRate=" + snapshot.tickRate +
                   " | autoStart=" + snapshot.autoStart +
                   " | overrideSource=" + SafeTrim(lastRuntimeOverrideSource);
        }

        public void ApplyRuntimeOverridesIfNeeded()
        {
            if (!allowRuntimeOverrides) return;
            if (runtimeOverridesApplied) return;

            runtimeOverridesApplied = true;

            Dictionary<string, string> args = BuildCommandLineDictionary();
            bool changed = false;
            string value;
            int intValue;
            float floatValue;
            bool boolValue;

            bool hasServerFlag = HasCommandLineFlag(args, "server", "dedicated", "dedicatedserver");
            runtimeServerStartSignalDetected = hasServerFlag || HasAnyEnvironmentValue("METAVERSE_DEDICATED_AUTO_START", "GAME_SERVER_AUTO_START", "METAVERSE_DEDICATED_ROOM_ID", "GAME_ROOM_ID", "ROOM_ID");

            if (TryReadStringSetting(args, out value, new[] { "nodeurl", "controlbaseurl", "controlurl" }, new[] { "METAVERSE_DEDICATED_NODE_URL", "METAVERSE_DEDICATED_CONTROL_BASE_URL", "GAME_SERVER_CONTROL_BASE_URL" })) { controlBaseUrl = value; changed = true; }
            if (TryReadStringSetting(args, out value, new[] { "servicetoken", "service-token" }, new[] { "METAVERSE_DEDICATED_SERVICE_TOKEN", "GAME_SERVER_SERVICE_TOKEN", "SERVICE_TOKEN" })) { serviceToken = value; changed = true; }
            if (TryReadStringSetting(args, out value, new[] { "serverid", "server-id" }, new[] { "METAVERSE_DEDICATED_SERVER_ID", "GAME_SERVER_ID", "SERVER_ID" })) { serverId = value; changed = true; }
            if (TryReadStringSetting(args, out value, new[] { "roomid", "room-id" }, new[] { "METAVERSE_DEDICATED_ROOM_ID", "GAME_ROOM_ID", "ROOM_ID" })) { roomId = value; changed = true; }
            if (TryReadStringSetting(args, out value, new[] { "roomname", "room-name" }, new[] { "METAVERSE_DEDICATED_ROOM_NAME", "GAME_ROOM_NAME", "ROOM_NAME" })) { roomName = value; changed = true; }
            if (TryReadStringSetting(args, out value, new[] { "region" }, new[] { "METAVERSE_DEDICATED_REGION", "GAME_SERVER_REGION", "REGION" })) { region = value; changed = true; }
            if (TryReadStringSetting(args, out value, new[] { "zone" }, new[] { "METAVERSE_DEDICATED_ZONE", "GAME_SERVER_ZONE", "ZONE" })) { zone = value; changed = true; }
            if (TryReadStringSetting(args, out value, new[] { "publichost", "public-host", "host" }, new[] { "METAVERSE_DEDICATED_PUBLIC_HOST", "GAME_SERVER_PUBLIC_HOST", "PUBLIC_HOST" })) { publicHost = value; changed = true; }
            if (TryReadStringSetting(args, out value, new[] { "listenhost", "listen-host", "bindhost", "bind-host" }, new[] { "METAVERSE_DEDICATED_LISTEN_HOST", "GAME_SERVER_LISTEN_HOST", "LISTEN_HOST" })) { listenHost = value; changed = true; }
            if (TryReadStringSetting(args, out value, new[] { "buildversion", "build-version" }, new[] { "METAVERSE_DEDICATED_BUILD_VERSION", "GAME_SERVER_BUILD_VERSION", "BUILD_VERSION" })) { buildVersion = value; changed = true; }

            if (TryReadIntSetting(args, out intValue, new[] { "port" }, new[] { "METAVERSE_DEDICATED_PORT", "GAME_SERVER_PORT", "PORT" })) { publicPort = Mathf.Max(1, intValue); listenPort = Mathf.Max(1, intValue); changed = true; }
            if (TryReadIntSetting(args, out intValue, new[] { "publicport", "public-port" }, new[] { "METAVERSE_DEDICATED_PUBLIC_PORT", "GAME_SERVER_PUBLIC_PORT", "PUBLIC_PORT" })) { publicPort = Mathf.Max(1, intValue); changed = true; }
            if (TryReadIntSetting(args, out intValue, new[] { "listenport", "listen-port", "bindport", "bind-port" }, new[] { "METAVERSE_DEDICATED_LISTEN_PORT", "GAME_SERVER_LISTEN_PORT", "LISTEN_PORT" })) { listenPort = Mathf.Max(1, intValue); changed = true; }
            if (TryReadIntSetting(args, out intValue, new[] { "maxplayers", "max-players" }, new[] { "METAVERSE_DEDICATED_MAX_PLAYERS", "GAME_SERVER_MAX_PLAYERS", "MAX_PLAYERS" })) { maxPlayers = Mathf.Max(1, intValue); changed = true; }
            if (TryReadIntSetting(args, out intValue, new[] { "tickrate", "tick-rate" }, new[] { "METAVERSE_DEDICATED_TICK_RATE", "GAME_SERVER_TICK_RATE", "TICK_RATE" })) { tickRate = Mathf.Max(1, intValue); changed = true; }
            if (TryReadFloatSetting(args, out floatValue, new[] { "heartbeatinterval", "heartbeat-interval" }, new[] { "METAVERSE_DEDICATED_HEARTBEAT_INTERVAL", "GAME_SERVER_HEARTBEAT_INTERVAL" })) { heartbeatIntervalSeconds = Mathf.Max(1f, floatValue); changed = true; }

            if (TryReadBoolSetting(args, out boolValue, new[] { "autostart", "auto-start" }, new[] { "METAVERSE_DEDICATED_AUTO_START", "GAME_SERVER_AUTO_START" })) { autoStart = boolValue; changed = true; }
            if (TryReadBoolSetting(args, out boolValue, new[] { "verboselogs", "verbose-logs" }, new[] { "METAVERSE_DEDICATED_VERBOSE_LOGS", "GAME_SERVER_VERBOSE_LOGS" })) { verboseLogs = boolValue; changed = true; }
            if (TryReadRunModeSetting(args, out DedicatedServerRunMode parsedRunMode)) { runMode = parsedRunMode; changed = true; }

            if (hasServerFlag)
            {
                if (autoStartWhenServerFlagExists) autoStart = true;
                if (runMode == DedicatedServerRunMode.Disabled) runMode = ResolveDefaultServerRunMode();
                changed = true;
            }

            lastRuntimeOverrideSource = changed ? "command_line_or_environment" : "inspector";

            if (changed && logRuntimeOverrideSource)
            {
                Debug.Log("[DedicatedServerConfig] Runtime overrides applied | " + ToDebugTextWithoutReapply());
            }
        }

        public void ApplyRealtimeRoom(string newRoomId, string newRoomName)
        {
            string safeRoomId = SafeTrim(newRoomId);
            string safeRoomName = SafeTrim(newRoomName);

            if (!string.IsNullOrWhiteSpace(safeRoomId)) roomId = safeRoomId;
            if (!string.IsNullOrWhiteSpace(safeRoomName)) roomName = safeRoomName;
        }

        public void ApplyServiceToken(string newServiceToken)
        {
            string safeToken = SafeTrim(newServiceToken);
            if (!string.IsNullOrWhiteSpace(safeToken)) serviceToken = safeToken;
        }

        public void ApplyControlBaseUrl(string newControlBaseUrl)
        {
            string safeUrl = SafeTrim(newControlBaseUrl);
            if (!string.IsNullOrWhiteSpace(safeUrl)) controlBaseUrl = safeUrl;
        }

        public void ApplyServerIdentity(string newServerId)
        {
            string safeServerId = SafeTrim(newServerId);
            if (!string.IsNullOrWhiteSpace(safeServerId)) serverId = safeServerId;
        }

        public void ApplyPorts(int newPublicPort, int newListenPort)
        {
            if (newPublicPort > 0) publicPort = newPublicPort;
            if (newListenPort > 0) listenPort = newListenPort;
        }

        public void ResetRuntimeOverrideCacheForEditorTest()
        {
            runtimeOverridesApplied = false;
            runtimeServerStartSignalDetected = false;
            lastRuntimeOverrideSource = string.Empty;
        }

        private string ToDebugTextWithoutReapply()
        {
            return "serverId=" + SafeTrim(serverId) +
                   " | public=" + SafeTrim(publicHost) + ":" + Mathf.Max(1, publicPort) +
                   " | listen=" + SafeTrim(listenHost) + ":" + Mathf.Max(1, listenPort) +
                   " | roomId=" + SafeTrim(roomId) +
                   " | roomName=" + SafeTrim(roomName) +
                   " | autoStart=" + autoStart;
        }

        private Dictionary<string, string> BuildCommandLineDictionary()
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] args = Environment.GetCommandLineArgs();

            for (int i = 0; i < args.Length; i++)
            {
                string raw = SafeTrim(args[i]);
                if (string.IsNullOrWhiteSpace(raw)) continue;
                if (!raw.StartsWith("-") && !raw.StartsWith("/")) continue;

                string token = raw.TrimStart('-', '/');
                string key = token;
                string value = "true";

                int equalsIndex = token.IndexOf('=');
                if (equalsIndex >= 0)
                {
                    key = token.Substring(0, equalsIndex);
                    value = token.Substring(equalsIndex + 1);
                }
                else if (i + 1 < args.Length)
                {
                    string next = SafeTrim(args[i + 1]);
                    if (!string.IsNullOrWhiteSpace(next) && !next.StartsWith("-") && !next.StartsWith("/"))
                    {
                        value = next;
                        i++;
                    }
                }

                key = NormalizeKey(key);
                if (string.IsNullOrWhiteSpace(key)) continue;

                result[key] = value;
            }

            return result;
        }

        private bool TryReadStringSetting(Dictionary<string, string> args, out string value, string[] commandKeys, string[] environmentKeys)
        {
            value = string.Empty;

            if (TryReadCommandLineValue(args, commandKeys, out value)) return true;
            return TryReadEnvironmentValue(environmentKeys, out value);
        }

        private bool TryReadIntSetting(Dictionary<string, string> args, out int value, string[] commandKeys, string[] environmentKeys)
        {
            value = 0;
            string raw;

            if (!TryReadStringSetting(args, out raw, commandKeys, environmentKeys)) return false;
            return int.TryParse(raw, out value);
        }

        private bool TryReadFloatSetting(Dictionary<string, string> args, out float value, string[] commandKeys, string[] environmentKeys)
        {
            value = 0f;
            string raw;

            if (!TryReadStringSetting(args, out raw, commandKeys, environmentKeys)) return false;
            return float.TryParse(raw, out value);
        }

        private bool TryReadBoolSetting(Dictionary<string, string> args, out bool value, string[] commandKeys, string[] environmentKeys)
        {
            value = false;
            string raw;

            if (!TryReadStringSetting(args, out raw, commandKeys, environmentKeys)) return false;

            if (bool.TryParse(raw, out value)) return true;
            if (raw == "1") { value = true; return true; }
            if (raw == "0") { value = false; return true; }

            return false;
        }

        private bool TryReadRunModeSetting(Dictionary<string, string> args, out DedicatedServerRunMode parsedRunMode)
        {
            parsedRunMode = runMode;
            string raw;

            if (!TryReadStringSetting(args, out raw, new[] { "runmode", "run-mode" }, new[] { "METAVERSE_DEDICATED_RUN_MODE", "GAME_SERVER_RUN_MODE" })) return false;

            int intValue;
            if (int.TryParse(raw, out intValue))
            {
                parsedRunMode = (DedicatedServerRunMode)Mathf.Clamp(intValue, 0, 2);
                return true;
            }

            return Enum.TryParse(raw, true, out parsedRunMode);
        }

        private bool TryReadCommandLineValue(Dictionary<string, string> args, string[] keys, out string value)
        {
            value = string.Empty;
            if (args == null || keys == null) return false;

            for (int i = 0; i < keys.Length; i++)
            {
                string key = NormalizeKey(keys[i]);
                if (!args.TryGetValue(key, out string found)) continue;

                value = SafeTrim(found);
                return !string.IsNullOrWhiteSpace(value);
            }

            return false;
        }

        private bool TryReadEnvironmentValue(string[] keys, out string value)
        {
            value = string.Empty;
            if (keys == null) return false;

            for (int i = 0; i < keys.Length; i++)
            {
                string found = Environment.GetEnvironmentVariable(keys[i]);
                if (string.IsNullOrWhiteSpace(found)) continue;

                value = found.Trim();
                return true;
            }

            return false;
        }


        private bool HasAnyEnvironmentValue(params string[] keys)
        {
            if (keys == null) return false;

            for (int i = 0; i < keys.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(keys[i]))) return true;
            }

            return false;
        }

        private bool HasCommandLineFlag(Dictionary<string, string> args, params string[] keys)
        {
            if (args == null || keys == null) return false;

            for (int i = 0; i < keys.Length; i++)
            {
                if (args.ContainsKey(NormalizeKey(keys[i]))) return true;
            }

            return false;
        }

        private DedicatedServerRunMode ResolveDefaultServerRunMode()
        {
#if UNITY_SERVER || UNITY_STANDALONE_LINUX
            return DedicatedServerRunMode.LinuxHeadlessServer;
#else
            return DedicatedServerRunMode.WindowsEditorTest;
#endif
        }

        private string NormalizeKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return string.Empty;
            return key.Trim().Replace("_", "").Replace("-", "").ToLowerInvariant();
        }

        //* این تابع مقدار رشته را بدون نال و فاصله اضافه برمی گرداند.
        private string SafeTrim(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        /*
        توضیح مکتوب فایل:
        این اسکریپت کانفیگ پایه ددیکیتد سرور یونیتی را نگه می دارد.
        این نسخه علاوه بر مقدارهای اینسپکتور، مقدارهای کامندلاین و انوایرومنت را هم می خواند.
        در فاز 11 ددیکیتد سرور باید بدون روم آی دی و روم نیم اولیه هم بوت شود.
        روم آی دی بعدا از تیکت یا بایند روم مشخص می شود.
        سرویس توکن باید از کامندلاین یا انوایرومنت تزریق شود و داخل بیلد عمومی هاردکد نشود.
        این اسکریپت باید فقط روی آبجکت مخصوص ددیکیتد سرور قرار بگیرد.
        */
    }
}
