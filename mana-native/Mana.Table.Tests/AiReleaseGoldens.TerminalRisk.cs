using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Mana.AI;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

internal static partial class AiReleaseGoldens
{
    private sealed record TerminalAcceptedFrame(AutomatedSeatObservation Observation, UseOfferedAction Command);
    private sealed record TerminalAcceptedPass(AutomatedSeatObservation Observation, PassPriority Command);
    private sealed record TerminalRouteFunding(string Name, string SourceId, string ActionId,
        ActionAffordability PublishedAffordability, SemanticKnowledge PublishedManaCostKnowledge,
        bool RequiresScheduledLoss, bool ScheduledActorLoss, AiManaPlan NominalFunding);

    // Capture the actual current input used by the ordinary policy, then retain
    // it only after the ordinary provider accepts the command. No fixture reply
    // or remembered action handle is submitted by this evidence wrapper.
    private sealed class TerminalReceiptProbe(ReceiptProbe inner) : IAutomatedSeatProvider
    {
        internal AutomatedSeatObservation? FirstOwnMain { get; private set; }
        internal TerminalAcceptedFrame? SafeCast { get; private set; }
        internal TerminalAcceptedPass? PostSafePass { get; private set; }
        internal List<TerminalAcceptedFrame> UnsafeCasts { get; } = [];
        public Task EnableAutomatedSeatsAsync(CancellationToken cancellation = default) => inner.EnableAutomatedSeatsAsync(cancellation);
        public Task ReportAutomationFaultAsync(string game, int seat, string reason, CancellationToken cancellation = default) => inner.ReportAutomationFaultAsync(game, seat, reason, cancellation);
        public Task<ImmutableArray<AutomatedSeatObservation>> ObserveAutomatedSeatsAsync(CancellationToken cancellation = default) => inner.ObserveAutomatedSeatsAsync(cancellation);
        public async Task ExecuteAutomatedAsync(int seat, GameCommand command, CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            var observation = inner.Last.Single(item => item.SeatId == seat);
            if (seat == 0 && observation.State.ActivePlayerId == seat
                && observation.State.PhaseKey == "MAIN1" && observation.State.Decision?.Payload is PriorityPayload)
                FirstOwnMain ??= observation;
            var source = command is UseOfferedAction action
                ? observation.Domain.Sources.Single(set => set.SourceCardKey == action.Card) : null;
            var route = command is UseOfferedAction offered ? source!.Actions.Single(item => item.Id == offered.ActionId) : null;
            var face = source == null ? null : CardViews.FindActionSource(observation.State, source.SourceCardKey)?.Card;
            await inner.ExecuteAutomatedAsync(seat, command, cancellation);
            if (seat == 0 && command is PassPriority pass && SafeCast is { } safe
                && observation.State.ActivePlayerId == 0 && observation.State.PhaseKey == "MAIN1"
                && observation.State.Decision?.Payload is PriorityPayload && observation.State.Stack.IsEmpty) {
                string safeId = safe.Observation.Domain.Sources.Single(set => set.SourceCardKey == safe.Command.Card).SourceCardId;
                if (observation.State.Viewer!.Zone("Battlefield").Cards.Any(card => CardViews.Identity(card) == safeId))
                    PostSafePass ??= new(observation, pass);
            }
            if (seat != 0 || route?.Kind != "cast" || command is not UseOfferedAction cast) return;
            var accepted = new TerminalAcceptedFrame(observation, cast);
            if (face?.Name is "Final Fortune" or "Glorious End") UnsafeCasts.Add(accepted);
            if (face?.Name == "Sol Ring") SafeCast ??= accepted;
        }
    }

