# Ultra MCP для вентзавода — архитектура

Один MCP-сервер, которым конструктор управляет из чата на естественном языке:
«в изделии ВКРН-5 поставь Ø ротора 500, поменяй разворот корпуса на Rd90, проверь
контуры, сделай габаритку и выгрузь DXF на резку». MCP делает это в открытом Inventor.

## Решение по базе

Форкаем **[bimwright/ipt-mcp](https://github.com/bimwright/ipt-mcp)** (Apache-2.0) — .NET add-in,
Inventor 2022–2027, связь через Named Pipe/TCP, команды на STA-потоке Inventor. Это самая
надёжная из открытых баз для цеха (не хрупкий `GetActiveObject`, а add-in внутри Inventor).

Что уже даёт база (58 инструментов, 13 toolset'ов): параметры, эскизы, фичи, iProperties,
масса, sketch/flat_pattern **export_dxf**, STEP/STL, сборки, interference/distance/**BOM**.

Чего **нет** и что делаем мы: **чертежи/габаритка (IDW)**, **разворот корпуса**, доменная
проверка контуров перед резкой, понятие **«изделие»** (каталог), пакетные операции.

## Два процесса (из ipt-mcp, не трогаем)

```
MCP-клиент (Claude) ──stdio──> Bimwright.Ipt.Server (.NET 8, без ссылки на Inventor)
                                        │ Named Pipe/TCP (NDJSON + токен)
                                        ▼
                               Bimwright.Ipt.Plugin.InvNN (add-in внутри Inventor)
                                        │ InventorStaDispatcher → STA-поток
                                        ▼
                                   Inventor API
```

Единицы централизованы на границе хендлера: мм↔см (÷10), градусы↔радианы.

## Точки расширения (проверено по исходнику)

| Слой | Куда добавляем |
|---|---|
| Серверный тул (без Inventor) | `src/server/Tools/VentTools.cs` — класс `[McpServerToolType]`, методы `[McpServerTool]`, зовут `_client.SendAsync("wire_command", params)` |
| Wire-хендлеры (Inventor API) | `src/shared/Handlers/Vent/*.cs` — `IInventorCommand { Name; IsReadOnly; Execute(ctx, JObject) }`, возвращают **DTO**, не COM-объекты |
| Регистрация | `src/shared/Plugin/InventorCommandRegistry.Vent.cs` (partial registrar) |
| Toolset | добавить `vent` в `ToolsetFilter` и `Program.ResolveToolTypesForRegistration` |

## Домен-слой `vent` (Domain-Specific Adapter)

Держим набор компактным (исследования: точность выбора инструмента падает после ~15 активных
тулзов) — тонкий доменный слой поверх низкоуровневых тулзов базы:

| Тул (`inventor_vent_*`) | RO? | Wire-команда → Inventor API | Статус |
|---|---|---|---|
| `list_products` | ✓ | скан каталога изделий (индекс) | новый |
| `open_product` | — | открыть .iam/.ipt изделия | обёртка над `open_document` |
| `set_casing_discharge` | — | `vent_set_discharge`: задать параметр разворота (Rd0/90/180/270 + правый/левый) → rebuild | новый |
| `make_gabarit` | — | `vent_make_drawing`: IDW, base+проекции, авто-габаритные размеры, штамп, export PDF/DXF | **новый, крупный** |
| `check_part` | ✓ | `vent_check_part`: замкнутость контура, min отступ отверстия от края, min перемычка, толщина листа | новый |
| `batch_flat_dxf` | — | по деталям изделия: flat pattern → DXF (job-статус, не синхронно) | новый |
| `bom_report` | ✓ | BOM изделия + материал/толщина/кол-во/площадь/длина реза | обёртка над `get_assembly_bom` + dxf-слой |

### «Разворот корпуса»
У центробежных вентиляторов это положение выхлопа улитки (Rd 0/90/135/180/225/270/315,
правое/левое исполнение). В модели это либо **пользовательский параметр** (`Rd`, `hand`),
либо поворот компонента улитки в сборке. Тул ставит параметр и делает `update` → проверяет
`health` фич → докладывает дельту габарита. Точное имя параметра берём из ваших моделей.

### «Габаритка» (главный кусок на C#)
Inventor Drawing API: `DrawingDocument` → `Sheet` → `DrawingView` (base + проекции) →
`GeneralDimension`/`RetrieveDimensions` для габаритов → заполнение `TitleBlock` из iProperties →
`DataIO`/`SaveAs` в PDF, при необходимости DXF. Делаем шаблон .idw под ваш штамп.

## Feedback loop (обязательно)
изменил параметр → `update` → `check_part`/`measure` → доклад дельты (габарит/объём/health) →
и только потом `batch_flat_dxf`/`make_gabarit`. Никаких «слепых» экспортов.

## Кросс-платформенный слой (работает уже сейчас, без Windows) — `dxf_tools/`
Python + ezdxf поверх готовых DXF. Не требует Inventor, тестируется на Mac:
- спецификация/каталог (материал, толщина, кол-во, габарит, площадь, длина реза);
- проверка перед резкой (замкнутость, отступы, диаметры отверстий);
- пакетная обработка сотен файлов.
Эта же логика проверок переносится в C#-хендлер `vent_check_part`.

Проверено на 250 реальных DXF (см. `spec_sample.csv`): 0 ошибок парсинга, 239/250 имён
распознано, суммарный рез ~1160 м. Известные доработки: сшивка контуров из дуг/линий в
петли; отступ отверстия считать от реального контура, а не от bounding box.

## План работ
1. **Сейчас, на Mac (Track A):** довести `dxf_tools` — сшивка контуров, обе схемы имён,
   отчёт-спецификация (CSV/HTML), пакетная правка DXF. Это самостоятельная ценность.
2. **На Windows (Track B):** форк ipt-mcp, сборка сервера (.NET 8) и add-in под вашу версию
   Inventor, дым-тест связи; затем toolset `vent`: `check_part`, `set_casing_discharge`,
   `batch_flat_dxf`, `bom_report`; в конце — `make_gabarit` (IDW-шаблон под ваш штамп).
3. Индекс «изделий» (каталог ВКРН / ВР 4-75 / ДН-26) → resolve имён из чата в файлы.
