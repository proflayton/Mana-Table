using System.Collections.Immutable;
using System.Text.Json;
using Mana.Contracts;

namespace Mana.Table;

// One Forge-public value, independent of the observing seat and decision.
// Control is the player whose zone contains the occurrence. Hidden zones expose
// counts only; concealed exile identities and a face-down permanent's printed
// face are not public facts, even when an owner has a private disclosure.
internal static class ForgePublicProjectionFacts
{
    // Retained cohort evidence contains public stack IDs and admitted historical
    // correlation IDs only, never a private hand, source face or action handle.
    internal sealed record ExecutionCorrelation(string GameId, long BoardRevision, bool Valid,
        ImmutableDictionary<string, string> StackExecutionIds);

    internal static ExecutionCorrelation CaptureExecutionCorrelations(GameSnapshot state)
    {
        var ids = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        ExecutionCorrelation Invalid() => new(state.Id, state.BoardRevision, false, ImmutableDictionary<string, string>.Empty);
        try { ExecutionFrames.Validate(state); }
        catch (ArgumentException) { return Invalid(); }
        foreach (var item in state.Stack) {
            if (item.ExecutionId.Length == 0) continue;
            if (ExecutionFrames.Find(state, item.ExecutionId) == null || item.Id.Length == 0 || ids.ContainsKey(item.Id)) return Invalid();
            // Actor, historical source generation and execution kind need not
            // equal current stack control, visibility or wrapper classification.
            ids.Add(item.Id, item.ExecutionId);
        }
        return new(state.Id, state.BoardRevision, true, ids.ToImmutable());
    }

    internal static bool ExecutionCorrelationsAgree(ExecutionCorrelation first, ExecutionCorrelation second)
        => first.Valid && second.Valid && first.GameId == second.GameId && first.BoardRevision == second.BoardRevision
            && first.StackExecutionIds.All(pair => !second.StackExecutionIds.TryGetValue(pair.Key, out string? other) || other == pair.Value);

    internal static ExecutionCorrelation? MergeExecutionCorrelations(ExecutionCorrelation first, ExecutionCorrelation second)
        => ExecutionCorrelationsAgree(first, second) ? first with { StackExecutionIds = first.StackExecutionIds.SetItems(second.StackExecutionIds) } : null;

    internal static string Capture(GameSnapshot state)
    {
        var publicCards = state.Players.SelectMany(player => player.Zones
            .Where(zone => PublicZone(zone.Name)).SelectMany(zone => zone.Cards
                .Where(card => zone.Name == "Battlefield" || !card.FaceDown)))
            .Concat(state.Stack.Where(item => !item.Ability && item.Card is { FaceDown: false }).Select(item => item.Card!)).ToArray();
        var publicIds = publicCards.SelectMany(card => card.FaceDown
            ? new[] { Occurrence(card) } : new[] { card.CombatId, card.VisualId })
            .Where(id => !string.IsNullOrEmpty(id)).ToHashSet(StringComparer.Ordinal);
        return JsonSerializer.Serialize(new {
            state.Id, state.BoardRevision, state.Turn, state.PhaseKey, state.ActivePlayerId,
            outcome = state.Outcome is not { } outcome ? null : new {
                outcome.GameId, outcome.Kind, outcome.RuleReason,
                winningPlayerIds = outcome.WinningPlayerIds.Order().ToArray(),
                winningTeamIds = outcome.WinningTeamIds.Order().ToArray()
            },
            states = States(state.States),
            players = state.Players.OrderBy(player => player.Id).Select(player => new {
                player.Id, player.TeamId, player.Life, player.Eliminated,
                mana = Numbers(player.Mana), counters = Numbers(player.Counters),
                player.LandPlaysUsed, player.LandPlayLimit, player.MaxHandSize,
                states = player.States.OrderBy(value => value.Id, StringComparer.Ordinal).Select(value => new {
                    value.Id, value.Kind, value.Value, value.Maximum
                }),
                commanderDamage = player.CommanderDamage.OrderBy(value => value.OwnerId)
                    .ThenBy(value => value.Name, StringComparer.Ordinal).Select(value => new {
                        value.OwnerId, value.Name, value.Damage
                    }),
                zones = player.Zones.OrderBy(zone => zone.Name, StringComparer.Ordinal).Select(zone => new {
                    zone.Name, zone.Count, cards = ZoneCards(zone).Select(CardFacts)
                })
            }),
            // Stack order and occurrence are public even when its source
            // portrait is historical, concealed, or no longer on the table.
            // Forge's finer Kind/effect/operation projections depend on source
            // disclosure. Do not compare those or infer a source by its name.
            stack = state.Stack.Select(item => new {
                item.Id, item.ControllerId, item.Ability,
                targets = item.Targets.Where(target => target.Kind != "card" || publicIds.Contains(target.Id))
                    .OrderBy(target => target.Kind, StringComparer.Ordinal)
                    .ThenBy(target => target.Id, StringComparer.Ordinal).ThenBy(target => target.PlayerId)
                    .Select(target => new { target.Kind, target.Id, target.PlayerId })
            }),
            // Options, repair witnesses, declaration/decision IDs and labels
            // belong to an audience's current input, not the shared topology.
            combat = state.Combat is not { } combat ? null : new {
                attackers = combat.Attackers.OrderBy(attack => attack.CardId, StringComparer.Ordinal).Select(attack => new {
                    attack.CardId, attack.DefendingPlayerId, attack.HasDefender, attack.Blocked,
                    defender = attack.Defender is not { } defender ? null : new {
                        defender.Kind, defender.Id, defender.PlayerId
                    },
                    blockers = attack.BlockerIds.Order(StringComparer.Ordinal).ToArray()
                })
            }
        });
    }

