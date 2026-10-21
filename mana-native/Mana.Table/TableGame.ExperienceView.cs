using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableGame
{
    private void DrawWelcome()
    {
        hits.Clear(); hovered = null; ClearCardComposition();
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

        canvas.Line(new(90, 889), new(1510, 889), LobbyTrim);
        DrawUpdateFooter(true);
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
        hits.Clear(); hovered = null; ClearCardComposition();
        int top = (DesignHeight - 390) / 2;
        canvas.Fill(new(0, 0, 1600, DesignHeight), Color.Black * .82f);
        canvas.Panel(new(340, top, 920, 390), new(23, 32, 43), LobbyTrim, 18);
        canvas.Text("Help for this moment", 375, top + 28, Gold, .86f, 660, 1, true);
        Button("experience-guide-close", "Close", 1110, top + 22, 112, CloseTableGuide, height: 36);
        var cue = match == null ? null : Mana.Magic.ControlGuidance.Decision(match, selectedCombat);
        canvas.Text(cue?.Title ?? (match?.Status == "finished" ? "Your game has finished" : "Follow the next action"), 376, top + 99, Ink, 1.2f, 835, 2, true);
        canvas.Text(cue?.Text ?? (match?.Status == "finished" ? "Inspect the final battlefield, or return to setup for another game."
            : "Short reminders appear beside the cards and controls when you need them."), 377, top + 158, Ink, .91f, 830, 3);
        canvas.Text("Reminders retire after successful actions. You can show them again in Settings.", 377, top + 247, Muted, .77f, 830, 2);
        Button("guidance-reset", "Show hints again", 377, top + 314, 270, () => { ResetControlHints(); CloseTableGuide(); }, height: 43);
        Button("experience-guide-done", "Back to it", 940, top + 314, 282, CloseTableGuide, accent: true, height: 43);
    }
}
