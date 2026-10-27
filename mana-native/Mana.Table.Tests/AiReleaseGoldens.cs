using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Mana.AI;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

internal sealed record AiGoldenResult(string Id, bool Passed, string Detail, int PublicComparisons,
    int AcceptedCommands, int Steps, double ElapsedMilliseconds, string StopReason, JsonElement Evidence);

// The test-only JVM entry point supplies declared pregame permanents. Every
// seat answer, cost, receipt and continuation uses the normal policy and ports.
internal static class AiReleaseGoldens
{
    private sealed record Scenario(string Id, string Commander, string Basic, string[] Extras);
    private static readonly Scenario[] Scenarios = [
        new("x-funded", "Omarthis, Ghostfire Initiate", "Wastes", ["Wastes", "Wastes", "Wastes", "Wastes", "Wastes"]),
        new("x-unfunded", "Omarthis, Ghostfire Initiate", "Wastes", []),
        new("consuming-funded", "Traxos, Scourge of Kroog", "Wastes", ["Crystal Vein", "Wastes"]),
        new("consuming-shortfall", "Traxos, Scourge of Kroog", "Wastes", ["Crystal Vein"]),
        new("optional-decline", "Rhys the Redeemed", "Forest", ["Mentor of the Meek"]),
        new("optional-pay", "Rhys the Redeemed", "Forest", ["Mentor of the Meek", "Forest"]),
        new("flexible-output", "Balmor, Battlemage Captain", "Island", ["Temple of Epiphany", "Island", "Island", "Island"])
    ];
    internal static int ExpectedCount => Scenarios.Length;

