using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mana.Contracts;

namespace Mana.Table;

[JsonConverter(typeof(JsonStringEnumConverter<MixedGameParticipantRole>))]
internal enum MixedGameParticipantRole { Host, Guest, Ai }
internal sealed record MixedGameParticipantEvidence(int SlotIndex, int PlayerId,
    MixedGameParticipantRole Role, int ProviderAcceptances);
[JsonConverter(typeof(JsonStringEnumConverter<MixedGameQualificationStatus>))]
internal enum MixedGameQualificationStatus { Passed, Failed, Inconclusive }
internal sealed record MixedGameQualificationAssessment(MixedGameQualificationStatus Status,
    bool TypedNaturalGameCompleted, ImmutableArray<string> FailureReasons, ImmutableArray<string> MissingEvidence)
{
    public string Audience => "host-private";
    public string Scope => "Mechanical evidence for this mixed game and its bound decks. Human UX review remains pending; positive causal witnesses do not establish all interactions or complete event history.";
}

// Admission of existing evidence, not another rules evaluator. Missing telemetry
// cannot be replaced with global board witnesses or a natural-finish boolean.
internal static class MixedGameQualification
{
    internal static MixedGameQualificationAssessment Assess(MixedGameResult result,
        ImmutableArray<AutomatedSeatObservation> finalOwned)
    {
        var failed = new SortedSet<string>(StringComparer.Ordinal);
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        if (result.Failure != null || result.StopReason == "mechanical-failure") failed.Add("mechanical-failure");
        if (result.Evidence.DiagnosticErrors.IsDefault || !result.Evidence.DiagnosticErrors.IsEmpty)
            failed.Add("diagnostic-evidence-failure");
        if (result.StopReason != "natural-outcome") missing.Add("natural-outcome-not-reached");
        if (!result.NaturalOutcomeVerified) missing.Add("natural-outcome-not-certified");
        if (result.Outcome == null) missing.Add("typed-result-outcome-missing");
        if (result.PublicComparisons < 0 || result.FourAudienceCohorts < 0) failed.Add("invalid-public-cohort-counts");
        if (result.PublicComparisons == 0) missing.Add("public-comparisons-missing");
        if (result.FourAudienceCohorts == 0) missing.Add("four-audience-cohort-missing");

        var participants = result.Participants.IsDefault ? [] : result.Participants;
        if (participants.Length != 4) missing.Add("four-physical-roles-missing");
        bool roles = participants.Length == 4 && participants.All(row => row != null && row.PlayerId >= 0
            && row.SlotIndex is >= 0 and < 4 && Enum.IsDefined(row.Role) && row.ProviderAcceptances >= 0)
            && participants.Select(row => row.PlayerId).Distinct().Count() == 4
            && participants.Select(row => row.SlotIndex).Distinct().Count() == 4
            && participants.Count(row => row.Role == MixedGameParticipantRole.Host) == 1
            && participants.Count(row => row.Role == MixedGameParticipantRole.Guest) == 1
            && participants.Count(row => row.Role == MixedGameParticipantRole.Ai) == 2;
        if (participants.Length == 4 && !roles || participants.Length > 4) failed.Add("invalid-physical-role-mapping");
        if (roles) {
            var ids = participants.Select(row => row.PlayerId).ToHashSet();
            if (!result.DevelopedSeats.IsDefault && result.DevelopedSeats.Any(id => !ids.Contains(id)))
                failed.Add("foreign-developed-player");
            foreach (var participant in participants) {
                if (participant.ProviderAcceptances == 0) missing.Add($"slot-{participant.SlotIndex}/accepted-action-missing");
                if (result.DevelopedSeats.IsDefault || !result.DevelopedSeats.Contains(participant.PlayerId))
                    missing.Add($"slot-{participant.SlotIndex}/development-missing");
                int? aggregate = participant.Role switch {
                    MixedGameParticipantRole.Host => result.HostProviderAcceptances,
                    MixedGameParticipantRole.Guest => result.GuestProviderAcceptances, _ => null
                };
                if (aggregate != null && aggregate != participant.ProviderAcceptances) failed.Add("player-receipt-count-disagrees");
            }
        }

        var frames = finalOwned.IsDefault ? [] : finalOwned;
        if (frames.Length != 4) missing.Add("four-final-owned-observations-missing");
        bool owned = frames.Length == 4 && frames.All(row => row != null && row.SeatId >= 0
            && row.State.ViewerId == row.SeatId) && frames.Select(row => row.SeatId).Distinct().Count() == 4;
        if (frames.Length == 4 && !owned || frames.Length > 4) failed.Add("invalid-final-owned-audiences");
        if (owned && roles && !participants.Select(row => row.PlayerId).ToHashSet().SetEquals(frames.Select(row => row.SeatId)))
            failed.Add("final-audiences-disagree-with-roles");
        bool terminal = owned;
        if (owned && frames.Select(row => row.State.Id).Distinct(StringComparer.Ordinal).Count() != 1)
            failed.Add("final-owned-games-disagree");
        bool cohort = owned && frames.All(row => row.State.Id == frames[0].State.Id
            && row.State.BoardRevision == frames[0].State.BoardRevision);
        foreach (var row in frames) {
            if (row == null || row.State.ViewerId != row.SeatId) { terminal = false; continue; }
            var state = row.State;
            if (state.Error != null || state.Status == "error") failed.Add("rules-observation-failure");
            try {
                if (!AiCampaignVisibility.PrivateWindowsAreDisclosed(state, row.Semantics, row.Domain))
                    failed.Add("unauthorized-private-window");
                if (state.Outcome != null) MatchOutcomeFacts.Validate(state);
                if (owned && (!state.Players.Select(player => player.Id).ToHashSet().SetEquals(frames.Select(item => item.SeatId))
                    || state.Players.Length != 4)) failed.Add("final-player-mapping-disagrees");
                if (cohort && ForgePublicProjectionFacts.Capture(state) != ForgePublicProjectionFacts.Capture(frames[0].State)) {
                    failed.Add("current-public-cohort-disagrees"); cohort = false;
                }
            } catch (Exception error) when (error is InvalidOperationException or InvalidDataException or ArgumentException) {
                failed.Add("invalid-final-observation"); terminal = false;
            }
            if (state.Status != "finished" || state.Outcome == null || state.Decision != null) terminal = false;
        }
        bool typedNatural = terminal && cohort;
        if (!typedNatural) missing.Add("shared-typed-terminal-cohort-missing");
        if (owned && result.Outcome is { } outcome) {
            try {
                MatchOutcomeFacts.Validate(outcome, frames[0].State.Id,
                    frames[0].State.Players.ToImmutableDictionary(player => player.Id, player => player.TeamId));
                if (frames[0].State.Outcome is { } final && !SameOutcome(outcome, final))
                    failed.Add("result-outcome-disagrees");
            } catch (Exception error) when (error is InvalidOperationException or ArgumentException) {
                failed.Add("invalid-result-outcome");
            }
        }

        var decks = result.Decks.IsDefault ? [] : result.Decks;
        bool boundDecks = decks.Length == 4;
        if (!boundDecks) missing.Add("four-bound-decks-missing");
        if (boundDecks) {
            if (decks.Select(deck => deck.CandidateId).Distinct(StringComparer.Ordinal).Count() != 4)
                failed.Add("four-distinct-decks-missing");
            for (int slot = 0; slot < decks.Length; slot++) {
                var deck = decks[slot];
                var manifest = AiCampaignDecks.All.SingleOrDefault(candidate => candidate.Id == deck.CandidateId);
                if (manifest == null || deck.SourceSha256 != manifest.Hash || deck.Family != manifest.Family
                    || !AiCampaignQualification.Hash(deck.SemanticVersionId) || string.IsNullOrWhiteSpace(deck.SavedDeckId)
                    || deck.Revision < 0 || string.IsNullOrWhiteSpace(deck.Handle)
                    || deck.RequiredWitnessIds.IsDefault || !deck.RequiredWitnessIds.SequenceEqual(manifest.RequiredWitnessIds)
                    || deck.IntendedMechanics.IsDefault || !deck.IntendedMechanics.SequenceEqual(manifest.IntendedMechanics))
                    failed.Add($"slot-{slot}/invalid-deck-binding");
            }
        }
        var causal = result.CausalDecks.IsDefault ? [] : result.CausalDecks;
        if (causal.IsEmpty) missing.Add("per-deck-causal-collector-missing");
        if (causal.Length > 4 || causal.Select(row => row.SlotIndex).Distinct().Count() != causal.Length)
            failed.Add("invalid-causal-slot-mapping");
        if (boundDecks && roles) {
            for (int slot = 0; slot < 4; slot++) {
                var participant = participants.Single(row => row.SlotIndex == slot);
                var rows = causal.Where(row => row.SlotIndex == slot).ToArray();
                var qualification = rows.Length == 1 ? rows[0] : null;
                if (qualification == null) { missing.Add($"slot-{slot}/causal-qualification-missing"); continue; }
                if (!SameBinding(qualification.Deck, decks[slot]) || qualification.PlayerId != participant.PlayerId
                    || !owned || qualification.GameId != frames[0].State.Id
                    || qualification.TeamId != frames[0].State.Players.FirstOrDefault(player => player.Id == participant.PlayerId)?.TeamId
                    || string.IsNullOrWhiteSpace(qualification.ConfigurationId) || !AiCampaignQualification.Hash(qualification.ConfigurationSha256))
                    failed.Add($"slot-{slot}/causal-identity-disagrees");
                if (qualification.MechanicalIntegrity == "failed" || !qualification.IntegrityFailures.IsDefaultOrEmpty)
                    failed.Add($"slot-{slot}/causal-integrity-failure");
                else if (qualification.MechanicalIntegrity != "no-observed-integrity-failure")
                    missing.Add($"slot-{slot}/causal-integrity-unassessed");
                if (qualification.SampledNaturalGame != "observed-natural-outcome")
                    missing.Add($"slot-{slot}/causal-natural-game-unobserved");
                var witnesses = qualification.RequiredWitnesses.IsDefault ? [] : qualification.RequiredWitnesses;
                if (witnesses.Select(witness => witness.Id).Distinct(StringComparer.Ordinal).Count() != witnesses.Length)
                    failed.Add($"slot-{slot}/duplicate-causal-witness");
                foreach (string id in decks[slot].RequiredWitnessIds) {
                    var matches = witnesses.Where(witness => witness.Id == id).ToArray();
                    var witness = matches.Length == 1 ? matches[0] : null;
                    if (witness?.Status == "failed") failed.Add($"slot-{slot}/{id}/failed");
                    if (witness is not { Status: "observed", Evidence.IsDefaultOrEmpty: false }) {
                        missing.Add($"slot-{slot}/{id}/unobserved"); continue;
                    }
                    foreach (var proof in witness.Evidence) {
                        if (proof.GameId != qualification.GameId || proof.SlotIndex != slot || proof.PlayerId != participant.PlayerId
                            || !proof.Accepted || string.IsNullOrWhiteSpace(proof.SourceCardId) || string.IsNullOrWhiteSpace(proof.DecisionId)
                            || string.IsNullOrWhiteSpace(proof.CommandType) || string.IsNullOrWhiteSpace(proof.ExecutionId)
                            || string.IsNullOrWhiteSpace(proof.Consequence) || proof.BeforeRevision < 0 || proof.BeforeBoardRevision < 0
                            || proof.AfterBoardRevision <= proof.BeforeBoardRevision
                            || owned && proof.AfterBoardRevision > frames[0].State.BoardRevision
                            || proof.ActivityIds.IsDefaultOrEmpty && proof.HistorySequences.IsDefaultOrEmpty
                            || !proof.ActivityIds.IsDefaultOrEmpty && proof.ActivityIds.Any(value => value <= 0)
                            || !proof.HistorySequences.IsDefaultOrEmpty && proof.HistorySequences.Any(value => value <= 0))
                            failed.Add($"slot-{slot}/{id}/invalid-causal-evidence");
                    }
                }
            }
            if (causal.Select(row => (row.ConfigurationId, row.ConfigurationSha256)).Distinct().Count() > 1)
                failed.Add("causal-configurations-disagree");
        }
        return new(failed.Count > 0 ? MixedGameQualificationStatus.Failed : missing.Count > 0
            ? MixedGameQualificationStatus.Inconclusive : MixedGameQualificationStatus.Passed,
            typedNatural, failed.ToImmutableArray(), missing.ToImmutableArray());
    }

