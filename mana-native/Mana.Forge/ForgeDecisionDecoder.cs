using System.Collections.Immutable;
using System.Text.Json;
using Mana.Contracts;

namespace Mana.Forge;

// The wire discriminants belong to this provider. Construct one closed native
// payload directly; no mutable JSON or shared record deserializer classifies it.
internal static class ForgeDecisionDecoder
{
    internal static Decision Decode(JsonElement prompt, IReadOnlySet<int>? players = null)
    {
        try {
            Object(prompt);
            string id = Text(prompt, "id", required: true), task = Text(prompt, "task", required: true);
            string kind = Text(prompt, "kind", required: true), purpose = Text(prompt, "purpose");
            if (string.IsNullOrWhiteSpace(id)) throw Invalid();
            bool ordered = Flag(prompt, "ordered"), numeric = Flag(prompt, "numeric");
            bool input = task is "priority" or "mana" or "attack" or "block" or "selection" or "openingHand" or "confirmation";
            bool dialog = task is "choice" or "reveal" or "number" or "text" or "allocate";
            if (!input && !dialog || input && kind != "input" || dialog && kind != task
                && !(task == "number" && kind == "text" && numeric) || ordered && task != "choice"
                || numeric && task != "number" || purpose is not ("" or "openingHand" or "targets" or "abilities")
                || (task == "openingHand") != (purpose == "openingHand") || purpose == "targets" && task != "selection"
                || purpose == "abilities" && (task != "choice" || ordered)) throw Invalid();
            if (Present(prompt, "librarySearch") && task != "confirmation" && !(task == "selection" && purpose == "targets")
                || Present(prompt, "openingHand") && task != "openingHand"
                || Present(prompt, "effect") && !(task == "selection" && purpose == "targets")) throw Invalid();
            string alternative = Text(prompt, "priorityAlternative"), quiet = Text(prompt, "quietYieldToken");
            if (alternative is not ("" or "none" or "undo" or "passTurn")
                || task != "priority" && (alternative is "undo" or "passTurn" || quiet.Length != 0 || Flag(prompt, "canAutoPass"))) throw Invalid();
            if (task != "priority" && Array(prompt, "priorityAbilities").Length != 0) throw Invalid();
            var context = SourceContext(prompt);
            DecisionReply Ok() => new(Text(prompt, "ok", fallback: "Confirm"), Flag(prompt, "okEnabled"));
            DecisionReply Cancel() => new(Text(prompt, "cancel", fallback: "Cancel"), Flag(prompt, "cancelEnabled"));
            DecisionPayload payload = task switch {
                "priority" => new PriorityPayload(Ok(), alternative switch {
                    "undo" => new(PriorityAlternative.UndoLastAction, Cancel()),
                    "passTurn" => new(PriorityAlternative.PassTurn, Cancel()), _ => null
                }, Flag(prompt, "canAutoPass"), quiet, PriorityAbilities(prompt)),
                "openingHand" => new OpeningHandPayload(Ok(), Cancel(), Opening(prompt)),
                "mana" => new PaymentPayload(Ok(), Cancel()),
                "attack" => new AttackPayload(Ok()), "block" => new BlockPayload(Ok()),
                "selection" when purpose == "targets" => new TargetSelectionPayload(Ok(), Cancel(), Range(prompt),
                    PlayerChoices(prompt, players), Optional<PendingEffect>(prompt, "effect"), SearchTarget(prompt)),
                "selection" => new EntitySelectionPayload(Ok(), Cancel(), PlayerChoices(prompt, players), OptionalRange(prompt)),
                "confirmation" => new ConfirmationPayload(Ok(), Flag(prompt, "cancelEnabled") ? Cancel() : null, SearchOffer(prompt, players)),
                "choice" when purpose == "abilities" => new AbilityChoicePayload(Selection(prompt)),
                "choice" when ordered => new OrderingPayload(Selection(prompt)),
                "choice" => new OptionsPayload(Selection(prompt)),
                "reveal" => new RevealPayload(Reveal(prompt)),
                "number" => new NumberPayload(Text(prompt, "initial"), kind == "text" ? OptionalNumberRange(prompt) : NumberRange(prompt)),
                "text" => new TextPayload(Text(prompt, "initial")),
                "allocate" => Allocation(prompt), _ => throw Invalid()
            };
            return new(id, context, payload);
        } catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException) {
            throw new IOException("The rules provider published an invalid decision schema.", error);
        }
    }

    private static DecisionContext SourceContext(JsonElement prompt)
    {
        var card = ReadCard(prompt, "sourceCard");
        string id = Text(prompt, "sourceCardId"), zone = Text(prompt, "sourceZone");
        int? controller = NullableInteger(prompt, "sourceControllerId");
        if (card is { Key.Length: > 0 }) throw Invalid();
        var source = card != null || id.Length != 0 || zone.Length != 0 || controller != null ? new DecisionSource(id, card, controller, zone) : null;
        return new(Text(prompt, "message"), Text(prompt, "title"), source);
    }

    private static OpeningHandContext? Opening(JsonElement prompt)
    {
        if (!Present(prompt, "openingHand")) return null;
        var value = Field(prompt, "openingHand"); Object(value);
        int hand = Integer(value, "handSize"), next = Integer(value, "nextHandSize"), bottom = Integer(value, "cardsToReturn");
        bool free = Flag(value, "freeMulligan", required: true);
        if (hand < 0 || next < 0 || next > hand || bottom < 0 || free && (bottom != 0 || next != hand)) throw Invalid();
        return new(hand, next, bottom, free);
    }
    private static LibrarySearchTarget? SearchTarget(JsonElement prompt)
    {
        if (!Present(prompt, "librarySearch")) return null;
        var search = Field(prompt, "librarySearch"); Object(search);
        if (!search.TryGetProperty("libraryOwnerId", out var owner) || owner.ValueKind != JsonValueKind.Null) throw Invalid();
        return new(Destination(search), Text(search, "cardName", required: true));
    }
    private static LibrarySearchOffer? SearchOffer(JsonElement prompt, IReadOnlySet<int>? players)
    {
        if (!Present(prompt, "librarySearch")) return null;
        var search = Field(prompt, "librarySearch"); Object(search);
        int owner = Integer(search, "libraryOwnerId");
        if (players == null || !players.Contains(owner)) throw Invalid();
        return new(owner, Destination(search), Text(search, "cardName", required: true));
    }
    private static string Destination(JsonElement search)
    {
        string destination = Text(search, "destination", required: true);
        return string.IsNullOrWhiteSpace(destination) ? throw Invalid() : destination;
    }
    private static ImmutableArray<int> PlayerChoices(JsonElement prompt, IReadOnlySet<int>? players)
    {
        var result = Array(prompt, "playerChoices").Select(value => value.TryGetInt32(out int id) ? id : throw Invalid()).ToImmutableArray();
        if (result.Distinct().Count() != result.Length || result.Any(id => players == null || !players.Contains(id))) throw Invalid();
        return result;
    }
    private static SelectionRange Range(JsonElement prompt) => new(Integer(prompt, "min"), Integer(prompt, "max"));
    private static SelectionRange? OptionalRange(JsonElement prompt) => Present(prompt, "min") || Present(prompt, "max") ? Range(prompt) : null;
    private static NumberRange NumberRange(JsonElement prompt) => new(Integer(prompt, "min"), Integer(prompt, "max"));
    private static NumberRange? OptionalNumberRange(JsonElement prompt) => Present(prompt, "min") || Present(prompt, "max") ? NumberRange(prompt) : null;

    private static ChoiceSelection Selection(JsonElement prompt)
    {
        var choices = Array(prompt, "choices", required: true).Select(value => {
            Object(value);
            var card = ReadCard(value, "card");
            if (card is { Key.Length: > 0 }) throw Invalid();
            return new Choice(Integer(value, "index"), Text(value, "label", required: true), card) {
                CardId = Text(value, "cardId"), Detail = Text(value, "detail"), Mana = Text(value, "mana")
            };
        }).ToImmutableArray();
        return new(choices, Range(prompt), Disclosures(prompt));
    }
    private static ImmutableArray<LibraryChoice> Disclosures(JsonElement prompt) => Array(prompt, "libraryCards").Select(value => {
        Object(value);
        var card = ReadCard(value, "card") ?? throw Invalid();
        if (card.Key.Length != 0 || card.VisualId.Length != 0 || card.CombatId.Length != 0) throw Invalid();
        return new LibraryChoice(NullableInteger(value, "index"), Text(value, "label", required: true), card);
    }).ToImmutableArray();
    private static ImmutableArray<LibraryChoice> Reveal(JsonElement prompt)
    {
        var disclosed = Disclosures(prompt);
        if (disclosed.Any(card => card.Index != null)) throw Invalid();
        var ordinary = Array(prompt, "choices").Select(value => {
            Object(value);
            var card = ReadCard(value, "card") ?? throw Invalid();
            if (card.Key.Length != 0) throw Invalid();
            return new LibraryChoice(null, Text(value, "label", required: true), card) { CardId = Text(value, "cardId") };
        }).ToImmutableArray();
        return ordinary.AddRange(disclosed);
    }

    private static AllocationPayload Allocation(JsonElement prompt)
    {
        int amount = Integer(prompt, "amount"), baseMinimum = Flag(prompt, "atLeastOne") ? 1 : 0;
        if (amount < 0) throw Invalid();
        var limits = Array(prompt, "limits").Select(value => value.TryGetInt32(out int limit) && limit >= 0 ? limit : throw Invalid()).ToImmutableArray();
        var values = Array(prompt, "choices", required: true);
        if (limits.Length != 0 && limits.Length != values.Length) throw Invalid();
        var recipients = values.Select((value, position) => {
            Object(value);
            int index = Integer(value, "index");
            if (index != position) throw Invalid(); // Amount arrays use engine positions, not presentation order.
            int? suppliedMinimum = NullableInteger(value, "minimum"), suppliedMaximum = NullableInteger(value, "maximum");
            if (suppliedMinimum < 0 || suppliedMaximum < 0) throw Invalid();
            int minimum = suppliedMinimum ?? baseMinimum;
            int maximum = Math.Min(amount, suppliedMaximum ?? (position < limits.Length ? limits[position] : amount));
            var card = ReadCard(value, "card");
            if (card is { Key.Length: > 0 }) throw Invalid();
            return new AllocationRecipient(index, Text(value, "id"), Text(value, "label", required: true), card,
                Text(value, "cardId"), NullableInteger(value, "playerId"), NullableInteger(value, "controllerId"), NullableInteger(value, "ownerId"),
                minimum, maximum, NullableInteger(value, "lethal"), Flag(value, "isDefender"));
        }).ToImmutableArray();
        var kind = Text(prompt, "allocationKind") switch {
            "combat" => AllocationKind.Combat, "generic" or "" => AllocationKind.Generic, _ => throw Invalid()
        };
        return new(amount, kind, Text(prompt, "amountLabel"), Flag(prompt, "maySkip"), recipients);
    }

    private static ImmutableArray<PriorityAbility> PriorityAbilities(JsonElement prompt)
    {
        var abilities = ReadArray<PriorityAbility>(prompt, "priorityAbilities");
        if (abilities.Any(ability => ability == null || string.IsNullOrWhiteSpace(ability.Id)
                || ability.SourceCardKey == null || ability.SourceName == null || ability.Text == null
                || ability.EligibleHandKeys.IsDefault || ability.HandPlacementLimit < 0
                || ability.EligibleHandKeys.Any(string.IsNullOrWhiteSpace))
                || abilities.Select(ability => ability.Id).Distinct(StringComparer.Ordinal).Count() != abilities.Length) throw Invalid();
        return abilities;
    }
    private static Card? ReadCard(JsonElement node, string key)
    {
        var card = Optional<Card>(node, key);
        if (card != null && (card.Key == null || card.VisualId == null || card.CombatId == null || card.Name == null)) throw Invalid();
        return card;
    }
    private static T? Optional<T>(JsonElement node, string key) where T : class => !Present(node, key) ? null
        : Field(node, key).Deserialize<T>(ForgeSnapshotDecoder.Json) ?? throw Invalid();
    private static ImmutableArray<T> ReadArray<T>(JsonElement node, string key) => Array(node, key).Select(value =>
        value.Deserialize<T>(ForgeSnapshotDecoder.Json) ?? throw Invalid()).ToImmutableArray();
    private static JsonElement Field(JsonElement node, string key) => node.TryGetProperty(key, out var value) ? value : default;
    private static bool Present(JsonElement node, string key) => Field(node, key).ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
    private static ImmutableArray<JsonElement> Array(JsonElement node, string key, bool required = false)
    {
        var field = Field(node, key);
        if (field.ValueKind == JsonValueKind.Undefined && !required) return [];
        return field.ValueKind == JsonValueKind.Array ? field.EnumerateArray().ToImmutableArray() : throw Invalid();
    }
    private static string Text(JsonElement node, string key, string fallback = "", bool required = false)
    {
        var value = Field(node, key);
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined && !required) return fallback;
        return value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Invalid();
    }
    private static bool Flag(JsonElement node, string key, bool required = false)
    {
        var value = Field(node, key);
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined && !required) return false;
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw Invalid();
    }
    private static int Integer(JsonElement node, string key) => Field(node, key).TryGetInt32(out int number) ? number : throw Invalid();
    private static int? NullableInteger(JsonElement node, string key) => Present(node, key) ? Integer(node, key) : null;
    private static void Object(JsonElement value) { if (value.ValueKind != JsonValueKind.Object) throw Invalid(); }
    private static IOException Invalid() => new("The rules provider published an unsupported or contradictory decision schema.");
}
