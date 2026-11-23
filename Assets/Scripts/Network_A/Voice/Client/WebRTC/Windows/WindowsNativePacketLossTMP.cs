#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Network_A.Voice.Client.WebRTC.Windows;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

// Dedicated Windows-only display. Reuses the exact text_packetLost TMP object.
// Never changes the WebGL/Quest component or creates a Canvas.
internal sealed class WindowsNativePacketLossTMP : MonoBehaviour
{
    private sealed class Sample
    {
        internal readonly string Epoch;
        internal readonly bool HasStats;
        internal readonly ulong Received;
        internal readonly long Lost;
        internal readonly string Error;
        internal Sample(string epoch, bool hasStats, ulong received, long lost, string error)
        {
            Epoch = epoch; HasStats = hasStats; Received = received; Lost = lost; Error = error;
        }
    }

    private TMP_Text target;
    private WindowsWebRtcVoiceBridge bridge;
    private Vme2WindowsWebRtcSfuSession priorSession;
    private string priorConsumers = string.Empty;
    private ulong priorReceived;
    private long priorLost;
    private bool hasBaseline;
    private float nextRefresh;
    private Task<Sample> pending;
    private string pendingEpoch;
    private bool warnedMissingExport;

    private void Awake()
    {
        target = GetComponent<TMP_Text>();
        var webglOnly = GetComponent<VME2W4WebGLPacketLostTMP>();
        if (webglOnly != null) webglOnly.enabled = false;
        if (target != null) target.text = "0";
    }

    private void OnDisable()
    {
        pending = null;
        ResetCounters();
    }

    private void ResetCounters()
    {
        hasBaseline = false;
        priorReceived = 0;
        priorLost = 0;
    }

    private void Update()
    {
        if (target == null) return;
        if (pending != null && pending.IsCompleted)
        {
            var task = pending;
            pending = null;
            if (task.IsFaulted)
            {
                target.text = "0";
                ResetCounters();
                Debug.LogWarning("VME2_WIN_RTP_STATS=ERROR | " +
                    task.Exception.GetBaseException().GetType().Name);
            }
            else
            {
                var result = task.Result;
                if (result.Epoch == pendingEpoch)
                    ConsumeResult(result);
            }
        }
        if (Time.unscaledTime < nextRefresh || pending != null) return;
        nextRefresh = Time.unscaledTime + 1f;
        if (bridge == null) bridge = FindFirstObjectByType<WindowsWebRtcVoiceBridge>();
        Vme2WindowsWebRtcSfuSession currentSession;
        string[] ids;
        if (bridge == null || !bridge.TryGetNativeRtpStatTargets(out currentSession, out ids))
        {
            if (priorSession != null || hasBaseline) ResetCounters();
            priorSession = null;
            priorConsumers = string.Empty;
            target.text = "0";
            return;
        }
        string idKey = string.Join(";", ids);
        if (!ReferenceEquals(priorSession, currentSession) || priorConsumers != idKey)
        {
            priorSession = currentSession;
            priorConsumers = idKey;
            ResetCounters();
            target.text = "0";
        }
        // Stable per-session epoch; result of superseded poll is discarded.
        string epoch = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(currentSession)
            .ToString(CultureInfo.InvariantCulture) + ":" + idKey;
        pendingEpoch = epoch;
        pending = Task.Run(() =>
        {
            ulong totalReceived = 0;
            long totalLost = 0;
            bool any = false;
            try
            {
                foreach (var id in ids)
                {
                    ulong received;
                    long lost;
                    if (!currentSession.TryGetAudioInboundRtpStats(id, out received, out lost))
                        continue;
                    checked
                    {
                        totalReceived += received;
                        totalLost += lost;
                    }
                    any = true;
                }
                return new Sample(epoch, any, totalReceived, totalLost, null);
            }
            catch (Exception e)
            {
                return new Sample(epoch, false, 0, 0, e.GetType().Name + ":" + e.Message);
            }
        });
    }

    private void ConsumeResult(Sample sample)
    {
        if (sample.Error != null)
        {
            target.text = "0";
            ResetCounters();
            if (!warnedMissingExport || !sample.Error.StartsWith("EntryPointNotFoundException", StringComparison.Ordinal))
            {
                Debug.LogWarning("VME2_WIN_RTP_STATS=UNAVAILABLE | " + sample.Error);
                warnedMissingExport = sample.Error.StartsWith("EntryPointNotFoundException", StringComparison.Ordinal);
            }
            return;
        }
        if (!sample.HasStats || !hasBaseline || sample.Received < priorReceived || sample.Lost < priorLost)
        {
            target.text = "0";
            priorReceived = sample.Received;
            priorLost = sample.Lost;
            hasBaseline = sample.HasStats;
            return;
        }
        ulong rx = sample.Received - priorReceived;
        long lost = sample.Lost - priorLost;
        priorReceived = sample.Received;
        priorLost = sample.Lost;
        // Actual number of packets lost during this one-second polling interval.
        // No percentage, and no placeholder other than numeric zero.
        long lostCount = Math.Max(0L, lost);
        target.text = lostCount.ToString(CultureInfo.InvariantCulture);
        Debug.Log("VME2_WIN_RTP_PACKET_LOSS=VALID | lostPackets=" +
            lostCount.ToString(CultureInfo.InvariantCulture) +
            " | receivedDelta=" + rx + " | lostDelta=" + lost);
    }
}

internal sealed class WindowsNativePacketLossInstaller : MonoBehaviour
{
    private float nextSearch;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Install()
    {
        var root = new GameObject("VME2_Windows_Native_PacketLoss_Installer");
        DontDestroyOnLoad(root);
        root.AddComponent<WindowsNativePacketLossInstaller>();
    }
    private void Update()
    {
        if (Time.unscaledTime < nextSearch) return;
        nextSearch = Time.unscaledTime + 1f;
        var active = SceneManager.GetActiveScene();
        if (!active.IsValid() || active.name != "Grpc_Enviroment_Voice") return;
        foreach (var text in Resources.FindObjectsOfTypeAll<TMP_Text>())
        {
            if (text == null || !text.gameObject.scene.IsValid() ||
                !text.gameObject.scene.isLoaded || text.name != "text_packetLost") continue;
            if (text.GetComponent<WindowsNativePacketLossTMP>() == null)
                text.gameObject.AddComponent<WindowsNativePacketLossTMP>();
        }
    }
}
#endif
