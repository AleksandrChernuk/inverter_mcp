# AGENTS.md — shared/Plugin/ (наш вклад)

Здесь лежит только наш `InventorCommandRegistry.Vent.cs` — partial-регистратор toolset `vent`.
Остальные файлы `Plugin/` принадлежат базе ipt-mcp (не трогать без причины).

## Как регистрируется
База использует partial-registrar: `InventorCommandRegistry.Build()` вызывает `AddXxx(d, Add)`,
каждый домен реализует свой `static partial void AddXxx` в отдельном файле.
Наш файл реализует `AddVent` и вызывает `add(new ...Handler())` для каждого хендлера из `Handlers/Vent/`.

## Обязательная правка базы (иначе `AddVent` не вызовется)
В `InventorCommandRegistry.cs` (файл базы) добавить:
```csharp
AddVent(d, Add);                                   // в Build()
static partial void AddVent(Dictionary<string, IInventorCommand> d, Action<IInventorCommand> add); // объявление
```
Незаявленный `partial void` компилируется в no-op, поэтому объявление обязательно. См. `overlay/INTEGRATION.md`.

## Правило
Добавил хендлер в `Handlers/Vent/` → добавь `add(new XxxHandler())` сюда. Имя `Name` хендлера
должно совпадать с wire-командой, которую шлёт `VentTools`.
