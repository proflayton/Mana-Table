using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Mana.AI;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

internal static partial class AiReleaseGoldens
{
    private const string TypedReturnId = "typed-return-vigilance-elimination";

    internal static async Task RunTypedReturnStandaloneAsync(ForgePaths paths, string productionJar,
        string sourceRevision, string sourceManifest)
    {
        Directory.CreateDirectory(paths.Profile);
        AiReleaseRuntime? runtime = null; AiGoldenResult? result = null; Exception? failure = null;
        var captureErrors = new List<string>();
        try {
            runtime = AiReleaseEvaluation.VerifyRuntime(paths, productionJar, verifyPackage: true,
                sourceRevision: sourceRevision, sourceManifest: sourceManifest);
            await using var engine = new ForgeEngine(paths);
            await engine.InitializeAsync(); await engine.EnableAutomatedSeatsAsync();
            result = await RunTypedReturnGoldenAsync(engine, Path.Combine(paths.Profile, "ai-goldens"));
            if (!result.Passed) throw new InvalidOperationException(result.Detail);
        } catch (Exception error) { failure = error; }
        finally {
            failure = CaptureTypedReturnEvidence(failure, captureErrors, () => File.WriteAllText(Path.Combine(paths.Profile, "typed-return-results.json"), JsonSerializer.Serialize(new {
                runtime, result, failure = failure?.ToString(), captureErrors, passed = failure == null && result?.Passed == true,
                naturalGameCompletionCredit = false, campaignDeckQualificationCredit = false
            }, new JsonSerializerOptions { WriteIndented = true })));
        }
        if (failure != null) throw new InvalidOperationException("The typed return engine golden failed; retain the first evidence. "
            + string.Join("\n", captureErrors), failure);
    }

