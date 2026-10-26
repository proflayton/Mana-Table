using System.Collections.Immutable;
using System.Text.Json;
using Mana.Client;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

internal static class PlayAccessBoundaryChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var card = new Card { Name = "Disclosed spell", VisualId = "exiled-occurrence", Key = "current-source" };
        var route = new PlayAccessRoute("permission-route", PlayAccessKind.Cast, "granting-source", "A granting source", "An engine-owned permission.", true);
        var access = new PlayAccess(1, "Exile", card.VisualId, [route]);
        var priority = Prompt("priority");
        JsonElement Frame(ImmutableArray<PlayAccess> accesses, Card? face = null, JsonElement? prompt = null,
            string status = "playing", string zone = "Exile", bool top = false) => JsonSerializer.SerializeToElement(new {
                id = "access-game", status, viewerId = 0, turn = 4,
                players = new[] {
                    new Player(0, "You", 40, true, false, ImmutableDictionary<string, int>.Empty, []),
                    new Player(1, "Opponent", 40, false, false, ImmutableDictionary<string, int>.Empty,
                        [new(zone, 1, top ? [] : [face ?? card], top ? face ?? card : null)])
                },
                stack = Array.Empty<StackItem>(), prompt = prompt ?? priority, playAccesses = accesses
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        var scope = new ForgeCommandScope();
        var current = scope.ObserveRaw(Frame([access]))!;
        check(current.PlayAccesses.Single().PlayerId == 1
                && current.PlayAccesses.Single().Routes.Single().CanStart
                && scope.CanReadSource(current.Id, current.Decision!.Id, card.Key),
            "A viewer's play access may reference an opponent-owned disclosed exile occurrence without changing its zone owner");
        var client = new ClientSession(new ReadOnlyConnection());
        client.Accept(current);
        check(!client.Submit(new UseOfferedAction(current.Id, current.Decision!.Id, card.Key, route.Id))
                && !client.Submit(new PlayCard(current.Id, current.Decision.Id, card.Key)),
            "A retained play route is presentation metadata and cannot replace a current source-action catalog or create a hand-play command");
        client.Close();

        var retained = new PlayAccess(1, "Exile", card.VisualId, [route with { CanStart = false }]);
        var resolving = scope.ObserveRaw(Frame([retained], prompt: JsonSerializer.SerializeToElement<object?>(null), status: "resolving"))!;
        check(resolving.Decision == null && resolving.PlayAccesses.Single().Routes.Single().Id == route.Id
                && !resolving.PlayAccesses.Single().Routes.Single().CanStart
                && !scope.CanReadSource(current.Id, current.Decision.Id, card.Key),
            "Resolving retains the same granted access while removing its playable glow and the previous prompt's source authority");
        var required = scope.ObserveRaw(Frame([retained], prompt: Prompt("selection")))!;
        check(required.Decision?.Payload is EntitySelectionPayload && required.PlayAccesses.Length == 1,
            "A required choice suspends play initiation without disguising an accessible exile card as part of the hand");

        var library = new PlayAccess(1, "Library", card.VisualId, [route]);
        check(scope.ObserveRaw(Frame([library], zone: "Library", top: true))?.PlayAccesses.Single().Zone == "Library",
            "An explicitly disclosed library top may carry access without projecting any other library cards");
        foreach (var malformed in new[] {
            Frame([new(1, "Exile", "same-name-but-missing-occurrence", [route])]),
            Frame([new(0, "Exile", card.VisualId, [route])]),
            Frame([access], face: card with { FaceDown = true, Name = "Face-down card" }),
            Frame([access, access]),
            Frame([access], prompt: Prompt("selection")),
            Frame([new(1, "Hand", card.VisualId, [route])], zone: "Hand"),
            Frame([new(1, "Command", card.VisualId, [route])], zone: "Command")
        }) {
            scope.ObserveRaw(Frame([access]));
            bool rejected = false;
            try { scope.ObserveRaw(malformed); } catch (IOException) { rejected = true; }
            check(rejected && !scope.CanReadSource(current.Id, current.Decision.Id, card.Key),
                "Malformed, hidden, stale or contradictory access cannot install a face or preserve previous source authority");
        }
    }

    private static JsonElement Prompt(string task) => JsonSerializer.SerializeToElement(new {
        id = "current-decision", kind = "input", task, ok = "Continue", okEnabled = true
    });

    private sealed class ReadOnlyConnection : IMatchConnection
    {
        public Task<GameSnapshot?> ObserveAsync() => Task.FromResult<GameSnapshot?>(null);
        public Task ExecuteAsync(GameCommand command) => throw new InvalidOperationException("Access metadata cannot authorize dispatch.");
        public Task ConcedeAsync(string gameId) => throw new InvalidOperationException();
    }
}
