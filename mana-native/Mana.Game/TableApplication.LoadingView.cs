using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private static readonly string[] LoadingStageLabels = ["Card library", "Your decks", "Commander presets", "Your table"];

    private void DrawStartup()
    {
        hits.Clear(); hovered = null; ClearCardComposition();
        canvas.Gradient(new(0, 0, 1600, 1000), new(20, 29, 39), new(8, 14, 20));
        canvas.Glow(new(800, 414), new(1200, 840), Teal * .25f);
        canvas.CenterText("COMMANDER", new(100, 72, 1400, 32), Muted, .7f, true);
        canvas.CenterText("MANA TABLE", new(100, 119, 1400, 88), Gold, 3.15f, true);
        DrawLoadingTable(new(800, 391), startupFailed ? .48f : 1);

        if (startupFailed) {
            canvas.CenterText("Mana Table couldn't start.", new(180, 603, 1240, 48), Ink, 1.4f, true);
            string detail = error.Length > 0 ? error : activityDetail;
            canvas.Text(detail, 295, 676, Red, .91f, 1010, 5);
            Button("activity-retry", "Try again", 490, 848, 300, RetryStartup, !ActivityPending, true, 52);
            Button("activity-exit", "Exit game", 810, 848, 300, RequestExit, !ActivityPending, height: 52);
            return;
        }

        canvas.CenterText(activityTitle.Length > 0 ? activityTitle : "Preparing your table", new(180, 603, 1240, 48), Ink, 1.4f, true);
        canvas.Text(activityDetail, 295, 673, Muted, .88f, 1010, 3);
        for (int i = 0; i < LoadingStageLabels.Length; i++) {
            int x = 200 + i * 300;
            bool current = string.Equals(activityStage, LoadingStageLabels[i], StringComparison.OrdinalIgnoreCase);
            canvas.Line(new(x + 12, 777), new(x + 288, 777), current ? Gold : LobbyTrim, current ? 3 : 1);
            canvas.CenterText(LoadingStageLabels[i], new(x + 10, 799, 280, 39), current ? Ink : Muted * .72f, .75f, current);
            if (current) canvas.Circle(new(x + 150, 855), 3, Gold);
        }
        if (activityStage.Length > 0 && !LoadingStageLabels.Contains(activityStage, StringComparer.OrdinalIgnoreCase))
            canvas.CenterText(activityStage, new(200, 863, 1200, 32), Muted, .73f);
        if (now - activityStarted >= 4)
            canvas.CenterText("Opening the card library can take a little time. Your table will appear when it's ready.", new(200, 920, 1200, 38), Muted, .75f);
    }

    private void DrawActivity()
    {
        hits.Clear(); hovered = null; ClearCardComposition();
        bool failed = !ActivityPending && ActivityFailed;
        if (activityScene && !failed) { DrawTableTransition(); return; }
        int top = (DesignHeight - 344) / 2;
        canvas.Fill(new(0, 0, 1600, DesignHeight), Color.Black * .77f);
        canvas.Glow(new(800, top + 170), new(1020, 630), Teal * .24f);
        canvas.Panel(new(340, top, 920, 344), new(22, 32, 43), failed ? Red * .65f : LobbyTrim, 18);
        if (failed) DrawActivityFailureIndicator(new(800, top + 65), 20);
        else DrawLoadingIndicator(new(800, top + 65), 20, ActivityPending, Gold);
        DrawActivityHeading(failed ? "That action could not finish" : activityTitle, new(380, top + 107, 840, 72));
        canvas.Text(failed ? error : activityDetail, 390, top + 186, failed ? Red : Muted, .9f, 820, 3);
        if (failed) {
            Button("activity-dismiss", "Back", 1010, top + 277, 210, DismissActivity, accent: true, height: 44);
            return;
        }
        double elapsed = Math.Max(0, now - activityStarted);
        if (ActivityPending && elapsed >= 4) {
            string reassurance = elapsed < 15 ? "Working on it. Please keep Mana Table open."
                : "This is taking a little longer. Your request is still in progress.";
            canvas.CenterText(reassurance, new(390, top + 278, 820, 32), Muted, .75f);
        } else if (!ActivityPending && activityCompleted > 0)
            canvas.CenterText("Ready", new(390, top + 278, 820, 32), Blue, .75f);
    }

    private void DrawTableTransition()
    {
        int height = DesignHeight;
        canvas.Gradient(new(0, 0, 1600, height), new(20, 29, 39), new(8, 14, 20));
        canvas.Glow(new(800, height * .43f), new(1200, 840), Teal * .25f);
        canvas.CenterText("COMMANDER", new(100, 48, 1400, 32), Muted, .7f, true);
        canvas.CenterText("MANA TABLE", new(100, 90, 1400, 88), Gold, 3.15f, true);
        DrawLoadingTable(new(800, height * .4f), 1);
        int titleTop = (int)(height * .645f);
        DrawActivityHeading(activityTitle, new(250, titleTop, 1100, 80), 1.5f);
        canvas.Text(activityDetail, 310, titleTop + 100, Muted, .94f, 980, 3);
        if (ActivityPending && now - activityStarted >= 4) {
            string reassurance = now - activityStarted < 15 ? "Getting your table ready. Please keep Mana Table open."
                : "This is taking a little longer. Your request is still in progress.";
            canvas.CenterText(reassurance, new(220, height - 86, 1160, 36), Muted, .77f);
        }
    }

    private void DrawActivityHeading(string title, Rectangle bounds, float size = 1.4f)
    {
        int lineLength = Math.Max(24, (int)(bounds.Width / (22 * size * .63f)));
        if (title.Length <= lineLength) {
            canvas.CenterText(title, bounds, Ink, size, true);
            return;
        }
        // Long deck names get two lines rather than a clipped one-line title.
        float wrappedSize = Math.Max(.9f, Math.Min(Math.Min(size, 1.2f), bounds.Width * 1.8f / (title.Length * 22 * .63f)));
        int splitAt = Math.Min(title.Length - 1, (int)(bounds.Width / (22 * wrappedSize * .63f)));
        int wordBreak = title.LastIndexOf(' ', splitAt);
        if (wordBreak >= splitAt / 2) splitAt = wordBreak;
        int lineHeight = Math.Min(bounds.Height / 2, (int)Math.Ceiling(22 * wrappedSize * 1.4f));
        int top = bounds.Y + Math.Max(0, (bounds.Height - lineHeight * 2) / 2);
        canvas.CenterText(title[..splitAt].Trim(), new(bounds.X, top, bounds.Width, lineHeight), Ink, wrappedSize, true);
        canvas.CenterText(title[splitAt..].Trim(), new(bounds.X, top + lineHeight, bounds.Width, lineHeight), Ink, wrappedSize, true);
    }

    private void DrawInlineActivity()
    {
        bool failed = !ActivityPending && ActivityFailed;
        Color accent = failed ? Red : ActivityPending ? Blue : ReadyColor;
        canvas.Panel(new(40, 951, 1280, 40), failed ? new(54, 29, 34) : new(22, 37, 47), accent * .55f, 7);
        if (failed) DrawActivityFailureIndicator(new(62, 971), 10);
        else DrawLoadingIndicator(new(62, 971), 10, ActivityPending, accent);
        string title = ActivityPending ? activityTitle : failed ? "Could not save changes" : "Changes saved";
        canvas.Text(title, 87, 961, Ink, .76f, 304, 1, true);
        string detail = failed ? error : ActivityPending ? activityDetail : "Your deck is up to date.";
        canvas.Text(detail, 411, 962, failed ? Red : Muted, .73f, 884, 1);
    }

    private void DrawActivityFailureIndicator(Vector2 center, float radius)
    {
        canvas.Ring(center, radius, Red * .55f, 2);
        canvas.Line(center + new Vector2(-radius * .3f), center + new Vector2(radius * .3f), Red, 2);
        canvas.Line(center + new Vector2(-radius * .3f, radius * .3f), center + new Vector2(radius * .3f, -radius * .3f), Red, 2);
    }

    private void DrawLoadingTable(Vector2 center, float opacity, bool pending = true)
    {
        float breath = preferences.ReducedMotion ? 1 : 1 + MathF.Sin((float)now * 1.65f) * .012f;
        var points = new Vector2[80];
        for (int i = 0; i < points.Length; i++) {
            float angle = i * MathF.Tau / points.Length;
            points[i] = center + new Vector2(MathF.Cos(angle) * 351, MathF.Sin(angle) * 129) * breath;
        }
        canvas.Polygon(points, new Color(25, 45, 51) * opacity);
        for (int i = 0; i < points.Length; i++)
            canvas.Line(points[i], points[(i + 1) % points.Length], Gold * (.25f * opacity), 2);

        DrawLoadingCard(center + new Vector2(-292, 0), -.24f, opacity);
        DrawLoadingCard(center + new Vector2(0, -125), 0, opacity);
        DrawLoadingCard(center + new Vector2(292, 0), .24f, opacity);
        DrawLoadingCard(center + new Vector2(0, 125), 0, opacity);
        canvas.Ring(center, 64, LobbyTrim * opacity, 1);
        canvas.Ring(center, 47, Gold * (.25f * opacity), 1);
        if (startupFailed || !pending) canvas.Ring(center, 29, Gold * (.45f * opacity), 2);
        else DrawLoadingIndicator(center, 29, true, Gold * opacity);
        for (int i = 0; i < 4; i++) {
            float angle = i * MathF.PI / 2;
            var seat = center + new Vector2(MathF.Cos(angle) * 398, MathF.Sin(angle) * 183);
            canvas.Circle(seat, 9, Teal * (.7f * opacity));
            canvas.Ring(seat, 13, Gold * (.48f * opacity), 1);
        }
    }

    private void DrawLoadingCard(Vector2 center, float angle, float opacity)
    {
        canvas.Quad(center + new Vector2(3, 5), new(74, 100), angle, Color.Black * (.35f * opacity));
        canvas.Quad(center, new(74, 100), angle, Gold * (.42f * opacity));
        canvas.Quad(center, new(71, 97), angle, new Color(20, 34, 44) * opacity);
        canvas.Quad(center, new(58, 84), angle, Teal * (.25f * opacity));
        canvas.Quad(center, new(55, 81), angle, new Color(20, 34, 44) * opacity);
        canvas.Quad(center, new(19, 19), angle + MathF.PI / 4, Gold * (.42f * opacity));
        canvas.Quad(center, new(15, 15), angle + MathF.PI / 4, new Color(20, 34, 44) * opacity);
    }

    private void DrawLoadingIndicator(Vector2 center, float radius, bool pending, Color color)
    {
        canvas.Ring(center, radius, color * .2f, 2);
        if (!pending) {
            canvas.Line(center + new Vector2(-radius * .42f, 0), center + new Vector2(-radius * .08f, radius * .32f), color, 3);
            canvas.Line(center + new Vector2(-radius * .08f, radius * .32f), center + new Vector2(radius * .47f, -radius * .38f), color, 3);
            return;
        }
        float start = preferences.ReducedMotion ? -.8f : (float)now * 1.9f;
        canvas.Ring(center, radius, color, 3, start, MathF.PI * .7f);
    }
}
