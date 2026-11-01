using System.Text.Json;

namespace Dashboard.Infrastructure.Persistence.V2;

/// <summary>Pulls simple scalar fields out of a push event's loosely-typed
/// <c>Dictionary&lt;string, object?&gt;</c> detail/report payload (P10b) --
/// System.Text.Json deserializes unknown-shaped values as boxed
/// <see cref="JsonElement"/>, so this is just the unwrapping in one place.</summary>
internal static class PushDetailHelpers
{
    public static string? GetString(Dictionary<string, object?>? detail, string key)
    {
        if (detail is null || !detail.TryGetValue(key, out var value) || value is null)
            return null;
        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
            JsonElement je => je.ToString(),
            _ => value.ToString(),
        };
    }

    public static int? GetInt(Dictionary<string, object?>? detail, string key)
    {
        if (detail is null || !detail.TryGetValue(key, out var value) || value is null)
            return null;
        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.Number } je when je.TryGetInt32(out var i) => i,
            JsonElement => null,
            IConvertible => Convert.ToInt32(value),
            _ => null,
        };
    }
}
