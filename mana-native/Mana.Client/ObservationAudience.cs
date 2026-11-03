using Mana.Contracts;

namespace Mana.Client;

// A transport receipt may advance its captured audience, never replace it.
// Explicit session acceptance remains the owner of audience handoff.
internal readonly record struct ObservationAudience(string GameId, int ViewerId)
{
    internal ObservationAudience(GameSnapshot state) : this(state.Id, state.ViewerId) { }

    internal void Validate(GameSnapshot? state)
    {
        if (state != null && (state.Id != GameId || state.ViewerId != ViewerId))
            throw new IOException("The match connection returned an observation for another game or viewer.");
    }
}
