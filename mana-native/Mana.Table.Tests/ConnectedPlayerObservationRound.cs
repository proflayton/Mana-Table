using Mana.Contracts;

namespace Mana.Table;

// Discovery belongs to one audience. Retiring it must neither starve the other
// owner nor grant a round any comparison, policy or step credit. A typed failed
// command is deliberately outside this bare read-only retirement boundary.
internal static class ConnectedPlayerObservationRound
{
    internal static async Task<bool> ObserveAsync(ConnectedPlayerDriver host, ConnectedPlayerDriver guest,
        MixedGameProgress? progress = null, int steps = 0)
    {
        bool complete = true;
        progress?.Enter(MixedGameStage.HostObservation, steps);
        try { if (await host.ObserveAsync()) progress?.Complete(); else complete = false; }
        catch (EngineObservationSupersededException) { progress?.SupersededObservation(); complete = false; }
        progress?.Enter(MixedGameStage.GuestObservation, steps);
        try { if (await guest.ObserveAsync()) progress?.Complete(); else complete = false; }
        catch (EngineObservationSupersededException) { progress?.SupersededObservation(); complete = false; }
        return complete;
    }
}
