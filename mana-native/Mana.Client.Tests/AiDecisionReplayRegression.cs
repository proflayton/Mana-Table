using System.Collections.Immutable;
using System.Text.Json;
using Mana.AI;
using Mana.Contracts;

internal static class AiDecisionReplayRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var recording = new RecordingPolicy(new CasualCommanderPolicy());
        var frame = Frame();
        using var original = new AiSeatSession(frame.State.Id, 11, recording, 913, new(TimeSpan.FromSeconds(5), 128));
        original.Observe(frame); await original.EvaluateAsync();
        var first = original.TakeCommand()!; original.Report(first, AiExecutionReceipt.Accepted);
        // Capture a later episode. Replay must preserve its evaluation seed,
        // rather than treating the original table seed as the decision seed.
        frame = new(frame.State with { Revision = 2, BoardRevision = 2,
            Decision = frame.State.Decision! with { Id = "second-episode" } }, semantics: new() { EpisodeId = "next" });
        original.Observe(frame); await original.EvaluateAsync();
        var expected = original.TakeCommand()!;
        var captured = recording.Recorded!;
        string encoded = JsonSerializer.Serialize(captured);
        var restored = JsonSerializer.Deserialize<AiDecisionReplayCase>(encoded)!;
        var replay = await AiDecisionReplay.EvaluateAsync(restored, new CasualCommanderPolicy());
        var repeated = await AiDecisionReplay.EvaluateAsync(restored, new CasualCommanderPolicy());
        check(replay.Status == AiSeatStatus.Ready && Equivalent(expected.Command, replay.Proposal)
                && Equivalent(replay.Proposal, repeated.Proposal),
            "A private decision JSON roundtrip reproduces the proposal for the exact later-episode seed without running the rules engine");
        check(replay.Evidence?.Seed == captured.Seed && replay.Evidence?.Stamp == frame.Stamp,
            "Replay evidence retains the original seat, game, observation and evaluation seed");
        check(replay.Evidence?.Outcome == AiDecisionOutcome.Offered,
            "Offline replay does not invent an accepted receipt or a resolved game effect");
        int firstChoice = ((ChooseOptions)first.Command).Choices.Single();
        var commandHistory = new CommandHistoryPolicy(firstChoice);
        var liveEvidence = await AiDecisionReplay.EvaluateAsync(captured, commandHistory);
        var restoredEvidence = await AiDecisionReplay.EvaluateAsync(restored, commandHistory);
        check(liveEvidence.Proposal is ChooseOptions liveChoice && restoredEvidence.Proposal is ChooseOptions restoredChoice
                && liveChoice.Choices.SequenceEqual([firstChoice == 7 ? 13 : 7]) && restoredChoice.Choices.SequenceEqual(liveChoice.Choices),
            "A comparison policy receives the same retained command kind, concrete past choice and scope before and after private JSON replay");
        AiDecisionRecord detachedEvidence;
        using (var document = JsonDocument.Parse(JsonSerializer.Serialize(first.Command, first.Command.GetType())))
            detachedEvidence = new(frame.Stamp, AiTask.Options, "evidence-probe", AiDecisionOutcome.Accepted,
                nameof(ChooseOptions), document.RootElement, "recorded-choice", 0, 0, TimeSpan.Zero);
        check(detachedEvidence.AnswerEvidence?.GetProperty("Choices")[0].GetInt32() == firstChoice
                && typeof(AiDecisionRecord).GetProperties().All(property => !typeof(GameCommand).IsAssignableFrom(property.PropertyType)),
            "Retained command evidence owns its JSON lifetime and exposes no executable historical command object");

        var fact = new AiObservedEvent(1, 3, "impact", 29, "source", null,
            new("damage", 2, 11, null, "source", 40, 38, true), 29, "public-cause");
        var historyCase = restored with { History = restored.History with { Events = [fact] } };
        var historyPolicy = new HistoryPolicy(captured.Seed);
        var informed = await AiDecisionReplay.EvaluateAsync(historyCase, historyPolicy);
        check(informed.Status == AiSeatStatus.Ready && informed.Proposal is ChooseOptions answer && answer.Choices.SequenceEqual([13]),
            "A comparison policy receives the captured private history and exact seed, rather than an empty fresh-game memory");
        var alternate = await AiDecisionReplay.EvaluateAsync(restored, historyPolicy);
        check(alternate.Status == AiSeatStatus.Unavailable && alternate.Proposal == null,
            "Policy comparison reports explicit missing knowledge instead of manufacturing a fallback answer");
        var wrongSeatRecord = new AiDecisionRecord(frame.Stamp with { SeatId = 29 }, AiTask.Options, "old-policy",
            AiDecisionOutcome.Accepted, null, "old-reason", 0, 0, TimeSpan.Zero);
        check(await Reject(restored with { History = restored.History with { Decisions = [wrongSeatRecord] } }),
            "Replay rejects another seat's retained decision evidence");
        check(await Reject(restored with { History = restored.History with { Decisions = [wrongSeatRecord with {
                Stamp = frame.Stamp with { Revision = frame.State.Revision + 1 } }] } }),
            "Replay rejects future decision knowledge even when it belongs to the same game and seat");
        var routeLimited = await AiDecisionReplay.EvaluateAsync(restored with { History = restored.History with { RouteLimitReached = true } },
            new CasualCommanderPolicy());
        check(routeLimited.Status == AiSeatStatus.BudgetExceeded && routeLimited.Proposal == null,
            "Captured route exhaustion cannot bypass the production session's planning guard during replay");
        check(await Reject(historyCase with { History = historyCase.History with { Events = [fact with { Turn = 9 }] } }),
            "Replay rejects an event from after the captured game state");
        check(await Reject(restored with { FormatVersion = 912 }),
            "Unknown replay formats are rejected explicitly");
        var forged = await AiDecisionReplay.EvaluateAsync(restored, new ForgingPolicy());
        check(forged.Status == AiSeatStatus.Invalid && forged.Proposal == null,
            "Replayed policies still cannot mint another decision's action scope");
        var exhausted = await AiDecisionReplay.EvaluateAsync(restored, new SpendingPolicy(), new(TimeSpan.FromSeconds(2), 1));
        check(exhausted.Status == AiSeatStatus.Unavailable && exhausted.Proposal == null,
            "Offline experiments use bounded work and preserve an explicit exhausted-policy result");

        async Task<bool> Reject(AiDecisionReplayCase invalid)
        {
            try { await AiDecisionReplay.EvaluateAsync(invalid, new CasualCommanderPolicy()); return false; }
            catch (ArgumentException) { return true; }
        }
    }

    private static bool Equivalent(DecisionCommand? first, DecisionCommand? second)
        => first?.GetType() == second?.GetType() && (first == null || JsonSerializer.Serialize(first, first.GetType())
            == JsonSerializer.Serialize(second, second!.GetType()));
    private sealed class RecordingPolicy(IAiPolicy inner) : IAiPolicy
    {
        public AiPolicyDescriptor Descriptor => inner.Descriptor;
        internal AiDecisionReplayCase? Recorded;
        public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation)
        {
            Recorded = AiDecisionReplayCase.Capture("captured-option-choice", Descriptor.Id, request);
            return inner.EvaluateAsync(request, cancellation);
        }
    }
    private sealed class HistoryPolicy(long seed) : IAiPolicy
    {
        public AiPolicyDescriptor Descriptor { get; } = new("history-probe.v1", AiTasks.All);
        public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation)
            => Task.FromResult<AiProposal>(request.Seed == seed && request.History.Events.Any(entry => entry.Impact?.Amount == 2)
                ? new AiProposal.Answer(new ChooseOptions(request.Frame.State.Id, request.Frame.Stamp.DecisionId, [13]), "recorded-history")
                : new AiProposal.Unavailable("recorded-history", "This case lacks the required captured fact."));
    }
    private sealed class CommandHistoryPolicy(int expectedChoice) : IAiPolicy
    {
        public AiPolicyDescriptor Descriptor { get; } = new("command-history-probe.v1", AiTasks.All);
        public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation)
        {
            bool retained = request.History.Decisions.Any(entry => entry.Outcome == AiDecisionOutcome.Accepted
                && entry.Command == nameof(ChooseOptions) && entry.AnswerEvidence is { ValueKind: JsonValueKind.Object } evidence
                && evidence.GetProperty("GameId").GetString() == request.Frame.State.Id
                && evidence.GetProperty("DecisionId").GetString() == "first-input"
                && evidence.GetProperty("Choices").EnumerateArray().Select(value => value.GetInt32()).SequenceEqual([expectedChoice]));
            return Task.FromResult<AiProposal>(retained
                ? new AiProposal.Answer(new ChooseOptions(request.Frame.State.Id, request.Frame.Stamp.DecisionId,
                    [expectedChoice == 7 ? 13 : 7]), "retained-command-evidence")
                : new AiProposal.Unavailable("retained-command-evidence", "This comparison requires its recorded previous choice."));
        }
    }
    private sealed class ForgingPolicy : IAiPolicy
    {
        public AiPolicyDescriptor Descriptor { get; } = new("forging-probe.v1", AiTasks.All);
        public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation)
            => Task.FromResult<AiProposal>(new AiProposal.Answer(new ChooseOptions(request.Frame.State.Id, "revoked-input", [13]), "forged-scope"));
    }
    private sealed class SpendingPolicy : IAiPolicy
    {
        public AiPolicyDescriptor Descriptor { get; } = new("work-probe.v1", AiTasks.All);
        public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation)
        {
            request.Work.TrySpend();
            return Task.FromResult<AiProposal>(!request.Work.TrySpend()
                ? new AiProposal.Unavailable("work-budget", "No further work is authorized.")
                : new AiProposal.Answer(new ChooseOptions(request.Frame.State.Id, request.Frame.Stamp.DecisionId, [13]), "work-probe"));
        }
    }
    private static AiDecisionFrame Frame()
    {
        var state = new GameSnapshot("replay-game", "playing", null, null, 11, 4, "Main", "MAIN1", 11,
            [new(11, "Actor", 40, false, false, ImmutableDictionary<string, int>.Empty, []),
             new(29, "Opponent", 40, false, false, ImmutableDictionary<string, int>.Empty, [])], [], null,
            new("first-input", new(), new OptionsPayload(new([new(7, "One", null), new(13, "Other", null)], new(1, 1))))) {
                Revision = 1, BoardRevision = 1, DecisionOwnerId = 11 };
        return new(state);
    }
}
