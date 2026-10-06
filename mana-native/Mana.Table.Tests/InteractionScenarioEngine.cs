using Mana.Magic;
using Mana.Contracts;
using Mana.Renderer;

namespace Mana.Table;

// Deterministic presentation test double. It validates scoped commands and models
// a few explicit projections, not Magic rules. Real Forge checks run separately.
internal sealed class InteractionScenarioEngine : ICardEngine
{
    private GameSnapshot state;
    private int revision;
    public string Name => "Presentation scenario";
    public List<DecisionReply> Replies { get; } = [];
    public InteractionScenarioEngine()
    {
        Card Creature(string id, string name = "Llanowar Elves") => new() { VisualId = id, CombatId = id, Name = name, Type = "Creature", Power = 2, Toughness = 2 };
        var players = Enumerable.Range(0, 4).Select(i => new Player(i, i == 0 ? "You" : "Opponent " + i, 40, i == 0, false, [], [
            new("Battlefield", i == 0 ? 13 : i == 1 ? 2 : 0, i == 0 ? Enumerable.Range(0, 13).Select(n => Creature("own-" + n)).ToArray() : i == 1 ? [Creature("enemy-a", "Goreclaw, Terror of Qal Sisma"), Creature("enemy-b")] : [], null),
            new("Hand", i == 0 ? 0 : 7, [], null), new("Command", 0, [], null), new("Library", 80, [], null), new("Graveyard", 0, [], null), new("Exile", 0, [], null)
        ])).ToArray();
        state = new("interaction-scenario", "playing", null, null, 0, 5, "Declare attackers", "COMBAT_DECLARE_ATTACKERS", 0, players, [],
            new([], new[] { "own-0", "own-9", "own-12" }.Select(id => new AttackOption(id, [new("player", "1", 1, "Opponent 1"), new("player", "2", 2, "Opponent 2")])).ToArray(), null),
            new() { Kind = "input", Intent = DecisionIntent.Attack, OkEnabled = true, Ok = "Confirm attackers" });
        Publish();
    }
    public GameSnapshot State => state;
    internal void Present(GameSnapshot snapshot) { state = snapshot; Publish(); }
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
            }).ToArray() }).ToArray() }).ToArray()
        };
    }
    public Task ReplyAsync(DecisionReply reply)
    {
        if (reply.GameId != state.Id || reply.DecisionId != state.Decision?.Id)
            throw new InvalidOperationException($"Scenario received stale {reply.Action}: {reply.DecisionId}, current {state.Decision?.Id}");
        Card Find(string? key) => state.Players.SelectMany(p => p.Zones).SelectMany(z => z.Cards).Single(c => c.Key == key);
        if (reply.Action == ReplyAction.AssignAttack) {
            var card = Find(reply.Attacker);
            if (state.Decision?.Intent != DecisionIntent.Attack || reply.Player is not (1 or 2) || !new[] { "own-0", "own-9", "own-12" }.Contains(card.CombatId)) throw new InvalidOperationException("Illegal attack escaped the UI");
            int defender = reply.Player.Value;
            var existing = state.Combat!.Attackers.FirstOrDefault(a => a.CardId == card.CombatId);
            var attacks = state.Combat.Attackers.Where(a => a.CardId != card.CombatId).ToList();
            if (existing?.DefendingPlayerId != defender) attacks.Add(new(card.CombatId, defender, [], []) { Defender = new("player", defender.ToString(), defender, "Opponent " + defender) });
            state = state with { Combat = state.Combat with { Attackers = attacks.ToArray() } };
        } else if (reply.Action == ReplyAction.AssignBlock) {
            var attacker = Find(reply.Attacker); var blocker = Find(reply.Blocker);
            if (state.Decision?.Intent != DecisionIntent.Block || attacker.CombatId != "enemy-a" || blocker.CombatId is not ("own-9" or "own-12")) throw new InvalidOperationException("Illegal block escaped the UI");
            var attacks = state.Combat!.Attackers.Select(a => {
                if (a.CardId != attacker.CombatId) return a;
                var blockers = a.BlockerIds.ToList(); if (!blockers.Remove(blocker.CombatId)) blockers.Add(blocker.CombatId);
                return a with { BlockerIds = blockers.ToArray(), Blocked = blockers.Count > 0 };
            }).ToArray();
            state = state with { Combat = state.Combat with { Attackers = attacks, BlockProblem = attacks[0].BlockerIds.Length == 1 ? "This attacker needs at least two blockers." : null } };
        } else if (reply.Action == ReplyAction.SelectCard) {
            var card = Find(reply.Card);
            if (!card.Selectable || state.Decision?.Intent is not (DecisionIntent.Priority or DecisionIntent.Selection)) throw new InvalidOperationException("Illegal card selection escaped the UI");
            if (state.Decision.Intent == DecisionIntent.Priority) {
                if (state.Viewer!.Zone("Battlefield").Cards.Contains(card)) {
                    state = state with { Decision = new() { Kind = "choice", Title = "Activate " + card.Name, Context = "playAbility", SourceCard = card,
                        SourceZone = "Battlefield", Min = 0, Max = 1, Choices = [new(0, "Tap: Add one green mana", null)] } };
                } else {
                    if (!state.Viewer.Zone("Hand").Cards.Contains(card)) throw new InvalidOperationException("Unexpected scenario source " + card.VisualId);
                    state = state with { Players = state.Players.Select(p => p.Id != state.ViewerId ? p : p with { Zones = p.Zones.Select(z =>
                    z.Name == "Hand" ? z with { Count = z.Count - 1, Cards = z.Cards.Where(c => c != card).ToArray() }
                    : z.Name == "Battlefield" ? z with { Count = z.Count + 1, Cards = [.. z.Cards, card] } : z).ToArray() }).ToArray() };
                }
            }
        } else if (reply.Action == ReplyAction.SelectPlayer && state.Decision is { Intent: DecisionIntent.Selection } playerChoice) {
            if (reply.Player is not { } player || !playerChoice.PlayerChoices.Contains(player)) throw new InvalidOperationException("Illegal player choice escaped the UI");
            state = state with { Decision = null };
        } else if (reply.Action == ReplyAction.AutoPass && state.Decision is { Intent: DecisionIntent.Priority, CanAutoPass: true }) {
            state = state with { Decision = null };
        } else if (reply.Action == ReplyAction.Choose && state.Decision is { Kind: "choice" or "reveal" } decision) {
            if (reply.Choices == null || reply.Choices.Length < decision.Min || reply.Choices.Length > decision.Max)
                throw new InvalidOperationException("Invalid gallery selection");
            state = state with { Decision = null };
        } else throw new InvalidOperationException("Unexpected scenario action: " + reply.Action);
        Replies.Add(reply); Publish(); return Task.CompletedTask;
    }
    public void BeginBlocks()
    {
        state = state with { ActivePlayerId = 1, PhaseKey = "COMBAT_DECLARE_BLOCKERS", Phase = "Declare blockers", Decision = new() { Kind = "input", Intent = DecisionIntent.Block, OkEnabled = true },
            Combat = new([new("enemy-a", 0, [], ["own-9", "own-12"]) { Defender = new("player", "0", 0, "You") }, new("enemy-b", 0, [], []) { Defender = new("player", "0", 0, "You"), Blocked = true }], [], null) };
        Publish();
    }
    public void Stack(params string[] ids)
    {
        state = state with { PhaseKey = "MAIN2", Phase = "Main phase", Combat = null, Decision = new() { Kind = "input", Intent = DecisionIntent.Priority, OkEnabled = true },
            Stack = ids.Select(id => new StackItem("Ability " + id, "This is the visible description for ability " + id + ".", true, state.Players[1].Zone("Battlefield").Cards[0]) { Id = id }).ToArray() };
        Publish();
    }
    public void Finish() { state = state with { Status = "finished", Result = "Victory", Stack = [], Decision = null, Players = state.Players.Select(p => p with { Eliminated = p.Id != 0 }).ToArray() }; Publish(); }
    public void ChoosePlayer(params int[] players)
    {
        state = state with { PhaseKey = "MAIN1", Phase = "Main phase", ActivePlayerId = 0, Combat = null, Stack = [],
            Decision = new() { Kind = "input", Intent = DecisionIntent.Selection, PlayerChoices = players,
                Message = "Bojuka Bog — Choose a player to exile their graveyard.", SourceCard = new() { Name = "Bojuka Bog", Type = "Land" } } };
        Publish();
    }
    public void ChooseCards(int min, int max, bool ordered = false)
    {
        var cards = new[] { "own-9", "own-12", "enemy-a" }.Select(id => CombatGuide.Find(state, id)!).ToArray();
        state = state with { Combat = null, Stack = [], Decision = new() { Kind = "choice", Min = min, Max = max, Ordered = ordered,
            Message = ordered ? "Choose these creatures in order." : "Choose creatures on the battlefield.",
            Choices = cards.Select((card, i) => new Choice(i, card.Name, card with { VisualId = "", Key = "", CombatId = "" }) { CardId = card.VisualId }).ToArray() } };
        Publish();
    }
    public Task InitializeAsync() => Task.CompletedTask;
    public Task<GameSnapshot?> ObserveAsync() => Task.FromResult<GameSnapshot?>(state);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public Task<IReadOnlyList<DeckSummary>> DecksAsync() => throw new NotSupportedException();
    public Task<DeckDetails> OpenDeckAsync(string id) => throw new NotSupportedException();
    public Task<CatalogPage> SearchCardsAsync(CardQuery query) => throw new NotSupportedException();
    public Task<DeckDetails> CreateDeckAsync(string name) => throw new NotSupportedException();
    public Task<DeckDetails> EditDeckAsync(string deckId, long revision, IReadOnlyList<DeckEdit> edits) => throw new NotSupportedException();
    public Task<DeckDetails> RenameDeckAsync(string deckId, long revision, string name) => throw new NotSupportedException();
    public Task<DeckDetails> DuplicateDeckAsync(string deckId, long revision, string name) => throw new NotSupportedException();
    public Task<DeckDetails> UndoDeckAsync(string deckId, long revision) => throw new NotSupportedException();
    public Task<DeckDetails> RedoDeckAsync(string deckId, long revision) => throw new NotSupportedException();
    public Task<DeckDetails> SaveDeckAsync(string deckId, long revision) => throw new NotSupportedException();
    public Task<string> ExportDeckAsync(string deckId, long revision) => throw new NotSupportedException();
    public Task<DeckDetails> SetCommandersAsync(string deckId, long revision, IReadOnlyList<string> cardIds) => throw new NotSupportedException();
    public Task<IReadOnlyList<DeckPreset>> PresetsAsync() => throw new NotSupportedException();
    public Task<DeckSummary> ImportAsync(string name, string text) => throw new NotSupportedException();
    public Task<DeckSummary> ImportPresetAsync(string id) => throw new NotSupportedException();
    public Task<SoloSetup> PrepareSoloAsync(string deckId) => throw new NotSupportedException();
    public Task StartSoloAsync(string deckId, IReadOnlyList<string> opponents) => throw new NotSupportedException();
    public Task<Lobby> HostAsync(int players, bool forwarding) => throw new NotSupportedException();
    public Task<Lobby> JoinAsync(string address) => throw new NotSupportedException();
    public Task<Lobby> LobbyAsync() => throw new NotSupportedException();
    public Task SelectDeckAsync(string id) => throw new NotSupportedException();
    public Task SetReadyAsync(bool ready) => throw new NotSupportedException();
    public Task StartAsync() => throw new NotSupportedException();
    public Task ConcedeAsync(string id) => throw new NotSupportedException();
    public Task ReturnToLobbyAsync() => throw new NotSupportedException();
    public Task LeaveAsync() => throw new NotSupportedException();
}
