using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TestForPromoOS.Data;
using TestForPromoOS.Messaging;
using TestForPromoOS.Models;

namespace TestForPromoOS.Tests;

public class CompleteTaskFlowTests : IClassFixture<TaskApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly TaskApiFactory _factory;

    public CompleteTaskFlowTests(TaskApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Create_then_complete_publishes_task_completed_event()
    {
        var publisher = (FakeTaskEventPublisher)_factory.Services.GetRequiredService<ITaskEventPublisher>();
        publisher.Reset();

        var client = _factory.CreateClient();

        var createResponse = await client.PostAsJsonAsync("/tasks",
            new { title = "Buy milk", priority = "High" }, JsonOptions);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var created = await createResponse.Content.ReadFromJsonAsync<TaskItem>(JsonOptions);
        Assert.NotNull(created);
        Assert.NotEqual(Guid.Empty, created.Id);

        var completeResponse = await client.PutAsync($"/tasks/{created.Id}/complete", content: null);
        Assert.Equal(HttpStatusCode.OK, completeResponse.StatusCode);

        var published = publisher.WaitForFirst(TimeSpan.FromSeconds(2));
        Assert.NotNull(published);
        Assert.Equal(created.Id, published!.TaskId);
        Assert.Equal("Buy milk", published.Title);
        Assert.Equal(Priority.High, published.Priority);
        Assert.True(published.CompletedAt > DateTimeOffset.UtcNow.AddSeconds(-10));
    }
}

public class TaskApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = $"tests-{Guid.NewGuid()}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<DbContextOptions>();
            services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
            services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(_dbName));

            services.RemoveAll<ITaskEventPublisher>();
            services.AddSingleton<ITaskEventPublisher, FakeTaskEventPublisher>();
        });
    }
}

public sealed class FakeTaskEventPublisher : ITaskEventPublisher
{
    private readonly List<TaskCompletedMessage> _published = new();
    private readonly object _lock = new();

    public IReadOnlyList<TaskCompletedMessage> Published
    {
        get { lock (_lock) return _published.ToArray(); }
    }

    public void PublishTaskCompleted(TaskCompletedMessage message)
    {
        lock (_lock) _published.Add(message);
    }

    public void Reset()
    {
        lock (_lock) _published.Clear();
    }

    public TaskCompletedMessage? WaitForFirst(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (_lock)
            {
                if (_published.Count > 0) return _published[0];
            }
            Thread.Sleep(20);
        }
        return null;
    }
}
