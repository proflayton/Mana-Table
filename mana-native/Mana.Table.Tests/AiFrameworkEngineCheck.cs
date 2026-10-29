using System.Diagnostics;
using Mana.AI;
using Mana.Client;
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
        GameSnapshot? expiredSourceObservation = null;
        bool kept = false, landPlayed = false;
        int expiredSourceReads = 0;
        var fixturePasses = new HashSet<string>();
        try {
            while (!landPlayed) {
                Guard();
                state = await engine.ObserveAsync();
                if (expiredSourceObservation is { } expired) {
                    if (state == null || state.Id != expired.Id || state.Revision <= expired.Revision)
                        throw new InvalidOperationException("An expired source read did not produce a newer observation of the same game.");
                    check(true, "A rejected conformance source read moved from revision " + expired.Revision
                        + " to " + state.Revision + " before any offer was reused");
                    expiredSourceObservation = null;
                }
                if (state?.Status is "error" or "finished") throw new InvalidOperationException(state.Error ?? "Framework fixture ended unexpectedly.");
                if (state?.Decision is not { } decision) { session?.Observe(null); await Task.Delay(5); continue; }
                session ??= new(state.Id, state.ViewerId, new ScriptedPolicy(), 261026, new(TimeSpan.FromSeconds(10)));
                if (kept && state.Viewer!.Zone("Battlefield").Cards.Any()) { landPlayed = true; break; }
                var current = new AiDecisionFrame(state);
                if (decision.Payload is PriorityPayload) {
                    if (state.ActivePlayerId == state.ViewerId && state.PhaseKey == "MAIN1" && state.Stack.IsEmpty) {
                        var land = state.Viewer!.Zone("Hand").Cards.First();
                        SourceActionSet offers;
                        try { offers = await engine.SourceActionsAsync(state.Id, decision.Id, land.Key); }
                        catch (EngineCommandRejectedException error) when (error.Message is
                            "The table changed. Read the current card again."
                            or "The source actions changed. Read the current card again."
                            or "The source action context changed. Select the current card again.") {
                            // A queued display publication can refresh the same
                            // unanswered priority between observation and query.
                            // Discard the read and observe again within the same
                            // deadline; no accepted command is replayed.
                            if (++expiredSourceReads > 16) throw new InvalidOperationException("Too many expired conformance source reads.", error);
                            expiredSourceObservation = state;
                            session.Observe(null);
                            await Task.Delay(5); continue;
                        }
                        if (offers.Revision != state.Revision) {
                            if (++expiredSourceReads > 16) throw new InvalidOperationException("Too many expired conformance source reads.");
                            expiredSourceObservation = state;
                            session.Observe(null);
                            await Task.Delay(5); continue;
                        }
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
                    && record.Command == nameof(UseOfferedAction)) && state!.Viewer!.Zone("Battlefield").Count == 1,
                "The policy framework keeps a real hand and follows a current offered land route; the subsequent authoritative board establishes the result");
            if (expiredSourceReads > 0) check(true,
                "The conformance driver discarded " + expiredSourceReads + " expired source reads before accepting a freshly scoped route, within its original deadline");
        } finally {
            session?.Dispose();
        }
        // A failed fixture is torn down by its engine owner. A secondary
        // concession failure must not replace the original contract failure.
        if (state != null) {
            await engine.ConcedeAsync(state.Id);
            Guard();
            var finished = await MatchCompletion.WaitAsync(engine, state, TimeSpan.FromSeconds(25) - elapsed.Elapsed);
            check(finished is { Status: "finished", Result: "Defeat", Viewer.Eliminated: true },
                "Conformance concession finishes the same solo game before the adapter starts its next game");
        }
        void Guard()
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(25)) throw new TimeoutException("AI framework engine conformance did not reach its offered land route and finish within its bounded watchdog.");
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
