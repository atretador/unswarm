using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Unswarm.Core.Persistence;

namespace Unswarm.Tests.Fakes;

/// <summary>
/// In-memory user store that also implements <see cref="IUserClaimStore{TUser}"/>
/// so <c>UserManager.GetClaimsAsync/AddClaimAsync/RemoveClaimAsync</c> work. Used
/// by the AuthController locale-preference coverage tests.
/// </summary>
public sealed class FakeUserStoreCoverage2 :
    IUserStore<ApplicationUser>,
    IUserPasswordStore<ApplicationUser>,
    IUserRoleStore<ApplicationUser>,
    IUserClaimStore<ApplicationUser>
{
    private readonly Dictionary<string, ApplicationUser> _byId = new();
    private readonly Dictionary<string, string> _hashes = new();
    private readonly Dictionary<string, List<Claim>> _claims = new();
    private int _nextId;

    public ApplicationUser AddUser(string userName, string? password = null, bool isTempPassword = false)
    {
        var user = new ApplicationUser
        {
            Id = (++_nextId).ToString(),
            UserName = userName,
            IsTempPassword = isTempPassword
        };
        _byId[user.Id] = user;
        _claims[user.Id] = [];
        if (password is not null)
            _hashes[user.Id] = new PasswordHasher<ApplicationUser>().HashPassword(user, password);
        return user;
    }

    public void Dispose() { }

    public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(user.Id)!;
    public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(user.UserName);
    public Task SetUserNameAsync(ApplicationUser user, string? userName, CancellationToken ct) { user.UserName = userName; return Task.CompletedTask; }
    public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(user.NormalizedUserName);
    public Task SetNormalizedUserNameAsync(ApplicationUser user, string? normalizedName, CancellationToken ct) { user.NormalizedUserName = normalizedName; return Task.CompletedTask; }

    public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken ct)
    {
        user.Id ??= (++_nextId).ToString();
        _byId[user.Id] = user;
        _claims[user.Id] = [];
        return Task.FromResult(IdentityResult.Success);
    }

    public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken ct)
    {
        _byId[user.Id] = user;
        return Task.FromResult(IdentityResult.Success);
    }

    public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken ct)
    {
        _byId.Remove(user.Id);
        _hashes.Remove(user.Id);
        _claims.Remove(user.Id);
        return Task.FromResult(IdentityResult.Success);
    }

    public Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken ct)
        => Task.FromResult(_byId.TryGetValue(userId, out var user) ? user : null);

    public Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken ct)
        => Task.FromResult(_byId.Values.FirstOrDefault(u => u.UserName == normalizedUserName));

    public Task SetPasswordHashAsync(ApplicationUser user, string? passwordHash, CancellationToken ct)
    {
        if (passwordHash is null) _hashes.Remove(user.Id);
        else _hashes[user.Id] = passwordHash;
        return Task.CompletedTask;
    }

    public Task<string?> GetPasswordHashAsync(ApplicationUser user, CancellationToken ct)
        => Task.FromResult(_hashes.TryGetValue(user.Id, out var h) ? h : null);

    public Task<bool> HasPasswordAsync(ApplicationUser user, CancellationToken ct)
        => Task.FromResult(_hashes.ContainsKey(user.Id));

    // Roles are not exercised by the locale tests.
    public Task AddToRoleAsync(ApplicationUser user, string roleName, CancellationToken ct) => Task.CompletedTask;
    public Task RemoveFromRoleAsync(ApplicationUser user, string roleName, CancellationToken ct) => Task.CompletedTask;
    public Task<IList<string>> GetRolesAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult<IList<string>>([]);
    public Task<bool> IsInRoleAsync(ApplicationUser user, string roleName, CancellationToken ct) => Task.FromResult(false);
    public Task<IList<ApplicationUser>> GetUsersInRoleAsync(string roleName, CancellationToken ct) => Task.FromResult<IList<ApplicationUser>>([]);

    // Claims store.
    public Task<IList<Claim>> GetClaimsAsync(ApplicationUser user, CancellationToken ct)
        => Task.FromResult<IList<Claim>>(_claims.TryGetValue(user.Id, out var list) ? list.ToList() : []);

    public Task AddClaimsAsync(ApplicationUser user, IEnumerable<Claim> claims, CancellationToken ct)
    {
        if (!_claims.TryGetValue(user.Id, out var list)) { list = []; _claims[user.Id] = list; }
        list.AddRange(claims);
        return Task.CompletedTask;
    }

    public Task ReplaceClaimAsync(ApplicationUser user, Claim claim, Claim newClaim, CancellationToken ct)
    {
        if (_claims.TryGetValue(user.Id, out var list)) { list.Remove(claim); list.Add(newClaim); }
        return Task.CompletedTask;
    }

    public Task RemoveClaimsAsync(ApplicationUser user, IEnumerable<Claim> claims, CancellationToken ct)
    {
        if (_claims.TryGetValue(user.Id, out var list))
            foreach (var claim in claims) list.Remove(claim);
        return Task.CompletedTask;
    }

    public Task<IList<ApplicationUser>> GetUsersForClaimAsync(Claim claim, CancellationToken ct)
        => Task.FromResult<IList<ApplicationUser>>([]);
}
