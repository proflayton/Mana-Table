using System.Collections.Immutable;
using Mana.AI;
using Mana.Contracts;

internal static partial class AiSwarmCombatRegression
{
    private static async Task TypedNominalReturnCoverage(Action<bool, string> check)
    {
        // Authored public arithmetic from the retained initial T35 scope; no
        // private hand, history or future engine state is embedded in this test.
        var frame = NominalReturnFrame();
        var tokens = frame.State.Viewer!.Zone("Battlefield").Cards.Where(card => card.Power == 2).ToArray();
        check(frame.State.Combat!.Attackers.IsEmpty && tokens.Length == 6
                && tokens.All(card => frame.Allows(new ToggleAttackPlayer(frame.State.Id, frame.State.Decision!.Id, card.Key, 1))),
            "The public T35 regression starts before any toggle with six distinct current vigilance-token offers");
        var result = await Evaluate(frame);
        Console.WriteLine($"Typed return witness: command={result.Proposal?.GetType().Name}; reason={result.Evidence?.ReasonCode}; "
            + $"remainingWork={result.Evidence?.RemainingWork}; offers={frame.State.Combat.AttackOptions.Length}; "
            + "actorLife=57; targetLife=6; blockers=3; prowess+defender=retained");
        check(result.Proposal is ToggleAttackPlayer { Player: 1 } && frame.Allows(result.Proposal)
                && result.Evidence?.ReasonCode == "combat-stage-swarm-finish",
            "Typed nominal return coverage starts the six-body elimination despite a remaining prowess source and zero-power defender");
    }

    private static AiDecisionFrame NominalReturnFrame(int revision = 1, List<string>? assigned = null)
    {
        var frame = FfaFrame(3, [], powers: [2, 2, 2, 2, 2, 2, 1, 1, 1], blockers: 3,
            enemyLife: 6, ownLife: 57, revision: revision, assigned: assigned);
        var rows = ImmutableArray.Create(
            new CommanderDamage("Actor lineage", 0, "Actor", 0) { CommanderId = "actor-rhys" },
            new CommanderDamage("Removed lineage", 1, "Target", 0) { CommanderId = "target-pir" },
            new CommanderDamage("Removed partner", 1, "Target", 0) { CommanderId = "target-toothy" },
            new CommanderDamage("Remaining lineage", 2, "Remaining", 1) { CommanderId = "return-balmor" });
        var changed = frame.State with { Turn = 35, CommanderCombat = new(21), Players = frame.State.Players.Select(player => player.Id switch {
            0 => player with { CommanderDamage = rows, Zones = player.Zones.Select(zone => zone with {
                Cards = zone.Cards.Select((card, index) => index < 6 ? Trait(card with { GamePieceType = "token" }, "vigilance")
                    : index == 7 ? card with { CommanderId = "actor-rhys" } : card).ToImmutableArray()
            }).ToImmutableArray() },
            1 => player with { Zones = player.Zones.Select(zone => zone with { Cards = zone.Cards.Select((card, index) => card with {
                Power = index == 1 ? 2 : 3, Toughness = index == 1 ? 2 : 3, Sick = index == 2,
                CommanderId = index == 0 ? "target-pir" : "", BlockingCapacity = new(1)
            }).ToImmutableArray() }).ToImmutableArray() },
            2 => player with { Zones = [new("Battlefield", 6, [
                Trait(Creature("return-prowess", 1, revision) with { Toughness = 2 }, "haste", "prowess"),
                Trait(Creature("return-defender", 0, revision) with { Toughness = 3, Tapped = true }, "defender"),
                Trait(Creature("return-balmor", 1, revision) with { Toughness = 3, Tapped = true, CommanderId = "return-balmor" }, "flying"),
                Creature("return-mystic", 1, revision) with { Toughness = 5 },
                Creature("return-chronarch", 2, revision) with { Sick = true },
                Creature("return-baral", 1, revision) with { Toughness = 3, Sick = true }
            ], null)] },
            _ => player
        }).ToImmutableArray() };
        return Change(frame, changed);
    }

    private static Card Trait(Card card, params string[] traits) => card with {
        CombatKeywords = traits.ToImmutableArray(),
        States = traits.Select(trait => new PublicState(trait, trait, "ability", 1, null, "", trait)).ToImmutableArray()
    };
}
