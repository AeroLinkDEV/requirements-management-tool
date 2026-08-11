namespace AeroLink.Domain.Requirements;

/// <summary>Controlled Problem Report identifier semantics shared by allocation, persistence, and ordering.</summary>
public static class ProblemReportNumber
{
    /// <summary>
    /// Returns the numeric suffix used by the historical queue order. Retained identifiers that do not end
    /// in a number keep the legacy fallback of sequence 1 so migrations do not invent a new ordering rule.
    /// </summary>
    public static int Sequence(string number)
    {
        var separator = number.LastIndexOf('-');
        return int.TryParse(number[(separator + 1)..], out var value) ? value : 1;
    }
}
