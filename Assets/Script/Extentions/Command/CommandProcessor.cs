using System.Collections.Generic;
using UnityEngine;

public class CommandProcessor
{
    public void Execute(string _Input, CommandContext _Context)
    {
        if (string.IsNullOrWhiteSpace(_Input)) return;

        if (!_Input.StartsWith("/"))
        {
            //ChatSystem.Instance.SendMessage(_Input); // اتصال به سیستم چت بازی
            return;
        }
        
        string _CommandLine = _Input.Substring(1);
        string[] _Split = _CommandLine.Split(" ");

        string _CommandKey = _Split[0].ToLower();
        
        if (!ConsoleDatabase.Instance.HasCommand(_CommandKey))
        {
            _Context.Console.LogError($"Command Not Found: {_CommandKey}");
            return;
        }

        var _Command = ConsoleDatabase.Instance.GetCommand(_CommandKey);
        _Context.Args = _Split.Length > 1 ? _Split[1..] : new string[0];

        if (_Command.IsNetworkCommand)
        {
            NetworkCommandExecutor.Instance.ExecuteCommand(_Command, _Context);
        }
        else
        {
            _Command.ExecuteLocal(_Context);
        }
    }
}
