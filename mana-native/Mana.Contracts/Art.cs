namespace Mana.Contracts;

public sealed record ArtRequest(string Name, string Face)
{
    public string Key => Face == "back" ? Name + "\0back" : Name;
}
public interface ICardArtSource : IDisposable
{
    Task<byte[]?> LoadAsync(ArtRequest art, CancellationToken cancellationToken);
}