    private static bool SameOutcome(MatchOutcome left, MatchOutcome right) => left.GameId == right.GameId
        && left.Kind == right.Kind && left.RuleReason == right.RuleReason
        && left.WinningPlayerIds.Order().SequenceEqual(right.WinningPlayerIds.Order())
        && left.WinningTeamIds.Order().SequenceEqual(right.WinningTeamIds.Order());

    private static bool SameBinding(AiCampaignDeckBinding left, AiCampaignDeckBinding right) => left.CandidateId == right.CandidateId
        && left.SourceSha256 == right.SourceSha256 && left.SemanticVersionId == right.SemanticVersionId
        && left.SavedDeckId == right.SavedDeckId && left.Revision == right.Revision && left.Handle == right.Handle
        && left.Family == right.Family && !left.IntendedMechanics.IsDefault && !right.IntendedMechanics.IsDefault
        && !left.RequiredWitnessIds.IsDefault && !right.RequiredWitnessIds.IsDefault
        && left.IntendedMechanics.SequenceEqual(right.IntendedMechanics)
        && left.RequiredWitnessIds.SequenceEqual(right.RequiredWitnessIds);
}

internal static class MixedGameQualificationChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var decks = AiCampaignDecks.All.Select((deck, index) => new AiCampaignDeckBinding(deck.Id, deck.Hash,
            new((char)('a' + index), 64), "saved-" + index, 1, "deck:saved-" + index + ":bytes", deck.Family,
            deck.IntendedMechanics, deck.RequiredWitnessIds)).ToImmutableArray();
        var participants = Enumerable.Range(0, 4).Select(index => new MixedGameParticipantEvidence(index, 10 + index,
            index == 0 ? MixedGameParticipantRole.Host : index == 1 ? MixedGameParticipantRole.Guest : MixedGameParticipantRole.Ai, 1)).ToImmutableArray();
        MatchOutcome Outcome() => new("mixed", MatchOutcomeKind.Win, [10], [10], "rules-win");
        var players = Enumerable.Range(10, 4).Select(id => new Player(id, "Player", 40, false, id != 10,
            ImmutableDictionary<string, int>.Empty, [new("Battlefield", 1, [new() { Name = "Forest", VisualId = "land-" + id, Type = "Land" }], null),
                new("Hand", 1, [], null)]) {
                TeamId = id }).ToImmutableArray();
        var final = Enumerable.Range(10, 4).Select(id => new AutomatedSeatObservation(id,
            new("mixed", "finished", null, "Win", id, 15, "Main", "MAIN1", 10, players, [], null, null) {
                Revision = 30, BoardRevision = 30, Outcome = Outcome() }, DecisionActionDomain.None, new())).ToImmutableArray();
        var evidence = new AiCampaignEvidence([], [], ["every-global-board-witness"], [], [], [], 100,
            ImmutableDictionary<string, int>.Empty, ImmutableDictionary<string, int>.Empty, [], [], [], [], [], [], [], [], "test evidence");
        var causal = decks.Select((deck, slot) => new AiCampaignDeckQualification(deck, "configuration", new('f', 64), slot,
            10 + slot, 10 + slot, "mixed", "no-observed-integrity-failure", "observed-natural-outcome", "pending",
            deck.RequiredWitnessIds.Select(id => new AiCampaignDeckWitness(id, "observed", "Typed recorder control",
                [new("mixed", slot, 10 + slot, "source-" + slot, "Source", "decision", "UseOfferedAction", true,
                    10, 10, 11, "execution-" + slot, [1], "Owned committed consequence")])).ToImmutableArray())).ToImmutableArray();
        var complete = new MixedGameResult(0, "natural-outcome", null, true, 15, 40, 1000,
            [10, 11, 12, 13], 3, 1, 1, 1, Outcome(), evidence, decks) { Participants = participants, CausalDecks = causal };
        MixedGameQualificationAssessment Assess(MixedGameResult result, ImmutableArray<AutomatedSeatObservation>? frames = null) =>
            MixedGameQualification.Assess(result, frames ?? final);
        check(Assess(complete) is { Status: MixedGameQualificationStatus.Passed, TypedNaturalGameCompleted: true }
            && Assess(complete).Scope.Contains("Human UX review remains pending", StringComparison.Ordinal),
            "Mixed qualification can accept complete typed mechanical evidence without treating it as human UX approval");
        check(JsonSerializer.SerializeToElement(Assess(complete)).GetProperty("Status").GetString() == "Passed",
            "Mixed-game qualification artifacts state their assessment explicitly instead of using an ambiguous enum number");
        foreach (string cutoff in new[] { "turn-budget", "time-budget", "not-started" })
            check(Assess(complete with { StopReason = cutoff }) is { Status: MixedGameQualificationStatus.Inconclusive, TypedNaturalGameCompleted: true },
                "A mixed-game cutoff cannot qualify even when a later retained typed outcome proves the game completed");
        check(Assess(complete with { Failure = "retained engine fault" }).Status == MixedGameQualificationStatus.Failed,
            "A raw mixed-game failure cannot be hidden by natural completion or complete mechanic witnesses");
        check(Assess(complete with { Participants = [], CausalDecks = [] }).Status == MixedGameQualificationStatus.Inconclusive,
            "Natural completion and global board witnesses cannot substitute for physical roles or per-deck causal evidence");
        check(Assess(complete with { CausalDecks = [] }).MissingEvidence.Contains("per-deck-causal-collector-missing"),
            "An unwired mixed-game causal collector remains explicitly pending");
        check(Assess(complete with { Outcome = null }, final.Select(row => row with { State = row.State with { Outcome = null } }).ToImmutableArray())
            is { Status: MixedGameQualificationStatus.Inconclusive, TypedNaturalGameCompleted: false },
            "Finished status and a claimed natural-outcome boolean cannot replace the typed rules outcome");
        foreach (int slot in Enumerable.Range(0, 4)) {
            var noAction = complete with { Participants = participants.SetItem(slot, participants[slot] with { ProviderAcceptances = 0 }),
                HostProviderAcceptances = slot == 0 ? 0 : 1, GuestProviderAcceptances = slot == 1 ? 0 : 1 };
            check(Assess(noAction).Status == MixedGameQualificationStatus.Inconclusive,
                "Each physical mixed-game role must have its own accepted action; aggregate acceptance cannot fill a missing role");
            check(Assess(complete with { DevelopedSeats = complete.DevelopedSeats.Remove(10 + slot) }).Status == MixedGameQualificationStatus.Inconclusive,
                "Each physical mixed-game role needs observed development; another player's battlefield cannot supply it");
            check(Assess(complete with { CausalDecks = causal.RemoveAt(slot) }).Status == MixedGameQualificationStatus.Inconclusive,
                "Each bound mixed-game deck needs its own causal mechanic report");
        }
        check(Assess(complete with { Participants = participants.SetItem(3, participants[3] with { PlayerId = 12 }) }).Status == MixedGameQualificationStatus.Failed,
            "Duplicating an audience cannot manufacture the fourth physical mixed-game player");
        check(Assess(complete with { PublicComparisons = 0, FourAudienceCohorts = 0 }).Status == MixedGameQualificationStatus.Inconclusive,
            "A natural mixed-game outcome cannot replace missing cross-audience public comparisons");
        check(Assess(complete, final.SetItem(1, final[1] with { State = final[1].State with { BoardRevision = 29 } })).Status
            == MixedGameQualificationStatus.Inconclusive,
            "Earlier complete public cohorts cannot replace one current terminal cohort across all four owned audiences");
        check(Assess(complete, final.SetItem(1, final[1] with { SeatId = 10 })).Status == MixedGameQualificationStatus.Failed,
            "An owned row for a different private audience fails mixed-game qualification");
        var leaked = final[0].State.Players.SetItem(1, players[1] with { Zones = players[1].Zones.Select(zone => zone.Name == "Hand"
            ? zone with { Cards = [new() { Name = "PRIVATE HAND", VisualId = "secret" }] } : zone).ToImmutableArray() });
        var leakedFrame = final[0] with { State = final[0].State with { Players = leaked } };
        check(ForgePublicProjectionFacts.Capture(leakedFrame.State) == ForgePublicProjectionFacts.Capture(final[0].State)
            && Assess(complete, final.SetItem(0, leakedFrame)).Status == MixedGameQualificationStatus.Failed,
            "Matching public facts cannot hide an unauthorized opponent hand disclosure in mixed-game evidence");
        var requirement = causal[0].RequiredWitnesses[0];
        foreach (string pending in new[] { "not-instrumented", "not-observed", "inconclusive" })
            check(Assess(complete with { CausalDecks = causal.SetItem(0, causal[0] with {
                RequiredWitnesses = causal[0].RequiredWitnesses.SetItem(0, requirement with { Status = pending, Evidence = [] }) }) }).Status
                == MixedGameQualificationStatus.Inconclusive,
                "An unobserved defining mechanic cannot qualify a mixed-game deck regardless of natural outcome");
        check(Assess(complete with { CausalDecks = causal.SetItem(0, causal[0] with {
            RequiredWitnesses = causal[0].RequiredWitnesses.SetItem(0, requirement with { Evidence = [] }) }) }).Status == MixedGameQualificationStatus.Inconclusive,
            "An observed label without accepted causal occurrences cannot qualify a defining mechanic");
        check(Assess(complete with { CausalDecks = causal.SetItem(0, causal[0] with {
            Deck = causal[0].Deck with { SemanticVersionId = new('e', 64) } }) }).Status == MixedGameQualificationStatus.Failed,
            "Causal credit from another semantic deck version cannot transfer to the current mixed-game deck");
        check(Assess(complete with { Decks = decks.SetItem(0, decks[0] with { RequiredWitnessIds = [] }),
            CausalDecks = causal.SetItem(0, causal[0] with { Deck = decks[0] with { RequiredWitnessIds = [] }, RequiredWitnesses = [] }) }).Status
            == MixedGameQualificationStatus.Failed,
            "Deleting defining requirements from both a deck and its report cannot make missing mixed-game mechanics pass");
        check(Assess(complete with { CausalDecks = causal.SetItem(0, causal[0] with { MechanicalIntegrity = "failed" }) }).Status == MixedGameQualificationStatus.Failed,
            "A causal integrity failure cannot be outweighed by otherwise complete mixed-game evidence");
        check(Assess(complete with { CausalDecks = causal.SetItem(0, causal[0] with {
            RequiredWitnesses = causal[0].RequiredWitnesses.SetItem(0, requirement with {
                Evidence = [requirement.Evidence[0] with { Accepted = false }] }) }) }).Status == MixedGameQualificationStatus.Failed,
            "A rejected command cannot supply accepted causal mechanic evidence for mixed-game qualification");
        check(Assess(complete with { CausalDecks = causal.SetItem(0, causal[0] with {
            RequiredWitnesses = causal[0].RequiredWitnesses.SetItem(0, requirement with {
                Evidence = [requirement.Evidence[0] with { GameId = "other-game" }] }) }) }).Status == MixedGameQualificationStatus.Failed,
            "A positive causal occurrence from another game cannot qualify the current mixed-game deck");
    }
}
