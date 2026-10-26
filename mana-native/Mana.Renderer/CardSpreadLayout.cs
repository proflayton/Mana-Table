using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Renderer;

/// <summary>A local view of existing occurrences. It cannot authorize a game action.</summary>
public sealed record CardSpreadRequest(string Id, BoardView? View = null, int Top = 148);

public sealed record CardSpreadPacket(string Id, string Title, string Context,
    IReadOnlyList<string> SourceIds, IReadOnlyList<SceneCard> Cards);
public sealed record CardSpreadSection(CardSpreadPacket Packet, Rectangle Bounds, Vector2 Heading);

/// <summary>A physical collection spread. Cards retain their identity and actual zone;
/// overview and focused views are cameras over the same world-size faces.</summary>
public sealed class CardSpreadPlan
{
    public const string HandId = "hand", AccessId = "play-access";
    public static Rectangle Viewport => new(78, 148, 1444, 576);
    public static Vector2 CardSize => new(186, 260);
    public string Id { get; }
    public IReadOnlyList<SceneCard> Anchors { get; }
    public IReadOnlyList<SceneCard> WorldCards { get; }
    public IReadOnlyList<SceneCard> Cards { get; }
    public IReadOnlyList<CardSpreadSection> Sections { get; }
    public Rectangle Area { get; }
    public Rectangle CameraArea => new(0, Area.Top, TableLayout.Width, Area.Height);
    public Rectangle WorldBounds { get; }
    public BoardView Overview { get; }
    public BoardView InitialView { get; }
    public BoardView View { get; }

    private CardSpreadPlan(string id, SceneCard[] anchors, SceneCard[] world,
        CardSpreadSection[] sections, BoardView? requested, int top)
    {
        Id = id; Anchors = Array.AsReadOnly(anchors); WorldCards = Array.AsReadOnly(world);
        Sections = Array.AsReadOnly(sections);
        Area = new(Viewport.X, Math.Clamp(top, Viewport.Top, 330), Viewport.Width, Viewport.Bottom - Math.Clamp(top, Viewport.Top, 330));
        WorldBounds = sections.Select(section => section.Bounds).Aggregate(Rectangle.Union);
        Overview = BoardView.Frame(WorldBounds, Area, 1);
        // Large collections open at play distance. Seeing their entire extent is
        // an intentional camera gesture, never the default reading scale.
        InitialView = Overview.Scale >= .85f ? Overview
            : Constrain(new(Area.Center.ToVector2() - world[0].Pose.Center, 1));
        View = Constrain(requested ?? InitialView);
        Cards = Array.AsReadOnly(world.Select(card => card with { Pose = View.Apply(card.Pose), Entrance = null, EntranceDelay = 0 }).ToArray());
    }

    public bool Contains(string occurrenceId) => Anchors.Any(card => card.Id == occurrenceId);
    public BoardView Focus(string occurrenceId)
    {
        var card = WorldCards.FirstOrDefault(card => card.Id == occurrenceId);
        return card == null ? View : Constrain(new(Area.Center.ToVector2() - card.Pose.Center, 1));
    }
    public BoardView Zoom(Vector2 screenPoint, float multiplier)
    {
        float scale = Math.Clamp(View.Scale * multiplier, Overview.Scale, 1.5f);
        return Constrain(new(screenPoint - View.Inverse(screenPoint) * scale, scale));
    }
    public BoardView Pan(Vector2 displacement, BoardView? origin = null)
    {
        var from = origin ?? View;
        return Constrain(new(from.Offset + displacement, from.Scale));
    }

    private BoardView Constrain(BoardView view)
    {
        float scale = Math.Clamp(view.Scale, Overview.Scale, 1.5f);
        var offset = view.Offset;
        float Axis(float value, int worldStart, int worldLength, int screenStart, int screenLength)
            => worldLength * scale <= screenLength
                ? screenStart + screenLength / 2f - (worldStart + worldLength / 2f) * scale
                : Math.Clamp(value, screenStart + screenLength - (worldStart + worldLength) * scale,
                    screenStart - worldStart * scale);
        offset.X = Axis(offset.X, WorldBounds.X, WorldBounds.Width, Area.X, Area.Width);
        offset.Y = Axis(offset.Y, WorldBounds.Y, WorldBounds.Height, Area.Y, Area.Height);
        return new(offset, scale);
    }

    public static CardSpreadPlan? Build(string id, IReadOnlyList<CardSpreadPacket> packets, BoardView? view = null, int top = 148,
        Func<CardSpreadPacket, int, int>? measureHeader = null)
    {
        var populated = packets.Where(packet => packet.Cards.Count > 0).ToArray();
        int total = populated.Sum(packet => packet.Cards.Count);
        if (total == 0) return null;
        const int gap = 24, packetGap = 58, padding = 22;
        int columns = Math.Clamp((int)Math.Ceiling(Math.Sqrt(total * 1.8 * (CardSize.Y + gap) / (CardSize.X + gap))), 1, 24);
        int spreadWidth = Math.Max(Viewport.Width, (int)(columns * (CardSize.X + gap) + padding * 2));
        int x = 0, y = 0, rowBottom = 0;
        var world = new List<SceneCard>();
        var sections = new List<CardSpreadSection>();
        foreach (var packet in populated) {
            int packetColumns = Math.Min(columns, packet.Cards.Count);
            int rows = (packet.Cards.Count + packetColumns - 1) / packetColumns;
            int width = (int)(padding * 2 + CardSize.X + (packetColumns - 1) * (CardSize.X + gap));
            int header = packet.Title.Length == 0 ? 0 : Math.Max(100, measureHeader?.Invoke(packet, width - padding * 2) ?? 100);
            int height = (int)(padding * 2 + header + CardSize.Y + (rows - 1) * (CardSize.Y + gap) + 48);
            if (x > 0 && x + width > spreadWidth) { x = 0; y = rowBottom + packetGap; }
            var bounds = new Rectangle(x, y, width, height);
            sections.Add(new(packet, bounds, new(x + padding, y + padding)));
            for (int index = 0; index < packet.Cards.Count; index++) {
                int row = index / packetColumns, column = index % packetColumns;
                int count = Math.Min(packetColumns, packet.Cards.Count - row * packetColumns);
                float bend = count <= 1 ? 0 : (column - (count - 1) / 2f) / Math.Max(1, (count - 1) / 2f);
                var center = new Vector2(x + padding + CardSize.X / 2 + column * (CardSize.X + gap)
                        + (packetColumns - count) * (CardSize.X + gap) / 2,
                    y + padding + header + CardSize.Y / 2 + row * (CardSize.Y + gap) + bend * bend * 34);
                var card = packet.Cards[index];
                world.Add(card with { Pose = new(center, CardSize, bend * .075f), Entrance = card.Pose.Center, EntranceDelay = 0 });
            }
            x += width + packetGap; rowBottom = Math.Max(rowBottom, bounds.Bottom);
        }
        return new(id, populated.SelectMany(packet => packet.Cards).ToArray(), world.ToArray(), sections.ToArray(), view, top);
    }
}
