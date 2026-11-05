namespace Dashboard.Web.Components.UI;

/// <summary>Single place mapping a raw status string to a label/tone/icon/spinner
/// so the same status always reads the same way everywhere it appears
/// (Style Refactor P1.3). Tone names match the ui-pill-*/ui-chip-* CSS classes.</summary>
public static class StatusPresentation
{
    public readonly record struct Presentation(string Label, string Tone, string? Icon, bool Spinner);

    private static readonly Dictionary<string, Presentation> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ready"] = new("Ready", "success", "check", false),
        ["active"] = new("Active", "success", "check", false),
        ["running"] = new("Running", "success", "check", false),
        ["healthy"] = new("Healthy", "success", "check", false),
        ["ok"] = new("OK", "success", "check", false),

        ["starting"] = new("Starting", "info", null, true),
        ["loading"] = new("Loading", "info", null, true),
        ["pulling"] = new("Pulling", "info", null, true),
        ["pending"] = new("Pending", "info", null, true),

        ["swapping"] = new("Swapping", "info", null, true),
        ["deploying"] = new("Deploying", "info", null, true),
        ["queued"] = new("Queued", "info", null, true),

        ["stopping"] = new("Stopping", "warn", null, true),

        ["idle"] = new("Idle", "muted", null, false),
        ["stopped"] = new("Stopped", "muted", null, false),
        ["disabled"] = new("Disabled", "muted", null, false),

        ["failed"] = new("Failed", "danger", "x-circle", false),
        ["error"] = new("Error", "danger", "x-circle", false),
        ["exited"] = new("Exited", "danger", "x-circle", false),

        ["unreachable"] = new("Unreachable", "danger", "x-circle", false),
        ["down"] = new("Down", "danger", "x-circle", false),

        ["degraded"] = new("Degraded", "warn", "alert", false),
        ["warning"] = new("Warning", "warn", "alert", false),
        ["mismatch"] = new("Mismatch", "warn", "alert", false),

        ["needs setup"] = new("Needs setup", "warn", "alert", false),
        ["needs_setup"] = new("Needs setup", "warn", "alert", false),

        ["unknown"] = new("Unknown", "muted", null, false),
    };

    /// <summary>Unmapped input falls back to a muted pill showing the raw status
    /// text verbatim, rather than throwing — new/unanticipated statuses should
    /// still render something readable.</summary>
    public static Presentation Resolve(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return Map["unknown"];

        return Map.TryGetValue(status.Trim(), out var presentation)
            ? presentation
            : new Presentation(status, "muted", null, false);
    }
}
