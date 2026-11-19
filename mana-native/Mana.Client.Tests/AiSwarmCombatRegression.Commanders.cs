using System.Collections.Immutable;
using Mana.AI;
using Mana.Contracts;

internal static partial class AiSwarmCombatRegression
{
    private static async Task TypedCommanderReturns(Action<bool, string> check)
    {
        // Public arithmetic distilled from the retained first master-5ad failure:
        // turns 38/42/46, respectively. Private decks/history are not fixtures.
        foreach (var (candidates, blockers, life, first, second, ordinary) in new[] {
            (8, 4, 52, 5, 7, 14), (8, 4, 51, 7, 7, 15), (10, 5, 56, 9, 0, 18)
        }) {
            using var seat = new AiSeatSession("swarm-regression", 0, new CasualCommanderPolicy(), 17,
                new(TimeSpan.FromSeconds(10), 4096));
            var assigned = new List<string>(); DecisionCommand? previous = null;
            int remaining = 4096; bool completed = false;
            for (int step = 0; step <= blockers + 1; step++) {
                var frame = TypedReturnFrame(candidates, blockers, life, first, second, ordinary, step + 1, assigned);
                seat.Observe(frame); await seat.EvaluateAsync(); var ticket = seat.TakeCommand();
                check(ticket != null && frame.Allows(ticket.Command) && (previous == null || !frame.Allows(previous)),
                    "A typed FFA elimination answers only its fresh offered declaration and revokes the earlier key");
                if (ticket == null) throw new InvalidOperationException("Typed FFA cohort lost its current answer.");
                int next = SubmittedWork(seat, ticket);
                check(next >= 64 && next < remaining, "Typed commander forecasting and every staged input share the unchanged work allowance");
                remaining = next;
                if (ticket.Command is ToggleAttackPlayer attack) {
                    var source = frame.State.Viewer!.Zone("Battlefield").Cards.Single(card => card.Key == attack.Attacker);
                    check(attack.Player == 1 && !assigned.Contains(source.VisualId)
                        && ticket.ReasonCode == "combat-stage-swarm-finish",
                        "Separate nonlethal commander channels permit one distinct offered attacker toward the one-life defender");
                    assigned.Add(source.VisualId);
                } else {
                    check(ticket.Command is CompleteAttackers && ticket.ReasonCode == "combat-complete-swarm-finish",
                        "A typed FFA cohort completes through the nominal finishing path");
                    completed = true;
                }
                check(seat.Report(ticket, AiExecutionReceipt.Accepted), "Only the current accepted fixture receipt advances the typed cohort");
                previous = ticket.Command;
                if (completed) break;
            }
            check(completed && assigned.Count == blockers + 1 && assigned.Count < candidates,
                $"The captured {candidates}-candidate/{blockers}-slot shape completes its minimal {blockers + 1}-body cohort and leaves spare creatures");
        }

        var baseline = TypedReturnFrame();
        async Task Declines(AiDecisionFrame frame, string reason, int work = 4096)
        {
            var result = await Evaluate(frame, work: work);
            check(result.Status == AiSeatStatus.Ready && result.Proposal != null && frame.Allows(result.Proposal)
                && result.Evidence?.ReasonCode is not ("combat-stage-swarm-finish" or "combat-complete-swarm-finish"),
                "Typed FFA expansion declines " + reason);
        }
        AiDecisionFrame Actor(Func<Player, Player> change) => Change(baseline, baseline.State with {
            Players = baseline.State.Players.Select(player => player.Id == 0 ? change(player) : player).ToImmutableArray() });
        AiDecisionFrame Rows(Func<ImmutableArray<CommanderDamage>, ImmutableArray<CommanderDamage>> change)
            => Actor(player => player with { CommanderDamage = change(player.CommanderDamage) });
        await Declines(Actor(player => player with { Life = 27 }), "aggregate ordinary return damage reaching life even though each commander is nonlethal");
        await Declines(Rows(rows => rows.Select(row => row.CommanderId == "partner-b" ? row with { Damage = 16 } : row).ToImmutableArray()),
            "a real returning commander reaching the declared threshold");
        await Declines(Rows(rows => rows.Select(row => row.CommanderId == "removed" ? row with { Damage = 21 } : row).ToImmutableArray()),
            "an inconsistent living actor already at the threshold, even for the excluded defender");
        await Declines(Rows(rows => rows.Where(row => row.CommanderId != "partner-b").ToImmutableArray()), "missing prior damage for a returning CommanderId");
        await Declines(Rows(rows => rows.Add(rows.Single(row => row.CommanderId == "partner-b"))), "duplicate typed prior lineage");
        await Declines(Rows(rows => rows.Add(null!)), "a null typed actor damage row without throwing from the optional forecast");
        check(new CommanderDamage("Legacy", 0, "Actor", 0) { CommanderId = null! }.CommanderId == ""
            && new Card { CommanderId = null! }.CommanderId == "",
            "Public damage rows and cards normalize null CommanderId to empty, independently of nullable row objects");
        await Declines(Rows(rows => rows.Select(row => row.CommanderId == "partner-b" ? row with { CommanderId = "" } : row).ToImmutableArray()),
            "a partial typed row instead of escaping to legacy arithmetic");
        await Declines(Change(baseline, baseline.State with { CommanderCombat = null }), "missing rules with retained typed lineage");
        await Declines(Change(baseline, baseline.State with { CommanderCombat = new(0) }), "an invalid declared threshold");
        await Declines(Change(baseline, baseline.State with { CommanderCombat = new(6) }), "a real commander loss under a nonstandard positive threshold");
        await Declines(Rows(rows => rows.Select(row => row with { Damage = -1 }).ToImmutableArray()), "malformed negative prior damage");
        await Declines(Change(baseline, baseline.State with { Players = baseline.State.Players.Select(player => player.Id != 2 ? player
            : player with { Zones = player.Zones.Select(zone => zone with { Cards = zone.Cards.Select(card => card with {
                CombatKeywords = ["infect"], States = [new("infect", "Infect", "ability", 1, null, "", "infect")]
            }).ToImmutableArray() }).ToImmutableArray() }).ToImmutableArray() }), "an unknown ordinary-life damage channel");
        await Declines(Change(baseline, baseline.State with { Players = baseline.State.Players.Select(player => player.Id != 2 ? player
            : player with { Zones = player.Zones.Select(zone => zone with { Cards = zone.Cards.Select(card => card with {
                CombatKeywords = ["adapt"], States = [new("adapt", "Adapt", "ability", 1, null, "", "adapt")]
            }).ToImmutableArray() }).ToImmutableArray() }).ToImmutableArray() }), "traits outside the shared survival helper's narrower coverage");
        await Declines(TypedReturnFrame(candidates: 4), "an army without enough distinct attackers to overcome the block slots");
        await Declines(baseline, "insufficient shared work for the remaining typed stages", work: 128);
        foreach (string trait in new[] { "menace", "protection", "modular" }) {
            var knownReturn = Change(baseline, baseline.State with { Players = baseline.State.Players.Select(player => player.Id != 2 ? player
                : player with { Zones = player.Zones.Select(zone => zone with { Cards = zone.Cards.Select(card => card with {
                    CombatKeywords = [trait], States = [new(trait, trait, "ability", 1, null, "", trait)]
                }).ToImmutableArray() }).ToImmutableArray() }).ToImmutableArray() });
            check((await Evaluate(knownReturn)).Evidence?.ReasonCode == "combat-stage-swarm-finish",
                "Only the nominal no-own-blockers return context admits the closed trait " + trait);
            CheckReturnContext(check, knownReturn.State, trait);
        }
        var legacy = FfaFrame(3, [1]);
        await Declines(Change(legacy, legacy.State with { Players = legacy.State.Players.Select(player => player.Id != 2 ? player
            : player with { CommanderDamage = [null!] }).ToImmutableArray() }), "a null legacy damage row retained by another recipient");
        var legacyCommand = Change(legacy, legacy.State with { Players = legacy.State.Players.Select(player => player.Id != 2 ? player
            : player with { Zones = player.Zones.Add(new("Command", 1, [
                Creature("command-only", 1, 1) with { CommanderId = "partial-command", BlockingCapacity = null }
            ], null)) }).ToImmutableArray() });
        await Declines(legacyCommand, "missing rules with typed lineage retained only in the Command zone");
        var retired = legacy.State.Players[2] with { Id = 9, Eliminated = true, Life = 0, Zones = [],
            CommanderDamage = [new("Retired", 9, "Retired", 0) { CommanderId = "partial-retired" }] };
        await Declines(Change(legacy, legacy.State with { Players = legacy.State.Players.Add(retired) }),
            "missing rules with typed lineage retained only by an eliminated recipient");
        await Declines(Change(legacy, legacy.State with { Players = legacy.State.Players.Add(retired with { CommanderDamage = [null!] }) }),
            "a null legacy damage row retained by an eliminated recipient");

        var reserved = new AiTurnPlan(baseline.State.Id, 0, baseline.State.Turn, 1, AiPlanPurpose.Defend, [],
            ["attacker-000", "attacker-001", "attacker-002", "attacker-003"]) { CreatedFrom = baseline.Stamp };
        check((await Evaluate(baseline, reserved)).Evidence?.ReasonCode != "combat-stage-swarm-finish",
            "Typed commander arithmetic does not spend retained defenders to invent a finishing cohort");

        var split = Rows(rows => rows.Select(row => row.CommanderId is "partner-a" or "partner-b" ? row with { Damage = 15 } : row).ToImmutableArray());
        check((await Evaluate(split)).Evidence?.ReasonCode == "combat-stage-swarm-finish",
            "Partner commanders at 16 and 20 total damage do not pool into a fictitious shared loss");
        var removed = Rows(rows => rows.Select(row => row.CommanderId == "removed" ? row with { Damage = 20 } : row).ToImmutableArray());
        check((await Evaluate(removed)).Evidence?.ReasonCode == "combat-stage-swarm-finish",
            "A removed defender's prior commander bill does not turn unrelated ordinary attackers into that commander");
        var stolen = Rows(rows => rows.Select(row => row.CommanderId == "foreign" ? row with { OwnerId = 1, Damage = 14 } : row).ToImmutableArray());
        await Declines(stolen, "a stolen commander controlled by a survivor but owned by the removed defender, using its real CommanderId");
        var noRule = Change(baseline, baseline.State with { CommanderCombat = new(null) });
        check((await Evaluate(noRule)).Evidence?.ReasonCode == "combat-stage-swarm-finish",
            "Explicit inapplicable commander damage differs from missing legacy rules while retaining the life check");

        using var rejected = new AiSeatSession("swarm-regression", 0, new CasualCommanderPolicy(), 17, new(TimeSpan.FromSeconds(10), 4096));
        rejected.Observe(baseline); await rejected.EvaluateAsync(); var firstTicket = rejected.TakeCommand()!;
        check(firstTicket.Command is ToggleAttackPlayer && rejected.Report(firstTicket, AiExecutionReceipt.Rejected)
            && rejected.TakeCommand() == null, "A rejected typed cohort command earns neither another dispatch nor an accepted assignment");
        var fresh = TypedReturnFrame(revision: 2);
        rejected.Observe(fresh); await rejected.EvaluateAsync(); var replacement = rejected.TakeCommand();
        check(replacement != null && fresh.Allows(replacement.Command) && !fresh.Allows(firstTicket.Command)
            && SubmittedWork(rejected, replacement) < SubmittedWork(rejected, firstTicket),
            "After rejection a fresh observation must obtain its own current command within the same episode allowance");
    }

