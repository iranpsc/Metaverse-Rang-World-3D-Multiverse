using System;
using UnityEngine;

namespace Network_A.GameIntegration.World
{
    //* هدف رویداد جهان در صحنه است و eventهای دریافتی مثل باز شدن در یا فعال شدن آبجکت را روی خودش اعمال می‌کند.
    public class RealtimeWorldEventTarget : MonoBehaviour
    {
        [Header("Identity")]
        [SerializeField] private string objectId = string.Empty;

        [Header("State")]
        [SerializeField] private bool boolState;
        [SerializeField] private float numberState;
        [SerializeField] private string stringState = string.Empty;
        [SerializeField] private string lastStateKey = string.Empty;
        [SerializeField] private long lastSequence;

        [Header("Behavior")]
        [SerializeField] private bool applyActiveStateToGameObject;
        [SerializeField] private bool logAppliedEvents;

        public string ObjectId => objectId;
        public bool BoolState => boolState;
        public float NumberState => numberState;
        public string StringState => stringState;
        public string LastStateKey => lastStateKey;
        public long LastSequence => lastSequence;

        public event Action<RealtimeWorldEventData> WorldEventApplied;

        //* شناسه آبجکت را برای استفاده runtime تنظیم می‌کند.
        public void InitializeIdentity(string targetObjectId)
        {
            objectId = string.IsNullOrWhiteSpace(targetObjectId) ? objectId : targetObjectId.Trim();
        }

        //* رویداد جهان را اگر مربوط به این آبجکت باشد اعمال می‌کند.
        public bool ApplyWorldEvent(RealtimeWorldEventData eventData)
        {
            if (eventData == null || !eventData.IsValid()) return false;
            if (!string.Equals(eventData.objectId, objectId, StringComparison.OrdinalIgnoreCase)) return false;
            if (eventData.sequence > 0 && lastSequence > 0 && eventData.sequence <= lastSequence) return false;

            lastSequence = eventData.sequence > 0 ? eventData.sequence : lastSequence + 1;
            lastStateKey = string.IsNullOrWhiteSpace(eventData.stateKey) ? eventData.eventType : eventData.stateKey;
            boolState = eventData.boolValue;
            numberState = eventData.numberValue;
            stringState = eventData.stringValue ?? string.Empty;

            ApplyKnownState(lastStateKey, boolState);

            if (logAppliedEvents) Debug.Log("[RealtimeWorldEventTarget] Applied world event. object=" + objectId + " | key=" + lastStateKey + " | bool=" + boolState + " | seq=" + lastSequence);
            WorldEventApplied?.Invoke(eventData);
            return true;
        }

        //* stateهای شناخته‌شده مثل active/isActive را روی آبجکت یونیتی اعمال می‌کند.
        private void ApplyKnownState(string stateKey, bool value)
        {
            if (!applyActiveStateToGameObject) return;
            if (string.IsNullOrWhiteSpace(stateKey)) return;

            if (string.Equals(stateKey, "active", StringComparison.OrdinalIgnoreCase) || string.Equals(stateKey, "isActive", StringComparison.OrdinalIgnoreCase)) gameObject.SetActive(value);
        }
    }
}
