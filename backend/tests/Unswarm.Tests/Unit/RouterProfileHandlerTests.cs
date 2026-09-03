using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Api.Services;
using Unswarm.Tests.Fakes;
using LogLevel = Unswarm.Core.Models.LogLevel;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Tests for RouterProfileHandler — the fallback orchestrator for router profile inference.
/// </summary>
public sealed class RouterProfileHandlerTests
{
    private sealed class FakeRouterProfileService : IRouterProfileService
    {
        private readonly List<RouterProfile> _profiles = [];

        public void AddProfile(RouterProfile profile) => _profiles.Add(profile);

        public Task<IReadOnlyList<RouterProfileEntry>?> ResolveEntriesAsync(string profileName, CancellationToken ct = default)
        {
            var profile = _profiles.FirstOrDefault(p =>
                string.Equals(p.Name, profileName, StringComparison.Ordinal));
            if (profile is null)
                return Task.FromResult<IReadOnlyList<RouterProfileEntry>?>(null);

            return Task.FromResult<IReadOnlyList<RouterProfileEntry>?>(
                profile.Entries
                    .Where(e => e.IsEnabled)
                    .OrderBy(e => e.Priority)
                    .ToList());
        }

        public Task<RouterProfileMode?> GetModeAsync(string profileName, CancellationToken ct = default)
        {
            var profile = _profiles.FirstOrDefault(p =>
                string.Equals(p.Name, profileName, StringComparison.Ordinal));
            return Task.FromResult(profile?.Mode);
        }

        public Task<IReadOnlyList<RouterProfile>> ListProfilesAsync(CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<RouterProfile>>(_profiles);
        }

        public Task<(IReadOnlyList<RouterProfileEntry> Entries, RouterProfileMode Mode)?> ResolveAsync(string profileName, CancellationToken ct = default)
        {
            var profile = _profiles.FirstOrDefault(p =>
                string.Equals(p.Name, profileName, StringComparison.Ordinal));
            if (profile is null)
                return Task.FromResult<(IReadOnlyList<RouterProfileEntry>, RouterProfileMode)?>(null);

            var entries = profile.Entries
                .Where(e => e.IsEnabled)
                .OrderBy(e => e.Priority)
                .ToList();

            if (!string.IsNullOrEmpty(profile.ActiveModelId))
            {
                var activeEntry = entries.FirstOrDefault(e => e.ModelId == profile.ActiveModelId);
                if (activeEntry is not null)
                {
                    entries.Remove(activeEntry);
                    entries.Insert(0, activeEntry);
                }
            }

            return Task.FromResult<(IReadOnlyList<RouterProfileEntry>, RouterProfileMode)?>(
                (entries, profile.Mode));
        }
    }

    private sealed class ScriptedCloudForwarding : ICloudForwardingService
    {
        private readonly Func<int, string, string, string, bool, CancellationToken, Task<CloudForwardResponse>> _func;
        private int _callIndex;

        public ScriptedCloudForwarding(
            Func<int, string, string, string, bool, CancellationToken, Task<CloudForwardResponse>> func)
        {
            _func = func;
        }

        public Task<CloudForwardResponse> ForwardAsync(
            string modelId, string requestBody, string requestPath, bool isStreaming, CancellationToken ct)
        {
            return _func(_callIndex++, modelId, requestBody, requestPath, isStreaming, ct);
        }
    }

    private static RouterProfileHandler CreateHandler(
        IRouterProfileService routerProfile,
        ICloudForwardingService? cloudForwarding = null,
        ISchedulerQueue? scheduler = null,
        ILogStore? logStore = null,
        IClock? clock = null,
        ISettingsStore? settings = null)
    {
        return new RouterProfileHandler(
            routerProfile,
            cloudForwarding ?? new FakeCloudForwardingService(),
            scheduler ?? new FakeSchedulerQueue(),
            logStore ?? new FakeLogStore(),
            clock ?? new FakeClock(),
            settings ?? new FakeSettingsStore());
    }

