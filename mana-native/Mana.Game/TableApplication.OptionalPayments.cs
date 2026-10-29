using Mana.Client;
using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private OptionalPaymentOffer? CurrentOptionalPayment => fixture == null && !session.NeedsRecovery
        ? session.CurrentOptionalPayment : null;
    // Quiet skipping owns the current answer only while local automation can
    // continue. Hold and reading must leave the real decline reachable.
    private bool QuietUnpayableOptionalPayment => CurrentOptionalPayment?.Availability == OptionalManaPaymentAvailability.Unpayable
        && !held && !ExperienceLabOpen && !commandFailure
        && AutomaticInteractionPause() == DecisionDockPauseReason.None;

    private void SetOptionalPaymentMode(OptionalPaymentMode mode)
    {
        if (CurrentOptionalPayment is not { } offer) return;
        if (mode == OptionalPaymentMode.Ask) {
            session.TrySetOptionalPaymentPolicy(mode);
            return;
        }
        if (busy) return;
        StopQuietPriority(); StopStackResolution(); session.CancelAdvance();
        if (!session.TrySetOptionalPaymentPolicy(mode)) return;
        held = false;
        error = ""; commandFailure = false;
    }

    // A saved optional answer does not yield priority. Only this exact current
    // source payment reserves the lane; dormant policies leave normal play alone.
    private bool UpdateOptionalPayments(bool interactionBlocked)
    {
        if (ResolvingCapturedStack) return false;
        if (session.OptionalPaymentPolicies.IsEmpty && CurrentOptionalPayment == null) return false;
        if (interactionBlocked || held || fixture != null || ExperienceLabOpen || commandFailure || session.NeedsRecovery) {
            session.PauseOptionalPayments(); return false;
        }
        session.ResumeOptionalPayments();
        if (CurrentOptionalPayment is not { } offer || offer.Paused && offer.Availability != OptionalManaPaymentAvailability.Unpayable
                || !(offer.Availability == OptionalManaPaymentAvailability.Unpayable
                || offer.Mode == OptionalPaymentMode.AutoDecline || offer.Mode == OptionalPaymentMode.AutoPay && offer.CanAutoPay)) return false;
        // No payable route is an engine fact, not the UI planner's estimate.
        // Quiet rejection needs no full per-trigger reading beat; paid/declined
        // preferences retain the ordinary source announcement before continuing.
        if (offer.Availability != OptionalManaPaymentAvailability.Unpayable && !matchPresentation.ReadyForStack(now)) return true;
        if (!busy && !polling && session.AdvanceOptionalPayments(PresentationPacing.ShouldPresent))
            pendingActionLabel = offer.Availability == OptionalManaPaymentAvailability.Unpayable
                ? "Skipping an optional cost that cannot be paid." : "Applying your source's optional payment choice.";
        return true;
    }

    private int OptionalPaymentControlsHeight(int width) => CurrentOptionalPayment is not { } offer ? 0
        : SourceActionTextLayout.Measure(canvas, "This ability " + OptionalPaymentCost(offer.ManaParts) + " \u00b7 this game",
            .57f, width - 16, true).Height + 68;

    // MTGO attaches Always Yes/No to the source ability separately from a yield.
    // The exact current bill above these choices supplies their fixed terms.
    private void DrawOptionalPaymentControls(Rectangle area)
    {
        if (CurrentOptionalPayment is not { } offer) return;
        var caption = SourceActionTextLayout.Measure(canvas, "This ability " + OptionalPaymentCost(offer.ManaParts) + " \u00b7 this game",
            .57f, area.Width - 16, true);
        for (int line = 0; line < caption.Lines.Count; line++)
            PaintSourceActionLine(caption, line, area.X + 8, area.Y + 2 + line * caption.LineHeight, Muted, .57f, true);
        var modes = new[] {
            (Mode: OptionalPaymentMode.Ask, Label: "Ask", Id: "optional-payment-ask"),
            (Mode: OptionalPaymentMode.AutoPay, Label: "Auto pay", Id: "optional-payment-auto-pay"),
            (Mode: OptionalPaymentMode.AutoDecline, Label: "Auto decline", Id: "optional-payment-auto-decline")
        };
        int[] widths = modes.Select(mode => canvas.TextWidth(mode.Label, .66f, true) + 20).ToArray();
        int total = widths.Sum(), x = area.X + Math.Max(0, (area.Width - total) / 2);
        for (int index = 0; index < modes.Length; index++) {
            var mode = modes[index];
            MenuAction(mode.Id, mode.Label, new(x, area.Y + caption.Height + 8, widths[index], 35),
                new MatchIntent.SetOptionalPaymentMode(mode.Mode), (mode.Mode == OptionalPaymentMode.Ask || !busy)
                    && (mode.Mode != OptionalPaymentMode.AutoPay || offer.CanAutoPay),
                offer.Mode == mode.Mode, .66f);
            x += widths[index];
        }
        if (offer.Mode != OptionalPaymentMode.Ask && offer.Paused)
            canvas.Text(session.OptionalPaymentsPaused ? "Paused while reading" : "Paused \u00b7 choose a mode to continue",
                area.X + 8, area.Bottom - 19, Gold, .52f, area.Width - 16, 1);
    }

    private static string OptionalPaymentCost(System.Collections.Immutable.ImmutableArray<ManaBillPart> parts)
        => string.Join(" ", parts.Select(part => part.Count > 1 ? part.Symbol + " \u00d7" + part.Count : part.Symbol));

    private static string OptionalPaymentModeLabel(OptionalPaymentPolicyEntry policy)
        => (policy.Mode == OptionalPaymentMode.AutoPay ? "Auto pay " : "Auto decline ") + OptionalPaymentCost(policy.ManaParts);

    private void ClearOptionalPaymentPolicy(string id)
    {
        session.ClearOptionalPaymentPolicy(id);
        session.CancelAdvance();
    }

    private void DrawOptionalPaymentMarkers()
    {
        if (match is not { Status: "playing" } state || session.NeedsRecovery || OverlayOpen || CardCollectionOpen) return;
        foreach (var group in session.OptionalPaymentPolicies.GroupBy(policy => policy.SourceCardId, StringComparer.Ordinal)) {
            var source = CardViews.FindZoneOccurrence(state, group.Key);
            if (source is not { Zone: "Battlefield", Card.FaceDown: false } current || current.PlayerId != state.ViewerId) continue;
            var entry = matchPresentation.Cards.Cards.LastOrDefault(card => card.Zone == "Battlefield"
                && CardViews.Identity(card.Card) == group.Key && !card.Focused);
            if (entry == null || entry.Display.Bounds.IsEmpty) continue;
            var center = entry.Display.Point(.94f, .12f);
            string id = "optional-payment-source:" + group.Key;
            bool over = IsControlHovered(id);
            var color = group.Any(policy => policy.Mode == OptionalPaymentMode.AutoPay) ? Gold : Blue;
            canvas.Circle(center + new Vector2(0, 2), 13, Microsoft.Xna.Framework.Color.Black * .75f);
            canvas.Circle(center, 12, new(10, 24, 31));
            canvas.Ring(center, 10, over ? Ink : color, 1.5f, -.3f, 4.6f);
            canvas.Arrow(center + new Vector2(-1, -10), center + new Vector2(5, -9), over ? Ink : color, 2);
            canvas.Text("A", (int)center.X - 4, (int)center.Y - 9, over ? Ink : color, .5f, 12, 1, true);
            var intent = new MatchIntent.InspectCard(current.Card);
            var bounds = new Rectangle((int)center.X - 15, (int)center.Y - 15, 30, 30);
            hits.Add(new(id, ScopeFor(intent), bounds, intent));
            if (over) DrawTurnHint(string.Join(" \u00b7 ", group.Select(OptionalPaymentModeLabel)) + " \u00b7 Read source to change", center);
        }
    }
}
