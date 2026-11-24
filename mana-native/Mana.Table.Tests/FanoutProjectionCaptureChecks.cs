using System.Text.Json;
using Mana.Contracts;

namespace Mana.Table;

internal static class FanoutProjectionCaptureChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var empty = new FanoutAcceptedProjection();
        bool emptyRejected = false;
        try { empty.Complete(); } catch (InvalidDataException) { emptyRejected = true; }
        using var release = new ManualResetEventSlim();
        int serializations = 0;
        var capture = new FanoutAcceptedProjection(serialize: state => { Interlocked.Increment(ref serializations); return JsonSerializer.Serialize(state); }, beforeDrain: () => release.Wait());
        var first = PresentationSmoke.Example() with { Revision = 10, BoardRevision = 10 };
        var repeatedBoard = first with { Revision = 11 };
        var changed = first with { Revision = 12, BoardRevision = 11 };
        capture.Observe(first); capture.Observe(first); capture.Observe(first with { }); capture.Observe(repeatedBoard);
        var owner = repeatedBoard.Players[0]; var hand = owner.Zone("Hand");
        var privateChange = repeatedBoard with { Players = repeatedBoard.Players.SetItem(0,
            owner with { Zones = owner.Zones.SetItem(owner.Zones.IndexOf(hand), hand with {
                Cards = hand.Cards.SetItem(0, hand.Cards[0] with { Name = "A changed private hand face" })
            }) }) };
        capture.Observe(privateChange);
        var eventChange = privateChange with { Activity = [new(1, privateChange.Turn, privateChange.PhaseKey,
            "draw", privateChange.ViewerId, "A card was drawn.", null, null)] };
        capture.Observe(eventChange);
        var correlationChange = eventChange with { Executions = new(1, 0, [], []) };
        capture.Observe(correlationChange); capture.Observe(changed);
        check(serializations == 0, "Receipt callbacks return without serializing even while the evidence consumer is blocked");
        capture.Freeze(); release.Set();
        var window = capture.Complete();
        capture.Observe(changed with { Revision = 13 });
        check(emptyRejected && window.Observations.Length == 6 && window.AcceptedCallbacks == 8 && window.IdenticalCallbacks == 2
                && ReferenceEquals(window.Observations[0], first) && ReferenceEquals(window.Observations[1], repeatedBoard)
                && ReferenceEquals(window.Observations[2], privateChange) && ReferenceEquals(window.Observations[3], eventChange)
                && ReferenceEquals(window.Observations[4], correlationChange) && ReferenceEquals(window.Observations[5], changed)
                && window.AcceptedAtTicks[0] == 0 && window.AcceptedAtTicks.Zip(window.AcceptedAtTicks.Skip(1)).All(pair => pair.First <= pair.Second)
                && window.EndTicks >= window.AcceptedAtTicks[^1] && ReferenceEquals(capture.Complete(), window)
                && capture.AcceptedCallbacks == 8 && window.Capture is { ReferenceIdenticalCallbacks: 1, UnretainedCallbacks: 0, Failure: null },
            "Deferred comparison preserves every distinct revision and same-identity private/activity/execution change, first acceptance times and immutable cleanup cutoff");
        var extended = new FanoutAcceptedProjection();
        for (int index = 0; index < 513; index++) {
            extended.Observe(first with { Revision = 10 + index });
            if (!SpinWait.SpinUntil(() => extended.Count == index + 1, 1000)) throw new InvalidDataException("The control consumer did not retire its admitted reference.");
        }
        var extendedWindow = extended.Complete();
        check(extendedWindow.Observations.Length == 513 && extendedWindow.Capture!.UnretainedCallbacks == 0
                && extendedWindow.Observations.Select(state => state.Revision).SequenceEqual(Enumerable.Range(10, 513).Select(value => (long)value)),
            "The observed 513th distinct accepted publication is retained as the next bounded recording page instead of throwing into an already committed receipt");
        var bounded = new FanoutAcceptedProjection(maximumCheckpoints: 2);
        bounded.Observe(first); bounded.Observe(repeatedBoard); bounded.Observe(changed);
        var partial = bounded.Complete(requireComplete: false);
        check(Rejected(bounded.ThrowIfFaulted) && partial.Observations.Length == 2 && partial.Capture!.UnretainedCallbacks == 1
                && partial.Observations[0] == first && partial.Observations[1] == repeatedBoard,
            "Whole-stream quota exhaustion preserves admitted frames and fault evidence, then fails the owner guard rather than throwing from a receipt callback");
        using var blocked = new ManualResetEventSlim();
        var queue = new FanoutAcceptedProjection(pendingCapacity: 2, beforeDrain: () => blocked.Wait());
        queue.Observe(first); queue.Observe(repeatedBoard); queue.Observe(changed); queue.Freeze(); blocked.Set();
        var queuePartial = queue.Complete(requireComplete: false);
        check(Rejected(queue.ThrowIfFaulted) && queuePartial.Capture is { PendingReferenceHighWater: 2, UnretainedCallbacks: 1 }
                && queuePartial.AcceptedCallbacks == 3 && queuePartial.Observations.Length == 2,
            "A full pending reference journal is a visible incomplete-evidence failure without waiting, eviction or callback exception");
        var broken = new FanoutAcceptedProjection(serialize: _ => throw new IOException("controlled serializer failure"));
        broken.Observe(first); broken.Observe(first with { });
        var brokenWindow = broken.Complete(requireComplete: false);
        check(Rejected(broken.ThrowIfFaulted) && brokenWindow.Observations.Length == 1 && brokenWindow.Capture!.UnretainedCallbacks == 1,
            "A deferred serializer failure remains separate from the production commit and retains the exact partial frame/fault diagnostics");
    }
    private static bool Rejected(Action action) { try { action(); return false; } catch (InvalidDataException) { return true; } }
}
