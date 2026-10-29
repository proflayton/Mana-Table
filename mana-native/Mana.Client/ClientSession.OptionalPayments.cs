using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Client;

public enum OptionalPaymentMode { Ask, AutoPay, AutoDecline }

public sealed record OptionalPaymentOffer(string OfferId, string SourceCardId, string SourceName,
    ImmutableArray<ManaBillPart> ManaParts, OptionalPaymentMode Mode,
    OptionalManaPaymentAvailability Availability, bool CanAutoPay, bool Paused);

public sealed record OptionalPaymentPolicyEntry(string Id, string SourceCardId, string SourceName,
    OptionalPaymentMode Mode, ImmutableArray<ManaBillPart> ManaParts, bool Paused);

public sealed partial class ClientSession
{
    // Future answers are local to this seat and match. They never confer a yield
    // or retain action handles, hidden faces, or a provider's payment plan.
    private sealed record PaymentPreference(string Id, string Source, string Name, string Group, string Key,
        PaymentPurpose Purpose, int Payer, ImmutableArray<ManaBillPart> Bill, OptionalPaymentMode Mode, bool Paused = false);
    private readonly List<PaymentPreference> optionalPayments = [];
    private readonly OptionalPaymentSubmissions optionalSubmissions = new();
    private OptionalPaymentIntent? optionalIntent;
    private bool optionalPaymentsPaused;

    public bool OptionalPaymentsPaused => optionalPaymentsPaused;
    public ImmutableArray<OptionalPaymentPolicyEntry> OptionalPaymentPolicies => optionalPayments.Select(preference =>
        new OptionalPaymentPolicyEntry(preference.Id, preference.Source, preference.Name, preference.Mode, preference.Bill,
            optionalPaymentsPaused || preference.Paused)).ToImmutableArray();

    public OptionalPaymentOffer? CurrentOptionalPayment {
        get {
            if (Closed || NeedsRecovery || State is not { } state || OptionalPaymentFacts.Current(state) is not { } facts) return null;
            var preference = optionalPayments.FirstOrDefault(item => Matches(item, facts));
            return new(facts.Offer.OfferId, facts.Offer.SourceCardId, facts.Name, facts.Support.OriginalManaParts,
                preference?.Mode ?? OptionalPaymentMode.Ask, facts.Support.Availability, facts.Support.CanAutoPay,
                optionalPaymentsPaused || preference?.Paused == true);
        }
    }

    public bool TrySetOptionalPaymentPolicy(OptionalPaymentMode mode)
    {
        if (!Enum.IsDefined(mode) || Closed || NeedsRecovery || State is not { } state
            || OptionalPaymentFacts.Current(state) is not { } facts) return false;
        if (mode == OptionalPaymentMode.Ask) {
            foreach (var item in optionalPayments.Where(item => Matches(item, facts)).ToArray()) ClearOptionalPaymentPolicy(item.Id);
            return true;
        }
        if (Busy || Polling || mode == OptionalPaymentMode.AutoPay && !facts.Support.CanAutoPay) return false;
        StopFiniteIntent(); CancelAdvance(); optionalIntent = null;
        optionalPayments.RemoveAll(item => Matches(item, facts));
        if (optionalPayments.Count >= 256) return false;
        optionalPayments.Add(new(Guid.NewGuid().ToString("N"), facts.Offer.SourceCardId, facts.Name, facts.Offer.GroupKey,
            facts.Support.PolicyKey, facts.Offer.Purpose, facts.Offer.PayerId, facts.Support.OriginalManaParts, mode));
        return true;
    }

    public void ClearOptionalPaymentPolicy(string id)
    {
        if (optionalPayments.RemoveAll(item => item.Id == id) == 0) return;
        if (optionalIntent?.PreferenceId == id) { StopFiniteIntent(); CancelAdvance(); optionalIntent = null; }
    }

    public void ClearAllOptionalPaymentPolicies()
    {
        optionalPayments.Clear();
        if (optionalIntent != null) { StopFiniteIntent(); CancelAdvance(); optionalIntent = null; }
    }

    public void PauseOptionalPayments()
    {
        optionalPaymentsPaused = true;
        optionalIntent?.Pause();
        // Cancellation reconciles one command already sent; it is not an undo.
        if (finiteIntent is OptionalPaymentIntent) CancelAdvance();
    }

    public void ResumeOptionalPayments()
    {
        optionalPaymentsPaused = false;
        optionalIntent?.Resume();
    }

