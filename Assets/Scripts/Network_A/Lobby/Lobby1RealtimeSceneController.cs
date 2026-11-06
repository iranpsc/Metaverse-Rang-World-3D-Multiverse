using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Bootstrap;
using Network_A.Core;
using Network_A.Lobby.Buildings;
using Network_A.Realtime.Controllers;
using Network_A.UI;
using UnityEngine;

namespace Network_A.Lobby
{
    public sealed class Lobby1RealtimeSceneController : MonoBehaviour
    {
        private const string LobbySceneErrorMessageId = "LOBBY_1_REALTIME_SCENE_ERROR";

        [Header("Lobby Start")]
        [SerializeField] private bool enterLobbyAutomatically = true;

        [Header("Room List UI")]
        [SerializeField] private Transform roomListContent;
        [SerializeField] private CompletedBuildingRoomListItemView roomListItemPrefab;

        private readonly List<CompletedBuildingRoomListItemView> roomListItems = new List<CompletedBuildingRoomListItemView>();
        private CancellationTokenSource lifecycleCts;
        private bool startFlowRunning;
        private bool buildingEntryRunning;
        private bool hasRenderedRoomList;
        private string lastRenderedRoomListSignature = string.Empty;

        public CompletedBuildingDto SelectedBuilding { get; private set; }
        public event Action<CompletedBuildingDto> OnBuildingSelected;

        //* این تابع منبع لغو مخصوص عمر همین صحنه را آماده و کامل‌بودن مراجع رابط فهرست روم را بررسی می‌کند.
        private void Awake()
        {
            lifecycleCts = new CancellationTokenSource();
            ValidateRoomListUiReferences();
        }

        //* این تابع رویدادهای مدیر سراسری را هنگام فعال‌شدن رابط همین صحنه دریافت می‌کند.
        private void OnEnable()
        {
            RealtimeRoomGameServerManager.OnBuildingsUpdated += HandleBuildingsUpdated;
            RealtimeRoomGameServerManager.OnStateChanged += HandleRealtimeStateChanged;
        }

        //* این تابع ابتدا جریان ورود لابی را کامل می‌کند و سپس فقط در صورت نیاز فهرست نهایی را نمایش می‌دهد تا داده کش‌شده پیش از دریافت تازه چشمک نزند.
        private async void Start()
        {
            if (!enterLobbyAutomatically)
            {
                RenderCachedRoomListIfAvailable();
                return;
            }

            bool entered = await EnterLobbyAsync();

            // در حالت معمول رویداد OnBuildingsUpdated فهرست را نمایش می‌دهد.
            // این مسیر فقط برای حالتی است که ورود موفق بوده ولی رویداد تازه‌ای منتشر نشده است.
            if (entered && !hasRenderedRoomList) RenderCachedRoomListIfAvailable();
        }

        //* این تابع رویدادهای مدیر سراسری را هنگام غیرفعال‌شدن رابط همین صحنه آزاد می‌کند.
        private void OnDisable()
        {
            RealtimeRoomGameServerManager.OnBuildingsUpdated -= HandleBuildingsUpdated;
            RealtimeRoomGameServerManager.OnStateChanged -= HandleRealtimeStateChanged;
        }

        //* این تابع هنگام خروج از صحنه فقط عملیات و آیتم‌های متعلق به رابط همین صحنه را آزاد می‌کند و مدیر سراسری را از بین نمی‌برد.
        private void OnDestroy()
        {
            if (lifecycleCts != null)
            {
                if (!lifecycleCts.IsCancellationRequested) lifecycleCts.Cancel();
                lifecycleCts.Dispose();
                lifecycleCts = null;
            }

            roomListItems.Clear();
            hasRenderedRoomList = false;
            lastRenderedRoomListSignature = string.Empty;
            buildingEntryRunning = false;
            SelectedBuilding = null;
            OnBuildingSelected = null;
        }

