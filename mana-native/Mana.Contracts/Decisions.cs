using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Mana.Contracts;

// The payload's closed runtime type is the task. There is no independently
// writable classifier or forwarding collection of task-specific fields.
public sealed record Decision
{
    private string id = "";
    public string Id { get => id; init => id = DecisionValue.Required(value, nameof(Id)); }
    private DecisionContext context = null!;
    private DecisionPayload payload = null!;
    public DecisionContext Context { get => context; init => context = value ?? throw new ArgumentNullException(nameof(Context)); }
    public DecisionPayload Payload { get => payload; init => payload = value ?? throw new ArgumentNullException(nameof(Payload)); }
    [JsonConstructor]
    public Decision(string id, DecisionContext context, DecisionPayload payload)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id)); Context = context; Payload = payload;
    }
}
public sealed record DecisionContext(string Message = "", string Title = "", DecisionSource? Source = null)
{
    private string message = DecisionValue.Text(Message), title = DecisionValue.Text(Title);
    public string Message { get => message; init => message = DecisionValue.Text(value); }
    public string Title { get => title; init => title = DecisionValue.Text(value); }
}
public sealed record DecisionSource(string CardId = "", Card? Card = null, int? ControllerId = null, string Zone = "")
{
    private string cardId = DecisionValue.Text(CardId), zone = DecisionValue.Text(Zone);
    public string CardId { get => cardId; init => cardId = DecisionValue.Text(value); }
    public string Zone { get => zone; init => zone = DecisionValue.Text(value); }
}
public sealed record DecisionReply(string Label, bool Enabled)
{
    private string label = DecisionValue.Required(Label, nameof(Label));
    public string Label { get => label; init => label = DecisionValue.Required(value, nameof(Label)); }
}
public enum PriorityAlternative { UndoLastAction, PassTurn }
public sealed record PriorityReply(PriorityAlternative Kind, DecisionReply Reply)
{
    private PriorityAlternative kind = ValidateKind(Kind);
    private DecisionReply reply = DecisionValue.Required(Reply, nameof(Reply));
    public PriorityAlternative Kind { get => kind; init => kind = ValidateKind(value); }
    public DecisionReply Reply { get => reply; init => reply = DecisionValue.Required(value, nameof(Reply)); }
    private static PriorityAlternative ValidateKind(PriorityAlternative value) => Enum.IsDefined(value)
        ? value : throw new ArgumentOutOfRangeException(nameof(value));
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", IgnoreUnrecognizedTypeDiscriminators = false)]
[JsonDerivedType(typeof(PriorityPayload), "priority")]
[JsonDerivedType(typeof(OpeningHandPayload), "openingHand")]
[JsonDerivedType(typeof(PaymentPayload), "payment")]
[JsonDerivedType(typeof(AttackPayload), "attack")]
[JsonDerivedType(typeof(BlockPayload), "block")]
[JsonDerivedType(typeof(TargetSelectionPayload), "targets")]
[JsonDerivedType(typeof(EntitySelectionPayload), "entities")]
[JsonDerivedType(typeof(ConfirmationPayload), "confirmation")]
[JsonDerivedType(typeof(AbilityChoicePayload), "abilities")]
[JsonDerivedType(typeof(OptionsPayload), "options")]
[JsonDerivedType(typeof(OrderingPayload), "ordering")]
[JsonDerivedType(typeof(RevealPayload), "reveal")]
[JsonDerivedType(typeof(NumberPayload), "number")]
[JsonDerivedType(typeof(TextPayload), "text")]
[JsonDerivedType(typeof(AllocationPayload), "allocation")]
public abstract record DecisionPayload
{
    private protected DecisionPayload() { }
    // Record copies must preserve their sealed variant. The generated protected
    // copy constructor would otherwise let another assembly introduce a variant.
    protected DecisionPayload(DecisionPayload original)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (GetType() != original.GetType()) throw new ArgumentException("Decision payload copies must preserve their variant.", nameof(original));
    }
}

