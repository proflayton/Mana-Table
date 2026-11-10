using System.Collections.Immutable;
using System.IO.Compression;
using System.Text.Json;
using Mana.AI;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

internal sealed record AiCampaignFinding(string Code, int Seat, int Turn, string DecisionId, string Detail);

// This is evidence collection, not another strategy or a substitute rules model.
// Full frames/history/proposals/receipts stay in a separate compressed private
// trace so a questionable decision can be replayed through the ordinary policy.
internal sealed class AiCampaignDiagnostics : IDisposable
{
    private readonly Utf8JsonLines decisions;
    private readonly string directory;
    private AiDecisionReplayCase? latestCase;
    private readonly Dictionary<int, AiDecisionReplayCase> latestBySeat = [];
    private bool disposed;
    private readonly Dictionary<int, (AiDecisionReplayCase Case, string SourceId)> initiations = [];
    private readonly Dictionary<int, AiDecisionReplayCase> confirmations = [];
    private readonly HashSet<(int Seat, long Event)> observedEvents = [];
    private readonly HashSet<string> recordedFindings = [];
    private readonly Dictionary<(int Seat, string Decision), AiCampaignNumericInvestment> numericInvestments = [];
    private readonly HashSet<string> criticalReplayCases = [];
    internal HashSet<string> OfferedEffects { get; } = [];
    internal HashSet<string> SelectedEffects { get; } = [];
    internal HashSet<string> BoardWitnesses { get; } = [];
    internal HashSet<string> DecisionWitnesses { get; } = [];
    internal HashSet<string> ResolvedSources { get; } = [];
    internal HashSet<string> UnmodeledOperationSources { get; } = [];
    internal Dictionary<string, int> Commands { get; } = [];
    internal Dictionary<string, int> TargetRecipients { get; } = [];
    internal List<AiCampaignFinding> Findings { get; } = [];
    internal List<AiCampaignStaleProbe> StaleAuthorityProbes { get; } = [];
    internal List<AiCampaignPaymentCancellation> PaymentCancellations { get; } = [];
    internal HashSet<AiTask> Tasks { get; } = [];
    internal int Accepted { get; private set; }
    internal List<string> ReplayCases { get; } = [];
    private readonly List<string> diagnosticErrors = [];
    private bool rawObservationsCaptured;
    private AiCampaignQualificationTracker? qualification;
    internal PublicProjectionPreparation? PublicProjectionPreparation { get; private set; }
    private AiCampaignEvidenceInbox? inbox;
    internal AiCausalMechanicEvidence? QualificationEvidence => qualification?.CausalEvidence;
    internal CampaignEvidenceHandoffStatus? HandoffStatus => inbox?.Status;
    internal bool HasOrderedHandoff => inbox != null;
    internal AiHostFault? CapturedHostFault { get; private set; }

    internal AiCampaignEvidenceInbox EnableOrderedHandoff(int capacity = 256, TimeProvider? clock = null)
    {
        if (inbox != null || capacity < 1) throw new InvalidOperationException("Configure one bounded campaign evidence handoff before starting its producers.");
        return inbox = new(this, capacity, clock);
    }
    internal void DrainPending() => inbox?.Drain();
    internal Task WaitForQueuedWorkAsync(CancellationToken cancellation = default)
        => inbox?.WaitForQueuedWorkAsync(cancellation) ?? Task.Delay(Timeout.Infinite, cancellation);
    internal void RetireHandoff() => inbox?.Retire();
    internal bool OwnsHandoff(AiCampaignEvidenceInbox handoff) => ReferenceEquals(inbox, handoff);

    internal void ApplyHandoff(CampaignEvidenceStamp stamp, CampaignEvidenceEvent value)
    {
        // This small ordered envelope captures event time, not drain time. The
        // existing private proposal/receipt schema and replay files stay intact;
        // idle batch events do not serialize another entire private snapshot.
        long? returnedBatchSequence = value switch {
            CampaignEvidenceEvent.Batch item => item.ReturnSequence,
            CampaignEvidenceEvent.StepCompleted item => item.ReturnedBatchSequence,
            _ => null
        };
        decisions.Write(new { kind = "evidence-handoff", stamp, returnedBatchSequence });
        switch (value) {
            case CampaignEvidenceEvent.Owned item: ObserveCore(item.Observation); break;
            case CampaignEvidenceEvent.Batch item:
                ValidatePublicBatchCore(item.Observations);
                foreach (var observation in item.Observations) ObserveCore(observation);
                break;
            case CampaignEvidenceEvent.ValidateBatch item: ValidatePublicBatchCore(item.Observations); break;
            case CampaignEvidenceEvent.Public item: ObservePublicCore(item.State); break;
            case CampaignEvidenceEvent.PolicyStarted item: SetLatestCase(item.Replay); break;
            case CampaignEvidenceEvent.Proposal item: Proposal(item.Replay, item.Frame, item.Result); break;
            case CampaignEvidenceEvent.PolicyFailure item: PreserveExceptionText(item.Replay, item.Error, item.Stage); break;
            case CampaignEvidenceEvent.Receipt item: ReceiptCore(item.Observation, item.Command, item.Accepted, item.Error); break;
            case CampaignEvidenceEvent.StaleProbe item: RecordStaleProbeCore(item.Result, item.Command); break;
            case CampaignEvidenceEvent.HostFault item:
                CapturedHostFault ??= item.Fault;
                diagnosticErrors.Add("background-host-fault: " + item.Fault.ReasonCode);
                decisions.Write(new { kind = "background-host-fault", item.Fault, item.Error });
                break;
            case CampaignEvidenceEvent.StepCompleted: break;
            default: throw new InvalidDataException("Unknown campaign evidence handoff event.");
        }
    }

