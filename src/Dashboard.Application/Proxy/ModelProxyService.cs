using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Dashboard.Core.Abstractions;
using Dashboard.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Dashboard.Application.Proxy;

/// <summary>
/// Transparent reverse proxy for a source's inference endpoint: a client hits a
/// stable dashboard URL instead of vLLM's own address, and this inspects the
/// request's <c>"model"</c> field, auto-swaps the backend container first if a
/// different model is requested, then forwards (streaming) to the real endpoint.
/// This is the "llama-swap"-style piece — hot-swap driven by an incoming request,
/// not a human clicking a button first.
/// </summary>
public interface IModelProxyService
{
    Task HandleAsync(HttpContext context, string sourceId, string path);
}

public sealed class ModelProxyService : IModelProxyService
{
    private readonly ISourceCatalog _sources;
    private readonly IModelCatalog _models;
    private readonly ILaunchSpecStore _launchSpecs;
    private readonly IDockerOrchestrator _orchestrator;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IHubContext<TrafficHub> _traffic;
    private readonly IActivityTracker _activity;
    private readonly ILogger<ModelProxyService> _logger;

    // One swap-in-flight lock per source, so two concurrent requests for the same
    // source don't both decide to swap at once and race each other.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SwapLocks = new();

    // Cap on what the live traffic view broadcasts — the real response to the client is
    // never truncated, only what's mirrored to the UI, so a very long generation can't
    // flood the browser or the SignalR connection.
    private const int MaxBroadcastCharsPerRequest = 20_000;

