# AGENTS.md — Handlers/Vent/

Wire-хендлеры toolset `vent`. Трогают Inventor API → компилируются только в add-in
(под `#if INVENTOR2022 || ... #endif`), собираются на Windows.

## Контракт хендлера
```csharp
public sealed class XxxHandler : HandlerBase, IInventorCommand {
    public string Name => "vent_xxx";     // snake_case, без префикса
    public bool IsReadOnly => true|false; // false = мутирует модель
    public InventorCommandResult Execute(InventorCommandContext ctx, JObject p) {
        var app = (Application)ctx.Application!;   // каст COM
        ... return Ok(ctx, dto) | Fail(ctx, InventorErrorCodes.X, "msg");
    }
}
```
Регистрируется в `../../Plugin/InventorCommandRegistry.Vent.cs` (`add(new XxxHandler())`).

## Файлы
- `VentSupport.cs` — общие helpers: `BBoxMm`, `FindParameter`, `MaterialName`, `ThicknessMm`,
  `DischargeParamCandidates` (кандидаты имени параметра разворота — правь под свои модели).
- `OpenProductHandler` / `SetDischargeHandler` / `CheckPartHandler` / `MakeDrawingHandler` /
  `BatchFlatDxfHandler` / `BomReportHandler`.

## Правила
- Единицы: Inventor внутри в см/радианах. Наружу — мм (×10) и градусы. Конверсия здесь.
- Только DTO наружу (`JObject`/анонимные объекты). Никогда — COM-объекты.
- `ActiveDocument` бери в try/catch; проверяй тип (`PartDocument`/`AssemblyDocument`/`SheetMetalComponentDefinition`).
- Экспортные пути — через `ExportPathPolicy.TryRejectPath`.
- Требует проверки на Windows: посадка видов в `MakeDrawingHandler`, имя параметра в `SetDischargeHandler`.
