using System.Collections.Immutable;
using Mana.Magic;
using Mana.Contracts;
using Mana.Renderer;

namespace Mana.Table;

// Deterministic presentation test double. It validates scoped commands and models
// a few explicit projections, not Magic rules. Real Forge checks run separately.
internal sealed class InteractionScenarioEngine : ICardEngine, ISourceActions
{
    private GameSnapshot state;
    private int revision;
    private string? handPlacementDecisionId;
    public string Name => "Presentation scenario";
    public List<GameCommand> Replies { get; } = [];
    internal Func<string, string, string, SourceActionSet>? SourceActionRead { get; set; }
    public bool SupportsSourceActions => SourceActionRead != null;
    public Task<SourceActionSet> SourceActionsAsync(string gameId, string decisionId, string sourceCardKey) =>
        SourceActionRead is { } read ? Task.FromResult(read(gameId, decisionId, sourceCardKey))
            : Task.FromException<SourceActionSet>(new NotSupportedException("This presentation fixture has no source-action reader."));
    internal bool RejectNextReply { get; set; }
    internal Func<GameCommand, Task>? ReplyOverride { get; set; }
    internal int RejectedReplies { get; private set; }
    internal bool PriorityHasOtherResponse { get; set; }
    public InteractionScenarioEngine()
    {
        Card Creature(string id, string name = "Llanowar Elves") => new() { VisualId = id, CombatId = id, Name = name, Type = "Creature", Power = 2, Toughness = 2 };
        var players = Enumerable.Range(0, 4).Select(i => new Player(i, i == 0 ? "You" : "Opponent " + i, 40, i == 0, false, ImmutableDictionary<string, int>.Empty, [
            new("Battlefield", i == 0 ? 13 : i == 1 ? 2 : 0, (i == 0 ? Enumerable.Range(0, 13).Select(n => Creature("own-" + n)).ToArray() : i == 1 ? [Creature("enemy-a", "Goreclaw, Terror of Qal Sisma"), Creature("enemy-b")] : []).ToImmutableArray(), null),
            new("Hand", i == 0 ? 0 : 7, [], null), new("Command", 0, [], null), new("Library", 80, [], null), new("Graveyard", 0, [], null), new("Exile", 0, [], null)
        ])).ToArray();
        state = new("interaction-scenario", "playing", null, null, 0, 5, "Declare attackers", "COMBAT_DECLARE_ATTACKERS", 0, players.ToImmutableArray(), [],
            new([], new[] { "own-0", "own-9", "own-12" }.Select(id => new AttackOption(id, [new("player", "1", 1, "Opponent 1"), new("player", "2", 2, "Opponent 2")])).ToImmutableArray(), null),
            new Decision("", new DecisionContext(), new AttackPayload(new DecisionReply("Confirm attackers", true))));
        Publish();
    }
    public GameSnapshot State => state;
    internal void Present(GameSnapshot snapshot) { handPlacementDecisionId = null; state = snapshot; Publish(); }
    internal void PresentHandPlacement(GameSnapshot snapshot)
    {
        if (snapshot.Decision is not { Payload: OptionsPayload { Selection.Range.Min: 0, Selection.Range.Max: 1 } or AbilityChoicePayload { Selection.Range.Min: 0, Selection.Range.Max: 1 } or OrderingPayload { Selection.Range.Min: 0, Selection.Range.Max: 1 } } decision
            || DecisionDockPresentation.From(snapshot, new()).Selection!.Choices.Any(choice => choice.Card == null))
            throw new ArgumentException("A hand-placement fixture requires its authored optional card choice.", nameof(snapshot));
        Present(snapshot);
        handPlacementDecisionId = state.Decision!.Id;
    }
    private void Publish()
    {
        string prompt = "scenario-" + ++revision;
        state = state with {
            Revision = revision,
            Decision = state.Decision == null ? null : state.Decision with { Id = prompt },
            Players = state.Players.Select(p => p with { Zones = p.Zones.Select(z => z with { Cards = z.Cards.Select(c => c with {
                Key = prompt + ":" + c.VisualId,
                Attacking = state.Combat?.Attackers.Any(a => a.CardId == c.CombatId) == true,
                Blocking = state.Combat?.Attackers.Any(a => a.BlockerIds.Contains(c.CombatId)) == true
            }).ToImmutableArray() }).ToImmutableArray() }).ToImmutableArray()
        };
    }
    public Task ExecuteAsync(GameCommand reply)
    {
        if (reply.GameId != state.Id || reply is DecisionCommand scoped && scoped.DecisionId != state.Decision?.Id)
            throw new InvalidOperationException($"Scenario received stale {reply.GetType().Name}; current {state.Decision?.Id}");
        reply.Validate();
        if (ReplyOverride is { } replyOverride) return replyOverride(reply);
        if (RejectNextReply) { RejectNextReply = false; RejectedReplies++; ObservationsAtRejection = Observations; throw new EngineCommandRejectedException("Controlled rejected play"); }
        Card Find(string? key) => state.Players.SelectMany(p => p.Zones).SelectMany(z => z.Cards).Single(c => c.Key == key);
        if (reply is IgnorePriorityAbility or RestorePriorityAbility) {
            bool ignored = reply is IgnorePriorityAbility;
            string abilityId = reply switch { IgnorePriorityAbility ignore => ignore.AbilityId, RestorePriorityAbility restore => restore.AbilityId, _ => "" };
            var available = (state.Decision?.Payload as PriorityPayload)?.Abilities ?? [];
            var ability = available.Concat(state.IgnoredPriorityAbilities).FirstOrDefault(a => a.Id == abilityId)
                ?? throw new InvalidOperationException("Unprojected ability preference");
            if (ignored && !available.Any(a => a.Id == abilityId)) throw new InvalidOperationException("Cannot ignore an unavailable ability");
            var policies = state.IgnoredPriorityAbilities.Where(a => a.Id != abilityId).ToList();
            if (ignored) policies.Add(ability with { IgnoredUntilNextTurn = true });
            var abilities = available.Select(a => a.Id == abilityId ? a with { IgnoredUntilNextTurn = ignored } : a).ToArray();
            state = state with { IgnoredPriorityAbilities = policies.ToImmutableArray(), Decision = state.Decision == null ? null : state.Decision with { Payload = ((PriorityPayload)(state.Decision).Payload) with { Abilities = abilities.ToImmutableArray(), CanAutoPass = !PriorityHasOtherResponse && abilities.All(a => a.IgnoredUntilNextTurn) } } };
        } else if (reply is ToggleAttackPlayer attack) {
            var card = Find(attack.Attacker);
            if (state.Decision?.Payload is not AttackPayload || attack.Player is not (1 or 2) || !new[] { "own-0", "own-9", "own-12" }.Contains(card.CombatId)) throw new InvalidOperationException("Illegal attack escaped the UI");
            int defender = attack.Player;
            var existing = state.Combat!.Attackers.FirstOrDefault(a => a.CardId == card.CombatId);
            var attacks = state.Combat.Attackers.Where(a => a.CardId != card.CombatId).ToList();
            if (existing?.DefendingPlayerId != defender) attacks.Add(new(card.CombatId, defender, [], []) { Defender = new("player", defender.ToString(), defender, "Opponent " + defender) });
            state = state with { Combat = state.Combat with { Attackers = attacks.ToImmutableArray() } };
        } else if (reply is ToggleBlock block) {
            var attacker = Find(block.Attacker); var blocker = Find(block.Blocker);
            if (state.Decision?.Payload is not BlockPayload || attacker.CombatId != "enemy-a" || blocker.CombatId is not ("own-9" or "own-12")) throw new InvalidOperationException("Illegal block escaped the UI");
            var attacks = state.Combat!.Attackers.Select(a => {
                if (a.CardId != attacker.CombatId) return a;
                var blockers = a.BlockerIds.ToList(); if (!blockers.Remove(blocker.CombatId)) blockers.Add(blocker.CombatId);
                return a with { BlockerIds = blockers.ToImmutableArray(), Blocked = blockers.Count > 0 };
            }).ToArray();
            state = state with { Combat = state.Combat with { Attackers = attacks.ToImmutableArray(), BlockProblem = attacks[0].BlockerIds.Length == 1 ? "This attacker needs at least two blockers." : null } };
        } else if (reply is PlaceFromHand placement) {
            var card = Find(placement.Card);
            if (HandPlacements.Start(state, card, placement.AbilityId) == null) throw new InvalidOperationException("Unprojected hand placement escaped the UI");
            var ability = ((PriorityPayload)state.Decision!.Payload).Abilities.Single(a => a.Id == placement.AbilityId);
            var source = Find(ability.SourceCardKey);
            state = state with { Stack = [new(ability.SourceName, ability.Text, true, source) { Id = "placement-on-stack" }],
                Decision = state.Decision with { Payload = ((PriorityPayload)(state.Decision).Payload) with { Abilities = [] } } };
        } else if (reply is SelectCard or PlayCard or ActivateCard or UseManaSource) {
            string cardKey = reply switch { SelectCard select => select.Card, PlayCard play => play.Card, ActivateCard activate => activate.Card, UseManaSource mana => mana.Card, _ => "" };
            var card = Find(cardKey);
            if (!card.Selectable || state.Decision?.Payload is not (PriorityPayload or TargetSelectionPayload or EntitySelectionPayload or PaymentPayload)) throw new InvalidOperationException("Illegal card selection escaped the UI");
            if (state.Decision.Payload is PriorityPayload or PaymentPayload) {
                if (state.Viewer!.Zone("Battlefield").Cards.Contains(card)) {
                    state = state with { Decision = new Decision("", new DecisionContext(Title: "Activate " + card.Name, Source: new DecisionSource(Card: card, Zone: "Battlefield")), new AbilityChoicePayload(new ChoiceSelection([new(0, "Tap: Add one green mana", null)], new SelectionRange(0, 1), []))) };
                } else {
                    string source = new[] { "Hand", "Command", "Graveyard", "Exile" }.FirstOrDefault(zone => state.Viewer.Zone(zone).Cards.Contains(card))
                        ?? throw new InvalidOperationException("Unexpected scenario source " + card.VisualId);
                    if (source is "Graveyard" or "Exile") state = state with { Decision = new Decision("", new DecisionContext(Source: new DecisionSource(Card: card, Zone: source)), new AbilityChoicePayload(new ChoiceSelection([new(0, source == "Graveyard" ? "Flashback — pay the ability's cost" : "Play from exile — pay the cost", null)], new SelectionRange(0, 1), []))) };
                    else state = state with { Players = state.Players.Select(p => p.Id != state.ViewerId ? p : p with { Zones = p.Zones.Select(z =>
                    z.Name == source ? z with { Count = z.Count - 1, Cards = z.Cards.Where(c => c != card).ToImmutableArray() }
                    : z.Name == "Battlefield" ? z with { Count = z.Count + 1, Cards = [.. z.Cards, card] } : z).ToImmutableArray() }).ToImmutableArray() };
                }
            }
        } else if (reply is SelectPlayer selectedPlayer && state.Decision is { Payload: EntitySelectionPayload } playerChoice) {
            if (!((EntitySelectionPayload)playerChoice.Payload).Players.Contains(selectedPlayer.Player)) throw new InvalidOperationException("Illegal player choice escaped the UI");
            state = state with { Decision = null };
        } else if (reply is AutoPassPriority && state.Decision is { Payload: PriorityPayload { CanAutoPass: true } }) {
            state = state with { Decision = null };
        } else if (reply is ChooseOptions or OrderOptions or AcknowledgeReveal && state.Decision is { Payload: OptionsPayload or AbilityChoicePayload or OrderingPayload or RevealPayload } decision) {
            var choices = reply switch { ChooseOptions choose => choose.Choices, OrderOptions order => order.Choices, _ => [] };
            var selection = DecisionDockPresentation.From(state, new()).Selection;
            if (decision.Payload is RevealPayload ? reply is not AcknowledgeReveal : selection == null
                || choices.Length < selection.Range.Min || choices.Length > selection.Range.Max
                || (decision.Payload is OrderingPayload) != (reply is OrderOptions)
                || choices.Any(index => !selection.Choices.Any(choice => choice.Index == index)))
                throw new InvalidOperationException("Invalid gallery selection");
            bool handPlacement = decision.Id == handPlacementDecisionId;
            if (handPlacement && choices.Length == 1) {
                var chosen = selection!.Choices.Single(c => c.Index == choices[0]).Card!;
                var card = state.Viewer!.Zone("Hand").Cards.Single(c => CardViews.Same(c, chosen));
                state = state with { Players = state.Players.Select(p => p.Id != state.ViewerId ? p : p with { Zones = p.Zones.Select(z =>
                    z.Name == "Hand" ? z with { Count = z.Count - 1, Cards = z.Cards.Where(c => c != card).ToImmutableArray() }
                    : z.Name == "Battlefield" ? z with { Count = z.Count + 1, Cards = [.. z.Cards, card] } : z).ToImmutableArray() }).ToImmutableArray() };
            }
            state = state with { Decision = null, Stack = handPlacement ? [] : state.Stack };
            handPlacementDecisionId = null;
        } else if (reply is AllocateAmounts amounts && state.Decision is { Payload: AllocationPayload } allocation) {
            if (AllocationGuide.Problem((AllocationPayload)allocation.Payload, amounts.Amounts) != null)
                throw new InvalidOperationException("Invalid allocation escaped the UI");
            state = state with { Decision = null };
        } else if (reply is SkipAllocation && state.Decision is { Payload: AllocationPayload { MaySkip: true } }) {
            state = state with { Decision = null };
        } else throw new InvalidOperationException("Unexpected scenario action: " + reply.GetType().Name);
        Replies.Add(reply); Publish(); return Task.CompletedTask;
    }
    public void BeginBlocks()
    {
        state = state with { ActivePlayerId = 1, PhaseKey = "COMBAT_DECLARE_BLOCKERS", Phase = "Declare blockers", Decision = new Decision("", new DecisionContext(), new BlockPayload(new DecisionReply("Confirm", true))),
            Combat = new([new("enemy-a", 0, [], ["own-9", "own-12"]) { Defender = new("player", "0", 0, "You") }, new("enemy-b", 0, [], []) { Defender = new("player", "0", 0, "You"), Blocked = true }], [], null) };
        Publish();
    }
    public void Stack(params string[] ids)
    {
        state = state with { PhaseKey = "MAIN2", Phase = "Main phase", Combat = null, Decision = new Decision("", new DecisionContext(), new PriorityPayload(new DecisionReply("Confirm", true), null, CanAutoPass: false, QuietYieldToken: "", Abilities: [])),
            Stack = ids.Select(id => new StackItem("Ability " + id, "This is the visible description for ability " + id + ".", true, state.Players[1].Zone("Battlefield").Cards[0]) { Id = id }).ToImmutableArray() };
        Publish();
    }
    public void Finish() { state = state with { Status = "finished", Result = "Victory", Stack = [], Decision = null, Players = state.Players.Select(p => p with { Eliminated = p.Id != 0 }).ToImmutableArray() }; Publish(); }
    public void ChoosePlayer(params int[] players)
    {
        state = state with { PhaseKey = "MAIN1", Phase = "Main phase", ActivePlayerId = 0, Combat = null, Stack = [],
            Decision = new Decision("", new DecisionContext(Message: "Bojuka Bog — Choose a player to exile their graveyard.", Source: new DecisionSource(Card: new() { Name = "Bojuka Bog", Type = "Land" })), new EntitySelectionPayload(new DecisionReply("Confirm", false), new DecisionReply("Cancel", false), players.ToImmutableArray(), null)) };
        Publish();
    }
    public void ChooseCards(int min, int max, bool ordered = false)
    {
        var cards = new[] { "own-9", "own-12", "enemy-a" }.Select(id => CombatGuide.Find(state, id)!).ToArray();
        state = state with { Combat = null, Stack = [], Decision = new Decision("", new DecisionContext(Message: ordered ? "Choose these creatures in order." : "Choose creatures on the battlefield."), (ordered) ? new OrderingPayload(new ChoiceSelection(cards.Select((card, i) => new Choice(i, card.Name, card with { VisualId = "", Key = "", CombatId = "" }) { CardId = card.VisualId }).ToImmutableArray(), new SelectionRange(min, max), [])) : new OptionsPayload(new ChoiceSelection(cards.Select((card, i) => new Choice(i, card.Name, card with { VisualId = "", Key = "", CombatId = "" }) { CardId = card.VisualId }).ToImmutableArray(), new SelectionRange(min, max), []))) };
        Publish();
    }
    public Task InitializeAsync() => Task.CompletedTask;
    private int observations;
    internal int Observations => Volatile.Read(ref observations);
    internal int ObservationsAtRejection { get; private set; }
    internal Func<Task<GameSnapshot?>>? ObserveOverride { get; set; }
    public Task<GameSnapshot?> ObserveAsync() { Interlocked.Increment(ref observations); return ObserveOverride?.Invoke() ?? Task.FromResult<GameSnapshot?>(state); }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public Task<ImmutableArray<DeckSummary>> DecksAsync() => throw new NotSupportedException();
    public Task<DeckDetails> OpenDeckAsync(string id) => throw new NotSupportedException();
    public Task<CatalogPage> SearchCardsAsync(CardQuery query) => throw new NotSupportedException();
    public Task<DeckDetails> CreateDeckAsync(string name) => throw new NotSupportedException();
    public Task<DeckDetails> EditDeckAsync(string deckId, long revision, ImmutableArray<DeckEdit> edits) => throw new NotSupportedException();
    public Task<DeckDetails> RenameDeckAsync(string deckId, long revision, string name) => throw new NotSupportedException();
    public Task<DeckDetails> DuplicateDeckAsync(string deckId, long revision, string name) => throw new NotSupportedException();
    public Task<DeckDetails> UndoDeckAsync(string deckId, long revision) => throw new NotSupportedException();
    public Task<DeckDetails> RedoDeckAsync(string deckId, long revision) => throw new NotSupportedException();
    public Task<DeckDetails> SaveDeckAsync(string deckId, long revision) => throw new NotSupportedException();
    public Task<string> ExportDeckAsync(string deckId, long revision) => throw new NotSupportedException();
    public Task<DeckDetails> SetCommandersAsync(string deckId, long revision, ImmutableArray<string> cardIds) => throw new NotSupportedException();
    public Task<ImmutableArray<DeckPreset>> PresetsAsync() => throw new NotSupportedException();
    public Task<DeckSummary> ImportAsync(string name, string text) => throw new NotSupportedException();
    public Task<DeckSummary> ImportPresetAsync(string id) => throw new NotSupportedException();
    public Task<SoloSetup> PrepareSoloAsync(string deckId) => throw new NotSupportedException();
    public Task StartSoloAsync(string deckId, ImmutableArray<string> opponents) => throw new NotSupportedException();
    public Task<Lobby> HostAsync(int players, bool forwarding) => throw new NotSupportedException();
    public Task<Lobby> JoinAsync(string address) => throw new NotSupportedException();
    public Task<Lobby> LobbyAsync() => throw new NotSupportedException();
    public Task<Lobby> SelectDeckAsync(LobbyDeckSelection selection) => throw new NotSupportedException();
    public Task SetReadyAsync(bool ready) => throw new NotSupportedException();
    public Task StartAsync() => throw new NotSupportedException();
    public Task ConcedeAsync(string id) => throw new NotSupportedException();
    public Task ReturnToLobbyAsync() => throw new NotSupportedException();
    public Task LeaveAsync() => throw new NotSupportedException();
}
