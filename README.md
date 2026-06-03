# TestForPromoOS

ASP.NET Core 9 Web API с CRUD задач, оптимистической блокировкой через `xmin` и публикацией событий в RabbitMQ при завершении задачи.

## Стек

- .NET 9, Minimal API
- PostgreSQL 16 + EF Core 9 (Npgsql)
- RabbitMQ 3 (только publish, consumer не реализован)
- xUnit 2 + `WebApplicationFactory` + EF Core InMemory для интеграционных тестов
- Docker / docker compose

## Быстрый старт (Docker)

```bash
docker compose up -d --build
```

Поднимет:

| Сервис    | Порт          | Доступ                                  |
|-----------|---------------|------------------------------------------|
| API       | 8080          | `http://localhost:8080/tasks`            |
| Postgres  | 5432          | `postgres / _Aa123456`, БД `testforpromoos` |
| RabbitMQ  | 5672 + 15672  | UI `http://localhost:15672`, `guest / guest` |

Миграции применяются автоматически при старте приложения (`db.Database.MigrateAsync()` под `IsRelational()` — на InMemory в тестах не срабатывает).

Остановить:

```bash
docker compose down       # сохранить volume с БД
docker compose down -v    # снести volume тоже
```

## Локальный запуск без Docker

1. Поднять PostgreSQL и RabbitMQ (любым способом).
2. Поправить `TestForPromoOS/appsettings.json` под свои credentials.
3. Применить миграции:
   ```bash
   dotnet ef database update --project TestForPromoOS/TestForPromoOS.csproj
   ```
4. Запустить:
   ```bash
   dotnet run --project TestForPromoOS/TestForPromoOS.csproj
   ```

## API

| Метод  | Эндпоинт                       | Что делает                                                   |
|--------|--------------------------------|---------------------------------------------------------------|
| POST   | `/tasks`                       | Создать задачу. Дефолты: `IsCompleted=false`, `Priority=Medium` |
| GET    | `/tasks`                       | Вернуть все задачи (без пагинации)                            |
| PUT    | `/tasks/{id}/complete`         | Завершить задачу, заполнить `CompletedAt`, опубликовать событие в RMQ |
| DELETE | `/tasks/{id}`                  | Удалить задачу (жёстко)                                       |

`Priority` сериализуется и принимается как строка (`"Low" / "Medium" / "High"`) — глобальный `JsonStringEnumConverter`.

Готовые запросы есть в `TestForPromoOS/TestForPromoOS.http`.

## Архитектурные решения

### Сущность `TaskItem`

```csharp
public class TaskItem
{
    public Guid Id { get; set; }
    public required string Title { get; set; }    // NOT NULL, max 200
    public bool IsCompleted { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }  // null, пока задача не завершена
    public Priority Priority { get; set; }
}
```

Валидация `Title`: `IsNullOrWhiteSpace || Length > 200` → `400`. Плюс `IsRequired().HasMaxLength(200)` на уровне БД.

### `CompletedAt` заполняется строго в момент завершения

В POST поле не выставляется — остаётся `null`. В PUT `/complete`: `item.CompletedAt = DateTimeOffset.UtcNow` непосредственно перед `SaveChangesAsync`.

### Optimistic concurrency через `xmin`

В PostgreSQL нет нативного аналога SQL Server'овского `rowversion`. Самый «человеческий» путь — системный столбец `xmin`, который PG автоматически меняет на каждом `UPDATE`. В контексте это shadow-property:

```csharp
entity.Property<uint>("xmin")
    .HasColumnName("xmin")
    .HasColumnType("xid")
    .ValueGeneratedOnAddOrUpdate()
    .IsConcurrencyToken();
```

EF добавляет `WHERE xmin = @prev` в `UPDATE`. Если строку уже изменил параллельный запрос — `UPDATE` затронет 0 строк, EF бросит `DbUpdateConcurrencyException`, мы вернём `409`. Миграция `AddConcurrencyToken` пустая по содержанию — `xmin` уже есть в каждой таблице PG, DDL не нужен.

