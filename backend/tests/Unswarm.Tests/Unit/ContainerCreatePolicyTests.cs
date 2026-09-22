using Unswarm.Core.Contracts;
using Unswarm.Core.Services.Validation;

namespace Unswarm.Tests.Unit;

public sealed class ContainerCreatePolicyTests
{
    private static ContainerCreateConfig Config(
        string networkMode = "bridge",
        string ipcMode = "private",
        int containerPort = 8080,
        int? hostPort = null,
        int shmSizeMb = 16384,
        IReadOnlyList<string>? devices = null,
        IReadOnlyList<VolumeMount>? volumes = null,
        IReadOnlyList<EnvVar>? env = null,
        IReadOnlyList<string>? serverArgs = null) => new()
    {
        Image = "image:latest",
        ContainerName = "container",
        NetworkMode = networkMode,
        IpcMode = ipcMode,
        ContainerPort = containerPort,
        HostPort = hostPort,
        ShmSizeMb = shmSizeMb,
        Devices = devices,
        Volumes = volumes,
        Env = env,
        ServerArgs = serverArgs,
    };

    private static void Validate(ContainerCreateConfig config, DockerPolicyOptions? options = null)
        => ContainerCreatePolicy.Validate(config, options ?? new DockerPolicyOptions());

    private static void AssertDenied(ContainerCreateConfig config, DockerPolicyOptions? options = null)
        => Assert.Throws<ContainerPolicyException>(() => Validate(config, options));

    // ── NetworkMode ────────────────────────────────────────────────────

    [Theory]
    [InlineData("bridge")]
    [InlineData("none")]
    [InlineData("")]
    public void NetworkMode_AllowsSafeModes(string mode)
        => Validate(Config(networkMode: mode));

    [Theory]
    [InlineData("host")]
    [InlineData("container:abc123")]
    public void NetworkMode_DeniesHostAndContainer(string mode)
        => AssertDenied(Config(networkMode: mode));

    [Fact]
    public void NetworkMode_HostAllowedWhenOptedIn()
        => Validate(Config(networkMode: "host"), new DockerPolicyOptions { AllowHostNetwork = true });

    // ── IpcMode ────────────────────────────────────────────────────────

    [Fact]
    public void IpcMode_AllowsPrivate()
        => Validate(Config(ipcMode: "private"));

    [Fact]
    public void IpcMode_DeniesHost()
        => AssertDenied(Config(ipcMode: "host"));

    [Fact]
    public void IpcMode_HostAllowedWhenOptedIn()
        => Validate(Config(ipcMode: "host"), new DockerPolicyOptions { AllowHostIpc = true });

    // ── Ports / sizes / args / env ─────────────────────────────────────

    [Theory]
    [InlineData(65536)]
    public void ContainerPort_OutOfRange_Denied(int port)
        => AssertDenied(Config(containerPort: port));

    [Fact]
    public void ContainerPort_Zero_TreatedAsUnset_Allowed()
        => Validate(Config(containerPort: 0));

    [Theory]
    [InlineData(65536)]
    public void HostPort_OutOfRange_Denied(int port)
        => AssertDenied(Config(hostPort: port));

    [Fact]
    public void HostPort_Zero_TreatedAsUnset_Allowed()
        => Validate(Config(hostPort: 0));

    [Theory]
    [InlineData(-1)]
    [InlineData(32)]
    [InlineData(65537)]
    public void ShmSize_OutOfRange_Denied(int shm)
        => AssertDenied(Config(shmSizeMb: shm));

    [Fact]
    public void ShmSize_Zero_TreatedAsUnset_Allowed()
        => Validate(Config(shmSizeMb: 0));

    [Theory]
    [InlineData(64)]
    [InlineData(16384)]
    [InlineData(65536)]
    public void ShmSize_Valid_Allowed(int shm)
        => Validate(Config(shmSizeMb: shm));

    [Fact]
    public void ServerArgs_TooMany_Denied()
        => AssertDenied(Config(serverArgs: Enumerable.Repeat("--flag", 65).ToList()));

    [Fact]
    public void ServerArgs_TooLong_Denied()
        => AssertDenied(Config(serverArgs: [new string('x', 4097)]));

    [Theory]
    [InlineData("GOOD_VAR")]
    [InlineData("_private")]
    [InlineData("A1")]
    public void EnvKey_Valid_Allowed(string key)
        => Validate(Config(env: [new EnvVar { Key = key, Value = "v" }]));

    [Theory]
    [InlineData("1BAD")]
    [InlineData("has-dash")]
    [InlineData("has space")]
    public void EnvKey_Invalid_Denied(string key)
        => AssertDenied(Config(env: [new EnvVar { Key = key, Value = "v" }]));

    // ── Volumes ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/")]
    [InlineData("/etc")]
    [InlineData("/etc/passwd")]
    [InlineData("/proc")]
    [InlineData("/sys/kernel")]
    [InlineData("/dev")]
    [InlineData("/root")]
    [InlineData("/boot")]
    [InlineData("/var/run/docker.sock")]
    [InlineData("/run/containerd/containerd.sock")]
    public void Volume_ProtectedPaths_Denied(string host)
        => AssertDenied(Config(volumes: [new VolumeMount { Host = host, Container = "/data" }]));

    [Theory]
    [InlineData("/home/models")]
    [InlineData("/run/unswarm")]
    [InlineData("/opt/models")]
    public void Volume_AllowedPaths_Allowed(string host)
        => Validate(Config(volumes: [new VolumeMount { Host = host, Container = "/data" }]));

    [Fact]
    public void Volume_RelativePath_Denied()
        => AssertDenied(Config(volumes: [new VolumeMount { Host = "relative/path", Container = "/data" }]));

    [Fact]
    public void Volume_SymlinkToRoot_Denied()
    {
        var link = Path.Combine(Path.GetTempPath(), $"unswarm-policy-{Guid.NewGuid():N}-root");
        try
        {
            Directory.CreateSymbolicLink(link, "/");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // symlinks unavailable — nothing to assert
        }

        try
        {
            AssertDenied(Config(volumes: [new VolumeMount { Host = link, Container = "/data" }]));
        }
        finally
        {
            try { Directory.Delete(link); } catch { }
        }
    }

    // ── Devices ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/dev/kfd")]
    [InlineData("/dev/dri/renderD128")]
    [InlineData("/dev/nvidia0")]
    public void Device_AllowedPrefixes_Allowed(string device)
        => Validate(Config(devices: [device]));

    [Theory]
    [InlineData("/dev/sda")]
    [InlineData("/etc/passwd")]
    [InlineData("/dev")]
    public void Device_NotAllowed_Denied(string device)
        => AssertDenied(Config(devices: [device]));
}
