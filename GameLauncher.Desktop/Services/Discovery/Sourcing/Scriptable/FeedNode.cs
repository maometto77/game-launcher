using System.Globalization;

namespace GameLauncher.Desktop.Services.Discovery.Sourcing.Scriptable;

/// <summary>
/// One node of a parsed feed payload, whatever format it arrived in.
/// </summary>
/// <remarks>
/// <para>
/// JSON, YAML and RSS all describe the same three things: a value, an ordered
/// list, and a set of named children. Normalising them into one tree at the
/// parse boundary means the mapping rules — the part users actually write — are
/// identical across formats, and there is one path implementation rather than
/// three.
/// </para>
/// <para>
/// Immutable and free of any parser type, so the mapper that consumes it needs
/// no reference to <c>System.Text.Json</c>, YamlDotNet or <c>XDocument</c>, and
/// a test can build a payload by hand.
/// </para>
/// </remarks>
public sealed class FeedNode
{
    /// <summary>
    /// Field an XML element's own text is stored under when it also has
    /// attributes or children.
    /// </summary>
    /// <remarks>
    /// Named with a character no XML name may contain, so it can never collide
    /// with a real element or attribute.
    /// </remarks>
    public const string TextField = "#text";

    private static readonly IReadOnlyList<FeedNode> NoItems = [];
    private static readonly IReadOnlyDictionary<string, FeedNode> NoFields =
        new Dictionary<string, FeedNode>(StringComparer.OrdinalIgnoreCase);

    private FeedNode(
        string? scalar,
        IReadOnlyList<FeedNode>? items,
        IReadOnlyDictionary<string, FeedNode>? fields)
    {
        Scalar = scalar;
        Items = items ?? NoItems;
        Fields = fields ?? NoFields;
    }

    /// <summary>A node that holds nothing, returned for every path that misses.</summary>
    /// <remarks>
    /// Returned rather than <see langword="null"/> so a chain of lookups reads
    /// as one expression. A mapping that names a field the payload does not have
    /// is ordinary — most feeds omit most optional fields — and should not
    /// require a null check at every step.
    /// </remarks>
    public static FeedNode Empty { get; } = new(null, null, null);

    /// <summary>The node's own value, or <see langword="null"/> when it has none.</summary>
    public string? Scalar { get; }

    /// <summary>Ordered children, for a list.</summary>
    public IReadOnlyList<FeedNode> Items { get; }

    /// <summary>Named children, for an object.</summary>
    public IReadOnlyDictionary<string, FeedNode> Fields { get; }

    /// <summary>Gets a value indicating whether this node holds nothing at all.</summary>
    public bool IsEmpty => Scalar is null && Items.Count == 0 && Fields.Count == 0;

    /// <summary>Builds a node holding a single value.</summary>
    /// <param name="value">The value, or <see langword="null"/> for an empty node.</param>
    /// <returns>The node.</returns>
    public static FeedNode Value(string? value) => value is null ? Empty : new FeedNode(value, null, null);

    /// <summary>Builds a node holding an ordered list.</summary>
    /// <param name="items">The children.</param>
    /// <returns>The node.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> is <see langword="null"/>.</exception>
    public static FeedNode List(IEnumerable<FeedNode> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new FeedNode(null, items.ToArray(), null);
    }