Двойная защита от повторного завершения:

- Быстрый путь: `if (item.IsCompleted) return Conflict()` — для последовательных ретраев.
- Гонка: `catch (DbUpdateConcurrencyException) → Conflict()` — для одновременных запросов.

### RabbitMQ publish

- Exchange `task.events` (topic, durable), routing key `task.completed`.
- Payload: `{ taskId, title, completedAt, priority }`, priority строкой.
- Сначала `SaveChangesAsync`, только потом `publisher.PublishTaskCompleted(...)` — если БД упала, сообщение не уйдёт.
- Внутри публикатора — fire-and-forget через `Task.Run`. Endpoint не ждёт ни коннекта, ни confirm'ов.
- Соединение долгоживущее, лениво создаётся под `SemaphoreSlim`. На каждом publish открывается дешёвый канал.
- Если RMQ недоступен — `LogWarning` и идём дальше. Задача всё равно считается завершённой.

### Graceful shutdown

`RabbitMqTaskEventPublisher` реализует `IHostedService`:

- Все in-flight `Task.Run` трекаются в списке под локом.
- На `StopAsync` ждём `Task.WhenAll(snapshot).WaitAsync(5s, ct)`. По таймауту — `LogWarning` со счётчиком брошенных.
- Флаг `_stopping` отсекает новые publish'и после начала остановки.
- В `DisposeAsync` явный `_connection.CloseAsync()` перед `DisposeAsync()` — сервер видит `connection.close-ok`, а не таймаут.

В DI один singleton играет роль и `ITaskEventPublisher`, и `IHostedService`:

```csharp
services.AddSingleton<RabbitMqTaskEventPublisher>();
services.AddSingleton<ITaskEventPublisher>(sp => sp.GetRequiredService<RabbitMqTaskEventPublisher>());
services.AddHostedService(sp => sp.GetRequiredService<RabbitMqTaskEventPublisher>());
```

## Тесты

```bash
dotnet test
```

`TestForPromoOS.Tests/CompleteTaskFlowTests.cs` — интеграционный тест через `WebApplicationFactory<Program>`:

1. `POST /tasks` → `201`.
2. `PUT /tasks/{id}/complete` → `200`.
3. Ассерт, что фейковый `ITaskEventPublisher` получил ровно одно сообщение с правильными `TaskId / Title / Priority / CompletedAt`.

Подмены в `TaskApiFactory`:

- EF Core переключён на InMemory (с фиксированным именем БД на инстанс фабрики, иначе POST и PUT получают разные базы). Чтобы переопределение работало, удаляются `DbContextOptions<AppDbContext>`, `DbContextOptions` и `IDbContextOptionsConfiguration<AppDbContext>`.
- `ITaskEventPublisher` подменяется на `FakeTaskEventPublisher` (потокобезопасный список + `WaitForFirst(timeout)`).

## Структура

```
TestForPromoOS/
├── Dockerfile
├── docker-compose.yml
├── TestForPromoOS.sln
├── TestForPromoOS/
│   ├── Data/
│   │   ├── AppDbContext.cs
│   │   └── Migrations/
│   ├── Models/        # TaskItem, Priority
│   ├── Messaging/     # ITaskEventPublisher, RabbitMqTaskEventPublisher, options, message
│   ├── Program.cs
│   └── appsettings.json
└── TestForPromoOS.Tests/
    └── CompleteTaskFlowTests.cs
```

## Чего нет (осознанно)

- Пагинации для `GET /tasks` — задача оговаривала «упростим».
- Consumer'а для RabbitMQ — нужен только publish.
- Outbox-паттерна — публикация после `SaveChangesAsync` без подтверждений, как и просили («fail silently, но с логом»).
- HTTPS внутри контейнера — `UseHttpsRedirection` без выставленного `ASPNETCORE_HTTPS_PORT` молчит и не редиректит.
- Тестов на параллельный 409 и на graceful shutdown с in-flight публикациями — поведение покрыто кодом, но дополнительные тесты не написаны.
