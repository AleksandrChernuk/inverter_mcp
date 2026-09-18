# Как вживить toolset `vent` в форк ipt-mcp

Скопировать файлы оверлея в форк, затем сделать 3 точечные правки базовых файлов.

## 1. Скопировать новые файлы (без правок)

```
overlay/src/server/Tools/VentTools.cs                     -> src/server/Tools/
overlay/src/shared/Handlers/Vent/*.cs                     -> src/shared/Handlers/Vent/
overlay/src/shared/Plugin/InventorCommandRegistry.Vent.cs -> src/shared/Plugin/
overlay/tests/Bimwright.Ipt.Tests/*.cs                    -> tests/Bimwright.Ipt.Tests/
```

Тесты `Vent*Tests.cs` — Inventor-free (создают `VentTools` напрямую, поднимают фейковый named-pipe
плагин и/или работают с файловой системой), поэтому идут в `dotnet test` вместе с базовыми. Тест-проект
собирает свою папку по умолчанию, отдельной правки csproj не нужно.

`src/shared/**/*.cs` уже собирается плагином по глобу — новые хендлеры подхватятся автоматически.
`VentTools.cs` лежит в `src/server/Tools/` — но серверный csproj включает файлы явно, поэтому
добавьте его в `Bimwright.Ipt.Server.csproj` рядом с другими `Tools/*.cs`, если там перечисление
явное (проверьте — в текущей версии это `<Compile Include="Tools\*.cs" />` по маске, тогда правка не нужна).

## 2. Три правки базовых файлов

### `src/server/ToolsetFilter.cs`
Добавить `"vent"` в три массива:
- `KnownToolsets` — добавить `"vent"`.
- `DefaultOn` — добавить `"vent"` (чтобы toolset был включён по умолчанию).
- `WriteCapable` — добавить `"vent"` (в нём есть мутирующие тулзы; под `--read-only` они скроются,
  а read-only `list_products`/`check_part`/`bom_report` останутся, т.к. фильтр режет по toolset —
  см. примечание ниже).

> Примечание: базовый `ToolsetFilter` режет целиком toolset. Если нужно, чтобы read-only тулзы `vent`
> оставались под `--read-only`, вынесите их в отдельный toolset `vent_query` (по образцу
> `assembly`/`assembly_query`). Для старта достаточно одного `vent`.

### `src/server/Program.cs`
В `RegisterToolsets` добавить строку:
```csharp
if (toolType == typeof(VentTools)) return mcp.WithTools<VentTools>();
```
В `ResolveToolTypesForRegistration` добавить:
```csharp
Add("vent", typeof(VentTools));
```

### `src/shared/Plugin/InventorCommandRegistry.cs`
В методе `Build(...)` добавить вызов:
```csharp
AddVent(d, Add);          // vent-factory toolset
```
и рядом с остальными — объявление partial:
```csharp
static partial void AddVent(Dictionary<string, IInventorCommand> d, Action<IInventorCommand> add);
```

### `tests/Bimwright.Ipt.Tests/RegistrationCountTests.cs` — сделать vent-независимым
Этот базовый guard замораживает поверхность **базы** (59/58 тулз). После включения `vent` реестр
отдаёт 59+N тулз, и три счётных теста падают. Не меняйте число — сделайте счётчик vent-независимым,
чтобы будущие vent-тулзы его не трогали (сама vent-поверхность охраняется `VentToolSurfaceTests`):
добавьте хелпер и используйте его в трёх тестах (`All_toolsets…59`, `The_59_tools_match…`, `Default…58`):
```csharp
private static string[] BaseToolNames(InventorMcpConfig cfg)
    => ToolNames(cfg).Where(n => !n.StartsWith("inventor_vent_", StringComparison.Ordinal)).ToArray();
```
Проверку на коллизии имён оставьте по ПОЛНОМУ набору (`ToolNames`), чтобы vent не конфликтовал с базой.

