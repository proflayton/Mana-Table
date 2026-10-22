using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Renderer;

/// <summary>One currently projected state within a public-name family.
/// Member IDs identify actual occurrences, never interchangeable action keys.</summary>
public sealed record StudyBattlefieldCohort(string FrontCardId, IReadOnlyList<string> MemberIds, Rectangle Bounds);

/// <summary>Study-only spatial association. The engine's cards, states and
/// permissions remain on each SceneCard; this record grants no group action.</summary>
public sealed record StudyBattlefieldFamily(string Id, string Name, string Role, Rectangle Bounds,
    Rectangle NameBounds, Rectangle BodyBounds, IReadOnlyList<StudyBattlefieldCohort> Cohorts)
{
    public int TotalCount => Cohorts.Sum(cohort => cohort.MemberIds.Count);
    public int CohortCount => Cohorts.Count;
    public string LabelCardId => Cohorts[0].FrontCardId;
}
