namespace WorkPlanStudio.Services.Import;

/// <summary>
/// Which column of the uploaded file feeds which field.
/// </summary>
/// <remarks>
/// <para>
/// The guess is a convenience, not the contract. Header names are the only clue a
/// CSV carries and they are written by people: <c>Stückzeit</c>, <c>Run time</c>,
/// <c>t_e</c>. So the mapping is always shown, always editable, and a required
/// field with no column stops the import rather than importing a column of zeros.
/// </para>
/// <para>
/// A remembered mapping is stored by <em>header name</em>, not by column index.
/// A monthly export that gains a column at the front would otherwise silently
/// shift every field by one, which is the kind of defect that shows up three
/// imports later in a costing report.
/// </para>
/// </remarks>
public sealed class ColumnMap
{
    /// <summary>The value for "this field has no column".</summary>
    public const int Unmapped = -1;

    private readonly Dictionary<string, int> _columnByField = new(StringComparer.Ordinal);

    private ColumnMap(ImportSchema schema, IReadOnlyList<string> header)
    {
        Schema = schema;
        Header = header;
        foreach (var field in schema.Fields)
            _columnByField[field.Key] = Unmapped;
    }

    /// <summary>The sheet being mapped.</summary>
    public ImportSchema Schema { get; }

    /// <summary>The header row as it was read, for the column pickers.</summary>
    public IReadOnlyList<string> Header { get; }

    /// <summary>An empty mapping over a header row.</summary>
    public static ColumnMap Empty(ImportSchema schema, IReadOnlyList<string> header)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(header);
        return new ColumnMap(schema, header);
    }

    /// <summary>
    /// Matches header names to fields: first an exact match on an alias, then a
    /// containment match for the compound names exports are full of
    /// (<c>Arbeitsplatz-Code</c>, <c>Setup time [min]</c>). Every column feeds at
    /// most one field, and the first field declared wins a contested column.
    /// </summary>
    public static ColumnMap Guess(ImportSchema schema, IReadOnlyList<string> header)
    {
        var map = Empty(schema, header);
        var normalised = header.Select(ImportSchemas.Normalise).ToArray();
        var taken = new bool[normalised.Length];

        foreach (var entry in schema.Fields)
        {
            // The first column that is both a match and still free. Taking the
            // first match outright would leave a field unmapped whenever two
            // columns answer to the same alias — and the second one is usually
            // the one it wanted.
            for (var index = 0; index < normalised.Length; index++)
            {
                if (taken[index] || normalised[index].Length == 0)
                    continue;
                if (!entry.Aliases.Contains(normalised[index], StringComparer.Ordinal))
                    continue;

                map._columnByField[entry.Key] = index;
                taken[index] = true;
                break;
            }
        }

        foreach (var entry in schema.Fields)
        {
            if (map._columnByField[entry.Key] != Unmapped)
                continue;

            for (var index = 0; index < normalised.Length; index++)
            {
                if (taken[index] || normalised[index].Length < 3)
                    continue;

                var contains = entry.Aliases.Any(alias =>
                    alias.Length >= 3
                    && (normalised[index].Contains(alias, StringComparison.Ordinal)
                        || alias.Contains(normalised[index], StringComparison.Ordinal)));

                if (!contains)
                    continue;

                map._columnByField[entry.Key] = index;
                taken[index] = true;
                break;
            }
        }

        return map;
    }

    /// <summary>
    /// Re-applies a remembered mapping to a fresh header row, falling back to the
    /// guess for any field whose remembered column is not in this file.
    /// </summary>
    public static ColumnMap Restore(
        ImportSchema schema,
        IReadOnlyList<string> header,
        IReadOnlyDictionary<string, string> remembered)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(remembered);

        var map = Guess(schema, header);
        var normalised = header.Select(ImportSchemas.Normalise).ToArray();

        foreach (var entry in schema.Fields)
        {
            if (!remembered.TryGetValue(entry.Key, out var name))
                continue;

            var index = name.Length == 0 ? Unmapped : Array.IndexOf(normalised, name);
            if (index >= 0 || name.Length == 0)
                map.Set(entry.Key, index < 0 ? Unmapped : index);
        }

        return map;
    }

    /// <summary>The remembered form: field key to normalised header name.</summary>
    public Dictionary<string, string> ToRemembered() =>
        _columnByField.ToDictionary(
            entry => entry.Key,
            entry => entry.Value == Unmapped ? "" : ImportSchemas.Normalise(Header[entry.Value]),
            StringComparer.Ordinal);

    /// <summary>The column feeding a field, or <see cref="Unmapped"/>.</summary>
    public int this[string fieldKey] => _columnByField.GetValueOrDefault(fieldKey, Unmapped);

    /// <summary>Points a field at a column, releasing whatever else pointed there.</summary>
    public void Set(string fieldKey, int column)
    {
        if (!_columnByField.ContainsKey(fieldKey))
            return;

        if (column >= 0)
            foreach (var other in _columnByField.Where(entry => entry.Value == column && entry.Key != fieldKey).ToList())
                _columnByField[other.Key] = Unmapped;

        _columnByField[fieldKey] = column >= 0 && column < Header.Count ? column : Unmapped;
    }

    /// <summary>Required fields with no column. While this is non-empty nothing may be imported.</summary>
    public IReadOnlyList<ImportField> MissingRequired() =>
        [.. Schema.Required.Where(entry => this[entry.Key] == Unmapped)];

    /// <summary>The header cell feeding a field, for a message that names a column.</summary>
    public string ColumnName(string fieldKey)
    {
        var index = this[fieldKey];
        return index >= 0 && index < Header.Count && !string.IsNullOrWhiteSpace(Header[index])
            ? Header[index].Trim()
            : fieldKey;
    }

    /// <summary>
    /// The value of a field in a record, or the empty string when the field has no
    /// column or the record is short. A short row is a rejected row, but that is
    /// the caller's judgement to make and it needs the other columns to report it.
    /// </summary>
    public string Value(CsvRecord record, string fieldKey)
    {
        ArgumentNullException.ThrowIfNull(record);
        var index = this[fieldKey];
        return index >= 0 && index < record.Fields.Count ? ImportValues.Clean(record.Fields[index]) : "";
    }
}
