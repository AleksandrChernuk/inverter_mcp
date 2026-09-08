# TODO — ultra Inventor MCP (вентзавод)

Цель: Inventor 2026. Фаза 1 — напрямую через Claude Desktop; Фаза 2 — через kvz-ai (топология B).
Подробности: `docs/ARCHITECTURE.md`, `docs/OPERATING.md`, `docs/KVZ_AI_INTEGRATION.md`, `docs/VERSIONS.md`.

## ✅ Сделано (на Mac, запушено в main)
- [x] Выбрана база `bimwright/ipt-mcp`, зафиксирован commit; разобраны точки расширения.
- [x] `overlay/` — toolset `vent` (9 тулзов): list/new/save_part_as/open/set_discharge/check_part/
      make_gabarit/batch_flat_dxf/bom_report + хендлеры + регистратор + INTEGRATION.md.
- [x] `dxf_tools/` — analyze / batch / compare (Python+ezdxf), проверено на 250 DXF.
- [x] `scripts/windows/setup.ps1` + пример конфига (read-only профиль по умолчанию + edit).
- [x] Документация + AGENTS.md по всем папкам + файлы памяти.

## ▶ Фаза 1 — запуск через Claude app (на цеховой Windows-машине)
- [ ] `git clone` репозитория на Windows; поставить .NET 8 SDK.
- [ ] `setup.ps1 -InventorYear 2026 -Install -CatalogRoot "D:\Catalog" -ExportRoot "D:\DXF_OUT"`.
- [ ] Сделать 3 правки базы из `overlay/INTEGRATION.md` (ToolsetFilter, Program, InventorCommandRegistry).
- [ ] Собрать: server + plugin-inv26; `dotnet test` тестов базы.
- [ ] Прописать `Server.exe` в `%APPDATA%\Claude\claude_desktop_config.json`; перезапустить Claude Desktop + Inventor.
- [ ] Проверить: `inventor_vent_list_products` видит каталог; открыть изделие; прочитать параметры.

## ▶ Живая доработка на Windows (с Inventor, по реальным моделям)
- [ ] Узнать реальное имя параметра «разворота корпуса» → вписать в `VentSupport.DischargeParamCandidates`.
- [ ] Габаритка: подключить заводской `.idw`-шаблон (штамп), настроить посадку видов, авто-габаритные размеры в `MakeDrawingHandler`.
- [ ] Регрессия: `vent_batch_flat_dxf` из `.ipt` → `dxf_tools/compare.py` с эталонными DXF каталога.
- [ ] Клон сборки: проверить тип ссылок; если абсолютные — реализовать Pack-and-Go в `vent_new_product`.
- [ ] Поправить `RegistrationCountTests` базы под +9 тулзов `vent`.

## ▶ Фаза 2 — интеграция с kvz-ai (топология B)
- [ ] HTTP-шлюз на Windows поверх stdio-сервера (bearer-токен, egress только к машине).
- [ ] Коннектор `connectors/inventor/` в kvz-ai (TS + @modelcontextprotocol/sdk, паттерн cad-activity):
      read-only по умолчанию, write-тулзы за ролью + approval-gate, аудит, zod-схемы.
- [ ] Регистрация в реестре коннекторов/tools kvz-ai + role-gating.
- [ ] Сквозной тест: чат kvz-ai → очередь → worker → шлюз → Inventor → ответ.

## ▶ Доработки dxf_tools (можно на Mac в любой момент)
- [ ] Сшивка контуров из LINE/ARC в замкнутые петли (убрать ложные OPEN_CONTOUR).
- [ ] Отступ отверстия от реального контура, а не от bounding box.
- [ ] HTML-отчёт-спецификация; обе схемы имён — расширить список материалов при необходимости.
