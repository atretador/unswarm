using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using LogLevel = Unswarm.Core.Models.LogLevel;

namespace Unswarm.Api.Services;

/// <summary>
/// Handles inference requests for router profiles with auto-fallback and retry.
/// Tries models in priority order; on server error (5xx), retries the same model
/// up to <see cref="Settings.RouterRetryAttempts"/> times before falling back.
/// Client errors (4xx) and exceptions fall back immediately to the next model.
/// Manual mode tries only the first entry.
/// </summary>
public sealed class RouterProfileHandler
{
    private readonly IRouterProfileService _routerProfile;
    private readonly ICloudForwardingService _cloudForwarding;
    private readonly ISchedulerQueue _scheduler;
    private readonly ILogStore _logStore;
    private readonly IClock _clock;
    private readonly ISettingsStore _settings;

    public RouterProfileHandler(
        IRouterProfileService routerProfile,
        ICloudForwardingService cloudForwarding,
        ISchedulerQueue scheduler,
        ILogStore logStore,
        IClock clock,
        ISettingsStore settings)
    {
        _routerProfile = routerProfile;
        _cloudForwarding = cloudForwarding;
        _scheduler = scheduler;
        _logStore = logStore;
        _clock = clock;
        _settings = settings;
    }

    /// <summary>
    /// Result of a router profile inference attempt.
    /// </summary>
    public sealed class RouterResult
    {
        /// <summary>The model that ultimately handled the request (null if all failed).</summary>
        public string? ServedModel { get; init; }
        /// <summary>HTTP status code to return.</summary>
        public int StatusCode { get; init; }
        /// <summary>Content type for the response.</summary>
        public string ContentType { get; init; } = "application/json";
        /// <summary>Response body stream (null for empty responses).</summary>
        public Stream? Body { get; init; }
        /// <summary>Whether the response is a streaming (SSE) response.</summary>
        public bool IsStreaming { get; init; }
        /// <summary>Token counts for usage recording.</summary>
        public int TokensGenerated { get; init; }
        public int PromptTokens { get; init; }
        public int PromptTokensCached { get; init; }
        /// <summary>Runtime name for usage attribution.</summary>
        public string? ServedByRuntimeName { get; init; }
        /// <summary>If all models failed, the last error message.</summary>
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Attempt inference through a router profile with fallback and retry on 5xx errors.
    /// </summary>
    /// <param name="profileName">The router profile name (without "router/" prefix).</param>
    /// <param name="rawBody">The original JSON request body.</param>
    /// <param name="requestPath">The request path (e.g. "/v1/chat/completions").</param>
    /// <param name="isStreaming">Whether the client requested streaming.</param>
    /// <param name="conversationKey">Conversation affinity key.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<RouterResult> HandleAsync(
        string profileName,
        string rawBody,
        string requestPath,
        bool isStreaming,
        string? conversationKey,
        CancellationToken ct)
    {
        var resolved = await _routerProfile.ResolveAsync(profileName, ct);
        if (resolved is null || resolved.Value.Entries.Count == 0)
        {
            return new RouterResult
            {
                StatusCode = 404,
                ErrorMessage = $"Router profile '{profileName}' not found or has no enabled entries."
            };
        }

        var settings = await _settings.GetAsync(ct);
        var retryAttempts = settings.RouterRetryAttempts;
        var retryDelayMs = settings.RouterRetryDelayMs;

        var entries = resolved.Value.Entries;
        var mode = resolved.Value.Mode;
        var maxAttempts = mode == RouterProfileMode.Manual ? 1 : entries.Count;

        for (var i = 0; i < maxAttempts; i++)
        {
            var entry = entries[i];
            var modelId = entry.ModelId;
            var lastWasRetryable = false;

            for (var attempt = 0; attempt <= retryAttempts; attempt++)
            {
                if (attempt > 0)
                {
                    _logStore.Enqueue(LogLevel.Info, "router",
                        $"Router retry {attempt}/{retryAttempts}: profile={profileName}, model={modelId}, delay={retryDelayMs}ms");
                    await Task.Delay(retryDelayMs, ct);
                }

                try
                {
                    _logStore.Enqueue(LogLevel.Info, "router",
                        $"Router attempt {attempt + 1}/{retryAttempts + 1} (entry {i + 1}/{maxAttempts}): profile={profileName}, model={modelId}");

                    RouterResult? result = null;
                    bool isRetryable;

                    if (modelId.StartsWith("cloud/", StringComparison.Ordinal))
                    {
                        (result, isRetryable) = await TryCloudModelAsync(modelId, rawBody, requestPath, isStreaming, ct);
                    }
                    else
                    {
                        (result, isRetryable) = await TryLocalModelAsync(modelId, rawBody, isStreaming, conversationKey, ct);
                    }

                    if (result is not null)
                        return result;

                    lastWasRetryable = isRetryable;

                    // Non-retryable error (4xx) → break inner loop, fall to next entry
                    if (!isRetryable)
                        break;

                    // Retryable (5xx) → continue inner loop for retry
                }
                catch (OperationCanceledException)
                {
                    throw; // Don't catch cancellations
                }
                catch (Exception ex)
                {
                    _logStore.Enqueue(LogLevel.Warn, "router",
                        $"Router fallback: model={modelId} failed: {ex.Message}");

                    if (mode == RouterProfileMode.Manual || i == maxAttempts - 1)
                    {
                        return new RouterResult
                        {
                            StatusCode = 502,
                            ErrorMessage = $"All router models failed. Last error: {ex.Message}"
                        };
                    }
                    break; // Fall through to next entry
                }
            }

            // If we exhausted retries on a retryable error and it's the last entry in Manual mode
            if (lastWasRetryable && (mode == RouterProfileMode.Manual || i == maxAttempts - 1))
            {
                return new RouterResult
                {
                    StatusCode = 502,
                    ErrorMessage = $"Model {entries[i].ModelId} returned server error after {retryAttempts + 1} attempts for profile '{profileName}'."
                };
            }
        }

        return new RouterResult
        {
            StatusCode = 502,
            ErrorMessage = $"All {maxAttempts} router models failed for profile '{profileName}'."
        };
    }