    private static async Task<AiGoldenResult> RunTerminalRiskGoldenAsync(ForgeEngine engine, string directory,
        ImmutableArray<string> decks)
    {
        Directory.CreateDirectory(directory);
        using var diagnostics = new AiCampaignDiagnostics(directory);
        var observed = new AiCampaignProvider(engine, diagnostics);
        var receipts = new ReceiptProbe(observed);
        var captured = new TerminalReceiptProbe(receipts);
        await using var host = new AiTableHost(captured, _ => diagnostics.Record(new CasualCommanderPolicy()), 261026,
            new(TimeSpan.FromSeconds(10), 4096));
        await using var trace = new StreamWriter(Path.Combine(directory, "public.jsonl"));
        var clock = Stopwatch.StartNew();
        GameSnapshot? state = null;
        string safeId = "", castExecution = "", stop = "not-started", detail = "";
        int steps = 0, comparisons = 0;
        bool passed = false, stackSeen = false, resolved = false, currentOffers = false, openingVerified = false;
        ImmutableArray<TerminalRouteFunding> nominalFunding = [];
        ImmutableArray<TerminalRouteFunding> postSafeNominalFunding = [];
        bool postSafePassVerified = false;
        Exception? failure = null;
        var captureErrors = new List<string>();
        try {
            state = await engine.StartSimulationAsync(decks);
            while (!state.Players.Any(player => player.Id == 0
                && player.Zone("Command").Cards.Any(card => card.Name == "Niv-Mizzet, Parun"))) {
                if (clock.Elapsed >= TimeSpan.FromSeconds(10) || state.Status is "error" or "finished")
                    throw new InvalidDataException("Terminal-risk fixture did not publish its declared commander before policy execution.");
                await Task.Delay(2);
                state = await engine.ObserveSimulationAsync() ?? throw new InvalidDataException("Terminal-risk setup disappeared.");
            }
            var actor = state.Players.Single(player => player.Id == 0);
            if (actor.Zone("Battlefield").Cards.Length != 4
                || actor.Zone("Battlefield").Cards.Any(card => card.Name != "Mountain"))
                throw new InvalidDataException("Terminal-risk fixture did not publish its declared four pregame Mountains.");
            while (true) {
                if (clock.Elapsed >= TimeSpan.FromSeconds(30)) { stop = "inconclusive-time-cutoff"; detail = "The first-main safe consequence and subsequent risk-declining pass were not observed within the existing golden budget."; break; }
                await host.StepAsync(); steps++;
                state = await engine.ObserveSimulationAsync() ?? throw new InvalidDataException("Terminal-risk public observation disappeared.");
                if (host.LastFault != null) throw new InvalidOperationException(JsonSerializer.Serialize(host.LastFault));
                foreach (var bot in receipts.Last) {
                    if (!AiCampaignVisibility.PrivateWindowsAreDisclosed(bot.State, bot.Semantics, bot.Domain))
                        throw new InvalidDataException("Terminal-risk observation disclosed an unauthorized private window.");
                    if (state.BoardRevision == bot.State.BoardRevision) {
                        if (!AiWholeSeatEngineCheck.ComparePublicProjection(state, bot.State, directory))
                            throw new InvalidDataException("Same-revision terminal-risk projections diverged.");
                        comparisons++;
                    }
                }
                diagnostics.ObservePublic(state);
                await trace.WriteLineAsync(JsonSerializer.Serialize(new { steps, state }));
                if (captured.UnsafeCasts.Count != 0)
                    throw new InvalidDataException("The policy initiated a real offered terminal-risk spell instead of preserving its safe alternative.");
                if (captured.FirstOwnMain is { } first && !openingVerified) {
                    var hand = first.State.Viewer!.Zone("Hand");
                    openingVerified = hand.Count == 8 && first.State.Viewer.Zone("Library").Count == 0
                        && new[] { "Final Fortune", "Glorious End", "Sol Ring" }.All(name => hand.Cards.Count(card => card.Name == name) == 1);
                    if (!openingVerified)
                        throw new InvalidDataException("The first natural draw did not expose every declared opening witness before the policy's first own-main action.");
                }
                if (captured.SafeCast is { } safe) {
                    var frame = safe.Observation;
                    var set = frame.Domain.Sources.Single(item => item.SourceCardKey == safe.Command.Card);
                    safeId = set.SourceCardId;
                    if (nominalFunding.IsEmpty) nominalFunding = new[] {
                        TerminalOffer(frame, "Final Fortune", true), TerminalOffer(frame, "Glorious End", true),
                        TerminalOffer(frame, "Sol Ring", false)
                    }.OfType<TerminalRouteFunding>().ToImmutableArray();
                    currentOffers = frame.State.ActivePlayerId == 0 && frame.State.PhaseKey == "MAIN1"
                        && frame.State.Decision?.Payload is PriorityPayload
                        && nominalFunding.Length == 3 && nominalFunding.All(route =>
                            route.PublishedAffordability != ActionAffordability.Unavailable
                            && route.NominalFunding.Status == AiManaPlanStatus.Funded
                            && (!route.RequiresScheduledLoss || route.ScheduledActorLoss))
                        && set.Actions.Any(action => action.Id == safe.Command.ActionId && action.Kind == "cast");
                    if (!currentOffers)
                        throw new InvalidDataException("The accepted safe action did not share its exact current input with two decoded terminal-risk routes and three nominally funded cast offers.");
                    stackSeen |= state.Stack.Any(item => item.Kind == StackActionKind.Spell && item.ControllerId == 0
                        && item.Card is { } card && CardViews.Identity(card) == safeId);
                    foreach (var entry in receipts.Last.Where(item => item.SeatId == 0).SelectMany(item => item.State.Activity).Concat(state.Activity)) {
                        if (entry.Kind == "cast" && entry.PlayerId == 0 && entry.CardId == safeId && entry.ExecutionId.Length > 0)
                            castExecution = entry.ExecutionId;
                        if (castExecution.Length > 0 && entry.Kind == "resolved" && entry.Outcome == "resolved"
                            && entry.ExecutionId == castExecution) resolved = true;
                    }
                    bool onBattlefield = state.Players.Single(player => player.Id == 0).Zone("Battlefield").Cards
                        .Any(card => CardViews.Identity(card) == safeId && card.Name == "Sol Ring");
                    if (captured.PostSafePass is { } pass) {
                        if (postSafeNominalFunding.IsEmpty) postSafeNominalFunding = new[] {
                            TerminalOffer(pass.Observation, "Final Fortune", true),
                            TerminalOffer(pass.Observation, "Glorious End", true)
                        }.OfType<TerminalRouteFunding>().ToImmutableArray();
                        postSafePassVerified = pass.Observation.State.Stack.IsEmpty
                            && pass.Observation.State.ActivePlayerId == 0 && pass.Observation.State.PhaseKey == "MAIN1"
                            && pass.Observation.State.Turn == frame.State.Turn
                            && postSafeNominalFunding.Length == 2 && postSafeNominalFunding.All(route =>
                                route.PublishedAffordability != ActionAffordability.Unavailable
                                && route.NominalFunding.Status == AiManaPlanStatus.Funded && route.ScheduledActorLoss
                                && nominalFunding.Any(initial => initial.Name == route.Name && initial.SourceId == route.SourceId));
                        if (!postSafePassVerified)
                            throw new InvalidDataException("The accepted empty-stack post-resolution pass did not retain both exact terminal-risk occurrences as nominally funded current cast offers.");
                    }
                    if (openingVerified && currentOffers && stackSeen && resolved && onBattlefield && postSafePassVerified && comparisons > 0) {
                        passed = true; stop = "observed-safe-consequence-and-risk-decline";
                        detail = "The policy cast and resolved the same Sol Ring occurrence, then accepted an empty-stack first-main pass while both exact terminal-risk spells remained nominally funded current offers; neither self-defeat route was initiated.";
                        break;
                    }
                }
                if (state.Status is "error" or "finished" || captured.FirstOwnMain is { } own
                    && (state.Turn != own.State.Turn || state.ActivePlayerId != 0 || state.PhaseKey is "END_OF_TURN" or "CLEANUP")) {
                    stop = "first-own-turn-ended"; detail = "The bounded safe consequence was not proven before leaving the first own turn."; break;
                }
                await Task.Delay(1);
            }
        } catch (Exception error) { failure = error; stop = "mechanical-failure"; detail = error.ToString(); }
        finally {
            try { File.WriteAllText(Path.Combine(directory, "pre-cleanup-public-state.json"), JsonSerializer.Serialize(state)); }
            catch (Exception error) { captureErrors.Add(error.ToString()); passed = false; }
            if (state != null) try { await engine.StopSimulationAsync(state.Id); }
                catch (Exception error) { captureErrors.Add(error.ToString()); passed = false; }
        }
        var summary = diagnostics.Summary();
        if (!summary.DiagnosticErrors.IsEmpty) { captureErrors.AddRange(summary.DiagnosticErrors); passed = false; }
        var evidence = JsonSerializer.SerializeToElement(new { audience = "host-private targeted fixture", openingVerified,
            currentOffers, safeId, castExecution, stackSeen, resolved, firstOwnMain = captured.FirstOwnMain,
            nominalFunding, postSafePassVerified, postSafeNominalFunding, actualAcceptedPostSafePass = captured.PostSafePass,
            actualAcceptedSafeInput = captured.SafeCast, unsafeAccepted = captured.UnsafeCasts, commands = receipts.Accepted,
            diagnostics = summary, failure = failure?.ToString(), captureErrors,
            campaignDeckQualificationCredit = false, naturalGameCompletionCredit = false, humanReview = "pending",
            scope = "Declared eight-card pregame override; actual current JVM facts, decoder, policy and accepted safe consequence. Per-route published affordability remains explicit; separate read-only nominal funding assessments do not establish an adjusted future bill, submit commands or consume the policy's own work allowance. No future-win or protection exception is certified." });
        var result = new AiGoldenResult("terminal-risk-current-routes", passed && failure == null && captureErrors.Count == 0,
            detail, comparisons, receipts.Accepted.Count, steps, clock.Elapsed.TotalMilliseconds, stop, evidence);
        File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return result;
    }

