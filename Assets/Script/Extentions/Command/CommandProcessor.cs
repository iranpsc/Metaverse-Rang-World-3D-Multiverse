using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Meta.Commands
{
    [AddComponentMenu("Meta/CommandProcessor")]
    [HelpURL("https://github.com/DreamFaver")]
    public class CommandProcessor
    {
        private readonly Dictionary<string, ICommand> Commands;
        public CommandProcessor(IEnumerable<ICommand> _Command)
        {
            Commands = _Command.ToDictionary(c => c.Name.ToLower());
        }

        public string Proccess(string _Raw, CommandContext _Context)
        {
            if (string.IsNullOrWhiteSpace(_Raw)) return null;

            string[] _Split = _Raw.Split(' ');
            string _CmdName = _Split[0].ToLower();
            _Context.Args = _Split.Skip(1).ToArray();

            if (!Commands.TryGetValue(_CmdName, out ICommand _Command)) { return $"Unknown Command: {_CmdName}"; }

            if (_Command.RequiresAuthority && !_Context.SenderIdentity.isOwned) { return "No Permission"; }

            return _Command.Execute(_Context);
        }
    }
}