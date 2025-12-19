using Mirror;
using UnityEngine;

namespace Meta.Commands
{
    [AddComponentMenu("Meta/ServerCommandManager")]
    [HelpURL("https://github.com/DreamFaver")]
    public class ServerCommandManager : NetworkBehaviour
    {
        public static ServerCommandManager Instance;
        private CommandProcessor Processor;

        public override void OnStartServer()
        {
            Instance = this;

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
            var _Ctx = new CommandContext(_Sender, null);
            return Processor.Proccess(_Raw, _Ctx);
        }
    }
}