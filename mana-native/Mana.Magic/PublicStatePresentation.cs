using Mana.Contracts;

namespace Mana.Magic;

/// <summary>Readable captions depend on the declared shape, never a mechanic name.</summary>
public static class PublicStatePresentation
{
    public static string Caption(PublicState state)
    {
        if (state.Value is not { } value) return state.Label;
        if (state.Kind == "ability") return state.Label + (value > 1 ? " ×" + value : "");
        if (state.Maximum is { } maximum)
            return $"{state.Label} {value}/{maximum}" + (value == maximum ? " MAX" : "");
        return state.Label + " " + value;
    }

    public static string Description(PublicState state) => Caption(state)
        + (string.IsNullOrWhiteSpace(state.Detail) ? "" : ": " + state.Detail);
}
