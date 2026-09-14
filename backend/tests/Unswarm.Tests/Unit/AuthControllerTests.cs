using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Unswarm.Api.Controllers;
using Unswarm.Core.Persistence;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

public sealed class AuthControllerTests
{
    private readonly FakeUserStore _store = new();

    private StubUserManager CreateUserManager() => new(_store);

    private AuthController CreateController(StubUserManager? userManager = null)
    {
        var um = userManager ?? CreateUserManager();
        var sm = new StubSignInManager(um, _store);
        return new AuthController(um, sm);
    }

    /// <summary>Sets up HttpContext.User with an authenticated identity for the given user id.</summary>
    private static void SetAuthenticatedUser(ControllerBase controller, string userId)
    {
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, userId) };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
    }

    // ── UserManager stub ──────────────────────────────────────────────

    private sealed class StubUserManager : UserManager<ApplicationUser>
    {
        public StubUserManager(FakeUserStore store)
            : base(store,
                Microsoft.Extensions.Options.Options.Create(new IdentityOptions()),
                new PasswordHasher<ApplicationUser>(),
                [], [], new UpperInvariantLookupNormalizer(),
                new IdentityErrorDescriber(),
                new ServiceCollection().BuildServiceProvider(),
                new LoggerFactory().CreateLogger<UserManager<ApplicationUser>>())
        { }
    }

    // ── SignInManager stub ────────────────────────────────────────────

    private sealed class StubSignInManager : SignInManager<ApplicationUser>
    {
        private readonly FakeUserStore _store;

        public StubSignInManager(UserManager<ApplicationUser> userManager, FakeUserStore store)
            : base(userManager,
                new HttpContextAccessor(),
                new StubClaimsPrincipalFactory(),
                Microsoft.Extensions.Options.Options.Create(new IdentityOptions()),
                new LoggerFactory().CreateLogger<SignInManager<ApplicationUser>>(),
                new StubAuthenticationSchemeProvider(),
                new DefaultUserConfirmation<ApplicationUser>())
        {
            _store = store;
        }

        public override Task<Microsoft.AspNetCore.Identity.SignInResult> PasswordSignInAsync(
            ApplicationUser user, string password, bool isPersistent, bool lockoutOnFailure)
        {
            var hash = _store.GetPasswordHashAsync(user, CancellationToken.None).GetAwaiter().GetResult();
            if (hash is null)
                return Task.FromResult(Microsoft.AspNetCore.Identity.SignInResult.Failed);

            var result = new PasswordHasher<ApplicationUser>().VerifyHashedPassword(user, hash, password);
            return Task.FromResult(result == PasswordVerificationResult.Success
                ? Microsoft.AspNetCore.Identity.SignInResult.Success
                : Microsoft.AspNetCore.Identity.SignInResult.Failed);
        }

        public override Task SignOutAsync() => Task.CompletedTask;
    }

    // ── Ancillary stubs ───────────────────────────────────────────────

    private sealed class StubClaimsPrincipalFactory : IUserClaimsPrincipalFactory<ApplicationUser>
    {
        public Task<ClaimsPrincipal> CreateAsync(ApplicationUser user)
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id!),
                new Claim(ClaimTypes.Name, user.UserName!)
            };
            return Task.FromResult(new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")));
        }
    }

    private sealed class StubAuthenticationSchemeProvider : IAuthenticationSchemeProvider
    {
        public void AddScheme(AuthenticationScheme scheme) { }
        public Task<AuthenticationScheme?> GetSchemeAsync(string name) => Task.FromResult<AuthenticationScheme?>(null);
        public Task<AuthenticationScheme?> GetDefaultAuthenticateSchemeAsync() => Task.FromResult<AuthenticationScheme?>(null);
        public Task<AuthenticationScheme?> GetDefaultChallengeSchemeAsync() => Task.FromResult<AuthenticationScheme?>(null);
        public Task<AuthenticationScheme?> GetDefaultForbidSchemeAsync() => Task.FromResult<AuthenticationScheme?>(null);
        public Task<AuthenticationScheme?> GetDefaultSignInSchemeAsync() => Task.FromResult<AuthenticationScheme?>(null);
        public Task<AuthenticationScheme?> GetDefaultSignOutSchemeAsync() => Task.FromResult<AuthenticationScheme?>(null);
        public Task<IEnumerable<AuthenticationScheme>> GetAllSchemesAsync() => Task.FromResult(Enumerable.Empty<AuthenticationScheme>());
        public Task<AuthenticationScheme?> GetPolicySchemeAsync(string policyName) => Task.FromResult<AuthenticationScheme?>(null);
        public Task<IEnumerable<AuthenticationScheme>> GetRequestHandlerSchemesAsync() => Task.FromResult(Enumerable.Empty<AuthenticationScheme>());
        public void RemoveScheme(string name) { }
    }

    // ── Login tests ───────────────────────────────────────────────────

    [Fact]
    public async Task Login_ValidCredentials_ReturnsOk()
    {
        _store.AddUser("admin", password: "P@ss1");
        var ctrl = CreateController();

        var result = await ctrl.Login(new LoginRequest("admin", "P@ss1"));

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public async Task Login_InvalidUsername_ReturnsUnauthorized()
    {
        var ctrl = CreateController();

        var result = await ctrl.Login(new LoginRequest("nobody", "x"));

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task Login_WrongPassword_ReturnsUnauthorized()
    {
        _store.AddUser("admin", password: "correct");
        var ctrl = CreateController();

        var result = await ctrl.Login(new LoginRequest("admin", "wrong"));

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task Login_TempPassword_ReturnsOkWithTempFlag()
    {
        _store.AddUser("tempuser", password: "temp1", isTempPassword: true);
        var ctrl = CreateController();

        var result = await ctrl.Login(new LoginRequest("tempuser", "temp1"));

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);

        // Verify isTempPassword is reflected in the response
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        Assert.Contains("\"isTempPassword\":true", json);
    }

    // ── Logout tests ──────────────────────────────────────────────────

    [Fact]
    public async Task Logout_ReturnsOk()
    {
        var ctrl = CreateController();

        var result = await ctrl.Logout();

        Assert.IsType<OkResult>(result);
    }

    // ── Me tests ──────────────────────────────────────────────────────

    [Fact]
    public async Task Me_Authenticated_ReturnsOk()
    {
        var user = _store.AddUser("meuser", password: "p");
        var ctrl = CreateController();
        SetAuthenticatedUser(ctrl, user.Id);

        var result = await ctrl.Me();

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        Assert.Contains("meuser", json);
    }

    // ── ChangePassword tests ──────────────────────────────────────────

    [Fact]
    public async Task ChangePassword_Valid_ReturnsOk()
    {
        var user = _store.AddUser("cpuser", password: "Old123!");
        var ctrl = CreateController();
        SetAuthenticatedUser(ctrl, user.Id);

        var result = await ctrl.ChangePassword(new ChangePasswordRequest("Old123!", "New456!"));

        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public async Task ChangePassword_InvalidCurrentPassword_ReturnsBadRequest()
    {
        var user = _store.AddUser("cpuser2", password: "Correct1!");
        var ctrl = CreateController();
        SetAuthenticatedUser(ctrl, user.Id);

        var result = await ctrl.ChangePassword(new ChangePasswordRequest("Wrong!", "New456!"));

        Assert.IsType<BadRequestObjectResult>(result);
    }
}