    public bool AdvanceOptionalPayments(Func<GameSnapshot, GameSnapshot, bool> shouldPresent)
    {
        ArgumentNullException.ThrowIfNull(shouldPresent);
        if (Busy || Polling || Closed || NeedsRecovery || optionalPaymentsPaused || State is not { } state
            || finiteIntent is FiniteResolutionIntent { IsActive: true }
            || OptionalPaymentFacts.Current(state) is not { } facts) return false;
        var preference = optionalPayments.FirstOrDefault(item => Matches(item, facts));
        if (preference?.Paused == true && facts.Support.Availability != OptionalManaPaymentAvailability.Unpayable
            || !OptionalPaymentFacts.CanAnswer(facts, preference?.Mode ?? OptionalPaymentMode.Ask)) return false;
        if (optionalIntent is not { IsActive: true } current || !current.Matches(facts)) {
            optionalIntent = new(state, facts, preference?.Id, preference?.Mode ?? OptionalPaymentMode.Ask, optionalSubmissions);
        }
        return AdvanceFiniteCore(optionalIntent, shouldPresent);
    }

    private static bool Matches(PaymentPreference preference, OptionalPaymentFacts facts)
        => preference.Source == facts.Offer.SourceCardId && preference.Group == facts.Offer.GroupKey
            && preference.Key == facts.Support.PolicyKey && preference.Purpose == facts.Offer.Purpose
            && preference.Payer == facts.Offer.PayerId && preference.Bill.SequenceEqual(facts.Support.OriginalManaParts);

    private void ObserveOptionalPayments(GameSnapshot? previous, GameSnapshot? state, bool recovered)
    {
        if (state == null || recovered || previous != null && (previous.Id != state.Id || previous.ViewerId != state.ViewerId)
            || state.Status is not ("playing" or "resolving") || state.Viewer is not { Eliminated: false }) {
            optionalPayments.Clear(); optionalIntent?.Stop(); optionalIntent = null;
            optionalSubmissions.Clear(); optionalPaymentsPaused = false; return;
        }
        optionalPayments.RemoveAll(item => CardViews.FindZoneOccurrence(state, item.Source) is not { Zone: "Battlefield", Card.FaceDown: false } source
            || source.PlayerId != state.ViewerId);
        if (OptionalPaymentFacts.Current(state) is { } facts) {
            // A new bill for this exact ability withdraws the old permission.
            optionalPayments.RemoveAll(item => item.Source == facts.Offer.SourceCardId && item.Group == facts.Offer.GroupKey && !Matches(item, facts));
        }
        if (optionalIntent is { PreferenceId: { } id } intent && intent.RequiresRearmAt(state)) {
            int index = optionalPayments.FindIndex(item => item.Id == id);
            if (index >= 0) optionalPayments[index] = optionalPayments[index] with { Paused = true };
        }
        if (optionalIntent?.HasFinishedAt(state) == true) optionalIntent = null;
    }
}

internal sealed record OptionalPaymentFacts(OptionalDeclineOffer Offer, OptionalManaPaymentSupport Support, string Name)
{
    internal static OptionalPaymentFacts? Current(GameSnapshot state)
    {
        if (state.Status is not ("playing" or "resolving") || state.Viewer is not { Eliminated: false }
            || state.Decision?.Payload is not PaymentPayload { OptionalDecline: { } offer, OptionalPaymentSupport: { } support }
            || !FiniteResolutionIntent.OwnedDecline(state, offer)
            || CardViews.FindZoneOccurrence(state, offer.SourceCardId) is not { Zone: "Battlefield", Card.FaceDown: false } source
            || source.PlayerId != state.ViewerId) return null;
        try { ExecutionFrames.Validate(state); FiniteResolutionFacts.Validate(state); }
        catch (ArgumentException) { return null; }
        return new(offer, support, source.Card.Name);
    }

    internal static bool CanAnswer(OptionalPaymentFacts facts, OptionalPaymentMode mode)
        => facts.Support.Availability == OptionalManaPaymentAvailability.Unpayable || mode == OptionalPaymentMode.AutoDecline
            || mode == OptionalPaymentMode.AutoPay && facts.Support.CanAutoPay;
}

