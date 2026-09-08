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

## ▶ Тулзы под реальный маршрут конструктора (см. docs/WORKFLOW.md)
- [ ] `vent_recode_product` — переименование файлов по кодировке + `Set iProperty` designation.
- [ ] `vent_new_product` — доработать: копировать/активировать `.ipj`; при абс. ссылках — Pack-and-Go (ReferenceManager).
- [ ] `vent_check_product` — проверки: отверстия под двигатель, зазоры, соответствие спецификации, коды/папки.
- [ ] `vent_batch_pdf` — пакетный экспорт чертежей (.idw) в PDF по кодировке.
- [ ] `vent_batch_flat_dxf` — именование DXF по коду детали (иерархия для лазера).
- [x] XLSX-спецификация по правилам учёта металла — `dxf_tools/spec_xlsx.py` + `materials.json`.
      Проверено на реальном ВКР 7,1 (итог 101.66 кг, +1% сварочной проволоки). TODO: пополнить коды материалов,
      привязать к `bom_report` (масса из Inventor) для сборок с покупными.
- [ ] Чертежи-деталировка — поэтапно от габаритки (длинный roadmap).

## ▶ Фаза 2 — интеграция с kvz-ai (топология B)
- [x] HTTP-шлюз (скелет) — `phase2/gateway/` (Node, MCP-client→Server.exe, bearer, allowlist).
- [x] Коннектор (скелет) — `phase2/connectors-inventor/` (TS MCP-сервер, read/write-гейтинг).
- [ ] Доводка шлюза: авто-reconnect Server.exe, rate-limit, аудит-лог, привязка к LAN/VPN + файрвол.
- [ ] Доводка коннектора: zod-валидация ответов, аудит, тесты (vitest), секреты из 1Password.
- [ ] Скопировать коннектор в `kvz-ai/connectors/inventor/`, зарегистрировать в реестре + role-gating.
- [ ] Сквозной тест: чат kvz-ai → очередь → worker → коннектор → шлюз → Inventor → ответ.

## ▶ Доработки dxf_tools (можно на Mac в любой момент)
- [ ] Сшивка контуров из LINE/ARC в замкнутые петли (убрать ложные OPEN_CONTOUR).
- [ ] Отступ отверстия от реального контура, а не от bounding box.
- [ ] HTML-отчёт-спецификация; обе схемы имён — расширить список материалов при необходимости.
