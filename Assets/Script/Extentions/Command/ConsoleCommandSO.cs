using UnityEngine;

public abstract class ConsoleCommandSO : ScriptableObject
{
    public string CommandName;
    public string CommandDescription;
    public bool IsNetworkCommand;

    public abstract void ExecuteLocal(CommandContext _Context);
    public abstract void ExecuteServer(CommandContext _Context);
}