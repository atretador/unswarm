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
}