public sealed record PriorityPayload(DecisionReply Pass, PriorityReply? Alternative = null,
    bool CanAutoPass = false, string QuietYieldToken = "", ImmutableArray<PriorityAbility> Abilities = default) : DecisionPayload
{
    private DecisionReply pass = DecisionValue.Required(Pass, nameof(Pass));
    private string quietYieldToken = DecisionValue.Text(QuietYieldToken);
    private ImmutableArray<PriorityAbility> abilities = DecisionValue.Items(Abilities);
    public DecisionReply Pass { get => pass; init => pass = DecisionValue.Required(value, nameof(Pass)); }
    public string QuietYieldToken { get => quietYieldToken; init => quietYieldToken = DecisionValue.Text(value); }
    public ImmutableArray<PriorityAbility> Abilities { get => abilities; init => abilities = DecisionValue.Items(value); }
}
public sealed record OpeningHandPayload(DecisionReply Keep, DecisionReply Mulligan, OpeningHandContext? Consequence = null) : DecisionPayload
{
    private DecisionReply keep = DecisionValue.Required(Keep, nameof(Keep)), mulligan = DecisionValue.Required(Mulligan, nameof(Mulligan));
    public DecisionReply Keep { get => keep; init => keep = DecisionValue.Required(value, nameof(Keep)); }
    public DecisionReply Mulligan { get => mulligan; init => mulligan = DecisionValue.Required(value, nameof(Mulligan)); }
}
public sealed record PaymentPayload(DecisionReply Pay, DecisionReply Cancel) : DecisionPayload
{
    private DecisionReply pay = DecisionValue.Required(Pay, nameof(Pay)), cancel = DecisionValue.Required(Cancel, nameof(Cancel));
    public DecisionReply Pay { get => pay; init => pay = DecisionValue.Required(value, nameof(Pay)); }
    public DecisionReply Cancel { get => cancel; init => cancel = DecisionValue.Required(value, nameof(Cancel)); }
    // Exact remaining bill and read-only resource applicability for this input.
    // Missing facts from an older provider do not mean the bill is zero.
    // Legacy expanded wire support. Current providers publish counted parts.
    public ImmutableArray<string> UnpaidMana { get; init; } = [];
    public ImmutableArray<ManaBillPart> UnpaidManaParts { get; init; } = [];
    public ManaFundingContext? FundingContext { get; init; }
    public ImmutableArray<string>? UsablePoolManaColors { get; init; }
    public int? PayerId { get; init; }
}
public sealed record AttackPayload(DecisionReply Complete) : DecisionPayload
{
    private DecisionReply complete = DecisionValue.Required(Complete, nameof(Complete));
    public DecisionReply Complete { get => complete; init => complete = DecisionValue.Required(value, nameof(Complete)); }
}
public sealed record BlockPayload(DecisionReply Complete) : DecisionPayload
{
    private DecisionReply complete = DecisionValue.Required(Complete, nameof(Complete));
    public DecisionReply Complete { get => complete; init => complete = DecisionValue.Required(value, nameof(Complete)); }
}
public sealed record TargetSelectionPayload(DecisionReply Complete, DecisionReply Cancel, SelectionRange Range,
    ImmutableArray<int> Players, PendingEffect? Effect = null, LibrarySearchTarget? Search = null) : DecisionPayload
{
    private DecisionReply complete = DecisionValue.Required(Complete, nameof(Complete)), cancel = DecisionValue.Required(Cancel, nameof(Cancel));
    public DecisionReply Complete { get => complete; init => complete = DecisionValue.Required(value, nameof(Complete)); }
    public DecisionReply Cancel { get => cancel; init => cancel = DecisionValue.Required(value, nameof(Cancel)); }
    private ImmutableArray<int> players = Players.IsDefault ? [] : Players;
    public ImmutableArray<int> Players { get => players; init => players = value.IsDefault ? [] : value; }
}
public sealed record EntitySelectionPayload(DecisionReply Complete, DecisionReply Cancel,
    ImmutableArray<int> Players, SelectionRange? Range = null, CounterPaymentContext? CounterPayment = null) : DecisionPayload
{
    private DecisionReply complete = DecisionValue.Required(Complete, nameof(Complete)), cancel = DecisionValue.Required(Cancel, nameof(Cancel));
    public DecisionReply Complete { get => complete; init => complete = DecisionValue.Required(value, nameof(Complete)); }
    public DecisionReply Cancel { get => cancel; init => cancel = DecisionValue.Required(value, nameof(Cancel)); }
    private ImmutableArray<int> players = Players.IsDefault ? [] : Players;
    public ImmutableArray<int> Players { get => players; init => players = value.IsDefault ? [] : value; }
    public CounterPaymentContext? CounterPayment { get; } = CounterPayment;
}
// An absent Decline is an acknowledgment, not an optional effect whose No
// button happens to be disabled. Commands preserve that distinction.
public sealed record ConfirmationPayload(DecisionReply Accept, DecisionReply? Decline = null, LibrarySearchOffer? Search = null) : DecisionPayload
{
    private DecisionReply accept = DecisionValue.Required(Accept, nameof(Accept));
    public DecisionReply Accept { get => accept; init => accept = DecisionValue.Required(value, nameof(Accept)); }
}
public sealed record OptionsPayload(ChoiceSelection Selection, CounterPaymentChoice? CounterPayment = null) : DecisionPayload
{
    private ChoiceSelection selection = ValidateSelection(Selection, CounterPayment);
    private ManaColorPaymentContext? manaPaymentContext;
    public ChoiceSelection Selection { get => selection; init {
        var validated = ValidateSelection(value, CounterPayment);
        ValidateManaChoice(validated, manaPaymentContext, CounterPayment);
        selection = validated;
    } }
    public CounterPaymentChoice? CounterPayment { get; } = CounterPayment;
    public ManaColorPaymentContext? ManaPaymentContext { get => manaPaymentContext; init {
        ValidateManaChoice(selection, value, CounterPayment); manaPaymentContext = value;
    } }
    private static void ValidateManaChoice(ChoiceSelection selection, ManaColorPaymentContext? context, CounterPaymentChoice? counters)
    {
        if (context != null && (counters != null || selection.Range is not { Min: 1, Max: 1 }
            || !selection.Disclosures.IsEmpty || selection.Choices.IsEmpty || selection.Choices.Length > 6
            || selection.Choices.Select(choice => choice.Mana).Distinct(StringComparer.Ordinal).Count() != selection.Choices.Length
            || selection.Choices.Any(choice => choice.Card != null
                || choice.CardId.Length > 0 || choice.Mana is not ("{W}" or "{U}" or "{B}" or "{R}" or "{G}" or "{C}"))))
            throw new ArgumentException("An owned mana-color choice describes exactly one of its current offered colors.", nameof(context));
    }
    private static ChoiceSelection ValidateSelection(ChoiceSelection value, CounterPaymentChoice? payment)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (payment != null && (value.Range is not { Min: 1, Max: 1 } || !value.Disclosures.IsEmpty
            || !value.Choices.Select(choice => (choice.Index, choice.Label)).SequenceEqual(payment.Options.Select(option => (option.Index, option.Label)))))
            throw new ArgumentException("Counter payment descriptions must name the exact ordinary offered choices.", nameof(payment));
        return value;
    }
}

