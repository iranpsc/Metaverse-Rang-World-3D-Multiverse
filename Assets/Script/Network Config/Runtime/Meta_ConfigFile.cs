using System;

[Serializable]
public class Meta_ConfigFile
{
    public GeneralConfig General = new GeneralConfig();
    public NetworkConfig Network = new NetworkConfig();
    public DiscoveryConfig Discovery = new DiscoveryConfig();
}

[Serializable]
public class GeneralConfig
{
    public bool dontDestroyOnLoad;
    public bool runInBackground;

    public int headlessStartMode;
    public bool editorAutoStart;
    public int sendRate;

    public float offlineSceneLoadDelay;

    public int transport;
    public string networkAddress;
    public int maxConnections;
    public bool disconnectInactiveConnections;
    public float disconnectInactiveTimeout;

    public bool autoCreatePlayer;
}

[Serializable]
public class NetworkConfig
{
    public TelepathyConfig Telepathy = new TelepathyConfig();
    public SimpleWebConfig SimpleWeb = new SimpleWebConfig();
}

[Serializable]
public class TelepathyConfig
{
    public int port;

    public bool NoDelay;
    public int SendTimeout;
    public int ReceiveTimeout;

    public int serverMaxMessageSize;
    public int serverMaxReceivesPerTick;
    public int serverSendQueueLimitPerConnection;
    public int serverReceiveQueueLimitPerConnection;

    public int clientMaxMessageSize;
    public int clientMaxReceivesPerTick;
    public int clientSendQueueLimit;
    public int clientReceiveQueueLimit;
}

[Serializable]
public class SimpleWebConfig
{
    public int maxMessageSize;
    public int maxHandshakeSize;
    public int serverMaxMsgsPerTick;
    public int clientMaxMsgsPerTick;
    public int sendTimeout;
    public int receiveTimeout;
    public bool noDelay;

    public bool sslEnabled;
    public int sslProtocols;
    public string sslCertJson;

    public int port;
    public bool batchSend;
    public bool waitBeforeSend;

    public bool clientUseWss;
    public int clientPortOption;
}

[Serializable]
public class DiscoveryConfig
{
    public bool enableActiveDiscovery; // Server will broadcast data to an address
    public string BroadcastAddress; // broadcast address
    public int serverBroadcastListenPort; // broadcast port
    public int ActiveDiscoveryInterval; // broadcast delay
}