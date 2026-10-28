namespace Mana.Contracts;

// Null on Card means unavailable knowledge. Unlimited is explicit rather than
// an arbitrary large finite number. Zero is a finite upper bound.
public sealed record BlockingCapacity(int? Maximum, bool Unlimited = false);

public static class BlockingCapacityFacts
{
    public static void Validate(Card? card)
    {
        if (card?.BlockingCapacity is not { } capacity) return;
        if (card.FaceDown || (capacity.Unlimited ? capacity.Maximum != null : capacity.Maximum is not >= 0))
            throw new ArgumentException("Blocking capacity must be an exposed finite upper bound or explicit unlimited fact.");
    }

    public static void Validate(GameSnapshot state)
    {
        foreach (var occurrence in CardViews.InZones(state)) {
            Validate(occurrence.Card);
            if (occurrence.Card.BlockingCapacity != null && occurrence.Zone != "Battlefield")
                throw new ArgumentException("Current blocking capacity belongs to a battlefield occurrence.");
        }
        foreach (var item in state.Stack) Validate(item.Card);
        foreach (var source in state.ActionSources) {
            Validate(source.Card);
            if (source.Card.BlockingCapacity != null && source.Zone != "Battlefield")
                throw new ArgumentException("A current off-battlefield action source cannot carry blocking capacity.");
        }
    }
}