/// <summary>Authoritative non-mana payment progress; it grants no selection permission.</summary>
public sealed record CounterPaymentContext
{
    public int Amount { get; }
    public int Remaining { get; }
    [JsonConstructor]
    public CounterPaymentContext(int amount, int remaining)
    {
        if (amount <= 0 || remaining < 0 || remaining > amount) throw new ArgumentOutOfRangeException(nameof(remaining));
        Amount = amount; Remaining = remaining;
    }
}

public sealed record CounterPaymentOption
{
    public int Index { get; }
    public string Type { get; }
    public string Label { get; }
    public int Available { get; }
    [JsonConstructor]
    public CounterPaymentOption(int index, string type, string label, int available)
    {
        if (index < 0 || available <= 0) throw new ArgumentOutOfRangeException(nameof(available));
        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(label)) throw new ArgumentException("A counter choice needs its type and label.");
        Index = index; Type = type; Label = label; Available = available;
    }
}

/// <summary>The current donor and exact debit for this one offered counter-type choice.</summary>
public sealed record CounterPaymentChoice
{
    public int Amount { get; }
    public DecisionSource Permanent { get; }
    public ImmutableArray<CounterPaymentOption> Options { get; }
    [JsonConstructor]
    public CounterPaymentChoice(int amount, DecisionSource permanent, ImmutableArray<CounterPaymentOption> options)
    {
        ArgumentNullException.ThrowIfNull(permanent);
        Options = DecisionValue.Items(options);
        if (amount <= 0 || Options.IsEmpty || Options.Any(option => option.Available < amount)
            || Options.Select(option => option.Index).Distinct().Count() != Options.Length)
            throw new ArgumentException("A counter payment requires a positive debit and unique offered indices.");
        if (permanent.Card is not { Key.Length: 0 } card || permanent.Zone != "Battlefield"
            || (card.FaceDown ? !string.IsNullOrEmpty(card.VisualId) || string.IsNullOrWhiteSpace(card.CombatId) || permanent.CardId != card.CombatId
                : string.IsNullOrWhiteSpace(card.VisualId) || permanent.CardId != card.VisualId))
            throw new ArgumentException("A counter payment donor requires a keyless disclosed battlefield occurrence.", nameof(permanent));
        if (card.FaceDown && (card.Name is not ("" or "Face-down card" or "Face-down or hidden card")
            || !string.IsNullOrEmpty(card.Text) || !string.IsNullOrEmpty(card.ManaCost) || !string.IsNullOrEmpty(card.ArtName) || card.OtherFace != null
            || !string.IsNullOrEmpty(card.ArtSet) || !string.IsNullOrEmpty(card.Edition) || !string.IsNullOrEmpty(card.CollectorNumber)))
            throw new ArgumentException("An anonymous battlefield donor cannot disclose its printed face.", nameof(permanent));
        Amount = amount; Permanent = permanent;
    }
}
public sealed record OrderingPayload(ChoiceSelection Selection) : DecisionPayload
{
    private ChoiceSelection selection = DecisionValue.Required(Selection, nameof(Selection));
    public ChoiceSelection Selection { get => selection; init => selection = DecisionValue.Required(value, nameof(Selection)); }
}
public sealed record AbilityChoicePayload : DecisionPayload
{
    public ChoiceSelection Selection { get; }
    [JsonConstructor]
    public AbilityChoicePayload(ChoiceSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.Range.Max != 1 || selection.Range.Min is < 0 or > 1 || selection.Choices.IsEmpty || !selection.Disclosures.IsEmpty)
            throw new ArgumentException("An ability choice requires one offered ability and no library disclosure.");
        Selection = selection;
    }
}
public sealed record RevealPayload(ImmutableArray<LibraryChoice> Cards) : DecisionPayload
{
    private ImmutableArray<LibraryChoice> cards = DecisionValue.Items(Cards);
    public ImmutableArray<LibraryChoice> Cards { get => cards; init => cards = DecisionValue.Items(value); }
}
// A null range is an integer textbox without a provider-imposed bound.
public sealed record NumberPayload(string Initial, NumberRange? Range = null) : DecisionPayload
{
    private string initial = DecisionValue.Text(Initial);
    public string Initial { get => initial; init => initial = DecisionValue.Text(value); }
}
public sealed record TextPayload(string Initial) : DecisionPayload
{
    private string initial = DecisionValue.Text(Initial);
    public string Initial { get => initial; init => initial = DecisionValue.Text(value); }
}