    // Headers that must never be copied verbatim between the inbound request/outbound
    // response — either because HttpClient sets them itself from the content, or
    // because they describe THIS hop's transport, not the proxied one.
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Transfer-Encoding", "Content-Length", "Keep-Alive", "Upgrade",
    };

    public ModelProxyService(
        ISourceCatalog sources,
        IModelCatalog models,
        ILaunchSpecStore launchSpecs,
        IDockerOrchestrator orchestrator,
        IHttpClientFactory httpFactory,
        IHubContext<TrafficHub> traffic,
        IActivityTracker activity,
        ILogger<ModelProxyService> logger)
    {
        _sources = sources;
        _models = models;
        _launchSpecs = launchSpecs;
        _orchestrator = orchestrator;
        _httpFactory = httpFactory;
        _traffic = traffic;
        _activity = activity;
        _logger = logger;
    }

    public async Task HandleAsync(HttpContext context, string sourceId, string path)
    {
        var ct = context.RequestAborted;

        var source = await _sources.GetByIdOrSlugAsync(sourceId, ct);
        if (source is null)
        {
            await WriteErrorAsync(context, StatusCodes.Status404NotFound, "Unknown source.");
            return;
        }
        if (string.IsNullOrWhiteSpace(source.InferenceUrl))
        {
            await WriteErrorAsync(context, StatusCodes.Status502BadGateway, "Source has no inference URL configured.");
            return;
        }

        // Model discovery (e.g. OpenWebUI populating its model picker) must list every
        // model registered for this source, not just whichever one happens to be
        // loaded right now — vLLM's own /v1/models only ever reports the single active
        // model, which would make every other registered model invisible to a client
        // and defeat the point of having a swappable registry at all. Answered directly
        // from the registry instead of forwarded.
        if (HttpMethods.IsGet(context.Request.Method) && IsModelsListPath(path))
        {
            await WriteModelsListAsync(context, source.Id, ct);
            return;
        }

        var requestBody = await BufferRequestBodyAsync(context, ct);
        var requestedModelName = TryExtractModelName(requestBody);

        if (!string.IsNullOrWhiteSpace(requestedModelName))
        {
            var swapError = await EnsureModelActiveAsync(source, requestedModelName, ct);
            if (swapError is not null)
            {
                await WriteErrorAsync(context, StatusCodes.Status502BadGateway, swapError);
                return;
            }
        }

        await ForwardAsync(context, source, path, requestBody, requestedModelName, ct);
    }

    /// <summary>Returns null on success, or a human-readable error to send back to the
    /// client. Swaps only if the requested model isn't already the active one.</summary>
    private async Task<string?> EnsureModelActiveAsync(SourceInfo source, string requestedModelName, CancellationToken ct)
    {
        var active = await _models.GetActiveAsync(source.Id, ct);
        if (active is not null && string.Equals(active.Name, requestedModelName, StringComparison.Ordinal))
            return null; // already serving the right model — nothing to do

        var gate = SwapLocks.GetOrAdd(source.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Re-check after acquiring the lock: another concurrent request may have
            // already completed the swap while this one was waiting.
            active = await _models.GetActiveAsync(source.Id, ct);
            if (active is not null && string.Equals(active.Name, requestedModelName, StringComparison.Ordinal))
                return null;

            var target = (await _models.GetBySourceAsync(source.Id, ct))
                .FirstOrDefault(m => string.Equals(m.Name, requestedModelName, StringComparison.Ordinal));
            if (target is null)
                return $"Model '{requestedModelName}' is not registered for this source.";

            // The DB's IsActive flag can drift from reality (e.g. a model started
            // directly via docker compose, outside this app, never gets marked active).
            // Ask the backend itself what it's actually serving before tearing down and
            // recreating a container that's already correct — a real swap should never
            // fire just because our own bookkeeping hadn't caught up yet.
            var actuallyLoaded = await GetActuallyLoadedModelNameAsync(source, ct);
            if (actuallyLoaded is not null && string.Equals(actuallyLoaded, requestedModelName, StringComparison.Ordinal))
            {
                await _models.ActivateAsync(target.Id, ct);
                return null;
            }

            var spec = await _launchSpecs.GetBySourceAsync(source.Id, ct);
            if (spec is null || string.IsNullOrWhiteSpace(spec.Image))
                return "This source's server settings (image) aren't configured yet — set them up on the Settings page.";

            _logger.LogInformation("Auto-swap: {Source} -> {Model} (requested by an inbound proxy request).", source.Label, target.Name);
            var result = await _orchestrator.SwapModelAsync(source, target, spec, NoOpProgress.Instance, ct);
            if (!result.Success)
                return $"Auto-swap to '{requestedModelName}' failed: {result.Error}";

            await _models.ActivateAsync(target.Id, ct);
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Asks the backend's own (real, not synthesized) <c>/v1/models</c> what it's
    /// actually serving right now. Returns null on any failure (unreachable, malformed,
    /// no models reported) — callers treat that as "unknown," not "definitely nothing."</summary>
    private async Task<string?> GetActuallyLoadedModelNameAsync(SourceInfo source, CancellationToken ct)
    {
        try
        {
            var client = _httpFactory.CreateClient("inference");
            var url = source.InferenceUrl.TrimEnd('/') + "/v1/models";
            using var resp = await client.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Array &&
                data.GetArrayLength() > 0 &&
                data[0].TryGetProperty("id", out var idEl) &&
                idEl.ValueKind == JsonValueKind.String)
            {
                return idEl.GetString();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not query the actual loaded model for {Source}.", source.Label);
        }
        return null;
    }

    private static async Task<string> BufferRequestBodyAsync(HttpContext context, CancellationToken ct)
    {
        if (context.Request.ContentLength is null or 0)
            return "";

        context.Request.EnableBuffering();
        using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
        var body = await reader.ReadToEndAsync(ct);
        context.Request.Body.Position = 0; // rewind so the real forward reads it again
        return body;
    }

    /// <summary>Matches the OpenAI model-listing endpoint (<c>/v1/models</c>), ignoring
    /// a leading/trailing slash. Pure/no I/O.</summary>
    internal static bool IsModelsListPath(string path) =>
        string.Equals(path.Trim('/'), "v1/models", StringComparison.OrdinalIgnoreCase);

    private async Task WriteModelsListAsync(HttpContext context, string sourceId, CancellationToken ct)
    {
        var models = await _models.GetBySourceAsync(sourceId, ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, new
        {
            @object = "list",
            data = models.Select(m => new
            {
                id = m.Name,
                @object = "model",
                created = now,
                owned_by = "vllm-dashboard",
            }),
        }, cancellationToken: ct);
    }

    /// <summary>Extracts the OpenAI-style <c>"model"</c> field from a request body.
    /// Pure/no I/O — returns null for anything that isn't a JSON object with a string
    /// "model" property, rather than throwing, since not every proxied request has one
    /// (e.g. GET /v1/models).</summary>
    internal static string? TryExtractModelName(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("model", out var m) &&
                m.ValueKind == JsonValueKind.String)
            {
                var value = m.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
        catch (JsonException)
        {
            // Not JSON, or malformed — treat as "no model specified", not an error.
        }
        return null;
    }

    private async Task ForwardAsync(HttpContext context, SourceInfo source, string path, string bufferedBody, string? requestedModelName, CancellationToken ct)
    {
        var targetUrl = source.InferenceUrl.TrimEnd('/') + "/" + path.TrimStart('/') + context.Request.QueryString;
        var requestId = Guid.NewGuid().ToString("n");
        var sw = Stopwatch.StartNew();
        BroadcastStarted(source.Id, requestId, context.Request.Method, path, requestedModelName, bufferedBody);

        using var upstream = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUrl);
        if (context.Request.ContentLength is > 0)
            upstream.Content = new StringContent(bufferedBody);

        foreach (var header in context.Request.Headers)
        {
            if (HopByHopHeaders.Contains(header.Key)) continue;
            if (upstream.Content is not null && header.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
            {
                // StringContent sets its own default Content-Type ("text/plain;
                // charset=utf-8"); without removing it first, TryAddWithoutValidation
                // appends the client's real value alongside it instead of replacing it,
                // producing a multi-value header (e.g. "text/plain; charset=utf-8,
                // application/json") that vLLM's strict media-type check rejects.
                upstream.Content.Headers.Remove(header.Key);
                upstream.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
            else
                upstream.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }

        // Streaming chat completions (SSE) need headers read as soon as they arrive,
        // then the body copied through as it comes in — buffering the whole response
        // first would break streaming entirely. No client-side timeout: a proxied
        // request has no natural fixed duration, and this app already sits behind
        // HttpContext.RequestAborted for cancellation if the actual client disconnects.
        var client = _httpFactory.CreateClient("proxy");
        HttpResponseMessage upstreamResponse;
        try
        {
            upstreamResponse = await client.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Proxy forward to {Url} failed.", targetUrl);
            await WriteErrorAsync(context, StatusCodes.Status502BadGateway, $"Could not reach the backend: {ex.Message}");
            BroadcastFinished(source.Id, requestId, StatusCodes.Status502BadGateway, sw.ElapsedMilliseconds, ex.Message);
            return;
        }

        using (upstreamResponse)
        {
            context.Response.StatusCode = (int)upstreamResponse.StatusCode;
            foreach (var header in upstreamResponse.Headers.Concat(upstreamResponse.Content.Headers))
            {
                if (HopByHopHeaders.Contains(header.Key)) continue;
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }
            // Prevents an intermediary reverse proxy (e.g. nginx in front of this app)
            // from buffering a streaming SSE response into one chunk.
            context.Response.Headers["X-Accel-Buffering"] = "no";

            await StreamWithTeeAsync(context, upstreamResponse, source.Id, requestId, ct);
        }
        _activity.Touch(source.Id);
        BroadcastFinished(source.Id, requestId, context.Response.StatusCode, sw.ElapsedMilliseconds, null);
    }

    /// <summary>Copies the upstream response body to the real client exactly as
    /// before (byte-for-byte, untouched), and separately mirrors just the assistant's
    /// text — not the surrounding JSON/SSE envelope — to the live traffic view. The
    /// mirror is capped and best-effort — it never affects what the real client
    /// receives, and never adds retries/blocking beyond the client's own write.</summary>
    private async Task StreamWithTeeAsync(HttpContext context, HttpResponseMessage upstreamResponse, string sourceId, string requestId, CancellationToken ct)
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var lineBuffer = new StringBuilder();
        var rawFallback = new StringBuilder();
        var sawSseLine = false;
        var broadcastCharsSent = 0;
        var truncationNoted = false;

        void EmitText(string text, TrafficChunkKind kind)
        {
            if (text.Length == 0 || broadcastCharsSent >= MaxBroadcastCharsPerRequest)
            {
                if (broadcastCharsSent >= MaxBroadcastCharsPerRequest && !truncationNoted)
                {
                    truncationNoted = true;
                    BroadcastChunk(sourceId, requestId, "\n…(live view truncated here; the full response was still delivered)", TrafficChunkKind.Content);
                }
                return;
            }
            broadcastCharsSent += text.Length;
            BroadcastChunk(sourceId, requestId, text, kind);
        }

        void ProcessPayload(string payload)
        {
            // Reasoning arrives first in practice (the model "thinks" before it
            // answers), so emit it ahead of any content this same line also carries.
            var reasoning = TryExtractReasoningText(payload);
            if (!string.IsNullOrEmpty(reasoning))
                EmitText(reasoning, TrafficChunkKind.Reasoning);

            var content = TryExtractDeltaText(payload);
            if (!string.IsNullOrEmpty(content))
                EmitText(content, TrafficChunkKind.Content);
        }

        void ProcessLine(string line)
        {
            line = line.Trim();
            if (line.Length == 0 || !line.StartsWith("data:", StringComparison.Ordinal))
                return;
            sawSseLine = true;
            var payload = line["data:".Length..].Trim();
            if (payload.Length == 0 || payload == "[DONE]")
                return;
            ProcessPayload(payload);
        }

        await using var upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[8192];
        int read;
        while ((read = await upstreamStream.ReadAsync(buffer, ct)) > 0)
        {
            await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), ct);
            await context.Response.Body.FlushAsync(ct);

            var charCount = decoder.GetCharCount(buffer, 0, read, flush: false);
            if (charCount == 0) continue;
            var chars = new char[charCount];
            decoder.GetChars(buffer, 0, read, chars, 0, flush: false);

            lineBuffer.Append(chars);
            if (rawFallback.Length < MaxBroadcastCharsPerRequest)
                rawFallback.Append(chars);

            int newlineIndex;
            while ((newlineIndex = IndexOfNewline(lineBuffer)) >= 0)
            {
                var line = lineBuffer.ToString(0, newlineIndex);
                lineBuffer.Remove(0, newlineIndex + 1);
                ProcessLine(line);
            }
        }

        if (lineBuffer.Length > 0)
            ProcessLine(lineBuffer.ToString());

        // Non-streaming responses (stream:false) are one JSON object, not "data:" lines —
        // fall back to parsing the whole body once nothing SSE-shaped was seen.
        if (!sawSseLine)
            ProcessPayload(rawFallback.ToString());
    }

    private static int IndexOfNewline(StringBuilder sb)
    {
        for (var i = 0; i < sb.Length; i++)
            if (sb[i] == '\n') return i;
        return -1;
    }

    /// <summary>Pulls just the human-readable text out of an OpenAI-style chat/completions
    /// JSON payload — a streaming delta's <c>content</c>, a non-streaming message's
    /// <c>content</c>, or a legacy completions <c>text</c> field, in that order. Pure/no
    /// I/O — returns null for anything else (tool calls, malformed JSON, reasoning-only
    /// deltas), since the live traffic view only wants the actual answer, not the
    /// envelope around it.</summary>
    internal static string? TryExtractDeltaText(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                return null;

            var choice = choices[0];
            if (choice.TryGetProperty("delta", out var delta) &&
                delta.TryGetProperty("content", out var deltaContent) &&
                deltaContent.ValueKind == JsonValueKind.String)
                return deltaContent.GetString();

            if (choice.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var messageContent) &&
                messageContent.ValueKind == JsonValueKind.String)
                return messageContent.GetString();

            if (choice.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                return textEl.GetString();
        }
        catch (JsonException)
        {
            // Not JSON, or malformed — nothing to show for this line/body.
        }
        return null;
    }

    /// <summary>Pulls a reasoning model's "thinking" text out of the same payload
    /// shapes as <see cref="TryExtractDeltaText"/> — vLLM's reasoning parsers emit it
    /// under <c>reasoning</c> or <c>reasoning_content</c> (naming varies by parser),
    /// on either a streaming delta or a non-streaming message. Pure/no I/O.</summary>
    internal static string? TryExtractReasoningText(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                return null;

            var choice = choices[0];
            if (choice.TryGetProperty("delta", out var delta) && TryGetReasoningField(delta, out var deltaReasoning))
                return deltaReasoning;
            if (choice.TryGetProperty("message", out var message) && TryGetReasoningField(message, out var messageReasoning))
                return messageReasoning;
        }
        catch (JsonException)
        {
            // Not JSON, or malformed — nothing to show for this line/body.
        }
        return null;
    }

    private static bool TryGetReasoningField(JsonElement obj, out string? text)
    {
        if (obj.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
        {
            text = rc.GetString();
            return true;
        }
        if (obj.TryGetProperty("reasoning", out var r) && r.ValueKind == JsonValueKind.String)
        {
            text = r.GetString();
            return true;
        }
        text = null;
        return false;
    }

    // Traffic broadcasts are fire-and-forget from the proxy's perspective: they must
    // never block or fail the actual request/response being forwarded, and they use
    // CancellationToken.None (not the client's RequestAborted) so a "Finished" event
    // still reaches the live view even when the real client has already disconnected.
    private void BroadcastStarted(string sourceId, string requestId, string method, string path, string? model, string requestBody)
    {
        _ = SendTrafficEventAsync(sourceId, TrafficHub.StartedEvent, new TrafficRequestStarted
        {
            RequestId = requestId,
            SourceId = sourceId,
            Method = method,
            Path = path,
            Model = model,
            Messages = ExtractMessages(requestBody),
            StartedAt = DateTimeOffset.UtcNow,
        });
    }

    private void BroadcastChunk(string sourceId, string requestId, string text, TrafficChunkKind kind) =>
        _ = SendTrafficEventAsync(sourceId, TrafficHub.ChunkEvent, new TrafficChunk { RequestId = requestId, SourceId = sourceId, Text = text, Kind = kind });

    /// <summary>Pulls the OpenAI-style <c>messages</c> array out of a chat/completions
    /// request body — the system prompt, prior turns, and the new user message exactly
    /// as sent — so the live traffic view can show what the model actually saw, not just
    /// its reply. Pure/no I/O; returns an empty list for anything that isn't a chat
    /// request with a "messages" array (e.g. the legacy /v1/completions "prompt" shape).</summary>
    internal static IReadOnlyList<TrafficMessage> ExtractMessages(string? json)
    {
        var result = new List<TrafficMessage>();
        if (string.IsNullOrWhiteSpace(json))
            return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("messages", out var messages) ||
                messages.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var m in messages.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object) continue;
                var role = m.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : "";
                var content = ExtractMessageContent(m);
                if (role.Length == 0 && content.Length == 0) continue;
                result.Add(new TrafficMessage { Role = role, Content = content });
            }
        }
        catch (JsonException)
        {
            // Not JSON, or malformed — no messages to show for this request.
        }
        return result;
    }

    /// <summary>A chat message's <c>content</c> is either a plain string or an array of
    /// content parts (multimodal); this handles both, joining text parts and labeling
    /// non-text ones (e.g. an image) rather than dropping them silently.</summary>
    private static string ExtractMessageContent(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content))
            return "";
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? "";
        if (content.ValueKind != JsonValueKind.Array)
            return "";

        var parts = new List<string>();
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object) continue;
            if (part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                parts.Add(t.GetString() ?? "");
            else if (part.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String)
                parts.Add($"[{ty.GetString()}]");
        }
        return string.Join("\n", parts);
    }

    private void BroadcastFinished(string sourceId, string requestId, int statusCode, long elapsedMs, string? error) =>
        _ = SendTrafficEventAsync(sourceId, TrafficHub.FinishedEvent, new TrafficFinished
        {
            RequestId = requestId,
            SourceId = sourceId,
            StatusCode = statusCode,
            ElapsedMs = elapsedMs,
            Error = error,
        });

    private async Task SendTrafficEventAsync(string sourceId, string eventName, object payload)
    {
        try
        {
            await _traffic.Clients.Group(TrafficHub.GroupFor(sourceId)).SendAsync(eventName, payload, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Live traffic broadcast ({Event}) failed.", eventName);
        }
    }

    private static async Task WriteErrorAsync(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/plain";
        await context.Response.WriteAsync(message);
    }

    private sealed class NoOpProgress : IAsyncActionProgress
    {
        public static readonly NoOpProgress Instance = new();
        public Task ReportAsync(SwapStep step) => Task.CompletedTask;
    }
}
