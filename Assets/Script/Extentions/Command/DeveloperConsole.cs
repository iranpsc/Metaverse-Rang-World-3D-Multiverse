using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Meta
{
    [AddComponentMenu("Meta/Meta_CommandHandler")]
    [HelpURL("https://github.com/DreamFaver")]
    public class DeveloperConsole
    {
        private readonly string Prefix;
        private readonly IEnumerable<IConsoleCommand> Command;
        public DeveloperConsole(string _Prefix, IEnumerable<IConsoleCommand> _Command)
        {
            Prefix = _Prefix;
            Command = _Command;
        }
        public string ExecuteCommand(string _InputValue)
        {
            if (!_InputValue.StartsWith(Prefix)) { return null; }
            
            _InputValue = _InputValue.Remove(0, Prefix.Length);

            string[] _InputSplit = _InputValue.Split(' ');

            string _CommandInput = _InputSplit[0];
            string[] _Args = _InputSplit.Skip(1).ToArray();

            return ExecuteCommand(_CommandInput, _Args);
        }
        public string ExecuteCommand(string _CommandInput, string[] _Args)
        {
            foreach (var _Command in Command)
            {
                if (!_CommandInput.Equals(_Command.CommandWord, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string _LogMessage = _Command.Execute(_Args);

                if (!string.IsNullOrEmpty(_LogMessage))
                {
                    return _LogMessage;
                }
            }
            return $"[Command] '{_CommandInput}' Not Recognized Or Failed";
        }
    }
}