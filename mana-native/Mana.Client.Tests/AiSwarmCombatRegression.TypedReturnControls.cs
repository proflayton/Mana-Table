using System.Collections.Immutable;
using Mana.AI;
using Mana.Contracts;

internal static partial class AiSwarmCombatRegression
{
    private static async Task TypedNominalReturnControls(Action<bool, string> check)
    {
        var initial = NominalReturnFrame();
        AiDecisionFrame Return(Func<Card, Card> change) => Change(initial, initial.State with {
            Players = initial.State.Players.Select(player => player.Id != 2 ? player : player with {
                Zones = player.Zones.Select(zone => zone with { Cards = zone.Cards.Select((card, index) => index == 0 ? change(card) : card).ToImmutableArray() }).ToImmutableArray()
            }).ToImmutableArray() });
        AiDecisionFrame Actor(Func<Player, Player> change) => Change(initial, initial.State with {
            Players = initial.State.Players.Select(player => player.Id == 0 ? change(player) : player).ToImmutableArray() });
        async Task Declines(AiDecisionFrame frame, string reason, AiTurnPlan? plan = null, int work = 4096)
        {
            var result = await Evaluate(frame, plan, work);
            check(result.Status == AiSeatStatus.Ready && result.Proposal != null && frame.Allows(result.Proposal)
                    && result.Evidence?.ReasonCode is not ("combat-stage-swarm-finish" or "combat-complete-swarm-finish"),
                "Typed nominal return expansion declines " + reason);
        }
        foreach (var (change, reason) in new (Func<Card, Card>, string)[] {
            (card => card with { FaceDown = true, BlockingCapacity = null }, "a face-down remaining source with no disclosed block capacity"),
            (card => card with { Power = null }, "unknown current return power"),
            (card => card with { Power = -1 }, "negative current return power"),
            (card => card with { Toughness = 0 }, "nonpositive return toughness"),
            (card => card with { States = null }, "missing typed state coverage"),
            (card => Trait(card, "infect"), "infect's unknown ordinary-life channel"),
            (card => Trait(card, "assign combat damage using toughness"), "toughness-based damage"),
            (card => Trait(card, "unmodeled-return-trait"), "an active unmodeled keyword"),
            (card => card with { CombatKeywords = [], States = [new("unknown", "Unknown", "ability", 1, null, "", "unmodeled-return-trait")] }, "an active unmodeled ability state")
        }) await Declines(Return(change), reason);
        foreach (var (frame, reason) in new[] {
            (Return(card => card with { States = default(ImmutableArray<PublicState>) }), "default typed state storage"),
            (Return(card => card with { CombatKeywords = default }), "default combat keyword storage")
        }) {
            // An initial unassigned source may also be considered as another
            // defender by the unchanged generic blocker model. Isolate the
            // remaining-return role with a current authored locked target.
            var locked = Change(frame, frame.State with { Combat = frame.State.Combat! with {
                Attackers = [new("attacker-000", 1, [], []) { Defender = new("player", "1", 1, "Target") }]
            } });
            var probe = new OptionalReturnProbe();
            using var guarded = new AiSeatSession(frame.State.Id, 0, probe, 17, new(TimeSpan.FromSeconds(10), 4096));
            guarded.Observe(locked); await guarded.EvaluateAsync();
            check(probe.Returned && probe.Command == null && guarded.TakeCommand() == null && probe.RemainingWork is > 64 and < 4096,
                "The typed optional forecast declines " + reason + " before its model scans, without exception or awarded cohort");
            var result = await Evaluate(frame);
            Console.WriteLine($"Malformed storage witness: {reason}; status={result.Status}; failure={result.Evidence?.Failure?.Kind}; reason={result.Evidence?.ReasonCode}");
            check(result.Proposal == null && result.Status == AiSeatStatus.Invalid
                    && result.Evidence?.Failure is { Kind: AiFailureKind.PolicyException, Code: "policy-failed" },
                "Initial malformed " + reason + " retains the specific existing generic-policy failure, without an awarded cohort");
        }
        check((await Evaluate(Return(card => Trait(card, "lifelink")))).Evidence?.ReasonCode == "combat-stage-swarm-finish",
            "Typed return traits are delegated to the existing model's lifelink coverage rather than a second swarm whitelist");
        check((await Evaluate(Return(card => card with { CombatKeywords = [],
                States = [new("inactive", "Inactive", "ability", 0, null, "", "unmodeled-return-trait")] }))).Evidence?.ReasonCode == "combat-stage-swarm-finish",
            "Typed return coverage preserves the model's existing distinction between inactive and active ability states");
        await Declines(Actor(player => player with { Life = 6 }), "full ordinary return damage meeting actor life");
        await Declines(Actor(player => player with { CommanderDamage = player.CommanderDamage.Select(row => row.CommanderId == "return-balmor"
            ? row with { Damage = 20 } : row).ToImmutableArray() }), "a real per-lineage commander-lethal return");
        await Declines(Actor(player => player with { CommanderDamage = player.CommanderDamage.Select(row => row.CommanderId == "target-pir"
            ? row with { Damage = 21 } : row).ToImmutableArray() }), "an inconsistent prior-at-limit bill even for the removed target");
        await Declines(Actor(player => player with { CommanderDamage = player.CommanderDamage.Where(row => row.CommanderId != "return-balmor").ToImmutableArray() }),
            "missing prior coverage for the remaining commander's actual lineage");
        await Declines(Actor(player => player with { CommanderDamage = player.CommanderDamage.Add(null!) }), "a malformed null typed prior row");
        await Declines(Change(initial, initial.State with { CommanderCombat = null }), "partial typed metadata instead of falling back to legacy");
        await Declines(Change(initial, initial.State with { CommanderCombat = new(0) }), "an invalid declared commander threshold");
        var partners = Return(card => card with { CommanderId = "return-partner" });
        partners = Change(partners, partners.State with { Players = partners.State.Players.Select(player => player.Id != 0 ? player : player with {
            CommanderDamage = player.CommanderDamage.Select(row => row.CommanderId == "return-balmor" ? row with { Damage = 19 } : row)
                .Append(new("Different partner", 2, "Remaining", 19) { CommanderId = "return-partner" }).ToImmutableArray()
        }).ToImmutableArray() });
        check((await Evaluate(partners)).Evidence?.ReasonCode == "combat-stage-swarm-finish",
            "Separate remaining commanders each at20 total damage are not pooled into a fictitious commander defeat");
        check((await Evaluate(Actor(player => player with { CommanderDamage = player.CommanderDamage.Select(row => row.CommanderId == "target-pir"
                ? row with { Damage = 20 } : row).ToImmutableArray() }))).Evidence?.ReasonCode == "combat-stage-swarm-finish",
            "The removed target contributes no future commander attack while its valid prior bill remains retained");
        await Declines(Change(initial, initial.State with { Players = initial.State.Players.Select(player => player.Id == 2 ? player with { TeamId = 0 } : player).ToImmutableArray() }),
            "an allied remaining seat rather than widening the explicit free-for-all model");
        await Declines(WithEnemyCards(initial, cards => cards.Select(card => card with { BlockingCapacity = null }).ToImmutableArray()), "unknown target block capacity");
        await Declines(WithEnemyCards(initial, cards => cards.Select(card => Trait(card, "prowess")).ToImmutableArray()),
            "prowess on a target blocker because generic attack/block coverage is unchanged");
        var actorProwess = Actor(player => player with { Zones = player.Zones.Select(zone => zone with {
            Cards = zone.Cards.Select(card => card.Power == 2 ? Trait(card, "vigilance", "prowess") : card).ToImmutableArray()
        }).ToImmutableArray() });
        await Declines(actorProwess, "prowess on the candidate attackers instead of changing their generic classification");
        await Declines(Change(actorProwess, actorProwess.State with { Players = actorProwess.State.Players.Where(player => player.Id != 2).ToImmutableArray() }),
            "the same unsupported actor trait in the unchanged duel path");
        var legacy = Change(initial, initial.State with { CommanderCombat = null, Players = initial.State.Players.Select(player => player with {
            CommanderDamage = [], Zones = player.Zones.Select(zone => zone with { Cards = zone.Cards.Select(card => card with { CommanderId = "" }).ToImmutableArray() }).ToImmutableArray()
        }).ToImmutableArray() });
        await Declines(legacy, "prowess in genuinely legacy return coverage, which still uses generic Ordinary");
        await Declines(Change(initial, initial.State with { Combat = initial.State.Combat! with {
            AttackOptions = initial.State.Combat.AttackOptions.Take(5).ToImmutableArray()
        } }), "fewer actual offered sources than a lethal cohort needs");
        await Declines(initial, "insufficient shared work for staging and completion", work: 128);
        var reserved = new AiTurnPlan(initial.State.Id, 0, initial.State.Turn, 1, AiPlanPurpose.Defend, [],
            ["attacker-000", "attacker-001", "attacker-002"]) { CreatedFrom = initial.Stamp };
        await Declines(initial, "a cohort that would consume retained blockers", reserved);
        var funding = reserved with { RetainedBlockerIds = [], Reservations = [new("attacker-000", "held-response", [], 0) {
            SourceZone = "Battlefield", ManaSourceIds = ["attacker-001", "attacker-002"] }] };
        await Declines(initial, "a cohort that would consume reserved response and mana sources", funding);
        CheckDefenderReturnModel(check, initial);
        await StageTypedNominalReturn(check);
    }

