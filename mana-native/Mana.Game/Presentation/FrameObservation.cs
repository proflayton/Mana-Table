using Mana.Magic;
using Mana.Contracts;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table.Presentation;

/// <summary>Copied, serializable evidence from the actual completed frame. No actions, engine or GPU objects.</summary>
internal sealed record FrameObservation(FrameStamp Stamp, int Width, int Height, int CommandCount,
    IReadOnlyList<ObservedCard> Cards, IReadOnlyList<ObservedControl> Controls, IReadOnlyList<ObservedText> Text,
    ObservedBoardView? BoardView = null)
{
    internal static FrameObservation From(PresentationFrame frame) => new(frame.Stamp, 1600, frame.DesignHeight,
        frame.Drawing.Commands.Count,
        Array.AsReadOnly(frame.Cards.Select(card => new ObservedCard(card.Id,
            card.Card.FaceDown ? "Face-down card" : card.Card.Name, card.Zone, card.Request.Owner,
            ObservedPose.From(card.Rest), ObservedPose.From(card.Display), card.Layer.ToString(), card.Focused,
            card.Card.Power, card.Card.Toughness, card.Card.Damage,
            Array.AsReadOnly(CardPresentation.StateLabels(card.Card)),
            Array.AsReadOnly(CardPresentation.Counters(card.Card).Select(pair => new ObservedCounter(pair.Key, pair.Value)).ToArray()))).ToArray()),
        Array.AsReadOnly(frame.Hits.Select(hit => new ObservedControl(hit.Id, hit.Scope, ObservedBounds.From(hit.Bounds),
            hit.Card == null ? null : CardViews.Identity(hit.Card), hit.Zone, hit.PlayerId, hit.Shape != null)).ToArray()),
        Array.AsReadOnly(ObserveText(frame.Drawing)), new(frame.BoardView.Offset.X, frame.BoardView.Offset.Y, frame.TurnFollowEnabled, frame.BoardView.Scale));

    private static ObservedText[] ObserveText(DrawingFrame drawing)
    {
        var result = new List<ObservedText>();
        var transforms = new Stack<BoardView>();
        var clips = new Stack<Rectangle?>();
        var view = Mana.Renderer.BoardView.Identity;
        Rectangle? clip = null;
        TextMetrics? metrics = null;
        foreach (var command in drawing.Commands) {
            switch (command) {
                case DrawCommand.BeginTransform begin:
                    transforms.Push(view); view = view.Compose(begin.View); break;
                case DrawCommand.EndTransform:
                    view = transforms.Pop(); break;
                case DrawCommand.BeginClip begin:
                    clips.Push(clip);
                    var bounds = view.Apply(begin.Bounds);
                    clip = clip is { } previous ? Rectangle.Intersect(previous, bounds) : bounds;
                    break;
                case DrawCommand.EndClip:
                    clip = clips.Pop(); break;
                case DrawCommand.Text text:
                    if (transforms.Count == 0) {
                        // Preserve the existing unscoped observation contract.
                        result.Add(new(text.Value ?? "", new(text.X, text.Y, text.Width, 0), text.Size,
                            text.MaxLines, text.Bold, false, drawing.TextScale));
                        break;
                    }
                    metrics ??= new TextMetrics();
                    var measured = metrics.TextSize(text.Value ?? "", text.Size, text.Width, text.MaxLines, text.Bold, drawing.TextScale);
                    Add(text.Value ?? "", new(text.X, text.Y, measured.X, measured.Y), text.Size, text.MaxLines, text.Bold, false);
                    break;
                case DrawCommand.CenterText text:
                    if (transforms.Count == 0) result.Add(new(text.Value ?? "", ObservedBounds.From(text.Bounds), text.Size,
                        1, text.Bold, true, drawing.TextScale));
                    else Add(text.Value ?? "", text.Bounds, text.Size, 1, text.Bold, true);
                    break;
            }
        }
        return result.ToArray();

        void Add(string value, Rectangle world, float size, int lines, bool bold, bool centered)
        {
            var displayed = view.Apply(world);
            if (clip is { } active) displayed = Rectangle.Intersect(displayed, active);
            if (displayed.IsEmpty) return;
            result.Add(new(value, ObservedBounds.From(displayed), size * view.Scale, lines, bold, centered, drawing.TextScale));
        }
    }
}

internal sealed record ObservedBoardView(float X, float Y, bool FollowTurnEnabled, float Zoom);

internal sealed record ObservedBounds(int X, int Y, int Width, int Height)
{
    internal static ObservedBounds From(Rectangle bounds) => new(bounds.X, bounds.Y, bounds.Width, bounds.Height);
}
internal sealed record ObservedPose(float X, float Y, float Width, float Height, float Angle, float Tilt, float Perspective, float Shear, float Elevation)
{
    internal static ObservedPose From(Pose pose) => new(pose.Center.X, pose.Center.Y, pose.Size.X, pose.Size.Y, pose.Angle,
        pose.Tilt, pose.Perspective, pose.Shear, pose.Elevation);
}
internal sealed record ObservedCounter(string Name, int Value);
internal sealed record ObservedCard(string Id, string Name, string Zone, int? Owner, ObservedPose Rest, ObservedPose Display,
    string Layer, bool Focused, int? Power, int? Toughness, int Damage, IReadOnlyList<string> States, IReadOnlyList<ObservedCounter> Counters);
internal sealed record ObservedControl(string Id, string Scope, ObservedBounds Bounds, string? CardId, string? Zone, int? PlayerId, bool HasPreciseShape);
internal sealed record ObservedText(string Value, ObservedBounds Bounds, float Size, int MaxLines, bool Bold, bool Centered, float TextScale);
