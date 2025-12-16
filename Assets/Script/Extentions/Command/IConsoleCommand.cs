using UnityEngine;

namespace Meta
{
    [AddComponentMenu("Meta/ICommand")]
    public interface IConsoleCommand
    {
        string CommandWord { get; }
        string Execute(string[] _Args);
    }
}