### `src/shared/Infrastructure/CommandDispatcher.cs` — silent-режим (рекомендуется)
Чтобы write-команды (`clone_recode`, `execute_plan`, `batch_*`) не упирались в интерактивные
диалоги Inventor («найти/открыть ссылку»), в `Dispatch(...)` для НЕ read-only команд включайте
`Application.SilentOperation = true` на время выполнения и восстанавливайте после. Делается через
reflection по `ctx.Application`, чтобы файл остался API-free (компилируется без Inventor interop):
```csharp
// !cmd.IsReadOnly: prev = SilentOperation; SilentOperation = true;
var app = ctx.Application;
var silentProp = (!cmd.IsReadOnly && app != null) ? app.GetType().GetProperty("SilentOperation") : null;
object? prevSilent = null;
if (silentProp is { CanRead: true, CanWrite: true }) { prevSilent = silentProp.GetValue(app); silentProp.SetValue(app, true); }
try { /* ... cmd.Execute(...) ... */ }
finally { if (silentProp != null && app != null) silentProp.SetValue(app, prevSilent); }
```
> ⚠️ Silent-режим подавляет диалоги, но НЕ заменяет корректное разрешение ссылок. Для реальной
> автономной работы нужен активный проект Inventor (`.ipj`) с правильными Workspace/Library-путями —
> иначе ссылки у изделий, выдернутых из архива, не резолвятся, и Pack-and-Go/open будут неполными.

## 3. Собрать и проверить (Inventor 2026 → .NET 8)
Не собирайте всё решение `IptMcp.sln` целиком — в нём есть `plugin-inv27` на .NET 10.
На машине с одним .NET 8 SDK стройте только нужное:
```powershell
dotnet build src/server/Bimwright.Ipt.Server.csproj -c Release
dotnet build src/plugin-inv26/Bimwright.Ipt.Plugin.Inv26.csproj -c Release
dotnet test  tests/Bimwright.Ipt.Tests/Bimwright.Ipt.Tests.csproj   # Inventor-free, должны быть зелёными
```
`inventor_vent_*` появятся в `tools/list` (27 тулзов в текущем overlay; `RegistrationCountTests`
сделан vent-независимым — см. правку выше, отдельно число править не нужно).

Для автономного smoke-test сначала вызовите `inventor_vent_execute_plan` с `dryRun=true`, затем на
копии модели — с `dryRun=false`, заведомо failing `physical_bounds` и убедитесь, что ответ содержит
`rolled_back=true`, `snapshot_restore.pass=true`, а выражения параметров и материалы активного документа
и вложенных occurrences восстановились. `Transaction` Inventor относится к одному `Document`, поэтому
executor дополнительно снимает bounded snapshot driving-параметров/материалов дерева ссылок и явно
восстанавливает его после abort. Только после passing checks отдельно вызывайте
`inventor_vent_save_product(productRoot=...)`; `execute_plan` намеренно не пишет файл на диск.
`save_product` вызывает `Document.Save2` с точным списком dirty dependencies и блокирует любую dirty
ссылку вне product root — обычного `Document.Save()` для сборки здесь недостаточно.
Product Job v2 может передать до 256 recipe bindings; executor принимает до 64 checks
(до 32 общих + до 32 mounting-pattern checks).

Для `inventor_vent_clone_recode_product` сначала выполните `dryRun=true` и сохраните file map в журнале.
Windows acceptance должен подтвердить: copied `.iam/.idw` ссылаются только на destination tree, shared
Content Center ссылки остались внешними, исходный шаблон не сохранён/не изменён, `INCOMPLETE.json` отсутствует.
Команда отказывает, если исходный document уже открыт с unsaved changes, и закрывает только документы,
которые открыла сама.

`vent_make_gabarit` теперь умеет retrieved + associative overall width/height dimensions, explicit
section/detail views, Parts List, balloons, hole table и техтребования. Координаты recipe подаются в мм,
но должны быть откалиброваны на заводском шаблоне; `minimum_dimensions`/`minimum_balloons` превращают
неполный drawing в FAIL.

`vent_batch_flat_dxf` рекурсивно обходит вложенные occurrences, считает количество одинаковых деталей,
и формирует проверяемое имя из thickness/material/quantity/Part Number/Description. Если имя материала
Inventor не совпадает с заводским (`Ст3`, `09Г2С` и т. п.), передайте `materialAliases`; коллизия имён
DXF является ошибкой, а не перезаписью.
Отсутствующий flat pattern также является ошибкой; `createMissingFlatPatterns=true` — явный approved
opt-in, который создаёт, rebuild-ит и сохраняет развёртку перед экспортом.
