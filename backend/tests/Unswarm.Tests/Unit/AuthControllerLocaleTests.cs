using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Unswarm.Api.Controllers;
using Unswarm.Core.Persistence;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

/// <summary>
/// AuthController locale-preference tests (GET/PUT /api/auth/me/locale). Uses a
/// user store that implements IUserClaimStore so the claim operations work.
/// </summary>
public sealed class AuthControllerLocaleTests
{
    private readonly FakeUserStoreCoverage2 _store = new();

    private AuthController CreateController()
    {
        var um = new StubUserManager(_store);
        var sm = new StubSignInManager(um);
        return new AuthController(um, sm);
    }

    private static void SetAuthenticatedUser(ControllerBase controller, string userId)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "TestAuth");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
    }

    private static void SetUnauthenticated(ControllerBase controller)
        => controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

    [Fact]
    public async Task GetLocale_NoClaim_DefaultsToEnglish()
    {
        var user = _store.AddUser("locale-user");
        var ctrl = CreateController();
        SetAuthenticatedUser(ctrl, user.Id);

        var result = await ctrl.GetLocalePreference();

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        Assert.Contains("\"locale\":\"en\"", json);
    }

    [Fact]
    public async Task GetLocale_ReturnsStoredClaim()
    {
        var user = _store.AddUser("locale-user");
        await _store.AddClaimsAsync(user, [new Claim("locale", "de")], CancellationToken.None);
        var ctrl = CreateController();
        SetAuthenticatedUser(ctrl, user.Id);

        var result = await ctrl.GetLocalePreference();

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        Assert.Contains("\"locale\":\"de\"", json);
    }

    [Fact]
    public async Task GetLocale_UnknownUser_ReturnsUnauthorized()
    {
        var ctrl = CreateController();
        SetAuthenticatedUser(ctrl, "nonexistent-id");

        Assert.IsType<UnauthorizedResult>(await ctrl.GetLocalePreference());
    }

    [Fact]
    public async Task UpdateLocale_AddsNewClaim()
    {
        var user = _store.AddUser("locale-user");
        var ctrl = CreateController();
        SetAuthenticatedUser(ctrl, user.Id);

        var result = await ctrl.UpdateLocalePreference(new LocaleRequest("fr"));

        var ok = Assert.IsType<OkObjectResult>(result);
        var claims = await _store.GetClaimsAsync(user, CancellationToken.None);
        Assert.Contains(claims, c => c.Type == "locale" && c.Value == "fr");
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public async Task UpdateLocale_ReplacesExistingClaim()
    {
        var user = _store.AddUser("locale-user");
        await _store.AddClaimsAsync(user, [new Claim("locale", "en")], CancellationToken.None);
        var ctrl = CreateController();
        SetAuthenticatedUser(ctrl, user.Id);

        var result = await ctrl.UpdateLocalePreference(new LocaleRequest("ja"));

        Assert.IsType<OkObjectResult>(result);
        var claims = await _store.GetClaimsAsync(user, CancellationToken.None);
        var localeClaims = claims.Where(c => c.Type == "locale").ToList();
        Assert.Single(localeClaims);
        Assert.Equal("ja", localeClaims[0].Value);
    }

    [Fact]
    public async Task UpdateLocale_UnknownUser_ReturnsUnauthorized()
    {
        var ctrl = CreateController();
        SetAuthenticatedUser(ctrl, "nonexistent-id");

        Assert.IsType<UnauthorizedResult>(await ctrl.UpdateLocalePreference(new LocaleRequest("fr")));
    }

    // ── Stubs ─────────────────────────────────────────────────────────

    private sealed class StubUserManager : UserManager<ApplicationUser>
    {
        public StubUserManager(FakeUserStoreCoverage2 store)
            : base(store,
                Microsoft.Extensions.Options.Options.Create(new IdentityOptions()),
                new PasswordHasher<ApplicationUser>(),
                [], [], new UpperInvariantLookupNormalizer(),
                new IdentityErrorDescriber(),
                new ServiceCollection().BuildServiceProvider(),
                new LoggerFactory().CreateLogger<UserManager<ApplicationUser>>())
        { }
    }

    private sealed class StubSignInManager : SignInManager<ApplicationUser>
    {
        public StubSignInManager(UserManager<ApplicationUser> userManager)
            : base(userManager,
                new HttpContextAccessor(),
                new StubClaimsPrincipalFactory(),
                Microsoft.Extensions.Options.Options.Create(new IdentityOptions()),
                new LoggerFactory().CreateLogger<SignInManager<ApplicationUser>>(),
                new StubAuthenticationSchemeProvider(),
                new DefaultUserConfirmation<ApplicationUser>())
        { }
    }

    private sealed class StubClaimsPrincipalFactory : IUserClaimsPrincipalFactory<ApplicationUser>
    {
        public Task<ClaimsPrincipal> CreateAsync(ApplicationUser user)
            => Task.FromResult(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, user.Id!), new Claim(ClaimTypes.Name, user.UserName!)], "Test")));
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
}