public readonly record struct SelectionRange
{
    public int Min { get; }
    public int Max { get; }
    [JsonConstructor]
    public SelectionRange(int min, int max)
    {
        if (min < 0 || max < min) throw new ArgumentOutOfRangeException(nameof(min), "Invalid selection bounds.");
        Min = min; Max = max;
    }
}
public readonly record struct NumberRange
{
    public int Min { get; }
    public int Max { get; }
    [JsonConstructor]
    public NumberRange(int min, int max)
    {
        if (max < min) throw new ArgumentOutOfRangeException(nameof(min), "Invalid number bounds.");
        Min = min; Max = max;
    }
}
public sealed record ChoiceSelection
{
    public ImmutableArray<Choice> Choices { get; }
    public SelectionRange Range { get; }
    // A face without an index is readable only. Disclosures never grant handles.
    public ImmutableArray<LibraryChoice> Disclosures { get; }
    [JsonConstructor]
    public ChoiceSelection(ImmutableArray<Choice> choices, SelectionRange range, ImmutableArray<LibraryChoice> disclosures = default)
    {
        Choices = DecisionValue.Items(choices);
        Disclosures = DecisionValue.Items(disclosures);
        if (range.Max > Choices.Length || Choices.Any(choice => choice.Index < 0)
            || Choices.Select(choice => choice.Index).Distinct().Count() != Choices.Length
            || Disclosures.Any(face => face.Index is { } index && !Choices.Any(choice => choice.Index == index)))
            throw new ArgumentException("Choice bounds and disclosure handles must name the offered options.");
        Range = range;
    }
}

