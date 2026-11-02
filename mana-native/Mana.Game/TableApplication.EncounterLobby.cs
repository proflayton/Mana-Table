using Mana.Contracts;
using Microsoft.Xna.Framework;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private void DrawEncounterLobby(Lobby state)
    {
        bool cooperative = IsCooperative(state);
        canvas.Text(cooperative ? "Your party" : "Your table", 90, 149, Ink, 1.8f, 660, 1, true, true);
        if (CanConfigureMatch) MenuAction("match-setup", cooperative ? "Co-op rules" : "Table rules", new(800, 143, 220, 45),
            new ShellIntent.OpenMatchSetup(), !busy);
        bool canInvite = !cooperative || state.Seats.Any(seat => seat.Role == MatchSeatRole.Party && seat.Type == "OPEN");
        MenuAction("invite", canInvite ? "Invite friends" : "Connection", new(1050, 143, 230, 45), new ShellIntent.OpenConnection(), !busy);
        MenuAction("leave", "Leave table", new(1310, 143, 220, 45), new ShellIntent.LeaveLobby(), !busy);
        DrawLobbyTempo(state);

        var ordered = state.Seats.OrderBy(seat => seat.Index).ToArray();
        var near = cooperative ? ordered.Where(seat => seat.Role == MatchSeatRole.Party).ToArray()
            : ordered.Take(3).ToArray();
        var far = cooperative ? ordered.Where(seat => seat.Role == MatchSeatRole.Opposition).ToArray()
            : ordered.Skip(3).ToArray();
        canvas.Text(cooperative ? "YOUR SIDE" : "COMMANDER", 94, 326, Gold, .69f, 650, 1, true);
        if (state.PartyProgress is { } progress)
            canvas.Text($"{progress.CompletedEncounters} encounters played · {progress.Victories} victories", 907, 326, Muted, .65f, 607, 1);
        DrawEncounterSide(near, 397, false, cooperative);
        if (cooperative) canvas.Text("OPPOSITION", 94, 629, Blue, .69f, 650, 1, true);
        DrawEncounterSide(far, 688, true, cooperative);

        bool ready = LocalSeat?.Ready == true;
        string? problem = !DeckReady ? selectedDeckDetails?.Problem is { Length: > 0 } issue ? issue : "Choose a legal saved Commander deck."
            : lobbyDeck.View.Status == Mana.Client.LobbyDeckStatus.Failed ? "Could not share your deck. Choose Ready to try again." : state.StartProblem;
        Button(ready ? "unready" : "ready", ready ? "Not ready" : "Ready", 90, 883, 320,
            new ShellIntent.SetReady(!ready), !busy && !state.MatchActive && (ready || DeckReady), !ready, 52, prominent: true);
        if (cooperative) DrawEncounterStart(state, ready, problem);
        else if (state.Mode == "hosting") Button("start", "Play", 1170, 883, 340,
            new ShellIntent.StartFriends(), !busy && ready && DeckConfirmed && state.CanStart, true, 52, prominent: true);
        else canvas.Text("Waiting for the host", 1170, 898, Muted, .85f, 340, 2);
        if (!cooperative && !string.IsNullOrWhiteSpace(problem))
            canvas.Text(problem, 444, 889, Gold, .69f, 694, 2);
    }

    private void DrawEncounterSide(Seat[] side, int y, bool opposition, bool cooperative)
    {
        int radius = opposition ? 40 : 51;
        for (int index = 0; index < side.Length; index++) {
            var seat = side[index];
            int x = (int)MathF.Round(800 + (index - (side.Length - 1) / 2f) * 347);
            bool open = seat.Type == "OPEN", ai = seat.Type == "AI";
            var color = seat.Ready ? ReadyColor : open ? Muted * .45f : seat.Local ? Gold : Blue;
            canvas.Glow(new(x, y), new(radius * 3, radius * 3), color * .10f);
            canvas.Circle(new(x, y), radius + 3, new(14, 25, 31));
            DrawLobbySeatPortrait(seat, x, y, color, radius);
            canvas.Ring(new(x, y), radius + 4, color, seat.Ready ? 4 : 2);
            if (seat.Ready) {
                canvas.Circle(new(x + radius - 6, y + radius - 10), 14, ReadyColor);
                canvas.CenterText("✓", new(x + radius - 19, y + radius - 25, 26, 30), new(9, 26, 25), .76f, true);
            }
            int nameY = opposition ? y + 54 : y + 67;
            canvas.Text(seat.Local ? "You" : open ? cooperative ? "Open ally seat" : "Open seat" : seat.Name, x - 147, nameY, open ? Muted : Ink,
                .88f, 294, 2, true);
            int deckY = opposition ? y + 94 : y + 108;
            canvas.Text(seat.DeckPreview?.Name ?? seat.Deck ?? (open ? cooperative ? "Invite a friend or add an AI ally" : "Invite a friend or add an AI opponent" : "Choosing a deck"),
                x - 147, deckY, seat.DeckPreview?.Problem is { Length: > 0 } ? Gold : Muted, .69f, 294, opposition ? 2 : 3);
            int controlsY = opposition ? y + 142 : y + 169;
            if (seat.Local) {
                MenuAction("edit-deck", "Edit deck", new(x - 143, controlsY, 132, 32), new ShellIntent.EditDeck(), !busy && selectedDeck != null, size: .66f);
                MenuAction("review-deck", "Deck list", new(x + 11, controlsY, 132, 32), new ShellIntent.ReviewDeck(), !busy && selectedDeckDetails != null, size: .66f);
            } else if (CanManageLobbySeats && (open || ai)) {
                MenuAction("lobby-ai:" + seat.Index, ai ? "Change deck" : cooperative ? "Add AI ally" : "Add AI", new(x - 143, controlsY, 286, 32),
                    new ShellIntent.OpenLobbyAi(seat.Index), !busy && lobby!.AiOpponents.Length > 0, size: .67f);
                if (ai && seat.Role != MatchSeatRole.Opposition)
                    MenuAction("lobby-ai-remove:" + seat.Index, "Open for a friend", new(x - 143, controlsY + 34, 286, 29),
                        new ShellIntent.RemoveLobbyAi(seat.Index), !busy, size: .62f);
            }
        }
    }
}
