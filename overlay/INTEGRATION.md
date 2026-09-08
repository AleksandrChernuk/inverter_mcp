# Как вживить toolset `vent` в форк ipt-mcp

Скопировать файлы оверлея в форк, затем сделать 3 точечные правки базовых файлов.

## 1. Скопировать новые файлы (без правок)

```
overlay/src/server/Tools/VentTools.cs                     -> src/server/Tools/
overlay/src/shared/Handlers/Vent/*.cs                     -> src/shared/Handlers/Vent/
overlay/src/shared/Plugin/InventorCommandRegistry.Vent.cs -> src/shared/Plugin/
```

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

## 3. Собрать и проверить (Inventor 2026 → .NET 8)
Не собирайте всё решение `IptMcp.sln` целиком — в нём есть `plugin-inv27` на .NET 10.
На машине с одним .NET 8 SDK стройте только нужное:
```powershell
dotnet build src/server/Bimwright.Ipt.Server.csproj -c Release
dotnet build src/plugin-inv26/Bimwright.Ipt.Plugin.Inv26.csproj -c Release
dotnet test  tests/Bimwright.Ipt.Tests/Bimwright.Ipt.Tests.csproj   # Inventor-free, должны быть зелёными
```
`inventor_vent_*` появятся в `tools/list` (проверьте `RegistrationCountTests` — счётчик тулзов вырастет,
поправьте ожидаемое число или исключите `vent` из этого теста).
