Создал модели, миграции, эндпоинты и запросы в TestForPromoOS.http 
ConcurrencyToken для xmin (RowVersion для PostgreSQL)
RabbitMQ publish. Секция RabbitMq в appsettings.json.
Graceful shutdown.  DisposeAsync вызывает явный _connection.CloseAsync() перед DisposeAsync
Обернул в докер + healthcheck'и + автомиграция при старте
Тест (интеграционный) в TestForPromoOS.Tests. Тест Create_then_complete_publishes_task_completed_event: POST → 201, PUT → 200, ассерт что фейк получил ровно одно сообщение с правильными TaskId/Title/Priority/CompletedAt. Проходит

Title и пустые строки — Program.cs:44:                                                                                                                                        
  if (string.IsNullOrWhiteSpace(req.Title) || req.Title.Length > 200)                                                                                                              
      return Results.BadRequest("Title is required and must be 1..200 chars.");                                                                                                    
  IsNullOrWhiteSpace ловит null, "", "   ", табы и переводы строк. Плюс на DB-уровне IsRequired().HasMaxLength(200) в AppDbContext.cs:14-16.                                       
                                                                                                                                                                                   
CompletedAt при создании — Program.cs:47-57:
  var item = new TaskItem
  {
      Id = Guid.NewGuid(),
      Title = req.Title,
      IsCompleted = false,
      CreatedAt = DateTimeOffset.UtcNow,
      Priority = req.Priority
  };
  Заполняется только в /complete (Program.cs:74):
  item.CompletedAt = DateTimeOffset.UtcNow;

  RabbitMQ публикуется до SaveChanges, последовательность правильная:
  item.IsCompleted = true;
  item.CompletedAt = DateTimeOffset.UtcNow;

  try { await db.SaveChangesAsync(); }
  catch (DbUpdateConcurrencyException) { return Results.Conflict(...); }

  publisher.PublishTaskCompleted(...);   // только после успешного коммита
  Если БД упадёт — SaveChangesAsync бросит, исключение пробросится наверх (или вернётся 409), publisher.PublishTaskCompleted не будет вызван. Сообщение зря не уйдёт.

  4. 409 при конкурентных вызовах — двойная защита:
  - Быстрый путь: if (item.IsCompleted) return Results.Conflict(...) — ловит последовательные ретраи.
  - Гонка: xmin как concurrency token → второй запрос на UPDATE бьётся о изменённый xmin → DbUpdateConcurrencyException → 409
