using System.Collections.Generic;
using UnityEngine;

public class ConsoleDatabase : MonoBehaviour
{
    public static ConsoleDatabase Instance;

    [Header("Registered Commands")]
    [SerializeField] private List<ConsoleCommandSO> CommandList = new List<ConsoleCommandSO>();

    private Dictionary<string, ConsoleCommandSO> CommandMap;

    private void Awake()
    {
        if (Instance != null)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);

        BuildDatabase();
    }

    private void BuildDatabase()
    {
        CommandMap = new Dictionary<string, ConsoleCommandSO>();

        foreach (var _Command in CommandList)
        {
            string _Key = _Command.CommandName.ToLower();

            if (CommandMap.ContainsKey(_Key) )
            {
                Debug.LogError($"[ConsoleDatabase] Duplicated Command: {_Key}");
                CommandMap.Remove(_Key);
                continue;
            }

            CommandMap.Add(_Key, _Command);
        }
    }

    public ConsoleCommandSO GetCommand(string _CommandName)
    {
        string _Key = _CommandName.ToLower();

        if (CommandMap.TryGetValue(_Key, out var _Command))
                return _Command;

        Debug.LogError($"[ConsoleDatabase] Command Not Found: {_CommandName}");
        return null;
    }

    public bool HasCommand(string _CommandName)
    {
        return CommandMap.ContainsKey(_CommandName.ToLower());
    }

    public IReadOnlyCollection<ConsoleCommandSO> GetAllCommand()
    {
        return CommandMap.Values;
    }
}
