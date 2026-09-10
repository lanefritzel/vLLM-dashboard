using System.Text;
using System.Text.RegularExpressions;

namespace Dashboard.Core.Models;

/// <summary>Turns a source label into a short, URL-safe identifier for the proxy
/// route (e.g. "/proxy/tes-vllm/v1/..." instead of the raw GUID). Pure/no I/O —
/// uniqueness against other sources is the caller's job, since only the caller has
/// the database.</summary>
public static partial class Slug
{
    private const int MaxLength = 60;

    public static string From(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return "source";

        var lowered = label.Trim().ToLowerInvariant();
        var hyphenated = NonAlphanumericRun().Replace(lowered, "-").Trim('-');
        if (hyphenated.Length > MaxLength)
            hyphenated = hyphenated[..MaxLength].Trim('-');

        return hyphenated.Length == 0 ? "source" : hyphenated;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphanumericRun();
}