    private static async Task StageTypedNominalReturn(Action<bool, string> check)
    {
        using var seat = new AiSeatSession("swarm-regression", 0, new CasualCommanderPolicy(), 17, new(TimeSpan.FromSeconds(10), 4096));
        var assigned = new List<string>(); DecisionCommand? previous = null; int remaining = 4096; bool completed = false;
        for (int step = 0; step <= 6; step++) {
            var frame = NominalReturnFrame(step + 1, assigned);
            seat.Observe(frame); await seat.EvaluateAsync(); var ticket = seat.TakeCommand();
            check(ticket != null && frame.Allows(ticket.Command) && (previous == null || !frame.Allows(previous)),
                "Each typed-return cohort stage independently obtains its current key and retires the prior command");
            if (ticket == null) throw new InvalidOperationException("The typed-return cohort lost its current command.");
            int currentWork = SubmittedWork(seat, ticket);
            check(currentWork >= 64 && currentWork < remaining, "Typed-return staging consumes one persistent4096 allowance without renewing its384 optional slices");
            remaining = currentWork;
            if (ticket.Command is ToggleAttackPlayer attack) {
                var source = frame.State.Viewer!.Zone("Battlefield").Cards.Single(card => card.Key == attack.Attacker);
                check(attack.Player == 1 && source.Power == 2 && source.CombatKeywords.Contains("vigilance")
                        && !assigned.Contains(source.VisualId) && ticket.ReasonCode == "combat-stage-swarm-finish",
                    "The typed-return forecast stages one new offered vigilance token at its locked target");
                assigned.Add(source.VisualId);
            } else {
                check(ticket.Command is CompleteAttackers && ticket.ReasonCode == "combat-complete-swarm-finish" && assigned.Count == 6,
                    "The six-body nominal cohort completes only after six current accepted assignments");
                completed = true;
            }
            check(seat.Report(ticket, AiExecutionReceipt.Accepted), "Only each accepted authored receipt advances the typed-return cohort");
            previous = ticket.Command;
        }
        check(completed && assigned.Count == 6 && remaining >= 64, "Six2/2 bodies overcome three worst-case block slots at life6 within the original shared budget");
        Console.WriteLine($"Typed-return staged witness: toggles={assigned.Count}; completed={completed}; remainingWork={remaining}");
        using var rejected = new AiSeatSession("swarm-regression", 0, new CasualCommanderPolicy(), 17, new(TimeSpan.FromSeconds(10), 4096));
        rejected.Observe(NominalReturnFrame()); await rejected.EvaluateAsync(); var first = rejected.TakeCommand()!;
        check(first.Command is ToggleAttackPlayer && rejected.Report(first, AiExecutionReceipt.Rejected) && rejected.TakeCommand() == null,
            "Rejected typed-return staging earns no second dispatch or invented assignment");
        var fresh = NominalReturnFrame(revision: 2); rejected.Observe(fresh); await rejected.EvaluateAsync(); var next = rejected.TakeCommand();
        check(next != null && fresh.Allows(next.Command) && !fresh.Allows(first.Command)
                && SubmittedWork(rejected, next) < SubmittedWork(rejected, first),
            "After rejection a fresh independent scope spends the remaining episode allowance and cannot reuse the old key");
    }

