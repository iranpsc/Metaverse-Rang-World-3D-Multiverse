namespace Network_A.Realtime.Protocol
{
    public static class RealtimeMessageTypes
    {
        public const string Ping = "ping";
        public const string Pong = "pong";
        public const string Auth = "auth";
        public const string AuthOk = "auth_ok";
        public const string AuthFailed = "auth_failed";
        public const string Ack = "ack";
        public const string Error = "error";

        public const string JoinRoom = "join_room";
        public const string LeaveRoom = "leave_room";
        public const string PlayerAction = "player_action";
        public const string PlayerState = "player_state";
        public const string PlayerJoined = "player_joined";
        public const string PlayerLeft = "player_left";
        public const string WorldEvent = "world_event";

        public const string ChatMessage = "message";

        public const string VoiceOffer = "offer";
        public const string VoiceAnswer = "answer";
        public const string VoiceIce = "ice";
    }
}

//* این فایل نام تایپ های رسمی پیام ریل تایم را نگه می دارد.
//* این تایپ ها باید با قرارداد سرور هم نام بمانند.
