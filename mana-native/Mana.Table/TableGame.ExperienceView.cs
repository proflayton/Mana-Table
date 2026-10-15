using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableGame
{
    private void DrawWelcome()
    {
        hits.Clear(); hovered = null; readingCards.Clear(); liftedCards.Clear();
        canvas.Gradient(new(0, 0, 1600, 1000), new(20, 29, 39), new(8, 14, 20));
        canvas.Glow(new(1120, 490), new(1150, 860), Teal * .25f);
        canvas.Text("MANA TABLE", 85, 91, Gold, 3.15f, 1100, 1, true);
        canvas.Text("COMMANDER", 93, 194, Muted, .77f, 565, 1, true);
        canvas.Text(experience.WelcomeSeen ? "Your next game starts here." : "Welcome to the table.", 91, 275, Ink, 1.3f, 630, 1, true);
        canvas.Text("Your commander. Your decisions. Your table.", 93, 325, Muted, .86f, 610, 2);

        canvas.CenterText("PULL UP A CHAIR", new(745, 267, 775, 32), Muted, .68f, true);
        DrawLoadingTable(new(1130, 490), 1, pending: false);
        canvas.CenterText("FOUR PLAYERS   /   40 LIFE", new(745, 716, 775, 37), Muted, .73f);

        DrawWelcomePath("experience-ai", "Play with AI", "Your deck against three AI opponents. No network needed.",
            380, true, () => ChooseWelcome("ai"));
        DrawWelcomePath("experience-friends", "Play with friends", "Host a table or join your friends with an invite.",
            520, false, () => ChooseWelcome("friends"));
        DrawWelcomePath("experience-decks", "Your decks", "Browse, import, or build around your commander.",
            660, false, () => ChooseWelcome("decks"));

        canvas.Text("Choose a deck   /   Confirm your list   /   Take your seat", 93, 841, Muted, .8f, 1400, 1);
        canvas.Line(new(90, 889), new(1510, 889), LobbyTrim);
        Button("experience-guide", "Table guide", 90, 920, 226, OpenTableGuide, loaded && !busy, height: 44);
        Button("experience-skip", "Continue to setup", 1202, 920, 308, DismissWelcome, loaded && !busy, height: 44);
    }

    private void DrawWelcomePath(string id, string title, string description, int y, bool primary, Action choose)
    {
        var bounds = new Rectangle(90, y, 570, 112);
        bool enabled = loaded && !busy, over = enabled && bounds.Contains(pointer), down = over && pressed?.Id == id;
        Color fill = down ? new(45, 68, 78) : over ? new(33, 54, 66) : primary ? new(24, 41, 52) : new(18, 29, 39);
        canvas.Gradient(bounds, fill, fill * .6f);
        canvas.Line(new(90, y + 111), new(660, y + 111), over ? Gold * .65f : LobbyTrim, 1);
        if (primary || over) canvas.Line(new(90, y + 18), new(90, y + 94), (over ? Gold : Blue) * (enabled ? 1 : .4f), 3);
        canvas.Text(title, 115, y + 18, enabled ? Ink : Muted, 1.27f, 480, 1, true);
        canvas.Text(description, 117, y + 67, enabled ? Muted : Muted * .6f, .75f, 470, 2);
        Color arrow = over ? Gold : primary ? Blue : Muted * .65f;
        canvas.Line(new(616, y + 43), new(629, y + 56), arrow, 2);
        canvas.Line(new(629, y + 56), new(616, y + 69), arrow, 2);
        if (enabled) hits.Add(new(id, ControlScope(id), bounds, choose));
    }

    private void DrawTableGuide()
    {
        hits.Clear(); hovered = null; readingCards.Clear(); liftedCards.Clear();
        int top = (DesignHeight - 720) / 2;
        canvas.Fill(new(0, 0, 1600, DesignHeight), Color.Black * .82f);
        canvas.Panel(new(166, top, 1268, 720), new(23, 32, 43), LobbyTrim, 18);
        canvas.Text("A few controls. Then it's your game.", 204, top + 27, Ink, 1.32f, 1000, 1, true);
        Button("experience-guide-close", "Close", 1284, top + 24, 112, CloseTableGuide, height: 36);
        canvas.Text("Know Magic? Here's how to make your plays in Mana Table.", 206, top + 82, Muted, .86f, 1150, 2);
        canvas.Line(new(204, top + 130), new(1396, top + 130), LobbyTrim);

        DrawTableGuideBlock(1, "Read and play your cards",
            "Hover to read. Right-click for the full card. Double-click highlighted cards, including your commander, to play them. " +
            "You can also drag cards from your hand onto the table. Click highlighted battlefield cards to use abilities.", top + 151);
        DrawTableGuideBlock(2, "Follow your next action",
            "The lower-right prompt tells you what the table needs from you. Highlighted cards and players show your choices. " +
            "Confirm there or press Space. Click the prompt for more detail.", top + 311);
        DrawTableGuideBlock(3, "Set your own pace",
            "Auto passes priority when you have no playable response. Your plays, required choices, attacks, and blocks still wait for you. " +
            "Use Hold or Ctrl to keep priority this turn. F switches between Auto and Full control.", top + 458);

        canvas.Line(new(204, top + 618), new(1396, top + 618), LobbyTrim);
        canvas.Text("Your Auto pauses while this guide is open.", 208, top + 645, Blue, .77f, 810, 1);
        canvas.Text("You can reopen this guide any time.", 208, top + 676, Muted, .69f, 810, 1);
        Button("experience-guide-done", "Got it", 1110, top + 643, 286, CloseTableGuide, accent: true, height: 49);
    }

    private void DrawTableGuideBlock(int number, string title, string explanation, int y)
    {
        canvas.Circle(new(229, y + 18), 20, Teal * .5f);
        canvas.CenterText(number.ToString(), new(209, y - 2, 40, 40), Blue, .8f, true);
        canvas.Text(title, 275, y, Ink, 1.03f, 1085, 1, true);
        canvas.Text(explanation, 276, y + 42, Muted, .86f, 1081, 4);
    }
}
