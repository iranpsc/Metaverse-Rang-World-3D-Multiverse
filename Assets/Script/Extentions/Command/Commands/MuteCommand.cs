using UnityEngine;
using UnityEngine.Audio;

[CreateAssetMenu(fileName ="Mute", menuName ="Meta/Command/Mute")]
public class MuteCommand : ConsoleCommandSO
{
    [Header("Audio")]
    [SerializeField] private AudioMixer TargetMixer;
    [SerializeField] private string MasterVolumeParameter = "MasterVolume";

    public static bool IsMuted = false;
    public static float LastVolume = 0f;

    public const float MuteVolume = -80f;

    public override void ExecuteLocal(CommandContext _Context)
    {
        if (TargetMixer == null)
        {
            _Context.SetResult("AudioMixer Not Assigned", CommandResultType.Error);
            return;
        }
        if (_Context.Args.Length > 0)
        {
            string _Args = _Context.Args[0].ToLowerInvariant();

            if (_Args == "on" || _Args == "true")
            {
                IsMuted = false;
            }
            else if (_Args == "off" || _Args == "false")
            {
                IsMuted = true;
            }
            else
            {
                _Context.SetResult("Invalid Argument. Use /mute , /mute on, /mute off", CommandResultType.Error);
                return;
            }
        }
        // -------- Toggle --------
        IsMuted = !IsMuted;

        if (IsMuted)
        {
            // --- MUTE ---
            if (!TargetMixer.GetFloat(MasterVolumeParameter, out float _CurrentVolume))
            {
                _Context.Console.LogError($"AudioMixer parameter '{MasterVolumeParameter}' not found");
                return;
            }

            if (_CurrentVolume > MuteVolume + 1f)
                LastVolume = _CurrentVolume;

            TargetMixer.SetFloat(MasterVolumeParameter, MuteVolume);

            _Context.Console.LogSuccess($"Audio muted (stored {_CurrentVolume:F1} dB)");
        }
        else
        {
            // --- UNMUTE ---
            float _RestoreVolume =
                (LastVolume < MuteVolume + 1f) ? 0f : LastVolume;

            TargetMixer.SetFloat(MasterVolumeParameter, _RestoreVolume);

            _Context.Console.LogSuccess($"Audio restored to {_RestoreVolume:F1} dB");
        }
    }

    public override void ExecuteServer(CommandContext _Context)
    {
        // ❌ never runs
    }
}