    private static TerminalRouteFunding? TerminalOffer(AutomatedSeatObservation frame, string name, bool requiresLoss)
    {
        var card = frame.State.Viewer!.Zone("Hand").Cards.SingleOrDefault(card => card.Name == name);
        if (card == null) return null;
        var set = frame.Domain.Sources.SingleOrDefault(source => source.SourceCardId == CardViews.Identity(card));
        var route = set?.Actions.SingleOrDefault(action => action.Kind == "cast");
        if (set == null || route == null) return null;
        var action = frame.Domain.Actions.Single(item => item.ActionId == route.Id);
        // The provider deliberately leaves mana-bearing casts Unknown. Use the
        // existing offline planner with its own bounded work to prove only a
        // nominal resource assignment on this exact already-recorded input.
        var funding = AiManaPlanner.EvaluateAction(new AiDecisionFrame(frame.State, domain: frame.Domain, semantics: frame.Semantics),
            action, workLimit: 2048);
        return new(name, set.SourceCardId, route.Id, action.Affordability, action.ManaCostKnowledge,
            requiresLoss, HasScheduledActorLoss(action.OperationGraph, frame.SeatId), funding);
    }

    private static bool HasScheduledActorLoss(ActionOperationGraph graph, int actor)
    {
        var nodes = graph.Operations.ToDictionary(node => node.Id);
        var pending = new Stack<int>(graph.Operations.SelectMany(node => node.Links)
            .Where(link => link.Kind == OperationLinkKind.Scheduled).Select(link => link.OperationId));
        var visited = new HashSet<int>();
        while (pending.TryPop(out int id)) {
            if (!visited.Add(id) || !nodes.TryGetValue(id, out var node)) continue;
            if (node.Effect == ActionEffectKind.LoseGame && (node.Recipients.Knowledge != SemanticKnowledge.Known
                || node.Recipients.Binding is not (EffectRecipientBinding.Actor or EffectRecipientBinding.Explicit)
                || node.Recipients.Entities.Any(entity => entity.Kind == SemanticEntityKind.Player && entity.PlayerId == actor))) return true;
            foreach (var link in node.Links) pending.Push(link.OperationId);
        }
        return false;
    }
}
