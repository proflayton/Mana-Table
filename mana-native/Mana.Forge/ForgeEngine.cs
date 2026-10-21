using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.Contracts;

namespace Mana.Forge;

public sealed record ForgePaths(string Java, string Jar, string Resources, string Profile);

public sealed partial class ForgeEngine(ForgePaths paths) : ICardEngine, ITableSocial, IDeckWorkshop, ITableSeatSetup, ICardPrintings, ISourceActions
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending = new();
    private readonly SemaphoreSlim write = new(1);
    // Readers retain their own process and ready signal across a failed startup.
    private sealed class EngineProcess(Process process)
    {
        public Process Process { get; } = process;
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? Reader { get; set; }
        public Task? Diagnostics { get; set; }
        public Task? Stopped { get; set; }
        public bool SourceActionsAvailable { get; set; }
        public volatile Exception? Failure;
    }
    private EngineProcess? current;
    private long sequence;
    private bool disposed;
    private string? activeDeckId;
    public string Name => "Forge";
    public async Task InitializeAsync()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (current is { } previous) {
            if (previous.Ready.Task.IsCompletedSuccessfully && previous.Failure == null && !previous.Process.HasExited) return;
            Fail(previous, new IOException("The engine is not running"));
            try { await StopAsync(previous); }
            finally { if (current == previous) current = null; }
        }
        Directory.CreateDirectory(paths.Profile);
        activeDeckId = null;
        var start = new ProcessStartInfo(paths.Java) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(false), StandardOutputEncoding = System.Text.Encoding.UTF8 };
        foreach (var arg in new[] { "-Xmx2g", "-Dfile.encoding=UTF-8", "-Djava.awt.headless=true", "-jar", paths.Jar, paths.Resources, Path.Combine(paths.Profile, "decks") }) start.ArgumentList.Add(arg);
        var owner = new EngineProcess(Process.Start(start) ?? throw new IOException("Could not start Forge"));
        current = owner;
        owner.Diagnostics = Task.Run(async () => {
            await using var log = new StreamWriter(Path.Combine(paths.Profile, "engine.log"), true);
            while (await owner.Process.StandardError.ReadLineAsync() is { } line) { await log.WriteLineAsync(line); await log.FlushAsync(); }
        });
        owner.Reader = Task.Run(async () => {
            try {
                while (await owner.Process.StandardOutput.ReadLineAsync() is { } line) {
                    using var document = JsonDocument.Parse(line);
                    var message = document.RootElement;
                    if (message.TryGetProperty("event", out var ev)) {
                        if (ev.GetString() == "ready") {
                            if (!message.TryGetProperty("protocolVersion", out var version) || version.GetInt32() != EngineProtocol.Version)
                                throw new IOException("The client and rules adapter use different protocol versions. Install the complete matching build.");
                            var capabilities = message.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()).ToHashSet();
                            if (EngineProtocol.RequiredCapabilities.Any(c => !capabilities.Contains(c))) throw new IOException("The rules adapter is missing required synchronization capabilities.");
                            owner.SourceActionsAvailable = capabilities.Contains("source-actions");
                            owner.Ready.TrySetResult();
                        }
                        else if (ev.GetString() == "error") throw new IOException(Text(message, "message"));
                    } else if (message.TryGetProperty("id", out var id) && pending.TryRemove(id.GetInt64(), out var reply)) {
                        if (message.TryGetProperty("error", out var error)) reply.TrySetException(new EngineCommandRejectedException(error.GetString() ?? "The rules engine rejected this request."));
                        else reply.TrySetResult(message.GetProperty("result").Clone());
                    }
                }
                if (!disposed) throw new IOException("The rules engine closed. See engine.log in your profile.");
            } catch (Exception ex) { Fail(owner, ex); }
        });
        try { await owner.Ready.Task.WaitAsync(TimeSpan.FromSeconds(120)); }
        catch (Exception ex) {
            Fail(owner, ex);
            try { await StopAsync(owner); }
            catch (Exception) { /* Preserve the startup failure reported to the player. */ }
            finally { if (current == owner) current = null; }
            throw;
        }
    }
    private void Fail(EngineProcess owner, Exception ex)
    {
        owner.Failure = ex;
        owner.Ready.TrySetException(ex);
        if (current != owner) return;
        foreach (var item in pending) if (pending.TryRemove(item.Key, out var reply)) reply.TrySetException(ex);
    }
    private async Task<JsonElement> Request(string method, object? arguments = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var owner = current ?? throw new IOException("The engine is not running");
        await owner.Ready.Task;
        if (current != owner || owner.Failure != null || owner.Process.HasExited) throw new IOException("The engine is not running");
        long id = Interlocked.Increment(ref sequence);
        var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = reply;
        try {
            await write.WaitAsync();
            try { await owner.Process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, method, @params = arguments ?? new { } })); await owner.Process.StandardInput.FlushAsync(); }
            finally { write.Release(); }
            return await reply.Task.WaitAsync(TimeSpan.FromSeconds(45));
        } finally { pending.TryRemove(id, out _); }
    }
    private static string Text(JsonElement value, string name) => value.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : "";
    private static T Read<T>(JsonElement value) => value.Deserialize<T>(Json) ?? throw new IOException("Missing engine response");
    private DeckSummary Deck(JsonElement data) {
        activeDeckId = Text(data, "id");
        return new(activeDeckId, data.TryGetProperty("deck", out var deck) ? Text(deck, "name") : Text(data, "name"));
    }
    public async Task<IReadOnlyList<DeckSummary>> DecksAsync() => Read<DeckSummary[]>((await Request("list")).GetProperty("decks"));
    public async Task<IReadOnlyList<DeckPreset>> PresetsAsync() => Read<DeckPreset[]>(await Request("deckPresets"));
    public async Task<DeckSummary> ImportAsync(string name, string text) => Deck(await Request("import", new { name, text, format = "Commander" }));
    public async Task<DeckSummary> ImportPresetAsync(string id) => Deck(await Request("presetImport", new { id }));
    private static Card MapCatalogFace(JsonElement card) => new() {
        Name = Text(card, "name"), ArtName = Text(card, "artName"), ArtFace = Text(card, "artFace"),
        Edition = Text(card, "edition"), ArtSet = Text(card, "artSet"), CollectorNumber = Text(card, "collectorNumber"),
        ArtIndex = card.TryGetProperty("artIndex", out var artIndex) ? artIndex.GetInt32() : 0,
        Foil = card.TryGetProperty("foil", out var foil) && foil.GetBoolean(),
        ManaCost = Text(card, "manaCost"), Type = Text(card, "type"), Text = Text(card, "oracleText"),
        Power = int.TryParse(Text(card, "power"), out var power) ? power : null,
        Toughness = int.TryParse(Text(card, "toughness"), out var toughness) ? toughness : null,
        OtherFace = card.TryGetProperty("otherFace", out var face) && face.ValueKind == JsonValueKind.Object ? MapOtherFace(face) : null
    };
    private static CardFace MapOtherFace(JsonElement face) => new(Text(face, "name"), Text(face, "type"),
        Text(face, "manaCost"), Text(face, "oracleText"),
        int.TryParse(Text(face, "power"), out var power) ? power : null,
        int.TryParse(Text(face, "toughness"), out var toughness) ? toughness : null,
        Text(face, "artName"), Text(face, "artFace"));
    private static CatalogCard MapCatalogCard(JsonElement card) => new(Text(card, "id"), MapCatalogFace(card),
        card.GetProperty("manaValue").GetInt32(), card.GetProperty("colorIdentity").GetInt32(), Text(card, "deckSection"), Text(card, "edition"));
    private DeckDetails MapDeck(JsonElement data)
    {
        activeDeckId = Text(data, "id");
        var deck = data.GetProperty("deck");
        var entries = deck.GetProperty("entries").EnumerateArray().Select(entry => {
            var card = MapCatalogCard(entry.GetProperty("card"));
            return new DeckEntry(Text(entry, "section"), card.Id, card.Card, entry.GetProperty("quantity").GetInt32(), card.ManaValue, card.ColorIdentity, card.Edition);
        }).ToArray();
        var validation = data.GetProperty("validation");
        return new(Text(data, "id"), Text(deck, "name"), deck.GetProperty("revision").GetInt64(), entries,
            Read<CommanderOption[]>(data.GetProperty("commanderChoices")), Text(validation, "problem"), Text(data, "saveError"),
            deck.GetProperty("canUndo").GetBoolean(), deck.GetProperty("canRedo").GetBoolean()) {
                Issues = validation.TryGetProperty("issues", out var issues) ? Read<DeckIssue[]>(issues) : [],
                StructureValid = validation.TryGetProperty("structureValid", out var structural) ? structural.GetBoolean() : null,
                Eligibility = Text(validation, "eligibility") is { Length: > 0 } eligibility ? eligibility : "unknown",
                Format = Text(data, "format")
            };
    }
    public async Task<DeckDetails> OpenDeckAsync(string id) => MapDeck(await Request("open", new { id }));
    public async Task<DeckDetails> SetCommandersAsync(string deckId, long revision, IReadOnlyList<string> cardIds) =>
        MapDeck(await Request("setCommanders", new { deckId, revision, cardIds }));
    public async Task<CatalogPage> SearchCardsAsync(CardQuery query)
    {
        var data = await Request("search", new { text = query.Text, type = query.Type, colorIdentity = query.ColorIdentity,
            maxManaValue = query.MaxManaValue, sort = query.Sort, offset = query.Offset, limit = query.Limit, unique = true });
        return new(data.GetProperty("total").GetInt32(), data.GetProperty("offset").GetInt32(),
            data.GetProperty("cards").EnumerateArray().Select(MapCatalogCard).ToArray());
    }
    public async Task<DeckDetails> CreateDeckAsync(string name) => MapDeck(await Request("new", new { name, format = "Commander" }));
    public async Task<CatalogPage> CardPrintingsAsync(string cardId, int offset = 0, int limit = 6, string text = "", string finish = "")
    {
        var data = await Request("cardPrintings", new { cardId, offset, limit, text, finish });
        return new(data.GetProperty("total").GetInt32(), data.GetProperty("offset").GetInt32(),
            data.GetProperty("cards").EnumerateArray().Select(MapCatalogCard).ToArray());
    }
    public async Task<DeckDetails> ReplacePrintingAsync(string deckId, long revision, string section, string cardId, string printingId) =>
        MapDeck(await Request("replacePrinting", new { deckId, revision, section, cardId, printingId }));
    public async Task<DeckDetails> EditDeckAsync(string deckId, long revision, IReadOnlyList<DeckEdit> edits) =>
        MapDeck(await Request("edit", new { deckId, revision, edits = edits.Select(e => new { section = e.Section, cardId = e.CardId, quantity = e.Quantity }) }));
    public async Task<DeckDetails> RenameDeckAsync(string deckId, long revision, string name) => MapDeck(await Request("rename", new { deckId, revision, name }));
    public async Task<DeckDetails> DuplicateDeckAsync(string deckId, long revision, string name) => MapDeck(await Request("duplicate", new { deckId, revision, name }));
    public async Task<DeckDetails> UndoDeckAsync(string deckId, long revision) => MapDeck(await Request("undo", new { deckId, revision }));
    public async Task<DeckDetails> RedoDeckAsync(string deckId, long revision) => MapDeck(await Request("redo", new { deckId, revision }));
    public async Task<DeckDetails> SaveDeckAsync(string deckId, long revision) => MapDeck(await Request("save", new { deckId, revision }));
    public async Task<string> ExportDeckAsync(string deckId, long revision) => (await Request("export", new { deckId, revision, kind = "text" })).GetString() ?? "";
    private async Task<JsonElement> PrepareSolo(string deckId)
    {
        // Opening a deck replaces its editor and undo history. Preparing a test
        // for the current document must retain its existing revision and edits.
        if (activeDeckId != deckId) MapDeck(await Request("open", new { id = deckId }));
        var setup = await Request("matchSetup");
        var problem = Text(setup.GetProperty("setup"), "problem");
        if (problem.Length > 0) throw new InvalidOperationException(problem);
        return setup;
    }
    public async Task<SoloSetup> PrepareSoloAsync(string deckId)
    {
        var setup = await PrepareSolo(deckId);
        return new(deckId, Text(setup, "name"), Read<AiOpponent[]>(setup.GetProperty("opponents"))) {
            MaxPlayers = setup.GetProperty("maxPlayers").GetInt32()
        };
    }
    public async Task StartSoloAsync(string deckId, IReadOnlyList<string> opponentIds)
    {
        if (opponentIds.Count is < 1 or > 5) throw new ArgumentException("Choose between one and five AI opponents.", nameof(opponentIds));
        var setup = await PrepareSolo(deckId);
        if (opponentIds.Count >= setup.GetProperty("maxPlayers").GetInt32()) throw new ArgumentException("This format supports fewer players.", nameof(opponentIds));
        await Request("matchStart", new { deckId, revision = setup.GetProperty("revision").GetInt64(), opponents = opponentIds });
    }
    private static Lobby MapLobby(JsonElement data) => new(Text(data, "mode"), Text(data, "status"), Text(data, "error"),
        data.GetProperty("canStart").GetBoolean(), Text(data, "startProblem"), data.GetProperty("matchActive").GetBoolean(),
        Text(data, "portMapping"), Text(data, "internetInvite"), Read<Address[]>(data.GetProperty("addresses")), Read<Seat[]>(data.GetProperty("slots")), Text(data, "tableId")) {
            AiOpponents = data.TryGetProperty("aiOpponents", out var opponents) ? Read<AiOpponent[]>(opponents) : []
        };
    public async Task<TableConversation> ConversationAsync(string tableId) => Read<TableConversation>(await Request("tableSocial", new { tableId }));
    public async Task<TableConversation> SendMessageAsync(string tableId, string kind, string text) => Read<TableConversation>(await Request("tableChat", new { tableId, kind, text }));
    public async Task<TableConversation> SetPlayerNameAsync(string tableId, string name) => Read<TableConversation>(await Request("tableName", new { tableId, name }));
    public async Task<Lobby> HostAsync(int players, bool automaticPortForwarding) => MapLobby(await Request("multiplayerHost", new { format = "Commander", playerCount = players, autoPortForward = automaticPortForwarding }));
    public async Task<Lobby> JoinAsync(string address) => MapLobby(await Request("multiplayerJoin", new { address }));
    public async Task<Lobby> LobbyAsync() => MapLobby(await Request("multiplayerState"));
    public async Task SelectDeckAsync(string id) => await Request("multiplayerSelectDeck", new { deckId = id });
    public async Task SetReadyAsync(bool ready) => await Request("multiplayerReady", new { ready });
    public async Task<Lobby> ConfigureSeatsAsync(string tableId, int players) =>
        MapLobby(await Request("multiplayerConfigure", new { tableId, format = "Commander", playerCount = players }));
    public async Task<Lobby> SetSeatAsync(string tableId, int seat, string type, string? opponentId = null) =>
        MapLobby(await Request("multiplayerSetSeat", new { tableId, seat, type, opponentId }));
    public async Task StartAsync() => await Request("multiplayerStart");
    public async Task ReturnToLobbyAsync() => await Request("multiplayerReturn");
    public async Task LeaveAsync() => await Request("multiplayerClose");
    public async Task ConcedeAsync(string gameId) => await Request("matchConcede", new { sessionId = gameId });
    public async Task<GameSnapshot?> ObserveAsync()
    {
        var raw = await Request("matchState");
        if (raw.ValueKind == JsonValueKind.Null) return null;
        var node = JsonNode.Parse(raw.GetRawText())!.AsObject();
        // Local games first publish a starting frame before the board exists.
        node["players"] ??= new JsonArray();
        node["stack"] ??= new JsonArray();
        foreach (var entry in node["stack"]!.AsArray()) if (entry is JsonObject item) item["id"] = item["id"]?.ToString() ?? "";
        node["phase"] ??= "Starting game";
        node["phaseKey"] ??= "PREGAME";
        var prompt = node["prompt"]?.DeepClone();
        if (prompt is not null) {
            var input = prompt["inputType"]?.GetValue<string>() ?? "";
            var intent = input switch { "InputPassPriority" => DecisionIntent.Priority, "InputAttack" => DecisionIntent.Attack,
                "InputBlock" => DecisionIntent.Block, _ when input.StartsWith("InputPayMana") => DecisionIntent.Mana,
                _ when input.Contains("Select") => DecisionIntent.Selection, _ => DecisionIntent.General };
            prompt["intent"] = (int)intent;
        }
        node["decision"] = prompt;
        // Defender IDs are numeric for players and strings for cards in Forge.
        if (node["combat"]?["attackOptions"] is JsonArray options)
            foreach (var option in options) if (option?["defenders"] is JsonArray defenders)
                foreach (var defender in defenders) if (defender is JsonObject d) d["id"] = d["id"]?.ToString() ?? "";
        if (node["combat"]?["attackers"] is JsonArray attackers)
            foreach (var attacker in attackers) if (attacker?["defender"] is JsonObject defender) defender["id"] = defender["id"]?.ToString() ?? "";
        var snapshot = node.Deserialize<GameSnapshot>(Json);
        sourceActionsGame = snapshot?.Id ?? "";
        return snapshot;
    }
    public async Task ReplyAsync(DecisionReply reply)
    {
        reply.Validate();
        var args = new Dictionary<string, object?> { ["sessionId"] = reply.GameId, ["promptId"] = reply.DecisionId };
        var action = reply.Action switch { ReplyAction.Confirm => "ok", ReplyAction.Cancel => "cancel", ReplyAction.SelectCard => "card",
            ReplyAction.SelectPlayer => "player", ReplyAction.AssignAttack => "attack", ReplyAction.AssignBlock => "block",
            ReplyAction.AutoPass => "passIfNoResponse", ReplyAction.Skip => "skip", ReplyAction.SetAbilityAutoPass => "setAbilityAutoPass",
            ReplyAction.UseHandPlacement => "useHandPlacement", ReplyAction.PassUntilPublicAction => "passUntilPublicAction",
            ReplyAction.UseSourceAction => "useSourceAction", _ => null };
        if (action != null) args["action"] = action;
        if (reply.Card != null) args[reply.Action == ReplyAction.UseHandPlacement ? "handKey" : "key"] = reply.Card;
        if (reply.Player != null) args[reply.Action == ReplyAction.AssignAttack ? "defenderPlayerId" : "playerId"] = reply.Player;
        if (reply.Attacker != null) args["attackerKey"] = reply.Attacker;
        if (reply.Blocker != null) args["blockerKey"] = reply.Blocker;
        if (reply.DefenderCard != null) args["defenderKey"] = reply.DefenderCard;
        if (reply.Choices != null) args["choices"] = reply.Choices;
        if (reply.Value != null) args["value"] = reply.Value;
        if (reply.Amounts != null) args["values"] = reply.Amounts;
        if (reply.AbilityId != null) args[reply.Action == ReplyAction.UseSourceAction ? "actionId" : "abilityId"] = reply.AbilityId;
        if (reply.Ignore != null) args["ignored"] = reply.Ignore;
        if (reply.YieldToken != null) args["yieldToken"] = reply.YieldToken;
        await Request("matchAction", args);
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        if (current is not { } owner) return;
        Fail(owner, new ObjectDisposedException(nameof(ForgeEngine)));
        try { await StopAsync(owner); }
        finally { if (current == owner) current = null; }
    }
    private static Task StopAsync(EngineProcess owner)
    {
        lock (owner) return owner.Stopped ??= StopProcessAsync(owner);
    }
    private static async Task StopProcessAsync(EngineProcess owner)
    {
        var process = owner.Process;
        try {
            try { process.StandardInput.Close(); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
            if (!process.HasExited) {
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (TimeoutException) {
                    try { process.Kill(true); }
                    catch (InvalidOperationException) when (process.HasExited) { }
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                }
            }
            if (owner.Reader != null) await owner.Reader;
            if (owner.Diagnostics != null) await owner.Diagnostics;
        } finally { process.Dispose(); }
    }
}
