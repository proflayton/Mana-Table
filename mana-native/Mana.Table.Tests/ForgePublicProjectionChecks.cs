using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.Contracts;

namespace Mana.Table;

// Forge publishes one shared public occurrence generation to every audience.
// This is an adapter publication invariant, not a requirement that unrelated
// providers share card IDs. Historical stack portraits never establish current
// occurrences by matching printed names; stack occurrences compare separately.
internal static class ForgePublicProjectionChecks
{
    internal const string RejectedBatchFile = "private-rejected-public-projection.json";
    private static readonly object FailureEvidenceKey = new();
    internal sealed record FailureScope(int SeatId, string GameId, int ViewerId, long Revision,
        long BoardRevision, string? DecisionId);
    internal sealed record FailureEvidence(bool Captured, bool EarlierFailureRetained, string? CaptureError,
        ImmutableArray<FailureScope> Scopes);

    internal static int ValidateBatch(ImmutableArray<AutomatedSeatObservation> observations, string profile)
    {
        try { return ValidateBatchCore(observations, profile); }
        catch (InvalidDataException error) {
            PreserveRejectedBatch(observations, profile, error);
            throw;
        }
    }

    private static int ValidateBatchCore(ImmutableArray<AutomatedSeatObservation> observations, string profile)
    {
        foreach (var item in observations)
            foreach (var player in item.State.Players)
                foreach (var card in player.Zone("Battlefield").Cards) {
                    if (!string.IsNullOrEmpty(card.VisualId) || !string.IsNullOrEmpty(card.CombatId)) continue;
                    if ((item.State.Status is "playing" or "resolving") && !player.Eliminated
                        || !string.IsNullOrEmpty(card.Key) || card.Selectable || card.Highlighted)
                        throw new InvalidDataException("Forge published a current or actionable battlefield face without an occurrence identity.");
                }
        int comparisons = 0;
        foreach (var group in observations.GroupBy(item => (item.State.Id, item.State.BoardRevision))) {
            var first = group.First().State;
            var correlations = ForgePublicProjectionFacts.CaptureExecutionCorrelations(first);
            if (!correlations.Valid) throw new InvalidDataException("Forge published invalid local stack execution references.");
            foreach (var item in group.Skip(1)) {
                if (!AiWholeSeatEngineCheck.ComparePublicProjection(first, item.State, profile))
                    throw new InvalidDataException("Forge published inconsistent same-revision public occurrences across its automated audiences.");
                correlations = ForgePublicProjectionFacts.MergeExecutionCorrelations(correlations,
                    ForgePublicProjectionFacts.CaptureExecutionCorrelations(item.State))
                    ?? throw new InvalidDataException("Forge audiences disclosed contradictory references for the same stack occurrence.");
                comparisons++;
            }
        }
        return comparisons;
    }

    internal static FailureEvidence? FindFailureEvidence(Exception error)
    {
        for (Exception? cause = error; cause != null; cause = cause.InnerException)
            if (cause.Data[FailureEvidenceKey] is FailureEvidence evidence) return evidence;
        return null;
    }

    private static void PreserveRejectedBatch(ImmutableArray<AutomatedSeatObservation> observations,
        string profile, InvalidDataException error)
    {
        if (FindFailureEvidence(error) != null) return;
        bool captured = false, earlier = false;
        string? secondary = null, pending = null;
        try {
            Directory.CreateDirectory(profile);
            string target = Path.Combine(profile, RejectedBatchFile);
            if (File.Exists(target)) earlier = true;
            else {
                pending = target + ".pending-" + Guid.NewGuid().ToString("N");
                using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    JsonSerializer.Serialize(stream, new {
                        formatVersion = 1, audience = "host-private", capturePhase = "public-invariant-validation", authority = "none",
                        role = "Exact supplied immutable batch rejected after adapter decode; not accepted state, a command or policy replay.",
                        audienceCount = observations.Length, audiencePlayerIds = observations.Select(item => item.SeatId),
                        exception = error.ToString(), batch = observations
                    });
                try { File.Move(pending, target); captured = true; }
                catch (IOException) when (File.Exists(target)) { earlier = true; }
            }
        } catch (Exception captureError) { secondary = captureError.ToString(); }
        finally {
            if (pending != null) try { File.Delete(pending); }
                catch (Exception cleanupError) { secondary = (secondary == null ? "" : secondary + "\n") + cleanupError; }
        }
        // Preserve the original exception object and its stack. A failed
        // private export is evidence about capture, never a replacement fault.
        error.Data[FailureEvidenceKey] = new FailureEvidence(captured, earlier, secondary,
            observations.Select(item => new FailureScope(item.SeatId, item.State.Id, item.State.ViewerId,
                item.State.Revision, item.State.BoardRevision, item.State.Decision?.Id)).ToImmutableArray());
    }

