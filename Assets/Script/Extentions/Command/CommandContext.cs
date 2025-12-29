using Mirror;
using UnityEngine;

public enum CommandResultType
{
    Info,
    Success,
    Error
}
public class CommandContext
{
    public NetworkIdentity Sender;
    public string[] Args;

    public string ResultMessage;
    public CommandResultType ResultType;
    public ConsoleManager Console;

    public void SetResult(string _Msg, CommandResultType _Type)
    {
        ResultMessage = _Msg;
        ResultType = _Type;
    }
}