    /// <summary>Builds a node holding named children.</summary>
    /// <param name="fields">The children, by name.</param>
    /// <returns>The node.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fields"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Names are compared without regard to case, because a payload written by
    /// hand rarely agrees with itself about <c>fileName</c> and <c>filename</c>.
    /// </remarks>
    public static FeedNode Object(IEnumerable<KeyValuePair<string, FeedNode>> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var map = new Dictionary<string, FeedNode>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, node) in fields)
        {
            // A repeated name becomes a list, which is how RSS says "several
            // items" and how a merge of two objects would read anyway.
            map[name] = map.TryGetValue(name, out var existing) ? Append(existing, node) : node;
        }

        return new FeedNode(null, null, map);
    }

    /// <summary>
    /// Walks a dotted path from this node.
    /// </summary>
    /// <param name="path">The path, or empty to return this node.</param>
    /// <returns>The node found, or <see cref="Empty"/>.</returns>
    /// <remarks>
    /// <para>
    /// One syntax for everything: <c>files.0.name</c> walks an object, then a
    /// list by index, then an object again. A segment starting <c>@</c> reads an
    /// XML attribute, which the readers store as an ordinary field.
    /// </para>
    /// <para>
    /// A field name containing a full stop is not addressable. Feeds that use
    /// one are rare enough that supporting an escape would cost every manifest
    /// author more than it saved.
    /// </para>
    /// </remarks>
    public FeedNode Select(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return this;
        }

        var current = this;

        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                // An index against a node that is not a list still means "the
                // first thing", so a feed that drops its single-element array
                // when there is only one item keeps working.
                current = current.Items.Count > 0
                    ? index >= 0 && index < current.Items.Count ? current.Items[index] : Empty
                    : index == 0 ? current : Empty;

                continue;
            }

            if (current.Fields.TryGetValue(segment, out var field))
            {
                current = field;
                continue;
            }

            // A named lookup against a one-element list reaches through it, for
            // the same reason: some publishers wrap, some do not.
            current = current.Items.Count == 1 && current.Items[0].Fields.TryGetValue(segment, out var nested)
                ? nested
                : Empty;
        }

        return current;
    }

    /// <summary>
    /// Reads the value at a path.
    /// </summary>
    /// <param name="path">The path to read.</param>
    /// <returns>The value, or <see langword="null"/> when there is none.</returns>
    /// <remarks>
    /// A list yields its first element's value, so a mapping does not have to
    /// know whether a publisher wrapped a single value in an array. An element
    /// carrying both attributes and text yields the text, which is what
    /// <c>&lt;title type="html"&gt;Doom&lt;/title&gt;</c> means to a reader.
    /// </remarks>
    public string? String(string? path)
    {
        var node = Select(path);

        var value = node.Scalar
                    ?? (node.Items.Count > 0 ? node.Items[0].Scalar : null)
                    ?? (node.Fields.TryGetValue(TextField, out var text) ? text.Scalar : null);

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// Reads an integer at a path.
    /// </summary>
    /// <param name="path">The path to read.</param>
    /// <returns>The number, or <see langword="null"/> when absent or unparseable.</returns>
    /// <remarks>
    /// Invariant culture on purpose: a feed's numbers are the publisher's, not
    /// the reader's, and parsing <c>1,234</c> by the machine's locale would give
    /// a different answer on a different machine.
    /// </remarks>
    public long? Int64(string? path) =>
        long.TryParse(String(path), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    /// <summary>
    /// Reads a path as a list.
    /// </summary>
    /// <param name="path">The path to read, or empty for this node.</param>
    /// <returns>The elements; a single node yields a list of one.</returns>
    /// <remarks>
    /// A feed with one download frequently publishes an object where a feed with
    /// several publishes an array. Treating the single case as a list of one is
    /// what stops that difference reaching the mapping rules.
    /// </remarks>
    public IReadOnlyList<FeedNode> ListAt(string? path)
    {
        var node = Select(path);

        if (node.Items.Count > 0)
        {
            return node.Items;
        }

        return node.IsEmpty ? [] : [node];
    }

    /// <summary>Combines two nodes sharing a name into one list.</summary>
    /// <param name="existing">The node already stored.</param>
    /// <param name="addition">The node being added.</param>
    /// <returns>A list holding both.</returns>
    private static FeedNode Append(FeedNode existing, FeedNode addition)
    {
        var items = new List<FeedNode>();

        // Only an existing *list* is flattened. A list that arrived as one value
        // is a value, and folding it in would lose the distinction between "two
        // items" and "one item that happens to be a list".
        if (existing.Items.Count > 0 && existing.Scalar is null && existing.Fields.Count == 0)
        {
            items.AddRange(existing.Items);
        }
        else
        {
            items.Add(existing);
        }

        items.Add(addition);

        return new FeedNode(null, items, null);
    }
}