    internal void RecordHandoffFailure(Exception error)
        => diagnosticErrors.Add("evidence-handoff: " + AiCampaignEvidenceInbox.ErrorText(error));

    private void SetLatestCase(AiDecisionReplayCase replay)
    {
        latestCase = replay;
        if (inbox == null) return;
        if (!latestBySeat.ContainsKey(replay.State.ViewerId) && latestBySeat.Count >= 8)
            throw new InvalidDataException("Ordered campaign replay attribution exceeded its bounded participant count.");
        latestBySeat[replay.State.ViewerId] = replay;
    }

    internal AiCampaignDiagnostics(string directory)
    {
        this.directory = directory;
        decisions = new(new GZipStream(File.Create(Path.Combine(directory, "private-decisions.jsonl.gz")), CompressionLevel.Fastest));
    }

    internal IAiPolicy Record(IAiPolicy inner) => new RecordingPolicy(inner, this, inbox);
    internal void BindQualification(AiCampaignQualificationTracker tracker)
    {
        if (qualification != null) throw new InvalidOperationException("Campaign qualification is already bound.");
        qualification = tracker;
        PublicProjectionPreparation = new(tracker.Participants.Select(participant => participant.PlayerId));
    }

    internal void ValidatePublicBatch(ImmutableArray<AutomatedSeatObservation> observations)
    {
        if (inbox != null) inbox.Post(new CampaignEvidenceEvent.ValidateBatch(observations));
        else ValidatePublicBatchCore(observations);
    }
    private void ValidatePublicBatchCore(ImmutableArray<AutomatedSeatObservation> observations)
        => ForgePublicProjectionChecks.ValidateBatch(observations, directory, PublicProjectionPreparation);

