using Mana.Contracts;

namespace Mana.Renderer;

public sealed record ManaPoolCue(int PlayerId, string Symbol, int Before, int After, double Started)
{
    public int Delta => After - Before;
    public double Progress(double now) => Math.Clamp((now - Started) / ManaPoolPresence.Duration, 0, 1);
}

// Compare copied public pool values. A loss may be payment, emptying, or conversion;
// its animation does not assert a cause or delay the authoritative count.
public sealed class ManaPoolPresence
{
    public const double Duration = 1.1;
    private readonly Dictionary<(int Player, string Symbol), ManaPoolCue> cues = [];
    private Dictionary<(int Player, string Symbol), int> amounts = [];
    private HashSet<int> players = [];
    private string? gameId;
    private long revision;

    public void Clear()
    {
        cues.Clear(); amounts.Clear(); players.Clear(); gameId = null; revision = 0;
    }

    public void Observe(GameSnapshot? previous, GameSnapshot current, double now)
    {
        if (current.Status == "finished") { Clear(); return; }
        Expire(now);
        if (gameId == null) {
            if (previous == null || previous.Id != current.Id || previous.Status == "finished") {
                Baseline(current); return;
            }
            Baseline(previous);
        }
        if (gameId != current.Id) { cues.Clear(); Baseline(current); return; }
        if (current.Revision <= revision) return;

        var currentPlayers = current.Players.Where(p => !p.Eliminated).Select(p => p.Id).ToHashSet();
        var currentAmounts = CopyAmounts(current);
        foreach (var key in cues.Keys.Where(key => !currentPlayers.Contains(key.Player)).ToArray()) cues.Remove(key);
        foreach (var key in amounts.Keys.Concat(currentAmounts.Keys).Distinct()) {
            if (!players.Contains(key.Player) || !currentPlayers.Contains(key.Player)) continue;
            int before = amounts.GetValueOrDefault(key), after = currentAmounts.GetValueOrDefault(key);
            if (before != after) cues[key] = new(key.Player, key.Symbol, before, after, now);
        }
        amounts = currentAmounts; players = currentPlayers; revision = current.Revision;
    }

    public ManaPoolCue? For(int playerId, string symbol, double now)
        => cues.TryGetValue((playerId, symbol), out var cue) && now - cue.Started < Duration ? cue : null;

    public IReadOnlyList<ManaPoolCue> ForPlayer(int playerId, double now)
        => cues.Values.Where(cue => cue.PlayerId == playerId && now - cue.Started < Duration)
            .OrderBy(cue => cue.Symbol, StringComparer.Ordinal).ToArray();

    private void Baseline(GameSnapshot state)
    {
        gameId = state.Id; revision = state.Revision; amounts = CopyAmounts(state);
        players = state.Players.Where(p => !p.Eliminated).Select(p => p.Id).ToHashSet();
    }

    private static Dictionary<(int Player, string Symbol), int> CopyAmounts(GameSnapshot state)
        => state.Players.Where(p => !p.Eliminated).SelectMany(p => p.Mana.Where(part => part.Value > 0)
            .Select(part => new KeyValuePair<(int, string), int>((p.Id, part.Key), part.Value)))
            .ToDictionary(part => part.Key, part => part.Value);

    private void Expire(double now)
    {
        foreach (var key in cues.Where(pair => now - pair.Value.Started >= Duration).Select(pair => pair.Key).ToArray()) cues.Remove(key);
    }
}
