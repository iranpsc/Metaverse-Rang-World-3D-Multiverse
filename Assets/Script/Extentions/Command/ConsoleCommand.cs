using UnityEngine;

namespace Meta
{
    [AddComponentMenu("Meta/Meta_CommandManager")]
    [HelpURL("https://google.com")]
    public abstract class ConsoleCommand : ScriptableObject, IConsoleCommand
    {
        [SerializeField] private string _commandWord = string.Empty;

        public string CommandWord => _commandWord;

        public abstract string Execute(string[] _Args);
    }
}