using System.Collections.Immutable;
using System.Security.Cryptography;
using Mana.Contracts;

namespace Mana.Table;

internal static class ForgePublicProjectionFactChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
        string directory = Path.Combine(profile, "expected-public-fact-rejections");
        Directory.CreateDirectory(directory);
        var state = Fixture();
        var mutations = new (string Name, Func<GameSnapshot, GameSnapshot> Change)[] {
            ("turn", value => value with { Turn = value.Turn + 1 }),
            ("phase", value => value with { PhaseKey = "COMBAT_DECLARE_BLOCKERS" }),
            ("active player", value => value with { ActivePlayerId = 2 }),
            ("stack count", value => value with { Stack = value.Stack.RemoveAt(0) }),
            ("stack order", value => value with { Stack = value.Stack.Reverse().ToImmutableArray() }),
            ("stack occurrence", value => value with { Stack = value.Stack.SetItem(0, value.Stack[0] with { Id = "replacement-trigger" }) }),
            ("stack execution", value => value with { Stack = value.Stack.SetItem(0, value.Stack[0] with { ExecutionId = "replacement-execution" }) }),
            ("contradictory admitted stack execution", value => ReidentifyExecution(value, "execution-trigger", "other-admitted-execution")),
            ("unrecognized admitted execution kind", value => ChangeExecution(value, "execution-trigger", occurrence => occurrence with { Kind = (ExecutionKind)int.MaxValue })),
            ("invalid historical source identifier", value => ChangeExecution(value, "execution-trigger", occurrence => occurrence with { SourceCardId = " " })),
            ("stack controller", value => value with { Stack = value.Stack.SetItem(0, value.Stack[0] with { ControllerId = 3 }) }),
            ("spell versus ability", value => value with { Stack = value.Stack.SetItem(0, value.Stack[0] with { Kind = StackActionKind.Spell }) }),
            ("public card target", value => value with { Stack = value.Stack.SetItem(0, value.Stack[0] with {
                Targets = value.Stack[0].Targets.SetItem(0, new("card", "c", "Another public creature", 2)) }) }),
            ("player target", value => value with { Stack = value.Stack.SetItem(0, value.Stack[0] with {
                Targets = value.Stack[0].Targets.SetItem(1, new("player", "2", "Another player", 2)) }) }),
            ("public spell-card target", value => value with { Stack = value.Stack.SetItem(0, value.Stack[0] with {
                Targets = value.Stack[0].Targets.RemoveAt(2) }) }),
            ("attacker occurrence", value => value with { Combat = value.Combat! with {
                Attackers = value.Combat.Attackers.SetItem(0, value.Combat.Attackers[0] with { CardId = "c" }) } }),
            ("blocker topology", value => value with { Combat = value.Combat! with {
                Attackers = value.Combat.Attackers.SetItem(0, value.Combat.Attackers[0] with { BlockerIds = ["c"] }) } }),
            ("defending player", value => value with { Combat = value.Combat! with {
                Attackers = value.Combat.Attackers.SetItem(0, value.Combat.Attackers[0] with { DefendingPlayerId = 2 }) } }),
            ("defending permanent", value => value with { Combat = value.Combat! with {
                Attackers = value.Combat.Attackers.SetItem(1, value.Combat.Attackers[1] with { Defender = new("card", "b", 1, "Other defender") }) } }),
            ("departed defender", value => value with { Combat = value.Combat! with {
                Attackers = value.Combat.Attackers.SetItem(1, value.Combat.Attackers[1] with { HasDefender = false, Defender = null }) } }),
            ("blocked fact", value => value with { Combat = value.Combat! with {
                Attackers = value.Combat.Attackers.SetItem(0, value.Combat.Attackers[0] with { Blocked = false }) } }),
            ("combat presence", value => value with { Combat = null }),
            ("permanent printed identity", value => ChangeCard(value, 0, "Battlefield", "a", card => card with { Name = "Different creature" })),
            ("permanent current face", value => ChangeCard(value, 0, "Battlefield", "a", card => card with { ArtFace = "back" })),
            ("permanent concealed state", value => ChangeCard(value, 0, "Battlefield", "a", card => card with { FaceDown = true })),
            ("face-down permanent public physical type", value => ChangeCard(value, 0, "Battlefield", "morph", card => card with { Type = "Artifact Creature" })),
            ("permanent controller", MoveController),
            ("attachment parent", value => ChangeCard(value, 0, "Battlefield", "equipment", card => card with { AttachedTo = new("c", "Other", "Creature") })),
            ("attached occurrence", value => ChangeCard(value, 0, "Battlefield", "a", card => card with { Attachments = [] })),
            ("public graveyard identity", value => ChangeCard(value, 0, "Graveyard", "grave-0-0", card => card with { Name = "Different graveyard card" })),
            ("graveyard order", value => ChangeZone(value, 0, "Graveyard", zone => zone with { Cards = zone.Cards.Reverse().ToImmutableArray() })),
            ("public exile identity", value => ChangeCard(value, 0, "Exile", "exile-0", card => card with { Name = "Different exiled card" })),
            ("command identity", value => ChangeCard(value, 0, "Command", "commander-0", card => card with { Name = "Different commander" })),
            ("player resource", value => ChangePlayer(value, 0, player => player with { Counters = player.Counters.SetItem("energy", 8) })),
            ("land allowance", value => ChangePlayer(value, 0, player => player with { LandPlaysUsed = 1 })),
            ("hand limit", value => ChangePlayer(value, 0, player => player with { MaxHandSize = null })),
            ("effective public ability", value => ChangeCard(value, 0, "Battlefield", "a", card => card with {
                States = [new("flying", "Flying", "keyword", null, null, "Public description", "Flying")] }))
        };
        foreach (var (name, change) in mutations)
            check(!AiWholeSeatEngineCheck.ComparePublicProjection(state, change(state), directory),
                "Same-publication audience agreement rejects a changed " + name + " without relying on equal life totals or zone counts");

        var perspective = Perspective(state);
        check(AiWholeSeatEngineCheck.ComparePublicProjection(state, perspective, directory),
            "The same public game tolerates other viewers' private hand/library/face-down disclosures, opaque keys, priority and decision permissions, localized prose and table ordering");
        check(AiWholeSeatEngineCheck.ComparePublicProjection(perspective, state, directory),
            "Public agreement is symmetric when neither audience is a spectator and each retains different private knowledge");
        var privatelyReadable = ChangeCard(state, 0, "Battlefield", "morph", card => card with { Key = "owner-masked-action", VisualId = "" });
        var physical = privatelyReadable.Players.Single(player => player.Id == 0).Zone("Battlefield").Cards.Single(card => card.CombatId == "morph");
        var known = new Card { Name = "PRIVATE_NOT_A_PUBLIC_FACT", Type = "Private printed Artifact", ManaCost = "{8}", Power = 1, Toughness = 1 };
        var grant = new ActionSourceOccurrence("owner-private-reading", "Battlefield", 0, physical) { KnownFace = known };
        privatelyReadable = privatelyReadable with {
            Decision = new("owner-private-reading", new(), new PriorityPayload(new("Pass", true))),
            DecisionOwnerId = 0, PriorityPlayerId = 0, ActionSources = [grant]
        };
        check(CardViews.IsCurrentActionSource(privatelyReadable, grant)
                && CardViews.KnownActionFace(privatelyReadable, physical)?.Type == known.Type
                && physical is { FaceDown: true, Type: "Creature", Power: 2, Toughness: 2 }
                && AiWholeSeatEngineCheck.ComparePublicProjection(state, privatelyReadable, directory)
                && AiWholeSeatEngineCheck.ComparePublicProjection(privatelyReadable, state, directory)
                && !ForgePublicProjectionFacts.Capture(privatelyReadable).Contains("Private printed Artifact", StringComparison.Ordinal),
            "A valid current owner grant keeps private printed type readable through KnownFace while the shared facts retain only the masked permanent's public physical type");
        var spectator = state with { ViewerId = -1, Executions = null,
            Stack = state.Stack.Select(item => item with { ExecutionId = "" }).ToImmutableArray() };
        check(AiWholeSeatEngineCheck.ComparePublicProjection(spectator, state, directory)
                && AiWholeSeatEngineCheck.ComparePublicProjection(state, spectator, directory),
            "A spectator's absent execution ledger and stack references agree with locally admitted seat correlations without weakening public stack facts");
        var privateHistory = perspective with { Executions = perspective.Executions! with {
            Occurrences = perspective.Executions.Occurrences.Add(new("private-execution", "private-execution", ExecutionKind.Spell, ExecutionStep.Preparation) {
                ActorPlayerId = 1, SourceCardId = "PRIVATE_NOT_A_PUBLIC_FACT", SourceName = "PRIVATE_NOT_A_PUBLIC_FACT"
            })
        } };
        check(AiWholeSeatEngineCheck.ComparePublicProjection(spectator, privateHistory, directory)
                && !ForgePublicProjectionFacts.Capture(privateHistory).Contains("PRIVATE_NOT_A_PUBLIC_FACT", StringComparison.Ordinal),
            "Unreferenced private execution ancestry stays local and is never copied into public comparison evidence");
        var optionalDescriptors = ChangeExecution(state, "execution-trigger", occurrence => occurrence with {
            ActorPlayerId = null, SourceCardId = null, SourceName = null
        });
        check(AiWholeSeatEngineCheck.ComparePublicProjection(spectator, optionalDescriptors, directory),
            "An admitted correlation may omit optional actor and source descriptors instead of inventing them from a historical portrait");
        var changedActor = ChangeExecution(state, "execution-trigger", occurrence => occurrence with { ActorPlayerId = 2 });
        check(AiWholeSeatEngineCheck.ComparePublicProjection(spectator, changedActor, directory),
            "An execution's initiating actor need not equal the current controller of a stack occurrence");
        var classifiedHistory = ChangeExecution(state, "execution-trigger", occurrence => occurrence with { Kind = ExecutionKind.Mana });
        check(AiWholeSeatEngineCheck.ComparePublicProjection(spectator, classifiedHistory, directory),
            "An admitted execution's historical kind does not redefine the stack occurrence's public spell-versus-ability fact");
        var historical = state with { Stack = state.Stack.SetItem(0, state.Stack[0] with {
            Card = state.Stack[0].Card! with { VisualId = "", CombatId = "historical-source-alias" }
        }) };
        check(AiWholeSeatEngineCheck.ComparePublicProjection(spectator, historical, directory),
            "A historical source portrait without a current disclosed visual anchor cannot contradict an event-time execution source generation");
        var redisclosed = state with { Stack = state.Stack.SetItem(0, state.Stack[0] with {
            Card = state.Stack[0].Card! with { VisualId = "new-public-source-generation", CombatId = "new-public-source-generation" }
        }) };
        check(AiWholeSeatEngineCheck.ComparePublicProjection(spectator, redisclosed, directory),
            "A source revealed again under a new visual generation does not rewrite its earlier event-time execution source anchor");
        var badMapping = state with { Stack = state.Stack.SetItem(0, state.Stack[0] with { ExecutionId = "missing-admitted-occurrence" }) };
        check(!AiWholeSeatEngineCheck.ComparePublicProjection(badMapping, badMapping, directory),
            "Two audiences repeating the same dangling supplied execution reference still fail local admitted-frame validation");
        var admitted = ForgePublicProjectionFacts.CaptureExecutionCorrelations(state);
        var absent = ForgePublicProjectionFacts.CaptureExecutionCorrelations(spectator);
        var retained = ForgePublicProjectionFacts.MergeExecutionCorrelations(absent, admitted);
        retained = retained == null ? null : ForgePublicProjectionFacts.MergeExecutionCorrelations(retained, absent);
        var contradiction = ForgePublicProjectionFacts.CaptureExecutionCorrelations(ReidentifyExecution(state, "execution-trigger", "other-admitted-execution"));
        check(retained is { Valid: true, StackExecutionIds.Count: 2 }
                && !ForgePublicProjectionFacts.ExecutionCorrelationsAgree(retained, contradiction)
                && ForgePublicProjectionFacts.MergeExecutionCorrelations(retained, contradiction) == null,
            "A bounded cohort retains admitted stack mappings through an absent spectator frame and rejects a later contradictory admitted identity");
        AutomatedSeatObservation Audience(GameSnapshot snapshot, int viewer) => new(viewer,
            snapshot with { ViewerId = viewer }, DecisionActionDomain.None, new());
        var missingFirst = Audience(spectator, 0);
        var mappingA = Audience(state, 1);
        var mappingB = Audience(ReidentifyExecution(state, "execution-trigger", "other-admitted-execution"), 2);
        check(ForgePublicProjectionChecks.ValidateBatch([missingFirst, mappingA, Audience(spectator, 2)], directory) == 2,
            "A whole audience batch keeps a later admitted stack mapping even when its first and last observations omit correlation metadata");
        bool conflictingBatch = false;
        try { ForgePublicProjectionChecks.ValidateBatch([missingFirst, mappingA, mappingB], directory); }
        catch (InvalidDataException) { conflictingBatch = true; }
        check(conflictingBatch,
            "An absent first audience cannot hide contradictory admitted stack mappings supplied by later audiences in the same batch");

        bool Refuses(GameSnapshot other) {
            try { AiWholeSeatEngineCheck.ComparePublicProjection(state, other, directory); return false; }
            catch (ArgumentException) { return true; }
        }
        check(Refuses(state with { Id = "replacement-game" }) && Refuses(state with { BoardRevision = state.BoardRevision + 1 }),
            "The shared oracle refuses cross-game and cross-publication comparisons instead of labelling sequential frames a desync");

        string receipt = Path.Combine(directory, "whole-seat-ai-projection-mismatch.json");
        string firstHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(receipt)));
        _ = AiWholeSeatEngineCheck.ComparePublicProjection(state, state with { Turn = 99 }, directory);
        check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(receipt))) == firstHash
                && !File.ReadAllText(receipt).Contains("PRIVATE_NOT_A_PUBLIC_FACT", StringComparison.Ordinal),
            "Expected mismatch evidence preserves its first observation and excludes private canary faces and action keys");
    }

    private static GameSnapshot Fixture()
    {
        Card Face(string id, string name, string type = "Creature") => new() {
            VisualId = id, CombatId = id, Name = name, Type = type, Power = 2, Toughness = 2,
            States = [], Counters = ImmutableDictionary<string, int>.Empty
        };
        var players = Enumerable.Range(0, 4).Select(id => new Player(id, "Player " + id, 40, false, false,
            ImmutableDictionary<string, int>.Empty.Add("U", 1), [
                new("Battlefield", 0, [], null),
                new("Graveyard", 2, [Face("grave-" + id + "-0", "First grave card"), Face("grave-" + id + "-1", "Second grave card")], null),
                new("Exile", 2, [Face("exile-" + id, "Public exile card")], null),
                new("Command", 1, [Face("commander-" + id, "Commander " + id)], null),
                new("Hand", 3, [], null), new("Library", 40, [], null)
            ]) { TeamId = id, LandPlaysUsed = 0, LandPlayLimit = 1,
                Counters = ImmutableDictionary<string, int>.Empty.Add("energy", 2),
                States = [new("energy", "Energy", "resource", 2, null, "Public resource text")] }).ToImmutableArray();
        var state = new GameSnapshot("public-facts-game", "playing", null, null, 0, 12, "Main phase", "MAIN1", 0,
            players, [
                new("Public source", "A trigger", StackActionKind.TriggeredAbility, Face("departed-portrait", "Historical source")) {
                    Id = "stack-trigger", ExecutionId = "execution-trigger", ControllerId = 0,
                    Targets = [new("card", "b", "Blocker", 1), new("player", "1", "Player 1", 1), new("card", "spell-portrait", "A public spell", 2)] },
                new("Public spell", "Spell prose", StackActionKind.Spell, Face("spell-portrait", "Public spell", "Instant")) {
                    Id = "stack-spell", ExecutionId = "execution-spell", ControllerId = 2, Targets = [new("card", "walker", "Walker", 1)] }
            ], new([
                new("a", 1, ["b"], ["b"]) { Blocked = true, Defender = new("player", "1", 1, "Player 1") },
                new("c", 1, [], []) { Defender = new("card", "walker", 1, "Walker") }
            ], [], null) { DeclarationId = "viewer-0-declaration" }, null) { BoardRevision = 42, Revision = 100,
                Executions = new(1, 2, [
                    new("execution-trigger", "execution-trigger", ExecutionKind.Trigger, ExecutionStep.Stack) {
                        ActorPlayerId = 0, SourceCardId = "departed-portrait", SourceName = "Historical source" },
                    new("execution-spell", "execution-spell", ExecutionKind.Spell, ExecutionStep.Stack) {
                        ActorPlayerId = 2, SourceCardId = "spell-portrait", SourceName = "Public spell" }
                ], [new(1, "execution-trigger", ExecutionStep.Stack), new(2, "execution-spell", ExecutionStep.Stack)])
            };
        state = ChangeZone(state, 0, "Battlefield", zone => zone with { Count = 3, Cards = [
            Face("a", "Attacker") with { Attacking = true, Attachments = [new("equipment", "Equipment", "Artifact")] },
            Face("equipment", "Equipment", "Artifact Equipment") with { AttachedTo = new("a", "Attacker", "Creature") },
            Face("morph", "Face-down card") with { FaceDown = true }
        ] });
        state = ChangeZone(state, 1, "Battlefield", zone => zone with { Count = 2, Cards = [Face("b", "Blocker") with { Blocking = true }, Face("walker", "Walker", "Planeswalker")] });
        state = ChangeZone(state, 2, "Battlefield", zone => zone with { Count = 1, Cards = [Face("c", "Other attacker") with { Attacking = true }] });
        return ChangeZone(state, 0, "Hand", zone => zone with {
            Cards = [Face("PRIVATE_NOT_A_PUBLIC_FACT", "PRIVATE_NOT_A_PUBLIC_FACT") with { Key = "PRIVATE_NOT_A_PUBLIC_FACT" }] });
    }

    private static GameSnapshot Perspective(GameSnapshot state)
    {
        state = state with { ViewerId = 1, Revision = 700, Status = "resolving", Phase = "Localized phase",
            PriorityPlayerId = 3, DecisionOwnerId = 2, Notices = ["A local notice"],
            Stack = state.Stack.Select(item => item with { Name = "Localized source", Text = "Localized stack prose",
                Card = item.Card! with { Name = "A private historical portrait", Key = "viewer-private-key" },
                Targets = item.Targets.Reverse().Select(target => target with { Name = "You" }).ToImmutableArray() }).ToImmutableArray(),
            Combat = state.Combat! with { DeclarationId = "viewer-1-declaration", AttackValid = false,
                BlockProblem = "Another audience's problem", AttackOptions = [new("c", [new("player", "3", 3, "You")])],
                Attackers = state.Combat.Attackers.Reverse().Select(attack => attack with {
                    EligibleBlockerIds = ["private-option"], Defender = attack.Defender! with { Name = "You" }
                }).ToImmutableArray() }
        };
        state = state with { Players = state.Players.Reverse().Select(player => player with {
            Name = player.Id == 1 ? "You" : "Localized player", Priority = player.Id == 3,
            States = player.States.Select(value => value with { Label = "Localized label", Detail = "Localized resource text" }).ToImmutableArray(),
            Zones = player.Zones.Reverse().Select(zone => zone.Name switch {
                "Hand" => zone with { Cards = player.Id == 1 ? [new Card { Name = "PRIVATE_NOT_A_PUBLIC_FACT", Key = "private-hand" }] : [] },
                "Library" => zone with { Cards = [new Card { Name = "PRIVATE_NOT_A_PUBLIC_FACT" }], TopCard = new() { Name = "PRIVATE_NOT_A_PUBLIC_FACT" } },
                "Exile" => zone with { Cards = zone.Cards.Add(new() { FaceDown = true, Name = "PRIVATE_NOT_A_PUBLIC_FACT", VisualId = "private-exile" }) },
                "Battlefield" => zone with { Cards = zone.Cards.Reverse().Select(card => card with {
                    Key = "different-opaque-key", Selectable = true, Highlighted = true, Text = "Localized rules prose",
                    Name = card.FaceDown ? "PRIVATE_NOT_A_PUBLIC_FACT" : card.Name,
                    ManaCost = card.FaceDown ? "{8}" : card.ManaCost,
                    VisualId = card.FaceDown ? "private-morph-visual" : card.VisualId,
                    OtherFace = card.FaceDown ? new("Secret back", "Secret type", "{9}", "Secret text", 9, 9, "Secret art", "back") : card.OtherFace,
                    AttachedTo = card.AttachedTo is { } parent ? parent with { Name = "Localized parent", Type = "Localized type" } : null,
                    Attachments = card.Attachments.Select(value => value with { Name = "Localized child", Type = "Localized type" }).ToImmutableArray()
                }).ToImmutableArray() },
                _ => zone
            }).ToImmutableArray()
        }).ToImmutableArray() };
        return state;
    }

    private static GameSnapshot ChangePlayer(GameSnapshot state, int id, Func<Player, Player> change) => state with {
        Players = state.Players.Select(player => player.Id == id ? change(player) : player).ToImmutableArray()
    };
    private static GameSnapshot ChangeExecution(GameSnapshot state, string id, Func<ExecutionOccurrence, ExecutionOccurrence> change) => state with {
        Executions = state.Executions! with { Occurrences = state.Executions.Occurrences.Select(occurrence => occurrence.Id == id ? change(occurrence) : occurrence).ToImmutableArray() }
    };
    private static GameSnapshot ReidentifyExecution(GameSnapshot state, string oldId, string nextId) => state with {
        Stack = state.Stack.Select(item => item.ExecutionId == oldId ? item with { ExecutionId = nextId } : item).ToImmutableArray(),
        Executions = state.Executions! with {
            Occurrences = state.Executions.Occurrences.Select(occurrence => occurrence.Id == oldId
                ? occurrence with { Id = nextId, BudgetRootId = nextId } : occurrence).ToImmutableArray(),
            Transitions = state.Executions.Transitions.Select(transition => transition.ExecutionId == oldId
                ? transition with { ExecutionId = nextId } : transition).ToImmutableArray()
        }
    };
    private static GameSnapshot ChangeZone(GameSnapshot state, int id, string name, Func<Zone, Zone> change) => ChangePlayer(state, id,
        player => player with { Zones = player.Zones.Select(zone => zone.Name == name ? change(zone) : zone).ToImmutableArray() });
    private static GameSnapshot ChangeCard(GameSnapshot state, int id, string zone, string cardId, Func<Card, Card> change) => ChangeZone(state, id, zone,
        value => value with { Cards = value.Cards.Select(card => card.CombatId == cardId ? change(card) : card).ToImmutableArray() });
    private static GameSnapshot MoveController(GameSnapshot state)
    {
        var card = state.Players.Single(player => player.Id == 0).Zone("Battlefield").Cards.Single(value => value.CombatId == "a");
        state = ChangeZone(state, 0, "Battlefield", zone => zone with { Count = zone.Count - 1, Cards = zone.Cards.Remove(card) });
        return ChangeZone(state, 2, "Battlefield", zone => zone with { Count = zone.Count + 1, Cards = zone.Cards.Add(card) });
    }
}
