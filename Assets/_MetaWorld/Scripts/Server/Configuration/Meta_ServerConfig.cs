using Mirror;
using Mirror.SimpleWeb;
using System;
using System.Security.Authentication;

namespace Meta
{
    public class Meta_ServerConfig
    {
        public Meta_NetworkManger Network = new Meta_NetworkManger();
        public Meta_MulltiplexTransport Multiplex = new Meta_MulltiplexTransport();
        public Meta_TelepathyTransport Telepathy = new Meta_TelepathyTransport();
        public Meta_SimpleWebTransport Web = new Meta_SimpleWebTransport();
    }

    [Serializable]
    public class Meta_NetworkManger
    {
        public bool DontDestroyOnLoad = true;
        public bool RunInBackground = true;

        public HeadlessStartOptions HeadlessStartMode;
        public bool EditorAutoStart = false;
        public int SendRate = 60;

        public float OfflineSceneLoadDelay = 0;

        public int Transport = 1;
        public string NetworkAddress = "3ddevelop.irpsc.com";
        public int MaxConnection = 100;
        public bool DisconnectInactiveConnections = false;
        public float DisconnectInactiveTimeout = 60;

        public NetworkAuthenticator Authenticator;

        public bool AutoCreatePlayer = true;
        public enum PlayerSpawnMethodType { Random, RoundRobin }
        public PlayerSpawnMethodType PlayerSpawnMethod;

        public bool ExceptionsDisconnect = true;

        public float BufferTimeMultiplier = 2f;
        public int BufferLimit = 32;

        public float CatchupNehativeThreshold = -1f;
        public float CatchupPositiveThreshold = 1f;
        public float CatchupSpeed = 0.02f;
        public float SlowdownSpeed = 0.04f;
        public int DriftEmaDuration = 1;

        public bool DynamicAdjustment = true;
        public float DynamicAdjustmentTolerance = 1f;
        public int DeliveryTimeEmaDuration = 2;

        public enum EvaluationMethodType { Simple, Pragmatic }
        public EvaluationMethodType EvaluationMethod;
        public float EvaluationInterval = 3f;

        public bool TimeInterpolationGui = false;
    }
    [Serializable]
    public class Meta_MulltiplexTransport
    {
        public Transport[] Transports;
    }
    [Serializable]
    public class Meta_TelepathyTransport
    {
        public ushort Port = 7777;

        public bool NoDelay = true;
        public int SendTimeout = 5000;
        public int ReceiveTimeout = 30000;

        public int ServerMaxMessageSize = 16384;
        public int ServerMaxReceivePerTick = 10000;
        public int ServerSendQueueLimitPerConnection = 10000;
        public int ServerReceiveQueueLimitPerConnection = 10000;

        public int ClientMaxMessageSize = 16384;
        public int ClientMaxReceivePerTick = 1000;
        public int ClientSendQueueLimit = 10000;
        public int ClientReceiveQueueLimit = 10000;
    }
    [Serializable]
    public class Meta_SimpleWebTransport
    {
        public int MaxMessageSize = 16384;
        public int MaxHandshakeSize = 16384;
        public int ServerMaxMsgsPerTick = 10000;
        public int ClientMaxMsgsPerTick = 1000;
        public int SendTimeout = 5000;
        public int ReceiveTimeout = 20000;
        public bool NoDelay = true;

        public bool SslEnabled = false;
        public SslProtocols SslProtocols;
        public string SslCertJson = "./cert.json";

        public ushort Port = 27777;
        public bool BatchSend = true;
        public bool WaitBeforeSend = true;

        public bool ClientUseWss = true;
        public WebsocketPortOption ClientPortOption;
        public ushort ClientPort = 443;
    }
}