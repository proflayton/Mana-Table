using System.Collections.Immutable;

namespace Mana.Contracts;

// The aggregate is for application composition. Consumers can depend on the
// narrower catalog, setup or connection contracts without implementing all three.
public interface ICardEngine : IEngineCatalog, ITableSetup, IMatchConnection, IAsyncDisposable
{
    string Name { get; }
    Task InitializeAsync();
}
public interface IEngineCatalog
{
    Task<ImmutableArray<DeckSummary>> DecksAsync();
    Task<ImmutableArray<DeckPreset>> PresetsAsync();
    Task<DeckSummary> ImportAsync(string name, string text);
    Task<DeckSummary> ImportPresetAsync(string id);
    Task<DeckDetails> OpenDeckAsync(string id);
    Task<DeckDetails> SetCommandersAsync(string deckId, long revision, ImmutableArray<string> cardIds);
    Task<CatalogPage> SearchCardsAsync(CardQuery query);
    Task<DeckDetails> CreateDeckAsync(string name);
    Task<DeckDetails> EditDeckAsync(string deckId, long revision, ImmutableArray<DeckEdit> edits);
    Task<DeckDetails> RenameDeckAsync(string deckId, long revision, string name);
    Task<DeckDetails> DuplicateDeckAsync(string deckId, long revision, string name);
    Task<DeckDetails> UndoDeckAsync(string deckId, long revision);
    Task<DeckDetails> RedoDeckAsync(string deckId, long revision);
    Task<DeckDetails> SaveDeckAsync(string deckId, long revision);
    Task<string> ExportDeckAsync(string deckId, long revision);
}
public interface ICardPrintings
{
    Task<CatalogPage> CardPrintingsAsync(string cardId, int offset = 0, int limit = 6, string text = "", string finish = "");
    Task<DeckDetails> ReplacePrintingAsync(string deckId, long revision, string section, string cardId, string printingId);
}
public interface ITableSetup
{
    Task<SoloSetup> PrepareSoloAsync(string deckId);
    Task StartSoloAsync(string deckId, ImmutableArray<string> opponentIds);
    Task<Lobby> HostAsync(int players, bool automaticPortForwarding);
    Task<Lobby> JoinAsync(string address);
    Task<Lobby> LobbyAsync();
    Task SelectDeckAsync(string id);
    Task SetReadyAsync(bool ready);
    Task StartAsync();
    Task ReturnToLobbyAsync();
    Task LeaveAsync();
}
public interface ITableSeatSetup
{
    Task<Lobby> ConfigureSeatsAsync(string tableId, int players);
    Task<Lobby> SetSeatAsync(string tableId, int seat, string type, string? opponentId = null);
}
public interface IMatchConnection
{
    Task<GameSnapshot?> ObserveAsync();
    Task ExecuteAsync(GameCommand command);
    Task ConcedeAsync(string gameId);
}
// Reading a source's current engine-owned actions does not answer its decision.
public interface ISourceActions
{
    bool SupportsSourceActions => true;
    Task<SourceActionSet> SourceActionsAsync(string gameId, string decisionId, string sourceCardKey);
}
public sealed record SourceActionSet(string GameId, string DecisionId, string SourceCardKey, string SourceCardId, ImmutableArray<SourceAction> Actions);
public sealed record SourceAction(string Id, string Kind, string Text)
{
    // A disclosed linked face is reading material, never another action handle.
    // Submission still names the source and this offer's opaque Id.
    public Card? Card { get; init; }
    public string Zone { get; init; } = "";
}
// Social commands have their own ordering and never advance a game decision.
public interface ITableSocial
{
    Task<TableConversation> ConversationAsync(string tableId);
    Task<TableConversation> SendMessageAsync(string tableId, string kind, string text);
    Task<TableConversation> SetPlayerNameAsync(string tableId, string name);
}
public sealed record TableMember(string Id, int Seat, string Name);
public sealed record TableMessage(long Id, string SenderId, int Seat, string Name, string Kind, string Text);
public sealed record TableConversation(string TableId, long Revision, int LocalSeat, ImmutableArray<TableMember> Members, ImmutableArray<TableMessage> Messages);
public sealed record DeckSummary(string Id, string Name)
{
    public string LibraryToken { get; init; } = "";
}
public sealed record DeckEntry(string Section, string Id, Card Card, int Quantity, int ManaValue = 0, int ColorIdentity = 0, string Edition = "");
public sealed record DeckEdit(string Section, string CardId, int Quantity);
public sealed record CatalogCard(string Id, Card Card, int ManaValue, int ColorIdentity, string Section, string Edition);
public sealed record CardQuery(string Text = "", string Type = "", int? ColorIdentity = null, int? MaxManaValue = null, string Sort = "name", int Offset = 0, int Limit = 6);
public sealed record CatalogPage(int Total, int Offset, ImmutableArray<CatalogCard> Cards);
public sealed record CommanderOption(string Id, ImmutableArray<string> Partners)
{
    // Discovery hints describe pairing capability even before a counterpart is
    // in the deck. Only Partners supplies compatible in-deck selection handles.
    public bool CanPartner { get; init; }
    public string PartnerName { get; init; } = "";
}
public sealed record DeckIssue(string Code, string Message, string? Section, ImmutableArray<string> CardIds);
public sealed record DeckDetails(string Id, string Name, long Revision, ImmutableArray<DeckEntry> Entries,
    ImmutableArray<CommanderOption> CommanderOptions, string Problem, string SaveError, bool CanUndo = false, bool CanRedo = false)
{
    public ImmutableArray<Card> TokenPreviews { get; init; } = [];
    public ImmutableArray<DeckIssue> Issues { get; init; } = [];
    public string Format { get; init; } = "Commander";
    public string LibraryToken { get; init; } = "";
    public bool? StructureValid { get; init; }
    public string Eligibility { get; init; } = "unknown";
    public ImmutableArray<string> CommanderIds => Entries.Where(e => e.Section == "Commander").Select(e => e.Id).ToImmutableArray();
}
public sealed record DeckPreset(string Id, string Name, string Description);
// Opponent IDs are opaque engine-owned handles, just like saved deck IDs.
public sealed record AiOpponent(string Id, string Name, string Description);
public sealed record SoloSetup(string DeckId, string DeckName, ImmutableArray<AiOpponent> Opponents)
{
    public int MaxPlayers { get; init; } = 6;
}
public sealed record Address(string Label, string Url, string Invite);
public sealed record Seat(int Index, string Name, string Type, string? Deck, bool Ready, bool Local)
{
    public string? OpponentId { get; init; }
}
public sealed record Lobby(string Mode, string Status, string? Error, bool CanStart, string? StartProblem,
    bool MatchActive, string PortMapping, string? InternetInvite, ImmutableArray<Address> Addresses, ImmutableArray<Seat> Seats, string TableId = "")
{
    public ImmutableArray<AiOpponent> AiOpponents { get; init; } = [];
    public TempoSettings? Tempo { get; init; }
}
public sealed record Card
{
    public string Key { get; init; } = "";
    public string VisualId { get; init; } = "";
    public string CombatId { get; init; } = "";
    public CardAttachment? AttachedTo { get; init; }
    public ImmutableArray<CardAttachment> Attachments { get; init; } = [];
    public string Name { get; init; } = "";
    // Engine helpers and emblems are effect objects, not printed card identities.
    public string GamePieceType { get; init; } = "card";
    public string ArtName { get; init; } = "";
    public string ArtFace { get; init; } = "front";
    public string ArtSet { get; init; } = "";
    public string Edition { get; init; } = "";
    public string CollectorNumber { get; init; } = "";
    public int ArtIndex { get; init; }
    public bool Foil { get; init; }
    public CardFace? OtherFace { get; init; }
    public ImmutableArray<string> CombatKeywords { get; init; } = [];
    // Null means a legacy engine. A present empty list authoritatively clears
    // effective abilities rather than falling back to older keyword hints.
    public ImmutableArray<PublicState>? States { get; init; }
    public int? DefenderId { get; init; }
    public string? Defender { get; init; }
    public string Type { get; init; } = "";
    public string ManaCost { get; init; } = "";
    // Extra generic mana for the next cast from the command zone, from the engine.
    public int CommanderTax { get; init; }
    public string Text { get; init; } = "";
    public int? Power { get; init; }
    public int? Toughness { get; init; }
    public int Damage { get; init; }
    public bool Tapped { get; init; }
    public bool PhasedOut { get; init; }
    public bool Sick { get; init; }
    public bool FaceDown { get; init; }
    public bool Selectable { get; init; }
    public bool Highlighted { get; init; }
    public bool Attacking { get; init; }
    public bool Blocking { get; init; }
    public ImmutableDictionary<string, int> Counters { get; init; } = ImmutableDictionary<string, int>.Empty;
}
public sealed record CardAttachment(string Id, string Name, string Type);
public sealed record CardFace(string Name, string Type, string ManaCost, string OracleText, int? Power, int? Toughness, string ArtName, string ArtFace);
public sealed record Zone(string Name, int Count, ImmutableArray<Card> Cards, Card? TopCard);
// Public player mechanics share one presentation contract. Values and rules
// descriptions come from the engine; the client does not inspect card names.
public sealed record PlayerState(string Id, string Label, string Kind, int? Value, int? Maximum, string Detail);
// Human-readable engine descriptors shared by public card and table mechanics.
// Key selects a familiar icon; Label/Detail preserve qualifiers and unknown mechanics.
public sealed record PublicState(string Id, string Label, string Kind, int? Value, int? Maximum, string Detail, string Key = "");
public sealed record Player(int Id, string Name, int Life, bool Priority, bool Eliminated, ImmutableDictionary<string, int> Mana, ImmutableArray<Zone> Zones)
{
    public ImmutableArray<PlayerState> States { get; init; } = [];
    // Public engine-owned player resources; card counters live on Card separately.
    public ImmutableDictionary<string, int> Counters { get; init; } = ImmutableDictionary<string, int>.Empty;
    // Missing used count means an older engine did not project this state.
    // A null limit with a present used count means unlimited land plays.
    public int? LandPlaysUsed { get; init; }
    public int? LandPlayLimit { get; init; }
    // The rules engine's current limit; null means no maximum hand size.
    public int? MaxHandSize { get; init; } = 7;
    public ImmutableArray<CommanderDamage> CommanderDamage { get; init; } = [];
    public Zone Zone(string name) => Zones.FirstOrDefault(z => z.Name == name) ?? new(name, 0, [], null);
}
public sealed record CommanderDamage(string Name, int OwnerId, string Owner, int Damage);
public sealed record Choice(int Index, string Label, Card? Card)
{
    private string label = DecisionValue.Text(Label), cardId = "", detail = "", mana = "";
    public string Label { get => label; init => label = DecisionValue.Text(value); }
    // Optional link to an occurrence already visible on the table; never a card name.
    public string CardId { get => cardId; init => cardId = DecisionValue.Text(value); }
    public string Detail { get => detail; init => detail = DecisionValue.Text(value); }
    public string Mana { get => mana; init => mana = DecisionValue.Text(value); }
}
public sealed record LibraryChoice(int? Index, string Label, Card Card)
{
    private string label = DecisionValue.Text(Label), cardId = "";
    private Card card = DecisionValue.Required(Card, nameof(Card));
    public string Label { get => label; init => label = DecisionValue.Text(value); }
    public Card Card { get => card; init => card = DecisionValue.Required(value, nameof(Card)); }
    // Public occurrence only; this never grants a selection or play handle.
    public string CardId { get => cardId; init => cardId = DecisionValue.Text(value); }
}
// Handles identify individual engine-owned abilities, never every ability on a card.
public sealed record PriorityAbility(string Id, string SourceCardKey, string SourceName, string Text, bool IgnoredUntilNextTurn)
{
    private string id = DecisionValue.Text(Id), sourceCardKey = DecisionValue.Text(SourceCardKey),
        sourceName = DecisionValue.Text(SourceName), text = DecisionValue.Text(Text);
    private ImmutableArray<string> eligibleHandKeys = [];
    public string Id { get => id; init => id = DecisionValue.Text(value); }
    public string SourceCardKey { get => sourceCardKey; init => sourceCardKey = DecisionValue.Text(value); }
    public string SourceName { get => sourceName; init => sourceName = DecisionValue.Text(value); }
    public string Text { get => text; init => text = DecisionValue.Text(value); }
    // Exact current-prompt handles from the actor's hand, supplied by Forge.
    public ImmutableArray<string> EligibleHandKeys { get => eligibleHandKeys; init => eligibleHandKeys = DecisionValue.Items(value); }
    public int HandPlacementLimit { get; init; } = 1;
}
// Planned effect facts copied by the engine before target selection. Amount is
// the base effect value, never a prediction after prevention/replacement.
public sealed record PendingEffect
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public int? Amount { get; init; }
    public string AmountBasis { get; init; } = "base";
    public string Distribution { get; init; } = "single";
    public CounterAddition? CounterChange { get; init; }
}
public sealed record CounterAddition
{
    public Card? Card { get; init; }
    public int? PlayerId { get; init; }
    public int? ActorPlayerId { get; init; }
    public ImmutableArray<CounterIncrement> Counters { get; init; } = [];
}
public sealed record CounterIncrement(string Type, string Label, int Added);
public sealed record Defender(string Kind, string Id, int PlayerId, string Name);
public sealed record AttackOption(string CardId, ImmutableArray<Defender> Defenders);
public sealed record Attack(string CardId, int DefendingPlayerId, ImmutableArray<string> BlockerIds, ImmutableArray<string> EligibleBlockerIds)
{
    public Defender? Defender { get; init; }
    public bool Blocked { get; init; }
}
public sealed record AttackAssignment(string CardId, Defender Defender);
public sealed record Combat(ImmutableArray<Attack> Attackers, ImmutableArray<AttackOption> AttackOptions, string? BlockProblem)
{
    public bool AttackValid { get; init; } = true;
    // One legal witness from the rules constraint solver, not a tactical plan.
    // It is supplied only when the current declaration fails joint constraints.
    public ImmutableArray<AttackAssignment> AttackRepair { get; init; } = [];
}
public enum StackActionKind { Spell, ActivatedAbility, TriggeredAbility, Ability }
[method: System.Text.Json.Serialization.JsonConstructor]
public sealed record StackItem(string Name, string Text, StackActionKind Kind, Card? Card)
{
    // Compatibility for authored projections that do not distinguish ability kinds.
    public StackItem(string Name, string Text, bool Ability, Card? Card)
        : this(Name, Text, Ability ? StackActionKind.Ability : StackActionKind.Spell, Card) { }
    public bool Ability => Kind != StackActionKind.Spell;
    public string Id { get; init; } = "";
    public int? ControllerId { get; init; }
    public ImmutableArray<StackTarget> Targets { get; init; } = [];
    public PendingEffect? Effect { get; init; }
}
public sealed record StackTarget(string Kind, string Id, string Name, int? PlayerId = null);
public sealed record GameSnapshot(string Id, string Status, string? Error, string? Result, int ViewerId,
    int Turn, string Phase, string PhaseKey, int? ActivePlayerId, ImmutableArray<Player> Players,
    ImmutableArray<StackItem> Stack, Combat? Combat, Decision? Decision)
{
    public ImmutableArray<ActionSourceOccurrence> ActionSources { get; init; } = [];
    public ImmutableArray<PublicState> States { get; init; } = [];
    public long Revision { get; init; }
    public long BoardRevision { get; init; }
    public int? PriorityPlayerId { get; init; }
    // Public acting seat only. Another seat's private prompt/options are never copied here.
    public int? DecisionOwnerId { get; init; }
    public string ActionClass { get; init; } = "";
    public ImmutableArray<ActivityEntry> Activity { get; init; } = [];
    public ImmutableArray<string> Notices { get; init; } = [];
    public ImmutableArray<PriorityAbility> IgnoredPriorityAbilities { get; init; } = [];
    // Only this viewer's granted plays from already disclosed external zones.
    // Access may persist while no current decision permits starting the play.
    public ImmutableArray<PlayAccess> PlayAccesses { get; init; } = [];
    public DecisionTempoFrame? Tempo { get; init; }
    public Player? Viewer => Players.FirstOrDefault(p => p.Id == ViewerId);
}
public sealed record ActivityEntry(long Id, int Turn, string PhaseKey, string Kind, int? PlayerId, string Message, string? CardId, string? CardName)
{
    // Older engines omit this field and remain conservatively interrupting.
    public bool InterruptsQuietYield { get; init; } = true;
    public string Detail { get; init; } = "";
    public string Outcome { get; init; } = "";
    public int Count { get; init; }
    public PublicCardChange? Change { get; init; }
    public PublicEffectCause? Cause { get; init; }
    public PublicImpact? Impact { get; init; }
}
// Event-time results. Damage is the reported amount dealt, not a pending estimate
// or a life-total difference; life is a separate signed old-to-new transition.
public sealed record PublicImpact(string Kind, long Amount, string? CardId = null, string? CardName = null,
    int? PlayerId = null, string? SourceCardId = null, string? SourceName = null,
    int? Before = null, int? After = null, string? DamageType = null, bool? Combat = null, bool? Infect = null);
public sealed record PublicCardChange(string Kind, string Property, int Before, int After);
public sealed record PublicEffectCause(string? CardId, string Name, string Ability, int? ControllerId, string Kind = "effect", string Id = "");
