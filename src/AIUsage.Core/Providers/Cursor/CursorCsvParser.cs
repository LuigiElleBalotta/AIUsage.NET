namespace AIUsage.Core.Providers.Cursor;

/// <summary>
/// Minimal CSV parser supporting quoted fields with embedded commas, newlines, and escaped quotes.
/// Direct port of the Swift CursorCSVParser.
/// </summary>
public static class CursorCsvParser
{
    public sealed record Summary(bool IsStructurallyComplete, int RejectedRecordCount);

    public static Summary ForEachRecord(string text, Action<List<string>>? onHeader, Action<Dictionary<string, string>> body)
    {
        List<string>? header = null;
        var rejectedRecordCount = 0;

        var isStructurallyComplete = ForEachRow(text, row =>
        {
            if (header is null)
            {
                var normalized = row.Select(f => f.Trim().Trim('\uFEFF')).ToList();
                header = normalized;
                onHeader?.Invoke(normalized);
                return;
            }

            if (row.Count != header.Count)
            {
                rejectedRecordCount++;
                return;
            }
            var dict = new Dictionary<string, string>();
            for (var i = 0; i < header.Count; i++) dict[header[i]] = row[i];
            body(dict);
        });

        return new Summary(isStructurallyComplete, rejectedRecordCount);
    }

    private enum FieldState { Start, Unquoted, Quoted, QuoteClosed }

    private static bool ForEachRow(string text, Action<List<string>> body)
    {
        var field = new System.Text.StringBuilder();
        var row = new List<string>();
        var state = FieldState.Start;
        var i = 0;

        void Emit()
        {
            if (row.Any(f => f.Length > 0)) body(row);
        }

        while (i < text.Length)
        {
            var c = text[i];

            if (state == FieldState.Quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i += 2;
                        continue;
                    }
                    state = FieldState.QuoteClosed;
                    i++;
                    continue;
                }
                field.Append(c);
                i++;
                continue;
            }

            switch (state)
            {
                case FieldState.Start:
                    if (c == '"') { state = FieldState.Quoted; }
                    else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
                    else if (c is '\r' or '\n') { row.Add(field.ToString()); Emit(); row = new List<string>(); field.Clear(); }
                    else { field.Append(c); state = FieldState.Unquoted; }
                    break;
                case FieldState.Unquoted:
                    if (c == '"') return false;
                    if (c == ',') { row.Add(field.ToString()); field.Clear(); state = FieldState.Start; }
                    else if (c is '\r' or '\n') { row.Add(field.ToString()); Emit(); row = new List<string>(); field.Clear(); state = FieldState.Start; }
                    else field.Append(c);
                    break;
                case FieldState.QuoteClosed:
                    if (c == ',') { row.Add(field.ToString()); field.Clear(); state = FieldState.Start; }
                    else if (c is '\r' or '\n') { row.Add(field.ToString()); Emit(); row = new List<string>(); field.Clear(); state = FieldState.Start; }
                    else return false;
                    break;
            }
            i++;
        }

        if (state == FieldState.Quoted) return false;
        if (field.Length > 0 || row.Count > 0 || state == FieldState.QuoteClosed)
        {
            row.Add(field.ToString());
            Emit();
        }
        return true;
    }
}