    private static void CheckReturnContext(Action<bool, string> check, GameSnapshot state, string trait)
    {
        // Keep the production helper internal: reflection checks that the named
        // risk query cannot widen the default model or expose AfterBlock.
        var assembly = typeof(CasualCommanderPolicy).Assembly;
        var type = assembly.GetType("Mana.AI.AiCombatSurvival", true)!;
        var damage = assembly.GetType("Mana.AI.AiCombatDamage", true)!;
        const System.Reflection.BindingFlags instance = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        const System.Reflection.BindingFlags statics = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
        var source = state.Players.Single(player => player.Id == 2).Zone("Battlefield").Cards[0];
        Array Contributions(long amount) {
            var array = Array.CreateInstance(damage, 1);
            array.SetValue(Activator.CreateInstance(damage, source, amount), 0); return array;
        }
        bool Unknown(object risk) => (bool)risk.GetType().GetProperty("UnknownNominalChannels")!.GetValue(risk)!;
        var full = Contributions(source.Power!.Value);
        var model = Activator.CreateInstance(type, instance, null, [state, state.Viewer!, full], null)!;
        var malformedDefender = state.Viewer! with { CommanderDamage = state.Viewer!.CommanderDamage.Add(null!) };
        var malformedModel = Activator.CreateInstance(type, instance, null, [state, malformedDefender, full], null)!;
        check(Unknown(type.GetProperty("Risk", instance)!.GetValue(malformedModel)!)
            && Unknown(type.GetMethod("AfterBlock", instance)!.Invoke(malformedModel, [source, 0L])!),
            "A reachable null damage row remains unknown in the default model and AfterBlock without throwing");
        check(Unknown(type.GetProperty("Risk", instance)!.GetValue(model)!)
            && Unknown(type.GetMethod("AfterBlock", instance)!.Invoke(model, [source, 0L])!),
            "Default current-damage and AfterBlock coverage remain unknown for " + trait);
        var method = type.GetMethod("NominalUnblockedReturn", statics)!;
        check(!Unknown(method.Invoke(null, [state, state.Viewer!, full])!), "The named risk-only query recognizes full nominal return for " + trait);
        check(Unknown(method.Invoke(null, [state, state.Viewer!, Contributions(0)])!),
            "The named return query refuses a reduced amount which would credit an invented blocker for " + trait);
        foreach (string unknown in new[] { "unrecognized-fourth-trait", "infect", "assign combat damage using toughness" }) {
            var changed = source with { CombatKeywords = [unknown], States = [new(unknown, unknown, "ability", 1, null, "", unknown)] };
            var incoming = Array.CreateInstance(damage, 1); incoming.SetValue(Activator.CreateInstance(damage, changed, 1L), 0);
            check(Unknown(method.Invoke(null, [state, state.Viewer!, incoming])!), "Named return coverage still refuses " + unknown);
        }
        var global = state with { States = [new("unmodeled", "Unmodeled", "unknown", 1, null, "")] };
        check(Unknown(method.Invoke(null, [global, global.Viewer!, full])!), "Named return coverage retains global-state uncertainty");
        var playerState = state.Viewer! with { States = [new("unmodeled", "Unmodeled", "unknown", 1, null, "")] };
        check(Unknown(method.Invoke(null, [state, playerState, full])!), "Named return coverage retains player-state uncertainty");
        var poison = state.Viewer! with { Counters = ImmutableDictionary<string, int>.Empty.Add("Poison", 1) };
        check(Unknown(method.Invoke(null, [state, poison, full])!), "Named return coverage retains counter-channel uncertainty");
    }

