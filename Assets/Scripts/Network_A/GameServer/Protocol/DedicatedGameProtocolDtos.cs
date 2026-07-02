using System;

namespace Network_A.GameServer.Protocol
{
    [Serializable]
    public class DedicatedMessageTypeDto
    {
        public string type;
    }

    [Serializable]
    public class DedicatedAuthTicketMessageDto
    {
        public string type;
        public string ticketId;
        public string signature;
        public string userId;
        public string roomId;
        public string serverId;
        public string sessionId;
        public string playerId;
        public string userName;
    }

    [Serializable]
    public class DedicatedVerifyTicketRequestDto
    {
        public string serviceToken;
        public string serverId;
        public string roomId;
        public string userId;
        public string ticketId;
        public string signature;
        public string sessionId;
        public string connectionId;
        public string playerId;
        public string userName;
        public DedicatedVerifyTicketMetadataDto metadata;
    }

    [Serializable]
    public class DedicatedVerifyTicketMetadataDto
    {
        public string source;
        public string platform;
        public string unityVersion;
        public string connectionType;
    }

    [Serializable]
    public class DedicatedVerifyTicketResponseDto
    {
        public bool success;
        public string reason;
        public string message;
        public DedicatedVerifyTicketResponseDataDto data;
        public long ts;
    }

    [Serializable]
    public class DedicatedVerifyTicketResponseDataDto
    {
        public DedicatedVerifiedTicketDto ticket;
        public DedicatedVerifiedSessionDto session;
        public DedicatedVerifiedPlayerDto player;
    }

    [Serializable]
    public class DedicatedVerifiedTicketDto
    {
        public string ticketId;
        public string userId;
        public string roomId;
        public string serverId;
        public long expiresAt;
        public string signature;
    }

    [Serializable]
    public class DedicatedVerifiedSessionDto
    {
        public string sessionId;
        public string roomId;
        public string serverId;
        public string status;
        public int maxPlayers;
        public int currentPlayers;
        public string region;
        public string zone;
    }

    [Serializable]
    public class DedicatedVerifiedPlayerDto
    {
        public string userId;
        public string playerId;
        public string connectionId;
    }

    [Serializable]
    public class DedicatedAuthOkMessageDto
    {
        public string type;
        public bool ok;
        public string reason;
        public string userId;
        public string playerId;
        public string connectionId;
        public string roomId;
        public string serverId;
        public string sessionId;
    }

    [Serializable]
    public class DedicatedAuthFailedMessageDto
    {
        public string type;
        public bool ok;
        public string reason;
        public string message;
    }



    [Serializable]
    public class DedicatedMirrorLikeRouteDto
    {
        public string phase;
        public string mirrorRoute;
        public string type;
        public string roomId;
        public string serverId;
        public string connectionId;
        public string userId;
        public string playerId;
        public string reason;
        public long ts;
    }

    [Serializable]
    public class DedicatedMirrorLikeAckDto
    {
        public string type;
        public bool ok;
        public string reason;
        public string mirrorRoute;
        public string requestId;
        public string connectionId;
        public string userId;
        public string playerId;
        public long ts;
    }

    /*
    توضیح مکتوب فایل:
    این فایل دی تی اوهای پیام های ساده بین کلاینت و یونیتی ددیکیتد سرور را نگه می دارد.
    پیام auth_ticket از کلاینت به ددیکیتد سرور می آید.
    بعد ددیکیتد سرور درخواست verify-ticket را به نود جی اس می فرستد.
    پاسخ auth_ok یا auth_failed از ددیکیتد سرور به کلاینت برمی گردد.
    این فایل فقط قرارداد داده است و منطق اجرایی ندارد.
    */
}
