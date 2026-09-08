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

## Changelog
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
