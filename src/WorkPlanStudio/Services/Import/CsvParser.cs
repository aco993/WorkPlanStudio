using System.Runtime.CompilerServices;
using System.Text;

namespace WorkPlanStudio.Services.Import;

/// <summary>One parsed record and the physical line it started on.</summary>
/// <param name="Line">1-based line in the file, so a message can point at it.</param>
/// <param name="Fields">The record's fields, separators and quoting removed.</param>
public sealed record CsvRecord(int Line, IReadOnlyList<string> Fields);

/// <summary>
/// A file the parser refuses outright, as opposed to a row the validators
/// reject. It carries a resource key rather than a sentence: the parser is not
/// allowed to know which language the reader uses.
/// </summary>
public sealed class CsvFormatException : Exception
{
    public CsvFormatException(string messageKey, int line, params object[] arguments)
        : base($"{messageKey} at line {line}")
    {
        MessageKey = messageKey;
        Line = line;
        Arguments = arguments ?? [];
    }

    /// <summary>Key in <c>SharedResource</c>.</summary>
    public string MessageKey { get; }

    /// <summary>The line the parser gave up on.</summary>
    public int Line { get; }

    /// <summary>Format arguments for the message.</summary>
    public IReadOnlyList<object> Arguments { get; }
}

/// <summary>
/// An RFC 4180 reader that is strict about everything the specification covers
/// and forgiving about everything it does not.
/// </summary>
/// <remarks>
/// <para>
/// It streams. The alternative — read the file into a string, split on newlines,
/// split each line on the separator — makes two full copies of the text before
/// the first row is looked at, cannot represent a newline inside a quoted field
/// at all, and gives a 10 000-row file a visible pause on a phone. Here the
/// decoder drives a state machine one buffer at a time and exactly one record
/// exists as strings at any moment.
/// </para>
/// <para>
/// Accepted beyond the specification, because real exports contain it:
/// <c>\r\n</c>, <c>\n</c> and bare <c>\r</c> terminators mixed in one file; blank
/// lines anywhere, including before the header; trailing empty columns; and text
/// after a closing quote (<c>"a"b</c> reads as <c>ab</c>) rather than an error
/// nobody can act on. A line break inside a quoted field is normalised to
/// <c>\n</c>, which loses nothing here: every field in this application is
/// single-line and the validators reject control characters.
/// </para>
/// <para>
/// Refused: a quote that is never closed, a field or a record large enough to be
/// an attack rather than a mistake, and a file with more records than the
/// browser's storage could ever hold. Every refusal names the line.
/// </para>
/// </remarks>
public static class CsvParser
{
    /// <summary>
    /// Longest single field. The widest column in this application holds 250
    /// characters, so 64 KB is generous by two orders of magnitude — and it bounds
    /// what one hostile field can make the parser allocate, which an unbounded
    /// <see cref="StringBuilder"/> does not.
    /// </summary>
    public const int MaxFieldCharacters = 64 * 1024;

    /// <summary>
    /// Most fields in one record. The widest sheet this app defines has twelve
    /// columns; a record with more than a thousand is a file whose separator was
    /// guessed wrong, or a deliberate allocation.
    /// </summary>
    public const int MaxColumns = 1024;

    /// <summary>
    /// Most records in one file. Well past what the browser's storage budget can
    /// hold, so reaching it means the file was never going to import.
    /// </summary>
    public const int MaxRecords = 200_000;

    private const int BufferChars = 8 * 1024;

    /// <summary>
    /// Reads records until the reader ends. A record whose fields are all blank is
    /// skipped, which is what makes a leading blank line and a trailing newline
    /// equally harmless.
    /// </summary>
    public static async IAsyncEnumerable<CsvRecord> ReadAsync(
        TextReader reader,
        char separator,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var buffer = new char[BufferChars];
        var fields = new List<string>();
        var field = new StringBuilder();

        var line = 1;
        var recordLine = 1;
        var records = 0;
        var inQuotes = false;
        var pendingQuote = false;
        var pendingCr = false;
        var started = false;

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;

            for (var index = 0; index < read; index++)
            {
                var character = buffer[index];

                // A CRLF is one terminator and was already acted on at the CR, so
                // the LF is swallowed here — including when the pair straddles two
                // buffers, which is why this is a flag and not a look-ahead.
                if (pendingCr)
                {
                    pendingCr = false;
                    if (character == '\n')
                        continue;
                }

                if (pendingQuote)
                {
                    pendingQuote = false;
                    if (character == '"')
                    {
                        Append(field, '"', line);
                        continue;
                    }

                    // The quote closed the field; this character is the tail after it.
                    inQuotes = false;
                }
                else if (inQuotes)
                {
                    switch (character)
                    {
                        case '"':
                            pendingQuote = true;
                            continue;
                        case '\r':
                            Append(field, '\n', line);
                            line++;
                            pendingCr = true;
                            continue;
                        case '\n':
                            Append(field, '\n', line);
                            line++;
                            continue;
                        default:
                            Append(field, character, line);
                            continue;
                    }
                }

                if (character == separator)
                {
                    CloseField(fields, field, line);
                    started = true;
                    continue;
                }

                switch (character)
                {
                    case '"' when field.Length == 0 && (!started || fields.Count > 0):
                        inQuotes = true;
                        started = true;
                        continue;

                    case '\r':
                    case '\n':
                        pendingCr = character == '\r';
                        CloseField(fields, field, line);
                        if (Complete(fields, recordLine, ref records) is { } completed)
                            yield return completed;

                        line++;
                        fields.Clear();
                        started = false;
                        recordLine = line;
                        continue;

                    default:
                        Append(field, character, line);
                        started = true;
                        continue;
                }
            }
        }

        if (inQuotes && !pendingQuote)
            throw new CsvFormatException("Import_Parse_UnterminatedQuote", recordLine);

        if (started || field.Length > 0 || fields.Count > 0)
        {
            CloseField(fields, field, line);
            if (Complete(fields, recordLine, ref records) is { } last)
                yield return last;
        }
    }

    private static void Append(StringBuilder field, char character, int line)
    {
        if (field.Length >= MaxFieldCharacters)
            throw new CsvFormatException("Import_Parse_FieldTooLong", line, MaxFieldCharacters);

        field.Append(character);
    }

    private static void CloseField(List<string> fields, StringBuilder field, int line)
    {
        if (fields.Count >= MaxColumns)
            throw new CsvFormatException("Import_Parse_TooManyColumns", line, MaxColumns);

        fields.Add(field.ToString());
        field.Clear();
    }

    /// <summary>A record, unless every field in it is blank.</summary>
    private static CsvRecord? Complete(List<string> fields, int line, ref int records)
    {
        if (fields.All(string.IsNullOrWhiteSpace))
            return null;

        if (++records > MaxRecords)
            throw new CsvFormatException("Import_Parse_TooManyRows", line, MaxRecords);

        return new CsvRecord(line, [.. fields]);
    }
}
