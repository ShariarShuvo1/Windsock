namespace Windsock.Core.Hardware;

/// <summary>Whether the PawnIO channel is open, and if not, why not.</summary>
/// <remarks>
/// In its own file because both editions report the state, while the channel
/// itself (<c>PawnIo</c>, <c>CpuThermalProbe</c>) is compiled only into the full
/// edition. The Store edition always reports <see cref="Stopped"/>.
/// </remarks>
public enum PawnIoState
{
    Stopped,

    Running,

    NotInstalled,

    RequiresElevation,

    Unavailable,
}
