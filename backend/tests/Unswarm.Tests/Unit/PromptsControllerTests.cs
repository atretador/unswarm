using Microsoft.AspNetCore.Mvc;
using Unswarm.Api.Controllers;
using Unswarm.Api.Dtos;
using Unswarm.Core.Contracts;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

public sealed class PromptsControllerTests
{
    private readonly FakePromptStore _prompts = new();

    private PromptsController CreateController() => new(_prompts);

    [Fact]
    public async Task List_ReturnsAllPrompts()
    {
        await _prompts.CreateAsync("Alpha", "text1");
        await _prompts.CreateAsync("Beta", "text2");

        var result = await CreateController().List(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var items = Assert.IsAssignableFrom<List<PromptResponse>>(ok.Value);
        Assert.Equal(2, items.Count);
        Assert.Equal("Alpha", items[0].Name);
        Assert.Equal("Beta", items[1].Name);
    }

    [Fact]
    public async Task Create_ValidInput_Returns201WithLocation()
    {
        var controller = CreateController();
        var request = new PromptUpsertRequest { Name = "Summarizer", Text = "Write a summary" };

        var result = await controller.Create(request, CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result);
        var response = Assert.IsType<PromptResponse>(created.Value);
        Assert.Equal("Summarizer", response.Name);
        Assert.Equal("Write a summary", response.Text);
        Assert.NotNull(response.Id);
    }

    [Fact]
    public async Task Create_EmptyName_Returns400()
    {
        var controller = CreateController();
        var result = await controller.Create(new PromptUpsertRequest { Name = "", Text = "text" }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Create_EmptyText_Returns400()
    {
        var controller = CreateController();
        var result = await controller.Create(new PromptUpsertRequest { Name = "name", Text = "" }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Get_ExistingId_Returns200()
    {
        var created = await _prompts.CreateAsync("My Prompt", "body");
        var controller = CreateController();

        var result = await controller.Get(created.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<PromptResponse>(ok.Value);
        Assert.Equal("My Prompt", response.Name);
    }

    [Fact]
    public async Task Get_UnknownId_Returns404()
    {
        var controller = CreateController();
        var result = await controller.Get("nonexistent", CancellationToken.None);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Update_ValidInput_Returns200()
    {
        var created = await _prompts.CreateAsync("Old", "text");
        var controller = CreateController();

        var result = await controller.Update(created.Id,
            new PromptUpsertRequest { Name = "New", Text = "updated" },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<PromptResponse>(ok.Value);
        Assert.Equal("New", response.Name);
        Assert.Equal("updated", response.Text);
    }

    [Fact]
    public async Task Update_UnknownId_Returns404()
    {
        var controller = CreateController();
        var result = await controller.Update("nonexistent",
            new PromptUpsertRequest { Name = "n", Text = "t" },
            CancellationToken.None);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Update_EmptyName_Returns400()
    {
        await _prompts.CreateAsync("Valid", "text");
        var controller = CreateController();

        var result = await controller.Update("any-id",
            new PromptUpsertRequest { Name = "", Text = "t" },
            CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Delete_ExistingId_Returns204()
    {
        var created = await _prompts.CreateAsync("Delete Me", "body");
        var controller = CreateController();

        var result = await controller.Delete(created.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Null(await _prompts.GetAsync(created.Id));
    }

    [Fact]
    public async Task Delete_UnknownId_Returns404()
    {
        var controller = CreateController();
        var result = await controller.Delete("nonexistent", CancellationToken.None);
        Assert.IsType<NotFoundResult>(result);
    }
}