    private static async Task<AiGoldenResult> RunTypedReturnGoldenAsync(ForgeEngine engine, string root)
    {
        string[] names = ["AI typed return vigilance elimination", "Typed return prowess opponent",
            "Typed return harmless opponent", "Typed return defending opponent"];
        string[] commanders = ["Rhys the Redeemed", "Balmor, Battlemage Captain", "Traxos, Scourge of Kroog", "Rhys the Redeemed"];
        var decks = ImmutableArray.CreateBuilder<string>();
        for (int index = 0; index < names.Length; index++) {
            var imported = await engine.ImportAsync(names[index], "Deck\n99 Wastes\nCommander\n1 " + commanders[index]);
            var details = await engine.OpenDeckAsync(imported.Id);
            if (details.StructureValid == false || details.Problem.Length > 0 || !details.Issues.IsEmpty)
                throw new InvalidDataException("Invalid typed return Commander deck: " + names[index]);
            var saved = await engine.SaveDeckAsync(details.Id, details.Revision);
            if (saved.SaveError.Length > 0) throw new InvalidDataException(saved.SaveError);
            decks.Add("deck:" + saved.Id);
        }
        string directory = Path.Combine(root, TypedReturnId); Directory.CreateDirectory(directory);
        using var diagnostics = new AiCampaignDiagnostics(directory);
        var observed = new AiCampaignProvider(engine, diagnostics);
        var probe = new TypedReturnProbe(observed, directory);
        await using var host = new AiTableHost(probe,
            _ => new TypedReturnPolicy(diagnostics.Record(new CasualCommanderPolicy()), probe), 261026,
            new(TimeSpan.FromSeconds(10), 4096));
        await using var gzip = new GZipStream(File.Create(Path.Combine(directory, "public.jsonl.gz")), CompressionLevel.Fastest);
        await using var trace = new StreamWriter(gzip);
        var clock = Stopwatch.StartNew();
        GameSnapshot? state = null; ConfiguredSimulationStart? accepted = null; Exception? failure = null;
        var captureErrors = new List<string>(); var impacts = new Dictionary<long, PublicImpact>();
        int steps = 0, comparisons = 0;
        bool passed = false, sixAttackers = false, threeBlocks = false, eliminated = false, vigilanceAfter = false;
        string stop = "not-started", detail = "The required same-combat elimination was not observed.";
        try {
            var setup = new ConfiguredSimulationSetup(new(MatchRulesets.FreeForAllCommander,
                [new(0, 0, MatchSeatRole.Participant), new(1, 1, MatchSeatRole.Participant),
                 new(2, 2, MatchSeatRole.Participant), new(3, 3, MatchSeatRole.Participant)]), decks.ToImmutable());
            accepted = await engine.StartConfiguredSimulationAsync(setup);
            ConfiguredSimulationFacts.ValidateReceipt(accepted, setup);
            state = accepted.State;
            while (!ConfiguredSimulationFacts.IsReady(accepted, state)) {
                if (clock.Elapsed >= TimeSpan.FromSeconds(10)) throw new TimeoutException("Typed return setup did not publish a ready roster within ten seconds.");
                await Task.Delay(2);
                state = await engine.ObserveSimulationAsync() ?? throw new InvalidDataException("Typed return setup disappeared.");
            }
            ConfiguredSimulationFacts.ValidateState(accepted, state);
            // Preserve the original receipt; only this later independently
            // observed public publication supplies the provider's ready baseline.
            observed.BindConfigured(accepted with { State = state });
            int Player(int slot) => accepted.Participants.Single(item => item.Index == slot).PlayerId;
            probe.Actor = Player(0); probe.Target = Player(3); probe.ProwessOwner = Player(1);
            var expected = new[] { new[] { "Concordant Crossroads", "Steadfast Guard", "Steadfast Guard", "Steadfast Guard", "Steadfast Guard", "Steadfast Guard", "Steadfast Guard" },
                new[] { "Monastery Swiftspear", "Thermo-Alchemist" }, new[] { "Ornithopter" }, new[] { "Grizzly Bears", "Grizzly Bears", "Grizzly Bears" } };
            for (int slot = 0; slot < 4; slot++) {
                var player = state.Players.Single(item => item.Id == Player(slot));
                string[] command = slot == 0 ? [commanders[slot], "Power Play"] : [commanders[slot]];
                if (!player.Zone("Battlefield").Cards.Select(card => card.Name).Order().SequenceEqual(expected[slot].Order())
                    || player.Life != (slot == 3 ? 6 : 40)
                    || !player.Zone("Command").Cards.Select(card => card.Name).Order().SequenceEqual(command.Order()))
                    throw new InvalidDataException("The typed return pregame battlefield/life/commander differs from its declared setup.");
            }
            probe.Cohort = state.Players.Single(item => item.Id == probe.Actor).Zone("Battlefield").Cards
                .Where(card => card.Name == "Steadfast Guard").Select(CardViews.Identity).ToImmutableHashSet(StringComparer.Ordinal);
            if (probe.Cohort.Count != 6) throw new InvalidDataException("Typed return setup did not establish six distinct guards.");
            // Ready roster publications can precede ordinary opening-hand
            // inputs at turn zero. The first owned attack below must prove
            // Power Play's actual first natural actor turn, without forcing it.
            File.WriteAllText(Path.Combine(directory, "setup.json"), JsonSerializer.Serialize(new {
                accepted, readyState = state, readyTurn = state.Turn, readyActivePlayer = state.ActivePlayerId,
                probe.Actor, probe.Target, probe.Cohort
            }));
            while (true) {
                if (clock.Elapsed >= TimeSpan.FromSeconds(30)) { stop = "time-budget"; break; }
                await host.StepAsync(); steps++;
                state = await engine.ObserveSimulationAsync() ?? throw new InvalidDataException("Typed return public state disappeared.");
                ConfiguredSimulationFacts.ValidateState(accepted, state);
                if (host.LastFault != null) throw new InvalidOperationException(JsonSerializer.Serialize(host.LastFault));
                foreach (var bot in observed.Last) {
                    if (!AiCampaignVisibility.PrivateWindowsAreDisclosed(bot.State, bot.Semantics, bot.Domain))
                        throw new InvalidDataException("Typed return observation disclosed hidden information.");
                    if (state.BoardRevision == bot.State.BoardRevision) {
                        if (!AiWholeSeatEngineCheck.ComparePublicProjection(state, bot.State, directory))
                            throw new InvalidDataException("Typed return same-board public projection differs.");
                        comparisons++;
                    }
                    // Spectator activity stays empty. Attribution comes only
                    // from the actor's authorized private activity stream.
                    if (probe.InitialValidated && bot.SeatId == probe.Actor && bot.State.Turn == 1
                        && bot.State.ActivePlayerId == probe.Actor) foreach (var entry in bot.State.Activity)
                        if (entry.Impact is { Kind: "damage", Combat: true, Amount: > 0 } impact
                            && impact.SourceControllerId == probe.Actor && impact.RecipientControllerId == probe.Target
                            && impact.PlayerId == probe.Target && impact.SourceCardId is { } source && probe.Cohort.Contains(source))
                            impacts[entry.Id] = impact;
                }
                await trace.WriteLineAsync(JsonSerializer.Serialize(new { steps, state }));
                var attacks = state.Combat?.Attackers.Where(item => probe.Cohort.Contains(item.CardId)).ToArray() ?? [];
                // Declaration IDs belong to one seat's input phase. The public
                // combat has no such ID. This minimal fixture has no extra-turn
                // or extra-combat effects, so the first actor turn, exact cohort
                // and observed block pairs delimit its single combat instead.
                bool fixtureCombat = probe.InitialValidated && state.Turn == 1 && state.ActivePlayerId == probe.Actor;
                var survivors = state.Players.Single(item => item.Id == probe.Actor).Zone("Battlefield").Cards
                    .Where(card => probe.Cohort.Contains(CardViews.Identity(card))).ToArray();
                if (fixtureCombat && attacks.Length == 6 && attacks.All(item => item.DefendingPlayerId == probe.Target)
                    && survivors.Length == 6 && survivors.All(card => !card.Tapped && HasTrait(card, "vigilance"))) sixAttackers = true;
                var blockReceipts = probe.Accepted.Where(item => item.Kind == nameof(ToggleBlock) && item.Seat == probe.Target).ToArray();
                if (fixtureCombat && attacks.SelectMany(item => item.BlockerIds).Distinct(StringComparer.Ordinal).Count() == 3
                    && attacks.Count(item => !item.BlockerIds.IsEmpty) == 3 && blockReceipts.Length == 3
                    && blockReceipts.All(item => attacks.Any(attack => attack.CardId == item.Other && attack.BlockerIds.Contains(item.Source))))
                    threeBlocks = true;
                var target = state.Players.Single(item => item.Id == probe.Target);
                eliminated |= fixtureCombat && target.Eliminated && target.Life <= 0;
                vigilanceAfter |= fixtureCombat && eliminated && survivors.Length >= 3 && survivors.All(card => !card.Tapped && HasTrait(card, "vigilance"));
                var toggles = probe.Accepted.Where(item => item.Kind == nameof(ToggleAttackPlayer) && item.Seat == probe.Actor).ToArray();
                var blocks = probe.Accepted.Where(item => item.Kind == nameof(ToggleBlock) && item.Seat == probe.Target).ToArray();
                bool staged = toggles.Length == 6 && toggles.Select(item => item.Source).ToHashSet(StringComparer.Ordinal).SetEquals(probe.Cohort)
                    && toggles.All(item => item.Target == probe.Target && item.Reason == "combat-stage-swarm-finish" && item.Declaration == probe.Declaration)
                    && probe.Accepted.Any(item => item.Seat == probe.Actor && item.Kind == nameof(CompleteAttackers)
                        && item.Reason == "combat-complete-swarm-finish" && item.Declaration == probe.Declaration);
                bool realBlocks = blocks.Length == 3 && blocks.Select(item => item.Source).Distinct(StringComparer.Ordinal).Count() == 3
                    && probe.BlockDeclaration.Length > 0
                    && blocks.All(item => probe.Cohort.Contains(item.Other) && item.Declaration == probe.BlockDeclaration)
                    && probe.Accepted.Any(item => item.Kind == nameof(CompleteBlockers) && item.Seat == probe.Target
                        && item.Declaration == probe.BlockDeclaration);
                if (probe.InitialValidated && staged && realBlocks && sixAttackers && threeBlocks && eliminated && vigilanceAfter
                    && impacts.Values.Sum(item => item.Amount) >= probe.InitialLife && comparisons > 0) {
                    passed = true; stop = "observed-consequence";
                    detail = "Six current vigilance attackers were staged by the real policy, three actual blocks committed, and attributed combat damage eliminated the target while surviving guards stayed untapped.";
                    break;
                }
                if (state.Status == "finished" || state.Turn > 1) { stop = "unobserved"; break; }
                await Task.Delay(1);
            }
        } catch (Exception error) { failure = error; stop = "mechanical-failure"; detail = error.ToString(); }
        finally {
            try { File.WriteAllText(Path.Combine(directory, "pre-cleanup-public-state.json"), JsonSerializer.Serialize(state)); }
            catch (Exception error) { captureErrors.Add(error.ToString()); failure ??= error; }
            if (state != null) try { await engine.StopSimulationAsync(state.Id); }
                catch (Exception error) { captureErrors.Add(error.ToString()); failure ??= error; }
        }
        var summary = diagnostics.Summary();
        captureErrors.AddRange(summary.DiagnosticErrors);
        bool cleanEvidence = TypedReturnEvidenceClean(summary.DiagnosticErrors, summary.Findings);
        if (!cleanEvidence && stop == "observed-consequence") { stop = "evidence-failure"; detail = "The consequence was observed but diagnostic errors or policy review findings prevent a pass."; }
        JsonElement Evidence() => JsonSerializer.SerializeToElement(new { accepted, probe.Actor, probe.Target, probe.Cohort,
            actorAttackDeclaration = probe.Declaration, targetBlockDeclaration = probe.BlockDeclaration,
            probe.InitialValidated, probe.InitialLife, commands = probe.Accepted, sixAttackers, threeBlocks, eliminated, vigilanceAfter,
            impacts, diagnostics = summary, failure = failure?.ToString(), captureErrors,
            combatCorrelation = "Bound first natural actor turn and exact cohort/block pairs in the declared fixture without extra-combat/extra-turn effects; declaration IDs remain separate seat/input scopes.",
            campaignDeckQualificationCredit = false, naturalGameCompletionCredit = false });
        var result = new AiGoldenResult(TypedReturnId, passed && failure == null && captureErrors.Count == 0 && cleanEvidence, detail,
            comparisons, probe.TotalAccepted, steps, clock.Elapsed.TotalMilliseconds, stop, Evidence());
        int beforeWriteErrors = captureErrors.Count;
        failure = CaptureTypedReturnEvidence(failure, captureErrors,
            () => File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })));
        if (captureErrors.Count != beforeWriteErrors)
            result = result with { Passed = false, Detail = failure!.ToString(), StopReason = "evidence-failure", Evidence = Evidence() };
        return result;
    }

    private static Exception? CaptureTypedReturnEvidence(Exception? primary, List<string> errors, Action write)
    {
        try { write(); }
        catch (Exception error) { errors.Add(error.ToString()); return primary ?? error; }
        return primary;
    }

    private static bool TypedReturnEvidenceClean(ImmutableArray<string> errors, ImmutableArray<AiCampaignFinding> findings)
        => !errors.IsDefault && errors.IsEmpty && !findings.IsDefault && findings.IsEmpty;

    internal static void CheckTypedReturnEvidence(Action<bool, string> check)
    {
        var primary = new InvalidOperationException("marker provider failure");
        var writerFailure = new IOException("marker report failure");
        var errors = new List<string>();
        var retained = CaptureTypedReturnEvidence(primary, errors, () => throw writerFailure);
        check(ReferenceEquals(retained, primary) && errors.Single().Contains("marker report failure"),
            "A failing typed return evidence writer preserves the exact earlier provider exception and records the secondary failure");
        errors.Clear();
        retained = CaptureTypedReturnEvidence(null, errors, () => throw writerFailure);
        check(ReferenceEquals(retained, writerFailure) && errors.Count == 1,
            "A report failure without an earlier exception fails the typed return check itself");
        check(ReferenceEquals(CaptureTypedReturnEvidence(primary, errors, () => { }), primary),
            "Successful typed return capture never clears the primary failure");
        check(TypedReturnEvidenceClean([], []) && !TypedReturnEvidenceClean(["recording failed"], [])
            && !TypedReturnEvidenceClean([], [new("quality-review", 0, 1, "decision", "review")])
            && !TypedReturnEvidenceClean(default, []) && !TypedReturnEvidenceClean([], default),
            "Typed return success requires explicitly empty diagnostic errors and policy findings, never missing or nonempty evidence");
    }

    private static bool HasTrait(Card card, string trait) => card.CombatKeywords.Contains(trait, StringComparer.OrdinalIgnoreCase)
        || card.States is { } states && states.Any(item => item.Kind == "ability" && item.Value != 0 && item.Key == trait);

    private sealed record TypedReturnCommand(int Seat, string Kind, string DecisionId, long Revision, long BoardRevision,
        string Declaration, string Source, string Other, int? Target, string Reason, AiDecisionStamp Stamp, JsonElement Command);

    private sealed class TypedReturnPolicy(IAiPolicy inner, TypedReturnProbe probe) : IAiPolicy
    {
        public AiPolicyDescriptor Descriptor => inner.Descriptor;
        public async Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation)
        {
            probe.Before(request.Frame);
            var proposal = await inner.EvaluateAsync(request, cancellation);
            if (proposal is AiProposal.Answer answer) probe.Pending[request.Frame.Stamp.SeatId] = (request.Frame, answer);
            return proposal;
        }
    }

    private sealed class TypedReturnProbe(AiCampaignProvider inner, string directory) : IAutomatedSeatProvider
    {
        internal int Actor, Target, ProwessOwner, InitialLife, TotalAccepted;
        internal string Declaration = "", BlockDeclaration = "";
        internal bool InitialValidated;
        internal ImmutableHashSet<string> Cohort = ImmutableHashSet<string>.Empty;
        internal readonly Dictionary<int, (AiDecisionFrame Frame, AiProposal.Answer Answer)> Pending = [];
        internal readonly List<TypedReturnCommand> Accepted = [];
        internal void Before(AiDecisionFrame frame)
        {
            var state = frame.State;
            if (state.ViewerId == Target && state.Decision?.Payload is BlockPayload) {
                if (!InitialValidated || state.Turn != 1 || state.ActivePlayerId != Actor
                    || state.Combat is not { DeclarationId.Length: > 0 } blocking
                    || blocking.Attackers.Length != 6 || blocking.Attackers.Any(item => !Cohort.Contains(item.CardId) || item.DefendingPlayerId != Target)
                    || BlockDeclaration.Length > 0 && BlockDeclaration != blocking.DeclarationId)
                    throw new InvalidDataException("The defender's own block scope lost the first-turn exact attacker cohort.");
                BlockDeclaration = blocking.DeclarationId;
            }
            if (state.ViewerId != Actor || state.Decision?.Payload is not AttackPayload || InitialValidated) return;
            File.WriteAllText(Path.Combine(directory, "initial-owned-attack.json"), JsonSerializer.Serialize(state));
            var cards = state.Viewer!.Zone("Battlefield").Cards.Where(card => Cohort.Contains(CardViews.Identity(card))).ToArray();
            var target = state.Players.Single(item => item.Id == Target);
            var blockers = target.Zone("Battlefield").Cards.Where(card => card.Type.Contains("Creature", StringComparison.OrdinalIgnoreCase)).ToArray();
            var remaining = state.Players.Single(item => item.Id == ProwessOwner).Zone("Battlefield").Cards;
            if (state.Turn != 1 || state.ActivePlayerId != Actor || state.CommanderCombat == null
                || !state.States.IsEmpty || state.Players.Any(player => !player.States.IsEmpty || player.Counters.Any(item => item.Value != 0))
                || state.Combat is not { Attackers.IsEmpty: true, DeclarationId.Length: > 0 } combat
                || combat.AttackOptions.Length != 6 || cards.Length != 6 || cards.Any(card => card.Power != 2 || card.Toughness != 2 || card.Tapped || card.Sick
                    || !HasTrait(card, "vigilance") || !HasTrait(card, "haste")
                    || !frame.Allows(new ToggleAttackPlayer(state.Id, state.Decision.Id, card.Key, Target)))
                || target.Life is not (5 or 6) || blockers.Length != 3
                || blockers.Any(card => card.Tapped || card.BlockingCapacity is not { Unlimited: false, Maximum: 1 })
                || !remaining.Any(card => card.Name == "Monastery Swiftspear" && card.Power == 1 && HasTrait(card, "prowess"))
                || !remaining.Any(card => card.Name == "Thermo-Alchemist" && card.Power == 0 && HasTrait(card, "defender")))
                throw new InvalidDataException("The initial real typed return attack scope does not establish the exact six-body/three-blocker admissible witness.");
            Declaration = combat.DeclarationId; InitialLife = target.Life; InitialValidated = true;
        }
        public Task EnableAutomatedSeatsAsync(CancellationToken cancellation = default) => inner.EnableAutomatedSeatsAsync(cancellation);
        public Task ReportAutomationFaultAsync(string gameId, int seatId, string reason, CancellationToken cancellation = default)
            => inner.ReportAutomationFaultAsync(gameId, seatId, reason, cancellation);
        public Task<ImmutableArray<AutomatedSeatObservation>> ObserveAutomatedSeatsAsync(CancellationToken cancellation = default)
            => inner.ObserveAutomatedSeatsAsync(cancellation);
        public async Task ExecuteAutomatedAsync(int seatId, GameCommand command, CancellationToken cancellation = default)
        {
            TypedReturnCommand? receipt = null;
            if (command is ToggleAttackPlayer or CompleteAttackers or ToggleBlock or CompleteBlockers) {
                if (!Pending.TryGetValue(seatId, out var pending) || pending.Answer.Command != command
                    || command is not DecisionCommand decision || !pending.Frame.Allows(decision))
                    throw new InvalidDataException("A combat command lacks its exact current policy offer.");
                var state = pending.Frame.State;
                if (state.Turn != 1 || state.ActivePlayerId != Actor
                    || seatId == Actor && command is (ToggleAttackPlayer or CompleteAttackers) && state.Combat?.DeclarationId != Declaration
                    || seatId == Target && command is (ToggleBlock or CompleteBlockers) && state.Combat?.DeclarationId != BlockDeclaration)
                    throw new InvalidDataException("A combat command lost its own first-turn declaration scope.");
                Card Find(string key) => state.Players.SelectMany(player => player.Zones).SelectMany(zone => zone.Cards).Single(card => card.Key == key);
                string source = command switch { ToggleAttackPlayer attack => CardViews.Identity(Find(attack.Attacker)), ToggleBlock block => CardViews.Identity(Find(block.Blocker)), _ => "" };
                string other = command is ToggleBlock assignment ? CardViews.Identity(Find(assignment.Attacker)) : "";
                receipt = new(seatId, command.GetType().Name, decision.DecisionId, state.Revision, state.BoardRevision,
                    state.Combat?.DeclarationId ?? "", source, other, command is ToggleAttackPlayer attackPlayer ? attackPlayer.Player : null,
                    pending.Answer.ReasonCode, pending.Frame.Stamp, JsonSerializer.SerializeToElement(command, command.GetType()));
                if (Accepted.Count >= 32) throw new InvalidDataException("The bounded typed return command proof exceeded 32 entries.");
                File.WriteAllText(Path.Combine(directory, "current-combat-" + Accepted.Count + ".json"), JsonSerializer.Serialize(new {
                    state, pending.Frame.Domain, pending.Frame.Semantics, receipt
                }));
            }
            await inner.ExecuteAutomatedAsync(seatId, command, cancellation); TotalAccepted++;
            if (receipt != null) {
                Accepted.Add(receipt);
            }
        }
    }
}
