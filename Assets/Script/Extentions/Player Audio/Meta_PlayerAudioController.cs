using System.Collections.Generic;
using UnityEngine;

namespace Meta.PlayerAudio
{
    [AddComponentMenu("Meta/Meta_PlayerAudioController")]
    [HelpURL("https://github.com/DreamFaver")]
    [System.Serializable]
    public class MovementSound
    {
        public string StateName;
        public AudioClip SoundEffect;
    }
    public class Meta_PlayerAudioController : MonoBehaviour
    {
        [Header("References")]

        [Header("Settings")]
        public List<MovementSound> MovementSounds;
        public AudioSource AudioSource;

        [SerializeField] private string CurrentState = string.Empty;

        [Header("Inputs")]


        [Header("Debugger")]
        [SerializeField] private bool EnableLog;

        public void UpdateMovementState(string _StateName)
        {
            if (CurrentState == _StateName) return;
                
            CurrentState = _StateName;

            if (string.IsNullOrEmpty(_StateName))
            {
                AudioSource.Stop();
                return;
            }

            MovementSound _Sound = MovementSounds.Find(s => s.StateName == _StateName);

            if (_Sound != null && _Sound.SoundEffect != null)
            {
                AudioSource.Stop();
                AudioSource.clip = _Sound.SoundEffect;

                AudioSource.loop = _StateName != "Jumping";

                AudioSource.Play();
            }
            else
            {
                AudioSource.Stop();
            }    
        }

    }
}