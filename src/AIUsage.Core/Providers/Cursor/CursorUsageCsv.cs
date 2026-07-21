using System.Globalization;
using AIUsage.Core.Pricing;

namespace AIUsage.Core.Providers.Cursor;

public sealed record CursorUsageCsvRow(DateTimeOffset Date, string Model, TokenBreakdown Tokens, double? ImputedCostDollars);

public sealed record CursorUsageCsvParseResult(List<CursorUsageCsvRow> Rows, int RejectedRowCount);

public enum CursorUsageCsvErrorKind
{
    MissingColumns,
    MalformedCsv
}

public sealed class CursorUsageCsvError : Exception
{
    public CursorUsageCsvErrorKind Kind { get; }
    public List<string> MissingColumns { get; }

    public CursorUsageCsvError(CursorUsageCsvErrorKind kind, List<string>? missingColumns = null)
    {
        Kind = kind;
        MissingColumns = missingColumns ?? new List<string>();
    }
}

/// <summary>Parses Cursor's CSV usage export into priced rows. Direct port of the Swift CursorUsageCSV.</summary>
public static class CursorUsageCsv
{
    private static class Column
    {
        public const string Date = "Date";
        public const string Model = "Model";
        public const string CacheWrite = "Input (w/ Cache Write)";
        public const string Input = "Input (w/o Cache Write)";
        public const string CacheRead = "Cache Read";
        public const string Output = "Output Tokens";
        public static readonly string[] Required = { Date, Model, CacheWrite, Input, CacheRead, Output };
    }

    public static CursorUsageCsvParseResult Parse(string csv, ModelPricing pricing)
    {
        var rows = new List<CursorUsageCsvRow>();
        var rejectedRowCount = 0;
        var acceptedTokenCount = 0L;
        var missingColumns = Column.Required.ToList();
        var hasDuplicateColumns = false;

        var summary = CursorCsvParser.ForEachRecord(csv,
            header =>
            {
                var available = new HashSet<string>(header);
                missingColumns = Column.Required.Where(c => !available.Contains(c)).ToList();
                hasDuplicateColumns = available.Count != header.Count;
            },
            r =>
            {
                if (!r.TryGetValue(Column.Date, out var dateStr) || string.IsNullOrWhiteSpace(dateStr) ||
                    ParseDate(dateStr.Trim()) is not { } date ||
                    !r.TryGetValue(Column.Model, out var modelRaw) || string.IsNullOrWhiteSpace(modelRaw) ||
                    !r.TryGetValue(Column.CacheWrite, out var cwRaw) || ParseIntValue(cwRaw) is not { } cacheWrite ||
                    !r.TryGetValue(Column.Input, out var inRaw) || ParseIntValue(inRaw) is not { } input ||
                    !r.TryGetValue(Column.CacheRead, out var crRaw) || ParseIntValue(crRaw) is not { } cacheRead ||
                    !r.TryGetValue(Column.Output, out var outRaw) || ParseIntValue(outRaw) is not { } output)
                {
                    rejectedRowCount++;
                    return;
                }
                var model = modelRaw.Trim();
                long rowTokenCount = (long)cacheWrite + input + cacheRead + output;
                acceptedTokenCount += rowTokenCount;

                var tokens = new TokenBreakdown { Input = input, CacheWrite5m = cacheWrite, CacheRead = cacheRead, Output = output };
                rows.Add(new CursorUsageCsvRow(date, model, tokens, pricing.EstimatedCostDollars(model, tokens, applyLongContextRates: false)));
            });

        if (!summary.IsStructurallyComplete || hasDuplicateColumns) throw new CursorUsageCsvError(CursorUsageCsvErrorKind.MalformedCsv);
        if (missingColumns.Count > 0) throw new CursorUsageCsvError(CursorUsageCsvErrorKind.MissingColumns, missingColumns);
        rejectedRowCount += summary.RejectedRecordCount;
        return new CursorUsageCsvParseResult(rows, rejectedRowCount);
    }

    private static DateTimeOffset? ParseDate(string raw)
    {
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)) return d;
        if (DateTime.TryParseExact(raw, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d2))
        {
            return new DateTimeOffset(d2, TimeSpan.Zero);
        }
        return null;
    }

    private static int? ParseIntValue(string? raw)
    {
        if (raw is null) return null;
        var normalized = raw.Trim();
        if (normalized.Length == 0) return 0;

        var groups = normalized.Split(',');
        if (groups.Length > 1)
        {
            var first = groups[0];
            if (first.Length is < 1 or > 3 || !IsAsciiDigits(first)) return null;
            if (!groups.Skip(1).All(g => g.Length == 3 && IsAsciiDigits(g))) return null;
        }
        else if (!IsAsciiDigits(normalized))
        {
            return null;
        }
        return int.TryParse(string.Concat(groups), out var value) ? value : null;
    }

    private static bool IsAsciiDigits(string value) => value.Length > 0 && value.All(char.IsAsciiDigit);
}