        //* این تابع عمومی برای شروع یا تلاش دوباره ورود به لابی استفاده می‌شود.
        public async Task<bool> EnterLobbyAsync()
        {
            if (startFlowRunning) return false;
            startFlowRunning = true;

            try
            {
                RealtimeRoomGameServerManager manager = RealtimeRoomGameServerManager.Instance;

                if (manager == null)
                {
                    const string details = "RealtimeRoomGameServerManager is not available in Lobby 1.";
                    NetworkFileLogger.Error("LOBBY_1_REALTIME", details);
                    GlobalMessageManager.ShowError(LobbySceneErrorMessageId, "راه‌اندازی لابی", "مدیر اتصال Realtime در صحنه آماده نیست.", details, 0f, true, GlobalMessageManager.MessageSource.System);
                    return false;
                }

                GlobalMessageManager.Clear(LobbySceneErrorMessageId);
                CancellationToken token = lifecycleCts != null ? lifecycleCts.Token : CancellationToken.None;
                return await manager.EnterLobbyAsync(token);
            }
            catch (OperationCanceledException)
            {
                NetworkFileLogger.Warning("LOBBY_1_REALTIME", "جریان ورود به لابی با خروج از صحنه لغو شد.");
                return false;
            }
            catch (Exception ex)
            {
                NetworkFileLogger.Exception("LOBBY_1_REALTIME", ex);
                GlobalMessageManager.ShowError(LobbySceneErrorMessageId, "راه‌اندازی لابی", "ورود به لابی انجام نشد.", ex.ToString(), 0f, false, GlobalMessageManager.MessageSource.System, true, RetryEnterLobbyAsync);
                return false;
            }
            finally
            {
                startFlowRunning = false;
            }
        }

        //* این تابع دکمه تلاش دوباره احتمالی رابط لابی را به همان جریان اصلی متصل می‌کند.
        public async void RetryEnterLobby()
        {
            await EnterLobbyAsync();
        }

        //* این تابع فراخوان بازگشتی پیام سراسری را به جریان تلاش دوباره قابل انتظار تبدیل می‌کند.
        private async Task RetryEnterLobbyAsync()
        {
            await EnterLobbyAsync();
        }

        //* این تابع کامل‌بودن مراجع دستی Scroll و Prefab را بدون جست‌وجوی خودکار در صحنه بررسی می‌کند.
        private bool ValidateRoomListUiReferences()
        {
            bool valid = true;

            if (roomListContent == null)
            {
                valid = false;
                NetworkFileLogger.Error("LOBBY_1_ROOM_LIST_UI", "مرجع Content اسکرول روم‌ها در بازرس تنظیم نشده است.");
            }

            if (roomListItemPrefab == null)
            {
                valid = false;
                NetworkFileLogger.Error("LOBBY_1_ROOM_LIST_UI", "مرجع Prefab آیتم روم در بازرس تنظیم نشده است.");
            }

            return valid;
        }

        //* این تابع فهرست کامل ساختمان‌های دریافت‌شده را فقط هنگام تغییر واقعی داده‌ها بازسازی می‌کند.
        private void RenderRoomListButtons(IReadOnlyList<CompletedBuildingDto> buildings)
        {
            if (!ValidateRoomListUiReferences()) return;

            if (buildings == null || buildings.Count == 0)
            {
                if (hasRenderedRoomList) ClearRoomListButtons();

                hasRenderedRoomList = false;
                lastRenderedRoomListSignature = string.Empty;
                NetworkFileLogger.Warning("LOBBY_1_ROOM_LIST_UI", "فهرست ساختمان‌ها برای نمایش خالی است.");
                return;
            }

            string nextSignature = BuildRoomListSignature(buildings);

            if (hasRenderedRoomList &&
                string.Equals(lastRenderedRoomListSignature, nextSignature, StringComparison.Ordinal))
            {
                RefreshExistingRoomListState();
                NetworkFileLogger.Info("LOBBY_1_ROOM_LIST_UI", "Room list rebuild skipped because data is unchanged | items=" + roomListItems.Count);
                return;
            }

            ClearRoomListButtons();

            int selectedFeatureId = SelectedBuilding != null ? SelectedBuilding.feature_id : 0;
            bool selectedBuildingStillExists = selectedFeatureId <= 0;
            bool interactable = CanSelectRoomItems();

            for (int i = 0; i < buildings.Count; i++)
            {
                CompletedBuildingDto building = buildings[i];
                if (building == null || !building.HasValidFeatureId()) continue;

                building.Normalize();

                CompletedBuildingRoomListItemView item = Instantiate(roomListItemPrefab, roomListContent);
                item.gameObject.SetActive(true);
                item.Setup(building, HandleRoomListItemClicked);
                item.SetInteractable(interactable);

                bool selected = selectedFeatureId > 0 && building.feature_id == selectedFeatureId;
                item.SetSelected(selected);
                if (selected) selectedBuildingStillExists = true;

                roomListItems.Add(item);
            }

            if (!selectedBuildingStillExists) SelectedBuilding = null;

            hasRenderedRoomList = roomListItems.Count > 0;
            lastRenderedRoomListSignature = hasRenderedRoomList ? nextSignature : string.Empty;

            NetworkFileLogger.Info("LOBBY_1_ROOM_LIST_UI", "Room list rendered | items=" + roomListItems.Count + " | selectedFeatureId=" + (SelectedBuilding != null ? SelectedBuilding.feature_id : 0));
        }

