namespace Windsock.Core.Insight;

/// <summary>
/// A record kept beside usage history that goes when a period of history is
/// erased: the connection timeline, in the full edition.
/// </summary>
/// <remarks>
/// The history store's <c>Erased</c> event says that something went, not
/// which period, so whatever erases a period calls these too. Erasing all of
/// history is followed by the records themselves, from the event.
/// </remarks>
public interface IErasedWithHistory
{
    /// <summary>Forgets everything in a period. Returns how much went.</summary>
    int Delete(DateTimeOffset from, DateTimeOffset until);
}
