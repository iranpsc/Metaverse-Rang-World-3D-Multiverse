using Mirror;
using Mirror.SimpleWeb;
using System;
using System.IO;
using UnityEngine;
using Meta;

[AddComponentMenu("Meta/Server Configuration")]
public class Meta_ServerConfiguration : MonoBehaviour
{
    public string ConfigFileName = "ServerConfig.json";

    public Meta_ServerConfig Config = new Meta_ServerConfig();

    private Meta_NetworkManager Manager;
    private string ConfigPath;

    [Header("Debugger")]
    public bool EnableLog;

    private void Awake()
    {
        if(!Application.isBatchMode)
        {
            if (EnableLog) Debug.Log("[Meta_ServerConfiguration] Config Server Skipped (Not A Headless Server).");
            return;
        }

        Manager = Meta_NetworkManager.singleton;
        ConfigPath = Path.Combine(Application.dataPath, "..", ConfigFileName/*"ServerConfig.json"*/);

        LoadConfig();
        ApplyConfig();
    }

    private void LoadConfig()
    {
        if (EnableLog) Debug.Log("[Meta_ServerConfiguration] Replace Server Default With Config File.");

        if(File.Exists(ConfigPath))
        {
            try
            {
                string _Json = File.ReadAllText(ConfigPath);
                Config = JsonUtility.FromJson<Meta_ServerConfig>(_Json);
                if (EnableLog) Debug.Log("[Meta_ServerConfiguration] Catching Server Config.");
            }
            catch (Exception _Ex)
            {
                if (EnableLog) Debug.LogError("[Meta_ServerConfiguration] Failed To Parse Config: " + _Ex.Message);
                Manager.StopServer();
            }
        }
        else
        {
            if (EnableLog) Debug.LogError($"[Meta_ServerConfiguration] No Config File Found at {ConfigPath} Stopping The Server...");
            Manager.StopServer();
        }
        if (EnableLog) Debug.Log("[Meta_ServerConfiguration] Finished Loading Config Process.");
    }

    private void ApplyConfig()
    {
        if (Manager == null)
        {
            if (EnableLog) Debug.LogError("[Meta_ServerConfiguration] No Network Manager Found.");
            enabled = false;
            return;
        }

        // ================================
        #region Network Manager
        Manager.headlessStartMode = Config.Network.HeadlessStartMode;
        Manager.sendRate = Config.Network.SendRate;

        TransportType(Config.Network.Transport, Manager);
        Manager.networkAddress = Config.Network.NetworkAddress;
        Manager.maxConnections = Config.Network.MaxConnection;

        Manager.autoCreatePlayer = Config.Network.AutoCreatePlayer;
        #endregion
        // ================================
        #region Multiplex Transport
        if (Manager.transport is MultiplexTransport _Multiplex)
        {
            foreach (Transport _Transport in _Multiplex.transports)
            {
                if (_Transport is TelepathyTransport _MTelepathy)
                {
                    TelepathyConfig(_MTelepathy);
                }
                if (_Transport is SimpleWebTransport _MSimpleWeb)
                {
                    WebConfig(_MSimpleWeb);
                }
            }
        }
        #endregion
        // ================================
        #region Telepathy Transport
        if (Manager.transport is TelepathyTransport _Telepathy)
        {
            TelepathyConfig(_Telepathy);
        }
        #endregion
        // ================================
        #region SimpleWeb Transport
        else if (Manager.transport is SimpleWebTransport _SimpleWeb)
        {
            WebConfig(_SimpleWeb);
        }
        #endregion
        // ================================

        if (EnableLog) Debug.Log("[Meta_ServerConfiguration] Config Applied To NetworkManager.");
    }

    private void TelepathyConfig(TelepathyTransport _Transport)
    {
        _Transport.port = (ushort)Config.Telepathy.Port;

        _Transport.NoDelay = Config.Telepathy.NoDelay;
    }
    private void WebConfig(SimpleWebTransport _Transport)
    {
        _Transport.noDelay = Config.Web.NoDelay;

        _Transport.sslEnabled = Config.Web.SslEnabled;
        _Transport.sslProtocols = Config.Web.SslProtocols;
        _Transport.sslCertJson = Config.Web.SslCertJson;

        _Transport.port = (ushort)Config.Web.Port;
        _Transport.batchSend = Config.Web.BatchSend;
        _Transport.waitBeforeSend = Config.Web.WaitBeforeSend;

        _Transport.clientUseWss = Config.Web.ClientUseWss;
        _Transport.clientWebsocketSettings.ClientPortOption = Config.Web.ClientPortOption;
        if (Config.Web.ClientPortOption == WebsocketPortOption.SpecifyPort)
            _Transport.clientWebsocketSettings.CustomClientPort = Config.Web.ClientPort;
    }
    private void TransportType(int _Number, Meta_NetworkManager _Manager)
    {
        Transport _Transport = null;

        switch (_Number)
        {
            case 1: _Transport = _Manager.GetComponent<MultiplexTransport>(); break;
                
            case 2: _Transport = _Manager.GetComponent<TelepathyTransport>(); break;

            case 3: _Transport = _Manager.GetComponent<SimpleWebTransport>(); break;
        }
        if (_Transport == null)
        {
            _Transport = _Manager.GetComponent<Transport>();
            if (EnableLog) Debug.LogWarning($"[Meta_ServerConfiguration] Failed To Catch Transport. Remember [1. Multiplex]-[2. Telepathy]-[3.SimpleWeb]. Confrim The Number On Config File1");
        }
        _Manager.transport = _Transport;
        if (EnableLog) Debug.Log($"[Meta_ServerConfiguration] Transport Set To {_Manager.transport}");
    }
}
