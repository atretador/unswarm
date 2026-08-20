using Unswarm.Core.Contracts;

namespace Unswarm.Api.Dtos;

public sealed class PromptResponse
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public static PromptResponse From(PromptEntry e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        Text = e.Text,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt
    };
}

public sealed class PromptUpsertRequest
{
    public string? Name { get; set; }
    public string? Text { get; set; }
}
