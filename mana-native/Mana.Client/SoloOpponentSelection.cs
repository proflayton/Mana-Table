using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Client;

public sealed record SoloOpponentChoice(string OpponentId, bool Explicit);

/// <summary>
/// One reviewed solo roster. Explicit choices survive a new game; automatic
/// choices survive refreshes, but are retired when their preparation ends.
/// </summary>
public sealed class SoloOpponentSelection
{
    public ImmutableArray<SoloOpponentChoice> Choices { get; }
    private SoloOpponentSelection(ImmutableArray<SoloOpponentChoice> choices) => Choices = choices;
    public static SoloOpponentSelection Empty { get; } = new([]);

    public static SoloOpponentSelection ExplicitIds(IEnumerable<string> ids) => new(
        ids.Select(id => new SoloOpponentChoice(id, true)).ToImmutableArray());

    public SoloOpponentSelection ClearAutomatic() => new(Choices.Select(choice => choice.Explicit
        ? choice : new SoloOpponentChoice("", false)).ToImmutableArray());

    public SoloOpponentSelection Choose(SoloSetup setup, int seat, string opponentId)
    {
        if (seat < 0 || seat >= Choices.Length || !setup.Opponents.Any(opponent => opponent.Id == opponentId))
            throw new InvalidOperationException("Choose an available AI deck for this seat.");
        return new(Choices.SetItem(seat, new(opponentId, true)));
    }

    public int[] Indices(SoloSetup setup) => Choices.Select(choice => {
        int index = setup.Opponents.IndexOf(setup.Opponents.FirstOrDefault(opponent => opponent.Id == choice.OpponentId)!);
        return index >= 0 ? index : throw new InvalidOperationException("An AI deck changed. Refresh the table before playing.");
    }).ToArray();

    public static SoloOpponentSelection Prepare(SoloSetup setup, SoloOpponentSelection previous, int seed, int count = 3)
    {
        if (count is < 1 or > 5 || setup.MaxPlayers < 2 || setup.Opponents.IsDefaultOrEmpty
            || setup.Opponents.Any(opponent => opponent == null || string.IsNullOrWhiteSpace(opponent.Id)
                || opponent.CompositionId == null || opponent.CompositionId.Length != 0 && !PublicChoiceFacts.ValidId(opponent.CompositionId))
            || setup.Opponents.Select(opponent => opponent.Id).Distinct(StringComparer.Ordinal).Count() != setup.Opponents.Length)
            throw new InvalidOperationException("The engine has no valid AI deck choices for this table.");
        var available = setup.Opponents.ToDictionary(opponent => opponent.Id, StringComparer.Ordinal);
        if (setup.DefaultOpponentIds.Length > Math.Min(5, setup.MaxPlayers - 1)
            || setup.DefaultOpponentIds.Any(id => id == null || !available.ContainsKey(id)))
            throw new InvalidOperationException("The prepared AI table does not match its current deck choices.");

        string Composition(AiOpponent opponent) => opponent.CompositionId.Length > 0
            ? "composition:" + opponent.CompositionId : "id:" + opponent.Id;
        // Older providers have no default pod. Shuffle a canonical list once
        // per preparation, rather than treating catalog/recency order as intent.
        var fallback = setup.Opponents.GroupBy(Composition).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.OrderBy(opponent => opponent.Id, StringComparer.Ordinal).First()).ToArray();
        new Random(seed).Shuffle(fallback);
        var candidates = setup.DefaultOpponentIds.Select(id => available[id]).Concat(fallback)
            .DistinctBy(opponent => opponent.Id).ToArray();
        var selected = new SoloOpponentChoice?[count];
        var uses = new Dictionary<string, int>(StringComparer.Ordinal);
        void Reserve(int seat, SoloOpponentChoice choice)
        {
            selected[seat] = choice;
            string key = Composition(available[choice.OpponentId]);
            uses[key] = uses.GetValueOrDefault(key) + 1;
        }
        // Refreshes preserve the actual reviewed roster, including deliberate
        // duplicate choices. Only vacant/retired automatic slots are filled.
        for (int seat = 0; seat < Math.Min(count, previous.Choices.Length); seat++)
            if (available.ContainsKey(previous.Choices[seat].OpponentId)) Reserve(seat, previous.Choices[seat]);
        for (int seat = 0; seat < count; seat++) {
            if (selected[seat] != null) continue;
            var next = candidates.OrderBy(opponent => uses.GetValueOrDefault(Composition(opponent))).First();
            Reserve(seat, new(next.Id, false));
        }
        return new(selected.Select(choice => choice!).ToImmutableArray());
    }
}
