# TODO — ultra Inventor MCP (вентзавод)

Цель: Inventor 2026. Фаза 1 — напрямую через Claude Desktop; Фаза 2 — через kvz-ai (топология B).
Подробности: `docs/ARCHITECTURE.md`, `docs/OPERATING.md`, `docs/KVZ_AI_INTEGRATION.md`, `docs/VERSIONS.md`.

## ✅ Реализовано в коде
- [x] Выбрана база `bimwright/ipt-mcp`, зафиксирован commit; разобраны точки расширения.
- [x] `overlay/` — toolset `vent` (19 тулзов): каталог/open/save-as/save-product, параметры/constraints,
      inspect model/sketch, проверки/габаритка/DXF/BOM и транзакционный `execute_plan`.
- [x] `vent_clone_recode_product`: dry-run карты, SaveCopyAs, ReplaceReference, recode файлов и iProperties,
      внешний shared-крепёж не копируется, при сбое остаётся `INCOMPLETE.json`.
- [x] Формульный `Product Job v2`: versioned family recipe, typed construction/assembly steps,
      создание детали/сборки, установка компонентов/метизов и digest двух уровней.
- [x] Проверка разболтовки двигателя по реальным `HoleFeature.HoleCenterPoints`, BCD и угловому шагу.
- [x] `vent_make_part_unique` — изоляция общей детали (лопатки из общей Библиотеки): копия внутрь папки
      изделия + перепривязка ссылки (`ComponentOccurrence.Replace`), оригинал не трогается. Закрывает дыру
      Pack-and-Go (он не копирует внешние/библиотечные ссылки). Приёмка на Windows/2026. Дальше по желанию:
      авто-вызов перед правкой общей детали и опция «затягивать общие детали» в `clone_recode_product`.
- [x] Параметризация типоразмеров (все фазы в коде): Фаза 0 `vent_inspect_parametrization`,
      Фаза 1 `vent_run_ilogic`, Фаза 2 `vent_select_ipart_member` (строка iPart/iAssembly),
      Фаза 3 `vent_make_components` (разведка многотелки + guarded execute), Фаза 4 `vent_import_params`
      (импорт таблицы параметров через `UserParameters.AddByExpression`). Приёмка на реальных изделиях —
      Windows/2026 (сигнатуры iPart `CreateMember`/`ChangeRow`, headless-API Make Components). См. `docs/PARAMETRIZATION_PLAN.md`.
- [x] Чертёжный пакет: виды, retrieved dimensions, разрезы, выносные виды, Parts List, balloons,
      hole table, техтребования; пакетный PDF всех IDW/DWG.
- [x] `dxf_tools/release.py`: глубокая DXF-проверка, коды/папки/PDF, CSV/XLSX, file manifest и release digest.
- [x] Gate главного конструктора + webhook сообщения в «цех-конструктора» после точного release digest.
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
- [ ] Подключить заводской `.idw`-шаблон и откалибровать координаты разрезов/выносок каждого drawing recipe.
- [ ] Регрессия: `vent_batch_flat_dxf` из `.ipt` → `dxf_tools/compare.py` с эталонными DXF каталога.
- [ ] Проверить `clone_recode_product` на копии реального ВКР: абсолютные/вложенные/чертёжные ссылки,
      восстановление исходных ссылок и отсутствие сохранения шаблона.
- [x] `RegistrationCountTests` базы сделан vent-независимым (фильтр `inventor_vent_*`); vent-поверхность
      охраняется `VentToolSurfaceTests`. Правка задокументирована в `overlay/INTEGRATION.md`.
- [x] Тесты vent-тулз (Inventor-free): `VentToolsWireTests` (25 wire-контрактов), `VentToolsServerTests`
      (list/new_product по ФС), `VentToolSurfaceTests` (замороженные 27 имён). Прогон: 196/196 зелёные (`dotnet test`).

## ▶ Заводская калибровка (не заменяется интернетом)
- [ ] Снять из утверждённых моделей/чертежей настоящие параметры и формулы ВКР, РК, рамы,
      диффузора, обребровки и занести как подписанную revision family recipe.
- [ ] Зафиксировать библиотечные пути покупных двигателей/метизов и устойчивые iMate/work-feature refs.
- [ ] Для каждого типа чертежа задать minimum dimensions/balloons, координаты section/detail и техтребования.
- [ ] Пополнить `materials.json` реальными складскими кодами и правилами именования DXF.
- [x] XLSX-спецификация по правилам учёта металла — `dxf_tools/spec_xlsx.py` + `materials.json`.
      Проверено на реальном ВКР 7,1 (итог 101.66 кг, +1% сварочной проволоки). TODO: пополнить коды материалов,
      привязать к `bom_report` (масса из Inventor) для сборок с покупными.

## ▶ Фаза 2 — интеграция с kvz-ai (топология B)
- [x] HTTP-шлюз — bearer, безопасный allowlist, reconnect, rate/body limits, JSONL audit,
      нормализация вложенных Inventor-ошибок, без retry неопределённых write-вызовов.
- [x] Автономный коннектор — строгий JobSpec, digest approval, persistent checkpoints/evidence,
      Product Job v2, release approval и 28 Node contract tests без новых test-зависимостей.
- [ ] На Windows привязать gateway к LAN/VPN + файрволу и подать токены/пути из 1Password.
- [ ] Проверить `vent_execute_plan` в Inventor 2026: Update2/HealthStatus и rollback вложенной детали.
- [ ] Скопировать коннектор в `kvz-ai/connectors/inventor/`, зарегистрировать в реестре + role-gating.
- [ ] Сквозной тест: чат kvz-ai → очередь → worker → коннектор → шлюз → Inventor → ответ.
- [ ] Настроить `RELEASE_PYTHON`, `RELEASE_SCRIPT`, `RELEASE_WEBHOOK_URL` из runtime/1Password и
      проверить реальный чат «цех-конструктора».
- [ ] Сквозная Windows-приёмка Product Job v2 на копии ВКР 6,3 → новый типоразмер.

## ▶ Доработки dxf_tools (можно на Mac в любой момент)
- [x] Сшивка контуров из LINE/ARC в замкнутые петли.
- [x] Отступ отверстия от реального контура.
- [ ] HTML-отчёт-спецификация; обе схемы имён — расширить список материалов при необходимости.
