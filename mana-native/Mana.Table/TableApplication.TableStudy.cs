using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private bool tableStudy;
    private bool TableStudyEnabled => tableStudy && (ExperienceLabOpen || Automated) && match?.Players.Length is 3 or 4;
    internal void SetTableStudy(bool enabled)
    {
        if (!(ExperienceLabOpen || Automated) || dragging) return;
        tableStudy = enabled;
        ClearCardReading();
        SetTurnFollow(false);
        boardViewMotion.Reset();
        pressed = null;
    }

    private void DrawStudyStackFlow()
    {
        var state = match!;
        var entry = state.Stack[0];
        var area = SharedTableStudy.ActionArea;
        if (cardPresentation.Pinned?.ReaderTarget is { } reading && reading.Bounds.Intersects(area)) {
            var reader = reading.Bounds;
            int leftWidth = Math.Max(0, reader.Left - area.Left - 12);
            int rightWidth = Math.Max(0, area.Right - reader.Right - 12);
            area = leftWidth >= rightWidth
                ? new(area.X, area.Y, Math.Max(1, leftWidth), area.Height)
                : new(reader.Right + 12, area.Y, Math.Max(1, rightWidth), area.Height);
        }
        string owner = entry.ControllerId is { } controller ? StateClarity.PlayerName(state, controller) : "Controller not reported";
        string status = priorityView?.ResponderId is { } responder
            ? responder == state.ViewerId ? "Your priority" : priorityView.ResponderName + " has priority"
            : "Pending";
        var header = new Rectangle(area.X, area.Y, area.Width, 23);
        canvas.Rounded(header, new(11, 22, 28), 4);
        PaintPriorityToken(new(header.X + 12, header.Center.Y), 6, 1);
        canvas.Text(status, header.X + 25, header.Y + 3, PriorityAccent, .52f,
            header.Width - (state.Stack.Length > 1 ? 160 : 35), 1, true);
        if (state.Stack.Length > 1) {
            var count = new Rectangle(header.Right - 125, header.Y, 119, 23);
            canvas.CenterText(state.Stack.Length + " pending", count, Muted, .5f, true);
            hits.Add(new("study-stack-list", Scope, count, () => OpenStack(0)));
        }
        var body = new Rectangle(area.X, header.Bottom + 2, area.Width, area.Height - header.Height - 2);
        canvas.Panel(body, new(17, 29, 36), Gold * .55f, 4);
        var art = new Rectangle(body.X + 10, body.Y + 8, 42, 37);
        if (entry.Card is { FaceDown: false } source && resources.Card(source) is { } image) {
            var crop = new Rectangle((int)(image.Width * .12f), (int)(image.Height * .14f),
                (int)(image.Width * .76f), (int)(image.Height * .38f));
            canvas.Sprite(image, art.Center.ToVector2(), art.Size.ToVector2(), 0, Color.White, crop);
        } else canvas.Fill(art, new(43, 58, 64));
        canvas.Text(entry.Name, art.Right + 10, body.Y + 7, Ink, .67f, body.Width - 78, 1, true);
        canvas.Text(owner, art.Right + 10, body.Y + 29, Gold, .48f, body.Width - 78, 1);
        AddPendingSourceReference(entry, new(art.X, art.Y, body.Width - 20, 40));
        string effect = entry.Text.Length > 0 ? entry.Text
            : !entry.Ability && entry.Card is { Text.Length: > 0 } spell ? spell.Text : "Effect text not reported.";
        var effectArea = new Rectangle(body.X + 12, body.Y + 51, body.Width - 24, 52);
        float effectSize = .66f;
        while (effectSize > .5f && canvas.TextSize(effect, effectSize, effectArea.Width, 80, true).Y > effectArea.Height)
            effectSize = Math.Max(.5f, effectSize - .025f);
        string[] effectPages = canvas.TextPages(effect, effectSize, effectArea.Width, effectArea.Height, 80, true);
        string preview = effectPages[0];
        if (effectPages.Length > 1) {
            while (preview.Length > 0 && canvas.TextSize(preview + " …", effectSize, effectArea.Width, 80, true).Y > effectArea.Height) {
                int space = preview.LastIndexOf(' ');
                preview = space > 0 ? preview[..space] : "";
            }
            preview += " …";
        }
        canvas.Text(preview, effectArea.X, effectArea.Y, Ink,
            effectSize, effectArea.Width, 80, true);
        hits.Add(new("study-stack-effect:" + entry.Id, Scope, effectArea, () => InspectPendingEffect(entry.Id)));
        if (entry.Targets.Length > 0)
            DrawPendingTargetReferences(entry, new(body.X + 12, body.Bottom - 28, body.Width - 24, 24));
        stackRows.Add(new(entry.Id, 1, body));
        // Source/target references retain their own hover and inspection.
        // Keep the normal table free of always-on cross-seat connector webs.
    }
}