    private static void CheckDefenderReturnModel(Action<bool, string> check, AiDecisionFrame frame)
    {
        var card = Trait(Creature("return-defender-model", 1, 1), "defender");
        var state = frame.State with { Players = frame.State.Players.Select(player => player.Id != 2 ? player : player with {
            Zones = [new("Battlefield", 1, [card], null)]
        }).ToImmutableArray() };
        CheckReturnContext(check, state, "defender");
        var assembly = typeof(CasualCommanderPolicy).Assembly;
        var type = assembly.GetType("Mana.AI.AiCombatSurvival", true)!;
        var damage = assembly.GetType("Mana.AI.AiCombatDamage", true)!;
        const System.Reflection.BindingFlags instance = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        const System.Reflection.BindingFlags statics = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
        bool Unknown(object risk) => (bool)risk.GetType().GetProperty("UnknownNominalChannels")!.GetValue(risk)!;
        // A direct nominal-model input, not a claim that a defender was offered
        // or allowed to attack in an actual engine frame.
        var current = state with { Combat = new([new(card.VisualId, 0, [], [])], [], null) };
        var currentModel = type.GetMethod("Current", statics)!.Invoke(null, [current, current.Viewer!])!;
        check(Unknown(type.GetProperty("Risk", instance)!.GetValue(currentModel)!),
            "The default Current model retains unknown defender coverage rather than inheriting the closed return context");
        var doubled = Trait(card with { Power = 3 }, "defender", "double strike");
        object Query(long amount) {
            var incoming = Array.CreateInstance(damage, 1); incoming.SetValue(Activator.CreateInstance(damage, doubled, amount), 0);
            return type.GetMethod("NominalUnblockedReturn", statics)!.Invoke(null, [state, state.Viewer!, incoming])!;
        }
        check(!Unknown(Query(6)) && Unknown(Query(3)),
            "The closed defender query requires canonical full double-strike damage and refuses a discounted partial amount");
    }

    private sealed class OptionalReturnProbe : IAiPolicy
    {
        public AiPolicyDescriptor Descriptor { get; } = new("optional-return-boundary-control", AiTasks.All);
        internal bool Returned;
        internal DecisionCommand? Command;
        internal int RemainingWork;
        public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation)
        {
            var type = typeof(CasualCommanderPolicy).Assembly.GetType("Mana.AI.AiSwarmCombat", true)!;
            var method = type.GetMethod("TryFinish", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            Command = (DecisionCommand?)method.Invoke(null, [request, (AttackPayload)request.Frame.State.Decision!.Payload, 64]);
            Returned = true; RemainingWork = request.Work.Remaining;
            return Task.FromResult<AiProposal>(new AiProposal.Unavailable("controlled-optional-decline", "A diagnostic boundary awards no command."));
        }
    }
}
