using System.IO;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using YamlDotNet.RepresentationModel;

namespace GameLauncher.Desktop.Services.Discovery.Sourcing.Scriptable;

/// <summary>
/// Turns a fetched payload into a <see cref="FeedNode"/> tree.
/// </summary>
/// <remarks>
/// Pure and static: text in, nodes out, no network, no clock, no state. Every
/// format quirk worth knowing about is decided here rather than leaking into the
/// mapper, which is what keeps the mapping rules identical across formats.
/// </remarks>
public static class FeedReader
{
    /// <summary>
    /// Parses a payload in the stated format.
    /// </summary>
    /// <param name="text">The payload.</param>
    /// <param name="format">How to read it.</param>
    /// <returns>The parsed tree.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException">The payload is not valid for the format.</exception>
    public static FeedNode Read(string text, FeedFormat format)
    {
        ArgumentNullException.ThrowIfNull(text);

        try
        {
            return format switch
            {
                FeedFormat.Json => ReadJson(text),
                FeedFormat.Yaml => ReadYaml(text),
                FeedFormat.Feed => ReadSyndication(text),
                _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown feed format.")
            };
        }
        catch (Exception ex) when (ex is JsonException or XmlException or YamlDotNet.Core.YamlException)
        {
            // Rewrapped so a caller handling a malformed feed catches one type
            // rather than three that happen to mean the same thing.
            throw new FormatException($"The payload is not valid {format}: {ex.Message}", ex);
        }
    }

    /// <summary>Parses JSON.</summary>
    /// <param name="text">The document.</param>
    /// <returns>The parsed tree.</returns>
    private static FeedNode ReadJson(string text)
    {
        using var document = JsonDocument.Parse(text);
        return FromJson(document.RootElement);
    }

    /// <summary>Converts one JSON element.</summary>
    /// <param name="element">The element to convert.</param>
    /// <returns>The node.</returns>
    private static FeedNode FromJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => FeedNode.Object(
            element.EnumerateObject().Select(p => KeyValuePair.Create(p.Name, FromJson(p.Value)))),

        JsonValueKind.Array => FeedNode.List(element.EnumerateArray().Select(FromJson)),

        JsonValueKind.String => FeedNode.Value(element.GetString()),

        // Raw text rather than a parsed number: this tree is strings, and
        // round-tripping a large integer through a double would silently lose
        // precision on exactly the field most likely to be a file size.
        JsonValueKind.Number => FeedNode.Value(element.GetRawText()),

        JsonValueKind.True => FeedNode.Value("true"),
        JsonValueKind.False => FeedNode.Value("false"),

        _ => FeedNode.Empty
    };

    /// <summary>Parses YAML.</summary>
    /// <param name="text">The document.</param>
    /// <returns>The parsed tree, or an empty node for an empty document.</returns>
    private static FeedNode ReadYaml(string text)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(text));

        return stream.Documents.Count == 0 ? FeedNode.Empty : FromYaml(stream.Documents[0].RootNode);
    }

    /// <summary>Converts one YAML node.</summary>
    /// <param name="node">The node to convert.</param>
    /// <returns>The node.</returns>
    private static FeedNode FromYaml(YamlNode node) => node switch
    {
        YamlMappingNode map => FeedNode.Object(
            map.Children.Select(pair => KeyValuePair.Create(
                pair.Key.ToString(), FromYaml(pair.Value)))),

        YamlSequenceNode sequence => FeedNode.List(sequence.Children.Select(FromYaml)),

        YamlScalarNode scalar => FeedNode.Value(scalar.Value),

        _ => FeedNode.Empty
    };

    /// <summary>
    /// Parses an RSS or Atom feed.
    /// </summary>
    /// <param name="text">The feed.</param>
    /// <returns>The parsed tree, rooted at the document element's contents.</returns>
    /// <remarks>
    /// <para>
    /// One reader for both dialects. Element names are taken without their
    /// namespace, so an Atom feed's <c>entry</c> is reached as <c>entry</c>
    /// rather than by writing a namespace into a manifest — which no one editing
    /// a YAML file by hand should have to do.
    /// </para>
    /// <para>
    /// The tree starts inside the document element, so an RSS feed's items are
    /// at <c>channel.item</c> and an Atom feed's at <c>entry</c>. Including
    /// <c>rss</c> or <c>feed</c> as a level would be a step that carries no
    /// information.
    /// </para>
    /// </remarks>
    private static FeedNode ReadSyndication(string text)
    {
        var document = XDocument.Parse(text, LoadOptions.None);

        return document.Root is null ? FeedNode.Empty : FromXml(document.Root);
    }

    /// <summary>Converts one XML element.</summary>
    /// <param name="element">The element to convert.</param>
    /// <returns>The node.</returns>
    /// <remarks>
    /// An element with children becomes an object; one without becomes its text.
    /// Attributes are stored as fields named with a leading <c>@</c>, which is
    /// what lets <c>enclosure.@url</c> — the address in an RSS feed — be
    /// expressed in the same path syntax as everything else.
    /// </remarks>
    private static FeedNode FromXml(XElement element)
    {
        var fields = new List<KeyValuePair<string, FeedNode>>();

        foreach (var attribute in element.Attributes())
        {
            if (!attribute.IsNamespaceDeclaration)
            {
                fields.Add(KeyValuePair.Create("@" + attribute.Name.LocalName, FeedNode.Value(attribute.Value)));
            }
        }

        foreach (var child in element.Elements())
        {
            fields.Add(KeyValuePair.Create(child.Name.LocalName, FromXml(child)));
        }

        if (fields.Count == 0)
        {
            return FeedNode.Value(element.Value);
        }

        // Text alongside children is kept under a reserved name, so
        // '<title type="html">Doom</title>' can be read as either the title or
        // its attribute without one hiding the other.
        if (!element.HasElements && !string.IsNullOrWhiteSpace(element.Value))
        {
            fields.Add(KeyValuePair.Create("#text", FeedNode.Value(element.Value)));
        }

        return FeedNode.Object(fields);
    }
}
