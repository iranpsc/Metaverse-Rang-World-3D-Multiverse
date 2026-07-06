using UnityEngine;

[CreateAssetMenu(fileName = "FPS", menuName = "Meta/Command/FPS")]
public class FPSCommand : ConsoleCommandSO
{
    public override void ExecuteLocal(CommandContext _Context)
    {
        // محاسبه FPS روی Client Local
        IsNetworkCommand = false;
        float fps = 1f / Time.unscaledDeltaTime;
        _Context.Console.LogInfo($"FPS: {(int)fps}");
    }

    public override void ExecuteServer(CommandContext _Context)
    {
        // چیزی اجرا نمی‌شود، Local-only
    }
}
