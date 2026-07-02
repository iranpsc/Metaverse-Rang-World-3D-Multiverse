namespace Network_A.Core
{
    public struct Endpoint
    {
        public string Host;
        public int Port;
        public bool UseTls;

        public Endpoint(string host, int port, bool useTls)
        {
            Host = host;
            Port = port;
            UseTls = useTls;
        }

        //* Builds an HTTP or HTTPS base URL.
        public string ToHttpBaseUrl()
        {
            return string.Format("{0}://{1}:{2}", UseTls ? "https" : "http", Host, Port);
        }

        //* Builds a WS or WSS base URL.
        public string ToWsBaseUrl()
        {
            return string.Format("{0}://{1}:{2}", UseTls ? "wss" : "ws", Host, Port);
        }

        //* Returns the readable endpoint value.
        public override string ToString()
        {
            return ToHttpBaseUrl();
        }
    }
}
