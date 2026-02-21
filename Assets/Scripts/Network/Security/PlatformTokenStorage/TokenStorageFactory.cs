namespace Assets.Scripts.Network.Security.PlatformTokenStorage
{
    public static class TokenStorageFactory
    {
        public static ITokenStorage Create()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return new WebGLTokenStorage();
#elif UNITY_STANDALONE_WIN && !UNITY_EDITOR
            return new WindowsTokenStorage();
#elif UNITY_ANDROID && !UNITY_EDITOR
            return new QuestTokenStorage();
#else
            return new EditorTokenStorage();
#endif
        }
    }
}
