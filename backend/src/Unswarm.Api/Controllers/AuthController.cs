using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Unswarm.Core.Persistence;

namespace Unswarm.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;

    public AuthController(UserManager<ApplicationUser> userManager, SignInManager<ApplicationUser> signInManager)
    {
        _userManager = userManager;
        _signInManager = signInManager;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var user = await _userManager.FindByNameAsync(request.Username);
        if (user == null || !await _userManager.CheckPasswordAsync(user, request.Password))
            return Unauthorized(new { error = "Invalid username or password" });

        await _signInManager.SignInAsync(user, isPersistent: true);
        return Ok(new { username = user.UserName, isTempPassword = user.IsTempPassword });
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await _signInManager.SignOutAsync();
        return Ok();
    }

    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        if (User.Identity?.IsAuthenticated != true)
            return Unauthorized();

        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        return Ok(new { username = user.UserName, email = user.Email, isTempPassword = user.IsTempPassword });
    }

    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        if (User.Identity?.IsAuthenticated != true)
            return Unauthorized();

        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var result = await _userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
            return BadRequest(new { error = "Invalid current password or new password does not meet requirements" });

        if (user.IsTempPassword)
        {
            user.IsTempPassword = false;
            await _userManager.UpdateAsync(user);
        }

        return Ok();
    }
}

public record LoginRequest(string Username, string Password);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
