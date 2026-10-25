using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private sealed record AbilityActionText(Choice Choice, string Text, int Height);

    private void SubmitAbilityChoice(GameSnapshot state, Decision decision, int index)
    {
        textFocus = false;
        CollapseCardReading();
        Send(new ChooseOptions(state.Id, decision.Id, [index]));
    }

    private sealed record AbilityActionPlan(Rectangle Anchor, Pose? Source, Rectangle Area, List<List<AbilityActionText>> Pages, int TextWidth);
    private AbilityActionPlan? abilityActionPlan;

    private AbilityActionPlan? PrepareAbilityActionLayout()
    {
        if (match is not { Decision: { } decision } state || decisions.Presentation?.Payload is not AbilityChoicePayload abilities || session.NeedsRecovery
            || ActivityOpen || updatesOpen || ExperienceOpen || overlay.Length > 0 || zoneName != null && !BattlefieldFocused) return null;
        var (anchor, sourcePose, reported, zone) = DecisionSourceAnchor(state, decision);
        const float fontSize = .78f;
        const int padding = 12, rowGap = 8, controlsHeight = 40;
        string context = sourcePose != null ? "" : reported is { FaceDown: false } ? reported.Name
            : !string.IsNullOrWhiteSpace(decision.Context.Title) ? decision.Context.Title : reported == null ? "Card ability" : "Face-down card";
        if (context.Length > 0 && zone.Length > 0) context += " · " + zone;
        string Content(Choice choice) => string.Join("\n", new[] { context, choice.Mana, choice.Label, choice.Detail }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        int requestedWidth = Math.Clamp(abilities.Selection.Choices.SelectMany(choice => Content(choice).Split('\n'))
            .Select(line => canvas.TextWidth(line, fontSize, true) + padding * 2).DefaultIfEmpty(120).Max(), 120, 460);
        int requestedHeight = controlsHeight + abilities.Selection.Choices.Sum(choice => Math.Max(48,
            canvas.TextSize(Content(choice), fontSize, requestedWidth - padding * 2, 80, true).Y + padding * 2))
            + (abilities.Selection.Choices.Length - 1) * rowGap;
        var area = DecisionActionArea(anchor, requestedWidth, Math.Min(400, requestedHeight));
        if (area.Width < 100 || area.Height < 110) return null;
        int textWidth = area.Width - padding * 2, bodyHeight = area.Height - controlsHeight;
        List<List<AbilityActionText>> pages = [[]];
        int used = 0;
        foreach (var choice in abilities.Selection.Choices) {
            string content = Content(choice);
            if (content.Length == 0) content = "Use this option";
            foreach (string fragment in canvas.TextPages(content, fontSize, textWidth, bodyHeight - padding * 2, 80, true)) {
                int height = Math.Max(48, canvas.TextSize(fragment, fontSize, textWidth, 80, true).Y + padding * 2);
                if (used > 0 && used + rowGap + height > bodyHeight) { pages.Add([]); used = 0; }
                pages[^1].Add(new(choice, fragment, height));
                used += (used > 0 ? rowGap : 0) + height;
            }
        }
        return new(anchor, sourcePose, area, pages, textWidth);
    }

    private void DrawAbilityActions()
    {
        if (abilityActionPlan is not { } layout || decisions.Presentation?.Payload is not AbilityChoicePayload abilities) return;
        const float fontSize = .78f;
        const int padding = 12, rowGap = 8, controlsHeight = 40;
        var anchor = layout.Anchor; var entry = layout.Source; var area = layout.Area;
        var pages = layout.Pages; int textWidth = layout.TextWidth;
        int page = Math.Clamp(decisions.Page, 0, pages.Count - 1);
        var visible = pages[page];
        int heightNeeded = visible.Sum(row => row.Height) + (visible.Count - 1) * rowGap + controlsHeight;
        int y = Math.Clamp(anchor.Top, area.Top, area.Bottom - heightNeeded);
        bool rightSide = area.Left >= anchor.Right;
        var target = new Vector2(rightSide ? area.Left : area.Right, y + visible[0].Height / 2);
        var connection = entry?.EdgeToward(target, 4)
            ?? new Vector2(rightSide ? anchor.Right : anchor.Left, Math.Clamp((int)target.Y, anchor.Top, anchor.Bottom));
        canvas.Line(connection, target, Blue * .45f, 1.5f);
        if (entry is { } source) canvas.CardFrame(source with { Size = source.Size + new Vector2(5) }, Blue * .55f, 1);
        foreach (var row in visible) {
            string id = "choice:" + row.Choice.Index;
            var bounds = new Rectangle(area.X, y, area.Width, row.Height);
            PaintControlSurface(id, bounds, Panel, new(77, 110, 125), !busy);
            canvas.Text(row.Text, bounds.X + padding, bounds.Y + padding, Ink, fontSize, textWidth, 80, true);
            int index = row.Choice.Index;
            if (!busy) hits.Add(new(id, Scope, bounds, new MatchIntent.ChooseAbility(index)));
            y += row.Height + rowGap;
        }
        if (pages.Count > 1) {
            Button("choicesprev", "‹", area.X, y, 40, new MatchIntent.SetPage(MatchPage.Choice, page - 1), !busy && page > 0, height: 32);
            Button("choicesnext", "›", area.X + 46, y, 40, new MatchIntent.SetPage(MatchPage.Choice, page + 1), !busy && page + 1 < pages.Count, height: 32);
            canvas.Text($"{page + 1} / {pages.Count}", area.X + 98, y + 6, Muted, .6f,
                Math.Min(110, Math.Max(0, area.Width - 98 - (abilities.Selection.Range.Min == 0 ? 117 : 0))), 1);
        }
        if (abilities.Selection.Range.Min == 0)
            Button("choice-cancel", "Cancel", area.Right - 105, y, 105, new MatchIntent.ChooseNone(true), !busy, height: 32);
    }

}
