using Mana.Contracts;
using Mana.Magic;
using Color = Microsoft.Xna.Framework.Color;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private void DrawWorkshopVersionView()
    {
        if (workshopVersionView is not { } saved) return;
        canvas.Text(saved.Name + " | " + WorkshopVersionLabel(saved.VersionId), 206, 244, Gold, 1, 1160, 1, true);
        canvas.Text("Read only. Your current deck stays unchanged. Saved format: " + saved.Format + "; current: " + saved.CurrentFormat,
            208, 289, Muted, .73f, 1160, 2);
        Button("workshop-view-list", "Saved list", 208, 342, 260, new DeckIntent.CompareVersion(false), !busy, !workshopCompareVersion, 36);
        Button("workshop-view-diff", "Compare to current (" + saved.Differences.Length + ")", 484, 342, 470, new DeckIntent.CompareVersion(true), !busy, workshopCompareVersion, 36);
        int count = workshopCompareVersion ? saved.Differences.Length : saved.Entries.Length;
        for (int i = 0; i < Math.Min(8, count - WorkshopDialogPage * 8); i++) {
            int index = WorkshopDialogPage * 8 + i, y = 405 + i * 47;
            canvas.Rounded(new(206, y, 1176, 40), new(28, 40, 53), 4);
            if (workshopCompareVersion) {
                var row = saved.Differences[index];
                canvas.Text(row.Card + " | " + row.Section, 220, y + 11, Ink, .72f, 835, 1);
                canvas.Text("Saved " + row.Saved + " → Current " + row.Current, 1070, y + 11, Gold, .68f, 297, 1);
            } else {
                var row = saved.Entries[index];
                canvas.Text(row.Quantity + "× " + row.Card.Name + " | " + row.Section + " | " + row.Edition, 220, y + 11, Ink, .73f, 935, 1);
                Button("workshop-version-card:" + index, "Inspect", 1190, y + 3, 178, new DeckIntent.ReadSavedCard(new(row.Id, row.Card, row.ManaValue, row.ColorIdentity, row.Section, row.Edition), "version-view"), !busy, height: 34);
            }
        }
        if (count == 0) canvas.Text(workshopCompareVersion ? "No playable quantity changes. Compare formats above; alternate printings do not count as tuning changes." : "This saved list is empty.", 220, 442, Muted, .85f, 1130, 4);
        Button("workshop-version-prev", "Previous", 208, 817, 160, new DeckIntent.PageWorkshopDialog(-1), WorkshopDialogPage > 0, height: 36);
        Button("workshop-version-next", "Next", 380, 817, 126, new DeckIntent.PageWorkshopDialog(1), (WorkshopDialogPage + 1) * 8 < count, height: 36);
        canvas.Text("Inspection never restores, saves, or changes a deck version.", 560, 827, Muted, .7f, 810, 1);
    }
    private void DrawWorkshopSourceConfirmation()
    {
        if (workshopSourcePreview is not { } preview) return;
        string choice = workshopSourceChoice;
        bool copy = workshopSourceImport || choice == "copy";
        canvas.Text(copy ? "Create a separate linked deck: " + preview.Name : "Confirm " + (choice == "local" ? "Keep local" : choice == "merge" ? "Merge changes" : "Use source"), 206, 244, Gold, 1, 1160, 2, true);
        canvas.Text(copy ? "Your existing deck stays unchanged. The new deck uses the source list, Considering cards, categories and " + preview.Format + " format."
            : DeckExperience.SourceEffect(choice), 208, 308, Ink, .8f, 1160, 4);
        var rows = DeckExperience.SourceRows(preview, choice, copy);
        canvas.Text(copy ? "Source-copy quantities (the original is not edited)" : "Every affected quantity, including local-only and Considering cards", 208, 443, Muted, .72f, 1160, 1);
        for (int i = 0; i < Math.Min(6, rows.Length - WorkshopDialogPage * 6); i++) {
            var row = rows[WorkshopDialogPage * 6 + i]; int y = 481 + i * 43;
            canvas.Rounded(new(206, y, 1176, 38), new(28, 40, 53), 4);
            canvas.Text(row.Card + " | " + row.Section, 220, y + 10, Ink, .7f, 820, 1);
            canvas.Text(copy ? "New copy: " + row.After : "Here " + row.Before + " → After " + row.After, 1070, y + 10, row.Before != row.After ? Gold : Muted, .67f, 297, 1);
        }
        if (rows.Length == 0) canvas.Text("No quantity changes. Accepting still advances the source baseline.", 220, 491, Muted, .8f, 1120, 2);
        Button("workshop-source-confirm-prev", "Previous", 208, 750, 160, new DeckIntent.PageWorkshopDialog(-1), WorkshopDialogPage > 0, height: 32);
        Button("workshop-source-confirm-next", "Next", 380, 750, 126, new DeckIntent.PageWorkshopDialog(1), (WorkshopDialogPage + 1) * 6 < rows.Length, height: 32);
        canvas.Text("Public import/manual refresh only. Nothing is uploaded to Archidekt.", 540, 760, Muted, .68f, 840, 1);
        Button("workshop-source-confirm-cancel", "Back to review", 208, 817, 340, new DeckIntent.ShowSourcePreview(), !busy, height: 40);
        Button("workshop-source-confirm-apply", copy ? "Create linked copy" : "Confirm these changes", 914, 817, 468, new DeckIntent.ConfirmSource(), !busy, true, 40);
    }
}
