using UnityEngine;

namespace Meta
{
    [AddComponentMenu("Meta/UI Audio Manager")]
    [HelpURL("https://google.com")]
    public static class Meta_UIAudioManager
    {
        public static AudioSource Source;
        public static AudioClip Hover;
        public static AudioClip Click;

        public static void PlayHover() => Source?.PlayOneShot(Hover);
        public static void PlayClick() => Source?.PlayOneShot(Click);
    }
}