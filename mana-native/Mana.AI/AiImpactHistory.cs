using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.AI;

// Public committed mutations, not announcements, proposals or current-board
// inference. Attribution stays copied from the event; utility and age belong
// to the policy. The diagnostic ring and this evidence ring are independent.
internal sealed class AiImpactHistory(int capacity)
{
    private readonly Queue<AiObservedEvent> events = [];
    internal ImmutableArray<AiObservedEvent> Snapshot => events.ToImmutableArray();

    internal void Observe(AiObservedEvent entry)
    {
        if (!Evidence(entry)) return;
        events.Enqueue(entry);
        while (events.Count > capacity) events.Dequeue();
    }

    internal static void ValidateRecorded(ImmutableArray<AiObservedEvent> recorded, GameSnapshot state)
    {
        if (recorded.IsDefault || recorded.Length > 4096) throw Invalid();
        var players = state.Players.Select(player => player.Id).ToHashSet();
        long previous = -1;
        foreach (var entry in recorded) {
            if (entry == null || entry.Id <= previous || entry.Turn < 0 || entry.Turn > state.Turn || !Evidence(entry)
                || entry.ExecutionId == null || entry.ExecutionId.Length > 0 && !UndoFacts.Identifier(entry.ExecutionId)
                || !Known(entry.ActorId) || !Known(entry.CauseControllerId)
                || !Known(entry.Impact!.PlayerId) || !Known(entry.Impact.SourceControllerId)
                || !Known(entry.Impact.RecipientControllerId)) throw Invalid();
            previous = entry.Id;
        }
        bool Known(int? player) => player == null || players.Contains(player.Value);
    }

    private static bool Evidence(AiObservedEvent entry)
        => entry.Impact is { Kind: "damage", Amount: > 0 }
            || entry.Impact is { Kind: "zone", Amount: 1, FromZone: "Battlefield",
                ToZone: "Graveyard" or "Exile" or "Hand" or "Library" }
                && entry.ExecutionStep == ExecutionStep.Resolution;

    private static ArgumentException Invalid()
        => new("Recorded impact history requires bounded, ordered committed facts from this game's known players and past turns.");
}
