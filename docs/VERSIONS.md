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
| Инструментов в базе | 58 (13 toolset'ов) + наш toolset `vent` (7) |

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

## Наш toolset `vent` (v0.1.0)
`list_products`, `open_product`, `set_casing_discharge`, `check_part`, `make_gabarit`,
`batch_flat_dxf`, `bom_report`. Соответствие тул↔wire — `overlay/AGENTS.md`.

## Зафиксированные версии зависимостей (точные пины, без ^/>=)
- Python: `ezdxf==1.4.4`, `openpyxl==3.1.5` (`dxf_tools/requirements.txt`).
- Node/TS: `@modelcontextprotocol/sdk 1.12.0`, `zod 3.23.8`, `typescript 5.6.3`, `tsx 4.19.2`,
  `@types/node 22.10.2` (`phase2/*/package.json`). Выбраны стабильные версии, не bleeding-edge.
- CI: Python 3.12, Node 20 (`.github/workflows/ci.yml`).

## CI (GitHub Actions)
`.github/workflows/ci.yml` — держим зелёным и минимальным:
- `dxf-tools` — ставит пиненые зависимости, гоняет `dxf_tools/selftest.py` (генерит DXF в памяти,
  проверяет analyze + spec_xlsx, без внешних файлов).
- `gateway-syntax` — `node --check` шлюза.
C#/Inventor-часть в CI не собирается (компилируется только в форке ipt-mcp на Windows).

## Changelog
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
- `MakeDrawingHandler`: посадка видов, штамп, .idw-шаблон, авто-габаритные размеры.
- Регрессионный цикл: `batch_flat_dxf` из .ipt → `dxf_tools/compare.py` с эталонными DXF каталога.
- Поправить `RegistrationCountTests` базы под +7 тулзов `vent`.
