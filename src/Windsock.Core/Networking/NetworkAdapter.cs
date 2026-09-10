namespace Windsock.Core.Networking;

/// <summary>
/// One of the machine's real network adapters.
/// </summary>
public sealed record NetworkAdapter(ulong Id, string Name, string Description, bool IsUp)
{
    /// <summary>
    /// The identifier meaning "every adapter, added together".
    /// </summary>
    public const ulong All = 0;

    /// <summary>Returns the name, so a picker announces the adapter by it.</summary>
    public override string ToString() => Name;
}
