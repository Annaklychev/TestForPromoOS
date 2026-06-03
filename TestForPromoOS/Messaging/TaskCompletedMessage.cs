using System.Text.Json.Serialization;
using TestForPromoOS.Models;

namespace TestForPromoOS.Messaging;

public record TaskCompletedMessage(
    [property: JsonPropertyName("taskId")] Guid TaskId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("completedAt")] DateTimeOffset CompletedAt,
    [property: JsonPropertyName("priority")] Priority Priority);