    private async Task<(RouterResult? Result, bool IsRetryable)> TryCloudModelAsync(
        string modelId, string rawBody, string requestPath, bool isStreaming, CancellationToken ct)
    {
        var response = await _cloudForwarding.ForwardAsync(modelId, rawBody, requestPath, isStreaming, ct);

        if (response.StatusCode >= 400)
        {
            _logStore.Enqueue(LogLevel.Warn, "router",
                $"Cloud model {modelId} returned {response.StatusCode}");

            // Consume error body to free the stream
            if (response.Body is not null)
                await response.Body.DisposeAsync();

            var isRetryable = response.StatusCode >= 500;
            return (null, isRetryable);
        }

        return (new RouterResult
        {
            ServedModel = modelId,
            StatusCode = response.StatusCode,
            ContentType = response.ContentType,
            Body = response.Body,
            IsStreaming = isStreaming,
        }, false);
    }

    private async Task<(RouterResult? Result, bool IsRetryable)> TryLocalModelAsync(
        string modelId, string rawBody, bool isStreaming, string? conversationKey, CancellationToken ct)
    {
        var request = new InferenceRequest
        {
            Id = Guid.NewGuid().ToString("N"),
            ModelName = modelId,
            OriginalJson = rawBody,
            IsStreaming = isStreaming,
            Priority = 0,
            EnqueuedAt = _clock.UtcNow,
            Tcs = new TaskCompletionSource<InferenceResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously),
            CancellationToken = ct,
            ConversationKey = conversationKey
        };

        var response = await _scheduler.EnqueueAsync(request, ct);

        if (response.StatusCode >= 400)
        {
            _logStore.Enqueue(LogLevel.Warn, "router",
                $"Local model {modelId} returned {response.StatusCode}");

            // Consume error body to free the stream
            if (response.Body is not null)
                await response.Body.DisposeAsync();

            var isRetryable = response.StatusCode >= 500;
            return (null, isRetryable);
        }

        return (new RouterResult
        {
            ServedModel = modelId,
            StatusCode = response.StatusCode,
            ContentType = response.ContentType,
            Body = response.Body,
            IsStreaming = isStreaming,
            TokensGenerated = response.TokensGenerated,
            PromptTokens = response.PromptTokens,
            PromptTokensCached = response.PromptTokensCached,
            ServedByRuntimeName = response.ServedByRuntimeName,
        }, false);
    }
}
