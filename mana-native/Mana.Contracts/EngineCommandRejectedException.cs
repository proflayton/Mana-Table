namespace Mana.Contracts;

/// <summary>An explicit negative engine response, distinct from a lost or uncertain acknowledgement.</summary>
public sealed class EngineCommandRejectedException(string message) : InvalidOperationException(message) { }
