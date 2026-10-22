using Mana.Contracts;
using Mana.Magic;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private GameSnapshot? artPreloadSnapshot;
    private (Card[] Visible, Card[] Anticipated) matchArtPreload = ([], []);
    private DeckDetails? artPreloadDeck;
    private Card[] selectedDeckArtwork = [];
    private Card? artPreloadSelection, artPreloadReview;
    private CatalogPage? artPreloadCatalog, artPreloadPrintings;
    private HandPractice? artPreloadPractice;
    private Card[]? artPreloadUpcoming;
    private (Card[] Visible, Card[] Anticipated) setupArtPreload = ([], []);

    internal (Card[] Visible, Card[] Anticipated) ArtPreloadCards()
    {
        var upcoming = SelectedDeckArtwork();
        if (match is { } state) {
            if (ReferenceEquals(artPreloadSnapshot, state) && ReferenceEquals(matchArtPreload.Anticipated, upcoming)) return matchArtPreload;
            artPreloadSnapshot = state;
            // Use only the seat-filtered snapshot, including a current reveal
            // or search. Never infer a library order or consult another deck.
            return matchArtPreload = (PrintableFaces(CardViews.Visible(state)).ToArray(), upcoming);
        }
        artPreloadSnapshot = null;

        var selection = deckEditor ? DeckSelection?.Card : null;
        var catalog = deckEditor ? catalogPage : null;
        var printings = deckEditor && workshopDialog == "printings" ? printingPage : null;
        var practice = deckEditor ? workshopHand : null;
        var review = deckEditor ? null : reviewCard?.Card;
        if (ReferenceEquals(artPreloadSelection, selection) && ReferenceEquals(artPreloadCatalog, catalog)
            && ReferenceEquals(artPreloadPrintings, printings) && ReferenceEquals(artPreloadPractice, practice)
            && ReferenceEquals(artPreloadReview, review) && ReferenceEquals(artPreloadUpcoming, upcoming)) return setupArtPreload;
        artPreloadSelection = selection; artPreloadCatalog = catalog; artPreloadPrintings = printings;
        artPreloadPractice = practice; artPreloadReview = review; artPreloadUpcoming = upcoming;
        var visible = new List<Card>();
        if (selection != null) visible.Add(selection);
        if (catalog != null) visible.AddRange(catalog.Cards.Select(card => card.Card));
        if (printings != null) visible.AddRange(printings.Cards.Select(card => card.Card));
        if (practice != null) visible.AddRange(practice.Hand.Select(card => card.Card));
        if (review != null) visible.Add(review);

        // A selected deck belongs to this player and is known before starting
        // the table. Warm its unique images without retaining a GPU deck copy.
        return setupArtPreload = (PrintableFaces(visible).ToArray(), upcoming);
    }

    private Card[] SelectedDeckArtwork()
    {
        if (ReferenceEquals(artPreloadDeck, selectedDeckDetails)) return selectedDeckArtwork;
        artPreloadDeck = selectedDeckDetails;
        return selectedDeckArtwork = PrintableFaces(selectedDeckDetails?.Entries
            .OrderBy(entry => entry.Section == "Commander" ? 0 : 1).Select(entry => entry.Card) ?? []).ToArray();
    }

    private static IEnumerable<Card> PrintableFaces(IEnumerable<Card> cards)
    {
        var seen = new HashSet<string>();
        foreach (var card in cards) {
            if (CardPresentation.Artwork(card) is not { } front) continue;
            if (seen.Add(front.Key)) yield return card;
            // OtherFace exists only when the engine exposes that printed face.
            // In particular a face-down occurrence never reaches this branch.
            if (card.OtherFace is not { } other) continue;
            var back = card with { Name = other.Name, ArtName = other.ArtName, ArtFace = other.ArtFace, OtherFace = null };
            if (CardPresentation.Artwork(back) is { } artwork && seen.Add(artwork.Key)) yield return back;
        }
    }
}
