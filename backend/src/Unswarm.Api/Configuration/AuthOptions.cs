namespace Unswarm.Api.Configuration;

public sealed class AuthOptions
{
    public string ApiKey { get; set; } = "";
    public string[] ProtectedPaths { get; set; } = [];
}
