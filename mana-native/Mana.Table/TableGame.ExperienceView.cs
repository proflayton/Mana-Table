using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableGame
{
    private void DrawWelcome()
    {
        hits.Clear(); hovered = null; readingCards.Clear(); liftedCards.Clear();
        canvas.Gradient(new(0, 64, 1600, 936), new(24, 31, 42), new(12, 17, 24));
        canvas.Glow(new(310, 400), new(940, 810), Teal * .18f);
        canvas.Text("YOUR PLACE FOR COMMANDER", 65, 110, Gold, .7f, 1000, 1, true);
        canvas.Text("Welcome to Mana Table.", 61, 150, Ink, 2.05f, 1430, 1, true);
        canvas.Text("Bring a favorite deck. Try something new. Make the table yours.", 65, 219, Muted, 1.03f, 1430, 2);
        canvas.Text("FOUR PLAYERS   /   40 LIFE   /   YOUR DECISIONS", 65, 278, Muted, .67f, 1430, 1);

        DrawWelcomePath("experience-ai", "PLAY ON YOUR TERMS", "Play with AI",
            "Your deck. Three AI opponents.", "No invite or network setup needed. Choose a precon or bring your own list.",
            "Set up an AI game", 64, true, () => ChooseWelcome("ai"));
        DrawWelcomePath("experience-friends", "GATHER YOUR GROUP", "Play with friends",
            "A seat for everyone.", "Host a four-player table or join with an invite. Everyone brings their own deck.",
            "Find your table", 564, false, () => ChooseWelcome("friends"));
        DrawWelcomePath("experience-decks", "MAKE IT YOURS", "Your decks",
            "Start with your commander.", "Browse saved decks, import a list, or open the builder to make something new.",
            "Explore your decks", 1064, false, () => ChooseWelcome("decks"));

        canvas.Line(new(64, 755), new(1536, 755), LobbyTrim);
        DrawWelcomeStep(1, "Choose a deck", "A precon, an import, or your own build.", 66);
        DrawWelcomeStep(2, "Confirm your list", "Review your commander and deck checks.", 566);
        DrawWelcomeStep(3, "Take your seat", "Set the table, then start Commander.", 1066);

        Button("experience-guide", "How to use the table", 64, 907, 298, OpenTableGuide, loaded && !busy, height: 44);
        canvas.Text("A quick guide to Mana Table's controls.", 384, 922, Muted, .72f, 640, 1);
        Button("experience-skip", "Continue to setup", 1228, 907, 308, DismissWelcome, loaded && !busy, height: 44);
    }

    private void DrawWelcomePath(string id, string eyebrow, string title, string lead, string description,
        string action, int x, bool primary, Action choose)
    {
        var bounds = new Rectangle(x, 347, 472, 360);
        var trim = primary ? Blue * .55f : LobbyTrim;
        canvas.Panel(bounds, primary ? new(25, 40, 53) : new(22, 32, 43), trim, 16);
        canvas.Text(eyebrow, x + 25, 374, primary ? Blue : Gold, .61f, 422, 1, true);
        canvas.Text(title, x + 25, 421, Ink, 1.38f, 422, 1, true);
        canvas.Text(lead, x + 26, 478, Ink, .86f, 420, 2);
        canvas.Text(description, x + 26, 524, Muted, .84f, 420, 4);
        Button(id, action, x + 24, 631, 424, choose, loaded && !busy, primary, 50);
    }

    private void DrawWelcomeStep(int number, string title, string detail, int x)
    {
        canvas.Circle(new(x + 19, 803), 19, Teal * .48f);
        canvas.CenterText(number.ToString(), new(x, 784, 38, 38), Blue, .78f, true);
        canvas.Text(title, x + 55, 785, Ink, .94f, 385, 1, true);
        canvas.Text(detail, x + 55, 824, Muted, .73f, 385, 2);
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
