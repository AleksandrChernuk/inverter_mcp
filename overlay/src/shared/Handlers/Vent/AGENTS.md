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

`CloneRecodeProductHandler` — Pack-and-Go/recode граница: dry-run обязателен в оркестраторе,
`SaveCopyAs` сохраняет InternalName, `ReplaceReference` перепривязывает только копии, shared refs вне
`source_root` не трогаются. Не заменять обычным `File.Copy` для native документов.

`CheckMountingPatternHandler` — отверстия двигателя/фланца по HoleFeature centers; `SaveProductHandler` —
bounded `Save2` активного изделия и dirty dependencies только под approved product root; `BatchPdfDrawingsHandler`
— пакетный PDF. `MakeDrawingHandler` принимает явные section/detail recipes и проверяемые минимумы
dimensions/balloons/hole-table; координаты листа наружу всегда в мм.

`ExecutePlanHandler` — rollback-граница автономной задачи: принимает только ограниченные
mutations/checks, делает `Update2(false)`, при любом FAIL вызывает `Transaction.Abort()` и явно
восстанавливает bounded snapshot model/user parameters + material referenced-tree. Mounting pattern
проверяется внутри этой границы. При PASS — `Transaction.End()` без сохранения на диск; затем
`SaveProductHandler.Save2` сохраняет проверенное дерево. Не переносить save/export внутрь transaction.

## Правила
- Единицы: Inventor внутри в см/радианах. Наружу — мм (×10) и градусы. Конверсия здесь.
- Только DTO наружу (`JObject`/анонимные объекты). Никогда — COM-объекты.
- `ActiveDocument` бери в try/catch; проверяй тип (`PartDocument`/`AssemblyDocument`/`SheetMetalComponentDefinition`).
- Экспортные пути — через `ExportPathPolicy.TryRejectPath`.
- Требует проверки на Windows: посадка видов в `MakeDrawingHandler`, имя параметра в `SetDischargeHandler`.
- Требует проверки на Windows: двойной rollback `ExecutePlanHandler` и bounded Save2 дерева изделия.