    internal static void Run(string profile, Action<bool, string> check)
    {
        ForgePublicProjectionFactChecks.Run(profile, check);
        string directory = Path.Combine(profile, "expected-public-projection-rejections");
        Directory.CreateDirectory(directory);
        AutomatedSeatObservation Audience(int seat, long board = 20, int playerCount = 2) {
            var players = Enumerable.Range(0, playerCount).Select(player => new Player(player, "Player " + player, 40, false, false,
                ImmutableDictionary<string, int>.Empty, [
                    new("Battlefield", 1, [new Card { Name = "Same printed creature", VisualId = "public-" + player,
                        CombatId = "public-" + player, Type = "Creature", Power = 2, Toughness = 2 }], null),
                    new("Hand", 1, player == seat ? [new Card { Name = "Private face " + seat,
                        VisualId = "private-" + seat }] : [], null)
                ])).ToImmutableArray();
            return new(seat, new("shared-publication", "playing", null, null, seat, 1, "Main", "MAIN1", 0,
                players, [], null, null) { BoardRevision = board, Revision = seat + 1 }, DecisionActionDomain.None, new());
        }
        var first = Audience(0);
        var other = Audience(1);
        check(ValidateBatch([first, other], directory) == 1,
            "Forge same-revision public occurrence comparison permits each audience's different private hand and decision revision");
        bool Diverges(AutomatedSeatObservation left, AutomatedSeatObservation right) {
            try { ValidateBatch([left, right], directory); return false; }
            catch (InvalidDataException) { return true; }
        }
        var firstTeams = first with { State = first.State with {
            Players = first.State.Players.Select(player => player with { TeamId = player.Id + 10 }).ToImmutableArray() } };
        var otherTeams = other with { State = other.State with {
            Players = other.State.Players.Select(player => player with { TeamId = player.Id + 10 }).ToImmutableArray() } };
        check(ValidateBatch([firstTeams, otherTeams], directory) == 1
                && Diverges(firstTeams, otherTeams with { State = otherTeams.State with {
                    Players = otherTeams.State.Players.Select(player => player with { TeamId = 10 }).ToImmutableArray() } }),
            "Same-publication comparisons reject divergent team relationships across audiences");
        var terminalFirst = firstTeams with { State = firstTeams.State with { Status = "finished",
            Outcome = new(firstTeams.State.Id, MatchOutcomeKind.Win, [0], [10], "AllOpposingTeamsLost") } };
        var terminalOther = otherTeams with { State = otherTeams.State with { Status = "finished", Outcome = terminalFirst.State.Outcome } };
        check(ValidateBatch([terminalFirst, terminalOther], directory) == 1
                && Diverges(terminalFirst, terminalOther with { State = terminalOther.State with {
                    Outcome = new(terminalOther.State.Id, MatchOutcomeKind.Win, [1], [11], "AllOpposingTeamsLost") } })
                && Diverges(terminalFirst, terminalOther with { State = terminalOther.State with { Outcome = null } }),
            "Every audience must agree on the same typed terminal outcome, including its presence");
        var changedCard = other.State.Players[0].Zone("Battlefield").Cards[0] with {
            VisualId = "rotated-public-id", CombatId = "rotated-public-id"
        };
        var changedPlayer = other.State.Players[0] with { Zones = [new("Battlefield", 1, [changedCard], null),
            other.State.Players[0].Zone("Hand")] };
        var inconsistent = other with { State = other.State with { Players = [changedPlayer, other.State.Players[1]] } };
        bool rejected = false;
        try { ValidateBatch([first, inconsistent], directory); } catch (InvalidDataException) { rejected = true; }
        check(rejected,
            "Forge rejects changed public occurrence IDs before policy evaluation even when printed names and all public statistics match");
        check(ValidateBatch([first, inconsistent with { State = inconsistent.State with { BoardRevision = 21 } }], directory) == 0
                && ValidateBatch([first, inconsistent with { State = inconsistent.State with { Id = "another-game" } }], directory) == 0,
            "Public occurrence comparison never combines different game or board revisions");
        var historical = other with { State = other.State with { Stack = [new("Same printed creature", "Historical ability",
            StackActionKind.TriggeredAbility, new Card { Name = "Same printed creature", VisualId = "departed-read-only-portrait" }) {
            Id = "historical-trigger"
        }] } };
        var historicalFirst = first with { State = first.State with { Stack = historical.State.Stack } };
        check(ValidateBatch([historicalFirst, historical], directory) == 1 && Diverges(first, historical),
            "Historical stack portraits are not joined to current public permanents by matching printed names");
        AutomatedSeatObservation MissingIdentity(AutomatedSeatObservation observation, string status, bool eliminated, bool selectable = false) {
            var owner = observation.State.Players[0];
            var face = owner.Zone("Battlefield").Cards[0] with { VisualId = null!, CombatId = null!, Selectable = selectable };
            owner = owner with { Eliminated = eliminated, Zones = [new("Battlefield", 1, [face], null), owner.Zone("Hand")] };
            return observation with { State = observation.State with { Status = status,
                Players = observation.State.Players.SetItem(0, owner) } };
        }
        bool MissingRejected(params AutomatedSeatObservation[] observations) {
            try { ValidateBatch(observations.ToImmutableArray(), directory); return false; }
            catch (InvalidDataException) { return true; }
        }
        foreach (string status in new[] { "playing", "resolving" })
            check(MissingRejected(MissingIdentity(first, status, false), MissingIdentity(other, status, false)),
                "Matching empty identities across all Forge audiences cannot certify a current live battlefield occurrence");
        var anonymous = MissingIdentity(first, "playing", false);
        var anonymousOwner = anonymous.State.Players[0];
        anonymousOwner = anonymousOwner with { Zones = [new("Battlefield", 1,
            [anonymousOwner.Zone("Battlefield").Cards[0] with { CombatId = "anonymous-public-occurrence", FaceDown = true }], null),
            anonymousOwner.Zone("Hand")] };
        check(ValidateBatch([anonymous with { State = anonymous.State with { Players = [anonymousOwner, anonymous.State.Players[1]] } }], directory) == 0,
            "A live concealed battlefield face may retain its anonymous combat occurrence without a readable visual identity");
        check(ValidateBatch([MissingIdentity(first, "finished", true), MissingIdentity(other, "finished", true)], directory) == 1
                && ValidateBatch([MissingIdentity(first, "playing", true), MissingIdentity(other, "playing", true)], directory) == 1,
            "Terminal or explicitly retired read-only faces compare without fabricating current identities or throwing on legacy null fields");
        check(MissingRejected(MissingIdentity(first, "finished", true, selectable: true)),
            "An identity-free retired face cannot regain target-selection authority through its display flags");
        FailureCapture(profile, check, Enumerable.Range(0, 4).Select(seat =>
            MissingIdentity(Audience(seat, playerCount: 4), "playing", false)).ToImmutableArray(), [first, other]);
    }

