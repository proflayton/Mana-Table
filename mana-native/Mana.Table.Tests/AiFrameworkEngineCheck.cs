using System.Diagnostics;
using Mana.AI;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

// A scripted conformance policy, not a gameplay bot or a second rules engine.
// Reuses the existing JVM and its real scoped command/source-action boundary.
internal static class AiFrameworkEngineCheck
{
    internal static async Task RunAsync(ForgeEngine engine, string deckId, Action<bool, string> check)
    {
        await engine.StartSoloAsync(deckId, ["green", "red", "green"]);
        var elapsed = Stopwatch.StartNew();
        AiSeatSession? session = null;
        GameSnapshot? state = null;
        bool kept = false, landPlayed = false;
        var fixturePasses = new HashSet<string>();
        try {
            while (!landPlayed) {
                if (elapsed.Elapsed > TimeSpan.FromSeconds(25)) throw new TimeoutException("AI framework engine conformance did not reach its offered land route.");
                state = await engine.ObserveAsync();
                if (state?.Status is "error" or "finished") throw new InvalidOperationException(state.Error ?? "Framework fixture ended unexpectedly.");
                if (state?.Decision is not { } decision) { session?.Observe(null); await Task.Delay(5); continue; }
                session ??= new(state.Id, state.ViewerId, new ScriptedPolicy(), 261026, new(TimeSpan.FromSeconds(10)));
                if (kept && state.Viewer!.Zone("Battlefield").Cards.Any()) { landPlayed = true; break; }
                var current = new AiDecisionFrame(state);
                if (decision.Payload is PriorityPayload) {
                    if (state.ActivePlayerId == state.ViewerId && state.PhaseKey == "MAIN1" && state.Stack.IsEmpty) {
                        var land = state.Viewer!.Zone("Hand").Cards.First();
                        var offers = await engine.SourceActionsAsync(state.Id, decision.Id, land.Key);
                        check(offers.Actions.Any(action => action.Kind == "play"), "Real Forge offers the fixture's land route independently of AI strategy");
                        current = new(state, offerVersion: 1, sourceActions: [offers]);
                    } else {
                        // Scenario setup is an explicit human test-driver pass,
                        // not AI authority inferred from an incomplete action list.
                        session.Observe(null);
                        if (fixturePasses.Add(decision.Id)) await engine.ExecuteAsync(new PassPriority(state.Id, decision.Id));
                        await Task.Delay(5); continue;
                    }
                }
                session.Observe(current);
                var result = await session.EvaluateAsync();
                if (result.Status == AiSeatStatus.WaitingForObservation) { await Task.Delay(5); continue; }
                if (result.Status != AiSeatStatus.Ready) throw new InvalidOperationException("Framework conformance stopped: " + result);
                var command = session.TakeCommand() ?? throw new InvalidOperationException("Missing one-shot framework handoff.");
                await engine.ExecuteAsync(command.Command);
                check(session.Report(command, AiExecutionReceipt.Accepted) && session.TakeCommand() == null,
                    "The real adapter accepts one " + command.Command.GetType().Name + " from its exact AI input scope without replay");
                if (command.Command is KeepOpeningHand) kept = true;
                await Task.Delay(5);
            }
            check(kept && landPlayed && session!.History.Decisions.Any(record => record.Outcome == AiDecisionOutcome.Accepted
                    && record.Answer is UseOfferedAction) && state!.Viewer!.Zone("Battlefield").Count == 1,
                "The policy framework keeps a real hand and follows a current offered land route; the subsequent authoritative board establishes the result");
        } finally {
            session?.Dispose();
            if (state != null) await engine.ConcedeAsync(state.Id);
        }
    }

    private sealed class ScriptedPolicy : IAiPolicy
    {
        public AiPolicyDescriptor Descriptor { get; } = new("real-engine-conformance.v1", AiTasks.All);
        public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation)
        {
            var state = request.Frame.State;
            DecisionCommand? answer = state.Decision!.Payload switch {
                OpeningHandPayload { Keep.Enabled: true } => new KeepOpeningHand(state.Id, state.Decision.Id),
                EntitySelectionPayload entity when entity.Players.Length > 0 => new SelectPlayer(state.Id, state.Decision.Id,
                    entity.Players.Contains(state.ViewerId) ? state.ViewerId : entity.Players[0]),
                RevealPayload => new AcknowledgeReveal(state.Id, state.Decision.Id),
                _ => null
            };
            if (answer == null && request.Frame.SourceActions.FirstOrDefault() is { } source
                && source.Actions.FirstOrDefault(action => action.Kind == "play") is { } offered)
                answer = new UseOfferedAction(state.Id, state.Decision.Id, source.SourceCardKey, offered.Id);
            return Task.FromResult<AiProposal>(answer == null ? new AiProposal.Unavailable("scripted-fixture", "This is not a production policy.")
                : new AiProposal.Answer(answer, "scripted-conformance"));
        }
    }
}
