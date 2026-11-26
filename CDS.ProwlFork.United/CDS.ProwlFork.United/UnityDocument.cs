#nullable enable

using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace CDS.ProwlFork.United;

/// <summary>
/// One YAML document from a Unity asset file.
/// </summary>
/// <remarks>
/// For documents introduced by a marker such as <c>--- !u!21 &amp;2100000</c>,
/// <see cref="Fields"/> contains the members of the leading class mapping
/// (the <c>Material:</c> / <c>MonoBehaviour:</c> / ... wrapper is unwrapped).
/// For Unity <c>.meta</c> files, which have no document marker, it contains the
/// top-level keys of the file instead.
/// </remarks>
public sealed class UnityDocument
{
    /// <summary>The numeric class id from the document marker (<c>!u!21</c> -&gt; 21), or 0 for meta files.</summary>
    public long ClassId { get; init; }

    /// <summary>The file id from the document marker (<c>&amp;2100000</c> -&gt; 2100000), or 0 for meta files.</summary>
    public long FileId { get; init; }

    /// <summary>Top-level fields of this document.</summary>
    public IReadOnlyDictionary<string, UnityYamlNode> Fields { get; init; } =
        new ReadOnlyDictionary<string, UnityYamlNode>(new Dictionary<string, UnityYamlNode>());
}
