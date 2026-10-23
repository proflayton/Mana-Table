using Mana.Contracts;
using Mana.Magic;
using System.Collections.Immutable;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table.Interaction;

internal sealed record AnalysisDetail
{
    internal AnalysisDetail(string Title = "", string Text = "", IEnumerable<string>? Cards = null,
        TuningQuestion? Question = null, Card? Suggestion = null, IEnumerable<string>? Missing = null, string Variant = "",
        int Index = -1, IEnumerable<string>? Payoffs = null, AnalysisRecommendation? Recommendation = null)
    {
        this.Title = Title; this.Text = Text; this.Question = Question; this.Suggestion = Suggestion;
        this.Variant = Variant; this.Index = Index; this.Recommendation = Recommendation;
        CardNames = Cards?.ToImmutableArray() ?? [];
        MissingNames = Missing?.ToImmutableArray() ?? [];
        PayoffNames = Payoffs?.ToImmutableArray() ?? [];
    }
    public string Title { get; }
    public string Text { get; }
    public TuningQuestion? Question { get; }
    public Card? Suggestion { get; }
    public string Variant { get; }
    public int Index { get; }
    public AnalysisRecommendation? Recommendation { get; }
    public ImmutableArray<string> CardNames { get; }
    public ImmutableArray<string> MissingNames { get; }
    public ImmutableArray<string> PayoffNames { get; }
}
internal sealed record WorkspaceCardReading(string Scope, string Identity, Card Card, bool OtherFace = false);

// Local deck inspection owns selection reconciliation, navigation and reader
// lifetime. It never edits a saved document or manufactures engine authority.
internal sealed class DeckInteractionState
{
    private ImmutableHashSet<string> relatedCards = ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase);
    public int AnalysisTab { get; private set; }
    public int AnalysisPage { get; private set; }
    public bool ShowReviewed { get; private set; }
    public AnalysisDetail Detail { get; private set; } = new();
    public string DetailBinding { get; private set; } = "";
    public bool Advanced { get; private set; }
    public string AdvancedText { get; private set; } = "";
    public int EvidenceScroll { get; private set; }
    public int EvidenceMaximum { get; private set; }
    public Rectangle EvidenceBounds { get; private set; }
    public WorkspaceCardReading? Reading { get; private set; }
    public ImmutableHashSet<string> RelatedCards => relatedCards;

    public void Reset()
    {
        AnalysisTab = AnalysisPage = 0; ShowReviewed = false; relatedCards = relatedCards.Clear();
        SelectDetail(new(), ""); AdvancedText = ""; EvidenceMaximum = 0; EvidenceBounds = default; Reading = null;
    }
    public void SelectTab(int tab) { AnalysisTab = Math.Clamp(tab, 0, 4); AnalysisPage = 0; }
    public void PageAnalysis(int direction) => AnalysisPage = Math.Max(0, AnalysisPage + direction);
    public void ClampAnalysisPage(int count) => AnalysisPage = Math.Clamp(AnalysisPage, 0, Math.Max(0, count - 1));
    public void ToggleReviewed() { ShowReviewed = !ShowReviewed; AnalysisPage = 0; }
    public void SelectDetail(AnalysisDetail detail, string binding)
    {
        Detail = detail; DetailBinding = binding; Advanced = false; EvidenceScroll = 0; Reading = null;
    }
    public bool NeedsSelection(string binding, IReadOnlyList<int> visible) => DetailBinding != binding
        || visible.Count > 0 && !visible.Contains(Detail.Index) || visible.Count == 0 && Detail.Index != -1;
    public void ShowAdvanced(string text, string binding)
    {
        AdvancedText = text; DetailBinding = binding; Advanced = true; EvidenceScroll = 0;
    }
    public void CloseAdvanced() { Advanced = false; EvidenceScroll = 0; }
    public void PrepareEvidence(Rectangle bounds, int contentHeight)
    {
        EvidenceBounds = bounds; EvidenceMaximum = Math.Max(0, contentHeight - bounds.Height);
        EvidenceScroll = Math.Clamp(EvidenceScroll, 0, EvidenceMaximum);
    }
    public void ScrollEvidence(int direction) => EvidenceScroll = Math.Clamp(EvidenceScroll - direction * 36, 0, EvidenceMaximum);
    public void ClearRelated() => relatedCards = relatedCards.Clear();
    public void ShowRelated() => relatedCards = relatedCards.Union(Detail.CardNames);
    public void ToggleReading(Card card, string identity, string scope) => Reading =
        Reading is { } current && current.Scope == scope && current.Identity == identity ? null : new(scope, identity, card);
    public bool CloseReading(string scope)
    {
        bool visible = Reading?.Scope == scope; Reading = null; return visible;
    }
    public void ReconcileReading(string scope) { if (Reading?.Scope != scope) Reading = null; }
    public void FlipReading(string identity)
    {
        if (Reading is { } reading && reading.Identity == identity && reading.Card.OtherFace != null)
            Reading = reading with { OtherFace = !reading.OtherFace };
    }
}
