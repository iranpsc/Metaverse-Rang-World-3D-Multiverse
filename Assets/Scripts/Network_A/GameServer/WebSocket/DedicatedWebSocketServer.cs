using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Network_A.GameServer;
using UnityEngine;

namespace Network_A.GameServer.WebSocket
{
    public class DedicatedWebSocketServer : MonoBehaviour
    {
        [Header("Runtime")]
        [SerializeField] private DedicatedServerRuntime runtime;

        [Header("Server")]
        [SerializeField] private bool autoStartOnRuntimeStarted = true;
        [SerializeField] private bool echoTestMessages = true;
        [SerializeField] private int maxConnections = 20;

        [Header("Debug")]
        [SerializeField] private bool logTextMessages = true;
        [SerializeField] private bool logFullTextMessages = false;

        private TcpListener listener;
        private CancellationTokenSource serverCts;
        private readonly ConcurrentDictionary<string, DedicatedWebSocketConnection> connections =
            new ConcurrentDictionary<string, DedicatedWebSocketConnection>();

        private readonly ConcurrentQueue<Action> mainThreadActions = new ConcurrentQueue<Action>();

        public bool IsListening { get; private set; }
        public int ConnectionCount { get { return connections.Count; } }

        public event Action<DedicatedWebSocketConnection> ClientConnected;
        public event Action<DedicatedWebSocketConnection, string> ClientDisconnected;
        public event Action<DedicatedWebSocketConnection, string> TextMessageReceived;

        //* این تابع رفرنس های لازم را در شروع آبجکت پیدا می کند.
        private void Awake()
        {
            EnsureRuntimeReference();
        }

        //* این تابع رویدادهای ران تایم را هنگام فعال شدن آبجکت گوش می دهد.
        private void OnEnable()
        {
            EnsureRuntimeReference();

            if (runtime != null)
            {
                runtime.RuntimeStarted += HandleRuntimeStarted;
                runtime.RuntimeStopped += HandleRuntimeStopped;
            }
        }

        //* این تابع رویدادها را هنگام غیرفعال شدن آبجکت پاک می کند.
        private void OnDisable()
        {
            if (runtime != null)
            {
                runtime.RuntimeStarted -= HandleRuntimeStarted;
                runtime.RuntimeStopped -= HandleRuntimeStopped;
            }

            StopWebSocketServer();
        }

        //* این تابع اکشن هایی را که از ترد شبکه آمده اند روی ترد اصلی یونیتی اجرا می کند.
        private void Update()
        {
            while (mainThreadActions.TryDequeue(out Action action))
            {
                try
                {
                    action?.Invoke();
                }
                catch (Exception ex)
                {
                    Debug.LogError("[DedicatedWebSocketServer] Main thread action failed | " + ex.Message);
                }
            }
        }

        //* این تابع رفرنس ران تایم را از همین آبجکت یا سینگلتون پیدا می کند.
        private void EnsureRuntimeReference()
        {
            if (runtime != null) return;

            runtime = GetComponent<DedicatedServerRuntime>();
            if (runtime != null) return;

            runtime = DedicatedServerRuntime.Instance;
        }

        //* این تابع بعد از شروع ران تایم، وب سوکت سرور را شروع می کند.
        private void HandleRuntimeStarted(DedicatedServerConfigData config)
        {
            if (!autoStartOnRuntimeStarted) return;

            StartWebSocketServer();
        }

        //* این تابع بعد از توقف ران تایم، وب سوکت سرور را متوقف می کند.
        private void HandleRuntimeStopped()
        {
            StopWebSocketServer();
        }

        //* این تابع از اینسپکتور برای شروع دستی وب سوکت سرور استفاده می شود.
        [ContextMenu("Start WebSocket Server")]
        public void StartWebSocketServer()
        {
            if (IsListening)
            {
                Debug.Log("[DedicatedWebSocketServer] WebSocket server is already listening.");
                return;
            }

            EnsureRuntimeReference();

            if (runtime == null)
            {
                Debug.LogError("[DedicatedWebSocketServer] DedicatedServerRuntime is missing.");
                return;
            }

            DedicatedServerConfigData config = runtime.GetCurrentConfig();

            if (config == null)
            {
                Debug.LogError("[DedicatedWebSocketServer] Runtime config is missing.");
                return;
            }

            try
            {
                IPAddress listenAddress = ParseListenAddress(config.listenHost);

                listener = new TcpListener(listenAddress, config.listenPort);
                listener.Start();

                serverCts = new CancellationTokenSource();
                IsListening = true;

                Debug.Log("[DedicatedWebSocketServer] Listening | ws://" + config.listenHost + ":" + config.listenPort);

                _ = AcceptLoopAsync(serverCts.Token);
            }
            catch (Exception ex)
            {
                IsListening = false;
                Debug.LogError("[DedicatedWebSocketServer] Start failed | " + ex.Message);
            }
        }

        //* این تابع از اینسپکتور یا کد برای توقف وب سوکت سرور استفاده می شود.
        [ContextMenu("Stop WebSocket Server")]
        public void StopWebSocketServer()
        {
            if (!IsListening && listener == null) return;

            IsListening = false;

            try
            {
                if (serverCts != null)
                {
                    serverCts.Cancel();
                    serverCts.Dispose();
                    serverCts = null;
                }
            }
            catch
            {
            }

            try
            {
                if (listener != null)
                {
                    listener.Stop();
                    listener = null;
                }
            }
            catch
            {
            }

            foreach (DedicatedWebSocketConnection connection in connections.Values)
            {
                _ = connection.CloseAsync("server_stopped");
            }

            connections.Clear();

            Debug.Log("[DedicatedWebSocketServer] Stopped.");
        }

