using Microsoft.AspNetCore.Identity;

namespace Unswarm.Core.Persistence;

public class ApplicationUser : IdentityUser
{
    public bool IsTempPassword { get; set; }
}
