# AGENTS.md — overlay/

C#-исходники нашего toolset `vent`. Копируются в форк `bimwright/ipt-mcp` поверх `src/`.
Собираются ТОЛЬКО на Windows с Inventor. На Mac — только чтение/правка.

## Что где
- `src/server/Tools/VentTools.cs` — серверные тулзы `inventor_vent_*` (без ссылки на Inventor,
  зовут `_client.SendAsync(wire_command, params)`). `list_products` — server-side (скан ФС).
- `src/shared/Handlers/Vent/*.cs` — wire-хендлеры (трогают Inventor API), возвращают DTO:
  `OpenProductHandler`, `SetDischargeHandler`, `CheckPartHandler`, `MakeDrawingHandler`,
  `CloneRecodeProductHandler`, `CheckMountingPatternHandler`, `BatchFlatDxfHandler`,
  `BatchPdfDrawingsHandler`, `SaveProductHandler`, `BomReportHandler`, inspect/drive assembly handlers,
  `ActivateProjectHandler` (активация .ipj), `InspectParametrizationHandler` (разведка iPart/iLogic),
  `RunILogicHandler` (прогон iLogic-правил), транзакционный `ExecutePlanHandler`, общий `VentSupport`.
- `src/shared/Plugin/InventorCommandRegistry.Vent.cs` — partial registrar `AddVent`.
- `INTEGRATION.md` — 3 правки базовых файлов (ToolsetFilter, Program, InventorCommandRegistry).

## Соответствие тул ↔ wire-команда
| Тул | Wire | Мутирует? |
|---|---|---|
| `inventor_vent_list_products` | (нет, server-side) | нет |
| `inventor_vent_new_product` | (нет, server-side — копия папки-шаблона) | да (создаёт файлы) |
| `inventor_vent_save_part_as` | `vent_save_part_as` | да (создаёт файл) |
| `inventor_vent_save_product` | `vent_save_product` | да (Save2 активного изделия и dirty dependencies) |
| `inventor_vent_open_product` | `vent_open_product` | да (авто-активирует .ipj, если нет откр. док.) |
| `inventor_vent_activate_project` | `vent_activate_project` | да (меняет активный проект .ipj) |
| `inventor_vent_clone_recode_product` | `vent_clone_recode_product` | да (создаёт изолированное дерево) |
| `inventor_vent_set_casing_discharge` | `vent_set_discharge` | да |
| `inventor_vent_check_part` | `vent_check_part` | нет |
| `inventor_vent_check_mounting_pattern` | `vent_check_mounting_pattern` | нет |
| `inventor_vent_make_gabarit` | `vent_make_drawing` | да |
| `inventor_vent_batch_flat_dxf` | `vent_batch_flat_dxf` | да |
| `inventor_vent_batch_pdf_drawings` | `vent_batch_pdf_drawings` | да |
| `inventor_vent_bom_report` | `vent_bom_report` | нет |
| `inventor_vent_inspect_model` | `vent_inspect_model` | нет |
| `inventor_vent_inspect_parametrization` | `vent_inspect_parametrization` | нет |
| `inventor_vent_drive_dimension` | `vent_drive_dimension` | да |
| `inventor_vent_run_ilogic` | `vent_run_ilogic` | да (ключ-параметр + прогон iLogic-правила) |
| `inventor_vent_set_component_parameter` | `vent_set_component_parameter` | да |
| `inventor_vent_set_constraint` | `vent_set_constraint` | да |
| `inventor_vent_inspect_sketch` | `vent_inspect_sketch` | нет |
| `inventor_vent_execute_plan` | `vent_execute_plan` | да (transaction + referenced-tree snapshot; dry-run не мутирует) |

## Правила для агента
- Стиль строго как в базе: `HandlerBase` (`Ok`/`Fail`), `IInventorCommand { Name; IsReadOnly; Execute }`,
  `#if INVENTOR2022 || ... #endif` вокруг файлов, трогающих `Inventor.*`.
- Единицы: см→мм ×10 на выходе; градусы→«N deg» в выражении параметра.
- НЕ возвращай COM-объекты — только `JObject`/DTO.
- Требует проверки на Windows: `MakeDrawingHandler` (посадка видов, штамп, шаблон .idw),
  имя параметра разворота в `SetDischargeHandler` (`VentSupport.DischargeParamCandidates`).
- Добавил/переименовал тул — обнови INTEGRATION.md, `../docs/VERSIONS.md` и root AGENTS.md.