    internal static async Task<ImmutableArray<AiGoldenResult>> RunAsync(ForgeEngine engine, string profile, Action<bool, string> check)
    {
        string directory = Path.Combine(profile, "ai-goldens"); Directory.CreateDirectory(directory);
        await engine.EnableAutomatedSeatsAsync();
        string opponent = await Save("AI golden opponents", "Rhys the Redeemed", "Forest");
        var results = ImmutableArray.CreateBuilder<AiGoldenResult>();
        foreach (var scenario in Scenarios) {
            string deck = await Save("AI gate " + scenario.Id, scenario.Commander, scenario.Basic);
            var result = await Run(engine, Path.Combine(directory, scenario.Id), scenario, [deck, opponent, opponent, opponent]);
            results.Add(result);
            File.WriteAllText(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            if (!result.Passed) break; // Do not reuse a potentially faulted JVM.
        }
        var completed = results.ToImmutable();
        check(completed.Length == Scenarios.Length && completed.All(result => result.Passed),
            "Real policy goldens: " + string.Join("; ", completed.Select(result => result.Id + ": " + result.Detail)));
        return completed;

        async Task<string> Save(string name, string commander, string basic)
        {
            var imported = await engine.ImportAsync(name, "Deck\n99 " + basic + "\nCommander\n1 " + commander);
            var details = await engine.OpenDeckAsync(imported.Id);
            if (details.StructureValid == false || details.Problem.Length > 0 || !details.Issues.IsEmpty)
                throw new InvalidDataException("The golden Commander deck is invalid: " + name);
            var saved = await engine.SaveDeckAsync(details.Id, details.Revision);
            if (saved.SaveError.Length > 0) throw new InvalidDataException(saved.SaveError);
            return "deck:" + details.Id;
        }
    }

    private static async Task<AiGoldenResult> Run(ForgeEngine engine, string directory, Scenario scenario, ImmutableArray<string> decks)
    {
        Directory.CreateDirectory(directory);
        using var diagnostics = new AiCampaignDiagnostics(directory);
        var observed = new AiCampaignProvider(engine, diagnostics);
        var provider = new ReceiptProbe(observed);
        await using var host = new AiTableHost(provider, _ => diagnostics.Record(new CasualCommanderPolicy()), 261026,
            new(TimeSpan.FromSeconds(10), 4096));
        await using var trace = new StreamWriter(Path.Combine(directory, "public.jsonl"));
        var timer = Stopwatch.StartNew();
        GameSnapshot? state = null;
        string target = "", vein = "", mentor = "", flexible = "", lastFacts = "", stop = "not-started", detail = "";
        ImmutableArray<string> blueSources = [];
        int comparisons = 0, steps = 0, firstTurn = 0;
        bool setup = false, offered = false, stack = false, resolved = false, landPlayed = false, passedOffer = false, passed = false;
        bool publicCaptured = false, actualRedPool = false, actualRedBillPaid = false;
        Exception? primaryFailure = null;
        var captureErrors = new List<string>();
        try {
            state = await engine.StartSimulationAsync(decks);
            // Fail before submitting any policy answer if this is the ordinary
            // JVM or the requested fixture did not load.
            while (!state.Players.Any(player => player.Id == 0
                && player.Zone("Command").Cards.Any(card => card.Name == scenario.Commander))) {
                if (timer.Elapsed >= TimeSpan.FromSeconds(10) || state.Status is "error" or "finished")
                    throw new InvalidDataException("Golden setup never published the declared commander before policy execution.");
                await Task.Delay(2);
                state = await engine.ObserveSimulationAsync() ?? throw new InvalidDataException("Golden setup state disappeared.");
            }
            var actor = state.Players.Single(player => player.Id == 0);
            var commander = actor.Zone("Command").Cards.SingleOrDefault(card => card.Name == scenario.Commander)
                ?? throw new InvalidDataException("Golden setup did not contain the declared commander.");
            target = CardViews.Identity(commander);
            var extras = actor.Zone("Battlefield").Cards;
            if (state.Players.Length != 4 || target.Length == 0 || extras.Length != scenario.Extras.Length
                || !extras.Select(card => card.Name).Order(StringComparer.Ordinal).SequenceEqual(scenario.Extras.Order(StringComparer.Ordinal)))
                throw new InvalidDataException("Golden pregame battlefield does not match the declared test-only setup.");
            vein = extras.Where(card => card.Name == "Crystal Vein").Select(CardViews.Identity).SingleOrDefault() ?? "";
            mentor = extras.Where(card => card.Name == "Mentor of the Meek").Select(CardViews.Identity).SingleOrDefault() ?? "";
            flexible = extras.Where(card => card.Name == "Temple of Epiphany").Select(CardViews.Identity).SingleOrDefault() ?? "";
            blueSources = extras.Where(card => card.Name == "Island").Select(CardViews.Identity).ToImmutableArray();
            setup = true;
            while (true) {
                state = await engine.ObserveSimulationAsync() ?? throw new InvalidDataException("Golden public state disappeared.");
                var bots = await provider.ObserveAutomatedSeatsAsync();
                if (host.LastFault != null || state.Status is "error" or "finished")
                    throw new InvalidDataException("Golden stopped before proof: " + JsonSerializer.Serialize(new { host.LastFault, state.Status, state.Error, state.Result }));
                if (state.ViewerId != -1 || state.Decision != null || state.Players.Any(player => !player.Zone("Hand").Cards.IsEmpty || !player.Zone("Library").Cards.IsEmpty)
                    || CardViews.InZones(state).Any(item => !string.IsNullOrEmpty(item.Card.Key)))
                    throw new InvalidDataException("Golden public spectator exposed private faces or action authority.");
                foreach (var bot in bots) {
                    if (bot.State.ViewerId != bot.SeatId || !AiCampaignVisibility.PrivateWindowsAreDisclosed(bot.State, bot.Semantics, bot.Domain))
                        throw new InvalidDataException("Golden private observation exceeded the current seat's disclosure.");
                    if (bot.State.Id == state.Id && bot.State.BoardRevision == state.BoardRevision) {
                        if (!AiWholeSeatEngineCheck.ComparePublicProjection(state, bot.State, directory))
                            throw new InvalidDataException("Golden same-revision public state diverged.");
                        comparisons++;
                    }
                    if (bot.SeatId == 0 && bot.State.ActivePlayerId == 0 && bot.State.PhaseKey is "MAIN1" or "MAIN2") {
                        firstTurn = firstTurn == 0 ? bot.State.Turn : firstTurn;
                        offered |= bot.Domain.Sources.Any(set => set.SourceCardId == target && set.Actions.Any(action => action.Kind == "cast"));
                    }
                    actualRedPool |= bot.SeatId == 0 && bot.Semantics.Payment is { PayerId: 0, Purpose: PaymentPurpose.SpellCost } cost
                        && cost.SourceCardId == target && bot.State.Viewer!.Mana.GetValueOrDefault("R") > 0;
                    actualRedBillPaid |= bot.SeatId == 0 && bot.Semantics.Payment is { PayerId: 0, Purpose: PaymentPurpose.SpellCost } paid
                        && paid.SourceCardId == target && bot.Semantics.UnpaidMana.SequenceEqual(["{U}"])
                        && provider.Accepted.Any(item => item.SourceId == flexible && item.ManaOutput?.Colors is { Count: 1 } colors
                            && colors.GetValueOrDefault("R") == 1);
                }
                diagnostics.ObservePublic(state);
                string facts = AiWholeSeatEngineCheck.ReplayFacts(state);
                if (facts != lastFacts) {
                    lastFacts = facts;
                    await trace.WriteLineAsync(JsonSerializer.Serialize(new { formatVersion = 1, elapsedMilliseconds = timer.Elapsed.TotalMilliseconds, steps, state }));
                }
                actor = state.Players.Single(player => player.Id == 0);
                blueSources = blueSources.Union(actor.Zone("Battlefield").Cards.Where(card => card.Name == "Island")
                    .Select(CardViews.Identity), StringComparer.Ordinal).ToImmutableArray();
                var receipts = provider.Accepted.Where(item => item.Seat == 0).ToArray();
                landPlayed |= receipts.Any(item => item.Land);
                passedOffer |= receipts.Any(item => item.Kind == nameof(PassPriority) && item.TargetOffered);
                stack |= state.Stack.Any(item => item.Kind == StackActionKind.Spell && item.Card is { } card && CardViews.Identity(card) == target);
                var permanent = actor.Zone("Battlefield").Cards.FirstOrDefault(card => CardViews.Identity(card) == target);
                resolved |= permanent != null;
                bool cast = receipts.Any(item => item.Kind == nameof(UseOfferedAction) && item.SourceId == target && item.Cast);
                var numeric = receipts.LastOrDefault(item => item.Kind == nameof(ChooseNumber) && item.CostOwner == target
                    && item.NumericPurpose == NumericDecisionPurpose.AnnounceX && item.Number > 0);
                bool spellPayment = receipts.Any(item => item.CostOwner == target && item.Payer == 0 && item.PaymentPurpose == PaymentPurpose.SpellCost
                    && item.Kind is nameof(UseOfferedAction) or nameof(PayMana) or nameof(SpendPoolMana));
                var optional = receipts.LastOrDefault(item => item.CostOwner == mentor && item.PaymentPurpose == PaymentPurpose.TriggeredAbilityCost
                    && item.Payer == 0 && item.Kind == (scenario.Id == "optional-decline" ? nameof(CancelPayment) : nameof(UseOfferedAction)));
                bool ended = firstTurn != 0 && (state.ActivePlayerId != 0 && state.Turn > firstTurn
                    || state.ActivePlayerId == 0 && state.Turn == firstTurn && state.PhaseKey is "END_OF_TURN" or "CLEANUP");
                bool positive;
                switch (scenario.Id) {
                    case "x-funded":
                        int counters = permanent?.Counters.Where(pair => pair.Key is "P1P1" or "+1/+1").Sum(pair => pair.Value) ?? 0;
                        positive = cast && stack && spellPayment && numeric?.Number is > 0 && permanent?.Power == numeric.Number
                            && permanent.Toughness == numeric.Number && counters == numeric.Number;
                        detail = "Accepted positive X and exact-source public stack, battlefield statistics and counters agree.";
                        break;
                    case "x-unfunded":
                        if (cast || receipts.Any(item => item.Kind == nameof(ChooseNumber) && item.CostOwner == target))
                            throw new InvalidDataException("The unfunded X source entered casting during its first opportunity.");
                        positive = ended && offered && passedOffer && landPlayed && !resolved;
                        detail = "The first own turn completed after a real cast offer and land play, without starting an unfunded X cast.";
                        break;
                    case "consuming-funded":
                        positive = cast && stack && spellPayment && permanent is { Power: 7, Toughness: 7 }
                            && receipts.Count(item => item.SourceId == vein && item.Consumed) == 1
                            && !actor.Zone("Battlefield").Cards.Any(card => CardViews.Identity(card) == vein)
                            && actor.Zone("Graveyard").Cards.Any(card => card.Name == "Crystal Vein");
                        detail = "A four-mana commander resolved after exactly one accepted consuming-source activation and its public graveyard move.";
                        break;
                    case "consuming-shortfall":
                        if (cast) throw new InvalidDataException("A three-unit first-turn resource set started a four-mana commander cast.");
                        positive = ended && offered && passedOffer && landPlayed && !resolved;
                        detail = "The first own turn completed with a real commander offer while preserving insufficient consuming resources.";
                        break;
                    case "optional-decline":
                        positive = ended && resolved && optional != null && actor.Zone("Hand").Count == optional.HandCount;
                        detail = "A real optional trigger payment was declined through its current cancel control and no card was drawn.";
                        break;
                    case "optional-pay":
                        positive = resolved && optional != null && actor.Zone("Hand").Count == optional.HandCount + 1
                            && state.ActivePlayerId == 0 && state.Turn == firstTurn;
                        detail = "A real optional trigger cost consumed offered mana and the public hand count increased by exactly one.";
                        break;
                    case "flexible-output":
                        if (receipts.Any(item => item.Kind == nameof(CancelPayment) && item.CostOwner == target))
                            throw new InvalidDataException("The flexible-output commander payment was cancelled after initiating its funded cast.");
                        bool requestedRed = receipts.Any(item => item.SourceId == flexible && item.CostOwner == target && item.Payer == 0
                            && item.PaymentPurpose == PaymentPurpose.SpellCost && item.ManaOutput?.Colors is { Count: 1 } colors
                            && colors.GetValueOrDefault("R") == 1);
                        bool requestedBlue = receipts.Any(item => blueSources.Contains(item.SourceId) && item.CostOwner == target && item.Payer == 0
                            && item.PaymentPurpose == PaymentPurpose.SpellCost && item.ManaOutput?.Colors is { Count: 1 } colors
                            && colors.GetValueOrDefault("U") == 1);
                        positive = cast && stack && spellPayment && requestedRed && requestedBlue && actualRedBillPaid
                            && permanent is { Power: 1, Toughness: 3 };
                        detail = "The normal policy's exact red flexible-source intent reduced the UR bill to U, an Island supplied blue, and the commander resolved without cancelling payment.";
                        break;
                    default: throw new InvalidDataException("Unknown golden scenario.");
                }
                if (positive && setup && comparisons > 0) { passed = true; stop = "proved-before-cleanup"; break; }
                if (ended) { stop = "first-own-turn-ended-without-proof"; detail = "Required exact continuation was not established before the first own turn ended."; break; }
                if (timer.Elapsed >= TimeSpan.FromSeconds(30)) { stop = "inconclusive-time-cutoff"; detail = "The declared continuation did not complete within the observation budget."; break; }
                provider.Target = target;
                await host.StepAsync();
                steps++;
                await Task.Delay(1);
            }
        } catch (Exception error) {
            primaryFailure = error;
            stop = "mechanical-or-continuation-failure"; detail = error.ToString();
            diagnostics.PreserveFailure(error, "golden", steps, provider.Last);
        } finally {
            try {
                var observationError = diagnostics.PreserveObservations(provider.Last);
                if (observationError != null) CaptureFailure(observationError, "private-observation-capture-failure");
                if (state != null) {
                    try {
                        File.WriteAllText(Path.Combine(directory, "pre-cleanup-public.json"), JsonSerializer.Serialize(state));
                        publicCaptured = true;
                    } catch (Exception error) { CaptureFailure(error, "public-observation-capture-failure"); }
                }
            } finally {
                if (state != null) {
                    try { await engine.StopSimulationAsync(state.Id); }
                    catch (Exception error) { CaptureFailure(error, "teardown-failure"); }
                }
            }
        }
        var summary = diagnostics.Summary();
        if (!summary.DiagnosticErrors.IsEmpty) CaptureFailure(new IOException(string.Join("\n", summary.DiagnosticErrors)), "diagnostic-evidence-failure");
        var evidence = JsonSerializer.SerializeToElement(new { setup, target, vein, mentor, flexible, blueSources, actualRedPool, actualRedBillPaid, offered, passedOffer, landPlayed,
            stack, resolved, firstTurn, capturedBeforeCleanup = publicCaptured, finalTurn = state?.Turn, finalPhase = state?.PhaseKey,
            commands = provider.Accepted, diagnostics = summary, captureErrors, primaryFailure = primaryFailure?.ToString(), engineRandomnessControlled = false });
        var result = new AiGoldenResult(scenario.Id, passed, detail, comparisons, provider.Accepted.Count, steps,
            timer.Elapsed.TotalMilliseconds, stop, evidence);
        File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return result;

        void CaptureFailure(Exception error, string classification)
        {
            if (passed) stop = classification;
            passed = false;
            captureErrors.Add(classification + ": " + error);
            detail += "\n" + classification + ": " + error;
            primaryFailure ??= error;
            diagnostics.PreserveFailure(primaryFailure, classification, steps, provider.Last);
        }
    }

    private sealed record AcceptedCommand(int Seat, string Kind, int Turn, string Phase, string SourceId, string CostOwner,
        int? Number, NumericDecisionPurpose? NumericPurpose, PaymentPurpose? PaymentPurpose, int? Payer, int HandCount,
        bool Consumed, bool Land, bool Cast, bool TargetOffered, ManaOutputChoice? ManaOutput);

    private sealed class ReceiptProbe(AiCampaignProvider inner) : IAutomatedSeatProvider
    {
        internal string Target { get; set; } = "";
        internal List<AcceptedCommand> Accepted { get; } = [];
        internal ImmutableArray<AutomatedSeatObservation> Last => inner.Last;
        public Task EnableAutomatedSeatsAsync() => inner.EnableAutomatedSeatsAsync();
        public Task ReportAutomationFaultAsync(string game, int seat, string reason) => inner.ReportAutomationFaultAsync(game, seat, reason);
        public Task<ImmutableArray<AutomatedSeatObservation>> ObserveAutomatedSeatsAsync() => inner.ObserveAutomatedSeatsAsync();
        public async Task ExecuteAutomatedAsync(int seat, GameCommand command)
        {
            var observation = inner.Last.Single(item => item.SeatId == seat);
            var source = command is UseOfferedAction offered ? observation.Domain.Sources.Single(set => set.SourceCardKey == offered.Card) : null;
            var action = command is UseOfferedAction use ? observation.Domain.Actions.Single(item => item.ActionId == use.ActionId) : null;
            var route = command is UseOfferedAction chosen ? source!.Actions.Single(item => item.Id == chosen.ActionId) : null;
            string owner = observation.Semantics.Numeric?.SourceCardId ?? observation.Semantics.Payment?.SourceCardId
                ?? observation.State.Decision?.Context.Source?.CardId ?? "";
            if (owner.Length == 0 && observation.State.Decision?.Context.Source?.Card is { } portrait) owner = CardViews.Identity(portrait);
            await inner.ExecuteAutomatedAsync(seat, command);
            Accepted.Add(new(seat, command.GetType().Name, observation.State.Turn, observation.State.PhaseKey, source?.SourceCardId ?? "", owner,
                command is ChooseNumber number ? number.Value : null, observation.Semantics.Numeric?.Purpose,
                observation.Semantics.Payment?.Purpose, observation.Semantics.Payment?.PayerId,
                observation.State.Viewer!.Zone("Hand").Count, action?.Costs.Any(cost => cost.Kind == ActionCostKind.Sacrifice && cost.FromSource && cost.Amount == 1) == true,
                action?.Effect == ActionEffectKind.Land, route?.Kind == "cast",
                observation.Domain.Sources.Any(set => set.SourceCardId == Target && set.Actions.Any(item => item.Kind == "cast")),
                (command as UseOfferedAction)?.ManaOutput));
        }
    }
}
