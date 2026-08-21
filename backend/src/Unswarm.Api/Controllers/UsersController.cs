using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Unswarm.Core.Persistence;

namespace Unswarm.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin")]
public class UsersController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _userManager;

    public UsersController(UserManager<ApplicationUser> userManager)
    {
        _userManager = userManager;
    }

    [HttpGet]
    public IActionResult List()
    {
        var users = _userManager.Users.Select(u => new { id = u.Id, username = u.UserName, isTempPassword = u.IsTempPassword }).ToList();
        return Ok(users);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest request)
    {
        var user = new ApplicationUser { UserName = request.Username };
        var result = await _userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
            return BadRequest(new { error = string.Join(", ", result.Errors.Select(e => e.Description)) });

        return Ok(new { id = user.Id, username = user.UserName, isTempPassword = user.IsTempPassword });
    }

    [HttpPost("{id}/reset-password")]
    public async Task<IActionResult> ResetPassword(string id, [FromBody] ResetPasswordRequest request)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user == null) return NotFound();

        var currentUserId = _userManager.GetUserId(User);
        if (user.Id == currentUserId)
            return BadRequest(new { error = "Cannot reset your own password here. Use Change Password instead." });

        var token = await _userManager.GeneratePasswordResetTokenAsync(user);
        var result = await _userManager.ResetPasswordAsync(user, token, request.NewPassword);
        if (!result.Succeeded)
            return BadRequest(new { error = string.Join(", ", result.Errors.Select(e => e.Description)) });

        if (user.IsTempPassword)
        {
            user.IsTempPassword = false;
            await _userManager.UpdateAsync(user);
        }

        return Ok();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user == null) return NotFound();

        var currentUserId = _userManager.GetUserId(User);
        if (user.Id == currentUserId)
            return BadRequest(new { error = "Cannot delete your own account" });

        var result = await _userManager.DeleteAsync(user);
        if (!result.Succeeded)
            return BadRequest(new { error = string.Join(", ", result.Errors.Select(e => e.Description)) });

        return Ok();
    }
}

public record CreateUserRequest(string Username, string Password);
public record ResetPasswordRequest(string NewPassword);