    private static FakeSettingsStore CreateRetrySettings(int retryAttempts = 0, int retryDelayMs = 0)
        => new(new Settings { RouterRetryAttempts = retryAttempts, RouterRetryDelayMs = retryDelayMs });

    [Fact]
    public async Task HandleAsync_ProfileNotFound_Returns404()
    {
        var profileService = new FakeRouterProfileService();
        var handler = CreateHandler(profileService);

        var result = await handler.HandleAsync(
            "nonexistent", "{}", "/v1/chat/completions", false, null, CancellationToken.None);

        Assert.Equal(404, result.StatusCode);
        Assert.Contains("not found", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_ProfileWithNoEntries_Returns404()
    {
        var profileService = new FakeRouterProfileService();
        profileService.AddProfile(new RouterProfile
        {
            Id = "id-1",
            Name = "empty-profile",
            Mode = RouterProfileMode.Auto,
            Entries = [],
            CreatedAt = default,
            UpdatedAt = default,
        });
        var handler = CreateHandler(profileService);

        var result = await handler.HandleAsync(
            "empty-profile", "{}", "/v1/chat/completions", false, null, CancellationToken.None);

        Assert.Equal(404, result.StatusCode);
        Assert.Contains("no enabled entries", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_CloudModelSuccess_ReturnsResult()
    {
        var profileService = new FakeRouterProfileService();
        profileService.AddProfile(new RouterProfile
        {
            Id = "id-1",
            Name = "cloud-profile",
            Mode = RouterProfileMode.Auto,
            Entries =
            [
                new RouterProfileEntry { ModelId = "cloud/openai/gpt-4o", Priority = 0, IsEnabled = true },
            ],
            CreatedAt = default,
            UpdatedAt = default,
        });

        var body = new MemoryStream("\"hello\""u8.ToArray());
        var cloud = new ScriptedCloudForwarding((idx, modelId, body, path, stream, ct) =>
            Task.FromResult(new CloudForwardResponse
            {
                StatusCode = 200,
                ContentType = "application/json",
                Body = new MemoryStream("\"hello\""u8.ToArray())
            }));

        var handler = CreateHandler(profileService, cloudForwarding: cloud);
        var result = await handler.HandleAsync(
            "cloud-profile", "{}", "/v1/chat/completions", false, null, CancellationToken.None);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("cloud/openai/gpt-4o", result.ServedModel);
        Assert.NotNull(result.Body);
    }

    [Fact]
    public async Task HandleAsync_CloudModelFailure_FallsBackToNext()
    {
        var profileService = new FakeRouterProfileService();
        profileService.AddProfile(new RouterProfile
        {
            Id = "id-1",
            Name = "fallback-profile",
            Mode = RouterProfileMode.Auto,
            Entries =
            [
                new RouterProfileEntry { ModelId = "cloud/openai/gpt-4o", Priority = 0, IsEnabled = true },
                new RouterProfileEntry { ModelId = "cloud/anthropic/claude-sonnet", Priority = 1, IsEnabled = true },
            ],
            CreatedAt = default,
            UpdatedAt = default,
        });

        var cloud = new ScriptedCloudForwarding((idx, modelId, body, path, stream, ct) =>
        {
            if (idx == 0)
                return Task.FromResult(new CloudForwardResponse
                {
                    StatusCode = 500,
                    ContentType = "application/json",
                    Body = new MemoryStream("error"u8.ToArray())
                });

            return Task.FromResult(new CloudForwardResponse
            {
                StatusCode = 200,
                ContentType = "application/json",
                Body = new MemoryStream("\"ok\""u8.ToArray())
            });
        });

        var handler = CreateHandler(profileService, cloudForwarding: cloud,
            settings: CreateRetrySettings(retryAttempts: 0));
        var result = await handler.HandleAsync(
            "fallback-profile", "{}", "/v1/chat/completions", false, null, CancellationToken.None);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("cloud/anthropic/claude-sonnet", result.ServedModel);
    }

    [Fact]
    public async Task HandleAsync_LocalModelSuccess_ReturnsResult()
    {
        var profileService = new FakeRouterProfileService();
        profileService.AddProfile(new RouterProfile
        {
            Id = "id-1",
            Name = "local-profile",
            Mode = RouterProfileMode.Auto,
            Entries =
            [
                new RouterProfileEntry { ModelId = "llama3-8b", Priority = 0, IsEnabled = true },
            ],
            CreatedAt = default,
            UpdatedAt = default,
        });

        var scheduler = new FakeSchedulerQueue
        {
            DefaultResponse = new InferenceResponse
            {
                StatusCode = 200,
                ContentType = "application/json",
                Body = new MemoryStream("\"response\""u8.ToArray()),
                TokensGenerated = 10,
                PromptTokens = 5,
            }
        };

        var handler = CreateHandler(profileService, scheduler: scheduler);
        var result = await handler.HandleAsync(
            "local-profile", "{}", "/v1/chat/completions", false, null, CancellationToken.None);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("llama3-8b", result.ServedModel);
        Assert.Equal(10, result.TokensGenerated);
    }

    [Fact]
    public async Task HandleAsync_LocalModelFailure_FallsBackToCloud()
    {
        var profileService = new FakeRouterProfileService();
        profileService.AddProfile(new RouterProfile
        {
            Id = "id-1",
            Name = "mixed-profile",
            Mode = RouterProfileMode.Auto,
            Entries =
            [
                new RouterProfileEntry { ModelId = "llama3-8b", Priority = 0, IsEnabled = true },
                new RouterProfileEntry { ModelId = "cloud/openai/gpt-4o", Priority = 1, IsEnabled = true },
            ],
            CreatedAt = default,
            UpdatedAt = default,
        });

        var scheduler = new FakeSchedulerQueue
        {
            DefaultResponse = new InferenceResponse
            {
                StatusCode = 500,
                ContentType = "application/json",
                Body = new MemoryStream("error"u8.ToArray()),
            }
        };

        var cloud = new ScriptedCloudForwarding((idx, modelId, body, path, stream, ct) =>
            Task.FromResult(new CloudForwardResponse
            {
                StatusCode = 200,
                ContentType = "application/json",
                Body = new MemoryStream("\"ok\""u8.ToArray())
            }));

        var handler = CreateHandler(profileService, cloudForwarding: cloud, scheduler: scheduler,
            settings: CreateRetrySettings(retryAttempts: 0));
        var result = await handler.HandleAsync(
            "mixed-profile", "{}", "/v1/chat/completions", false, null, CancellationToken.None);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("cloud/openai/gpt-4o", result.ServedModel);
    }

    [Fact]
    public async Task HandleAsync_AllModelsFail_Returns502()
    {
        var profileService = new FakeRouterProfileService();
        profileService.AddProfile(new RouterProfile
        {
            Id = "id-1",
            Name = "all-fail",
            Mode = RouterProfileMode.Auto,
            Entries =
            [
                new RouterProfileEntry { ModelId = "cloud/openai/gpt-4o", Priority = 0, IsEnabled = true },
                new RouterProfileEntry { ModelId = "cloud/anthropic/claude-sonnet", Priority = 1, IsEnabled = true },
            ],
            CreatedAt = default,
            UpdatedAt = default,
        });

        var cloud = new ScriptedCloudForwarding((idx, modelId, body, path, stream, ct) =>
            Task.FromResult(new CloudForwardResponse
            {
                StatusCode = 500,
                ContentType = "application/json",
                Body = new MemoryStream("error"u8.ToArray())
            }));

        var handler = CreateHandler(profileService, cloudForwarding: cloud,
            settings: CreateRetrySettings(retryAttempts: 0));
        var result = await handler.HandleAsync(
            "all-fail", "{}", "/v1/chat/completions", false, null, CancellationToken.None);

        Assert.Equal(502, result.StatusCode);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("server error", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_ManualMode_NoFallback()
    {
        var profileService = new FakeRouterProfileService();
        profileService.AddProfile(new RouterProfile
        {
            Id = "id-1",
            Name = "manual-profile",
            Mode = RouterProfileMode.Manual,
            Entries =
            [
                new RouterProfileEntry { ModelId = "cloud/openai/gpt-4o", Priority = 0, IsEnabled = true },
                new RouterProfileEntry { ModelId = "cloud/anthropic/claude-sonnet", Priority = 1, IsEnabled = true },
            ],
            CreatedAt = default,
            UpdatedAt = default,
        });

        var callCount = 0;
        var cloud = new ScriptedCloudForwarding((idx, modelId, body, path, stream, ct) =>
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult(new CloudForwardResponse
            {
                StatusCode = 500,
                ContentType = "application/json",
                Body = new MemoryStream("error"u8.ToArray())
            });
        });

        var handler = CreateHandler(profileService, cloudForwarding: cloud,
            settings: CreateRetrySettings(retryAttempts: 0));
        var result = await handler.HandleAsync(
            "manual-profile", "{}", "/v1/chat/completions", false, null, CancellationToken.None);

        Assert.Equal(502, result.StatusCode);
        Assert.Equal(1, callCount); // Manual mode only tries the first entry
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task HandleAsync_CloudException_FallsBackToNext()
    {
        var profileService = new FakeRouterProfileService();
        profileService.AddProfile(new RouterProfile
        {
            Id = "id-1",
            Name = "exception-profile",
            Mode = RouterProfileMode.Auto,
            Entries =
            [
                new RouterProfileEntry { ModelId = "cloud/openai/gpt-4o", Priority = 0, IsEnabled = true },
                new RouterProfileEntry { ModelId = "cloud/anthropic/claude-sonnet", Priority = 1, IsEnabled = true },
            ],
            CreatedAt = default,
            UpdatedAt = default,
        });

        var cloud = new ScriptedCloudForwarding((idx, modelId, body, path, stream, ct) =>
        {
            if (idx == 0)
                throw new HttpRequestException("Connection refused");
            return Task.FromResult(new CloudForwardResponse
            {
                StatusCode = 200,
                ContentType = "application/json",
                Body = new MemoryStream("\"ok\""u8.ToArray())
            });
        });

        var handler = CreateHandler(profileService, cloudForwarding: cloud);
        var result = await handler.HandleAsync(
            "exception-profile", "{}", "/v1/chat/completions", false, null, CancellationToken.None);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("cloud/anthropic/claude-sonnet", result.ServedModel);
    }

    // ─── Retry on 5xx ────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_Cloud5xx_RetriesSameModelBeforeFallback()
    {
        // Setup: 2-entry profile, first model returns 5xx on first 2 attempts then succeeds on 3rd
        // retryAttempts=2 → total 3 attempts per entry
        var profileService = new FakeRouterProfileService();
        profileService.AddProfile(new RouterProfile
        {
            Id = "id-1",
            Name = "retry-profile",
            Mode = RouterProfileMode.Auto,
            Entries =
            [
                new RouterProfileEntry { ModelId = "cloud/openai/gpt-4o", Priority = 0, IsEnabled = true },
                new RouterProfileEntry { ModelId = "cloud/anthropic/claude-sonnet", Priority = 1, IsEnabled = true },
            ],
            CreatedAt = default,
            UpdatedAt = default,
        });

        var callCount = 0;
        var cloud = new ScriptedCloudForwarding((idx, modelId, body, path, stream, ct) =>
        {
            var current = Interlocked.Increment(ref callCount);
            // Calls 1,2 → 500 (retryable); call 3 → 200 (success on retry)
            if (current <= 2)
                return Task.FromResult(new CloudForwardResponse
                {
                    StatusCode = 500,
                    ContentType = "application/json",
                    Body = new MemoryStream("error"u8.ToArray())
                });
            return Task.FromResult(new CloudForwardResponse
            {
                StatusCode = 200,
                ContentType = "application/json",
                Body = new MemoryStream("\"ok\""u8.ToArray())
            });
        });

        var handler = CreateHandler(profileService, cloudForwarding: cloud,
            settings: CreateRetrySettings(retryAttempts: 2, retryDelayMs: 0));
        var result = await handler.HandleAsync(
            "retry-profile", "{}", "/v1/chat/completions", false, null, CancellationToken.None);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("cloud/openai/gpt-4o", result.ServedModel);
        Assert.Equal(3, callCount); // 1 initial + 2 retries = 3 total calls
    }

    [Fact]
    public async Task HandleAsync_Cloud5xx_RetriesExhausted_FallsBackToNextEntry()
    {
        // Setup: 2-entry profile, first model returns 5xx on all attempts, second model succeeds
        var profileService = new FakeRouterProfileService();
        profileService.AddProfile(new RouterProfile
        {
            Id = "id-1",
            Name = "retry-fallback",
            Mode = RouterProfileMode.Auto,
            Entries =
            [
                new RouterProfileEntry { ModelId = "cloud/openai/gpt-4o", Priority = 0, IsEnabled = true },
                new RouterProfileEntry { ModelId = "cloud/anthropic/claude-sonnet", Priority = 1, IsEnabled = true },
            ],
            CreatedAt = default,
            UpdatedAt = default,
        });

        var firstModelCalls = 0;
        var cloud = new ScriptedCloudForwarding((idx, modelId, body, path, stream, ct) =>
        {
            if (modelId == "cloud/openai/gpt-4o")
            {
                Interlocked.Increment(ref firstModelCalls);
                return Task.FromResult(new CloudForwardResponse
                {
                    StatusCode = 503,
                    ContentType = "application/json",
                    Body = new MemoryStream("error"u8.ToArray())
                });
            }
            // Second model always succeeds
            return Task.FromResult(new CloudForwardResponse
            {
                StatusCode = 200,
                ContentType = "application/json",
                Body = new MemoryStream("\"ok\""u8.ToArray())
            });
        });

        var handler = CreateHandler(profileService, cloudForwarding: cloud,
            settings: CreateRetrySettings(retryAttempts: 2, retryDelayMs: 0));
        var result = await handler.HandleAsync(
            "retry-fallback", "{}", "/v1/chat/completions", false, null, CancellationToken.None);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("cloud/anthropic/claude-sonnet", result.ServedModel);
        Assert.Equal(3, firstModelCalls); // 1 initial + 2 retries = 3 total calls to first model
    }

    [Fact]
    public async Task HandleAsync_Cloud4xx_NoRetry_FallsBackImmediately()
    {
        // Setup: 2-entry profile, first model returns 400 (non-retryable), second model succeeds
        var profileService = new FakeRouterProfileService();
        profileService.AddProfile(new RouterProfile
        {
            Id = "id-1",
            Name = "no-retry-profile",
            Mode = RouterProfileMode.Auto,
            Entries =
            [
                new RouterProfileEntry { ModelId = "cloud/openai/gpt-4o", Priority = 0, IsEnabled = true },
                new RouterProfileEntry { ModelId = "cloud/anthropic/claude-sonnet", Priority = 1, IsEnabled = true },
            ],
            CreatedAt = default,
            UpdatedAt = default,
        });

        var firstModelCalls = 0;
        var cloud = new ScriptedCloudForwarding((idx, modelId, body, path, stream, ct) =>
        {
            if (modelId == "cloud/openai/gpt-4o")
            {
                Interlocked.Increment(ref firstModelCalls);
                return Task.FromResult(new CloudForwardResponse
                {
                    StatusCode = 400,
                    ContentType = "application/json",
                    Body = new MemoryStream("bad request"u8.ToArray())
                });
            }
            return Task.FromResult(new CloudForwardResponse
            {
                StatusCode = 200,
                ContentType = "application/json",
                Body = new MemoryStream("\"ok\""u8.ToArray())
            });
        });

        var handler = CreateHandler(profileService, cloudForwarding: cloud,
            settings: CreateRetrySettings(retryAttempts: 3, retryDelayMs: 0));
        var result = await handler.HandleAsync(
            "no-retry-profile", "{}", "/v1/chat/completions", false, null, CancellationToken.None);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("cloud/anthropic/claude-sonnet", result.ServedModel);
        Assert.Equal(1, firstModelCalls); // Only 1 call — 4xx is NOT retried
    }

    [Fact]
    public async Task HandleAsync_Cloud429_NoRetry_FallsBackImmediately()
    {
        // Specifically test that 429 (rate limit) is treated as non-retryable
        var profileService = new FakeRouterProfileService();
        profileService.AddProfile(new RouterProfile
        {
            Id = "id-1",
            Name = "rate-limit-profile",
            Mode = RouterProfileMode.Auto,
            Entries =
            [
                new RouterProfileEntry { ModelId = "cloud/openai/gpt-4o", Priority = 0, IsEnabled = true },
                new RouterProfileEntry { ModelId = "cloud/anthropic/claude-sonnet", Priority = 1, IsEnabled = true },
            ],
            CreatedAt = default,
            UpdatedAt = default,
        });

        var firstModelCalls = 0;
        var cloud = new ScriptedCloudForwarding((idx, modelId, body, path, stream, ct) =>
        {
            if (modelId == "cloud/openai/gpt-4o")
            {
                Interlocked.Increment(ref firstModelCalls);
                return Task.FromResult(new CloudForwardResponse
                {
                    StatusCode = 429,
                    ContentType = "application/json",
                    Body = new MemoryStream("rate limited"u8.ToArray())
                });
            }
            return Task.FromResult(new CloudForwardResponse
            {
                StatusCode = 200,
                ContentType = "application/json",
                Body = new MemoryStream("\"ok\""u8.ToArray())
            });
        });

        var handler = CreateHandler(profileService, cloudForwarding: cloud,
            settings: CreateRetrySettings(retryAttempts: 3, retryDelayMs: 0));
        var result = await handler.HandleAsync(
            "rate-limit-profile", "{}", "/v1/chat/completions", false, null, CancellationToken.None);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("cloud/anthropic/claude-sonnet", result.ServedModel);
        Assert.Equal(1, firstModelCalls); // 429 is NOT retried — falls back immediately
    }

    [Fact]
    public async Task HandleAsync_RetryExhausted_SingleEntry_Returns502()
    {
        // Single-entry profile, all retries exhausted → 502
        var profileService = new FakeRouterProfileService();
        profileService.AddProfile(new RouterProfile
        {
            Id = "id-1",
            Name = "single-entry-retry",
            Mode = RouterProfileMode.Auto,
            Entries =
            [
                new RouterProfileEntry { ModelId = "cloud/openai/gpt-4o", Priority = 0, IsEnabled = true },
            ],
            CreatedAt = default,
            UpdatedAt = default,
        });

        var cloud = new ScriptedCloudForwarding((idx, modelId, body, path, stream, ct) =>
            Task.FromResult(new CloudForwardResponse
            {
                StatusCode = 502,
                ContentType = "application/json",
                Body = new MemoryStream("bad gateway"u8.ToArray())
            }));

        var handler = CreateHandler(profileService, cloudForwarding: cloud,
            settings: CreateRetrySettings(retryAttempts: 2, retryDelayMs: 0));
        var result = await handler.HandleAsync(
            "single-entry-retry", "{}", "/v1/chat/completions", false, null, CancellationToken.None);

        Assert.Equal(502, result.StatusCode);
        Assert.Contains("server error", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3 attempts", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_ManualMode_RetriesSameModel()
    {
        // Manual mode: only first entry, but should still retry on 5xx
        var profileService = new FakeRouterProfileService();
        profileService.AddProfile(new RouterProfile
        {
            Id = "id-1",
            Name = "manual-retry",
            Mode = RouterProfileMode.Manual,
            Entries =
            [
                new RouterProfileEntry { ModelId = "cloud/openai/gpt-4o", Priority = 0, IsEnabled = true },
                new RouterProfileEntry { ModelId = "cloud/anthropic/claude-sonnet", Priority = 1, IsEnabled = true },
            ],
            CreatedAt = default,
            UpdatedAt = default,
        });

        var callCount = 0;
        var cloud = new ScriptedCloudForwarding((idx, modelId, body, path, stream, ct) =>
        {
            var current = Interlocked.Increment(ref callCount);
            if (current <= 2)
                return Task.FromResult(new CloudForwardResponse
                {
                    StatusCode = 500,
                    ContentType = "application/json",
                    Body = new MemoryStream("error"u8.ToArray())
                });
            return Task.FromResult(new CloudForwardResponse
            {
                StatusCode = 200,
                ContentType = "application/json",
                Body = new MemoryStream("\"ok\""u8.ToArray())
            });
        });

        var handler = CreateHandler(profileService, cloudForwarding: cloud,
            settings: CreateRetrySettings(retryAttempts: 2, retryDelayMs: 0));
        var result = await handler.HandleAsync(
            "manual-retry", "{}", "/v1/chat/completions", false, null, CancellationToken.None);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("cloud/openai/gpt-4o", result.ServedModel);
        Assert.Equal(3, callCount); // Manual mode retries first entry only, never falls back
    }

    [Fact]
    public async Task HandleAsync_RetryZeroAttempts_NoRetriesPerformed()
    {
        // retryAttempts=0 → exactly 1 call per entry (no retries)
        var profileService = new FakeRouterProfileService();
        profileService.AddProfile(new RouterProfile
        {
            Id = "id-1",
            Name = "zero-retry",
            Mode = RouterProfileMode.Auto,
            Entries =
            [
                new RouterProfileEntry { ModelId = "cloud/openai/gpt-4o", Priority = 0, IsEnabled = true },
                new RouterProfileEntry { ModelId = "cloud/anthropic/claude-sonnet", Priority = 1, IsEnabled = true },
            ],
            CreatedAt = default,
            UpdatedAt = default,
        });

        var firstModelCalls = 0;
        var cloud = new ScriptedCloudForwarding((idx, modelId, body, path, stream, ct) =>
        {
            if (modelId == "cloud/openai/gpt-4o")
            {
                Interlocked.Increment(ref firstModelCalls);
                return Task.FromResult(new CloudForwardResponse
                {
                    StatusCode = 500,
                    ContentType = "application/json",
                    Body = new MemoryStream("error"u8.ToArray())
                });
            }
            return Task.FromResult(new CloudForwardResponse
            {
                StatusCode = 200,
                ContentType = "application/json",
                Body = new MemoryStream("\"ok\""u8.ToArray())
            });
        });

        var handler = CreateHandler(profileService, cloudForwarding: cloud,
            settings: CreateRetrySettings(retryAttempts: 0, retryDelayMs: 0));
        var result = await handler.HandleAsync(
            "zero-retry", "{}", "/v1/chat/completions", false, null, CancellationToken.None);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("cloud/anthropic/claude-sonnet", result.ServedModel);
        Assert.Equal(1, firstModelCalls); // No retries, immediate fallback
    }
}
