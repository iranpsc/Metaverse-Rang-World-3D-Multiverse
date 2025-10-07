using Mirror;
using Mirror.SimpleWeb;
using System;
using System.IO;
using System.Security.Authentication;
using UnityEngine;

namespace Meta
{
    [AddComponentMenu("Meta/Network Config")]
    [HelpURL("")]
    public class Meta_NetworkConfig : MonoBehaviour
    {
        [ReadOnly]
        public string ConfigFileName = "ServerConfig.json";
        public string ConfigPath;

        public Meta_ConfigFile Config = new Meta_ConfigFile();
        private Meta_NetworkManager Manager;

        public bool LogScript;

        private void Start()
        {
            if (!Application.isBatchMode)
            {
                if (LogScript) Debug.Log("[Meta] Configing Server Skinpepd (Not A Headless Server)");
                return;
            }
            GetConfig();
        }

        private void Initialize()
        {
            Manager = Meta_NetworkManager.singleton;
#if UNITY_EDITOR
            ConfigPath = Path.Combine(Application.dataPath, ConfigFileName);
#else
        ConfigPath = Path.Combine(Application.dataPath, "..", ConfigFileName);
#endif
        }
        private void GetConfig()
        {
            if (LogScript) Debug.Log("[Meta] Start Loading Config");

            Initialize();

            if (File.Exists(ConfigPath))
            {
                try
                {
                    string _Json = File.ReadAllText(ConfigPath);
                    Config = JsonUtility.FromJson<Meta_ConfigFile>(_Json);
                    if (LogScript) Debug.Log("[Meta] Server Config Loaded Successfuly");
                    SetConfig();
                }
                catch (Exception _Ex)
                {
                    if (LogScript) Debug.LogError("[Meta] Failed To Parse Config With Result: " + _Ex.Message);
                    Manager.StopServer();
                }
            }
            else
            {
                if (LogScript) Debug.Log("[Meta] No Config File Found In " + ConfigPath);
                Manager.StopServer();
            }
            if (LogScript) Debug.Log("[Meta] Finished Loading Config Proccess");
        }

