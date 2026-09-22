using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Unswarm.Api.Middleware;

namespace Unswarm.Tests.Unit;

public sealed class PermissionCheckTests
{
    private static HttpRequest Request(params Claim[] claims)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))
        };
        return context.Request;
    }

    [Fact]
    public void CookieAdmin_HasReadAndWriteAccessToEveryDomain()
    {
        var request = Request(new Claim(ClaimTypes.Role, "Admin"));

        Assert.True(PermissionCheck.Read(request, "users"));
        Assert.True(PermissionCheck.Write(request, "anything"));
    }

    [Fact]
    public void ReadWritePermission_AllowsBothOperations()
    {
        var request = Request(new Claim("unswarm:permissions", "{\"users\":\"rw\"}"));

        Assert.True(PermissionCheck.Read(request, "users"));
        Assert.True(PermissionCheck.Write(request, "users"));
    }

    [Fact]
    public void ReadOnlyPermission_AllowsReadButNotWrite()
    {
        var request = Request(new Claim("unswarm:permissions", "{\"users\":\"r\"}"));

        Assert.True(PermissionCheck.Read(request, "users"));
        Assert.False(PermissionCheck.Write(request, "users"));
    }

    [Fact]
    public void NonePermission_DeniesReadAndWrite()
    {
        var request = Request(new Claim("unswarm:permissions", "{\"users\":\"none\"}"));

        Assert.False(PermissionCheck.Read(request, "users"));
        Assert.False(PermissionCheck.Write(request, "users"));
    }

    [Fact]
    public void MissingDomain_DeniesReadAndWrite()
    {
        var request = Request(new Claim("unswarm:permissions", "{\"users\":\"rw\"}"));

        Assert.False(PermissionCheck.Read(request, "models"));
        Assert.False(PermissionCheck.Write(request, "models"));
    }

    [Fact]
    public void MalformedPermissions_DeniesReadAndWrite()
    {
        var request = Request(new Claim("unswarm:permissions", "not-json"));

        Assert.False(PermissionCheck.Read(request, "users"));
        Assert.False(PermissionCheck.Write(request, "users"));
    }

    [Fact]
    public void NonControlPlaneKeyWithoutPermissions_DoesNotGetCookieAccess()
    {
        var request = Request(new Claim("unswarm:key-scope", "Inference"));

        Assert.False(PermissionCheck.Read(request, "users"));
        Assert.False(PermissionCheck.Write(request, "users"));
    }

    [Fact]
    public void UnknownDomain_DeniesAccess()
    {
        var request = Request(new Claim("unswarm:permissions", "{\"users\":\"rw\"}"));

        Assert.False(PermissionCheck.Read(request, "unknown-domain"));
        Assert.False(PermissionCheck.Write(request, "unknown-domain"));
    }

    [Fact]
    public void DomainKey_IsCaseInsensitive()
    {
        var request = Request(new Claim("unswarm:permissions", "{\"APIKeys\":\"rw\"}"));

        Assert.True(PermissionCheck.Read(request, "apikeys"));
        Assert.True(PermissionCheck.Write(request, "APIKEYS"));
    }

    // ── ParsePermissions ──────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-json")]
    [InlineData("{\"users\":")]
    public void ParsePermissions_MalformedOrMissing_ReturnsEmpty(string? json)
    {
        var parsed = PermissionCheck.ParsePermissions(json);

        Assert.Empty(parsed);
    }

    [Fact]
    public void ParsePermissions_CaseNormalizesDomainKeys()
    {
        var parsed = PermissionCheck.ParsePermissions("{\"APIKeys\":\"rw\",\" Models \":\"r\"}");

        Assert.Equal("rw", parsed["apikeys"]);
        Assert.Equal("r", parsed["models"]);
    }

    // ── LevelRank ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, 0)]
    [InlineData("none", 0)]
    [InlineData("r", 1)]
    [InlineData("rw", 2)]
    [InlineData("RW", 2)]
    [InlineData("garbage", 0)]
    public void LevelRank_MapsLevels(string? level, int expected)
    {
        Assert.Equal(expected, PermissionCheck.LevelRank(level));
    }

    // ── IsSubsetOf ────────────────────────────────────────────────────

    private static Dictionary<string, string> Perms(string json) => PermissionCheck.ParsePermissions(json);

    [Fact]
    public void IsSubsetOf_RequestedCoveredByHeld_ReturnsTrue()
    {
        Assert.True(PermissionCheck.IsSubsetOf(Perms("{\"models\":\"r\"}"), Perms("{\"models\":\"rw\"}")));
    }

    [Fact]
    public void IsSubsetOf_RequestedExceedsHeld_ReturnsFalse()
    {
        Assert.False(PermissionCheck.IsSubsetOf(Perms("{\"models\":\"rw\"}"), Perms("{\"models\":\"r\"}")));
    }

    [Fact]
    public void IsSubsetOf_RequestedDomainMissingFromHeld_ReturnsFalse()
    {
        Assert.False(PermissionCheck.IsSubsetOf(Perms("{\"users\":\"rw\"}"), Perms("{\"models\":\"rw\"}")));
    }

    [Fact]
    public void IsSubsetOf_EmptyRequested_ReturnsTrue()
    {
        Assert.True(PermissionCheck.IsSubsetOf(Perms("{}"), Perms("{\"models\":\"r\"}")));
    }

    [Fact]
    public void IsSubsetOf_NoneRequested_DoesNotRequireGrant()
    {
        Assert.True(PermissionCheck.IsSubsetOf(Perms("{\"users\":\"none\"}"), Perms("{}")));
    }

    [Fact]
    public void IsSubsetOf_IsCaseInsensitive()
    {
        Assert.True(PermissionCheck.IsSubsetOf(Perms("{\"APIKeys\":\"r\"}"), Perms("{\"apikeys\":\"rw\"}")));
    }
}