public sealed record LibrarySearchTarget(string Destination, string CardName)
{
    private string destination = DecisionValue.Required(Destination, nameof(Destination)), cardName = DecisionValue.Text(CardName);
    public string Destination { get => destination; init => destination = DecisionValue.Required(value, nameof(Destination)); }
    public string CardName { get => cardName; init => cardName = DecisionValue.Text(value); }
}
public sealed record LibrarySearchOffer(int LibraryOwnerId, string Destination, string CardName)
{
    private string destination = DecisionValue.Required(Destination, nameof(Destination)), cardName = DecisionValue.Text(CardName);
    public string Destination { get => destination; init => destination = DecisionValue.Required(value, nameof(Destination)); }
    public string CardName { get => cardName; init => cardName = DecisionValue.Text(value); }
}
// Current hand size is already effective: Forge bottoms London cards before
// offering Keep again. Remaining values describe accepting the next redraw.
public sealed record OpeningHandContext(int HandSize, int NextHandSize, int CardsToReturn, bool FreeMulligan);

public enum AllocationKind { Generic, Combat }
public sealed record AllocationRecipient
{
    public int Index { get; }
    public string Id { get; }
    public string Label { get; }
    public Card? Card { get; }
    public string CardId { get; }
    public int? PlayerId { get; }
    public int? ControllerId { get; }
    public int? OwnerId { get; }
    public int Minimum { get; }
    public int Maximum { get; }
    public int? Lethal { get; }
    public bool IsDefender { get; }
    [JsonConstructor]
    public AllocationRecipient(int index, string id, string label, Card? card, string cardId, int? playerId,
        int? controllerId, int? ownerId, int minimum, int maximum, int? lethal = null, bool isDefender = false)
    {
        if (index < 0 || minimum < 0 || maximum < minimum || lethal < 0) throw new ArgumentOutOfRangeException(nameof(minimum));
        Index = index; Id = DecisionValue.Text(id); Label = DecisionValue.Text(label); Card = card; CardId = DecisionValue.Text(cardId); PlayerId = playerId;
        ControllerId = controllerId; OwnerId = ownerId; Minimum = minimum; Maximum = maximum; Lethal = lethal; IsDefender = isDefender;
    }
}
public sealed record AllocationPayload : DecisionPayload
{
    public int Amount { get; }
    public AllocationKind Kind { get; }
    public string AmountLabel { get; }
    public bool MaySkip { get; }
    public ImmutableArray<AllocationRecipient> Recipients { get; }
    [JsonConstructor]
    public AllocationPayload(int amount, AllocationKind kind, string amountLabel, bool maySkip, ImmutableArray<AllocationRecipient> recipients)
    {
        Recipients = DecisionValue.Items(recipients);
        if (amount < 0 || !Enum.IsDefined(kind) || Recipients.Any(recipient => recipient.Maximum > amount)
            || Recipients.Select(recipient => recipient.Index).Distinct().Count() != Recipients.Length)
            throw new ArgumentException("Invalid allocation constraints.");
        Amount = amount; Kind = kind; AmountLabel = DecisionValue.Text(amountLabel); MaySkip = maySkip;
    }
}

internal static class DecisionValue
{
    internal static T Required<T>(T? value, string name) where T : class => value ?? throw new ArgumentNullException(name);
    internal static string Text(string? value) => value ?? "";
    internal static ImmutableArray<T> Items<T>(ImmutableArray<T> values)
    {
        if (values.IsDefault) return [];
        if (values.Any(value => value is null)) throw new ArgumentException("Decision items cannot be null.", nameof(values));
        return values;
    }
}