        private void SetConfig()
        {
            if (Manager == null)
            {
                if (LogScript) Debug.Log("[Meta] Netwrok Manager Not Found");
                return;
            }

            Manager.dontDestroyOnLoad = Config.General.dontDestroyOnLoad;
            Manager.runInBackground = Config.General.runInBackground;

            Manager.headlessStartMode = (HeadlessStartOptions)(Config.General.headlessStartMode);
            Manager.editorAutoStart = Config.General.editorAutoStart;
            Manager.sendRate = Config.General.sendRate;

            Manager.offlineSceneLoadDelay = Config.General.offlineSceneLoadDelay;

            #region Manager.transport = Config.General.transport;
            TransportType(Config.General.transport);

            // Telepathy
            if (Transport.active is TelepathyTransport _Tp)
            {
                TelepathyConfig(_Tp);
                if (LogScript) Debug.Log("[Meta] Server Environment Type Set To Telepathy Transport");
            }

            // Simple Web
            else if (Transport.active is SimpleWebTransport _Sw)
            {
                WebConfig(_Sw);
                if (LogScript) Debug.Log("[Meta] Server Environment Type Set To Simple Web Transport");
            }

            // Multiplex
            else if (Transport.active is MultiplexTransport _Mx)
            {

                foreach (Transport _Child in _Mx.transports)
                {
                    if (_Child is TelepathyTransport _TpChild)
                    {
                        TelepathyConfig(_TpChild);
                    }
                    if (_Child is SimpleWebTransport _SwChild)
                    {
                        WebConfig(_SwChild);
                    }
                }
                if (LogScript) Debug.Log("[Meta] Server Environment Type Set To Multiplex Transport");
            }
            #endregion
            Manager.networkAddress = Config.General.networkAddress;
            Manager.maxConnections = Config.General.maxConnections;
            Manager.disconnectInactiveConnections = Config.General.disconnectInactiveConnections;
            Manager.disconnectInactiveTimeout = Config.General.disconnectInactiveTimeout;

            Manager.autoCreatePlayer = Config.General.autoCreatePlayer;
        }
        private void TelepathyConfig(TelepathyTransport _Transport)
        {
            _Transport.port = (ushort)Config.Network.Telepathy.port;

            _Transport.NoDelay = Config.Network.Telepathy.NoDelay;
            _Transport.SendTimeout = Config.Network.Telepathy.SendTimeout;
            _Transport.ReceiveTimeout = Config.Network.Telepathy.ReceiveTimeout;

            _Transport.serverMaxMessageSize = Config.Network.Telepathy.serverMaxMessageSize;
            _Transport.serverMaxReceivesPerTick = Config.Network.Telepathy.serverMaxReceivesPerTick;
            _Transport.serverSendQueueLimitPerConnection = Config.Network.Telepathy.serverSendQueueLimitPerConnection;
            _Transport.serverReceiveQueueLimitPerConnection = Config.Network.Telepathy.serverReceiveQueueLimitPerConnection;

            _Transport.clientMaxMessageSize = Config.Network.Telepathy.clientMaxMessageSize;
            _Transport.clientMaxReceivesPerTick = Config.Network.Telepathy.clientMaxReceivesPerTick;
            _Transport.clientSendQueueLimit = Config.Network.Telepathy.clientSendQueueLimit;
            _Transport.clientReceiveQueueLimit = Config.Network.Telepathy.clientReceiveQueueLimit;
            #region Log Server
            if (LogScript) Debug.Log($"[Meta] Telephaty Transport Configed With:" +
                $"Port: {_Transport.port}" +
                $"Remove Delay: {_Transport.NoDelay}");
            #endregion
        }
        private void WebConfig(SimpleWebTransport _Transport)
        {
            _Transport.maxMessageSize = Config.Network.SimpleWeb.maxMessageSize;
            _Transport.maxHandshakeSize = Config.Network.SimpleWeb.maxHandshakeSize;

            _Transport.serverMaxMsgsPerTick = Config.Network.SimpleWeb.serverMaxMsgsPerTick;
            _Transport.clientMaxMsgsPerTick = Config.Network.SimpleWeb.clientMaxMsgsPerTick;
            _Transport.sendTimeout = Config.Network.SimpleWeb.sendTimeout;
            _Transport.receiveTimeout = Config.Network.SimpleWeb.receiveTimeout;
            _Transport.noDelay = Config.Network.SimpleWeb.noDelay;

            _Transport.sslEnabled = Config.Network.SimpleWeb.sslEnabled;
            _Transport.sslProtocols = (SslProtocols)(Config.Network.SimpleWeb.sslProtocols);
            _Transport.sslCertJson = Config.Network.SimpleWeb.sslCertJson;

            _Transport.port = (ushort)Config.Network.SimpleWeb.port;
            _Transport.batchSend = Config.Network.SimpleWeb.batchSend;
            _Transport.waitBeforeSend = Config.Network.SimpleWeb.waitBeforeSend;

            _Transport.clientUseWss = Config.Network.SimpleWeb.clientUseWss;
            ClientWebsocketSettings _WebSocket = new ClientWebsocketSettings();
            _WebSocket.ClientPortOption = (WebsocketPortOption)(Config.Network.SimpleWeb.clientPortOption);
            _Transport.clientWebsocketSettings = _WebSocket;
            #region Log Server
            if (LogScript) Debug.Log($"[Meta] Simple Web Transport Configed With" +
                $"Port: {_Transport.port}" +
                $"Ssl: {_Transport.sslEnabled}" +
                $"Wss: {_Transport.clientUseWss}" +
                $"Remove Delay: {_Transport.noDelay}" +
                $"ClientPortLevel: {_Transport.clientWebsocketSettings.ClientPortOption}");
            #endregion
        }

        /// <summary>
        /// [1 Multiplex] - [2 Telephaty] - [3 Simple Web]
        /// </summary>
        /// <param name="_Number"></param>
        /// <param name="Manager"></param>
        private void TransportType(int _Number)
        {
            switch (_Number)
            {
                case 1:
                    Manager.transport = GetComponent<MultiplexTransport>();
                    break;
                case 2:
                    Manager.transport = GetComponent<TelepathyTransport>();
                    break;
                case 3:
                    Manager.transport = GetComponent<SimpleWebTransport>();
                    break;
                default:
                    Manager.transport = GetComponent<MultiplexTransport>();
                    break;
            }
        }
    }
}