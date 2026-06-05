using System;

namespace Network_A.Auth
{
    [Serializable]
    public class AuthUserDto
    {
        public string id;
        public string emailOrUsername;
        public long createdAtUnix;
    }

    [Serializable]
    public class AuthResponseDto
    {
        public bool success;
        public string message;
        public string accessToken;
        public string refreshToken;
        public int expiresIn;
        public AuthUserDto user;
    }

    [Serializable]
    public class GetUserDataResponseDto
    {
        public bool success;
        public string message;
        public AuthUserDto user;
    }
}
