using System;
using System.Globalization;
using System.Collections.Generic;
using System.Threading.Tasks;
using Network_A.Realtime.Protocol;
using Network_A.Realtime.Stability;
using Network_A.Tests.Realtime;
using UnityEngine;

public class G7GrpcStreamingRealtime3DMultiplayerController : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private RealtimeGrpcStreamingG7RoomLobbyTestController roomLobbyController;
    [SerializeField] private G7ThreeDModeController threeDModeController;

    [Header("Movement Sync")]
    [SerializeField] private float sendRatePerSecond = 12f;
    [SerializeField] private bool sendOnlyWhenChanged = true;
    [SerializeField] private float minPositionDelta = 0.015f;
    [SerializeField] private float minRotationDelta = 0.75f;

    [Header("State Heartbeat")]
    [SerializeField] private float unchangedStateHeartbeatSeconds = 2f;

    [Header("Snapshot")]
    [SerializeField] private bool requestSnapshotWhen3DStarts = true;
    [SerializeField] private bool requestSnapshotWhenRoomJoins = true;
    [SerializeField] private bool requestSnapshotWhenPlayerJoins = true;

    [Header("Cleanup")]
    [SerializeField] private bool clearRemotePlayersOnLeave = true;
    [SerializeField] private bool clearRemotePlayersOnDisconnect = true;

    [Header("Remote Timeout Cleanup")]
    [SerializeField] private bool removeRemotePlayersWhenStateTimeout = true;
    [SerializeField] private float remotePlayerStateTimeoutSeconds = 8f;
    [SerializeField] private float remoteTimeoutCheckIntervalSeconds = 1f;

    private float nextSendTime;
    private bool hasLastSentState;
    private bool isSendingPlayerState;
    private bool isRequestingSnapshot;
    private float lastStateHeartbeatSendTime;
    private Vector3 lastSentPosition;
    private Quaternion lastSentRotation = Quaternion.identity;
    private float nextRemoteTimeoutCheckTime;
    private readonly Dictionary<string, string> dict_RemoteUserNamesByUserId = new Dictionary<string, string>();
    private readonly Dictionary<string, float> dict_RemoteLastSeenTimeByUserId = new Dictionary<string, float>();

    //* این تابع رفرنس های لازم را پیدا می کند تا اسکریپت از اینسپکتور یا صحنه قابل استفاده باشد.
    private void Awake()
    {
        ResolveReferences();
    }

    //* این تابع ایونت های لابی و حالت سه بعدی را وصل می کند.
    private void OnEnable()
    {
        ResolveReferences();
        BindEvents();
    }

    //* این تابع ایونت ها را قطع می کند تا بعد از Destroy لیسنر تکراری باقی نماند.
    private void OnDisable()
    {
        UnbindEvents();
    }

    //* این تابع هر فریم وضعیت پلیر لوکال را با نرخ محدود برای ریل تایم ارسال می کند.
    private void Update()
    {
        RemoveTimedOutRemotePlayers();

        if (!CanSendLocalPlayerState()) return;

        float safeRate = Mathf.Max(1f, sendRatePerSecond);
        if (Time.unscaledTime < nextSendTime) return;

        Transform localTransform = threeDModeController.GetLocalPlayerTransform();
        if (localTransform == null) return;

        bool heartbeatDue = Time.unscaledTime - lastStateHeartbeatSendTime >= Mathf.Max(1f, unchangedStateHeartbeatSeconds);
        if (sendOnlyWhenChanged && !HasLocalStateChanged(localTransform.position, localTransform.rotation) && !heartbeatDue)
        {
            nextSendTime = Time.unscaledTime + 1f / safeRate;
            return;
        }

        nextSendTime = Time.unscaledTime + 1f / safeRate;
        _ = SendLocalPlayerStateAsync(localTransform.position, localTransform.rotation);
    }

    //* این تابع رفرنس های خالی را از صحنه پیدا می کند.
    private void ResolveReferences()
    {
        if (roomLobbyController == null) roomLobbyController = FindObjectOfType<RealtimeGrpcStreamingG7RoomLobbyTestController>();
        if (threeDModeController == null) threeDModeController = FindObjectOfType<G7ThreeDModeController>();
    }

    //* این تابع همه ایونت های لازم برای اتصال لابی به سه بعدی را وصل می کند.
    private void BindEvents()
    {
        if (roomLobbyController != null)
        {
            roomLobbyController.OnRoomJoinedFor3D -= HandleRoomJoined;
            roomLobbyController.OnRoomLeftFor3D -= HandleRoomLeft;
            roomLobbyController.OnRealtimeDisconnectedFor3D -= HandleRealtimeDisconnected;
            roomLobbyController.OnPlayerJoinedFor3D -= HandlePlayerJoined;
            roomLobbyController.OnPlayerLeftFor3D -= HandlePlayerLeft;
            roomLobbyController.OnPlayerStateReceivedFor3D -= HandlePlayerStateReceived;
            roomLobbyController.OnRoomMembersSnapshotReceivedFor3D -= HandleRoomMembersSnapshotReceived;

            roomLobbyController.OnRoomJoinedFor3D += HandleRoomJoined;
            roomLobbyController.OnRoomLeftFor3D += HandleRoomLeft;
            roomLobbyController.OnRealtimeDisconnectedFor3D += HandleRealtimeDisconnected;
            roomLobbyController.OnPlayerJoinedFor3D += HandlePlayerJoined;
            roomLobbyController.OnPlayerLeftFor3D += HandlePlayerLeft;
            roomLobbyController.OnPlayerStateReceivedFor3D += HandlePlayerStateReceived;
            roomLobbyController.OnRoomMembersSnapshotReceivedFor3D += HandleRoomMembersSnapshotReceived;
        }

        if (threeDModeController != null)
        {
            threeDModeController.OnThreeDModeEntered -= HandleThreeDModeEntered;
            threeDModeController.OnThreeDModeExited -= HandleThreeDModeExited;

            threeDModeController.OnThreeDModeEntered += HandleThreeDModeEntered;
            threeDModeController.OnThreeDModeExited += HandleThreeDModeExited;
        }
    }

    //* این تابع همه ایونت های وصل شده را قطع می کند.
    private void UnbindEvents()
    {
        if (roomLobbyController != null)
        {
            roomLobbyController.OnRoomJoinedFor3D -= HandleRoomJoined;
            roomLobbyController.OnRoomLeftFor3D -= HandleRoomLeft;
            roomLobbyController.OnRealtimeDisconnectedFor3D -= HandleRealtimeDisconnected;
            roomLobbyController.OnPlayerJoinedFor3D -= HandlePlayerJoined;
            roomLobbyController.OnPlayerLeftFor3D -= HandlePlayerLeft;
            roomLobbyController.OnPlayerStateReceivedFor3D -= HandlePlayerStateReceived;
            roomLobbyController.OnRoomMembersSnapshotReceivedFor3D -= HandleRoomMembersSnapshotReceived;
        }

        if (threeDModeController != null)
        {
            threeDModeController.OnThreeDModeEntered -= HandleThreeDModeEntered;
            threeDModeController.OnThreeDModeExited -= HandleThreeDModeExited;
        }
    }

    //* این تابع هنگام ورود به حالت سه بعدی، پلیر لوکال را آماده می کند و اسنپ شات روم را می گیرد.
    private void HandleThreeDModeEntered()
    {
        if (threeDModeController == null) return;

        SyncLocalPlayerNameText();
        threeDModeController.EnsureLocalPlayerSpawned();
        SyncLocalPlayerNameText();
        ResetLastSentState();

        Transform localTransform = threeDModeController.GetLocalPlayerTransform();
        if (localTransform != null) _ = SendLocalPlayerStateAsync(localTransform.position, localTransform.rotation, true);

        if (requestSnapshotWhen3DStarts) _ = RequestRoomMembersSnapshotAsync();
    }

    //* این تابع هنگام خروج از حالت سه بعدی، وضعیت ارسال آخرین حرکت را ریست می کند.
    private void HandleThreeDModeExited()
    {
        ResetLastSentState();
    }

    //* این تابع بعد از جوین موفق، در صورت فعال بودن حالت سه بعدی اسنپ شات اعضای روم را درخواست می کند.
    private void HandleRoomJoined(string roomId)
    {
        ResetLastSentState();
        SyncLocalPlayerNameText();

        if (threeDModeController == null || !threeDModeController.IsThreeDModeActive) return;

        Transform localTransform = threeDModeController.GetLocalPlayerTransform();
        if (localTransform != null) _ = SendLocalPlayerStateAsync(localTransform.position, localTransform.rotation, true);

        if (requestSnapshotWhenRoomJoins) _ = RequestRoomMembersSnapshotAsync();
    }

    //* این تابع هنگام خروج از روم، کلون های ریموت را پاک می کند.
    private void HandleRoomLeft(string roomId)
    {
        ResetLastSentState();
        ClearRemoteUserNames();
        ClearRemoteLastSeenTimes();
        if (clearRemotePlayersOnLeave) threeDModeController?.ClearRemotePlayers();
    }

    //* این تابع هنگام دیسکانکت، کلون های ریموت را پاک می کند.
    private void HandleRealtimeDisconnected(string reason)
    {
        ResetLastSentState();
        ClearRemoteUserNames();
        ClearRemoteLastSeenTimes();
        if (clearRemotePlayersOnDisconnect) threeDModeController?.ClearRemotePlayers();
    }

    //* این تابع وقتی پلیر جدید وارد روم شد، برای گرفتن وضعیت اولیه او اسنپ شات جدید درخواست می کند.
    private void HandlePlayerJoined(string userId, string userName)
    {
        if (IsLocalUser(userId)) return;

        CacheRemoteUserName(userId, userName);

        if (!requestSnapshotWhenPlayerJoins) return;
        if (threeDModeController == null || !threeDModeController.IsThreeDModeActive) return;

        _ = RequestRoomMembersSnapshotAsync();
    }

    //* این تابع وقتی پلیر از روم خارج شد، کلون همان پلیر را حذف می کند.
    private void HandlePlayerLeft(string userId, string userName)
    {
        if (IsLocalUser(userId)) return;
        RemoveRemoteUserName(userId);
        RemoveRemoteLastSeenTime(userId);
        threeDModeController?.RemoveRemotePlayer(userId);
    }

    //* این تابع وضعیت حرکتی دریافتی یک پلیر را به کلون همان پلیر اعمال می کند.
    private void HandlePlayerStateReceived(RealtimeEnvelope envelope)
    {
        if (!CanApplyRemoteState()) return;
        if (!MatchesCurrentRoom(envelope?.room)) return;

        G7PlayerStateEnvelopePayload payload = ReadPayload<G7PlayerStateEnvelopePayload>(envelope.payloadJson);
        if (payload == null || string.IsNullOrWhiteSpace(payload.userId)) return;
        if (IsLocalUser(payload.userId)) return;

        G7PlayerTransformState state = PickTransformState(payload.data, payload.state);
        if (state == null) return;

        MarkRemotePlayerSeen(payload.userId);
        string displayName = ResolveCachedRemoteDisplayName(payload.userId, payload.userName);
        threeDModeController.SpawnOrUpdateRemotePlayer(payload.userId, displayName, state.ToPosition(), state.ToRotation());
    }

    //* این تابع اسنپ شات اعضای روم را می خواند و وضعیت های ذخیره شده پلیرها را به کلون ها اعمال می کند.
    private void HandleRoomMembersSnapshotReceived(RealtimeEnvelope envelope)
    {
        if (!CanApplyRemoteState()) return;
        if (!MatchesCurrentRoom(envelope?.room)) return;

        G7RoomMembersSnapshotPayload snapshot = ReadPayload<G7RoomMembersSnapshotPayload>(envelope.payloadJson);
        if (snapshot == null || snapshot.states == null) return;

        CacheSnapshotMemberNames(snapshot);

        for (int i = 0; i < snapshot.states.Length; i++)
        {
            G7StoredPlayerState storedState = snapshot.states[i];
            if (storedState == null || string.IsNullOrWhiteSpace(storedState.userId)) continue;
            if (IsLocalUser(storedState.userId)) continue;

            CacheRemoteUserName(storedState.userId, storedState.userName);

            G7PlayerTransformState transformState = PickTransformState(storedState.data, storedState.state);
            if (transformState == null) continue;

            MarkRemotePlayerSeen(storedState.userId);
            string displayName = ResolveSnapshotDisplayName(snapshot, storedState.userId, storedState.userName);
            threeDModeController.SpawnOrUpdateRemotePlayer(storedState.userId, displayName, transformState.ToPosition(), transformState.ToRotation());
        }
    }

    //* این تابع بررسی می کند آیا امکان ارسال وضعیت پلیر لوکال وجود دارد یا نه.
    private bool CanSendLocalPlayerState()
    {
        if (isSendingPlayerState) return false;
        if (roomLobbyController == null || threeDModeController == null) return false;
        if (!threeDModeController.IsThreeDModeActive) return false;
        if (!roomLobbyController.IsRealtimeReadyState || !roomLobbyController.IsJoinedRoom) return false;
        if (string.IsNullOrWhiteSpace(roomLobbyController.CurrentRoomId)) return false;
        return true;
    }

    //* این تابع بررسی می کند آیا وضعیت دریافتی ریموت باید روی کلون ها اعمال شود یا نه.
    private bool CanApplyRemoteState()
    {
        if (roomLobbyController == null || threeDModeController == null) return false;
        if (!threeDModeController.IsThreeDModeActive) return false;
        if (!roomLobbyController.IsJoinedRoom) return false;
        return true;
    }

    //* این تابع وضعیت پلیر لوکال را با پیام player_state ارسال می کند.
    private async Task<bool> SendLocalPlayerStateAsync(Vector3 position, Quaternion rotation, bool force = false)
    {
        if (!force && isSendingPlayerState) return false;
        if (roomLobbyController == null) return false;

        isSendingPlayerState = true;

        try
        {
            string payloadJson = BuildPlayerStatePayload(position, rotation);
            RealtimeEnvelope envelope = RealtimeEnvelope.Create(RealtimeChannels.Presence, RealtimeMessageTypes.PlayerState, payloadJson, roomLobbyController.CurrentRoomId, false);
            envelope.id = RealtimeEnvelope.CreateMessageId("player_state");

            bool sent = await roomLobbyController.SendRealtimeEnvelopeAsync(envelope, RealtimeDeliveryPolicy.UnreliableLatestOnly, false);

            if (sent)
            {
                lastSentPosition = position;
                lastSentRotation = rotation;
                lastStateHeartbeatSendTime = Time.unscaledTime;
                hasLastSentState = true;
            }

            return sent;
        }
        finally
        {
            isSendingPlayerState = false;
        }
    }

    //* این تابع از سرور اسنپ شات اعضای زنده روم را درخواست می کند.
    private async Task<bool> RequestRoomMembersSnapshotAsync()
    {
        if (isRequestingSnapshot) return false;
        if (roomLobbyController == null) return false;
        if (!roomLobbyController.IsRealtimeReadyState || !roomLobbyController.IsJoinedRoom) return false;
        if (string.IsNullOrWhiteSpace(roomLobbyController.CurrentRoomId)) return false;

        isRequestingSnapshot = true;

        try
        {
            string payloadJson = BuildRoomMembersRequestPayload();
            RealtimeEnvelope envelope = RealtimeEnvelope.Create(RealtimeChannels.Presence, RealtimeMessageTypes.RoomMembersRequest, payloadJson, roomLobbyController.CurrentRoomId, false);
            envelope.id = RealtimeEnvelope.CreateMessageId("room_members_request");

            return await roomLobbyController.SendRealtimeEnvelopeAsync(envelope, RealtimeDeliveryPolicy.ReliableNoQueue, true);
        }
        finally
        {
            isRequestingSnapshot = false;
        }
    }

    //* این تابع پِیلود جیسون وضعیت پلیر لوکال را برای سرور می سازد.
    private string BuildPlayerStatePayload(Vector3 position, Quaternion rotation)
    {
        return "{"
               + "\"roomId\":\"" + EscapeJson(roomLobbyController.CurrentRoomId) + "\","
               + "\"userId\":\"" + EscapeJson(roomLobbyController.CurrentUserId) + "\","
               + "\"state\":{"
               + "\"px\":" + ToJsonNumber(position.x) + ","
               + "\"py\":" + ToJsonNumber(position.y) + ","
               + "\"pz\":" + ToJsonNumber(position.z) + ","
               + "\"qx\":" + ToJsonNumber(rotation.x) + ","
               + "\"qy\":" + ToJsonNumber(rotation.y) + ","
               + "\"qz\":" + ToJsonNumber(rotation.z) + ","
               + "\"qw\":" + ToJsonNumber(rotation.w)
               + "},"
               + "\"ts\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
               + "}";
    }

    //* این تابع پِیلود درخواست اسنپ شات اعضای روم را می سازد.
    private string BuildRoomMembersRequestPayload()
    {
        return "{"
               + "\"roomId\":\"" + EscapeJson(roomLobbyController.CurrentRoomId) + "\","
               + "\"userId\":\"" + EscapeJson(roomLobbyController.CurrentUserId) + "\","
               + "\"userName\":\"" + EscapeJson(roomLobbyController.CurrentUserName) + "\","
               + "\"ts\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
               + "}";
    }

    //* این تابع بررسی می کند وضعیت پلیر لوکال نسبت به آخرین ارسال تغییر مهم داشته است یا نه.
    private bool HasLocalStateChanged(Vector3 position, Quaternion rotation)
    {
        if (!hasLastSentState) return true;
        if (Vector3.Distance(lastSentPosition, position) >= minPositionDelta) return true;
        return Quaternion.Angle(lastSentRotation, rotation) >= minRotationDelta;
    }

    //* این تابع وضعیت آخرین ارسال را ریست می کند.
    private void ResetLastSentState()
    {
        hasLastSentState = false;
        lastStateHeartbeatSendTime = 0f;
        lastSentPosition = Vector3.zero;
        lastSentRotation = Quaternion.identity;
    }

    //* این تابع بررسی می کند روم پیام با روم فعلی یکی است یا نه.
    private bool MatchesCurrentRoom(string roomId)
    {
        if (roomLobbyController == null) return false;
        if (string.IsNullOrWhiteSpace(roomId)) return true;
        return string.Equals(roomId.Trim(), roomLobbyController.CurrentRoomId?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    //* این تابع بررسی می کند یوزر دریافتی همان یوزر لوکال است یا نه.
    private bool IsLocalUser(string userId)
    {
        if (roomLobbyController == null) return false;
        if (string.IsNullOrWhiteSpace(userId)) return false;
        return string.Equals(userId.Trim(), roomLobbyController.CurrentUserId?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    //* این تابع از بین data و state اولین وضعیت معتبر را برمی گرداند.
    private G7PlayerTransformState PickTransformState(G7PlayerTransformState data, G7PlayerTransformState state)
    {
        if (state != null) return state;
        return data;
    }

    //* این تابع نام نمایشی پلیر را از کش، state یا members snapshot پیدا می کند و دوباره در پیام حرکت دنبال نام نمی گردد.
    private string ResolveSnapshotDisplayName(G7RoomMembersSnapshotPayload snapshot, string userId, string fallback)
    {
        string cachedName = ResolveCachedRemoteDisplayName(userId, fallback);
        if (!string.IsNullOrWhiteSpace(cachedName) && !string.Equals(cachedName, userId, StringComparison.OrdinalIgnoreCase)) return cachedName;
        if (snapshot?.members == null) return string.IsNullOrWhiteSpace(cachedName) ? userId : cachedName;

        for (int i = 0; i < snapshot.members.Length; i++)
        {
            G7RoomMemberSnapshot member = snapshot.members[i];
            if (member == null) continue;
            if (!string.Equals(member.userId, userId, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(member.userName))
            {
                CacheRemoteUserName(userId, member.userName);
                return member.userName.Trim();
            }
        }

        return string.IsNullOrWhiteSpace(cachedName) ? userId : cachedName;
    }

    //* این تابع جیسون را با JsonUtility به مدل مقصد تبدیل می کند.
    private T ReadPayload<T>(string json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonUtility.FromJson<T>(json);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[G7-3D-MP] Payload parse failed: " + ex.Message);
            return null;
        }
    }

    //* این تابع نام پلیر لوکال را از لابی می گیرد و روی تکست بالای سر پلیر لوکال اعمال می کند.
    private void SyncLocalPlayerNameText()
    {
        if (roomLobbyController == null || threeDModeController == null) return;

        string displayName = roomLobbyController.CurrentUserName;
        if (string.IsNullOrWhiteSpace(displayName)) displayName = roomLobbyController.CurrentUserId;
        threeDModeController.SetLocalPlayerDisplayName(displayName);
    }

    //* این تابع نام ریموت را یک بار از ایونت جوین یا اسنپ شات در کش نگه می دارد.
    private void CacheRemoteUserName(string userId, string userName)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(userName)) return;
        if (IsLocalUser(userId)) return;

        dict_RemoteUserNamesByUserId[userId.Trim()] = userName.Trim();
    }

    //* این تابع نام همه اعضای اسنپ شات را قبل از ساخت کلون ها در کش ذخیره می کند.
    private void CacheSnapshotMemberNames(G7RoomMembersSnapshotPayload snapshot)
    {
        if (snapshot?.members == null) return;

        for (int i = 0; i < snapshot.members.Length; i++)
        {
            G7RoomMemberSnapshot member = snapshot.members[i];
            if (member == null) continue;
            CacheRemoteUserName(member.userId, member.userName);
        }
    }

    //* این تابع نام ذخیره شده یک ریموت را هنگام خروج همان یوزر پاک می کند.
    private void RemoveRemoteUserName(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return;
        dict_RemoteUserNamesByUserId.Remove(userId.Trim());
    }

    //* این تابع همه نام های کش شده ریموت را هنگام خروج از روم یا دیسکانکت پاک می کند.
    private void ClearRemoteUserNames()
    {
        dict_RemoteUserNamesByUserId.Clear();
    }

    //* این تابع نام نمایشی ریموت را از کش برمی گرداند و اگر نبود از مقدار fallback یا userId استفاده می کند.
    private string ResolveCachedRemoteDisplayName(string userId, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(userId) && dict_RemoteUserNamesByUserId.TryGetValue(userId.Trim(), out string cachedName))
        {
            if (!string.IsNullOrWhiteSpace(cachedName)) return cachedName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(fallback))
        {
            CacheRemoteUserName(userId, fallback);
            return fallback.Trim();
        }

        return string.IsNullOrWhiteSpace(userId) ? "Player" : userId.Trim();
    }


    //* این تابع زمان آخرین وضعیت دریافت شده از ریموت را ذخیره می کند تا در صورت قطع ناگهانی، کلون روی صحنه نماند.
    private void MarkRemotePlayerSeen(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return;
        if (IsLocalUser(userId)) return;

        dict_RemoteLastSeenTimeByUserId[userId.Trim()] = Time.unscaledTime;
    }

    //* این تابع زمان آخرین وضعیت یک ریموت را هنگام خروج همان یوزر پاک می کند.
    private void RemoveRemoteLastSeenTime(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return;
        dict_RemoteLastSeenTimeByUserId.Remove(userId.Trim());
    }

    //* این تابع همه زمان های آخرین وضعیت ریموت ها را هنگام خروج از روم یا دیسکانکت پاک می کند.
    private void ClearRemoteLastSeenTimes()
    {
        dict_RemoteLastSeenTimeByUserId.Clear();
    }

    //* این تابع اگر از یک ریموت برای چند ثانیه هیچ player_state نرسد، کلون او را حذف می کند.
    private void RemoveTimedOutRemotePlayers()
    {
        if (!removeRemotePlayersWhenStateTimeout) return;
        if (threeDModeController == null || !threeDModeController.IsThreeDModeActive) return;
        if (dict_RemoteLastSeenTimeByUserId.Count == 0) return;

        float now = Time.unscaledTime;
        if (now < nextRemoteTimeoutCheckTime) return;

        nextRemoteTimeoutCheckTime = now + Mathf.Max(0.2f, remoteTimeoutCheckIntervalSeconds);
        float timeout = Mathf.Max(2f, remotePlayerStateTimeoutSeconds);
        List<string> expiredUserIds = new List<string>();

        foreach (KeyValuePair<string, float> pair in dict_RemoteLastSeenTimeByUserId)
        {
            if (now - pair.Value >= timeout) expiredUserIds.Add(pair.Key);
        }

        for (int i = 0; i < expiredUserIds.Count; i++)
        {
            string userId = expiredUserIds[i];
            RemoveRemoteUserName(userId);
            RemoveRemoteLastSeenTime(userId);
            threeDModeController.RemoveRemotePlayer(userId);
            Debug.Log("[G7-3D-MP] Remote player removed by state timeout | userId=" + userId);
        }
    }

    //* این تابع عدد float را با فرمت امن جیسون می سازد.
    private string ToJsonNumber(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value)) return "0";
        return value.ToString("0.######", CultureInfo.InvariantCulture);
    }

    //* این تابع متن را برای قرار گرفتن داخل جیسون escape می کند.
    private string EscapeJson(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
    }

    [Serializable]
    private class G7PlayerStateEnvelopePayload
    {
        public string roomId;
        public string userId;
        public string userName;
        public string connectionId;
        public G7PlayerTransformState data;
        public G7PlayerTransformState state;
        public long ts;
        public long receivedAt;
    }

    [Serializable]
    private class G7RoomMembersSnapshotPayload
    {
        public string roomId;
        public string requesterUserId;
        public string requesterConnectionId;
        public G7RoomMemberSnapshot[] members;
        public G7StoredPlayerState[] states;
        public int memberCount;
        public int stateCount;
        public long ts;
    }

    [Serializable]
    private class G7RoomMemberSnapshot
    {
        public string roomId;
        public string userId;
        public string userName;
        public string connectionId;
        public string transportKind;
        public bool isSelf;
    }

    [Serializable]
    private class G7StoredPlayerState
    {
        public string roomId;
        public string userId;
        public string userName;
        public string connectionId;
        public string transportKind;
        public G7PlayerTransformState data;
        public G7PlayerTransformState state;
        public long ts;
        public long receivedAt;
    }

    [Serializable]
    private class G7PlayerTransformState
    {
        public float px;
        public float py;
        public float pz;
        public float qx;
        public float qy;
        public float qz;
        public float qw;

        //* این تابع مدل شبکه را به پوزیشن یونیتی تبدیل می کند.
        public Vector3 ToPosition()
        {
            return new Vector3(px, py, pz);
        }

        //* این تابع مدل شبکه را به روتیشن یونیتی تبدیل می کند.
        public Quaternion ToRotation()
        {
            if (Mathf.Abs(qx) + Mathf.Abs(qy) + Mathf.Abs(qz) + Mathf.Abs(qw) <= 0.0001f) return Quaternion.identity;
            return new Quaternion(qx, qy, qz, qw);
        }
    }
}
