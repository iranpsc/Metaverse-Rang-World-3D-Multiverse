using System.Globalization;
using System.Runtime.InteropServices;
using TMPro;
using UnityEngine;

// UI-only display of the WebRTC statistic already cached by the W4 browser bundle.
// Attach this component directly to the existing text_packetLost TMP object.
[DisallowMultipleComponent]
public sealed class VME2W4WebGLPacketLostTMP : MonoBehaviour
{
    private TMP_Text targetText;
    private float nextRefreshAt;
    private int lastDisplayed = int.MinValue;
    private int previousTotalLost;
    private bool hasPreviousSample;

#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")]
    private static extern int VME2W4GetWebRtcPacketsLost();
#endif

    private void Awake()
    {
        targetText = GetComponent<TMP_Text>();
        if (targetText == null)
        {
            Debug.LogError("[VME2_W4_PACKET_LOST] Attach to a TextMeshPro or RTLTMPro text component.", this);
            enabled = false;
        }
    }

    private void OnEnable()
    {
        if (targetText == null)
            targetText = GetComponent<TMP_Text>();
        if (targetText == null)
        {
            enabled = false;
            return;
        }

        lastDisplayed = int.MinValue;
        previousTotalLost = 0;
        hasPreviousSample = false;
        Refresh();
        nextRefreshAt = Time.unscaledTime + 1f;
    }

    private void Update()
    {
        if (Time.unscaledTime < nextRefreshAt)
            return;

        nextRefreshAt = Time.unscaledTime + 1f;
        Refresh();
    }

    private void Refresh()
    {
        int totalLost = 0;
#if UNITY_WEBGL && !UNITY_EDITOR
        totalLost = VME2W4GetWebRtcPacketsLost();
#endif
        // WebRTC packetsLost is cumulative. Display only the increase since
        // the previous one-second UI refresh; never request additional stats.
        int lostThisInterval = 0;
        if (hasPreviousSample && totalLost >= previousTotalLost)
            lostThisInterval = totalLost - previousTotalLost;

        previousTotalLost = totalLost;
        hasPreviousSample = true;

        if (lastDisplayed == lostThisInterval)
            return;

        lastDisplayed = lostThisInterval;
        targetText.text = lostThisInterval.ToString(CultureInfo.InvariantCulture);
    }
}
