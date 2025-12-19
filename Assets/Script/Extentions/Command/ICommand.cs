namespace Meta.Commands
{
    public interface ICommand
    {
        string Name { get; }
        string Help { get; }
        bool RequiresAuthority { get; }
        string Execute(CommandContext _Context);
    }
}