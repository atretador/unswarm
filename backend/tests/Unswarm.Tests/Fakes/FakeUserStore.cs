using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Unswarm.Core.Persistence;

namespace Unswarm.Tests.Fakes;

/// <summary>
/// In-memory user store for unit-testing controllers that depend on UserManager/SignInManager.
/// Implements the store interfaces consumed by the real UserManager&lt;ApplicationUser&gt;.
/// </summary>
public sealed class FakeUserStore :
    IUserStore<ApplicationUser>,
    IUserPasswordStore<ApplicationUser>,
    IUserRoleStore<ApplicationUser>,
    IQueryableUserStore<ApplicationUser>
{
    private readonly Dictionary<string, ApplicationUser> _usersById = new();
    private readonly Dictionary<string, ApplicationUser> _usersByName = new();
    private readonly Dictionary<string, string> _passwordHashes = new();
    private readonly Dictionary<string, HashSet<string>> _roles = new(StringComparer.OrdinalIgnoreCase);
    private int _nextId;

    private static readonly ILookupNormalizer Normalizer = new UpperInvariantLookupNormalizer();
    private static readonly IPasswordHasher<ApplicationUser> Hasher = new PasswordHasher<ApplicationUser>();

    public IQueryable<ApplicationUser> Users => _usersById.Values.AsQueryable();

    // ── Test helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Adds a user directly to the store, normalizing the username and optionally hashing a password.
    /// </summary>
    public ApplicationUser AddUser(string userName, string? password = null, bool isTempPassword = false)
    {
        var user = new ApplicationUser
        {
            Id = (++_nextId).ToString(),
            UserName = userName,
            NormalizedUserName = Normalizer.NormalizeName(userName),
            IsTempPassword = isTempPassword
        };
        _usersById[user.Id] = user;
        _usersByName[user.NormalizedUserName!] = user;
        if (password != null)
            _passwordHashes[user.Id] = Hasher.HashPassword(user, password);
        return user;
    }

    // ── IUserStore<ApplicationUser> ──────────────────────────────────

    public void Dispose() { }

    public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken ct)
        => Task.FromResult(user.Id)!;

    public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken ct)
        => Task.FromResult(user.UserName);

    public Task SetUserNameAsync(ApplicationUser user, string? userName, CancellationToken ct)
    {
        user.UserName = userName;
        return Task.CompletedTask;
    }

    public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken ct)
    {
        // Reject duplicate normalized usernames (mirrors real store behaviour).
        if (user.NormalizedUserName is not null && _usersByName.ContainsKey(user.NormalizedUserName))
            return Task.FromResult(IdentityResult.Failed(new IdentityError
            {
                Code = "DuplicateUserName",
                Description = $"User name '{user.UserName}' is already taken."
            }));

        user.Id ??= (++_nextId).ToString();
        _usersById[user.Id] = user;
        if (user.NormalizedUserName is not null)
            _usersByName[user.NormalizedUserName] = user;
        return Task.FromResult(IdentityResult.Success);
    }

    public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken ct)
    {
        if (user.NormalizedUserName is not null)
            _usersByName[user.NormalizedUserName] = user;
        return Task.FromResult(IdentityResult.Success);
    }

    public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken ct)
    {
        _usersById.Remove(user.Id);
        if (user.NormalizedUserName is not null)
            _usersByName.Remove(user.NormalizedUserName);
        _passwordHashes.Remove(user.Id);
        _roles.Remove(user.Id);
        return Task.FromResult(IdentityResult.Success);
    }

    public Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken ct)
    {
        _usersById.TryGetValue(userId, out var user);
        return Task.FromResult(user);
    }

    public Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken ct)
    {
        _usersByName.TryGetValue(normalizedUserName, out var user);
        return Task.FromResult(user);
    }

    public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken ct)
        => Task.FromResult(user.NormalizedUserName);

    public Task SetNormalizedUserNameAsync(ApplicationUser user, string? normalizedName, CancellationToken ct)
    {
        user.NormalizedUserName = normalizedName;
        return Task.CompletedTask;
    }

    // ── IUserPasswordStore<ApplicationUser> ──────────────────────────

    public Task SetPasswordHashAsync(ApplicationUser user, string? passwordHash, CancellationToken ct)
    {
        if (passwordHash is not null)
            _passwordHashes[user.Id] = passwordHash;
        else
            _passwordHashes.Remove(user.Id);
        return Task.CompletedTask;
    }

    public Task<string?> GetPasswordHashAsync(ApplicationUser user, CancellationToken ct)
    {
        _passwordHashes.TryGetValue(user.Id, out var hash);
        return Task.FromResult(hash);
    }

    public Task<bool> HasPasswordAsync(ApplicationUser user, CancellationToken ct)
        => Task.FromResult(_passwordHashes.ContainsKey(user.Id));

    // ── IUserRoleStore<ApplicationUser> ──────────────────────────────

    public Task AddToRoleAsync(ApplicationUser user, string roleName, CancellationToken ct)
    {
        if (!_roles.TryGetValue(user.Id, out var set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _roles[user.Id] = set;
        }
        set.Add(roleName);
        return Task.CompletedTask;
    }

    public Task RemoveFromRoleAsync(ApplicationUser user, string roleName, CancellationToken ct)
    {
        if (_roles.TryGetValue(user.Id, out var set))
            set.Remove(roleName);
        return Task.CompletedTask;
    }

    public Task<IList<string>> GetRolesAsync(ApplicationUser user, CancellationToken ct)
    {
        _roles.TryGetValue(user.Id, out var set);
        return Task.FromResult<IList<string>>(set?.ToList() ?? []);
    }

    public Task<bool> IsInRoleAsync(ApplicationUser user, string roleName, CancellationToken ct)
    {
        _roles.TryGetValue(user.Id, out var set);
        return Task.FromResult(set?.Contains(roleName, StringComparer.OrdinalIgnoreCase) ?? false);
    }

    public Task<IList<ApplicationUser>> GetUsersInRoleAsync(string roleName, CancellationToken ct)
    {
        var users = _roles
            .Where(kv => kv.Value.Contains(roleName, StringComparer.OrdinalIgnoreCase))
            .Select(kv => _usersById[kv.Key])
            .ToList();
        return Task.FromResult<IList<ApplicationUser>>(users);
    }

    // ── IQueryableUserStore<ApplicationUser> ─────────────────────────

    IQueryable<ApplicationUser> IQueryableUserStore<ApplicationUser>.Users => Users;
}
