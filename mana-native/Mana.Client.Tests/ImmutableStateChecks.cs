using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Mana.Client;
using Mana.Contracts;

internal static class ImmutableStateChecks
{
    public static void Run(Action<bool, string> check)
    {
        var visited = new HashSet<Type>();
        AssertImmutable(typeof(GameSnapshot), visited);
        AssertImmutable(typeof(SourceActionSet), visited);
        check(visited.Contains(typeof(Decision)) && visited.Contains(typeof(PendingEffect))
            && visited.Contains(typeof(Card)) && visited.Contains(typeof(ActivityEntry)),
            "Published match state has no mutable collection or writable property anywhere in its object graph");

        Type[] services = [typeof(IEngineCatalog), typeof(ICardPrintings), typeof(ITableSetup), typeof(ITableSeatSetup),
            typeof(ITableSocial), typeof(IDeckLibrary), typeof(IDeckWorkshop), typeof(IDeckAnalysis), typeof(IDeckVersionHistory)];
        foreach (var service in services)
            foreach (var method in service.GetMethods()) {
                var result = method.ReturnType;
                if (result.IsGenericType && result.GetGenericTypeDefinition() == typeof(Task<>)) result = result.GenericTypeArguments[0];
                if (result != typeof(Task)) AssertImmutable(result, visited);
                foreach (var parameter in method.GetParameters()) AssertImmutable(parameter.ParameterType, visited);
            }
        check(visited.Contains(typeof(WorkshopState)) && visited.Contains(typeof(AnalysisReport))
            && visited.Contains(typeof(Lobby)) && visited.Contains(typeof(WorkshopMetadataEdit)),
            "Deck, workshop, analysis and table service requests and results have deeply immutable graphs");

        var originalCounters = new Dictionary<string, int> { ["+1/+1"] = 2 };
        var card = new Card { VisualId = "visible", Key = "current-key", Name = "Creature", Power = 4, Toughness = 4,
            Counters = originalCounters.ToImmutableDictionary(), States = [new("flying", "Flying", "ability", null, null, "", "flying")] };
        Card[] originalCards = [card];
        Choice[] originalChoices = [new(0, "Creature", card)];
        var state = new GameSnapshot("immutable", "playing", null, null, 0, 1, "Main phase", "MAIN1", 0,
            [new(0, "You", 40, true, false, ImmutableDictionary<string, int>.Empty, [new("Battlefield", 1, originalCards.ToImmutableArray(), null)])],
            [], null, new() { Id = "choice", Kind = "choice", Choices = originalChoices.ToImmutableArray(), Min = 1, Max = 1 });
        var client = new ClientSession(new ReadOnlyConnection(state));
        client.Accept(state);

        originalCounters["+1/+1"] = 100;
        originalCards[0] = card with { Name = "Replaced outside the session" };
        originalChoices[0] = new(1, "Replaced outside the session", null);
        check(client.State!.Viewer!.Zone("Battlefield").Cards[0].Name == "Creature"
            && client.State.Decision!.Choices[0].Index == 0 && client.State.Decision.Choices[0].Card!.Counters["+1/+1"] == 2,
            "Changing provider construction buffers cannot change an accepted frame or its private choices");

        bool listRejected = false, dictionaryRejected = false;
        try { ((IList<Card>)state.Viewer!.Zone("Battlefield").Cards)[0] = new(); }
        catch (NotSupportedException) { listRejected = true; }
        try { ((IDictionary<string, int>)card.Counters)["+1/+1"] = 100; }
        catch (NotSupportedException) { dictionaryRejected = true; }
        check(listRejected && dictionaryRejected && client.State.Viewer!.Zone("Battlefield").Cards[0] == card,
            "Consumers cannot mutate an accepted frame by casting its collections to writable interfaces");

        var nextCard = card with { Counters = card.Counters.SetItem("+1/+1", 3), Power = 5, Toughness = 5 };
        var next = state with { Revision = 1, Players = state.Players.SetItem(0, state.Viewer! with {
            Zones = state.Viewer!.Zones.SetItem(0, new("Battlefield", 1, [nextCard], null)) }) };
        client.Accept(next);
        check(state.Viewer!.Zone("Battlefield").Cards[0].Counters["+1/+1"] == 2
            && client.State!.Viewer!.Zone("Battlefield").Cards[0].Counters["+1/+1"] == 3,
            "A new revision can share immutable state without rewriting the preceding frame used for transitions");

        var roundTrip = JsonSerializer.Deserialize<GameSnapshot>(JsonSerializer.Serialize(state))!;
        check(roundTrip.Viewer!.Zone("Battlefield").Cards[0].Counters["+1/+1"] == 2
            && roundTrip.Decision!.Choices[0].Card!.States!.Value[0].Key == "flying",
            "Immutable nested cards, counters, state descriptors and decision choices survive JSON projection");
        client.Close();

        CheckWorkspace(check, card);
    }

