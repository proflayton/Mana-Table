using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private string priorityAbilityId = "";
    private int priorityAbilityTextPage;
    private int priorityAbilityListPage;

    private PriorityAbility[] PriorityAbilityOptions()
    {
        if (match is not { } state || state.Status is not ("playing" or "resolving") || state.Decision is { Payload: not PriorityPayload }) return [];
        return ((state.Decision?.Payload as PriorityPayload)?.Abilities ?? []).Concat(state.IgnoredPriorityAbilities)
            .Where(a => !string.IsNullOrWhiteSpace(a.Id)).DistinctBy(a => a.Id).ToArray();
    }

    private void SetAbilityAutoPass(PriorityAbility ability, bool ignored)
    {
        StopQuietPriority();
        if (match is not { } state || state.Status is not ("playing" or "resolving")) return;
        if (ignored) {
            if (state.Decision is not { Payload: PriorityPayload priority } prompt
                || !priority.Abilities.Any(a => a.Id == ability.Id)) return;
            Send(new IgnorePriorityAbility(state.Id, prompt.Id, ability.Id));
        } else {
            if (!state.IgnoredPriorityAbilities.Any(a => a.Id == ability.Id)) return;
            error = "";
            session.SubmitSetting(new(state.Id, ability.Id));
        }
        overlay = "";
    }

    private void PriorityAbilityButton(string id, string label, Rectangle bounds, UiIntent action, bool enabled = true, bool active = false)
    {
        PaintControlSurface(id, bounds, active ? new(29, 62, 70) : new(28, 39, 50),
            active ? Blue : new Color(73, 92, 106), enabled);
        canvas.CenterText(label, new(bounds.X + 12, bounds.Y, bounds.Width - 24, bounds.Height), enabled ? Ink : Muted, .76f, true);
        if (enabled) hits.Add(new(id, ScopeFor(action), bounds, action));
    }

    // Only the collapsed entry reserves table space. The explicitly opened
    // list owns overlay input; opening it never changes an engine preference.
    private Rectangle AbilityPassingControlBounds()
    {
        var abilities = PriorityAbilityOptions();
        if (match == null || abilities.Length == 0) return Rectangle.Empty;
        var priority = LocalTurnControls.PriorityArea;
        int width = canvas.TextWidth($"Activation stops · {abilities.Length}", .62f, true) + 40;
        int height = Math.Max(40, canvas.TextSize("Activation stops", .62f, width - 40, 1, true).Y + 12);
        var area = new Rectangle(priority.Right - width, priority.Top - height - 8, width, height);
        if (TableStudyEnabled && StudyHudObstacles.Any(bounds => bounds.Intersects(area))) {
            var occupied = StudyHudObstacles.Append(PlacePortraitPriorityArea(PortraitPriorityArea())).ToArray();
            area = DecisionWorkspaceLayout.Place(priority, width, height, occupied,
                bounded: true, minimumWidth: width, minimumHeight: height);
        }
        return area;
    }

    private void DrawPriorityAbilityControl(PriorityAbility[] abilities)
    {
        if (abilities.Length == 0 || AbilityPassingControlBounds() is not { IsEmpty: false } bounds) return;
        int ignored = abilities.Count(ability => ability.IgnoredUntilNextTurn);
        const string id = "ability-auto-pass-open";
        bool hover = IsControlHovered(id);
        canvas.CenterText($"Activation stops · {abilities.Length}", new(bounds.X, bounds.Y, bounds.Width - 26, bounds.Height),
            hover ? Ink : ignored > 0 ? Blue : Muted, .62f, true);
        canvas.CenterText("›", new(bounds.Right - 26, bounds.Y, 24, bounds.Height), hover ? Ink : Muted, .85f, true);
        if (hover) {
            canvas.Line(new(bounds.Left + 8, bounds.Bottom - 3), new(bounds.Right - 8, bounds.Bottom - 3), Blue);
            DrawTurnHint($"{ignored} skipping until your next turn · Open activation stops", bounds.Center.ToVector2());
        }
        var action = new MatchIntent.OpenPriorityPassing();
        hits.Add(new(id, ScopeFor(action), bounds, action));
    }

    private void DrawPriorityAbilityList()
    {
        var abilities = PriorityAbilityOptions();
        if (abilities.Length == 0) return;
        // Expand the workspace before splitting a large list. Complete names
        // and readable rules keep their size, including with enlarged text.
        (int Name, string Rules, int Height) Measure(PriorityAbility ability, int width) {
            int name = canvas.TextSize(ability.SourceName, .76f, width - 36, 80, true).Y;
            var pages = canvas.TextPages(ability.Text, .62f, width - 36, 220, 80);
            string rules = pages[0] + (pages.Length > 1 ? "\nRead complete ability ›" : "");
            int body = canvas.TextSize(rules, .62f, width - 36, 80).Y;
            return (name, rules, name + body + 68);
        }
        bool wide = abilities.Sum(ability => Measure(ability, 508).Height + 12) > 540;
        int width = wide ? 590 : 508, columns = wide ? 2 : 1;
        List<List<(PriorityAbility Ability, Rectangle Bounds, int Name, string Rules)>> pages = [[]];
        int column = 0, top = 0;
        foreach (var ability in abilities) {
            var measured = Measure(ability, width);
            if (top > 0 && top + measured.Height > 540) { column++; top = 0; }
            if (column == columns) { pages.Add([]); column = 0; }
            pages[^1].Add((ability, new(column * (width + 24), top, width, measured.Height), measured.Name, measured.Rules));
            top += measured.Height + 12;
        }
        int page = Math.Clamp(priorityAbilityListPage, 0, pages.Count - 1);
        Rectangle content;
        if (wide) {
            ModalFrame("Activation stops", new MatchIntent.CloseView(), "Back to table", 750);
            // The modal owns its empty body too, so no hidden table card can
            // receive a click. The backdrop is a deliberate collapse gesture.
            var close = hits.ToArray(); hits.Clear();
            var closeIntent = new MatchIntent.CloseView();
            hits.Add(new("ability-list-backdrop", ScopeFor(closeIntent), new(0, 60, 1600, 840), closeIntent, ShowPointer: false));
            hits.Add(new("ability-list-body", ScopeFor(closeIntent), new(166, 94, 1268, 750), new UiIntent.Block(closeIntent.ScopeKind), ShowPointer: false));
            foreach (var hit in close) hits.Add(hit);
            content = new(192, 226, 1204, 540);
        } else {
            int height = Math.Max(280, top + 190);
            var drawer = SystemDrawer("Activation stops", "ability-list-", new MatchIntent.CloseView(), height, "close-overlay");
            content = new(drawer.X + 28, drawer.Y + 132, 508, top);
        }
        int ignored = abilities.Count(ability => ability.IgnoredUntilNextTurn);
        canvas.Text($"{ignored} of {abilities.Length} skipping until your next turn", content.X, content.Y - 45, Blue, .6f, content.Width, 2, true);
        foreach (var row in pages[page]) {
            var bounds = row.Bounds; bounds.Offset(content.Location);
            string id = "ability-read:" + row.Ability.Id;
            bool hover = IsControlHovered(id);
            if (hover) canvas.Rounded(bounds, new Color(35, 60, 69), 5);
            canvas.Text(row.Ability.SourceName, bounds.X + 14, bounds.Y + 9, Ink, .76f, bounds.Width - 36, 80, true);
            canvas.Text(row.Rules, bounds.X + 14, bounds.Y + row.Name + 17, Muted, .62f, bounds.Width - 36, 80);
            string status = row.Ability.IgnoredUntilNextTurn ? "Skipping · until your next turn" : "Stops Auto";
            canvas.Text(status + "   ›", bounds.X + 14, bounds.Bottom - 34, row.Ability.IgnoredUntilNextTurn ? Blue : Gold, .58f, bounds.Width - 36, 2, true);
            canvas.Line(new(bounds.X + 14, bounds.Bottom), new(bounds.Right - 14, bounds.Bottom), Muted * .25f);
            var read = new MatchIntent.ReadPriorityAbility(row.Ability.Id);
            hits.Add(new(id, ScopeFor(read), bounds, read));
        }
        if (pages.Count > 1) {
            int y = content.Bottom + 12;
            MenuAction("ability-list-prev", "‹ Previous", new(content.X, y, 200, 44), new MatchIntent.SetPage(MatchPage.PriorityAbilities, page - 1), page > 0);
            canvas.CenterText($"{page + 1} / {pages.Count}", new(content.X + 208, y, 90, 44), Muted, .62f);
            MenuAction("ability-list-next", "More activations ›", new(content.Right - 300, y, 300, 44), new MatchIntent.SetPage(MatchPage.PriorityAbilities, page + 1), page + 1 < pages.Count);
        }
    }

    private void DrawAbilityAutoPass()
    {
        var abilities = PriorityAbilityOptions();
        if (abilities.Length == 0) return;
        int index = Array.FindIndex(abilities, ability => ability.Id == priorityAbilityId);
        if (index < 0) return;
        var ability = abilities[index];
        // A retained policy can have no current action handle. In that case the
        // disclosed name and rules remain useful, but no guessed face is shown.
        var card = ability.SourceCardKey.Length == 0 ? null
            : VisibleCards().FirstOrDefault(card => card.Key == ability.SourceCardKey && !card.FaceDown);

        hits.Clear(); hovered = null; ClearCardComposition();
        canvas.Fill(new(0, 60, 1600, 840), Color.Black * .4f);
        var panel = new Rectangle(400, 137, 1060, 661);
        var closeIntent = new MatchIntent.CloseView();
        hits.Add(new("ability-reader-backdrop", ScopeFor(closeIntent), new(0, 60, 1600, 840), closeIntent, ShowPointer: false));
        hits.Add(new("ability-reader-body", ScopeFor(closeIntent), panel, new UiIntent.Block(closeIntent.ScopeKind), ShowPointer: false));
        canvas.Panel(panel, new(17, 28, 35), Gold * .65f, 9);
        int closeWidth = Math.Max(196, canvas.TextWidth("Back to table", .76f) + 24);
        int listWidth = Math.Max(180, canvas.TextWidth("Back to list", .76f) + 24);
        int listX = 1434 - closeWidth - listWidth - 12;
        canvas.Text("Auto · This activation", 426, 160, Gold, .92f, listX - 442, 2, true);
        Button("ability-list-back", "Back to list", listX, 156, listWidth, new MatchIntent.OpenPriorityPassing(), height: 40);
        Button("close-overlay", "Back to table", 1434 - closeWidth, 156, closeWidth, new MatchIntent.CloseView(), height: 40);
        int nameHeight = canvas.TextSize(ability.SourceName, .96f, 1008, 80, true).Y;
        canvas.Text(ability.SourceName, 426, 218, Ink, .96f, 1008, 80, true);
        int contentTop = 232 + nameHeight;
        int rulesX = card == null ? 426 : 770;
        int rulesWidth = card == null ? 1008 : 664;
        if (card != null) {
            float height = Math.Min(432, 685 - contentTop);
            PaintInteractionCard(card, new(new(581, contentTop + height / 2), new(height / 1.4f, height)), false, false);
        }
        canvas.Text(ability.IgnoredUntilNextTurn ? "Skipping until your next turn" : "Auto stops for this activation",
            rulesX, contentTop, ability.IgnoredUntilNextTurn ? Blue : Gold, .68f, rulesWidth, 2, true);
        int rulesTop = contentTop + 55;
        const string policy = "Only this activation, until your next turn. You can still use it manually.";
        string mode = !automatic ? "Full control is on. Turn Auto on to use this preference."
            : held ? "Holding this turn. Release Hold to use this preference." : "Other available responses still stop Auto.";
        int modeTop = 723 - canvas.TextSize(mode, .6f, rulesWidth, 80).Y;
        int policyTop = modeTop - 10 - canvas.TextSize(policy, .6f, rulesWidth, 80).Y;
        int rulesBottom = policyTop - 14;
        int rulesHeight = Math.Max(80, rulesBottom - rulesTop);
        var text = canvas.TextPages(ability.Text, .83f, rulesWidth, rulesHeight, 80);
        if (text.Length > 1) text = canvas.TextPages(ability.Text, .83f, rulesWidth, Math.Max(40, rulesHeight - 46), 80);
        int page = Math.Clamp(priorityAbilityTextPage, 0, text.Length - 1);
        canvas.Text(text[page], rulesX, rulesTop, Ink, .83f, rulesWidth, 80);
        if (text.Length > 1) {
            int previousWidth = Math.Max(190, canvas.TextWidth("Previous text", .76f) + 24);
            int nextWidth = Math.Max(165, canvas.TextWidth("More text", .76f) + 24);
            Button("ability-text-prev", "Previous text", rulesX, rulesBottom - 34, previousWidth, new MatchIntent.SetPage(MatchPage.PriorityText, page - 1), page > 0, height: 34);
            Button("ability-text-next", "More text", rulesX + previousWidth + 12, rulesBottom - 34, nextWidth, new MatchIntent.SetPage(MatchPage.PriorityText, page + 1), page + 1 < text.Length, height: 34);
        }
        canvas.Text(policy, rulesX, policyTop, Muted, .6f, rulesWidth, 80);
        canvas.Text(mode, rulesX, modeTop, Muted, .6f, rulesWidth, 80);
        int actionX = abilities.Length > 1 ? 770 : rulesX;
        PriorityAbilityButton(ability.IgnoredUntilNextTurn ? "ability-undo:" + ability.Id : "ability-pass:" + ability.Id,
            ability.IgnoredUntilNextTurn ? "Stop for this ability" : "Skip this activation", new(actionX, 735, 1434 - actionX, 48),
            new MatchIntent.SetPriorityAbility(ability.Id, !ability.IgnoredUntilNextTurn), ability.IgnoredUntilNextTurn || !busy, ability.IgnoredUntilNextTurn);
        if (abilities.Length > 1) {
            Button("ability-prev", "‹", 426, 735, 55, new MatchIntent.ReadPriorityAbility(abilities.ElementAtOrDefault(index - 1)?.Id ?? ""), index > 0, height: 48);
            canvas.CenterText($"{index + 1} / {abilities.Length} activations", new(491, 735, 203, 48), Muted, .62f);
            Button("ability-next", "›", 704, 735, 55, new MatchIntent.ReadPriorityAbility(abilities.ElementAtOrDefault(index + 1)?.Id ?? ""), index + 1 < abilities.Length, height: 48);
        }
    }
}
