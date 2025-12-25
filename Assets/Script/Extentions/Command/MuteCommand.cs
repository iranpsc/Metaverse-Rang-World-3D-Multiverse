// MuteCommand.cs (Refactored for robustness)
using UnityEngine;
using UnityEngine.Audio;

namespace Meta.Commands
{
    public class MuteCommand : BaseCommand
    {
        [SerializeField] private AudioMixer targetMixer = Resources.Load<AudioMixer>("MainMixer");

        [SerializeField] private string MasterVolumeParameter = "MasterVolume";

        private bool isMuted = false;
        private float lastVolume = 0f;
        private const float MuteVolume = -80f;

        public override string Name => "mute";

        public override string Help => "Toggle Mute The Game Audio";
        public override bool RequiresAuthority => false;

        public override string Execute(CommandContext _Context)
        {
            if (targetMixer == null)
            {
                return "<color=#FF0000>Error:</color> Audio Mixer reference is missing on the MuteCommand asset.";
            }

            if (_Context.Args.Length > 0)
            {
                string arg = _Context.Args[0].ToLowerInvariant();
                if (arg == "on" || arg == "true")
                {
                    isMuted = false; // Intend to mute (set opposite for toggle below)
                }
                else if (arg == "off" || arg == "false")
                {
                    isMuted = true; // Intend to unmute
                }
                else
                {
                    return "<color=#FF0000>Error:</color> Invalid argument. Use 'on' or 'off'.";
                }
            }

            // --- Execution ---
            isMuted = !isMuted; // Toggle state

            if (isMuted)
            {
                // MUTE LOGIC
                float currentVolume;

                // 1. Get the current volume BEFORE muting
                if (!targetMixer.GetFloat(MasterVolumeParameter, out currentVolume))
                {
                    return $"<color=#FF0000>Error:</color> Could not find parameter '{MasterVolumeParameter}' in the Audio Mixer. Check spelling and exposure.";
                }

                // Only store volume if it's above the mute threshold (i.e., not already muted)
                if (currentVolume > MuteVolume + 1f)
                {
                    lastVolume = currentVolume;
                }

                // 2. Mute the audio. SetFloat returns a bool (success/failure) but is usually ignored.
                targetMixer.SetFloat(MasterVolumeParameter, MuteVolume);

                return $"<color=#00FF00>Success:</color> Game audio has been muted. Previous volume stored: {lastVolume:F1} dB.";
            }
            else
            {
                // UNMUTE LOGIC

                // 1. Determine volume to restore: use stored volume, or default to 0f if stored volume is muted/low.
                float volumeToRestore = (lastVolume < MuteVolume + 1f) ? 0f : lastVolume;

                // 2. Restore the stored volume level.
                targetMixer.SetFloat(MasterVolumeParameter, volumeToRestore);

                // 3. Confirm the final volume for the user's log
                targetMixer.GetFloat(MasterVolumeParameter, out float finalVolume);

                return $"<color=#00FF00>Success:</color> Game audio has been restored to {finalVolume:F1} dB.";
            }
        }
    }
}