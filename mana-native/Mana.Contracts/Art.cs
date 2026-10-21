namespace Mana.Contracts;

public sealed record ArtRequest(string Name, string Face, string Set = "", string CollectorNumber = "")
{
    public bool ExactPrinting => !string.IsNullOrWhiteSpace(Set) && !string.IsNullOrWhiteSpace(CollectorNumber) && CollectorNumber != "N.A.";
    public string Key => ExactPrinting ? Name + "\0" + Set.ToLowerInvariant() + "\0" + CollectorNumber + "\0" + Face
        : Face == "back" ? Name + "\0back" : Name;
}
public interface ICardArtSource : IDisposable
{
    Task<byte[]?> LoadAsync(ArtRequest art, CancellationToken cancellationToken);
}