        //* این تابع حلقه پذیرش کانکشن های جدید وب سوکت را اجرا می کند.
        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && IsListening)
            {
                try
                {
                    TcpClient tcpClient = await listener.AcceptTcpClientAsync();

                    if (connections.Count >= Mathf.Max(1, maxConnections))
                    {
                        tcpClient.Close();
                        Debug.LogWarning("[DedicatedWebSocketServer] Connection rejected | server_full");
                        continue;
                    }

                    DedicatedWebSocketConnection connection =
                        new DedicatedWebSocketConnection(tcpClient, cancellationToken);

                    WireConnectionEvents(connection);

                    _ = connection.StartAsync();
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException)
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        EnqueueMain(() => Debug.LogError("[DedicatedWebSocketServer] Socket error in accept loop."));
                    }

                    break;
                }
                catch (Exception ex)
                {
                    EnqueueMain(() => Debug.LogError("[DedicatedWebSocketServer] Accept failed | " + ex.Message));
                }
            }
        }

        //* این تابع رویدادهای کانکشن را به سرور وصل می کند.
        private void WireConnectionEvents(DedicatedWebSocketConnection connection)
        {
            connection.Opened += HandleConnectionOpened;
            connection.TextReceived += HandleTextReceived;
            connection.Closed += HandleConnectionClosed;
        }

        //* این تابع بعد از باز شدن کامل کانکشن وب سوکت اجرا می شود.
        private void HandleConnectionOpened(DedicatedWebSocketConnection connection)
        {
            connections[connection.ConnectionId] = connection;

            EnqueueMain(() =>
            {
                Debug.Log("[DedicatedWebSocketServer] Client connected | connectionId=" +
                          connection.ConnectionId + " | remote=" + connection.RemoteEndPoint +
                          " | count=" + connections.Count);

                ClientConnected?.Invoke(connection);
            });
        }

        //* این تابع پیام متنی دریافتی از کلاینت را پردازش اولیه می کند.
        private void HandleTextReceived(DedicatedWebSocketConnection connection, string text)
        {
            EnqueueMain(() =>
            {
                if (logTextMessages)
                {
                    if (logFullTextMessages)
                    {
                        Debug.Log("[DedicatedWebSocketServer] Text received | connectionId=" +
                                  connection.ConnectionId + " | text=" + text);
                    }
                    else
                    {
                        Debug.Log("[DedicatedWebSocketServer] Text received | connectionId=" +
                                  connection.ConnectionId + " | type=" + ExtractMessageType(text));
                    }
                }

                TextMessageReceived?.Invoke(connection, text);

                if (echoTestMessages)
                {
                    _ = connection.SendTextAsync("{\"type\":\"server_received\",\"ok\":true}");
                }
            });
        }

        //* این تابع بعد از قطع کانکشن کلاینت اجرا می شود.
        private void HandleConnectionClosed(DedicatedWebSocketConnection connection, string reason)
        {
            connections.TryRemove(connection.ConnectionId, out DedicatedWebSocketConnection _);

            EnqueueMain(() =>
            {
                Debug.Log("[DedicatedWebSocketServer] Client disconnected | connectionId=" +
                          connection.ConnectionId + " | reason=" + reason +
                          " | count=" + connections.Count);

                ClientDisconnected?.Invoke(connection, reason);
            });
        }

        //* این تابع فقط تایپ پیام را برای لاگ خلاصه از متن جیسون استخراج می کند.
        private string ExtractMessageType(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            const string pattern = "\"type\":";
            int typeIndex = text.IndexOf(pattern, StringComparison.Ordinal);
            if (typeIndex < 0) return "unknown";

            int valueStart = text.IndexOf('\"', typeIndex + pattern.Length);
            if (valueStart < 0) return "unknown";

            int valueEnd = text.IndexOf('\"', valueStart + 1);
            if (valueEnd <= valueStart) return "unknown";

            return text.Substring(valueStart + 1, valueEnd - valueStart - 1);
        }

        //* این تابع آی پی لیسن را از متن کانفیگ می خواند.
        private IPAddress ParseListenAddress(string listenHost)
        {
            if (string.IsNullOrWhiteSpace(listenHost)) return IPAddress.Loopback;

            string value = listenHost.Trim();

            if (value == "*" || value == "0.0.0.0") return IPAddress.Any;
            if (value == "localhost") return IPAddress.Loopback;

            if (IPAddress.TryParse(value, out IPAddress address)) return address;

            return IPAddress.Loopback;
        }

        //* این تابع اکشن های شبکه را برای اجرا روی ترد اصلی یونیتی صف می کند.
        private void EnqueueMain(Action action)
        {
            if (action == null) return;
            mainThreadActions.Enqueue(action);
        }

        //* این تابع هنگام حذف آبجکت، وب سوکت سرور را متوقف می کند.
        private void OnDestroy()
        {
            StopWebSocketServer();
        }

        /*
        توضیح مکتوب فایل:
        این اسکریپت لیسنر وب سوکت ددیکیتد سرور یونیتی است.
        بعد از شروع DedicatedServerRuntime روی پورت تنظیم شده گوش می دهد.
        در این فاز فقط اتصال، پیام تست، لاگ و پاسخ ساده server_received انجام می شود.
        هنوز auth_ticket، وریفای تیکت و رجیستری پلیرها داخل این فایل فعال نشده اند.
        فاز بعدی پیام auth_ticket را از همین مسیر دریافت و به نود جی اس برای وریفای ارسال می کند.
        */
    }
}
