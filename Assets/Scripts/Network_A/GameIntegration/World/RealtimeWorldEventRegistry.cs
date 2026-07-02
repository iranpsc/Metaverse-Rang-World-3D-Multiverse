using System;
using System.Collections.Generic;
using UnityEngine;

namespace Network_A.GameIntegration.World
{
    //* رجیستری آبجکت‌های قابل کنترل جهان است و رویدادهای world_event را روی هدف درست اعمال می‌کند.
    public class RealtimeWorldEventRegistry : MonoBehaviour
    {
        [SerializeField] private List<RealtimeWorldEventTarget> initialTargets = new List<RealtimeWorldEventTarget>();
        [SerializeField] private bool autoFindTargetsInChildren = true;
        [SerializeField] private bool logApplyResult;

        private readonly Dictionary<string, RealtimeWorldEventTarget> dict_TargetByObjectId = new Dictionary<string, RealtimeWorldEventTarget>(StringComparer.OrdinalIgnoreCase);
        private RealtimeWorldEventReceiver worldEventReceiver;

        public int TargetCount => dict_TargetByObjectId.Count;
        public event Action<RealtimeWorldEventData, RealtimeWorldEventTarget> WorldEventApplied;
        public event Action<RealtimeWorldEventData> WorldEventTargetMissing;

        //* رجیستری را با گیرنده رویداد جهان آماده می‌کند و تارگت‌های اولیه را ثبت می‌کند.
        public void Initialize(RealtimeWorldEventReceiver receiver)
        {
            if (worldEventReceiver != null) worldEventReceiver.WorldEventReceived -= HandleWorldEventReceived;

            worldEventReceiver = receiver;
            RebuildTargetLookup();

            if (worldEventReceiver != null) worldEventReceiver.WorldEventReceived += HandleWorldEventReceived;
        }

        //* اتصال رویدادها را هنگام حذف کامپوننت جدا می‌کند.
        private void OnDestroy()
        {
            if (worldEventReceiver != null) worldEventReceiver.WorldEventReceived -= HandleWorldEventReceived;
            worldEventReceiver = null;
            dict_TargetByObjectId.Clear();
        }

        //* یک تارگت جهان را در رجیستری ثبت می‌کند.
        public bool RegisterTarget(RealtimeWorldEventTarget target)
        {
            if (target == null || string.IsNullOrWhiteSpace(target.ObjectId)) return false;
            dict_TargetByObjectId[target.ObjectId] = target;
            if (!initialTargets.Contains(target)) initialTargets.Add(target);
            return true;
        }

        //* تارگت را از رجیستری حذف می‌کند.
        public bool UnregisterTarget(string objectId)
        {
            if (string.IsNullOrWhiteSpace(objectId)) return false;
            return dict_TargetByObjectId.Remove(objectId.Trim());
        }

        //* تلاش می‌کند تارگت مربوط به یک objectId را پیدا کند.
        public bool TryGetTarget(string objectId, out RealtimeWorldEventTarget target)
        {
            target = null;
            if (string.IsNullOrWhiteSpace(objectId)) return false;
            return dict_TargetByObjectId.TryGetValue(objectId.Trim(), out target);
        }

        //* رویداد جهان را روی تارگت درست اعمال می‌کند.
        public bool ApplyWorldEvent(RealtimeWorldEventData eventData)
        {
            if (eventData == null || !eventData.IsValid()) return false;

            if (!dict_TargetByObjectId.TryGetValue(eventData.objectId, out RealtimeWorldEventTarget target) || target == null)
            {
                if (logApplyResult) Debug.LogWarning("[RealtimeWorldEventRegistry] Target missing. object=" + eventData.objectId);
                WorldEventTargetMissing?.Invoke(eventData);
                return false;
            }

            bool applied = target.ApplyWorldEvent(eventData);
            if (applied)
            {
                if (logApplyResult) Debug.Log("[RealtimeWorldEventRegistry] World event applied. object=" + eventData.objectId + " | type=" + eventData.eventType);
                WorldEventApplied?.Invoke(eventData, target);
            }

            return applied;
        }

        //* لیست lookup تارگت‌ها را از اول می‌سازد.
        public void RebuildTargetLookup()
        {
            dict_TargetByObjectId.Clear();

            if (autoFindTargetsInChildren)
            {
                RealtimeWorldEventTarget[] childTargets = GetComponentsInChildren<RealtimeWorldEventTarget>(true);
                for (int i = 0; i < childTargets.Length; i++) RegisterTarget(childTargets[i]);
            }

            for (int i = 0; i < initialTargets.Count; i++) RegisterTarget(initialTargets[i]);
        }

        //* رویداد دریافتی را از receiver گرفته و روی تارگت مربوطه اعمال می‌کند.
        private void HandleWorldEventReceived(RealtimeWorldEventData eventData)
        {
            ApplyWorldEvent(eventData);
        }
    }
}
