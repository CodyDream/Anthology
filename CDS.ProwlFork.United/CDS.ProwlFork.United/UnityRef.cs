#nullable enable

namespace CDS.ProwlFork.United;

/// <summary>
/// A Unity object reference, e.g. <c>{fileID: 2100000, guid: abc..., type: 3}</c>.
/// </summary>
/// <param name="FileId">The local file identifier. Zero means no reference.</param>
/// <param name="Guid">The GUID of the referenced asset (empty for local/built-in references).</param>
/// <param name="Type">Reference type: 2 for a built-in Unity object, 3 for an external GUID reference.</param>
public readonly record struct UnityRef(long FileId, string Guid, int Type)
{
    /// <summary>Whether this reference points at nothing (<see cref="FileId"/> is zero).</summary>
    public bool IsNull => FileId == 0;
}
