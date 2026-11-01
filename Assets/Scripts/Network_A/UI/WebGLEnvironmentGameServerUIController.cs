using System;
using System.Threading.Tasks;
using Network_A.DedicatedGameServer.Client;
using UnityEngine.SceneManagement;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Network_A.UI
{
    public class WebGLEnvironmentGameServerUIController : MonoBehaviour
    {
        #region رفرنس های رابط کاربری

        [Header("UI References")]
        [SerializeField] private Button disconnectGameServerButton;
        [SerializeField] private TextMeshProUGUI statusText;
        [SerializeField] private TextMeshProUGUI gameServerStateText;

        #endregion

        #region تنظیمات دیباگ

        [Header("Debug")]
        [SerializeField] private bool verboseLogs = true;

        #endregion

        #region متغیرهای داخلی

        private DedicatedGameServerRealtimeRoomBinderWebGL binder;
        private bool manualLobbyExitRunning;
        private bool manualRoomReentryRunning;
        [SerializeField] private Button reloadRoomButton;
        private Button manualRoomReentryButton;
        private float nextReloadButtonStateRefreshTime;

        #endregion

        #region چرخه حیات

        //* این تابع رابط صحنه را در حالت اولیه امن آماده می کند.
        private void Awake()
        {
            if (disconnectGameServerButton != null) disconnectGameServerButton.interactable = false;
        }

        //* این تابع دکمه قطع اتصال و رویدادهای بایندر را فعال می کند.
        private void OnEnable()
        {
            BindDisconnectButton();
            BindManualRoomReentryButton();
            BindBinder();
        }

        //* این تابع پس از آماده شدن کامل صحنه، رفرنس بایندر دائمی را دوباره بررسی می کند.
        private void Start()
        {
            if (binder == null) BindBinder();
        }

        //* این تابع هنگام خروج از صحنه همه رویدادها و دکمه ها را آزاد می کند.
        private void OnDisable()
        {
            UnbindBinder();
            UnbindManualRoomReentryButton();
            UnbindDisconnectButton();
        }

        #endregion

        #region اتصال دکمه

        //* این تابع دکمه قطع اتصال را به تابع محلی وصل می کند.
        private void BindDisconnectButton()
        {
            if (disconnectGameServerButton == null) return;
            disconnectGameServerButton.onClick.RemoveListener(Btn_DisconnectGameServer);
            disconnectGameServerButton.onClick.AddListener(Btn_DisconnectGameServer);
        }

        //* این تابع اتصال دکمه قطع اتصال را پاک می کند.
        private void UnbindDisconnectButton()
        {
            if (disconnectGameServerButton != null) disconnectGameServerButton.onClick.RemoveListener(Btn_DisconnectGameServer);
        }

        // Uses the existing scene button. Never instantiate, re-parent or re-position UI.
        // Bind from Inspector, or resolve the Btn_Reload_Room sibling in MainCanvas.
        private void BindManualRoomReentryButton()
        {
            Button resolved = reloadRoomButton;
            if (resolved == null && disconnectGameServerButton != null)
            {
                Transform parent = disconnectGameServerButton.transform.parent;
                Transform existing = parent != null ? parent.Find("Btn_Reload_Room") : null;
                if (existing != null) resolved = existing.GetComponent<Button>();
            }
            if (resolved == null)
            {
                // The existing button can be nested under MainCanvas in newer scenes.
                // Search exactly once on enable; never create or move a Button.
                GameObject mainCanvas = GameObject.Find("MainCanvas");
                if (mainCanvas != null)
                {
                    Button[] existingButtons = mainCanvas.GetComponentsInChildren<Button>(true);
                    foreach (Button candidate in existingButtons)
                    {
                        if (candidate != null && candidate.name == "Btn_Reload_Room")
                        {
                            resolved = candidate;
                            break;
                        }
                    }
                }
            }
            if (resolved == null)
            {
                Debug.LogWarning("MANUAL_ROOM_RESTART_BUTTON=UNBOUND | reason=Btn_Reload_Room_not_found");
                return;
            }
            if (manualRoomReentryButton != null && manualRoomReentryButton != resolved)
                manualRoomReentryButton.onClick.RemoveListener(Btn_ManualReenterSameRoom);
            manualRoomReentryButton = resolved;
            manualRoomReentryButton.onClick.RemoveListener(Btn_ManualReenterSameRoom);
            manualRoomReentryButton.onClick.AddListener(Btn_ManualReenterSameRoom);
            RefreshManualRoomReentryButtonState();
            Log("MANUAL_ROOM_RESTART_BUTTON=BOUND | object=" + resolved.name);
        }

        private void RefreshManualRoomReentryButtonState()
        {
            if (manualRoomReentryButton == null) return;
            var activeBinder = DedicatedGameServerRealtimeRoomBinderWebGL.Instance;
            var activeRealtime = Network_A.Realtime.Controllers.RealtimeRoomGameServerManager.Instance;
            bool canReload = !manualRoomReentryRunning &&
                !manualLobbyExitRunning && activeBinder != null && activeRealtime != null &&
                activeRealtime.CanManuallyReenterLastRoom && !activeRealtime.IsManualRoomRestartRunning &&
                !activeBinder.IsConnectFlowRunning && !activeBinder.IsDisconnectFlowRunning;
            if (manualRoomReentryButton.interactable != canReload)
                manualRoomReentryButton.interactable = canReload;
        }

        // The Realtime reconnect state can change without a Binder UI availability event.
        // Refresh this one cached Button twice per second, not the scene hierarchy.
        private void Update()
        {
            if (Time.unscaledTime < nextReloadButtonStateRefreshTime) return;
            nextReloadButtonStateRefreshTime = Time.unscaledTime + 0.5f;
            RefreshManualRoomReentryButtonState();
        }

        private void UnbindManualRoomReentryButton()
        {
            if (manualRoomReentryButton != null)
                manualRoomReentryButton.onClick.RemoveListener(Btn_ManualReenterSameRoom);
        }

        #endregion

        #region اتصال بایندر دائمی

        //* این تابع کنترلر رابط صحنه را به نمونه دائمی بایندر متصل می کند.
        private void BindBinder()
        {
            DedicatedGameServerRealtimeRoomBinderWebGL currentBinder = DedicatedGameServerRealtimeRoomBinderWebGL.Instance;

            if (currentBinder == null)
            {
                ApplyMissingBinderState();
                Log("Persistent DedicatedGameServerRealtimeRoomBinderWebGL instance is missing.");
                return;
            }

            if (binder == currentBinder)
            {
                ApplyCurrentBinderState();
                return;
            }

            UnbindBinder();
            binder = currentBinder;
            binder.StatusChanged += HandleStatusChanged;
            binder.GameServerStateChanged += HandleGameServerStateChanged;
            binder.DisconnectAvailabilityChanged += HandleDisconnectAvailabilityChanged;
            ApplyCurrentBinderState();
            Log("Scene UI connected to the persistent DedicatedGameServerRealtimeRoomBinderWebGL.");
        }

        //* این تابع رویدادهای رابط صحنه را از بایندر دائمی جدا می کند.
        private void UnbindBinder()
        {
            if (binder == null) return;
            binder.StatusChanged -= HandleStatusChanged;
            binder.GameServerStateChanged -= HandleGameServerStateChanged;
            binder.DisconnectAvailabilityChanged -= HandleDisconnectAvailabilityChanged;
            binder = null;
        }

        #endregion

        #region نمایش وضعیت

        //* این تابع وضعیت فعلی بایندر را بلافاصله روی رابط صحنه اعمال می کند.
        private void ApplyCurrentBinderState()
        {
            if (binder == null)
            {
                ApplyMissingBinderState();
                return;
            }

            HandleStatusChanged(binder.CurrentStatus);
            HandleGameServerStateChanged(binder.CurrentGameServerState);
            HandleDisconnectAvailabilityChanged(binder.CanDisconnect);
        }

        //* این تابع هنگام نبود بایندر دائمی، رابط صحنه را در حالت امن قرار می دهد.
        private void ApplyMissingBinderState()
        {
            if (disconnectGameServerButton != null) disconnectGameServerButton.interactable = false;
            if (statusText != null) statusText.text = "مدیر اتصال گیم سرور در دسترس نیست.";
            if (gameServerStateText != null) gameServerStateText.text = string.Empty;
        }

        //* این تابع متن وضعیت عمومی اتصال را از بایندر دریافت و نمایش می دهد.
        private void HandleStatusChanged(string value)
        {
            if (statusText != null) statusText.text = Safe(value);
        }

        //* این تابع وضعیت داخل یا خارج بودن گیم سرور را از بایندر دریافت و نمایش می دهد.
        private void HandleGameServerStateChanged(string value)
        {
            if (gameServerStateText != null) gameServerStateText.text = Safe(value);
        }

        //* این تابع فعال یا غیرفعال بودن دکمه قطع اتصال را از بایندر دریافت می کند.
        private void HandleDisconnectAvailabilityChanged(bool canDisconnect)
        {
            if (disconnectGameServerButton != null) disconnectGameServerButton.interactable = canDisconnect;
        }

        #endregion

        #region عملیات دکمه

        //* این تابع کلیک دکمه قطع اتصال را به بایندر دائمی ارسال می کند.
        public async void Btn_DisconnectGameServer()
        {
            // This scene-only handler never changes Realtime, Dedicated, or Voice recovery state.
            // Use the existing coordinated close contract and await its success result.
            if (manualLobbyExitRunning)
            {
                Log("WEBGL_MANUAL_LOBBY_UI=IGNORED | reason=exit_already_running");
                return;
            }

            DedicatedGameServerRealtimeRoomBinderWebGL currentBinder =
                DedicatedGameServerRealtimeRoomBinderWebGL.Instance;
            if (currentBinder == null)
            {
                ApplyMissingBinderState();
                Debug.LogError("WEBGL_MANUAL_LOBBY_UI=FAIL | reason=binder_missing");
                return;
            }

            if (binder != currentBinder) BindBinder();
            if (currentBinder.IsDisconnectFlowRunning)
            {
                Log("WEBGL_MANUAL_LOBBY_UI=IGNORED | reason=disconnect_flow_running");
                return;
            }

            manualLobbyExitRunning = true;
            Log("WEBGL_MANUAL_LOBBY_UI=CLICK | scene=" + SceneManager.GetActiveScene().name);

            try
            {
                bool closed = await currentBinder.DisconnectGameServerAndLeaveRoomAsync(
                    "manual_game_server_disconnect");
                if (!closed)
                {
                    Debug.LogWarning("WEBGL_MANUAL_LOBBY_UI=BLOCKED | reason=coordinated_close_not_confirmed | status=" +
                        Safe(currentBinder.CurrentStatus));
                    return;
                }

                // The Binder normally loads the Lobby itself; this is a UI-only fallback
                // for a confirmed close that was not followed by a scene transition.
                await Task.Yield();
                Scene activeScene = SceneManager.GetActiveScene();
                if (activeScene.IsValid() &&
                    string.Equals(activeScene.name, "Lobby 1 WebGL", StringComparison.Ordinal))
                {
                    Debug.Log("WEBGL_MANUAL_LOBBY_UI=PASS | source=binder | scene=" + activeScene.name);
                    return;
                }

                // Never leave an unclosed Dedicated session behind to force scene loading.
                if (currentBinder.HasUnclosedDedicatedSession)
                {
                    Debug.LogError("WEBGL_MANUAL_LOBBY_UI=BLOCKED | reason=dedicated_session_unclosed");
                    return;
                }

                Debug.Log("WEBGL_MANUAL_LOBBY_UI=FALLBACK | reason=closed_but_not_in_lobby");
                AsyncOperation operation = SceneManager.LoadSceneAsync(
                    "Lobby 1 WebGL", LoadSceneMode.Single);
                if (operation == null)
                {
                    Debug.LogError("WEBGL_MANUAL_LOBBY_UI=FAIL | reason=lobby_load_not_started");
                    return;
                }

                while (!operation.isDone) await Task.Yield();
                await Task.Yield();
                Scene loadedScene = SceneManager.GetActiveScene();
                if (!loadedScene.IsValid() ||
                    !string.Equals(loadedScene.name, "Lobby 1 WebGL", StringComparison.Ordinal))
                    Debug.LogError("WEBGL_MANUAL_LOBBY_UI=FAIL | reason=lobby_scene_mismatch");
                else
                    Debug.Log("WEBGL_MANUAL_LOBBY_UI=PASS | source=ui_fallback");
            }
            catch (Exception error)
            {
                Debug.LogError("WEBGL_MANUAL_LOBBY_UI=EXCEPTION | " + error);
            }
            finally
            {
                manualLobbyExitRunning = false;
            }
        }

        // Unity scene reload only: no browser navigation. The manager calls this ONLY
        // after confirmed Dedicated close and server-side LeaveRoom acknowledgement.
        private static async Task ReloadCurrentGameplaySceneForManualReentryAsync()
        {
            string sceneName = SceneManager.GetActiveScene().name;
            if (!string.Equals(sceneName, "WebGL_Enviroment_Voice", StringComparison.Ordinal))
                throw new InvalidOperationException("Same-room scene reload refused outside the voice gameplay scene: " + sceneName);
            AsyncOperation operation = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
            if (operation == null)
                throw new InvalidOperationException("Unity scene reload could not start: " + sceneName);
            while (!operation.isDone) await Task.Yield();
            await Task.Yield();
            if (!string.Equals(SceneManager.GetActiveScene().name, sceneName, StringComparison.Ordinal))
                throw new InvalidOperationException("Unity scene reload finished in a different scene.");
        }

        // Deliberately separate from Disconnect Game Server and all Voice reload buttons.
        // Explicit LEAVE -> gameplay scene reload -> same room JOIN -> Dedicated auth.
        public async void Btn_ManualReenterSameRoom()
        {
            if (manualRoomReentryRunning || manualLobbyExitRunning) return;
            var realtimeManager = Network_A.Realtime.Controllers.RealtimeRoomGameServerManager.Instance;
            var currentBinder = DedicatedGameServerRealtimeRoomBinderWebGL.Instance;
            if (realtimeManager == null || currentBinder == null || !realtimeManager.CanManuallyReenterLastRoom)
            {
                Debug.LogWarning("MANUAL_ROOM_RESTART_BUTTON=BLOCKED | reason=room_or_manager_missing");
                return;
            }
            if (currentBinder.IsConnectFlowRunning || currentBinder.IsDisconnectFlowRunning ||
                realtimeManager.IsManualRoomRestartRunning)
            {
                Debug.LogWarning("MANUAL_ROOM_RESTART_BUTTON=BLOCKED | reason=another_flow_running");
                return;
            }

            manualRoomReentryRunning = true;
            RefreshManualRoomReentryButtonState();
            Debug.Log("MANUAL_ROOM_RESTART_BUTTON=CLICK | scene=" + SceneManager.GetActiveScene().name);
            try
            {
                bool restarted = await realtimeManager.ManuallyRestartRoomFromBeginningAsync(
                    ReloadCurrentGameplaySceneForManualReentryAsync);
                Debug.Log("MANUAL_ROOM_RESTART_BUTTON=" + (restarted ? "PASS" : "FAILED_OR_PENDING") +
                          " | roomId=" + realtimeManager.CurrentRoomId);
            }
            catch (Exception error)
            {
                Debug.LogError("MANUAL_ROOM_RESTART_BUTTON=ERROR | " + error);
            }
            finally
            {
                manualRoomReentryRunning = false;
                RefreshManualRoomReentryButtonState();
            }
        }

        #endregion

        #region ابزارهای داخلی

        //* این تابع مقدار متنی را برای نمایش امن آماده می کند.
        private static string Safe(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        //* این تابع لاگ های کنترلر رابط صحنه را ثبت می کند.
        private void Log(string message)
        {
            if (verboseLogs) Debug.Log("[WebGLEnvironmentGameServerUIController] " + message);
        }

        #endregion
    }
}
