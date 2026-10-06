namespace Mana.Contracts;

public static class EngineProtocol
{
    public const int Version = 2;
    public static readonly string[] RequiredCapabilities = ["seat-sync", "snapshots", "command-receipts", "resume", "table-social"];
}

// The aggregate is for application composition. Consumers can depend on the
// narrower catalog, setup or connection contracts without implementing all three.
public interface ICardEngine : IEngineCatalog, ITableSetup, IMatchConnection, IAsyncDisposable
{
    string Name { get; }
    Task InitializeAsync();
}
public interface IEngineCatalog
{
    Task<IReadOnlyList<DeckSummary>> DecksAsync();
    Task<IReadOnlyList<DeckPreset>> PresetsAsync();
    Task<DeckSummary> ImportAsync(string name, string text);
    Task<DeckSummary> ImportPresetAsync(string id);
    Task<DeckDetails> OpenDeckAsync(string id);
    Task<DeckDetails> SetCommandersAsync(string deckId, long revision, IReadOnlyList<string> cardIds);
    Task<CatalogPage> SearchCardsAsync(CardQuery query);
    Task<DeckDetails> CreateDeckAsync(string name);
    Task<DeckDetails> EditDeckAsync(string deckId, long revision, IReadOnlyList<DeckEdit> edits);
    Task<DeckDetails> RenameDeckAsync(string deckId, long revision, string name);
    Task<DeckDetails> DuplicateDeckAsync(string deckId, long revision, string name);
    Task<DeckDetails> UndoDeckAsync(string deckId, long revision);
    Task<DeckDetails> RedoDeckAsync(string deckId, long revision);
    Task<DeckDetails> SaveDeckAsync(string deckId, long revision);
    Task<string> ExportDeckAsync(string deckId, long revision);
}
public interface ITableSetup
{
    Task<SoloSetup> PrepareSoloAsync(string deckId);
    Task StartSoloAsync(string deckId, IReadOnlyList<string> opponentIds);
    Task<Lobby> HostAsync(int players, bool automaticPortForwarding);
    Task<Lobby> JoinAsync(string address);
    Task<Lobby> LobbyAsync();
    Task SelectDeckAsync(string id);
    Task SetReadyAsync(bool ready);
    Task StartAsync();
    Task ReturnToLobbyAsync();
    Task LeaveAsync();
}
public interface IMatchConnection
{
    Task<GameSnapshot?> ObserveAsync();
    Task ReplyAsync(DecisionReply reply);
    Task ConcedeAsync(string gameId);
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
public sealed record TableConversation(string TableId, long Revision, int LocalSeat, TableMember[] Members, TableMessage[] Messages);
public sealed record DeckSummary(string Id, string Name);
public sealed record DeckEntry(string Section, string Id, Card Card, int Quantity, int ManaValue = 0, int ColorIdentity = 0, string Edition = "");
public sealed record DeckEdit(string Section, string CardId, int Quantity);
public sealed record CatalogCard(string Id, Card Card, int ManaValue, int ColorIdentity, string Section, string Edition);
public sealed record CardQuery(string Text = "", string Type = "", int? ColorIdentity = null, int? MaxManaValue = null, string Sort = "name", int Offset = 0, int Limit = 6);
public sealed record CatalogPage(int Total, int Offset, CatalogCard[] Cards);
public sealed record CommanderOption(string Id, string[] Partners);
public sealed record DeckDetails(string Id, string Name, long Revision, DeckEntry[] Entries,
    CommanderOption[] CommanderOptions, string Problem, string SaveError, bool CanUndo = false, bool CanRedo = false)
{
    public string[] CommanderIds => Entries.Where(e => e.Section == "Commander").Select(e => e.Id).ToArray();
}
public sealed record DeckPreset(string Id, string Name, string Description);
// Opponent IDs are opaque engine-owned handles, just like saved deck IDs.
public sealed record AiOpponent(string Id, string Name, string Description);
public sealed record SoloSetup(string DeckId, string DeckName, AiOpponent[] Opponents);
public sealed record Address(string Label, string Url, string Invite);
public sealed record Seat(int Index, string Name, string Type, string? Deck, bool Ready, bool Local);
public sealed record Lobby(string Mode, string Status, string? Error, bool CanStart, string? StartProblem,
    bool MatchActive, string PortMapping, string? InternetInvite, Address[] Addresses, Seat[] Seats, string TableId = "");
public enum DecisionIntent { General, Priority, Attack, Block, Mana, Selection }
public enum ReplyAction { Confirm, Cancel, SelectCard, SelectPlayer, AssignAttack, AssignBlock, AutoPass, Skip, Choose, Value, Allocate }
public sealed record DecisionReply(string GameId, string DecisionId, ReplyAction Action,
    string? Card = null, int? Player = null, string? Attacker = null, string? Blocker = null,
    string? DefenderCard = null, int[]? Choices = null, string? Value = null, int[]? Amounts = null)
{
    public void Validate()
    {
        bool Present(string? value) => !string.IsNullOrWhiteSpace(value);
        bool valid = Present(GameId) && Present(DecisionId) && Action switch {
            ReplyAction.SelectCard => Present(Card), ReplyAction.SelectPlayer => Player != null,
            ReplyAction.AssignAttack => Present(Attacker) && ((Player != null) != Present(DefenderCard)),
            ReplyAction.AssignBlock => Present(Attacker) && Present(Blocker),
            ReplyAction.Choose => Choices != null && Choices.All(i => i >= 0) && Choices.Distinct().Count() == Choices.Length,
            ReplyAction.Value => Value != null, ReplyAction.Allocate => Amounts != null && Amounts.All(i => i >= 0),
            ReplyAction.Confirm or ReplyAction.Cancel or ReplyAction.AutoPass or ReplyAction.Skip => true,
            _ => false
        };
        if (!valid) throw new ArgumentException("Incomplete or invalid decision reply");
    }
}
public sealed record Card
{
    public string Key { get; init; } = "";
    public string VisualId { get; init; } = "";
    public string CombatId { get; init; } = "";
    public string Name { get; init; } = "";
    public string ArtName { get; init; } = "";
    public string ArtFace { get; init; } = "front";
    public CardFace? OtherFace { get; init; }
    public string[] CombatKeywords { get; init; } = [];
    public int? DefenderId { get; init; }
    public string? Defender { get; init; }
    public string Type { get; init; } = "";
    public string ManaCost { get; init; } = "";
    public string Text { get; init; } = "";
    public int? Power { get; init; }
    public int? Toughness { get; init; }
    public int Damage { get; init; }
    public bool Tapped { get; init; }
    public bool Sick { get; init; }
    public bool FaceDown { get; init; }
    public bool Selectable { get; init; }
    public bool Highlighted { get; init; }
    public bool Attacking { get; init; }
    public bool Blocking { get; init; }
    public Dictionary<string, int> Counters { get; init; } = [];
}
public sealed record CardFace(string Name, string Type, string ManaCost, string OracleText, int? Power, int? Toughness, string ArtName, string ArtFace);
public sealed record Zone(string Name, int Count, Card[] Cards, Card? TopCard);
public sealed record Player(int Id, string Name, int Life, bool Priority, bool Eliminated, Dictionary<string, int> Mana, Zone[] Zones)
{
    // The rules engine's current limit; null means no maximum hand size.
    public int? MaxHandSize { get; init; } = 7;
    public CommanderDamage[] CommanderDamage { get; init; } = [];
    public Zone Zone(string name) => Zones.FirstOrDefault(z => z.Name == name) ?? new(name, 0, [], null);
}
public sealed record CommanderDamage(string Name, int OwnerId, string Owner, int Damage);
public sealed record Choice(int Index, string Label, Card? Card)
{
    // Optional link to an occurrence already visible on the table; never a card name.
    public string CardId { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Mana { get; init; } = "";
}
public sealed record LibraryChoice(int? Index, string Label, Card Card);
public sealed record Decision
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public DecisionIntent Intent { get; init; }
    public string Message { get; init; } = "";
    public string Ok { get; init; } = "Confirm";
    public string Cancel { get; init; } = "Cancel";
    public bool OkEnabled { get; init; }
    public bool CancelEnabled { get; init; }
    public bool CanAutoPass { get; init; }
    public int[] PlayerChoices { get; init; } = [];
    public Choice[] Choices { get; init; } = [];
    public int Min { get; init; }
    public int Max { get; init; }
    public bool Ordered { get; init; }
    public bool AtLeastOne { get; init; }
    public bool MaySkip { get; init; }
    public int Amount { get; init; }
    public int[] Limits { get; init; } = [];
    public string Initial { get; init; } = "";
    public Card? SourceCard { get; init; }
    public string SourceZone { get; init; } = "";
    public string Title { get; init; } = "";
    public string Context { get; init; } = "";
    public bool Numeric { get; init; }
    public LibraryChoice[] LibraryCards { get; init; } = [];
}
public sealed record Defender(string Kind, string Id, int PlayerId, string Name);
public sealed record AttackOption(string CardId, Defender[] Defenders);
public sealed record Attack(string CardId, int DefendingPlayerId, string[] BlockerIds, string[] EligibleBlockerIds)
{
    public Defender? Defender { get; init; }
    public bool Blocked { get; init; }
}
public sealed record Combat(Attack[] Attackers, AttackOption[] AttackOptions, string? BlockProblem);
public sealed record StackItem(string Name, string Text, bool Ability, Card? Card)
{
    public string Id { get; init; } = "";
    public StackTarget[] Targets { get; init; } = [];
}
public sealed record StackTarget(string Kind, string Id, string Name, int? PlayerId = null);
public sealed record GameSnapshot(string Id, string Status, string? Error, string? Result, int ViewerId,
    int Turn, string Phase, string PhaseKey, int? ActivePlayerId, Player[] Players,
    StackItem[] Stack, Combat? Combat, Decision? Decision)
{
    public long Revision { get; init; }
    public long BoardRevision { get; init; }
    public ActivityEntry[] Activity { get; init; } = [];
    public string[] Notices { get; init; } = [];
    public Player? Viewer => Players.FirstOrDefault(p => p.Id == ViewerId);
}
public sealed record ActivityEntry(long Id, int Turn, string PhaseKey, string Kind, int? PlayerId, string Message, string? CardId, string? CardName)
{
    public string Detail { get; init; } = "";
}
