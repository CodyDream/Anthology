#nullable enable

using System.Collections.Generic;

namespace CDS.ProwlFork.United;

/// <summary>Base type for values parsed from a Unity YAML document.</summary>
/// <remarks>
/// The model is intentionally small: scalar strings, maps and lists. Unity's typed
/// values ({fileID: ...}, {x: ..., y: ...}, etc.) are represented as maps and can
/// be interpreted by callers with <see cref="UnityYaml.ParseRef"/> or by walking
/// the tree with the <c>Get</c> extension in <see cref="UnityYaml"/>.
/// </remarks>
public abstract record UnityYamlNode;

/// <summary>A plain scalar value. Numbers, booleans and strings are all kept as text.</summary>
/// <param name="Value">The scalar text, with YAML quoting removed.</param>
public sealed record ScalarNode(string Value) : UnityYamlNode;

/// <summary>A YAML mapping.</summary>
/// <param name="Fields">Mapping from key to parsed value.</param>
public sealed record MapNode(IReadOnlyDictionary<string, UnityYamlNode> Fields) : UnityYamlNode;

/// <summary>A YAML sequence.</summary>
/// <param name="Items">Items in document order.</param>
public sealed record ListNode(IReadOnlyList<UnityYamlNode> Items) : UnityYamlNode;
