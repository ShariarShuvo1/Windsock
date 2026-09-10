using System.Collections;

namespace Windsock.App.ViewModels;

/// <summary>
/// A fixed backing array exposed as a list whose length can shrink and grow.
/// </summary>
public sealed class SeriesView(double[] buffer) : IReadOnlyList<double>
{
    /// <summary>The backing array, written in place by the owner.</summary>
    public double[] Buffer { get; } = buffer;

    /// <summary>Number of entries currently valid.</summary>
    public int Count { get; set; }

    public double this[int index] => Buffer[index];

    public IEnumerator<double> GetEnumerator()
    {
        for (int i = 0; i < Count; i++)
        {
            yield return Buffer[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
