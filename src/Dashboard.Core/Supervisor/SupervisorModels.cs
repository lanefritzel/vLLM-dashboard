namespace Dashboard.Core.Supervisor;

/// <summary>Plain DTOs mirroring the supervisor's admin API JSON exactly (doc 5) --
/// deserialized with <c>JsonNamingPolicy.SnakeCaseLower</c>, so these property
/// names are PascalCase while the wire shape stays snake_case.</summary>
public sealed record SupervisorChildInfo
{
    public IReadOnlyList<string> Argv { get; init; } = Array.Empty<string>();
    public int Pid { get; init; }
    public double StartedAt { get; init; }
    public double Uptime { get; init; }
}

/// <summary>Which inference engine the supervisor wraps (P2). Version is null until
/// the supervisor can report one.</summary>
public sealed record SupervisorEngineInfo
{
    public required string Name { get; init; }
    public string? Version { get; init; }
}

public sealed record SupervisorStatus
{
    public required string State { get; init; }
    public SupervisorChildInfo? Child { get; init; }
    public bool VllmHealthy { get; init; }
    public int? ExitCode { get; init; }
    public string? CurrentDeploymentId { get; init; }
    public Dictionary<string, object?>? FailureReport { get; init; }
    public bool? SmokeOk { get; init; }

    /// <summary>Null when talking to a supervisor older than P2.</summary>
    public SupervisorEngineInfo? Engine { get; init; }
}

public sealed record SupervisorDeploymentEvent
{
    public required string DeploymentId { get; init; }
    public int Seq { get; init; }
    public double Ts { get; init; }
    public required string State { get; init; }
    public Dictionary<string, object?>? Detail { get; init; }
}

public sealed record SupervisorVramInfo
{
    public bool Released { get; init; }
    public long? UsedMb { get; init; }
    public long? TotalMb { get; init; }
}

public sealed record SupervisorStopResult
{
    public required string State { get; init; }
    public required SupervisorVramInfo Vram { get; init; }
}

public sealed record SupervisorCancelResult
{
    public required string DeploymentId { get; init; }
    public required string State { get; init; }
}

public sealed record SupervisorGpuInfo
{
    public required string Vendor { get; init; }
    public int Count { get; init; }
    public long? TotalMb { get; init; }
    public long? UsedMb { get; init; }
}

public sealed record SupervisorSmokeResult
{
    public bool Ok { get; init; }
    public string Excerpt { get; init; } = "";
    public double Ms { get; init; }
}

public sealed record SupervisorRepoFile
{
    public required string Path { get; init; }
    public long Size { get; init; }
}

/// <summary>P13's "Add model" flow, step 1: <c>POST /admin/downloads/inspect</c> --
/// the file listing + parsed config.json + the server's own free disk space,
/// BEFORE anything is downloaded (doc 6: "Required X, Available Y" disk check).</summary>
public sealed record SupervisorDownloadInspectResult
{
    public IReadOnlyList<SupervisorRepoFile> Files { get; init; } = Array.Empty<SupervisorRepoFile>();
    public Dictionary<string, object?>? Config { get; init; }
    public long FreeBytes { get; init; }
}

/// <summary>Polled via <c>GET /admin/downloads/{id}</c> for the progress bar
/// (doc 6: "40.2% · 105 MB/s · 4m 48s", "file 5 of 14"). "download_failed",
/// not "failed" -- a failed download is not a failed swap.</summary>
public sealed record SupervisorDownloadStatus
{
    public required string Status { get; init; } // "downloading" | "done" | "download_failed" | "cancelled"
    public int FilesTotal { get; init; }
    public int FilesDone { get; init; }
    public long TotalBytes { get; init; }
    public long DownloadedBytes { get; init; }
    public double ProgressPct { get; init; }
    public double SpeedBps { get; init; }
    public double? EtaSeconds { get; init; }
    public string? Error { get; init; }
    public string? Path { get; init; }
    public long? SizeBytes { get; init; }
    public Dictionary<string, object?>? Config { get; init; }
    public string? LoadFormat { get; init; }
}

/// <summary>Thrown by <see cref="Dashboard.Core.Abstractions.ISupervisorClient"/>
/// for any non-2xx response the caller needs to react to (409 "already in
/// flight"/"not serving", 401, 404, etc.) -- callers branch on
/// <see cref="StatusCode"/> rather than parsing a message string.</summary>
public sealed class SupervisorApiException : Exception
{
    public int StatusCode { get; }
    public string? Body { get; }

    public SupervisorApiException(int statusCode, string? body, string message) : base(message)
    {
        StatusCode = statusCode;
        Body = body;
    }
}
