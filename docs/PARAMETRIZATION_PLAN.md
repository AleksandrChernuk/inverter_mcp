# План: параметризация типоразмеров в коннекторе (по порядку)

> Статус: согласованный план. Реализация ещё не начата. Целевая версия — Inventor 2026.

## Context (зачем)
Заводской регламент КВЗ: типорозміри создают через ОДИН параметризованный мастер
(iPart/iAssembly-таблицы, правила iLogic по параметру-ключу, Excel-параметры, и
мультитело .ipt → Make Components), а не отдельными файлами и не ручной правкой геометрии.

Сейчас коннектор умеет только читать/писать параметры и размеры
(`vent_drive_dimension`, `vent_set_component_parameter`, `create/get/set/list_parameter`).
Он НЕ умеет: запустить iLogic-правило, выбрать строку iPart/iAssembly, сделать Make Components,
привязать Excel. Поэтому прошлая попытка увеличить колесо провалилась — деталь была НЕ
параметризована (Эскиз3: 0 размеров / 0 ограничений), тянуть было не за что.

Цель: дать коннектору «заводской» способ менять типорозмір — задать ключевой параметр
параметризованного мастера и дать модели перестроиться. Плюс тулза-разведка: мгновенно
понять, параметризовано ли конкретное изделие и каким ключом (экономит токены и попытки).

## Порядок работ

### Фаза 0 — `vent_inspect_parametrization` (read-only, разведка) — делаем ПЕРВОЙ
Отчёт по активному документу:
- iPart/iAssembly: это factory или member? список строк (members), колонки (= параметры),
  какие колонки ключевые.
- iLogic: список правил (имена, кол-во) через iLogic Automation.
- Пользовательские параметры и признак привязки к Excel.
Зачем первой: дешёвая, сразу показывает, драйвится ли изделие и чем. На ней проверим
реальные изделия (ВЦ 4-75 3,15М и др.) до постройки «рычагов».

### Фаза 1 — `vent_run_ilogic` (рычаг для способа 2: iLogic)
Задать ключевой параметр (существующими `drive_dimension`/`set_parameter`) и прогнать
правило: запуск правила по имени (или всех), Update, отчёт об изменённых параметрах + bbox/мм + масса.

### Фаза 2 — `vent_select_ipart_member` (рычаг для способа 1: iPart/iAssembly)
Выбрать строку iPart/iAssembly по имени члена или по значениям ключевых колонок на активном
factory-документе; сгенерировать/активировать member; вернуть итоговый размер (bbox/масса).

### Фаза 3 — `vent_make_components` (мультитело .ipt → .iam)
Make Components: сперва разведка API на Inventor 2026 (есть ли программный вызов; иначе —
через диспетчер UI-команды), затем реализация. На 2026 API шире, чем на 2021.

### Фаза 4 — Excel-параметры (способ 3)
Привязка/импорт Excel-таблицы параметров в деталь/сборку (и в iPart/iAssembly). Организация
данных для семейств с одинаковыми именами параметров.

**Объём: берём все методы, по порядку (Фазы 0→4).**

## Файлы (паттерн — как у существующих vent-тулз)
Для каждой новой тулзы (правим И `_forks/ipt-mcp/…` для сборки, И `overlay/…` для git):
- `overlay|_forks .../shared/Handlers/Vent/<Name>Handler.cs` — новый handler
  (`HandlerBase` + `IInventorCommand`, `ctx.Application`, `Ok/Fail`, `InventorErrorCodes`),
  по образцу `InspectModelHandler.cs` / `SetComponentParameterHandler.cs`.
- `.../shared/Plugin/InventorCommandRegistry.Vent.cs` — строка `add(new <Name>Handler());`.
- `.../server/Tools/VentTools.cs` — метод с `[McpServerTool(Name=...)]` + `Call("<wire>", jobj)`.
- `.../shared/Handlers/Vent/VentSupport.cs` — общие хелперы (доступ к iLogic Automation;
  доступ к iPart factory/member) — по образцу уже добавленных `FindProjectFile`/`ActivateProject`.

## Технические заметки
- **iLogic**: `app.ApplicationAddIns.ItemById("{3BDD8D79-2179-4B11-8A5A-257B1C0263AC}").Automation`,
  вызвать поздним связыванием (`dynamic`, без ссылки на сборку): `RunRule(doc, name)`, `Rules(doc)`.
  Защита: add-in iLogic может отсутствовать → понятный Fail.
- **iPart/iAssembly**: `PartComponentDefinition.iPartFactory` / `iPartMember`;
  `AssemblyComponentDefinition.iAssemblyFactory` / `iAssemblyMember`; строки `TableRows`,
  колонки `TableColumns`, переключение `ChangeRow`/`CreateMember` (уточнить на реальном factory).
- **Guardrail**: переиспользовать copy-before-resize (`ExportPathPolicy` + `allow_in_place`):
  выбор строки / прогон правила на мастер-файле каталога меняет общий файл → тот же гейт, что в
  `drive_dimension`/`set_component_parameter`.
- **Silent-mode** уже оборачивает non-readonly handlers (диалоги подавляются).
- **Версия — целимся в Inventor 2026** (машину переустанавливают на 2026). Не привязываемся к
  2021: целевая сборка/деплой = **plugin-inv26** (net8, финальная сборка на машине с 2026 —
  там реальный interop). shared-код версионно-нейтрален (тот же `#if`), при желании остаётся
  собираемым и под 2021, но это уже не цель.

## Деплой (на машине с Inventor 2026)
Батчем: собрать все новые тулзы → `dotnet build` **plugin-inv26** + server на 2026-машине →
закрыть Inventor, скопировать свежий DLL в `…\Inventor 2026\Addins\`, открыть Inventor
(сервер сам поднимется). Новые СЕРВЕРНЫЕ тулзы появятся в списке Claude только после
перезапуска Claude Desktop — один перезапуск в конце, а не после каждой.

## Проверка (end-to-end, на 2026)
1. Открыть кандидат-деталь/сборку → `vent_inspect_parametrization`.
   - Есть iPart/iLogic → задать ключ + прогнать правило / выбрать строку → убедиться, что
     bbox/масса изменились ожидаемо.
   - Нет параметризации → это подтверждает: изделие сперва надо параметризовать (отдельная задача моделирования).
   - На 2026 доступны и 2026-изделия (напр. ВКР 7,1), которые на 2021 не открывались.
2. Сборка 0 ошибок; после деплоя — `inventor_health`, `inventor_list_open_documents`.
3. Коммит + пуш каждой фазы; CI (сейчас зелёный) должен остаться зелёным.
