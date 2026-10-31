using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private string priorityAbilityId = "";
    private int priorityAbilityTextPage;

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

    // Drawing and reader avoidance share the exact measured source entry. This
    // pocket belongs to the player's timing controls, including between prompts.
    private Rectangle AbilityPassingControlBounds()
    {
        var abilities = PriorityAbilityOptions();
        if (match == null || abilities.Length == 0) return Rectangle.Empty;
        var priority = TurnControlLayout.For(SeatHero(match.ViewerId)).PriorityArea;
        int left = Math.Max(priority.X, PendingStackLeft + PendingStackWidth + 14);
        int width = priority.Right - left;
        int headingHeight = canvas.TextSize("ACTIVATION STOPS", .48f, width - 46, 1, true).Y;
        int sourceHeight = canvas.TextSize(abilities[0].SourceName, .66f, width - 28, 80, true).Y;
        int statusHeight = canvas.TextSize("Skipping until your next turn", .48f, width - 28, 1).Y;
        int height = Math.Max(72, 8 + headingHeight + 4 + sourceHeight + 6 + statusHeight + 8);
        return new(left, priority.Top - height - 14, width, height);
    }

    private void DrawPriorityAbilityControl(PriorityAbility[] abilities)
    {
        if (abilities.Length == 0 || AbilityPassingControlBounds() is not { IsEmpty: false } bounds) return;
        var first = abilities[0];
        int ignored = abilities.Count(ability => ability.IgnoredUntilNextTurn);
        const string id = "ability-auto-pass-open";
        bool hover = IsControlHovered(id);
        PaintControlSurface(id, bounds, ignored > 0 ? new(24, 49, 55) : new(23, 34, 43), hover ? Blue : new Color(73, 92, 106), true);
        string heading = abilities.Length == 1 ? "ACTIVATION STOP" : $"{abilities.Length} ACTIVATION STOPS";
        canvas.Text(heading, bounds.X + 14, bounds.Y + 8, ignored > 0 ? Blue : Gold, .48f, bounds.Width - 46, 1, true);
        int sourceTop = bounds.Y + 12 + canvas.TextSize(heading, .48f, bounds.Width - 46, 1, true).Y;
        canvas.Text(first.SourceName, bounds.X + 14, sourceTop, Ink, .66f, bounds.Width - 28, 80, true);
        string status = ignored == 0 ? "Choose what Auto stops for"
            : abilities.Length == 1 ? "Skipping until your next turn" : $"{ignored} skipping until your next turn";
        int statusHeight = canvas.TextSize(status, .48f, bounds.Width - 28, 1).Y;
        canvas.Text(status, bounds.X + 14, bounds.Bottom - statusHeight - 8, Muted, .48f, bounds.Width - 28, 1);
        canvas.Text("›", bounds.Right - 29, bounds.Y + 4, hover ? Ink : Muted, .85f, 20, 1, true);
        var action = new MatchIntent.OpenPriorityPassing(first.Id);
        hits.Add(new(id, ScopeFor(action), bounds, action));
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
        canvas.Panel(panel, new(17, 28, 35), Gold * .65f, 9);
        canvas.Text("Auto · This activation", 426, 160, Gold, .92f, 650, 2, true);
        int closeWidth = Math.Max(196, canvas.TextWidth("Back to table", .76f) + 24);
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
