using Mana.Contracts;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private int lobbyAiSeat = -1, lobbyAiPage;
    private bool CanManageLobbySeats => services.Seats != null && lobby is { Mode: "hosting", MatchActive: false };
    private Seat? ManagedLobbySeat(int index) => CanManageLobbySeats && index > 0
        ? lobby!.Seats.FirstOrDefault(s => s.Index == index && s.Type is "OPEN" or "AI") : null;
    private bool CanResizeLobby(int players) => CanManageLobbySeats && players is >= 2 and <= 4
        && lobby!.Seats.Where(s => s.Index >= players).All(s => s.Type == "OPEN");

    private void DrawLobbySizeControls()
    {
        canvas.Text("Seats", 716, 710, Muted, .64f, 100, 1, true);
        for (int count = 2; count <= 4; count++) {
            int players = count;
            MenuAction("lobby-count:" + count, count.ToString(), new(813 + (count - 2) * 64, 697, 54, 40),
                new ShellIntent.ResizeLobby(players), !busy && CanResizeLobby(count), lobby!.Seats.Length == count, .75f);
        }
    }
    private void ResizeLobby(int players)
    {
        if (busy || !CanResizeLobby(players) || lobby is not { } current || current.Seats.Length == players || services.Seats is not { } service) return;
        string table = current.TableId;
        Work(async () => {
            var state = await service.ConfigureSeatsAsync(table, players);
            updates.Enqueue(() => {
                if (lobby?.TableId != table) return;
                lobby = state; friendPlayerCount = state.Seats.Length;
                message = $"Your table has {state.Seats.Length} seats. Decks stay selected; everyone should Ready up again.";
            });
        }, "Resizing your table", $"Setting up {players} seats for your group. Everyone will ready up again.");
    }

    private void DrawLobbySeatControls(int position, Seat seat)
    {
        int x = 280 + position * 347;
        MenuAction("lobby-ai:" + seat.Index, seat.Type == "AI" ? "Change deck" : "Add AI", new(x - 145, 637, 290, 36),
            new ShellIntent.OpenLobbyAi(seat.Index), !busy && lobby!.AiOpponents.Length > 0, size: .75f);
        if (seat.Type == "AI") MenuAction("lobby-ai-remove:" + seat.Index, "Remove AI", new(x - 145, 680, 290, 36),
            new ShellIntent.RemoveLobbyAi(seat.Index), !busy, size: .65f);
    }
    private void OpenLobbyAiPicker(int seat)
    {
        if (busy || ManagedLobbySeat(seat) == null) return;
        lobbyAiSeat = seat; lobbyAiPage = 0; setupModal = "lobby-ai"; textFocus = false;
    }
    private void SetLobbyAi(string opponentId)
    {
        if (busy || ManagedLobbySeat(lobbyAiSeat) == null || services.Seats is not { } service || lobby is not { } current
            || !current.AiOpponents.Any(o => o.Id == opponentId)) return;
        int seat = lobbyAiSeat; string table = current.TableId;
        string deck = current.AiOpponents.Single(o => o.Id == opponentId).Name;
        Work(async () => {
            var state = await service.SetSeatAsync(table, seat, "AI", opponentId);
            updates.Enqueue(() => {
                if (lobby?.TableId != table) return;
                lobby = state; setupModal = "";
                message = $"AI seat {seat + 1} will play {deck}.";
            });
        }, "Preparing this AI seat", "Loading its deck and letting everyone at the table know.");
    }
    private void RemoveLobbyAi(int seat)
    {
        if (busy || ManagedLobbySeat(seat) is not { Type: "AI" } || services.Seats is not { } service || lobby is not { } current) return;
        string table = current.TableId;
        Work(async () => {
            var state = await service.SetSeatAsync(table, seat, "OPEN");
            updates.Enqueue(() => {
                if (lobby?.TableId != table) return;
                lobby = state; setupModal = ""; message = $"Seat {seat + 1} is open for a friend or another AI opponent.";
            });
        }, "Opening this seat", "Removing the AI opponent from your lobby.");
    }
    private void DrawLobbyAiPicker()
    {
        var seat = ManagedLobbySeat(lobbyAiSeat);
        canvas.Text($"Choose the AI deck for seat {lobbyAiSeat + 1}", 202, 179, Ink, 1.35f, 1020, 1, true);
        if (seat == null || lobby is not { } current) {
            canvas.Text("This seat is no longer available. Go back to see the current table.", 208, 279, Muted, .95f, 1164, 3);
            return;
        }
        canvas.Text("Choose one of the host's saved decks or a precon for this AI. The other seats stay as they are.", 208, 240, Muted, .8f, 1164, 2);
        var choices = current.AiOpponents;
        for (int i = 0; i < Math.Min(6, choices.Length - lobbyAiPage * 6); i++) {
            var choice = choices[lobbyAiPage * 6 + i];
            bool selected = seat.OpponentId == choice.Id;
            int x = 204 + i % 2 * 602, y = 324 + i / 2 * 140;
            canvas.Panel(new(x, y, 586, 125), selected ? new(37, 59, 75) : new(29, 42, 56), selected ? Blue : LobbyTrim, 10);
            canvas.Text(choice.Name, x + 18, y + 15, Ink, .9f, 440, 2, true);
            canvas.Text(choice.Description, x + 18, y + 69, Muted, .66f, 440, 2);
            Button("lobby-ai-deck:" + choice.Id, selected ? "Selected" : "Use deck", x + 465, y + 45, 103,
                new ShellIntent.SelectLobbyAi(choice.Id), !busy, selected, 38);
        }
        if (choices.Length == 0) canvas.Text("No compatible AI decks are available in this build.", 208, 363, Muted, .95f, 1164, 3);
        Button("lobby-ai-prev", "Previous", 208, 777, 150, new ShellIntent.Page(ShellPage.LobbyAi, -1), !busy && lobbyAiPage > 0, height: 36);
        Button("lobby-ai-next", "Next", 370, 777, 126, new ShellIntent.Page(ShellPage.LobbyAi, 1), !busy && (lobbyAiPage + 1) * 6 < choices.Length, height: 36);
        canvas.Text($"{choices.Length} compatible decks | Page {lobbyAiPage + 1} / {Math.Max(1, (choices.Length + 5) / 6)}", 522, 788, Muted, .67f, 850, 1);
        canvas.Text("AI seats are ready automatically. Remove an AI from the lobby to make room for a friend.", 208, 846, Muted, .7f, 1164, 2);
    }
}
