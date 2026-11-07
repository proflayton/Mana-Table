namespace Mana.Table;

internal static class FanoutProjectionCaptureChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var capture = new FanoutAcceptedProjection();
        bool emptyRejected = false;
        try { capture.Complete(); } catch (InvalidDataException) { emptyRejected = true; }
        var first = PresentationSmoke.Example() with { Revision = 10, BoardRevision = 10 };
        var repeatedBoard = first with { Revision = 11 };
        var changed = first with { Revision = 12, BoardRevision = 11 };
        capture.Observe(first); capture.Observe(first with { }); capture.Observe(repeatedBoard);
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
        var window = capture.Complete();
        check(emptyRejected && window.Observations.Length == 6 && window.AcceptedCallbacks == 7 && window.IdenticalCallbacks == 1
                && ReferenceEquals(window.Observations[0], first) && ReferenceEquals(window.Observations[1], repeatedBoard)
                && ReferenceEquals(window.Observations[2], privateChange) && ReferenceEquals(window.Observations[3], eventChange)
                && ReferenceEquals(window.Observations[4], correlationChange) && ReferenceEquals(window.Observations[5], changed) && window.AcceptedAtTicks[0] == 0
                && window.AcceptedAtTicks.Zip(window.AcceptedAtTicks.Skip(1)).All(pair => pair.First <= pair.Second)
                && window.EndTicks >= window.AcceptedAtTicks[^1],
            "Only a byte-equivalent adjacent callback shares its first checkpoint; fresh same-ID private faces, activity and execution payloads retain ordered actual offsets and final dwell");
        var bounded = new FanoutAcceptedProjection();
        for (int index = 0; index < Mana.Scenarios.ProjectionRecording.MaximumObservations; index++) bounded.Observe(first with { Revision = 10 + index });
        bool overflowRejected = false;
        try { bounded.Observe(changed with { Revision = 1000 }); } catch (InvalidDataException) { overflowRejected = true; }
        check(overflowRejected && bounded.Count == Mana.Scenarios.ProjectionRecording.MaximumObservations
                && bounded.Complete().Observations.Select(state => state.Revision).SequenceEqual(Enumerable.Range(10, Mana.Scenarios.ProjectionRecording.MaximumObservations).Select(value => (long)value)),
            "Fanout capture overflow fails instead of silently evicting an initial cast or replacing earlier accepted observations");
    }
}
