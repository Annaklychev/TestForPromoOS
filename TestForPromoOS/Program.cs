using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using TestForPromoOS.Data;
using TestForPromoOS.Messaging;
using TestForPromoOS.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection("RabbitMq"));
builder.Services.AddSingleton<RabbitMqTaskEventPublisher>();
builder.Services.AddSingleton<ITaskEventPublisher>(sp =>
    sp.GetRequiredService<RabbitMqTaskEventPublisher>());
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<RabbitMqTaskEventPublisher>());

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (db.Database.IsRelational())
        await db.Database.MigrateAsync();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapPost("/tasks", async (CreateTaskRequest req, AppDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(req.Title) || req.Title.Length > 200)
        return Results.BadRequest("Title is required and must be 1..200 chars.");

    var item = new TaskItem
    {
        Id = Guid.NewGuid(),
        Title = req.Title,
        IsCompleted = false,
        CreatedAt = DateTimeOffset.UtcNow,
        Priority = req.Priority
    };

    db.TaskItems.Add(item);
    await db.SaveChangesAsync();

    return Results.Created($"/tasks/{item.Id}", item);
});

app.MapGet("/tasks", async (AppDbContext db) =>
    await db.TaskItems.AsNoTracking().ToListAsync());

app.MapPut("/tasks/{id:guid}/complete", async (
    Guid id,
    AppDbContext db,
    ITaskEventPublisher publisher) =>
{
    var item = await db.TaskItems.FindAsync(id);
    if (item is null) return Results.NotFound();
    if (item.IsCompleted) return Results.Conflict("Task already completed.");

    item.IsCompleted = true;
    item.CompletedAt = DateTimeOffset.UtcNow;

    try
    {
        await db.SaveChangesAsync();
    }
    catch (DbUpdateConcurrencyException)
    {
        return Results.Conflict("Task already completed.");
    }

    publisher.PublishTaskCompleted(new TaskCompletedMessage(
        item.Id, item.Title, item.CompletedAt!.Value, item.Priority));

    return Results.Ok(item);
});

app.MapDelete("/tasks/{id:guid}", async (Guid id, AppDbContext db) =>
{
    var rows = await db.TaskItems.Where(t => t.Id == id).ExecuteDeleteAsync();
    return rows == 0 ? Results.NotFound() : Results.NoContent();
});

app.Run();

public record CreateTaskRequest(string Title, Priority Priority = Priority.Medium);

public partial class Program;