    private static AiDecisionFrame TypedReturnFrame(int candidates = 8, int blockers = 4, int life = 52,
        int partnerPower = 5, int foreignPower = 7, int ordinaryPower = 14, int revision = 1, List<string>? assigned = null)
    {
        int[] powers = [3, .. Enumerable.Repeat(1, candidates - 1)];
        var frame = FfaFrame(4, [], powers, blockers: blockers, enemyLife: 1, ownLife: life, assigned: assigned, revision: revision);
        var rows = ImmutableArray.Create(
            new CommanderDamage("Same label", 1, "Removed", 4) { CommanderId = "removed" },
            new CommanderDamage("Same label", 2, "Partner owner", 0) { CommanderId = "partner-a" },
            new CommanderDamage("Same label", 2, "Partner owner", 0) { CommanderId = "partner-b" },
            new CommanderDamage("Same label", 3, "Other owner", 0) { CommanderId = "foreign" });
        return Change(frame, frame.State with { CommanderCombat = new(21), Players = frame.State.Players.Select(player => player.Id switch {
            0 => player with { CommanderDamage = rows },
            2 => player with { Zones = [new("Battlefield", 2, [
                Creature("return-a", 1, revision) with { CommanderId = "partner-a", Tapped = true },
                Creature("return-b", partnerPower, revision) with { CommanderId = "partner-b", Tapped = true }
            ], null)] },
            3 => player with { Zones = [new("Battlefield", 2, [
                Creature("return-c", foreignPower, revision) with { CommanderId = "foreign", Tapped = true },
                Creature("return-ordinary", ordinaryPower, revision) with { Tapped = true }
            ], null)] },
            _ => player
        }).ToImmutableArray() });
    }
}
