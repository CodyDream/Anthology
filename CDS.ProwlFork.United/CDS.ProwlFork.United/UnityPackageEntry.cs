#nullable enable

namespace CDS.ProwlFork.United;

/// <summary>
/// A single asset extracted from a Unity <c>.unitypackage</c> archive.
/// </summary>
/// <remarks>
/// <see cref="Path"/> is the original Unity project path read from the entry's
/// <c>pathname</c> file (for example <c>Assets/Materials/Rock.mat</c>).
/// Asset and meta bytes are kept verbatim so callers can decode them however
/// they need (text YAML, binary meshes, textures, ...).
/// </remarks>
public sealed class UnityPackageEntry
{
    /// <summary>The original Unity project path of the asset.</summary>
    public string Path { get; init; } = "";

    /// <summary>Raw bytes of the asset file (the <c>asset</c> member).</summary>
    public byte[] Asset { get; init; } = [];

    /// <summary>Raw bytes of the <c>asset.meta</c> member, or <see langword="null"/> if absent.</summary>
    public byte[]? Meta { get; init; }
}
