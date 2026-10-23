using Mana.Contracts;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private static readonly Color ReadyColor = new(118, 214, 181), LobbyTrim = new(51, 67, 82);
    private void LobbyPill(string label, int x, int y, int width, Color color)
    {
        canvas.Rounded(new(x, y, width, 28), color * .13f, 14);
        canvas.CenterText(label, new(x + 6, y, width - 12, 28), color, .58f, true);
    }
    private void DrawLobby()
    {
        DrawHomeSurface();
        DrawMenuHeading();
        if (deckEditor && selectedDeckDetails != null) { DrawDeckEditor(); return; }
        if (Connected) DrawFriendsLobby();
        else {
            int actionTop = DrawHomeDeck();
            MenuAction("solo-tab", "Play with AI", new(84, actionTop, 210, 48), new ShellIntent.ChoosePlayMode(true), loaded && !busy, soloTab);
            MenuAction("friends-tab", "With friends", new(315, actionTop, 210, 48), new ShellIntent.ChoosePlayMode(false), loaded && !busy, !soloTab);
            if (soloTab) DrawSoloLobby(actionTop); else DrawFriendsWelcome(actionTop);
            DrawHomeDecks();
        }
        DrawUpdateFooter(true);
        if (setupModal.Length > 0) DrawSetupModal();
        DrawWorkshopDialog();
    }
    private void DrawHomeSurface()
    {
        canvas.Gradient(new(0, 0, 1600, 1000), new(20, 29, 31), new(7, 13, 17));
        var leader = selectedDeckDetails?.Entries.FirstOrDefault(entry => entry.Section == "Commander")?.Card;
        if (leader != null && resources.Card(leader) is { } image) {
            var crop = new Rectangle((int)(image.Width * .08f), (int)(image.Height * .13f), (int)(image.Width * .84f), (int)(image.Height * .43f));
            canvas.Sprite(image, new(800, 430), new(1600, 1187), tint: Color.White * .25f, crop: crop);
            for (int i = 0; i < 64; i++) {
                float shade = .92f * Math.Clamp((1080 - i * 25) / 850f, 0, 1);
                canvas.Fill(new(i * 25, 0, 25, 1000), new Color(8, 17, 21) * shade);
            }
        }
        canvas.Gradient(new(0, 0, 1600, 1000), new Color(9, 17, 22) * .18f, new Color(5, 11, 16) * .98f);
        canvas.Glow(new(1000, 390), new(1400, 980), Teal * .10f);
    }
    private void DrawMenuHeading()
    {
        canvas.Text("MANA TABLE", 64, 38, Gold, 1.35f, 340, 1, true);
        MenuAction("experience-home", "Play", new(405, 31, 100, 48), new ShellIntent.OpenPlayHome(), loaded && !busy, !deckEditor);
        MenuAction("change-deck", "Decks", new(528, 31, 115, 48), new ShellIntent.OpenDeckLibrary(), loaded && !busy);
        MenuAction("new-deck", "+ New deck", new(666, 31, 160, 48), new DeckIntent.NewDeck(), loaded && !busy);
        MenuAction("player-name", preferences.PlayerName.Length > 0 ? preferences.PlayerName : "Player", new(1130, 31, 210, 48), new ShellIntent.EditPlayerName(), loaded && !busy);
    }
    private int DrawHomeDeck()
    {
        var deck = selectedDeckDetails;
        canvas.Text(deck?.Format.ToUpperInvariant() ?? "COMMANDER", 94, 140, Gold, .74f, 570, 1, true);
        string title = deck?.Name ?? "Your next commander.";
        int titleHeight = canvas.TextSize(title, 1.7f, 670, 3, true).Y;
        canvas.Text(title, 88, 180, Ink, 1.7f, 670, 3, true);
        if (deck == null) {
            canvas.Text("Choose a precon or bring your own deck.", 94, 375, Muted, .95f, 600, 2);
            if (soloTab) Button("home-choose", "Choose a deck", 90, 554, 430, new ShellIntent.OpenDeckLibrary(), loaded && !busy, true, 68, prominent: true);
            if (resources.CardBack is { } back) for (int i = 0; i < 3; i++)
                canvas.Sprite(back, new(1010 + i * 95, 445 + (i == 1 ? -22 : 0)), new(270, 378), (i - 1) * .13f, Color.White * .7f);
            return 465;
        }
        var leaders = deck.Entries.Where(entry => entry.Section == "Commander").Take(2).ToArray();
        string names = string.Join(" + ", leaders.Select(entry => entry.Card.Name));
        int namesY = Math.Max(290, 180 + titleHeight + 24);
        int namesHeight = canvas.TextSize(names, .93f, 630, 3).Y;
        canvas.Text(names, 94, namesY, Muted, .93f, 630, 3);
        int countY = namesY + namesHeight + 24, actionTop = Math.Max(465, countY + 48);
        int count = deck.Entries.Where(entry => entry.Section is "Commander" or "Main").Sum(entry => entry.Quantity);
        string countLabel = CommanderDeck ? $"{count} / 100 cards" : $"{count} cards";
        canvas.Text(countLabel + (DeckReady ? "" : " · Needs changes"), 94, countY, DeckReady ? Muted : Gold, .76f, 620, 1);
        var bounds = new Rectangle(855, 155, 590, 600);
        bool over = !busy && bounds.Contains(pointer);
        for (int i = leaders.Length - 1; i >= 0; i--) {
            var pose = leaders.Length == 1 ? new Pose(new(1135, 439), new(370, 518), -.025f)
                : new Pose(new(1060 + i * 166, 410 + i * 38), new(300, 420), i == 0 ? -.08f : .07f);
            canvas.Quad(pose.Center + new Vector2(10, 16), pose.Size + new Vector2(10), pose.Angle, Color.Black * .5f);
            PaintCard(leaders[i].Card, pose, false, false);
            if (over) canvas.CardFocus(pose, Gold);
        }
        if (leaders.Length == 0) {
            canvas.CardFrame(new Vector2(1135, 439), new(315, 440), -.025f, Gold * .45f, 2);
            canvas.CenterText("Choose a commander", new(972, 401, 326, 80), Gold, 1.05f, true);
        }
        canvas.CenterText("Edit deck", new(950, 718, 370, 40), over ? Ink : Muted, .82f, over);
        if (!busy) hits.Add(ShellHit("edit-deck", bounds, new ShellIntent.EditDeck(true)));
        if (!DeckReady && soloTab) {
            string issue = deck.SaveError.Length > 0 ? "Unsaved changes. Open your deck to retry." : deck.Problem.Length > 0 ? deck.Problem : "Choose a commander in your deck.";
            canvas.Text(issue, 94, actionTop + 170, Gold, .82f, 620, 4);
        }
        MenuAction("review-deck", "Deck list", new(950, 765, 370, 36), new ShellIntent.ReviewDeck(true), !busy, size: .74f);
        return actionTop;
    }
    private void DrawHomeDecks()
    {
        canvas.Text("YOUR DECKS", 94, 810, Gold, .64f, 240, 1, true);
        MenuAction("home-library", "All decks", new(1330, 795, 170, 42), new ShellIntent.OpenDeckLibrary(), loaded && !busy, size: .75f);
        for (int i = 0; i < Math.Min(4, decks.Count - homeDeckPage * 4); i++) {
            var deck = decks[homeDeckPage * 4 + i];
            int x = 94 + i * 347;
            var bounds = new Rectangle(x, 849, 315, 69);
            bool selected = selectedDeck?.Id == deck.Id, over = !busy && bounds.Contains(pointer);
            canvas.Line(new(x, 846), new(x + 314, 846), selected ? Gold : over ? Ink : LobbyTrim, selected ? 3 : 1);
            canvas.Text(deck.Name, x + 6, 864, selected || over ? Ink : Muted, .95f, 302, 2, selected);
            if (!busy) hits.Add(ShellHit("home-deck:" + deck.Id, bounds, new ShellIntent.SelectHomeDeck(deck.Id)));
        }
        if (decks.Count == 0) MenuAction("home-precons", "Choose a precon", new(90, 848, 300, 60), new ShellIntent.OpenDeckLibrary(), loaded && !busy);
        if (decks.Count > 4) {
            MenuAction("home-prev", "‹", new(1502, 844, 37, 42), new ShellIntent.Page(ShellPage.HomeDecks, -1), !busy && homeDeckPage > 0, size: 1.3f);
            MenuAction("home-next", "›", new(1540, 844, 37, 42), new ShellIntent.Page(ShellPage.HomeDecks, 1), !busy && (homeDeckPage + 1) * 4 < decks.Count, size: 1.3f);
        }
    }
    private void DrawSoloLobby(int top)
    {
        if (selectedDeckDetails == null) return;
        Button("start-solo", "Play", 90, top + 89, 430, new ShellIntent.StartSolo(), loaded && !busy && DeckReady, true, 68, prominent: true);
        if (!DeckReady) return;
        int count = CommanderDeck ? 3 : 1;
        for (int i = 0; i < count; i++) {
            int seat = i;
            var opponent = soloSetup is { } setup && setup.DeckId == selectedDeck?.Id ? setup.Opponents.ElementAtOrDefault(soloChoices[i]) : null;
            int x = 164 + i * 139;
            int centerY = top + 232;
            var bounds = new Rectangle(x - 58, centerY - 33, 116, 94);
            bool over = !busy && DeckReady && bounds.Contains(pointer);
            canvas.Circle(new(x, centerY), 29, new(20, 37, 45));
            canvas.Ring(new(x, centerY), 30, over ? Gold : LobbyTrim, over ? 3 : 1);
            canvas.Rounded(new(x - 13, centerY - 9, 26, 18), over ? Ink : Muted, 4);
            canvas.Circle(new(x - 5, centerY - 1), 2, Panel); canvas.Circle(new(x + 5, centerY - 1), 2, Panel);
            canvas.Line(new(x, centerY - 9), new(x, centerY - 15), over ? Ink : Muted, 2);
            canvas.CenterText("AI", new(x - 35, centerY + 36, 70, 26), over ? Ink : Muted, .65f, true);
            if (over && opponent != null) canvas.Text(opponent.Name, 580, centerY - 8, Ink, .74f, 300, 3);
            if (!busy && DeckReady) hits.Add(ShellHit("choose-ai:" + i, bounds, new ShellIntent.OpenOpponentPicker(seat)));
        }
    }
    private void DrawFriendsWelcome(int top)
    {
        canvas.Text("Seats", 94, top + 76, Muted, .75f, 100, 1);
        for (int count = 2; count <= 4; count++) {
            int players = count;
            MenuAction("friends-count:" + count, count.ToString(), new(204 + (count - 2) * 68, top + 58, 53, 42), new ShellIntent.SetFriendCount(players), !busy, friendPlayerCount == count);
        }
        Button("host", "Host a table", 90, top + 119, 430, new ShellIntent.Connect(true), loaded && !busy, true, 68, prominent: true);
        MenuAction("join", "Join with an invite", new(90, top + 208, 430, 48), new ShellIntent.OpenJoin(), loaded && !busy);
        MenuAction("connection-options", "Connection", new(90, top + 266, 430, 38), new ShellIntent.OpenConnection(true), !busy, size: .68f);
    }
    private void DrawFriendsLobby()
    {
        var state = lobby!;
        canvas.Text(state.Mode == "hosting" ? "Your table" : "At the table", 90, 149, Ink, 1.8f, 800, 1, true, true);
        MenuAction("invite", "Invite friends", new(1050, 143, 230, 45), new ShellIntent.OpenConnection(), !busy);
        MenuAction("leave", "Leave table", new(1310, 143, 220, 45), new ShellIntent.LeaveLobby(), !busy);
        for (int i = 0; i < Math.Min(4, state.Seats.Length); i++) {
            var seat = state.Seats[i]; int x = 280 + i * 347, y = 390;
            bool open = seat.Type == "OPEN", ai = seat.Type == "AI";
            Color color = seat.Ready ? ReadyColor : open ? Muted * .45f : seat.Local ? Gold : Blue;
            canvas.Glow(new(x, y), new(250, 250), color * .10f);
            canvas.Circle(new(x, y), 70, new(14, 25, 31));
            if (seat.Local && selectedDeckDetails?.Entries.FirstOrDefault(entry => entry.Section == "Commander")?.Card is { } leader && resources.Card(leader) is { } portrait)
                canvas.Portrait(portrait, new(x, y), 66, Color.White);
            else canvas.CenterText(open ? "+" : ai ? "AI" : seat.Name.Length > 0 ? seat.Name[..1] : "?", new(x - 63, y - 62, 126, 124), color, open ? 2.7f : 1.7f, true);
            canvas.Ring(new(x, y), 71, color, seat.Ready ? 4 : 2);
            if (seat.Ready) { canvas.Circle(new(x + 52, y + 47), 18, ReadyColor); canvas.CenterText("✓", new(x + 34, y + 27, 36, 40), new(9, 26, 25), .95f, true); }
            canvas.CenterText(seat.Local ? "You" : open ? "Open seat" : seat.Name, new(x - 154, 489, 308, 43), open ? Muted : Ink, 1.03f, true);
            canvas.Text(seat.Deck ?? (open ? "Invite a friend" : "Choosing a deck"), x - 148, 546, Muted, .82f, 296, 3);
            if (seat.Local) MenuAction("edit-deck", "Edit deck", new(x - 130, 637, 260, 36), new ShellIntent.EditDeck(), !busy && selectedDeck != null, size: .75f);
            if (CanManageLobbySeats && seat.Index > 0 && (open || ai)) DrawLobbySeatControls(i, seat);
        }
        if (CanManageLobbySeats) DrawLobbySizeControls();
        MenuAction("review-deck", "Deck list", new(92, 714, 250, 40), new ShellIntent.ReviewDeck(), !busy && selectedDeckDetails != null, size: .75f);
        bool ready = LocalSeat?.Ready == true;
        Button(ready ? "unready" : "ready", ready ? "Not ready" : "Ready", 90, 797, 320, new ShellIntent.SetReady(!ready), !busy && (ready || DeckReady), !ready, 60, prominent: true);
        if (state.Mode == "hosting") Button("start", "Play", 1170, 797, 340, new ShellIntent.StartFriends(), !busy && ready && DeckConfirmed && state.CanStart, true, 60, prominent: true);
        else canvas.Text("Waiting for the host", 1170, 815, Muted, .9f, 340, 2);
        string? problem = !DeckReady ? selectedDeckDetails is not { } deck ? "Choose your deck."
            : deck.SaveError.Length > 0 ? "Save your deck before becoming Ready."
            : deck.Problem.Length > 0 ? deck.Problem : "Choose a commander in your deck." : state.StartProblem;
        if (!string.IsNullOrWhiteSpace(problem)) canvas.Text(problem, 464, 808, Gold, .78f, 650, 3);
    }
    private void DrawSetupModal()
    {
        hits.Clear();
        canvas.Fill(new(0, 64, 1600, 936), Color.Black * .82f);
        canvas.Panel(new(168, 142, 1264, 752), new(22, 32, 44), LobbyTrim, 18);
        Button("setup-close", "Back", 1292, 160, 112, new ShellIntent.CloseSetup(), !busy, height: 36);
        switch (setupModal) {
            case "decks": DrawDeckPicker(); break;
            case "confirm": DrawDeckConfirmation(); break;
            case "join": DrawJoinLobby(); break;
            case "connection": DrawLobbyConnection(); break;
            case "profile": DrawPlayerName(); break;
            case "lobby-ai": DrawLobbyAiPicker(); break;
        }
    }
    private void DrawDeckConfirmation()
    {
        if (selectedDeckDetails is not { } deck) return;
        canvas.Text(deck.Name, 202, 179, Ink, 1.4f, 1020, 2, true);
        Button("review-overview", "Deck overview", 204, 232, 184, new ShellIntent.ShowReview(false), !busy, !reviewList, 36);
        Button("review-list", "Full deck list", 400, 232, 184, new ShellIntent.ShowReview(true), !busy, reviewList, 36);
        canvas.Text("Browsing keeps your current deck selected.", 628, 244, Muted, .73f, 740, 1);
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
            canvas.Text(DeckReady ? "Your saved list is ready to play." : problem.Length > 0 ? problem : "Choose a commander in the deck builder.", 628, 587, DeckReady ? Muted : Gold, .8f, 739, 7);
            if (DeckReady) DrawDeckComposition(deck, 628, 679, 739);
        }
        canvas.Line(new(204, 808), new(1394, 808), LobbyTrim);
        Button("confirm-change", "Choose another deck", 204, 817, 280, new ShellIntent.OpenDeckLibrary(), !busy, height: 48);
        Button("confirm-edit", reviewList && reviewCard != null ? "Edit this card in builder" : "Open deck builder", 638, 817, 280, new ShellIntent.EditDeck(AtReviewCard: true), !busy, !DeckReady, 48);
        Button("confirm-deck", "Back to play", 932, 817, 462, new ShellIntent.CloseSetup(), !busy, true, 48);
        string confirmHint = !DeckReady ? "Fix the issues above in Deck builder first."
            : Connected ? "Ready at the table when you want to play."
            : "Play starts directly with your selected deck.";
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
        if (!busy) hits.Add(ShellHit("join-address", field, new ShellIntent.FocusSetupInput(ResetSelection: false)));
        canvas.Text("Use the same Mana Table build as your friends. For a shared VPN or local network, the host can copy a matching address from Connection.", 233, 518, Muted, .86f, 1120, 4);
        if (error.Length > 0) canvas.Text(error, 233, 662, Red, .8f, 1120, 4);
        Button("join-confirm", busy ? "Joining..." : "Join table", 958, 810, 412, new ShellIntent.Connect(false), !busy && joinAddress.Trim().Length > 0, true, 50);
    }
    private void DrawLobbyConnection()
    {
        canvas.Text(Connected ? "Invite & connection" : "Connection options", 204, 179, Ink, 1.4f, 1020, 1, true);
        if (!Connected) {
            canvas.Text("Let the host request port forwarding from their router.", 208, 269, Ink, 1.05f, 1100, 2);
            Button("forward", (forward ? "[x] " : "[ ] ") + "Automatic router port forwarding", 208, 354, 730, new ShellIntent.TogglePortForwarding(), !busy, forward, 48);
            canvas.Text("If the router cannot forward ports, use a shared private VPN or a reachable host address. This setting applies when you create a table.", 210, 454, Muted, .92f, 1110, 4);
            return;
        }
        var state = lobby!;
        canvas.Text(state.Mode == "hosting" ? "Share an invite for the network your friends will use." : "You are connected. Your host manages the table and invites.", 208, 245, Muted, .83f, 1110, 2);
        if (state.Mode != "hosting") return;
        canvas.Text("Router mapping: " + state.PortMapping, 209, 301, Ink, .85f, 1100, 1);
        canvas.Text("A router mapping is not a verified internet connection. Local or VPN addresses work only on the matching network.", 209, 337, Muted, .71f, 1140, 2);
        if (!string.IsNullOrWhiteSpace(state.InternetInvite)) {
            Button("copy-invite", "Copy internet invite", 210, 404, 398, new ShellIntent.CopyInternetInvite(), !busy, true, 42);
        } else canvas.Text("No internet invite yet. Choose a local or VPN address below.", 210, 411, Gold, .79f, 1140, 1);
        for (int i = 0; i < Math.Min(5, state.Addresses.Length - connectionPage * 5); i++) {
            var address = state.Addresses[connectionPage * 5 + i]; int y = 474 + i * 61;
            canvas.Rounded(new(208, y, 1186, 51), new(29, 43, 57), 6);
            canvas.Text(address.Label, 222, y + 16, Ink, .74f, 325, 1);
            canvas.Text(address.Url, 561, y + 16, Muted, .74f, 540, 1);
            Button("copy-address:" + address.Url, "Copy", 1246, y + 7, 132, new ShellIntent.CopyAddress(address.Url), !busy, height: 35);
        }
        Button("connection-prev", "Previous", 208, 818, 140, new ShellIntent.Page(ShellPage.Connection, -1), connectionPage > 0, height: 34);
        Button("connection-next", "Next", 360, 818, 110, new ShellIntent.Page(ShellPage.Connection, 1), (connectionPage + 1) * 5 < state.Addresses.Length, height: 34);
    }
}
