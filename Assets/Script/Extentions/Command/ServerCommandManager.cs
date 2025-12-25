using Meta.Commands;
using Mirror;

public class ServerCommandManager : NetworkBehaviour
{
    public static ServerCommandManager Instance;

    private CommandProcessor Processor;

    private void Awake()
    {
        if (Instance != null)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    public override void OnStartServer()
    {
        Processor = new CommandProcessor(new ICommand[]
        {
            new HelpCommand(),
            new UnstuckCommand(),
            new TeleportCommand(),
            new ClearCommand(),
            new MuteCommand(),
            new FpsCommand(),
            new LogCommand(),
            new VehicleDestroyCommand(),
        });
    }

    [Server]
    public string Execute(string _Raw, NetworkConnectionToClient _Sender)
    {
        var _Split = _Raw.Split(' ');
        var _Args = _Split.Length > 1 ? _Split[1..] : System.Array.Empty<string>();

        var _Ctx = new CommandContext(_Sender, _Args);

        return Processor.Proccess(_Raw, _Ctx);
    }
}
