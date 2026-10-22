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
            throw new Exception("Mutable or foreign state type escaped the match boundary: " + type);
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
