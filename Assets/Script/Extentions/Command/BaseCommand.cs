using Mirror;
using UnityEngine;

namespace Meta.Commands
{
    [AddComponentMenu("Meta/BaseCommand")]
    [HelpURL("https://github.com/DreamFaver")]
    public abstract class BaseCommand : ICommand
    {
        public abstract string Name { get; }
        public abstract string Help { get; }
        public virtual bool RequiresAuthority => true;
        public abstract string Execute(CommandContext _Context);

        protected bool IsAdmin(NetworkIdentity _Identity)
        {
            return _Identity.isOwned;
        }
        
    }
}