    private static void FailureCapture(string profile, Action<bool, string> check,
        ImmutableArray<AutomatedSeatObservation> rejectedBatch, ImmutableArray<AutomatedSeatObservation> lastValid)
    {
        string directory = Path.Combine(profile, "public-projection-first-failure");
        using var evidence = new AiCampaignDiagnostics(Directory.CreateDirectory(directory).FullName);
        var inbox = evidence.EnableOrderedHandoff();
        inbox.Returned(rejectedBatch);
        InvalidDataException? original = null;
        try { evidence.DrainPending(); } catch (InvalidDataException error) { original = error; }
        var captured = original == null ? null : FindFailureEvidence(original);
        using (var retained = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, RejectedBatchFile))))
            check(original != null && captured is { Captured: true, EarlierFailureRetained: false, CaptureError: null, Scopes.Length: 4 }
                    && retained.RootElement.GetProperty("capturePhase").GetString() == "public-invariant-validation"
                    && retained.RootElement.GetProperty("authority").GetString() == "none"
                    && retained.RootElement.GetProperty("audienceCount").GetInt32() == 4
                    && JsonNode.DeepEquals(JsonNode.Parse(retained.RootElement.GetProperty("batch").GetRawText()),
                        JsonSerializer.SerializeToNode(rejectedBatch)),
                "The first public-invariant rejection retains the exact supplied four-audience immutable batch, including private faces, before rejecting it");
        var originalStack = original!.StackTrace;
        Exception? later = null;
        try { inbox.EnsureActive(); } catch (Exception error) { later = error; }
        check(ReferenceEquals(original, later) && originalStack?.Contains(nameof(ForgePublicProjectionChecks), StringComparison.Ordinal) == true
                && later!.StackTrace?.Contains(originalStack, StringComparison.Ordinal) == true,
            "Later evidence-inbox operations preserve the original invariant exception object and validation stack instead of replacing its origin");
        evidence.PreserveObservations(lastValid);
        evidence.PreserveFailure(original, "ordered-public-validation", 1, lastValid);
        using (var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "private-failure-manifest.json")))) {
            var failure = manifest.RootElement.GetProperty("rejectedPublicProjection");
            check(failure.GetProperty("captured").GetBoolean()
                    && failure.GetProperty("capturePhase").GetString() == "public-invariant-validation"
                    && failure.GetProperty("scopes").GetArrayLength() == 4
                    && !manifest.RootElement.GetProperty("rejectedWireObservation").GetProperty("captured").GetBoolean()
                    && manifest.RootElement.GetProperty("rawObservation").GetProperty("scopes").GetArrayLength() == 2,
                "The failure manifest distinguishes the exact rejected public-invariant batch from adapter-decode errors and the separate last valid audience batch");
        }
        byte[] firstBytes = File.ReadAllBytes(Path.Combine(directory, RejectedBatchFile));
        InvalidDataException? repeated = null;
        try { ValidateBatch(rejectedBatch.RemoveAt(0), directory); } catch (InvalidDataException error) { repeated = error; }
        check(File.ReadAllBytes(Path.Combine(directory, RejectedBatchFile)).SequenceEqual(firstBytes)
                && repeated != null && FindFailureEvidence(repeated) is { Captured: false, EarlierFailureRetained: true },
            "A later failing batch cannot overwrite or relabel the retained first public-invariant failure");
        string twoAudiences = Path.Combine(profile, "public-projection-two-audience-failure");
        try { ValidateBatch(rejectedBatch.Take(2).ToImmutableArray(), twoAudiences); } catch (InvalidDataException) { }
        using (var retained = JsonDocument.Parse(File.ReadAllText(Path.Combine(twoAudiences, RejectedBatchFile))))
            check(retained.RootElement.GetProperty("audienceCount").GetInt32() == 2
                    && retained.RootElement.GetProperty("batch").GetArrayLength() == 2
                    && retained.RootElement.GetProperty("audiencePlayerIds").EnumerateArray().Select(item => item.GetInt32()).SequenceEqual([0, 1]),
                "A rejected two-audience provider batch retains only its supplied participants without synthesizing other table views");
        string blocked = Path.Combine(profile, "public-projection-blocked-capture");
        File.WriteAllText(blocked, "The profile path is deliberately a file.");
        InvalidDataException? uncaptured = null;
        try { ValidateBatch(rejectedBatch, blocked); } catch (InvalidDataException error) { uncaptured = error; }
        check(uncaptured?.Message == original.Message && FindFailureEvidence(uncaptured!) is { Captured: false, CaptureError.Length: > 0 }
                && uncaptured.StackTrace?.Contains(nameof(ForgePublicProjectionChecks), StringComparison.Ordinal) == true,
            "A failed private batch export records its secondary capture error while preserving the original invariant rejection");
    }
}
