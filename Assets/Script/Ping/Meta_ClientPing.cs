using Mirror;
using TMPro;
using UnityEngine;

[HelpURL("GitHub")]
public class Meta_ClientPing : MonoBehaviour
{
    public Color TextColor = Color.white;
    public TMP_Text Ping;

    public void Update()
    {
        if (!NetworkClient.active) return;

        string _Ping = Mathf.Round((float)(NetworkTime.rtt * 1000)).ToString();
        Ping.text = ($"ping: {_Ping}ms");
        Ping.color = NetworkClient.connectionQuality.ColorCode();
    }
}
