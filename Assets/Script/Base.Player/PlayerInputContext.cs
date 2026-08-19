using System;

public static class PlayerInputContext
{
    private static InputContext _current = InputContext.Gameplay;

    public static InputContext Current => _current;

    public static event Action<InputContext> OnContextChanged;

    public static void Set(InputContext _Context)
    {
        if (_current == _Context)
            return;

        _current = _Context;
        OnContextChanged?.Invoke(_current);
    }

    public static bool Is(InputContext _Context)
    {
        return _current == _Context;
    }
}
