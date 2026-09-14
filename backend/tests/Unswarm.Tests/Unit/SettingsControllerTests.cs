using Microsoft.AspNetCore.Mvc;
using Unswarm.Api.Controllers;
using Unswarm.Api.Dtos;
using Unswarm.Tests.Fakes;
using Unswarm.Core.Models;

namespace Unswarm.Tests.Unit;

public sealed class SettingsControllerTests
{
    private readonly FakeSettingsStore _store = new();

    private SettingsController CreateController(FakeSettingsStore? store = null)
        => new(store ?? _store);

    [Fact]
    public async Task Get_ReturnsOkWithSettings()
    {
        var ctrl = CreateController();

        var result = await ctrl.Get(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<SettingsResponse>(ok.Value);
        // Default settings values
        Assert.Equal(120, resp.RequestTimeout);
        Assert.Equal("fifo", resp.PriorityMode);
    }

    [Fact]
    public async Task Update_AppliesSettingsAndReturnsOk()
    {
        var ctrl = CreateController();
        var req = new SettingsUpdateRequest
        {
            RequestTimeout = 300,
            PriorityMode = "priority",
            EnableBenchmarking = false
        };

        var result = await ctrl.Update(req, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<SettingsResponse>(ok.Value);
        Assert.Equal(300, resp.RequestTimeout);
        Assert.Equal("priority", resp.PriorityMode);
        Assert.False(resp.EnableBenchmarking);

        // Verify persisted in store
        var stored = await _store.GetAsync(CancellationToken.None);
        Assert.Equal(300, stored.RequestTimeout);
        Assert.Equal("priority", stored.PriorityMode);
    }

    [Fact]
    public async Task Update_ClampsNumericValues()
    {
        var ctrl = CreateController();

        // MaxQueueDepth clamped to [1, 10000]
        var resultLow = await ctrl.Update(
            new SettingsUpdateRequest { MaxQueueDepth = 0 },
            CancellationToken.None);
        var okLow = Assert.IsType<OkObjectResult>(resultLow);
        var respLow = Assert.IsType<SettingsResponse>(okLow.Value);
        Assert.Equal(1, respLow.MaxQueueDepth);

        var resultHigh = await ctrl.Update(
            new SettingsUpdateRequest { MaxQueueDepth = 99999 },
            CancellationToken.None);
        var okHigh = Assert.IsType<OkObjectResult>(resultHigh);
        var respHigh = Assert.IsType<SettingsResponse>(okHigh.Value);
        Assert.Equal(10000, respHigh.MaxQueueDepth);

        // HealthCheckTimeoutSeconds clamped to [10, 600]
        var resultHcsLow = await ctrl.Update(
            new SettingsUpdateRequest { HealthCheckTimeoutSeconds = 1 },
            CancellationToken.None);
        var okHcsLow = Assert.IsType<OkObjectResult>(resultHcsLow);
        var respHcsLow = Assert.IsType<SettingsResponse>(okHcsLow.Value);
        Assert.Equal(10, respHcsLow.HealthCheckTimeoutSeconds);

        var resultHcsHigh = await ctrl.Update(
            new SettingsUpdateRequest { HealthCheckTimeoutSeconds = 9999 },
            CancellationToken.None);
        var okHcsHigh = Assert.IsType<OkObjectResult>(resultHcsHigh);
        var respHcsHigh = Assert.IsType<SettingsResponse>(okHcsHigh.Value);
        Assert.Equal(600, respHcsHigh.HealthCheckTimeoutSeconds);

        // RouterRetryAttempts clamped to [0, 10]
        var resultRraHigh = await ctrl.Update(
            new SettingsUpdateRequest { RouterRetryAttempts = 99 },
            CancellationToken.None);
        var okRraHigh = Assert.IsType<OkObjectResult>(resultRraHigh);
        var respRraHigh = Assert.IsType<SettingsResponse>(okRraHigh.Value);
        Assert.Equal(10, respRraHigh.RouterRetryAttempts);

        // ConversationDwellSeconds min 1
        var resultCds = await ctrl.Update(
            new SettingsUpdateRequest { ConversationDwellSeconds = 0 },
            CancellationToken.None);
        var okCds = Assert.IsType<OkObjectResult>(resultCds);
        var respCds = Assert.IsType<SettingsResponse>(okCds.Value);
        Assert.Equal(1, respCds.ConversationDwellSeconds);

        // UsageRetentionDays min 0
        var resultUrd = await ctrl.Update(
            new SettingsUpdateRequest { UsageRetentionDays = -5 },
            CancellationToken.None);
        var okUrd = Assert.IsType<OkObjectResult>(resultUrd);
        var respUrd = Assert.IsType<SettingsResponse>(okUrd.Value);
        Assert.Equal(0, respUrd.UsageRetentionDays);
    }

    [Fact]
    public async Task Update_WithNullFields_KeepsDefaults()
    {
        // Pre-populate with non-default values
        var store = new FakeSettingsStore(new Settings
        {
            RequestTimeout = 500,
            PriorityMode = "priority",
            IdleTimeout = 600
        });
        var ctrl = CreateController(store);

        // Update only one field; others should be preserved
        var result = await ctrl.Update(
            new SettingsUpdateRequest { RequestTimeout = 99 },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<SettingsResponse>(ok.Value);
        Assert.Equal(99, resp.RequestTimeout);
        Assert.Equal("priority", resp.PriorityMode);
        Assert.Equal(600, resp.IdleTimeout);
    }
}
