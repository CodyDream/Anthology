#nullable enable

using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Formats.Tar;

namespace CDS.ProwlFork.United;

/// <summary>
/// Reads Unity <c>.unitypackage</c> files without any Unity dependencies.
/// </summary>
/// <remarks>
/// A unitypackage is a gzip-compressed tarball. Each asset lives in a directory named
/// after its GUID hash and normally contains three members: <c>asset</c>,
/// <c>asset.meta</c> (optional) and <c>pathname</c> (UTF-8 Unity project path).
/// Directory entries that do not contain a <c>pathname</c> member are ignored.
/// </remarks>
public static class UnityPackageReader
{
    /// <summary>
    /// Reads all assets from a <c>.unitypackage</c> archive, preserving the raw bytes of
    /// each asset and its meta file.
    /// </summary>
    /// <param name="packagePath">Path to the <c>.unitypackage</c> file.</param>
    /// <returns>Entries in the order their <c>pathname</c> members appear in the archive.</returns>
    public static IReadOnlyList<UnityPackageEntry> Read(string packagePath)
    {
        var pending = new Dictionary<string, PendingEntry>(System.StringComparer.Ordinal);
        var order = new List<string>();

        using var file = File.OpenRead(packagePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);

        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                continue;

            // Tar names use '/' on every platform; accept '\' defensively as well.
            string name = entry.Name.Replace('\\', '/');
            int slash = name.LastIndexOf('/');
            if (slash <= 0 || slash == name.Length - 1)
                continue;

            string directory = name.Substring(0, slash);
            string fileName = name.Substring(slash + 1);

            if (!IsKnownMember(fileName))
                continue;

            if (!pending.TryGetValue(directory, out var pendingEntry))
            {
                pendingEntry = new PendingEntry();
                pending.Add(directory, pendingEntry);
                order.Add(directory);
            }

            byte[] bytes = ReadAllBytes(entry);
            switch (fileName)
            {
                case "pathname":
                    if (pendingEntry.Path is null)
                    {
                        string path = Encoding.UTF8.GetString(bytes)
                            .TrimEnd('\0', '\r', '\n');
                        pendingEntry.Path = path;
                    }
                    break;
                case "asset":
                    pendingEntry.Asset ??= bytes;
                    break;
                case "asset.meta":
                    pendingEntry.Meta ??= bytes;
                    break;
            }
        }

        var result = new List<UnityPackageEntry>();
        foreach (string directory in order)
        {
            var pendingEntry = pending[directory];
            if (pendingEntry.Path is null)
                continue; // folder entry without a pathname - not an asset

            result.Add(new UnityPackageEntry
            {
                Path = pendingEntry.Path,
                Asset = pendingEntry.Asset ?? [],
                Meta = pendingEntry.Meta
            });
        }

        return result;
    }

    private static bool IsKnownMember(string fileName) =>
        fileName is "pathname" or "asset" or "asset.meta";

    private static byte[] ReadAllBytes(TarEntry entry)
    {
        using var buffer = new MemoryStream();
        entry.DataStream?.CopyTo(buffer);
        return buffer.ToArray();
    }

    private sealed class PendingEntry
    {
        public string? Path;
        public byte[]? Asset;
        public byte[]? Meta;
    }
}
