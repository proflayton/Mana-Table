using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mana.AI;
using Mana.Contracts;

// Authored decision controls, not real-provider captures or a strength study.
internal static class AiUncertainLifeRiskRegression
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private static IAiPolicy Candidate() => AiExperimentPolicyRegistry.Create(new(AiExperimentPolicyRegistry.UncertainLifeRiskFactoryId));

    internal static async Task RunAsync(Action<bool, string> check)
    {
        check(Candidate() is CasualCommanderPolicy { Options: { AttachmentEvaluation: true,
                LibraryAcquisitionEvaluation: false, UncertainLifeRiskEvaluation: true } }
                && Candidate().Descriptor.Id == AiExperimentPolicyRegistry.UncertainLifeRiskFactoryId
                && new CasualCommanderPolicy().Descriptor.Id == "casual-commander.v6",
            "Uncertain life risk has a separate closed identity while shipping v6 remains unchanged");
        using (var json = JsonDocument.Parse("{\"factoryId\":\"casual-commander.v6.uncertain-life-risk.v1\",\"attachmentEvaluation\":true}"))
            check(AiExperimentPolicyRegistry.Parse(json.RootElement).FactoryId == AiExperimentPolicyRegistry.UncertainLifeRiskFactoryId,
                "The life-risk registry parses its exact versioned factory without accepting weights");
        foreach (var bad in new CasualCommanderOptions[] { new(false, false, true), new(true, true, true), new(false, true, true) })
            Reject(check, () => _ = new CasualCommanderPolicy(bad),
                "The public constructor refuses risk/acquisition/attachment-ablation composition");
        Reject(check, () => _ = AiExperimentPolicyRegistry.Create(new(AiExperimentPolicyRegistry.UncertainLifeRiskFactoryId, false)),
            "The closed risk factory also refuses attachment ablation");
        using (var invalid = JsonDocument.Parse("{\"factoryId\":\"casual-commander.v6.uncertain-life-risk.v1\",\"attachmentEvaluation\":false}"))
            Reject(check, () => _ = AiExperimentPolicyRegistry.Parse(invalid.RootElement),
                "Serialized risk settings cannot introduce an attachment ablation");

        var original = Read("protected-12-4");
        string before = JsonSerializer.Serialize(original, Json);
        var baseline = await Evaluate(original, false);
        var candidate = await Evaluate(original);
        var repeat = await Evaluate(original);
        check(Selected(baseline, 41) && Selected(candidate, 17) && Equivalent(candidate.Proposal, repeat.Proposal),
            "Frozen v6 chooses the unpriced branch; the distinct candidate reproducibly allows Draw at life12/exposure4");
        check(before == JsonSerializer.Serialize(original, Json) && original.State.Players.All(player =>
                player.Zones.Where(zone => zone.Name == "Library").All(zone => zone.Cards.IsEmpty && zone.TopCard == null)),
            "Risk evaluation preserves immutable input and receives no player's library faces or top card");
        check(Selected(await Evaluate(Read("renamed-protected-12-4")), 93),
            "Renamed source, misleading prose and reversed/reindexed choices preserve the structural current Draw decision");

        foreach (int count in new[] { 1, 2, 3, 4, 10 }) {
            var boundary = Exposure(original, 4 * count, count);
            check(Selected(await Evaluate(boundary), 17), $"The frozen four-life stress uses conservative equality at exposure{count}");
            await Parity(check, Exposure(original, 4 * count + 1, count),
                $"Above the stress boundary at exposure{count}, ordinary v6 chooses the alternative");
        }
        await Parity(check, Read("high-life-40-4"), "Life40/exposure4 preserves baseline willingness to spend life");
        await Parity(check, Read("small-exposure-12-1"), "Life12/exposure1 preserves baseline willingness to spend life");
        check(Selected(await Evaluate(Exposure(original, 8, 100, library: 2)), 17),
            "Nominal exposure caps at the opponent's public two-card library");
        await Parity(check, Exposure(original, 9, 100, library: 2), "The public exposure cap preserves the ordinary branch above its boundary");
        check(Selected(await Evaluate(Exposure(original, 12, 0)), 41),
            "A certified zero-counter control retains the non-draw branch");

        foreach (int amount in new[] { 0, 1 }) {
            var known = Read("known-self-loss-" + amount);
            var old = await Evaluate(known, false); var current = await Evaluate(known);
            check(Selected(current, 41) && Equivalent(old.Proposal, current.Proposal)
                    && old.Evidence?.RemainingWork == current.Evidence?.RemainingWork,
                $"Known own loss{amount} keeps ordinary comparative valuation instead of always allowing Draw");
        }
        var knownFive = Read("known-self-loss-1");
        var fixedOption = knownFive.Semantics.Options[1];
        knownFive = knownFive with { Semantics = knownFive.Semantics with { Options = [knownFive.Semantics.Options[0],
            fixedOption with { Amount = 5, OperationGraph = fixedOption.OperationGraph with {
                Operations = [fixedOption.OperationGraph.Operations[0] with { Amount = 5 }] } }] } };
        check(Selected(await Evaluate(knownFive), 17) && Selected(await Evaluate(knownFive, false), 17),
            "A known five-life cost still uses existing utility to allow Draw; the risk rule does not replace bounded comparisons");

        foreach (int library in new[] { 0, 1 })
            await Parity(check, Exposure(original, 12, 4, library),
                "Empty/last opponent library retains exact v6 terminal treatment (count " + library + ")");
        foreach (bool ally in new[] { false, true }) {
            var relations = original with { State = original.State with { Players = original.State.Players.Select(player =>
                player with { TeamId = ally ? 1 : null }).ToImmutableArray() } };
            await Parity(check, relations, "Allied or genuinely unknown relationships cannot qualify as risk-specific opponents");
        }
        var absentOwner = original with { State = original.State with { DecisionOwnerId = null } };
        Reject(check, () => _ = Frame(absentOwner),
            "A present certificate without explicit current ownership is rejected before either policy evaluates it");
        var oldWireAbsentOwner = absentOwner with { Semantics = absentOwner.Semantics with {
            Options = absentOwner.Semantics.Options.Select(option => option with { RememberedMillLifeLoss = null }).ToImmutableArray() } };
        await Parity(check, oldWireAbsentOwner, "Legacy input without a certificate or explicit owner preserves baseline fallback");

        foreach (long seed in new[] { long.MinValue, 17L, 9007199254740993L, long.MaxValue })
        foreach (int work in new[] { 1, 5, 6, 16, 4096 }) {
            var oldWire = original with { Seed = seed, RemainingWork = work, Semantics = original.Semantics with {
                Options = original.Semantics.Options.Select(option => option with { RememberedMillLifeLoss = null }).ToImmutableArray() } };
            var old = await Evaluate(oldWire, false); var current = await Evaluate(oldWire);
            check(old.Status == current.Status && Equivalent(old.Proposal, current.Proposal)
                    && old.Evidence?.RemainingWork == current.Evidence?.RemainingWork,
                $"Absent old-wire certificates preserve exact fallback/status/work at seed{seed}, work{work}");
        }
        foreach (int work in new[] { 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 16, 24, 32 }) {
            var limited = original with { RemainingWork = work };
            var result = await Evaluate(limited);
            check(result.Evidence is { RemainingWork: >= 0 } && result.Evidence.RemainingWork <= work
                    && ((result.Status is AiSeatStatus.Unavailable or AiSeatStatus.BudgetExceeded
                            && result.Evidence.Failure?.Kind == AiFailureKind.WorkLimit && result.Proposal == null)
                        || result.Status == AiSeatStatus.Ready && result.Proposal != null && Frame(limited).Allows(result.Proposal)),
                $"Risk qualification respects its shared {work}-unit allowance without an unvalidated or illegal answer");
        }

        var payload = (OptionsPayload)original.State.Decision!.Payload;
        var renewed = original with { State = original.State with { Revision = 2, BoardRevision = 2,
            Decision = original.State.Decision with { Id = "renewed-risk-choice", Payload = payload with {
                Selection = new(payload.Selection.Choices.Select(choice => choice with { Index = choice.Index + 100 }).ToImmutableArray(), new(1, 1)) } } },
            Semantics = original.Semantics with { Options = original.Semantics.Options.Select(option => option with { Index = option.Index + 100 }).ToImmutableArray() } };
        var renewedResult = await Evaluate(renewed);
        check(Selected(renewedResult, 117) && !Frame(renewed).Allows(candidate.Proposal!) && Frame(renewed).Allows(renewedResult.Proposal!),
            "Renewed publications use their actual current indices and reject the earlier correct-looking command");

        // Exact-input repeatability only: this constructs two copies of one
        // recorded request, not two hidden worlds. Producer privacy and actual
        // different-future continuations require separate provider evidence.
        foreach (var recorded in new[] { original, Read("high-life-40-4") }) {
            byte[] firstInput = JsonSerializer.SerializeToUtf8Bytes(recorded, Json);
            byte[] secondInput = JsonSerializer.SerializeToUtf8Bytes(recorded, Json);
            var first = JsonSerializer.Deserialize<AiDecisionReplayCase>(firstInput, Json)!;
            var second = JsonSerializer.Deserialize<AiDecisionReplayCase>(secondInput, Json)!;
            check(firstInput.SequenceEqual(secondInput)
                    && Equivalent((await Evaluate(first)).Proposal, (await Evaluate(second)).Proposal),
                "Copies of exact identical replay bytes/history/seed produce the same current command");
        }
    }

    private static AiDecisionReplayCase Read(string name) => JsonSerializer.Deserialize<AiDecisionReplayCase>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "PunisherChoiceAuthored", name + ".json")), Json)!;

    private static AiDecisionReplayCase Exposure(AiDecisionReplayCase original, int life, int count, int library = 50)
    {
        Card Counter(Card card) => card with { Counters = card.Counters.SetItem("Influence", count) };
        var source = original.State.Decision!.Context.Source!;
        return original with { State = original.State with {
            Players = original.State.Players.Select(player => player.Id == 1 ? player with { Life = life }
                : player with { Zones = player.Zones.Select(zone => zone.Name == "Library" ? zone with { Count = library }
                    : zone.Name == "Battlefield" ? zone with { Cards = zone.Cards.Select(Counter).ToImmutableArray() } : zone).ToImmutableArray() }).ToImmutableArray(),
            Decision = original.State.Decision with { Context = original.State.Decision.Context with {
                Source = source with { Card = Counter(source.Card!) } } } },
            Semantics = original.Semantics with { Options = original.Semantics.Options.Select(option => option.RememberedMillLifeLoss is not { } fact
                ? option : option with { RememberedMillLifeLoss = fact with { ObservedCount = count } }).ToImmutableArray() } };
    }

    private static async Task Parity(Action<bool, string> check, AiDecisionReplayCase recorded, string description)
        => check(Equivalent((await Evaluate(recorded, false)).Proposal, (await Evaluate(recorded)).Proposal), description);
    private static Task<AiDecisionReplayResult> Evaluate(AiDecisionReplayCase recorded, bool candidate = true)
        => AiDecisionReplay.EvaluateAsync(recorded, candidate ? Candidate() : new CasualCommanderPolicy());
    private static AiDecisionFrame Frame(AiDecisionReplayCase recorded) => new(recorded.State, domain: recorded.Domain, semantics: recorded.Semantics);
    private static bool Selected(AiDecisionReplayResult result, int index) => result.Status == AiSeatStatus.Ready
        && result.Proposal is ChooseOptions { Choices: [var actual] } && actual == index;
    private static bool Equivalent(DecisionCommand? a, DecisionCommand? b) => a?.GetType() == b?.GetType()
        && (a == null || JsonSerializer.Serialize(a, a.GetType()) == JsonSerializer.Serialize(b, b!.GetType()));
    private static void Reject(Action<bool, string> check, Action action, string description)
    {
        bool rejected = false;
        try { action(); } catch (ArgumentException) { rejected = true; }
        check(rejected, description);
    }
}
