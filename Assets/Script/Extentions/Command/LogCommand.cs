using UnityEngine;

namespace Meta
{
    [CreateAssetMenu(fileName = "Log Message", menuName = "Meta/Console/Log Message")]
    public class LogCommand : ConsoleCommand
    {
        public override string Execute(string[] _Args)
        {
            string _LogText = string.Join(" ", _Args);

            return $"[log] {_LogText}"; // unknow must change to user name
        }
    }
}