    private static bool PublicZone(string name) => name is "Battlefield" or "Graveyard" or "Exile" or "Command";
    private static IEnumerable<Card> ZoneCards(Zone zone)
    {
        if (!PublicZone(zone.Name)) return [];
        var visible = zone.Cards.Where(card => zone.Name == "Battlefield" || !card.FaceDown);
        // Graveyard order can be a rules fact; table placement in the other
        // public zones is not. Never sort a graveyard into a cosmetic order.
        return zone.Name == "Graveyard" ? visible : visible.OrderBy(Occurrence, StringComparer.Ordinal);
    }

    private static string Occurrence(Card card) => !string.IsNullOrEmpty(card.CombatId)
        ? card.CombatId : card.VisualId ?? "";

    private static object CardFacts(Card card) => new {
        id = Occurrence(card), card.FaceDown,
        name = card.FaceDown ? "" : card.Name, type = card.FaceDown ? "" : card.Type,
        manaCost = card.FaceDown ? "" : card.ManaCost,
        piece = card.FaceDown ? "" : card.GamePieceType,
        face = card.FaceDown ? "" : card.ArtFace,
        otherFace = card.FaceDown || card.OtherFace is not { } other ? null : new {
            other.Name, other.Type, other.ManaCost, other.Power, other.Toughness, other.ArtFace
        },
        commanderTax = card.FaceDown ? 0 : card.CommanderTax,
        card.Power, card.Toughness, card.Damage, card.Tapped, card.PhasedOut, card.Sick,
        card.Attacking, card.Blocking, card.DefenderId, card.BlockingCapacity,
        keywords = card.CombatKeywords.Order(StringComparer.Ordinal).ToArray(),
        counters = Numbers(card.Counters), states = States(card.States),
        attachedTo = card.AttachedTo?.Id,
        attachments = card.Attachments.Select(value => value.Id).Order(StringComparer.Ordinal).ToArray()
    };

    private static object Numbers(ImmutableDictionary<string, int> values) => values
        .OrderBy(value => value.Key, StringComparer.Ordinal).Select(value => new { value.Key, value.Value });

    private static object? States(ImmutableArray<PublicState>? values) => values?.OrderBy(value => value.Id, StringComparer.Ordinal)
        .Select(value => new { value.Id, value.Kind, value.Key, value.Value, value.Maximum });
}