        //* این تابع فهرست کش‌شده مدیر را فقط وقتی هنوز هیچ فهرستی نمایش داده نشده است روی رابط قرار می‌دهد.
        private void RenderCachedRoomListIfAvailable()
        {
            RealtimeRoomGameServerManager manager = RealtimeRoomGameServerManager.Instance;
            if (manager == null || manager.CompletedBuildings == null || manager.CompletedBuildings.Count == 0) return;

            RenderRoomListButtons(manager.CompletedBuildings);
        }

        //* این تابع بدون ساخت دوباره آیتم‌ها فقط انتخاب و امکان کلیک فهرست موجود را تازه می‌کند.
        private void RefreshExistingRoomListState()
        {
            int selectedFeatureId = SelectedBuilding != null ? SelectedBuilding.feature_id : 0;
            bool selectedBuildingStillExists = selectedFeatureId <= 0;
            bool interactable = CanSelectRoomItems();

            for (int i = 0; i < roomListItems.Count; i++)
            {
                CompletedBuildingRoomListItemView item = roomListItems[i];
                if (item == null) continue;

                bool selected = selectedFeatureId > 0 && item.MatchesFeatureId(selectedFeatureId);
                item.SetSelected(selected);
                item.SetInteractable(interactable);
                if (selected) selectedBuildingStillExists = true;
            }

            if (!selectedBuildingStillExists) SelectedBuilding = null;
        }

        //* این تابع از داده‌های مؤثر فهرست یک امضای پایدار می‌سازد تا رندر تکراری همان داده تشخیص داده شود.
        private static string BuildRoomListSignature(IReadOnlyList<CompletedBuildingDto> buildings)
        {
            if (buildings == null || buildings.Count == 0) return string.Empty;

            StringBuilder builder = new StringBuilder(buildings.Count * 48);

            for (int i = 0; i < buildings.Count; i++)
            {
                CompletedBuildingDto building = buildings[i];

                if (building == null)
                {
                    builder.Append("null;");
                    continue;
                }

                builder.Append(building.feature_id).Append('|');
                builder.Append(building.feature_properties_id ?? string.Empty).Append('|');
                builder.Append(building.width ?? string.Empty).Append('|');
                builder.Append(building.length ?? string.Empty).Append(';');
            }

            return builder.ToString();
        }

        //* این تابع همه آیتم‌های قبلی را همان لحظه غیرفعال و سپس نابود می‌کند تا نمونه‌های قدیمی و جدید در یک فریم روی هم دیده نشوند.
        private void ClearRoomListButtons()
        {
            for (int i = 0; i < roomListItems.Count; i++)
            {
                CompletedBuildingRoomListItemView item = roomListItems[i];
                if (item != null) item.gameObject.SetActive(false);
            }

            roomListItems.Clear();
            if (roomListContent == null) return;

            for (int i = roomListContent.childCount - 1; i >= 0; i--)
            {
                Transform child = roomListContent.GetChild(i);
                if (child == null) continue;

                child.gameObject.SetActive(false);
                Destroy(child.gameObject);
            }
        }

        //* این تابع امکان انتخاب همه آیتم‌های موجود را با وضعیت اتصال و شبکه هماهنگ می‌کند.
        private void SetRoomListInteractable(bool value)
        {
            for (int i = 0; i < roomListItems.Count; i++)
            {
                if (roomListItems[i] != null) roomListItems[i].SetInteractable(value);
            }
        }

        //* این تابع کلیک آیتم را بدون تبدیل فراخوان دکمه به تابع قابل انتظار، به جریان ورود روم و گیم سرور تحویل می‌دهد.
        private void HandleRoomListItemClicked(CompletedBuildingDto building)
        {
            _ = EnterSelectedBuildingAsync(building);
        }

