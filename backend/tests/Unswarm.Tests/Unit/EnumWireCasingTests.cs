using System.Text.Json;
using System.Text.Json.Serialization;
using Unswarm.Core.Models;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Pins the lowercase wire contract for enum-typed statuses. The frontend depends on
/// lowercase strings ("ready", "running", "processing", ...) — the Api's
/// JsonStringEnumConverter is configured with JsonNamingPolicy.CamelCase in Program.cs.
/// These tests mirror that configuration so a regression in the serializer setup fails
/// here, not silently in the UI.
/// </summary>
public sealed class EnumWireCasingTests
{
    // Same options shape as Program.cs AddJsonOptions.
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Fact]
    public void ModelStatus_SerializesLowercase()
    {
        Assert.Equal("\"ready\"", JsonSerializer.Serialize(ModelStatus.Ready, Options));
        Assert.Equal("\"validating\"", JsonSerializer.Serialize(ModelStatus.Validating, Options));
        Assert.Equal("\"invalid\"", JsonSerializer.Serialize(ModelStatus.Invalid, Options));
        Assert.Equal("\"deprecated\"", JsonSerializer.Serialize(ModelStatus.Deprecated, Options));
    }

    [Fact]
    public void ContainerStatus_SerializesLowercase()
    {
        Assert.Equal("\"running\"", JsonSerializer.Serialize(ContainerStatus.Running, Options));
        Assert.Equal("\"stopped\"", JsonSerializer.Serialize(ContainerStatus.Stopped, Options));
        Assert.Equal("\"starting\"", JsonSerializer.Serialize(ContainerStatus.Starting, Options));
        Assert.Equal("\"error\"", JsonSerializer.Serialize(ContainerStatus.Error, Options));
    }

    [Fact]
    public void ContainerRegistrationStatus_SerializesLowercase()
    {
        Assert.Equal("\"ready\"", JsonSerializer.Serialize(ContainerRegistrationStatus.Ready, Options));
        Assert.Equal("\"error\"", JsonSerializer.Serialize(ContainerRegistrationStatus.Error, Options));
        Assert.Equal("\"starting\"", JsonSerializer.Serialize(ContainerRegistrationStatus.Starting, Options));
        Assert.Equal("\"registered\"", JsonSerializer.Serialize(ContainerRegistrationStatus.Registered, Options));
    }

    [Fact]
    public void QueueItemStatus_SerializesLowercase()
    {
        Assert.Equal("\"processing\"", JsonSerializer.Serialize(QueueItemStatus.Processing, Options));
        Assert.Equal("\"waiting\"", JsonSerializer.Serialize(QueueItemStatus.Waiting, Options));
        Assert.Equal("\"completed\"", JsonSerializer.Serialize(QueueItemStatus.Completed, Options));
        Assert.Equal("\"failed\"", JsonSerializer.Serialize(QueueItemStatus.Failed, Options));
    }

    [Fact]
    public void EnumDeserialization_IsCaseInsensitive()
    {
        // Request bodies may arrive in either casing; the converter must keep reading
        // both PascalCase and lowercase.
        Assert.Equal(ModelStatus.Ready, JsonSerializer.Deserialize<ModelStatus>("\"Ready\"", Options));
        Assert.Equal(ModelStatus.Ready, JsonSerializer.Deserialize<ModelStatus>("\"ready\"", Options));
        Assert.Equal(ContainerStatus.Running, JsonSerializer.Deserialize<ContainerStatus>("\"Running\"", Options));
        Assert.Equal(ContainerStatus.Running, JsonSerializer.Deserialize<ContainerStatus>("\"running\"", Options));
    }
}
