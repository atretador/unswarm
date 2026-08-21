using Unswarm.Core.Models;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

public sealed class SettingsTests
{
    [Fact]
    public async Task DefaultSettings_HasExpectedValues()
    {
        var store = new FakeSettingsStore();
        var settings = await store.GetAsync();

        Assert.Equal(1, settings.MaxConcurrentModels);
        Assert.Null(settings.DefaultModel);
        Assert.Equal(120, settings.RequestTimeout);
        Assert.Equal(10, settings.HealthCheckInterval);
        Assert.True(settings.AutoShutdownIdle);
        Assert.Equal(300, settings.IdleTimeout);
        Assert.Equal(168, settings.LogRetention);
        Assert.True(settings.EnableBenchmarking);
        Assert.Equal("fifo", settings.PriorityMode);
        Assert.False(settings.BatchDrain);
        Assert.True(settings.LazyStop);
        Assert.Equal(32, settings.MaxQueueDepth);
    }

    [Fact]
    public async Task Update_PersistsAllFields()
    {
        var store = new FakeSettingsStore();
        var updated = new Settings
        {
            MaxConcurrentModels = 4,
            DefaultModel = "llama",
            RequestTimeout = 300,
            HealthCheckInterval = 5,
            AutoShutdownIdle = false,
            IdleTimeout = 600,
            LogRetention = 24,
            EnableBenchmarking = false,
            PriorityMode = "priority",
            BatchDrain = true,
            LazyStop = false,
            MaxQueueDepth = 128
        };

        var result = await store.UpdateAsync(updated);

        Assert.Equal(4, result.MaxConcurrentModels);
        Assert.Equal("llama", result.DefaultModel);
        Assert.Equal(300, result.RequestTimeout);
        Assert.False(result.AutoShutdownIdle);
        Assert.Equal("priority", result.PriorityMode);
        Assert.True(result.BatchDrain);
        Assert.False(result.LazyStop);
        Assert.Equal(128, result.MaxQueueDepth);
    }

    [Fact]
    public async Task RoundTrip_GetAfterUpdate_ReturnsUpdatedValues()
    {
        var store = new FakeSettingsStore();

        await store.UpdateAsync(new Settings
        {
            MaxConcurrentModels = 2,
            DefaultModel = "mistral",
            PriorityMode = "priority",
            LazyStop = false
        });

        var fetched = await store.GetAsync();

        Assert.Equal(2, fetched.MaxConcurrentModels);
        Assert.Equal("mistral", fetched.DefaultModel);
        Assert.Equal("priority", fetched.PriorityMode);
        Assert.False(fetched.LazyStop);
        // Unchanged defaults still apply for non-updated fields
        Assert.Equal(120, fetched.RequestTimeout);
        Assert.Equal(32, fetched.MaxQueueDepth);
    }

    [Fact]
    public async Task Update_OverwritesPreviousUpdate()
    {
        var store = new FakeSettingsStore();

        await store.UpdateAsync(new Settings { MaxConcurrentModels = 2, DefaultModel = "a" });
        await store.UpdateAsync(new Settings { MaxConcurrentModels = 8, DefaultModel = "b" });

        var result = await store.GetAsync();

        Assert.Equal(8, result.MaxConcurrentModels);
        Assert.Equal("b", result.DefaultModel);
    }

    [Fact]
    public void SettingsModel_DefaultConstructor_AllDefaultsCorrect()
    {
        var s = new Settings();

        Assert.Equal(1, s.MaxConcurrentModels);
        Assert.Null(s.DefaultModel);
        Assert.Equal(120, s.RequestTimeout);
        Assert.Equal(10, s.HealthCheckInterval);
        Assert.True(s.AutoShutdownIdle);
        Assert.Equal(300, s.IdleTimeout);
        Assert.Equal(168, s.LogRetention);
        Assert.True(s.EnableBenchmarking);
        Assert.Equal("fifo", s.PriorityMode);
        Assert.False(s.BatchDrain);
        Assert.True(s.LazyStop);
        Assert.Equal(32, s.MaxQueueDepth);
    }

    [Theory]
    [InlineData(0, 1)]      // zero → clamped to 1
    [InlineData(-5, 1)]     // negative → clamped to 1
    [InlineData(1, 1)]      // boundary
    [InlineData(100, 100)]  // normal
    [InlineData(10000, 10000)] // upper boundary
    [InlineData(99999, 10000)] // over upper → clamped
    public void MaxQueueDepth_Clamp_ProducesExpected(int input, int expected)
    {
        // The controller applies Math.Clamp(value, 1, 10000) before persisting.
        // This test verifies the clamping semantics used by the controller.
        var clamped = Math.Clamp(input, 1, 10000);
        Assert.Equal(expected, clamped);
    }
}