    private sealed class RecordingPolicy(IAiPolicy inner, AiCampaignDiagnostics evidence, AiCampaignEvidenceInbox? handoff) : IAiPolicy
    {
        public AiPolicyDescriptor Descriptor => inner.Descriptor;
        public async Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation)
        {
            var recorded = AiDecisionReplayCase.Capture(request.Frame.Stamp.SeatId + "-" + request.Frame.Stamp.DecisionId
                + "-" + request.Frame.Stamp.Revision, inner.Descriptor.Id, request);
            if (handoff != null) handoff.Post(new CampaignEvidenceEvent.PolicyStarted(recorded));
            else evidence.latestCase = recorded;
            AiProposal proposal;
            try {
                proposal = await inner.EvaluateAsync(request, cancellation);
            } catch (Exception error) when (error is not OperationCanceledException) {
                // The normal session deliberately redacts model exceptions.
                // Preserve this exact request and the original exception only
                // in the campaign's private diagnostics before rethrowing.
                if (handoff == null) evidence.PreserveException(recorded, error, "policy");
                else try { handoff.Post(new CampaignEvidenceEvent.PolicyFailure(recorded, AiCampaignEvidenceInbox.ErrorText(error), "policy")); }
                    catch { /* The inbox retains its fault; preserve the original policy exception. */ }
                throw;
            }
            try {
                if (handoff != null) handoff.Post(new CampaignEvidenceEvent.Proposal(recorded, request.Frame, proposal));
                else evidence.Proposal(recorded, request.Frame, proposal);
            }
            catch (Exception error) {
                if (handoff == null) evidence.PreserveException(recorded, error, "diagnostic");
                else try { handoff.Post(new CampaignEvidenceEvent.PolicyFailure(recorded, AiCampaignEvidenceInbox.ErrorText(error), "diagnostic")); }
                    catch { /* Preserve the first handoff failure. */ }
                throw;
            }
            return proposal;
        }
    }

    private void PreserveException(AiDecisionReplayCase recorded, Exception error, string stage)
    {
        try { PreserveExceptionText(recorded, error.ToString(), stage); }
        catch { /* Even an unusual exception formatter cannot replace the original policy failure. */ }
    }

    private void PreserveExceptionText(AiDecisionReplayCase recorded, string error, string stage)
    {
        try {
            ExportCase("last-before-mechanical-failure", recorded);
            File.WriteAllText(Path.Combine(directory, "private-" + stage + "-exception.json"),
                JsonSerializer.Serialize(new { recorded.Id, stage, error }));
        } catch (Exception evidenceError) {
            // A failing diagnostic write must never replace the original
            // exception that the caller is about to rethrow.
            try {
                File.WriteAllText(Path.Combine(directory, "private-diagnostic-write-exception.json"),
                    JsonSerializer.Serialize(new { recorded.Id, stage, originalError = error,
                        evidenceError = evidenceError.ToString() }));
            } catch (Exception) { }
        }
    }

    private void Proposal(AiDecisionReplayCase recorded, AiDecisionFrame frame, AiProposal proposal)
    {
        SetLatestCase(recorded);
        var answer = proposal as AiProposal.Answer;
        decisions.Write(new {
            kind = "proposal", frame.Stamp, replay = recorded,
            policy = proposal is AiProposal.Unavailable unavailable ? unavailable.Capability : null,
            reason = answer?.ReasonCode, commandType = answer?.Command.GetType().Name,
            command = answer == null ? (JsonElement?)null : JsonSerializer.SerializeToElement(answer.Command, answer.Command.GetType())
        });
        if (answer == null) return;
        var state = frame.State;
        if (answer.Command is ChooseNumber numericChoice && frame.Semantics.Numeric is { } numeric && numericInvestments.Count < 128) {
            numericInvestments.TryAdd((state.ViewerId, state.Decision!.Id),
                new(recorded, numeric.Purpose.ToString(), numeric.SourceCardId, numericChoice.Value));
            ExportCase("numeric-choice-" + numeric.Purpose + "-" + numericChoice.Value + "-"
                + (state.Decision.Context.Source?.Card?.Name ?? "unknown-source") + "-" + state.Decision.Id, recorded);
        }
        if (answer.Command is PassPriority && state.ActivePlayerId == state.ViewerId && state.PhaseKey is "MAIN1" or "MAIN2"
                && frame.Domain.Actions.Any(action => action.Effect == ActionEffectKind.Land && action.Affordability == ActionAffordability.Payable))
            Finding("land-offer-skipped", "Passed priority with an authoritative playable land offer; review intent before treating this as an error.");
        if (answer.Command is CancelPayment) {
            var cancellation = AiCampaignPaymentCancellation.Capture(state, frame.Semantics);
            PaymentCancellations.Add(cancellation);
            string source = state.Decision?.Context.Source?.Card?.Name ?? "unknown-source";
            if (cancellation.OptionalEffectDecline) {
                ExportCase("optional-effect-payment-declined-" + source, recorded);
            } else {
                Finding("payment-cancelled", "The policy proposed cancelling a payment with provenance "
                    + (cancellation.Provenance?.Purpose.ToString() ?? "unclassified") + "; its receipt, exact bill and production offers are retained separately.");
                string occurrence = state.Decision?.Context.Source?.Card?.VisualId ?? "";
                if (occurrence.Length > 0 && initiations.TryGetValue(state.ViewerId, out var initiation)
                    && initiation.SourceId == occurrence && initiation.Case.State.Turn == state.Turn)
                    ExportCase("earlier-same-source-action-before-cancel-" + source, initiation.Case);
                if (occurrence.Length > 0 && confirmations.TryGetValue(state.ViewerId, out var confirmation) && confirmation.State.Turn == state.Turn
                    && confirmation.State.Decision?.Context.Source?.Card?.VisualId == occurrence)
                    ExportCase("earlier-same-source-confirmation-before-cancel-" + source, confirmation);
            }
        }
        if (!frame.Semantics.SelectionIsCost && frame.Semantics.Intent is DecisionIntent.Harm or DecisionIntent.Damage
                or DecisionIntent.Destroy or DecisionIntent.Exile or DecisionIntent.Bounce or DecisionIntent.Tap) {
            bool own = answer.Command is SelectPlayer player && player.Player == state.ViewerId
                || answer.Command is SelectCard selected && state.Viewer!.Zone("Battlefield").Cards.Any(card => card.Key == selected.Card);
            bool otherCandidate = state.Players.Where(player => player.Id != state.ViewerId)
                .Any(player => player.Zones.SelectMany(zone => zone.Cards).Any(card => card.Selectable))
                || (state.Decision?.Payload switch { TargetSelectionPayload target => target.Players,
                    EntitySelectionPayload entity => entity.Players, _ => [] }).Any(player => player != state.ViewerId);
            if (own && otherCandidate) Finding("harmful-own-target-review", "A harmful operation selected this seat's player/permanent while an opposing candidate was present; synergy can make this intentional.");
            else if (own) DecisionWitnesses.Add("forced-own-operation");
        }
        void Finding(string code, string detail)
        {
            if (recordedFindings.Add(code + ":" + frame.Stamp.SeatId + ":" + frame.Stamp.DecisionId)) {
                Findings.Add(new(code, frame.Stamp.SeatId, state.Turn, frame.Stamp.DecisionId, detail));
                string source = state.Decision?.Context.Source?.Card?.Name ?? "unknown-source";
                ExportCase(code + "-" + source, recorded);
            }
        }
    }

    internal void Receipt(AutomatedSeatObservation? observation, GameCommand command, bool accepted, string? error)
    {
        if (inbox != null) inbox.Post(new CampaignEvidenceEvent.Receipt(observation, command, accepted, error));
        else ReceiptCore(observation, command, accepted, error);
    }
    private void ReceiptCore(AutomatedSeatObservation? observation, GameCommand command, bool accepted, string? error)
    {
        qualification?.Receipt(observation, command, accepted, error);
        decisions.Write(new { kind = "receipt", seat = observation?.SeatId,
            command.GameId, decision = (command as DecisionCommand)?.DecisionId, accepted, error,
            commandType = command.GetType().Name, command = JsonSerializer.SerializeToElement(command, command.GetType()) });
        if (observation != null && command is ChooseNumber number
            && numericInvestments.TryGetValue((observation.SeatId, number.DecisionId), out var numeric))
            numeric.Receipt(accepted, error);
        if (observation != null && command is CancelPayment cancel) {
            int index = PaymentCancellations.FindLastIndex(item => item.GameId == cancel.GameId
                && item.SeatId == observation.SeatId && item.DecisionId == cancel.DecisionId);
            if (index >= 0) {
                var item = PaymentCancellations[index] with { Accepted = accepted };
                PaymentCancellations[index] = item;
                if (accepted && item.OptionalEffectDecline) DecisionWitnesses.Add("optional-effect-payment-declined");
            }
        }
        if (!accepted || observation == null) return;
        Accepted++;
        var latest = inbox == null ? latestCase : latestBySeat.GetValueOrDefault(observation.SeatId);
        if (latest is { } captured && captured.State.ViewerId == observation.SeatId
                && captured.State.Decision?.Id == (command as DecisionCommand)?.DecisionId) {
            if (captured.State.Decision?.Payload is PriorityPayload && command is UseOfferedAction route
                && observation.Domain.Actions.Any(action => action.ActionId == route.ActionId && !action.ManaAbility
                    && action.Effect != ActionEffectKind.Land)
                && observation.Domain.Sources.FirstOrDefault(source => source.Actions.Any(action => action.Id == route.ActionId)) is { } source)
                initiations[observation.SeatId] = (captured, source.SourceCardId);
            if (captured.State.Decision?.Payload is ConfirmationPayload && command is AcceptConfirmation)
                confirmations[observation.SeatId] = captured;
        }
        Commands[command.GetType().Name] = Commands.GetValueOrDefault(command.GetType().Name) + 1;
        if (command is UseOfferedAction use && observation.Domain.Actions.FirstOrDefault(action => action.ActionId == use.ActionId) is { } semantic)
            foreach (var effect in semantic.Effects) SelectedEffects.Add(effect.ToString());
        int? recipient = command is SelectPlayer player ? player.Player : command is ToggleAttackPlayer attack ? attack.Player : null;
        if (recipient != null) {
            string purpose = command is ToggleAttackPlayer ? "attack" : observation.Semantics.SelectionIsCost ? "cost" : observation.Semantics.Intent.ToString();
            string key = observation.SeatId + ":" + purpose + "->" + recipient;
            TargetRecipients[key] = TargetRecipients.GetValueOrDefault(key) + 1;
        }
    }

    internal void RecordStaleProbe(AiCampaignStaleProbe result, DecisionCommand command)
    {
        if (inbox != null) inbox.Post(new CampaignEvidenceEvent.StaleProbe(result, command));
        else RecordStaleProbeCore(result, command);
    }
    private void RecordStaleProbeCore(AiCampaignStaleProbe result, DecisionCommand command)
    {
        StaleAuthorityProbes.Add(result);
        try {
            decisions.Write(new { kind = "stale-authority-probe", result,
                command = JsonSerializer.SerializeToElement(command, command.GetType()) });
        } catch (Exception error) {
            diagnosticErrors.Add("stale-probe-capture: " + error);
            throw;
        }
    }

    internal void Observe(AutomatedSeatObservation observation)
    {
        if (inbox != null) inbox.Post(new CampaignEvidenceEvent.Owned(observation));
        else ObserveCore(observation);
    }
    private void ObserveCore(AutomatedSeatObservation observation)
    {
        qualification?.ObserveOwned(observation);
        foreach (var investment in numericInvestments.Values) investment.ObserveOwned(observation);
        ReviewNumericInvestments(observation.State.Id);
        var state = observation.State;
        if (state.Decision != null) Tasks.Add(AiTasks.From(state.Decision.Payload));
        foreach (var action in observation.Domain.Actions) {
            foreach (var effect in action.Effects) OfferedEffects.Add(effect.ToString());
            if (action.ManaAbility && action.Production.Knowledge == SemanticKnowledge.Known
                && action.Production.Alternatives.SelectMany(option => option.Colors.Keys).Distinct(StringComparer.Ordinal).Count() > 1)
                DecisionWitnesses.Add("finite-multicolor-production-offered");
            if (action.OperationGraph.Knowledge != SemanticKnowledge.Known
                || action.OperationGraph.Operations.Any(operation => operation.Knowledge != SemanticKnowledge.Known)) {
                var source = observation.Domain.Sources.FirstOrDefault(source => source.Actions.Any(offer => offer.Id == action.ActionId));
                string name = source == null ? "unresolved occurrence" : CardViews.FindActionSource(state, source.SourceCardKey)?.Card.Name ?? "unresolved occurrence";
                UnmodeledOperationSources.Add(name + ":" + action.Effect + ":" + action.OperationGraph.Knowledge);
            }
        }
        if (observation.Semantics.UnpaidMana.Any(symbol => symbol.Trim('{', '}') == "C")) DecisionWitnesses.Add("required-colorless-bill");
        if (observation.Semantics.Intent == DecisionIntent.RemoveCounters && observation.Semantics.SelectionIsCost)
            DecisionWitnesses.Add("remove-counter-cost");
        foreach (var entry in state.Activity) {
            if (!observedEvents.Add((observation.SeatId, entry.Id))) continue;
            if (entry.Kind == "resolved" && entry.Outcome == "resolved" && entry.CardName is { Length: > 0 }) ResolvedSources.Add(entry.CardName);
            if (entry.Impact is { Kind: "damage", Amount: > 0 }) BoardWitnesses.Add("reported-damage");
            if (entry.Change is { Kind: "counters" } change && change.Before != change.After) BoardWitnesses.Add("counter-change");
        }
    }

    internal void ObservePublic(GameSnapshot state)
    {
        if (inbox != null) inbox.Post(new CampaignEvidenceEvent.Public(state));
        else ObservePublicCore(state);
    }
    private void ObservePublicCore(GameSnapshot state)
    {
        qualification?.ObservePublic(state);
        foreach (var investment in numericInvestments.Values) investment.ObservePublic(state);
        ReviewNumericInvestments(state.Id);
        foreach (var card in state.Players.SelectMany(player => player.Zone("Battlefield").Cards)) {
            if (card.Type.Contains("Creature", StringComparison.Ordinal)) BoardWitnesses.Add("creature-battlefield");
            if (card.GamePieceType == "token") BoardWitnesses.Add("token-battlefield");
            if (card.Counters.Values.Any(value => value > 0)) BoardWitnesses.Add("counters-on-battlefield");
            if (card.AttachedTo != null || !card.Attachments.IsEmpty) BoardWitnesses.Add("attachment-on-battlefield");
        }
        if (state.Combat?.Attackers.Length > 0) BoardWitnesses.Add("declared-combat");
        if (state.Stack.Any(item => item.Targets.Length > 1)) BoardWitnesses.Add("multiple-stack-targets");
    }

    private void ReviewNumericInvestments(string gameId)
    {
        foreach (var investment in numericInvestments.Values) {
            if (investment.GameId != gameId || !investment.ZeroXNonpositiveBodyReview
                || !recordedFindings.Add("zero-x-nonpositive-body-review:" + investment.SeatId + ":" + investment.DecisionId)) continue;
            Findings.Add(new("zero-x-nonpositive-body-review", investment.SeatId, investment.Request.State.Turn,
                investment.DecisionId, "An accepted zero-X announcement resolved and the same public source was observed in the graveyard with nonpositive toughness. Review intent; this is not a universal legality or causal judgment."));
            ExportCriticalNumericCase(investment);
        }
    }

    private void ExportCriticalNumericCase(AiCampaignNumericInvestment investment)
    {
        if (criticalReplayCases.Count >= 4) return;
        string safe = new(investment.DecisionId.Where(character => char.IsAsciiLetterOrDigit(character) || character == '-').Take(64).ToArray());
        string relative = Path.Combine("private-critical-replays", "zero-x-body-" + investment.SeatId + "-" + safe + ".json");
        if (!criticalReplayCases.Add(relative)) return;
        try {
            Directory.CreateDirectory(Path.Combine(directory, "private-critical-replays"));
            File.WriteAllText(Path.Combine(directory, relative), JsonSerializer.Serialize(investment.Request));
            ReplayCases.Add(relative);
        } catch (Exception error) { diagnosticErrors.Add("critical-numeric-replay: " + error); }
    }

    private void ExportCase(string label, AiDecisionReplayCase recorded)
    {
        string safe = new(label.Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-').Take(96).ToArray());
        string relative = Path.Combine("private-replays", safe + ".json");
        if (ReplayCases.Contains(relative) || ReplayCases.Count(path => path.StartsWith("private-replays" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) >= 16
            && label != "last-before-mechanical-failure") return;
        Directory.CreateDirectory(Path.Combine(directory, "private-replays"));
        File.WriteAllText(Path.Combine(directory, relative), JsonSerializer.Serialize(recorded));
        ReplayCases.Add(relative);
    }

    internal static void CheckCriticalReplayBudget(Action<bool, string> check, AiDecisionReplayCase request)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "mana-ai-critical-replays-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try {
            using var evidence = new AiCampaignDiagnostics(temporary);
            for (int index = 0; index < 17; index++) evidence.ExportCase("ordinary-" + index, request);
            for (int index = 0; index < 6; index++) {
                var recorded = request with { Id = "critical-case-" + index,
                    State = request.State with { Decision = request.State.Decision! with { Id = "critical-" + index } } };
                evidence.ExportCriticalNumericCase(new(recorded, "AnnounceX", "numeric-source", 0));
            }
            var critical = evidence.ReplayCases.Where(path => path.StartsWith("private-critical-replays" + Path.DirectorySeparatorChar,
                StringComparison.Ordinal)).ToArray();
            check(evidence.ReplayCases.Count == 20 && critical.Length == 4
                    && critical.Select((path, index) => JsonSerializer.Deserialize<AiDecisionReplayCase>(
                        File.ReadAllText(Path.Combine(temporary, path)))?.Id == "critical-case-" + index).All(value => value),
                "Four exact critical numeric requests survive an exhausted sixteen-case ordinary replay budget without unbounded export growth");
        } finally { Directory.Delete(temporary, recursive: true); }
    }

    internal Exception? PreserveObservations(ImmutableArray<AutomatedSeatObservation> observations)
    {
        try {
            File.WriteAllText(Path.Combine(directory, "private-final-observations.json"), JsonSerializer.Serialize(observations));
            rawObservationsCaptured = true;
            return null;
        } catch (Exception error) {
            diagnosticErrors.Add("raw-observation-capture: " + error);
            return error;
        }
    }

    internal Exception? PreserveOutcome(AiCampaignOutcomeProof outcome, GameSnapshot? state)
    {
        try {
            File.WriteAllText(Path.Combine(directory, outcome.PublicSnapshotFile), JsonSerializer.Serialize(new {
                formatVersion = 1, stage = "before-simulation-cleanup", outcome, state
            }));
            return null;
        } catch (Exception error) {
            diagnosticErrors.Add("pre-cleanup-outcome-capture: " + error);
            return error;
        }
    }

    internal void PreserveFailure(Exception error, string stage, int completedSteps,
        ImmutableArray<AutomatedSeatObservation> observations)
    {
        bool rejectedWireCaptured = false;
        for (Exception? cause = error; cause != null; cause = cause.InnerException) {
            if (cause is not ForgeAutomatedObservationException rejected) continue;
            try {
                File.WriteAllText(Path.Combine(directory, "private-rejected-observation.json"), JsonSerializer.Serialize(new {
                    formatVersion = 1, audience = "host-private", stage = "adapter-decode", authority = "none",
                    role = "rejected raw wire batch; not a current scope, executable command or policy replay",
                    raw = rejected.RejectedWire
                }));
                rejectedWireCaptured = true;
            } catch (Exception secondary) { diagnosticErrors.Add("rejected-wire-export: " + secondary); }
            break;
        }
        try { if (latestCase != null) ExportCase("last-before-mechanical-failure", latestCase); }
        catch (Exception secondary) { diagnosticErrors.Add("last-policy-request-export: " + secondary); }
        try {
            File.WriteAllText(Path.Combine(directory, "private-failure-manifest.json"),
                DescribeFailure(error, stage, completedSteps, observations, latestCase, rawObservationsCaptured, diagnosticErrors, rejectedWireCaptured).GetRawText());
        } catch (Exception secondary) { diagnosticErrors.Add("failure-manifest-export: " + secondary); }
    }

    internal static JsonElement DescribeFailure(Exception error, string stage, int completedSteps,
        ImmutableArray<AutomatedSeatObservation> observations, AiDecisionReplayCase? lastPolicyRequest,
        bool rawCaptured, IEnumerable<string> secondaryErrors, bool rejectedWireCaptured = false)
    {
        var rejectedProjection = ForgePublicProjectionChecks.FindFailureEvidence(error);
        return JsonSerializer.SerializeToElement(new {
            formatVersion = 2, audience = "host-private", kind = "raw-observation-failure-context", authority = "none",
            stage, completedSteps, exception = error.ToString(),
            rejectedWireObservation = new {
                file = "private-rejected-observation.json", captured = rejectedWireCaptured, capturePhase = "adapter-decode",
                role = "rejected raw wire batch; not an executable command or policy replay"
            },
            rejectedPublicProjection = new {
                file = ForgePublicProjectionChecks.RejectedBatchFile, captured = rejectedProjection?.Captured == true,
                capturePhase = "public-invariant-validation", earlierFailureRetained = rejectedProjection?.EarlierFailureRetained == true,
                captureError = rejectedProjection?.CaptureError,
                audienceCount = rejectedProjection?.Scopes.Length ?? 0,
                role = "Exact supplied immutable batch rejected after adapter decode; not accepted state, a command or policy replay.",
                scopes = (rejectedProjection?.Scopes ?? []).Select(item => new { seatId = item.SeatId, gameId = item.GameId,
                    viewerId = item.ViewerId, revision = item.Revision, boardRevision = item.BoardRevision, decisionId = item.DecisionId })
            },
            rawObservation = new {
                file = "private-final-observations.json", captured = rawCaptured, capturePhase = "before-teardown",
                role = "last-completed-owned-observation-batch; not an executable command or policy replay",
                scopes = observations.Select(item => new { seatId = item.SeatId, gameId = item.State.Id,
                    viewerId = item.State.ViewerId, decisionId = item.State.Decision?.Id, revision = item.State.Revision,
                    boardRevision = item.State.BoardRevision, offerVersion = item.Domain.Version })
            },
            lastPolicyRequest = lastPolicyRequest == null ? null : new {
                file = Path.Combine("private-replays", "last-before-mechanical-failure.json"),
                role = "last-policy-evaluated-request; not the rejected-observation artifact",
                note = "This request can precede the failure. Rejected adapter wire and public-invariant batches have separate artifacts; the last completed batch may be valid.",
                lastPolicyRequest.Id, gameId = lastPolicyRequest.State.Id, seatId = lastPolicyRequest.State.ViewerId,
                decisionId = lastPolicyRequest.State.Decision?.Id, revision = lastPolicyRequest.State.Revision,
                offerVersion = lastPolicyRequest.Domain.Version
            },
            secondaryDiagnosticErrors = secondaryErrors.ToArray()
        });
    }

    internal AiCampaignEvidence Summary() => new(OfferedEffects.Order().ToImmutableArray(), SelectedEffects.Order().ToImmutableArray(),
        BoardWitnesses.Order().ToImmutableArray(), DecisionWitnesses.Order().ToImmutableArray(), ResolvedSources.Order().ToImmutableArray(),
        Tasks.Order().ToImmutableArray(), Accepted, Commands.ToImmutableDictionary(), TargetRecipients.ToImmutableDictionary(),
        UnmodeledOperationSources.Order().ToImmutableArray(), Findings.ToImmutableArray(), numericInvestments.Values.Select(value => value.Snapshot()).ToImmutableArray(),
        StaleAuthorityProbes.ToImmutableArray(), PaymentCancellations.ToImmutableArray(),
        ReplayCases.ToImmutableArray(), diagnosticErrors.ToImmutableArray(),
        new[] { "creature-battlefield", "token-battlefield", "counter-change", "attachment-on-battlefield",
            "declared-combat", "reported-damage", "multiple-stack-targets" }.Except(BoardWitnesses).ToImmutableArray(),
        "Offers, chosen commands, authoritative board/event witnesses and review findings are separate. Numeric evidence separates proposed/accepted values from subsequent exact-source payment inputs and owned-seat event reports; public stack and actual statistics are observed independently through the spectator. A source correlation does not prove X caused every later effect. Earlier-same-source replay links do not identify which cast, ability or optional payment was cancelled. A resolved source does not prove every intended effect occurred. TargetRecipients counts player choices and attacks only; their distribution does not establish social quality.");

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (inbox?.RetireForDisposal() is { } failure) RecordHandoffFailure(failure);
        PublicProjectionPreparation?.Clear();
        decisions.Dispose();
    }
}

