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
        Array.AsReadOnly(frame.Drawing.Commands.Select(command => command switch {
            DrawCommand.Text text => new ObservedText(text.Value ?? "", new(text.X, text.Y, text.Width, 0), text.Size,
                text.MaxLines, text.Bold, false, frame.Drawing.TextScale),
            DrawCommand.CenterText text => new ObservedText(text.Value ?? "", ObservedBounds.From(text.Bounds), text.Size,
                1, text.Bold, true, frame.Drawing.TextScale),
            _ => null
        }).OfType<ObservedText>().ToArray()), new(frame.BoardView.Offset.X, frame.BoardView.Offset.Y, frame.TurnFollowEnabled));
}

internal sealed record ObservedBoardView(float X, float Y, bool FollowTurnEnabled);

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
