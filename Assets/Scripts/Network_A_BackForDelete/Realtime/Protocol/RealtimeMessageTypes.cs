namespace Network_A.Realtime.Protocol
{
    public static class RealtimeMessageTypes
    {
        public const string Ping = "ping";
        public const string Pong = "pong";
        public const string Auth = "auth";
        public const string ServerHello = "server_hello";
        public const string AuthTicket = "auth_ticket";
        public const string AuthOk = "auth_ok";
        public const string AuthFailed = "auth_failed";
        public const string Ack = "ack";
        public const string Error = "error";

        public const string CreateRoom = "create_room";
        public const string ListRooms = "list_rooms";
        public const string RoomCreated = "room_created";
        public const string RoomUpdated = "room_updated";
        public const string RoomClosed = "room_closed";

        public const string JoinRoom = "join_room";
        public const string LeaveRoom = "leave_room";
        public const string PlayerAction = "player_action";
        public const string PlayerState = "player_state";
        public const string PlayerStateAccepted = "player_state_accepted";
        public const string PlayerJoined = "player_joined";
        public const string PlayerLeft = "player_left";
        public const string RoomMembersRequest = "room_members_request";
        public const string RoomMembersSnapshot = "room_members_snapshot";
        public const string WorldEvent = "world_event";

        public const string Spawn = "spawn";
        public const string Despawn = "despawn";
        public const string Snapshot = "snapshot";
        public const string Command = "command";
        public const string ClientRpc = "client_rpc";
        public const string TargetRpc = "target_rpc";
        public const string SyncVar = "sync_var";
        public const string NetworkTransform = "network_transform";
        public const string Ownership = "ownership";
        public const string PlayerInput = "player_input";

        public const string ChatMessage = "message";

        public const string VoiceOffer = "offer";
        public const string VoiceAnswer = "answer";
        public const string VoiceIce = "ice";
    }
}

//* این فایل نام تایپ های رسمی پیام ریل تایم را نگه می دارد.
//* این تایپ ها باید با قرارداد سرور هم نام بمانند.
