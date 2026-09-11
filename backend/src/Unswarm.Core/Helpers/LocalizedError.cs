namespace Unswarm.Core.Helpers;

public static class LocalizedError
{
    /// <summary>
    /// Creates a structured error response with i18n key + params alongside English fallback.
    /// Usage: return BadRequest(LocalizedError.Create("auth.invalidCredentials"));
    /// Or with params: return BadRequest(LocalizedError.Create("agents.notFound", new { name = agentName }));
    /// </summary>
    public static object Create(string key, object? parameters = null)
    {
        return new
        {
            errorKey = key,
            errorParams = parameters ?? new { },
            error = key // English fallback — frontend can use this if i18n lookup fails
        };
    }
}
