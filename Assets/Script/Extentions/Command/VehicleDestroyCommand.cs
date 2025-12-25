using UnityEngine;

namespace Meta.Commands
{
    [AddComponentMenu("Meta/VehicleDestroyCommand")]
    [HelpURL("https://github.com/DreamFaver")]
    public class VehicleDestroyCommand : BaseCommand
    {
        public override string Name => "destroy";
        public override string Help => "Destroy Vehicle Player Looking At";
        public override bool RequiresAuthority => false;
        public override string Execute(CommandContext _Context)
        {
            if (_Context.SenderIdentity == null) return "No Permission.";

            Meta_SimpleVehicleInteraction _Interaction = _Context.SenderIdentity.GetComponent<Meta_SimpleVehicleInteraction>();
            if (_Interaction == null) return "No Interaction Component Found";

            _Interaction.RequestDestroyVehicle();
            return "Vehicle Destroied.";
        }
    }
}