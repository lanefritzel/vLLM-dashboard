using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Dashboard.Core.Abstractions;
using Dashboard.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
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
    private readonly IInFlightTrafficTracker _inFlight;
    private readonly IActivityTracker _activity;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ModelProxyService> _logger;

    // One swap-in-flight lock per source, so two concurrent requests for the same
    // source don't both decide to swap at once and race each other.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SwapLocks = new();

    // Cap on what the live traffic view broadcasts — the real response to the client is
    // never truncated, only what's mirrored to the UI, so a very long generation can't
    // flood the browser or the SignalR connection.
    private const int MaxBroadcastCharsPerRequest = 20_000;

    // A normal single streamed SSE delta is tens to low hundreds of characters. Anything
    // this large signals an abnormal payload (e.g. a reasoning parser resending the full
    // cumulative text instead of an incremental delta) — see StreamWithTeeAsync.
    private const int MaxTeeParsePayloadChars = 32_768;

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
        IInFlightTrafficTracker inFlight,
        IActivityTracker activity,
        IHostApplicationLifetime lifetime,
        ILogger<ModelProxyService> logger)
    {
        _sources = sources;
        _models = models;
        _launchSpecs = launchSpecs;
        _orchestrator = orchestrator;
        _httpFactory = httpFactory;
        _traffic = traffic;
        _inFlight = inFlight;
        _activity = activity;
        _lifetime = lifetime;
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

        // Touched here, at request start, not just on completion below: IdleUnloadService
        // only sees this source as "still busy" via this timestamp, and a long-running
        // streamed generation could otherwise sit for minutes with the source's last-touch
        // still pointing at whenever the *previous* request finished — long enough to look
        // idle and get its container torn down mid-stream by the 30s idle-unload check.
        _activity.Touch(source.Id);

        // Model discovery (e.g. OpenWebUI populating its model picker) must list every
        // model registered for this source, not just whichever one happens to be
        // loaded right now — vLLM's own /v1/models only ever reports the single active
        // model, which would make every other registered model invisible to a client
        // and defeat the point of having a swappable registry at all. Answered directly
        // from the registry instead of forwarded.
        if (HttpMethods.IsGet(context.Request.Method) && IsModelsListPath(path))
        {
            await WriteModelsListAsync(context, source, ct);
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

            if (string.IsNullOrWhiteSpace(source.PhysicalServerId))
                return "This source has no physical server configured — set one up on the Settings page.";

            var target = (await _models.GetByPhysicalServerAsync(source.PhysicalServerId, ct))
                .FirstOrDefault(m => string.Equals(m.Name, requestedModelName, StringComparison.Ordinal));
            if (target is null)
                return $"Model '{requestedModelName}' is not registered on this source's physical server.";

            // The DB's IsActive flag can drift from reality (e.g. a model started
            // directly via docker compose, outside this app, never gets marked active).
            // Ask the backend itself what it's actually serving before tearing down and
            // recreating a container that's already correct — a real swap should never
            // fire just because our own bookkeeping hadn't caught up yet.
            var actuallyLoaded = await _orchestrator.GetActuallyRunningModelNameAsync(source, ct);
            if (actuallyLoaded is not null && string.Equals(actuallyLoaded, requestedModelName, StringComparison.Ordinal))
            {
                await _models.ActivateAsync(source.Id, target.Id, ct);
                return null;
            }

            var spec = await _launchSpecs.GetBySourceAsync(source.Id, ct);
            if (spec is null || string.IsNullOrWhiteSpace(spec.Image))
                return "This source's server settings (image) aren't configured yet — set them up on the Settings page.";

            // A real swap can take several minutes on a slow GPU (weight load + compile) —
            // far longer than a chat client's own request timeout. If this ran on the
            // inbound request's own token, a client that gives up waiting and retries
            // would cancel the swap mid-flight, abandoning a container that's still
            // loading with nothing left to clean it up; the retry would then tear that
            // orphaned container down and start over, forever, never actually finishing
            // (a real incident: LibreChat cycled the same swap 4+ times before one
            // attempt finally outlasted its own retry). Runs to completion independent of
            // whether the triggering request is still connected — only app shutdown
            // should be able to cut it short.
            _logger.LogInformation("Auto-swap: {Source} -> {Model} (requested by an inbound proxy request).", source.Label, target.Name);
            var result = await _orchestrator.SwapModelAsync(source, target, spec, NoOpProgress.Instance, _lifetime.ApplicationStopping);
            if (!result.Success)
                return $"Auto-swap to '{requestedModelName}' failed: {result.Error}";

            await _models.ActivateAsync(source.Id, target.Id, _lifetime.ApplicationStopping);
            return null;
        }
        finally
        {
            gate.Release();
        }
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

    private async Task WriteModelsListAsync(HttpContext context, SourceInfo source, CancellationToken ct)
    {
        var models = string.IsNullOrWhiteSpace(source.PhysicalServerId)
            ? Array.Empty<ModelInfo>()
            : await _models.GetByPhysicalServerAsync(source.PhysicalServerId, ct);
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

        // Live Traffic only shows an in-flight stream live, in someone's open browser
        // tab — nothing about it was ever written anywhere durable. A live incident
        // (a long generation going silent partway through, reproducible only through
        // this proxy and not against vLLM directly) had no server-side trail at all to
        // look at after the fact, forcing live packet capture on production to get any
        // evidence. This heartbeat + oversized-payload warning exist so the same class
        // of incident leaves an actual record in the dashboard's own logs next time.
        var sw = Stopwatch.StartNew();
        var lastHeartbeat = TimeSpan.Zero;
        long totalBytesForwarded = 0;
        var maxPayloadCharsSeen = 0;
        var oversizedPayloadsSkipped = 0;
        _logger.LogInformation("Proxy stream {RequestId} for source {SourceId} starting.", requestId, sourceId);

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
            // A normal single SSE delta is a handful of characters. If a reasoning
            // parser ever sends the FULL cumulative text on every event instead of an
            // incremental delta (a real bug class, and the leading suspect for a live
            // incident where long-running generations went silent specifically when
            // proxied through this app but not against vLLM directly), re-parsing that
            // ever-growing JSON string on every single token turns this mirror-only
            // logic into O(n^2) work over the length of the whole response. The real
            // client already got its bytes above, untouched — skipping the mirror for
            // an abnormally large single line costs nothing but Live Traffic fidelity
            // for that one line, and completely removes the runaway-cost risk.
            if (payload.Length > maxPayloadCharsSeen)
                maxPayloadCharsSeen = payload.Length;

            if (payload.Length > MaxTeeParsePayloadChars)
            {
                oversizedPayloadsSkipped++;
                // Only the first occurrence gets a full snippet — if this is truly a
                // parser resending cumulative text, it'll repeat on every subsequent
                // token for the rest of the response, and logging a growing JSON blob
                // every time would just recreate the exact cost problem this guards
                // against, in the logger instead of the JSON parser.
                if (oversizedPayloadsSkipped == 1)
                {
                    _logger.LogWarning(
                        "Proxy stream {RequestId}: skipped mirroring an abnormally large SSE payload ({Length} chars, cap is {Cap}) — first 300 chars: {Snippet}",
                        requestId, payload.Length, MaxTeeParsePayloadChars, payload[..Math.Min(300, payload.Length)]);
                }
                return;
            }

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

        try
        {
            await using var upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[8192];
            int read;
            while ((read = await upstreamStream.ReadAsync(buffer, ct)) > 0)
            {
                totalBytesForwarded += read;
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), ct);
                await context.Response.Body.FlushAsync(ct);

                var charCount = decoder.GetCharCount(buffer, 0, read, flush: false);
                if (charCount > 0)
                {
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

                if (sw.Elapsed - lastHeartbeat > TimeSpan.FromSeconds(30))
                {
                    lastHeartbeat = sw.Elapsed;
                    _logger.LogInformation(
                        "Proxy stream {RequestId}: {Elapsed:0}s elapsed, {Bytes} bytes forwarded, largest single SSE payload seen {MaxPayload} chars, {Skipped} oversized payload(s) skipped from mirroring.",
                        requestId, sw.Elapsed.TotalSeconds, totalBytesForwarded, maxPayloadCharsSeen, oversizedPayloadsSkipped);
                }
            }

            if (lineBuffer.Length > 0)
                ProcessLine(lineBuffer.ToString());

            // Non-streaming responses (stream:false) are one JSON object, not "data:" lines —
            // fall back to parsing the whole body once nothing SSE-shaped was seen.
            if (!sawSseLine)
                ProcessPayload(rawFallback.ToString());

            _logger.LogInformation(
                "Proxy stream {RequestId} finished: {Elapsed:0}s, {Bytes} bytes forwarded, largest single SSE payload {MaxPayload} chars.",
                requestId, sw.Elapsed.TotalSeconds, totalBytesForwarded, maxPayloadCharsSeen);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation(
                "Proxy stream {RequestId} cancelled after {Elapsed:0}s, {Bytes} bytes forwarded (client disconnected or request cancelled).",
                requestId, sw.Elapsed.TotalSeconds, totalBytesForwarded);
            throw;
        }
        catch (Exception ex)
        {
            // Previously unguarded: an exception anywhere in this loop (a bad decode, a
            // write to an already-closed response, anything) propagated silently out of
            // ForwardAsync with nothing logging what happened or how far the stream had
            // gotten — exactly the blind spot that made a live incident require raw
            // packet capture on production to get any evidence at all.
            _logger.LogError(ex,
                "Proxy stream {RequestId} failed after {Elapsed:0}s, {Bytes} bytes forwarded.",
                requestId, sw.Elapsed.TotalSeconds, totalBytesForwarded);
            throw;
        }
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
        var e = new TrafficRequestStarted
        {
            RequestId = requestId,
            SourceId = sourceId,
            Method = method,
            Path = path,
            Model = model,
            Messages = ExtractMessages(requestBody),
            StartedAt = DateTimeOffset.UtcNow,
        };
        _inFlight.Started(e);
        _ = SendTrafficEventAsync(sourceId, TrafficHub.StartedEvent, e);
    }

    private void BroadcastChunk(string sourceId, string requestId, string text, TrafficChunkKind kind)
    {
        _inFlight.Append(requestId, text, kind);
        _ = SendTrafficEventAsync(sourceId, TrafficHub.ChunkEvent, new TrafficChunk { RequestId = requestId, SourceId = sourceId, Text = text, Kind = kind });
    }

    // A coding-agent client (e.g. OpenCode) resends its ENTIRE conversation history on
    // every single turn, exactly like every stateless chat-completions API — a session
    // with tens of thousands of tokens of context turns this into a message list that's
    // hundreds of KB of JSON. Unlike the streamed response/reasoning text (already
    // capped via MaxBroadcastCharsPerRequest), this had no cap at all: it's sent once
    // per request AND replayed by TrafficHub's catch-up on every single viewer join/
    // reconnect, which is a live incident of its own — a live incident where the Live
    // Traffic page's own Blazor circuit kept dying and reloading turned out to trace
    // back to exactly this, once an in-progress request's history got large enough.
    private const int MaxMessagesCharsPerRequest = 20_000;

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

            var charsSoFar = 0;
            foreach (var m in messages.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object) continue;
                var role = m.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : "";
                var content = ExtractMessageContent(m);
                if (role.Length == 0 && content.Length == 0) continue;

                if (charsSoFar >= MaxMessagesCharsPerRequest)
                {
                    result.Add(new TrafficMessage
                    {
                        Role = "system",
                        Content = "…(earlier messages omitted from this live view — the full conversation was still sent to the model)",
                    });
                    break;
                }
                if (charsSoFar + content.Length > MaxMessagesCharsPerRequest)
                    content = content[..(MaxMessagesCharsPerRequest - charsSoFar)] + "…";

                charsSoFar += content.Length;
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

    private void BroadcastFinished(string sourceId, string requestId, int statusCode, long elapsedMs, string? error)
    {
        _inFlight.Finished(requestId);
        _ = SendTrafficEventAsync(sourceId, TrafficHub.FinishedEvent, new TrafficFinished
        {
            RequestId = requestId,
            SourceId = sourceId,
            StatusCode = statusCode,
            ElapsedMs = elapsedMs,
            Error = error,
        });
    }

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
}