        //* این تابع ساختمان کلیک‌شده را انتخاب می‌کند، ورود روم ریل‌تایم را آغاز می‌کند و اتصال گیم سرور را به Binder واگذار می‌کند.
        private async Task EnterSelectedBuildingAsync(CompletedBuildingDto building)
        {
            if (building == null || !building.HasValidFeatureId()) return;
            if (!CanSelectRoomItems())
            {
                NetworkFileLogger.Warning("LOBBY_1_ROOM_SELECTED", "انتخاب ساختمان نادیده گرفته شد چون لابی آماده نیست یا ورود قبلی هنوز ادامه دارد.");
                return;
            }

            buildingEntryRunning = true;
            building.Normalize();
            SelectedBuilding = building;
            SetRoomListInteractable(false);

            for (int i = 0; i < roomListItems.Count; i++)
            {
                CompletedBuildingRoomListItemView item = roomListItems[i];
                if (item != null) item.SetSelected(item.MatchesFeatureId(building.feature_id));
            }

            NetworkFileLogger.Info("LOBBY_1_ROOM_SELECTED", "featureId=" + building.feature_id + " | buildingCode=" + building.feature_properties_id + " | width=" + building.width + " | length=" + building.length);
            NotifyBuildingSelected(building);

            try
            {
                RealtimeRoomGameServerManager manager = RealtimeRoomGameServerManager.Instance;
                if (manager == null)
                {
                    NetworkFileLogger.Error("LOBBY_1_ROOM_ENTRY", "RealtimeRoomGameServerManager is not available.");
                    return;
                }

                CancellationToken token = lifecycleCts != null ? lifecycleCts.Token : CancellationToken.None;
                bool joined = await manager.EnterBuildingRoomAsync(building, token);
                NetworkFileLogger.Info("LOBBY_1_ROOM_ENTRY", "joined=" + joined + " | buildingCode=" + building.feature_properties_id + " | roomId=" + manager.CurrentRoomId);
            }
            catch (OperationCanceledException)
            {
                NetworkFileLogger.Warning("LOBBY_1_ROOM_ENTRY", "ورود به روم با خروج از صحنه لغو شد.");
            }
            catch (Exception ex)
            {
                NetworkFileLogger.Exception("LOBBY_1_ROOM_ENTRY", ex);
            }
            finally
            {
                buildingEntryRunning = false;
                SetRoomListInteractable(CanSelectRoomItems());
            }
        }

        //* این تابع رویداد انتخاب ساختمان را بدون متوقف‌کردن جریان ورود روم اجرا می‌کند.
        private void NotifyBuildingSelected(CompletedBuildingDto building)
        {
            Action<CompletedBuildingDto> handler = OnBuildingSelected;
            if (handler == null) return;

            try
            {
                handler(building);
            }
            catch (Exception ex)
            {
                NetworkFileLogger.Exception("LOBBY_1_ROOM_SELECTED_EVENT", ex);
            }
        }

        //* این تابع فهرست تازه ساختمان‌ها را از مدیر سراسری دریافت و در Scroll نمایش می‌دهد.
        private void HandleBuildingsUpdated(IReadOnlyList<CompletedBuildingDto> buildings)
        {
            RenderRoomListButtons(buildings);
        }

        //* این تابع هنگام تغییر وضعیت Realtime فقط امکان کلیک آیتم‌های موجود را تغییر می‌دهد و فهرست را بی‌دلیل پاک نمی‌کند.
        private void HandleRealtimeStateChanged(RealtimeRoomGameServerManager.FlowState state)
        {
            SetRoomListInteractable(CanSelectRoomItems());
        }

        //* این تابع بررسی می‌کند شبکه، احراز هویت Realtime و داده‌های لابی برای انتخاب یک ساختمان آماده باشند.
        #region انتخاب ساختمان از لابی عمومی سه بعدی

        //* این تابع فقط پس از آماده شدن لابی عمومی سه بعدی اجازه انتخاب ساختمان را می دهد.
        private bool CanSelectRoomItems()
        {
            RealtimeRoomGameServerManager manager = RealtimeRoomGameServerManager.Instance;

            if (buildingEntryRunning || manager == null || !manager.IsRealtimeReady) return false;
            if (manager.IsSwitchingFromPublicLobbyToBuildingRoom) return false;
            if (StartupNetworkSceneRouter.Instance == null || !StartupNetworkSceneRouter.IsOnline) return false;

            bool publicLobbyReady =
                manager.IsInsidePublicLobbyRoom &&
                RealtimeRoomGameServerManager.CurrentState == RealtimeRoomGameServerManager.FlowState.RoomJoined;

            bool legacyLobbyReady =
                !manager.IsJoinedRoom &&
                RealtimeRoomGameServerManager.CurrentState == RealtimeRoomGameServerManager.FlowState.LobbyReady;

            return publicLobbyReady || legacyLobbyReady;
        }

        #endregion
    }
}
