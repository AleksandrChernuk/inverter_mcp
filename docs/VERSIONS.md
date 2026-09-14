# Версии и совместимость

## Архитектура (кратко)
Форк `bimwright/ipt-mcp`: MCP-сервер (.NET, без ссылки на Inventor) ↔ per-version add-in внутри
Inventor, связь Named Pipe (2025–2027) / TCP (2022–2024), команды на STA-потоке. Наш слой — toolset
`vent` (домен вентзавода) + кросс-платформенный `dxf_tools` (Python). Подробно — `ARCHITECTURE.md`.

## База
| | Значение |
|---|---|
| Репозиторий базы | https://github.com/bimwright/ipt-mcp |
| Зафиксированный commit | `d539a2ee7295747c7ef3b44a64aa870e8889deac` (2026-08-28) |
| Лицензия базы | Apache-2.0 |
| MCP SDK базы | `ModelContextProtocol` 1.1.0 |
| Инструментов в базе | 58 (13 toolset'ов) + наш toolset `vent` (22) |

## Совместимость Inventor / TFM
| Год Inventor | TFM add-in | Транспорт |
|---|---|---|
| 2022 / 2023 / 2024 | `net48` | TCP |
| 2025 / 2026 | `net8.0-windows7.0` | Named Pipe |
| 2027 | `net10.0-windows7.0` | Named Pipe |

Требуется .NET SDK: 8 (2025/2026), 10 (2027). Сборка/запуск C#-части — только Windows с Inventor.

## Кросс-платформенный слой `dxf_tools`
| | Значение |
|---|---|
| Python | 3.12+ (разработка велась на 3.14) |
| ezdxf | >= 1.4 (проверено 1.4.4) |
| Платформа | Mac / Windows / Linux (Inventor не нужен) |
| Формат входных DXF | AC1032 (AutoCAD 2018), единицы мм (INSUNITS=4) |

## Наш toolset `vent` (v0.4.0)
22 доменных тула: каталог/clone+recode/open/save-as/save-product, активация .ipj, разворот,
проверки/габаритка/DXF/PDF/BOM, inspect model/sketch/parametrization, drive dimension/component/constraint,
run iLogic и транзакционный `execute_plan`.
Соответствие тул↔wire — `overlay/AGENTS.md`.

## Зафиксированные версии зависимостей (точные пины, без ^/>=)
- Python: `ezdxf==1.4.4`, `openpyxl==3.1.5` (`dxf_tools/requirements.txt`).
- Node/TS: `@modelcontextprotocol/sdk 1.30.0`, `zod 3.25.76`, `typescript 5.6.3`, `tsx 4.19.2`,
  `@types/node 22.10.2` (`phase2/*/package.json`). Выбраны стабильные версии, не bleeding-edge.
- CI: Python 3.12, Node 20 (`.github/workflows/ci.yml`).

## CI (GitHub Actions)
`.github/workflows/ci.yml` — держим зелёным и минимальным:
- `dxf-tools` — ставит пиненые зависимости, гоняет `dxf_tools/selftest.py` (генерит DXF в памяти,
  проверяет analyze + spec_xlsx, без внешних файлов).
- `phase2-node` — frozen pnpm install, TypeScript build + connector tests, gateway tests + syntax check.
C#/Inventor-часть в CI не собирается (компилируется только в форке ipt-mcp на Windows).

## Changelog
### v0.4.0 — 2026-09-11
- `inventor_vent_activate_project` + авто-активация `.ipj` в `vent_open_product` (когда нет открытых
  документов): Workspace + Library-пути (W:) резолвятся, Inventor не просит «найти/открыть».
- Guardrail copy-before-resize на `drive_dimension` / `set_component_parameter` / `run_ilogic`:
  отказ менять деталь в защищённом/мастер-расположении (`ExportPathPolicy`), override `allow_in_place`.
- Silent-mode для write-операций (add-in подавляет диалоги на время не-read-only команд).
- Параметризация (роадмап `docs/PARAMETRIZATION_PLAN.md`): Фаза 0 `inventor_vent_inspect_parametrization`
  (разведка iPart/iAssembly, iLogic-правил, польз. параметров) + Фаза 1 `inventor_vent_run_ilogic`
  (ключевой параметр → прогон правила → bbox/масса). iLogic — позднее связывание, без ссылки на сборку.
- Находка: файлы `D:\Catalog` смешаны 2021/2026; ВКР 7,1 = 2026 (не откр. на 2021), ВЦ 4-75 3,15М и
  Колесо 1в1 = 2021 (см. `docs/CATALOG_MAP.md`). Цель платформы — Inventor 2026 (plugin-inv26).
- CI: pnpm ставится напрямую версией 8.10.0 (обход `ERR_PNPM_BAD_PM_VERSION` от corepack) — CI зелёный.
- Собрано под plugin-inv21, plugin-inv26 и server (0 ошибок).

### v0.3.0 — 2026-09-10
- Добавлен Product Job v2: versioned family recipe, детерминированные линейные формулы,
  до 512 типизированных шагов создания детали/сборки/метизов, formula-driven construction values,
  job-root path templates и checkpoint каждой стадии.
- `inventor_vent_clone_recode_product`: dry-run file map, native `SaveCopyAs`, рекурсивный
  `FileDescriptor.ReplaceReference`, перекодирование filenames/Part Number/Stock Number/Description/Title,
  external shared refs не копируются, сбой оставляет `INCOMPLETE.json`.
- `inventor_vent_check_mounting_pattern`: проверка отверстий двигателя/фланца по реальным центрам,
  диаметру разболтовки, числу отверстий и равномерному угловому шагу.
- `vent_make_gabarit` расширен: auto-fit, retrieved + associative overall dimensions, projected/section/detail views,
  Parts List, balloons, hole table и нумерованные техтребования с минимальными release-gates.
- `inventor_vent_batch_pdf_drawings` экспортирует всё дерево IDW/DWG в PDF.
- `inventor_vent_batch_flat_dxf` обходит вложенные узлы, считает BOM quantity и кодирует в filename
  thickness/material/quantity/Part Number; material aliases задаются recipe, коллизии и расхождение
  exact DXF export manifest блокируют выпуск.
- `dxf_tools/release.py` объединяет deep DXF checks, структуру/коды/PDF, CSV/XLSX металла,
  SHA-256 manifest и release digest; gateway запускает его без shell только под export root.
- Второй gate `inventor_approve_release`: точный digest + approved_by, затем webhook в цех;
  до успешной отправки задача не получает статус `released`.
- Connector tests: 23; gateway tests: 5; Python selftest включает полный release package.
- `vent_execute_plan` больше не полагается на transaction активной сборки для rollback вложенных `.ipt`:
  перед мутациями снимается bounded snapshot model/user expressions и part material всего referenced-tree;
  при FAIL snapshot явно восстанавливается и rebuild-ится, а любой restore error делает `rolled_back=false`.
- Mounting-pattern check перенесён внутрь той же rollback-границы. Новый `vent_save_product` после PASS
  вызывает bounded `Document.Save2` для active + dirty dependencies под product root и блокирует dirty external refs.

### v0.2.0 — 2026-09-10
- Phase 2 стал рабочим автономным контуром: строгий `JobSpec v1`, prepare/digest/approval,
  persistent checkpoint, evidence ledger и JSONL-аудит.
- Новый `inventor_vent_execute_plan`: до 32 ограниченных mutations в транзакции Inventor,
  `Update2(false)`, objective checks и `Transaction.Abort()` при любом FAIL; save отдельно после PASS.
- Проверки: feature health, sheet-metal readiness, constraint health, interference, min distance,
  mass/bounding-box limits. `inspect_model` теперь возвращает feature `HealthStatus`.
- Коннектор выдаёт 10 курированных inspect/job tools вместо россыпи низкоуровневых write-команд;
  потеря транспорта во время мутации даёт `uncertain` без автоматического retry.
- Gateway: безопасный allowlist по умолчанию, constant-time bearer, production fail-closed, reconnect,
  rate limit, body limit, аудит и корректная обработка вложенного `ok:false`.
- Добавлено 16 unit/contract tests (11 connector + 5 gateway), точные `pnpm-lock.yaml`; MCP SDK обновлён
  до 1.30.0, Zod до совместимого 3.25.76.
- Архитектурные источники и Windows acceptance metrics: `docs/AUTONOMOUS_CONNECTOR.md`.

### v0.1.5 — 2026-09-08
- Все зависимости запинены точными версиями (убраны `^`/`>=`).
- Добавлен чистый CI (GitHub Actions): Python selftest + gateway syntax. `node_modules/`, `dist/` в .gitignore.

### v0.1.4 — 2026-09-08
- `dxf_tools/spec_xlsx.py` + `materials.json` — XLSX-спецификация по правилам учёта металла КВЗ.
  Проверено на реальном `ВКР №7,1/Специфікація.xlsx`: итог массы 101.66 кг воспроизведён точно,
  +1% сварочной проволоки, коды склада (`/*NNNNNN`) подставляются. `openpyxl>=3.1` в requirements.

### v0.1.3 — 2026-09-08
- Фаза 2 (скелеты): `phase2/gateway/` (Node HTTP поверх stdio-MCP) + `phase2/connectors-inventor/`
  (TS MCP-коннектор для kvz-ai, read/write-гейтинг). Шлюз прошёл `node --check`.
- `docs/WORKFLOW.md` — реальный маршрут конструктора (ВКР 7,1 ← ВКР 6,3, Pack-and-Go) → тулзы.
- Вывод из реальной папки ВКР №7,1: есть `.ipj` → клон папки рабочий (относительные ссылки),
  надо активировать новый `.ipj`. Кодировка `ВКР 7,1 УЗ.ДЕ.ПОЗ`. Новые тулзы в TODO
  (recode_product, check_product, batch_pdf, XLSX-спецификация).

### v0.1.2 — 2026-09-08
- Создание изделий: `inventor_vent_new_product` (server-side клон папки-шаблона) +
  `inventor_vent_save_part_as` (wire, SaveAs детали). Toolset `vent` теперь 9 тулзов.
- Зафиксирована фазность: Фаза 1 — напрямую через Claude Desktop; Фаза 2 — kvz-ai, топология B
  (worker отдельно, сетевой шлюз на Windows + TS-коннектор `connectors/inventor/`).
- Каveat клона сборки с абсолютными ссылками (Pack-and-Go) — доводится вживую на Windows.

### v0.1.1 — 2026-09-08
- Профиль **read-only по умолчанию** в примере конфига + отдельный edit-профиль.
- `setup.ps1`: `CatalogRoot` обязателен, запрет корня диска.
- Добавлен `docs/KVZ_AI_INTEGRATION.md` (подключение как коннектор kvz-ai, роли, approval-gate, топология).
- Цель Inventor зафиксирована: **2026** (.NET 8, Named Pipe, plugin-inv26).

### v0.1.0 — 2026-09-08
- Выбрана база ipt-mcp; зафиксирован commit.
- `dxf_tools`: `analyze.py`, `batch.py`, `compare.py` — проверено на 250 реальных DXF
  (0 ошибок парсинга, 239/250 имён распознано, рез ~1160 м; self-compare PASS, cross DIFF).
- `overlay`: C#-скелет toolset `vent` (7 тулзов, 6 wire-хендлеров) + INTEGRATION.md.
- `scripts/windows`: `setup.ps1` + пример конфига MCP-клиента.
- Документация: ARCHITECTURE / OPERATING / VERSIONS, AGENTS.md по всем папкам.

## Требует проверки на Windows (следующая живая сессия)
- Имя параметра «разворота корпуса» в реальных моделях (`VentSupport.DischargeParamCandidates`).
- `CloneRecodeProductHandler`: абсолютные/вложенные ссылки `.iam/.idw`, восстановление источника,
  внешний Content Center, collision и `INCOMPLETE` recovery.
- `MakeDrawingHandler`: заводской штамп, section/detail coordinates, Parts List/balloon/hole table styles.
- Регрессионный цикл: `batch_flat_dxf` из .ipt → `dxf_tools/compare.py` с эталонными DXF каталога.
- Поправить `RegistrationCountTests` базы под +22 тулза `vent`.
- `vent_execute_plan`: Windows smoke-test двойного rollback (Transaction + snapshot) для изменения параметра
  и материала вложенного occurrence в реальной `.iam`, включая проверку исходного dirty-state.
