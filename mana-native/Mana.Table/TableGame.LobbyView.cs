using Mana.Contracts;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableGame
{
    private static readonly Color ReadyColor = new(118, 214, 181), LobbyTrim = new(51, 67, 82);
    private void LobbyPill(string label, int x, int y, int width, Color color)
    {
        canvas.Rounded(new(x, y, width, 28), color * .13f, 14);
        canvas.CenterText(label, new(x + 6, y, width - 12, 28), color, .58f, true);
    }
    private void DrawLobby()
    {
        if (deckEditor && selectedDeckDetails != null) { DrawDeckEditor(); return; }
        canvas.Glow(new(310, 450), new(840, 810), Teal * .25f);
        canvas.Text("Gather your table.", 48, 106, Ink, 1.9f, 970, 1, true);
        canvas.Text(CommanderDeck ? "COMMANDER   /   FOUR PLAYERS   /   40 LIFE" : selectedDeckDetails!.Format.ToUpperInvariant() + "   /   TWO PLAYERS", 51, 165, Muted, .73f, 970, 1);
        Button("solo-tab", "Play with AI", 1122, 112, 200, () => SetupMode(true), !busy && !Connected, soloTab && !Connected, 44);
        Button("friends-tab", "Play with friends", 1334, 112, 220, () => SetupMode(false), !busy && !Connected, !soloTab || Connected, 44);
        canvas.Line(new(48, 205), new(1552, 205), LobbyTrim);
        DrawLobbyDeck();
        canvas.Panel(new(556, 232, 1000, 664), new(20, 29, 39), LobbyTrim, 16);
        if (Connected) DrawFriendsLobby();
        else if (soloTab) DrawSoloLobby();
        else DrawFriendsWelcome();
        if (!Connected) Button("experience-home", "Home", 1115, 920, 160, OpenWelcome, loaded && !busy, height: 38);
        DrawUpdateFooter(false);
        if (setupModal.Length > 0) DrawSetupModal();
        DrawWorkshopDialog();
    }
    private void DrawLobbyDeck()
    {
        canvas.Panel(new(40, 232, 490, 664), new(23, 33, 44), DeckConfirmed ? ReadyColor * .6f : LobbyTrim, 16);
        canvas.Text("YOUR DECK", 64, 256, Gold, .75f, 245, 1, true);
        Button("new-deck", "+ New deck", 352, 249, 152, () => NewDeck(), loaded && !busy, height: 32);
        if (selectedDeckDetails is not { } deck) {
            canvas.Ring(new(285, 461), 77, Teal, 2);
            canvas.Rounded(new(251, 414, 66, 94), new(35, 55, 68), 8);
            canvas.CenterText("+", new(251, 413, 66, 94), Blue, 2);
            canvas.Text("Make it your table.", 81, 585, Ink, 1.15f, 406, 1, true);
            canvas.Text("Try a precon, import a list, or build your own.", 81, 707, Muted, .86f, 405, 2);
            Button("change-deck", "Choose a deck", 64, 780, 440, OpenDeckPicker, loaded && !busy, true, 54);
            return;
        }
        canvas.Text(deck.Name, 64, 298, Ink, 1.2f, 440, 2, true);
        var leaders = deck.Entries.Where(e => e.Section == "Commander").ToArray();
        if (leaders.Length == 0) {
            canvas.Panel(new(87, 388, 186, 260), new(19, 27, 36), LobbyTrim, 8);
            canvas.Text("Choose your\ncommander", 108, 478, Muted, .9f, 149, 3);
        } else for (int i = 0; i < Math.Min(2, leaders.Length); i++) {
            var pose = leaders.Length == 1 ? new Pose(new(181, 503), new(186, 260))
                : new Pose(new(153 + i * 77, 500 + i * 12), new(142, 199), i == 0 ? -.065f : .065f);
            PaintCard(leaders[i].Card, pose, false, false);
        }
        int main = deck.Entries.Where(e => e.Section == "Main").Sum(e => e.Quantity), commanders = leaders.Sum(e => e.Quantity);
        int side = deck.Entries.Where(e => e.Section == "Sideboard").Sum(e => e.Quantity);
        canvas.Text((main + commanders).ToString(), 319, 394, Ink, 2.5f, 174, 1, true);
        canvas.Text(CommanderDeck ? "OF 100 CARDS" : "CARDS IN MAIN", 322, 461, Muted, .58f, 170, 1);
        canvas.Text($"{main} main\n{commanders} commander{(commanders == 1 ? "" : "s")}\n{side} sideboard", 320, 496, Muted, .73f, 177, 4);
        LobbyPill(DeckConfirmed ? "CONFIRMED" : DeckReady ? "READY TO REVIEW" : "NEEDS CHANGES", 305, 605, 194, DeckConfirmed ? ReadyColor : DeckReady ? Blue : Gold);
        canvas.Text(leaders.Length == 0 ? "No commander selected" : string.Join(" + ", leaders.Select(e => e.Card.Name)), 66, 665, leaders.Length == 0 ? Gold : Ink, .78f, 438, 2);
        Button("change-deck", "Change deck", 64, 739, 213, OpenDeckPicker, !busy, height: 40);
        Button("edit-deck", "Deck builder", 289, 739, 215, OpenDeckEditor, !busy, height: 40);
        Button("review-deck", DeckConfirmed ? "Review confirmed deck" : "Review & confirm", 64, 795, 440, ReviewDeck, !busy, !DeckConfirmed, 50);
        string deckHint = deck.SaveError.Length > 0 ? "Save failed. Open Deck builder to retry."
            : !DeckReady ? "Finish this list in Deck builder, then review it."
            : !DeckConfirmed ? "Review your list, then select Confirm deck."
            : Connected ? "Editing or changing decks clears Ready."
            : "Editing or changing decks means confirming again.";
        canvas.Text(deckHint, 67, 856, deck.SaveError.Length > 0 ? Red : Muted, .68f, 433, 2);
    }
    private void DrawLobbySeat(int index, string name, string deckName, string detail, string status, bool ready, bool local, bool open = false)
    {
        int x = 586 + index % 2 * 480, y = 346 + index / 2 * 210;
        var color = ready ? ReadyColor : open ? Muted : local ? Blue : Gold;
        canvas.Panel(new(x, y, 460, 190), open ? new(21, 29, 38) : new(28, 40, 53), ready ? color * .7f : LobbyTrim, 12);
        canvas.Circle(new(x + 38, y + 36), 18, color * .18f);
        canvas.CenterText((index + 1).ToString(), new(x + 20, y + 18, 36, 36), color, .8f, true);
        canvas.Text(name, x + 68, y + 21, Ink, .9f, 230, 1, true);
        LobbyPill(status, x + 310, y + 22, 132, color);
        canvas.Text(deckName, x + 22, y + 69, open ? Muted : Ink, .94f, 416, 2, !open);
        if (Connected || !soloTab || local || open) canvas.Text(detail, x + 23, y + 138, Muted, .64f, 411, 2);
    }
    private void DrawSoloLobby()
    {
        canvas.Text("A table on your terms", 586, 257, Ink, 1.13f, 650, 1, true);
        LobbyPill("SOLO / AI", 1358, 254, 168, Blue);
        int opponentCount = Math.Min(3, (soloSetup?.MaxPlayers ?? (CommanderDeck ? 6 : 2)) - 1);
        canvas.Text(opponentCount == 1 ? "You make every decision for your deck. One AI opponent takes the other seat." : "You make every decision for your deck. Three AI opponents take the other seats.", 587, 300, Muted, .73f, 920, 1);
        DrawLobbySeat(0, "You", selectedDeck?.Name ?? "Choose your deck", DeckConfirmed ? "Deck confirmed. Your seat is reserved." : "Review and confirm your deck to play.", DeckConfirmed ? "CONFIRMED" : "CHOOSING", DeckConfirmed, true);
        for (int i = 0; i < opponentCount; i++) {
            int seat = i;
            var opponent = soloSetup?.Opponents[soloChoices[i]];
            DrawLobbySeat(i + 1, "AI opponent " + (i + 1), opponent?.Name ?? "Waiting for your deck", opponent?.Description ?? "Confirm your deck to choose an opponent.", "AI", opponent != null, false);
            int x = 586 + (i + 1) % 2 * 480, y = 346 + (i + 1) / 2 * 210;
            Button("choose-ai:" + i, "Choose deck", x + 22, y + 143, 171, () => OpenOpponentPicker(seat), !busy && opponent != null, height: 30);
            Button("ai-prev:" + i, "<", x + 343, y + 143, 43, () => soloChoices[seat] = (soloChoices[seat] + soloSetup!.Opponents.Length - 1) % soloSetup.Opponents.Length, !busy && opponent != null, height: 30);
            Button("ai-next:" + i, ">", x + 395, y + 143, 43, () => soloChoices[seat] = (soloChoices[seat] + 1) % soloSetup!.Opponents.Length, !busy && opponent != null, height: 30);
        }
        canvas.Text(DeckConfirmed ? "Your table is ready." : "Waiting for your deck.", 590, 800, DeckConfirmed ? ReadyColor : Muted, .85f, 420, 2);
        Button("start-solo", CommanderDeck ? "Start Commander" : "Start game", 1070, 790, 456, StartSolo, !busy && DeckConfirmed && soloSetup?.DeckId == selectedDeck?.Id, true, 54);
        string startHint = selectedDeck == null ? "Choose a deck on the left to get started."
            : !DeckReady ? "Open Deck builder on the left to finish your list."
            : !DeckConfirmed ? "Select Review & confirm under your deck first."
            : "Start now. Changing the AI decks above is optional.";
        canvas.Text(startHint, 1072, 855, Muted, .68f, 452, 2);
    }
    private void DrawFriendsWelcome()
    {
        canvas.Text("Bring your friends", 586, 257, Ink, 1.2f, 620, 1, true);
        Button("connection-options", "Connection options", 1300, 251, 226, () => { setupModal = "connection"; connectionPage = 0; }, !busy, height: 34);
        canvas.Text("One player hosts. Everyone chooses a deck and readies their seat.", 588, 305, Muted, .8f, 917, 2);
        canvas.Panel(new(586, 375, 940, 180), new(28, 40, 53), LobbyTrim, 12);
        canvas.Text("Start a new table", 614, 405, Ink, 1.13f, 510, 1, true);
        canvas.Text("Create four seats and share your invite.", 614, 453, Muted, .83f, 510, 2);
        Button("host", "Host table", 1174, 439, 324, () => ConnectLobby(true), loaded && !busy, true, 52);
        canvas.Panel(new(586, 581, 940, 180), new(24, 35, 47), LobbyTrim, 12);
        canvas.Text("Your friends are waiting", 614, 612, Ink, 1.13f, 510, 1, true);
        canvas.Text("Paste their invite to take a seat.", 614, 660, Muted, .83f, 510, 2);
        Button("join", "Join with invite", 1174, 645, 324, OpenJoin, loaded && !busy, height: 52);
        canvas.Text("Keep this build in sync with your friends. Decks use the same builder and rules as solo play.", 595, 807, Muted, .78f, 903, 3);
    }
    private void DrawFriendsLobby()
    {
        var state = lobby!;
        int joined = state.Seats.Count(s => s.Type != "OPEN"), ready = state.Seats.Count(s => s.Ready);
        canvas.Text(state.Mode == "hosting" ? "Your friends' table" : "You're at the table", 586, 257, Ink, 1.16f, 570, 1, true);
        Button("connection-options", "Connection", 1110, 252, 151, () => { setupModal = "connection"; connectionPage = 0; }, !busy, height: 34);
        if (state.Mode == "hosting") Button("invite", "Invite friends", 1273, 252, 143, () => { setupModal = "connection"; connectionPage = 0; }, !busy, true, 34);
        Button("leave", "Leave", 1428, 252, 98, LeaveLobby, !busy, height: 34);
        canvas.Text($"{joined} / {state.Seats.Length} connected     ·     {ready} ready" + (state.Mode == "hosting" ? "     ·     You are hosting" : ""), 588, 301, Muted, .78f, 915, 1);
        for (int i = 0; i < Math.Min(4, state.Seats.Length); i++) {
            var seat = state.Seats[i]; bool open = seat.Type == "OPEN";
            string detail = open ? "Share an invite to fill this seat." : seat.Ready ? "Ready for the host to start." : seat.Local && !DeckConfirmed ? "Review your selected deck, then confirm." : seat.Deck == null ? "Choosing a deck." : "Deck selected. Not ready yet.";
            DrawLobbySeat(i, seat.Local ? "You" : seat.Name, open ? "Open seat" : seat.Deck ?? "No deck confirmed", detail, open ? "OPEN" : seat.Ready ? "READY" : "NOT READY", seat.Ready, seat.Local, open);
        }
        bool localReady = LocalSeat?.Ready == true;
        Button(localReady ? "unready" : "ready", localReady ? "Not ready" : "Ready up", 586, 790, 250, () => SetLobbyReady(!localReady), !busy && (localReady || DeckConfirmed), !localReady, 54);
        Button("start", state.Mode == "hosting" ? "Start Commander" : "Waiting for host", 1070, 790, 456, StartFriends, !busy && DeckConfirmed && localReady && state.CanStart, true, 54);
        string readyHint = !DeckConfirmed ? "Review & confirm your deck first, then Ready up."
            : localReady ? "You're ready. Editing your deck makes you unready."
            : "Your deck is confirmed. Ready up to join this game.";
        canvas.Text(readyHint, 590, 855, localReady ? ReadyColor : Muted, .67f, 460, 2);
        int missing = Math.Max(0, state.Seats.Length - joined);
        string startHint = state.CanStart ? state.Mode == "hosting" ? "Everyone is ready. Start when you're set." : "Everyone is ready. Your host can start."
            : !string.IsNullOrWhiteSpace(state.StartProblem) ? state.StartProblem
            : missing > 0 ? $"Waiting for {missing} more player{(missing == 1 ? "" : "s")}."
            : state.Mode == "hosting" ? "Waiting for all four seats to ready up." : "Your host starts once all four seats are ready.";
        canvas.Text(startHint, 1072, 855, state.CanStart ? ReadyColor : Muted, .67f, 452, 2);
    }
    private void DrawSetupModal()
    {
        hits.Clear();
        canvas.Fill(new(0, 64, 1600, 936), Color.Black * .82f);
        canvas.Panel(new(168, 142, 1264, 752), new(22, 32, 44), LobbyTrim, 18);
        Button("setup-close", "Back", 1292, 160, 112, CloseSetupModal, !busy, height: 36);
        switch (setupModal) {
            case "decks": DrawDeckPicker(); break;
            case "confirm": DrawDeckConfirmation(); break;
            case "join": DrawJoinLobby(); break;
            case "connection": DrawLobbyConnection(); break;
            case "profile": DrawPlayerName(); break;
        }
    }
    private void DrawDeckConfirmation()
    {
        if (selectedDeckDetails is not { } deck) return;
        canvas.Text(reviewList ? deck.Name : "Bring this deck to the table?", 202, 179, Ink, 1.4f, 1020, 1, true);
        Button("review-overview", "Deck overview", 204, 232, 184, () => { reviewList = false; textFocus = false; }, !busy, !reviewList, 36);
        Button("review-list", "Full deck list", 400, 232, 184, () => { reviewList = true; textFocus = false; }, !busy, reviewList, 36);
        canvas.Text(DeckConfirmed ? "Confirmed. Browsing preserves your ready status." : "Confirm selects this saved list for the game.", 628, 244, DeckConfirmed ? ReadyColor : Muted, .73f, 740, 1);
        if (reviewList) DrawLobbyDeckList();
        else {
            var leaders = deck.Entries.Where(e => e.Section == "Commander").ToArray();
            if (leaders.Length == 0) {
                canvas.Panel(new(230, 309, 280, 392), new(17, 24, 32), Gold * .5f, 10);
                canvas.Text("No commander\nselected", 269, 454, Gold, 1.2f, 203, 3);
            } else for (int i = 0; i < Math.Min(2, leaders.Length); i++)
                PaintCard(leaders[i].Card, leaders.Length == 1 ? new Pose(new(372, 506), new(280, 392)) : new Pose(new(310 + i * 155, 497 + i * 50), new(202, 283), i == 0 ? -.035f : .035f), false, false);
            canvas.Text(deck.Name, 625, 309, Ink, 1.4f, 738, 2, true);
            canvas.Text(string.Join(" + ", leaders.Select(e => e.Card.Name)), 628, 394, Gold, .9f, 738, 2);
            int main = deck.Entries.Where(e => e.Section == "Main").Sum(e => e.Quantity), commander = leaders.Sum(e => e.Quantity), side = deck.Entries.Where(e => e.Section == "Sideboard").Sum(e => e.Quantity);
            canvas.Text(CommanderDeck ? $"{main + commander} / 100 cards    ·    {main} main + {commander} commander    ·    {side} sideboard" : $"{main} main · {side} sideboard · {deck.Format}", 628, 473, Ink, .81f, 738, 2);
            LobbyPill(DeckReady ? "DECK CHECKS PASSED" : "NEEDS ATTENTION", 626, 538, 290, DeckReady ? ReadyColor : Gold);
            string problem = deck.SaveError.Length > 0 ? "Could not save this deck. Open the builder and retry saving.\n" + deck.SaveError : deck.Problem;
            canvas.Text(DeckReady ? "Your saved list passes Commander checks.\nReview again after editing or changing decks." : problem.Length > 0 ? problem : "Choose a commander in the deck builder.", 628, 587, DeckReady ? Muted : Gold, .8f, 739, 7);
            if (DeckReady) DrawDeckComposition(deck, 628, 679, 739);
        }
        canvas.Line(new(204, 808), new(1394, 808), LobbyTrim);
        Button("confirm-change", "Choose another deck", 204, 817, 280, OpenDeckPicker, !busy, height: 48);
        Button("confirm-edit", reviewList && reviewCard != null ? "Edit this card in builder" : "Open deck builder", 638, 817, 280, () => OpenDeckEditorAt(reviewList ? reviewCard : null), !busy, !DeckReady, 48);
        Button("confirm-deck", busy ? "Confirming..." : DeckConfirmed ? "Return to lobby" : "Confirm deck", 932, 817, 462, ConfirmDeck, !busy && DeckReady, DeckReady, 48);
        string confirmHint = !DeckReady ? "Fix the issues above in Deck builder first."
            : DeckConfirmed ? "Browsing this list keeps it confirmed."
            : Connected ? "Confirm chooses your deck. Ready up next in the lobby."
            : "Confirm this list to enable Start Commander.";
        canvas.Text(confirmHint, 936, 873, Muted, .61f, 454, 1);
    }
    private void DrawJoinLobby()
    {
        canvas.Text("Take a seat", 204, 183, Ink, 1.5f, 1000, 1, true);
        canvas.Text("Paste the invite your host shared with you.", 206, 248, Muted, 1, 1140, 2);
        canvas.Text("INVITE OR HOST ADDRESS", 231, 375, Gold, .7f, 1080, 1, true);
        var field = new Rectangle(230, 413, 1140, 66);
        canvas.Panel(field, new(14, 22, 32), textFocus ? Blue : LobbyTrim, 8);
        canvas.Text(joinAddress.Length == 0 ? "MT1-... or host:port" : joinAddress + (textFocus && (int)(now * 2) % 2 == 0 ? "|" : ""), 248, 435, setupSelectAll ? Gold : joinAddress.Length == 0 ? Muted : Ink, .96f, 1100, 1);
        if (!busy) hits.Add(new("join-address", Scope, field, () => textFocus = true));
        canvas.Text("Use the same Mana Table build as your friends. For a shared VPN or local network, the host can copy a matching address from Connection.", 233, 518, Muted, .86f, 1120, 4);
        if (error.Length > 0) canvas.Text(error, 233, 662, Red, .8f, 1120, 4);
        Button("join-confirm", busy ? "Joining..." : "Join table", 958, 810, 412, () => ConnectLobby(false), !busy && joinAddress.Trim().Length > 0, true, 50);
    }
    private void DrawLobbyConnection()
    {
        canvas.Text(Connected ? "Invite & connection" : "Connection options", 204, 179, Ink, 1.4f, 1020, 1, true);
        if (!Connected) {
            canvas.Text("Let the host request port forwarding from their router.", 208, 269, Ink, 1.05f, 1100, 2);
            Button("forward", (forward ? "[x] " : "[ ] ") + "Automatic router port forwarding", 208, 354, 730, () => forward = !forward, !busy, forward, 48);
            canvas.Text("If the router cannot forward ports, use a shared private VPN or a reachable host address. This setting applies when you create a table.", 210, 454, Muted, .92f, 1110, 4);
            return;
        }
        var state = lobby!;
        canvas.Text(state.Mode == "hosting" ? "Share an invite for the network your friends will use." : "You are connected. Your host manages the table and invites.", 208, 245, Muted, .83f, 1110, 2);
        if (state.Mode != "hosting") return;
        canvas.Text("Router mapping: " + state.PortMapping, 209, 301, Ink, .85f, 1100, 1);
        canvas.Text("A router mapping is not a verified internet connection. Local or VPN addresses work only on the matching network.", 209, 337, Muted, .71f, 1140, 2);
        if (!string.IsNullOrWhiteSpace(state.InternetInvite)) {
            Button("copy-invite", "Copy internet invite", 210, 404, 398, () => { Clipboard.SetText(state.InternetInvite!); message = "Internet invite copied."; }, !busy, true, 42);
        } else canvas.Text("No internet invite yet. Choose a local or VPN address below.", 210, 411, Gold, .79f, 1140, 1);
        connectionPage = Math.Clamp(connectionPage, 0, Math.Max(0, (state.Addresses.Length - 1) / 5));
        for (int i = 0; i < Math.Min(5, state.Addresses.Length - connectionPage * 5); i++) {
            var address = state.Addresses[connectionPage * 5 + i]; int y = 474 + i * 61;
            canvas.Rounded(new(208, y, 1186, 51), new(29, 43, 57), 6);
            canvas.Text(address.Label, 222, y + 16, Ink, .74f, 325, 1);
            canvas.Text(address.Url, 561, y + 16, Muted, .74f, 540, 1);
            Button("copy-address:" + address.Url, "Copy", 1246, y + 7, 132, () => { Clipboard.SetText(address.Url); message = address.Label + " address copied."; }, !busy, height: 35);
        }
        Button("connection-prev", "Previous", 208, 818, 140, () => connectionPage--, connectionPage > 0, height: 34);
        Button("connection-next", "Next", 360, 818, 110, () => connectionPage++, (connectionPage + 1) * 5 < state.Addresses.Length, height: 34);
    }
}