// The serial runner can be cancelled between send and publication. Deduplicate
// at the send boundary, across worker chunks and replacement intents as well.
internal sealed class OptionalPaymentSubmissions
{
    private readonly object gate = new();
    private readonly HashSet<string> sent = [];
    private readonly Queue<string> order = [];
    internal bool Contains(string scope) { lock (gate) return sent.Contains(scope); }
    internal void Add(string scope) { lock (gate) {
        if (!sent.Add(scope)) return;
        order.Enqueue(scope);
        if (order.Count > 4096) sent.Remove(order.Dequeue());
    } }
    internal void Clear() { lock (gate) { sent.Clear(); order.Clear(); } }
}

internal sealed class OptionalPaymentIntent(GameSnapshot initial, OptionalPaymentFacts original, string? preferenceId,
    OptionalPaymentMode mode, OptionalPaymentSubmissions submissions) : IFiniteDecisionPolicy
{
    private readonly object gate = new();
    private bool active = true, paused;
    private long revision = initial.Revision;
    public string? PreferenceId => preferenceId;
    public bool IsActive { get { lock (gate) return active; } }
    public bool IsPaused { get { lock (gate) return paused; } }
    // Derive presentation/lifecycle changes from the accepted publication, never
    // a worker's newer intermediate observation waiting in the pump queue.
    internal bool RequiresRearmAt(GameSnapshot state)
    {
        if (state.Id != initial.Id || state.ViewerId != initial.ViewerId || state.DecisionOwnerId != state.ViewerId) return false;
        if (HasFinishedAt(state)) return false;
        if (OptionalPaymentFacts.Current(state) is { } facts && Matches(facts))
            return state.ResolutionChoiceVersion != initial.ResolutionChoiceVersion || !OptionalPaymentFacts.CanAnswer(facts, mode);
        return state.Decision != null && state.Decision.Payload is not PriorityPayload
            && (state.Decision.Payload is not PaymentPayload { OptionalDecline: { } offer } || offer.OfferId == original.Offer.OfferId);
    }
    internal bool HasFinishedAt(GameSnapshot state) => state.Decision?.Payload is PriorityPayload
        || ExecutionFrames.Find(state, original.Offer.ProducingTriggerExecutionId)?.Step == ExecutionStep.Complete
        || state.Decision?.Payload is PaymentPayload { OptionalDecline: { } offer } && offer.OfferId != original.Offer.OfferId;
    public void Pause() { lock (gate) paused = true; }
    public void Resume() { lock (gate) paused = false; }
    public void Stop() { lock (gate) active = false; }
    internal bool Matches(OptionalPaymentFacts facts) => facts.Offer.OfferId == original.Offer.OfferId
        && facts.Offer.SourceCardId == original.Offer.SourceCardId && facts.Offer.GroupKey == original.Offer.GroupKey
        && facts.Support.PolicyKey == original.Support.PolicyKey
        && facts.Support.OriginalManaParts.SequenceEqual(original.Support.OriginalManaParts);
    public bool CanContinue(GameSnapshot state)
    {
        lock (gate) {
            if (!active) return false;
            if (state.Revision < revision) return true;
            revision = state.Revision;
            if (state.Id != initial.Id || state.ViewerId != initial.ViewerId || state.Status is not ("playing" or "resolving")
                || state.Viewer is not { Eliminated: false }) { active = false; return false; }
            if (state.Decision == null && state.DecisionOwnerId != state.ViewerId) return true;
            if (OptionalPaymentFacts.Current(state) is { } facts && Matches(facts)) {
                if (state.ResolutionChoiceVersion == initial.ResolutionChoiceVersion && OptionalPaymentFacts.CanAnswer(facts, mode)) return true;
            }
            active = false; return false;
        }
    }
    public DecisionCommand? NextCommand(GameSnapshot state)
    {
        lock (gate) {
            if (paused || !CanContinue(state) || state.Revision < revision || state.Decision is not { } decision
                || OptionalPaymentFacts.Current(state) is not { } facts || submissions.Contains("step:" + decision.Id)
                || submissions.Contains("declined:" + facts.Offer.OfferId)) return null;
            return mode == OptionalPaymentMode.AutoPay && facts.Support.Availability != OptionalManaPaymentAvailability.Unpayable
                ? new AutoPayOptionalPayment(state.Id, decision.Id, facts.Offer.OfferId) : new CancelPayment(state.Id, decision.Id);
        }
    }
    public void Submitted(GameSnapshot state, DecisionCommand command)
        => submissions.Add(command is CancelPayment ? "declined:" + original.Offer.OfferId : "step:" + command.DecisionId);
}