    private static void CheckWorkspace(Action<bool, string> check, Card card)
    {
        string[] roleBuffer = ["ramp"];
        int[] curveBuffer = [0, 3];
        var countBuffer = new Dictionary<string, int> { ["ramp"] = 1 };
        DeckEntry[] entryBuffer = [new("Main", "card", card, 1)];
        var deck = new DeckDetails("deck", "Original", 4, entryBuffer.ToImmutableArray(), [], "", "");
        var workshop = new WorkshopState(deck.Id, deck.Revision, 2, "version", "plan", "question",
            [new("card", roleBuffer.ToImmutableArray(), false, "local")], [], [], null,
            new(countBuffer.ToImmutableDictionary(), curveBuffer.ToImmutableArray(), ImmutableDictionary<string, int>.Empty, ""));
        var edit = new WorkshopMetadataEdit(Roles: workshop.Roles);
        var workspace = new DeckWorkspaceSession(null, null, action => action());
        workspace.Observe(new(deck.Id, deck.Revision));
        check(workspace.AcceptWorkshop(workspace.Capture(), workshop), "A scoped immutable workshop result is accepted");
        var retained = workspace.View;

        roleBuffer[0] = "removed"; curveBuffer[1] = 100; countBuffer["ramp"] = 100;
        entryBuffer[0] = entryBuffer[0] with { Quantity = 100 };
        check(retained.Workshop!.Roles[0].Roles[0] == "ramp" && retained.Workshop.Insights.Curve[1] == 3
            && retained.Workshop.Insights.RoleCounts["ramp"] == 1 && deck.Entries[0].Quantity == 1
            && edit.Roles!.Value[0].Roles[0] == "ramp",
            "Provider construction buffers cannot rewrite accepted deck state or a queued metadata edit");

        bool rolesRejected = false, countsRejected = false;
        try { ((IList<string>)retained.Workshop.Roles[0].Roles)[0] = "removed"; }
        catch (NotSupportedException) { rolesRejected = true; }
        try { ((IDictionary<string, int>)retained.Workshop.Insights.RoleCounts)["ramp"] = 100; }
        catch (NotSupportedException) { countsRejected = true; }
        check(rolesRejected && countsRejected, "Consumer casts cannot mutate nested accepted workshop values");

        var next = workshop with { MetadataRevision = 3, Roles = workshop.Roles.SetItem(0,
            workshop.Roles[0] with { Roles = workshop.Roles[0].Roles.Add("draw") }) };
        check(workspace.AcceptWorkshop(workspace.Capture(), next) && retained.Workshop.Roles[0].Roles.Length == 1
            && workspace.View.Workshop!.Roles[0].Roles.Length == 2,
            "Publishing a new workshop revision preserves the preceding view and pending edit");

        var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var roundTrip = JsonSerializer.Deserialize<WorkshopState>(JsonSerializer.Serialize(workshop, json), json)!;
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(workshop, json));
        check(roundTrip.Roles[0].Roles[0] == "ramp" && roundTrip.Insights.RoleCounts["ramp"] == 1
            && wire.RootElement.GetProperty("roles").ValueKind == JsonValueKind.Array
            && wire.RootElement.GetProperty("insights").GetProperty("roleCounts").ValueKind == JsonValueKind.Object,
            "Immutable deck collections keep the existing JSON array and object wire representation");
        var omitted = JsonSerializer.Deserialize<WorkshopMetadataEdit>("{}", json)!;
        var cleared = JsonSerializer.Deserialize<WorkshopMetadataEdit>("{\"roles\":[],\"considering\":[]}", json)!;
        check(omitted.Roles == null && omitted.Considering == null && cleared.Roles is { IsEmpty: true }
            && cleared.Considering is { IsEmpty: true },
            "Metadata patches preserve omitted fields separately from explicit empty collection clears");
        workspace.Close();
    }

    private static void AssertImmutable(Type type, HashSet<Type> visited)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || !visited.Add(type)) return;
        if (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(ImmutableArray<>)
            || type.GetGenericTypeDefinition() == typeof(ImmutableDictionary<,>))) {
            foreach (var element in type.GetGenericArguments()) AssertImmutable(element, visited);
            return;
        }
        if (type.Assembly != typeof(GameSnapshot).Assembly || !type.IsSealed || type.GetFields(BindingFlags.Instance | BindingFlags.Public).Length != 0)
            throw new Exception("Mutable or foreign state type escaped the service boundary: " + type);
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public)) {
            if (property.SetMethod is { } setter && !setter.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit)))
                throw new Exception("Writable published state: " + type.Name + "." + property.Name);
            AssertImmutable(property.PropertyType, visited);
        }
    }

    private sealed class ReadOnlyConnection(GameSnapshot state) : IMatchConnection
    {
        public Task<GameSnapshot?> ObserveAsync() => Task.FromResult<GameSnapshot?>(state);
        public Task ExecuteAsync(GameCommand command) => throw new InvalidOperationException("No command is needed to inspect immutable state.");
        public Task ConcedeAsync(string gameId) => throw new InvalidOperationException("No command is needed to inspect immutable state.");
    }
}