internal sealed class AiCampaignProvider(IAutomatedSeatProvider inner, AiCampaignDiagnostics diagnostics,
    Func<int, GameSnapshot, GameSnapshot, DecisionCommand, string, Task>? staleReplay = null,
    AiCampaignEvidenceInbox? inbox = null) : IAutomatedSeatProvider, IAutomatedPriorityContinuation
{
    private readonly AiCampaignStaleAuthority? stale = staleReplay == null ? null : new();
    private ConfiguredSimulationStart? configured;
    private readonly AiCausalHistoryReader history = new();
    private ImmutableArray<AutomatedSeatObservation> last = [];
    private int executing;
    private int controlsExecuting;
    public bool SupportsAutomatedPriorityContinuation => inner is IAutomatedPriorityContinuation {
        SupportsAutomatedPriorityContinuation: true };
    public bool SupportsAutomatedPriorityResponses => inner is IAutomatedPriorityContinuation {
        SupportsAutomatedPriorityResponses: true };
    public long AutomatedPriorityEpoch => inner is IAutomatedPriorityContinuation port ? port.AutomatedPriorityEpoch : 0;
    internal int PriorityControlsExecuting => Volatile.Read(ref controlsExecuting);
    public async Task<AutomatedPriorityControlResult> ExecuteAutomatedPriorityControlAsync(long epoch, int seatId,
        AutomatedPriorityControl control, CancellationToken cancellation = default)
    {
        RequireHandoff();
        if (inner is not IAutomatedPriorityContinuation port) return new AutomatedPriorityControlResult.Retired();
        Interlocked.Increment(ref controlsExecuting);
        try { return await port.ExecuteAutomatedPriorityControlAsync(epoch, seatId, control, cancellation); }
        finally { Interlocked.Decrement(ref controlsExecuting); }
        // No Receipt/AcceptedBySeat: consent and observation metadata are not
        // gameplay commands or policy acceptance. Host certificates stay separate.
    }
    internal void BindConfigured(ConfiguredSimulationStart start)
    {
        if (configured != null) throw new InvalidOperationException("The campaign provider is already bound to a configured game.");
        ConfiguredSimulationFacts.ValidateState(start, start.State);
        configured = start;
    }
    internal ImmutableArray<AutomatedSeatObservation> Last => SnapshotObservations;
    internal ImmutableArray<AutomatedSeatObservation> SnapshotObservations => inbox?.SnapshotObservations ?? last;
    internal ImmutableArray<AutomatedSeatObservation> ProcessedObservations => inbox?.ProcessedObservations
        ?? throw new InvalidOperationException("Processed-cycle evidence requires the explicit background handoff.");
    internal ImmutableDictionary<int, int> AcceptedBySeat => inbox?.AcceptedBySeat ?? ImmutableDictionary<int, int>.Empty;
    internal int Executing => Volatile.Read(ref executing);
    private void RequireHandoff()
    {
        if (inbox == null) return;
        if (!diagnostics.OwnsHandoff(inbox)) throw new InvalidOperationException("The background provider belongs to another evidence owner.");
        inbox.EnsureActive();
    }
    public Task EnableAutomatedSeatsAsync(CancellationToken cancellation = default)
    {
        RequireHandoff();
        return inner.EnableAutomatedSeatsAsync(cancellation);
    }
    public Task ReportAutomationFaultAsync(string gameId, int seatId, string reasonCode, CancellationToken cancellation = default)
    {
        RequireHandoff();
        return inner.ReportAutomationFaultAsync(gameId, seatId, reasonCode, cancellation);
    }
    public async Task<ImmutableArray<AutomatedSeatObservation>> ObserveAutomatedSeatsAsync(CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        RequireHandoff();
        if (inbox != null && stale != null) throw new InvalidOperationException("Background evidence cannot inject manual stale-command probes.");
        inbox?.BeginNextCycle();
        var received = await inner.ObserveAutomatedSeatsAsync(cancellation);
        if (inbox == null) last = received;
        cancellation.ThrowIfCancellationRequested();
        foreach (var observation in received)
            if (observation.State.ViewerId != observation.SeatId
                || !AiCampaignVisibility.PrivateWindowsAreDisclosed(observation.State, observation.Semantics, observation.Domain))
                throw new InvalidDataException("An automated observation disclosed another player's private hand or library before policy evaluation.");
        if (configured is { } setup) {
            if (received.Length != setup.Participants.Length || received.Select(item => item.SeatId).Distinct().Count() != received.Length
                || !received.Select(item => item.SeatId).Order().SequenceEqual(setup.Participants.Select(item => item.PlayerId).Order()))
                throw new InvalidDataException("Configured automated observations changed their actual player mapping.");
            foreach (var observation in received) {
                if (observation.SeatId != observation.State.ViewerId)
                    throw new InvalidDataException("A configured bot received a different player's observation.");
                ConfiguredSimulationFacts.ValidateOwnedState(setup, observation.State);
            }
        }
        if (inbox != null) inbox.Returned(received);
        else {
            diagnostics.ValidatePublicBatch(received);
            foreach (var observation in received) diagnostics.Observe(observation);
            await history.DrainAsync(inner, received, diagnostics.QualificationEvidence);
            if (stale != null) await stale.ObserveAsync(received, staleReplay!, diagnostics.RecordStaleProbe);
        }
        cancellation.ThrowIfCancellationRequested();
        return received;
    }

    // Only the campaign owner calls this, after draining ordered observations
    // and receipts. Reading history does not observe/drive a second AI scope.
    internal async Task DrainHistoryAsync(bool force = false)
    {
        if (inbox == null) throw new InvalidOperationException("Background history requires the explicit evidence handoff.");
        RequireHandoff();
        diagnostics.DrainPending();
        await history.DrainAsync(inner, ProcessedObservations, diagnostics.QualificationEvidence, force);
    }

    public async Task ExecuteAutomatedAsync(int seatId, GameCommand command, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        RequireHandoff();
        var observation = SnapshotObservations.FirstOrDefault(item => item.SeatId == seatId);
        Interlocked.Increment(ref executing);
        try {
            try { await inner.ExecuteAutomatedAsync(seatId, command, cancellation); }
            catch (Exception error) {
                if (inbox == null) diagnostics.Receipt(observation, command, false, error.GetType().Name);
                else try { inbox.Receipt(observation, command, false, error.GetType().Name); }
                    catch { /* Preserve the actual provider failure; the inbox retains its own first fault. */ }
                throw;
            }
            if (inbox != null) inbox.Receipt(observation, command, true, null);
            else {
                diagnostics.Receipt(observation, command, true, null);
                if (observation != null)
                    await history.DrainAsync(inner, [observation], diagnostics.QualificationEvidence, force: true);
                if (observation != null) stale?.Remember(observation, command);
            }
        } finally { Interlocked.Decrement(ref executing); }
    }
}
