using UnityEngine;

public class Diagram : MonoBehaviour
{
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {

    }

    // Update is called once per frame
    void Update()
    {

    }
}

//*ساختار پوشه Assets برای Network
/* 

Assets /
└── Network /
    ├── Core /
    │   ├── Interfaces /
    │   │   ├── IRequest.cs
    │   │   ├── IResponse.cs
    │   │   └── IWebSocketClient.cs
    │   ├── Models /
    │   │   ├── RequestModel.cs
    │   │   ├── ResponseModel.cs
    │   │   └── NetworkError.cs
    │   └── Utils /
    │       ├── JSONSerializer.cs
    │       └── URLBuilder.cs
    ├── HTTP /
    │   ├── HTTPClient.cs
    │   ├── HTTPRetryPolicy.cs
    │   ├── HTTPHeadersManager.cs
    │   └── NetworkLogger.cs
    ├── WebSocket /
    │   ├── WebSocketClient.cs
    │   ├── ReconnectManager.cs
    │   ├── MessageQueue.cs
    │   ├── HeartbeatMonitor.cs
    │   └── WebSocketMessage.cs
    ├── Security /
    │   ├── AuthManager.cs
    │   ├── TokenStorage.cs
    │   ├── PlatformTokenStorage/
    │   │   ├── WebGLTokenStorage.cs
    │   │   ├── WindowsTokenStorage.cs
    │   │   └── QuestTokenStorage.cs
    │   ├── CryptoService.cs
    │   └── RefreshTokenHandler.cs
    └── Test/
        ├── NetworkTestScene.unity
        ├── NetworkTestController.cs
        └── TestUI.cs
 */