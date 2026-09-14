using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Unswarm.Api.Controllers;
using Unswarm.Core.Helpers;
using Unswarm.Core.Persistence;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

public sealed class UsersControllerTests
{
    private readonly FakeUserStore _store = new();

    private StubUserManager CreateUserManager() => new(_store);

    private UsersController CreateController(StubUserManager? userManager = null, string? currentUserId = null)
    {
        var ctrl = new UsersController(userManager ?? CreateUserManager());
        if (currentUserId is not null)
        {
            var claims = new[] { new Claim(ClaimTypes.NameIdentifier, currentUserId) };
            var identity = new ClaimsIdentity(claims, "TestAuth");
            ctrl.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
            };
        }
        return ctrl;
    }

    // ── UserManager stub with token overrides ─────────────────────────

    private sealed class StubUserManager : UserManager<ApplicationUser>
    {
        private readonly Dictionary<string, string> _resetTokens = new();

        public StubUserManager(FakeUserStore store)
            : base(store,
                Microsoft.Extensions.Options.Options.Create(new IdentityOptions()),
                new PasswordHasher<ApplicationUser>(),
                [], [], new UpperInvariantLookupNormalizer(),
                new IdentityErrorDescriber(),
                new ServiceCollection().BuildServiceProvider(),
                new LoggerFactory().CreateLogger<UserManager<ApplicationUser>>())
        { }

        public override Task<string> GeneratePasswordResetTokenAsync(ApplicationUser user)
        {
            var token = $"reset-token-{user.Id}";
            _resetTokens[user.Id] = token;
            return Task.FromResult(token);
        }

        public override async Task<IdentityResult> ResetPasswordAsync(ApplicationUser user, string token, string newPassword)
        {
            if (!_resetTokens.TryGetValue(user.Id, out var expected) || expected != token)
                return IdentityResult.Failed(new IdentityError { Description = "Invalid token" });

            var hasher = new PasswordHasher<ApplicationUser>();
            var hash = hasher.HashPassword(user, newPassword);
            await ((IUserPasswordStore<ApplicationUser>)Store).SetPasswordHashAsync(user, hash, CancellationToken.None);
            await Store.UpdateAsync(user, CancellationToken.None);
            return IdentityResult.Success;
        }
    }

    // ── List ──────────────────────────────────────────────────────────

    [Fact]
    public void List_ReturnsOkWithUsers()
    {
        _store.AddUser("alice", password: "p1");
        _store.AddUser("bob", password: "p2");
        var ctrl = CreateController();

        var result = ctrl.List();

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.Contains("alice", json);
        Assert.Contains("bob", json);
    }

    // ── Create ────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_ValidRequest_ReturnsOk()
    {
        var ctrl = CreateController();

        var result = await ctrl.Create(new CreateUserRequest("newuser", "Strong1!"));

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.Contains("newuser", json);
    }

    [Fact]
    public async Task Create_DuplicateUsername_ReturnsBadRequest()
    {
        _store.AddUser("taken");
        var ctrl = CreateController();

        var result = await ctrl.Create(new CreateUserRequest("taken", "Strong1!"));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── ResetPassword ─────────────────────────────────────────────────

    [Fact]
    public async Task ResetPassword_OtherUser_ReturnsOk()
    {
        var target = _store.AddUser("target");
        var admin = _store.AddUser("admin");
        var um = CreateUserManager();
        var ctrl = CreateController(um, currentUserId: admin.Id);

        var result = await ctrl.ResetPassword(target.Id, new ResetPasswordRequest("NewPass1!"));

        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public async Task ResetPassword_SelfReset_ReturnsBadRequest()
    {
        var user = _store.AddUser("selfuser");
        var um = CreateUserManager();
        var ctrl = CreateController(um, currentUserId: user.Id);

        var result = await ctrl.ResetPassword(user.Id, new ResetPasswordRequest("NewPass1!"));

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        // Verify it's a LocalizedError with the correct key
        var json = JsonSerializer.Serialize(bad.Value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.Contains("cannotResetOwnPassword", json);
    }

    // ── Delete ────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_OtherUser_ReturnsOk()
    {
        var target = _store.AddUser("deleteme");
        var admin = _store.AddUser("admin");
        var um = CreateUserManager();
        var ctrl = CreateController(um, currentUserId: admin.Id);

        var result = await ctrl.Delete(target.Id);

        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public async Task Delete_SelfDelete_ReturnsBadRequest()
    {
        var user = _store.AddUser("selfdel");
        var um = CreateUserManager();
        var ctrl = CreateController(um, currentUserId: user.Id);

        var result = await ctrl.Delete(user.Id);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var json = JsonSerializer.Serialize(bad.Value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.Contains("cannotDeleteOwnAccount", json);
    }
}
