# AGENTS.md — server/Tools/

Серверные тулзы `vent` (класс `VentTools`). Компилируются БЕЗ ссылки на Inventor —
сервер собирается на любой машине (но на Mac нет `dotnet`, так что сборка всё равно на Windows).

## Паттерн
```csharp
[McpServerToolType] public sealed class VentTools {
  private readonly PluginClient _client;
  [McpServerTool(Name="inventor_vent_xxx"), Description("... мм/градусы ...")]
  public Task<string> Xxx(args, CancellationToken ct=default) => Call("vent_xxx", new JObject{...}, ct);
}
```
- `Call` → `_client.SendAsync(wire, params, ct)` → JSON-строка (как в `ExportTools`).
- `list_products` — исключение: чистый server-side (скан ФС), без `SendAsync`.

## Правила
- Имя тула — `inventor_vent_*`. Description пиши плотно и с единицами — по нему модель выбирает тул.
- Держи публичный набор курированным; автономный connector должен выбирать workflow tools, а не показывать
  планировщику все низкоуровневые write-команды одновременно.
- Добавил тул с wire-командой → заведи хендлер в `../../shared/Handlers/Vent/` и зарегистрируй.
- Не забудь правки `Program.cs` и `ToolsetFilter.cs` (см. `overlay/INTEGRATION.md`).
- Автономные изменения объединяй через `inventor_vent_execute_plan`; не добавляй в коннектор новый
  низкоуровневый write-тул без объективного post-check и approval-сценария.
