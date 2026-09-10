# Как этим управлять

## Кто есть кто
- **Конструктор** — пишет задачи в чат на русском.
- **MCP-клиент** (Claude Desktop или Claude Code на цеховой Windows-машine) — понимает чат,
  вызывает тулзы.
- **Bimwright.Ipt.Server.exe** — MCP-сервер (запускается клиентом, живёт вне Inventor).
- **Add-in внутри Inventor** — исполняет команды на модели.

```
Конструктор ──чат──> Claude (MCP-клиент) ──stdio──> Server.exe ──pipe──> Add-in ──> Inventor
```

## Разовая настройка (один раз на машине)
1. Установить: Autodesk Inventor, .NET SDK (8 для 2025/2026, 10 для 2027), git.
2. `powershell -ExecutionPolicy Bypass -File scripts\windows\setup.ps1 -InventorYear 2026 -Install -CatalogRoot D:\Catalog -ExportRoot D:\DXF_OUT`
3. Сделать 3 правки из `overlay/INTEGRATION.md`, пересобрать.
4. Прописать `scripts\windows\claude_mcp_config.example.json` в конфиг клиента, указать путь к `Server.exe`.
5. Перезапустить Inventor и MCP-клиент.

## Ежедневная работа
1. Открыть Inventor (можно без документа — MCP сам откроет изделие).
2. В чате клиента дать задачу. Примеры:

   - **Найти изделие:** «покажи изделия в каталоге» → `inventor_vent_list_products`.
   - **Открыть:** «открой ВКРН-5 сборку» → `inventor_vent_open_product`.
   - **Параметр:** «поставь диаметр рабочего колеса 500 мм» → `inventor_set_parameter` + `update`.
   - **Разворот корпуса:** «поверни корпус на Rd90 правый» → `inventor_vent_set_casing_discharge angle=90 hand=right`.
   - **Проверка перед резкой:** «проверь эту деталь» → `inventor_vent_check_part`.
   - **Габаритка:** «сделай габаритку в D:\DXF_OUT\vkrn5.pdf» → `inventor_vent_make_gabarit`.
   - **Пакетный DXF:** «выгрузи все развёртки этого изделия в D:\DXF_OUT\vkrn5» → `inventor_vent_batch_flat_dxf`.
   - **Спецификация:** «дай спецификацию по сборке» → `inventor_vent_bom_report`.
   - **Новое изделие:** «создай ВКРН-5.6 из ВКРН-5» → `inventor_vent_clone_recode_product`
     (`dryRun=true`, проверить карту; затем execute) → family recipe → checks → drawing/PDF/DXF/release.
     Native копии создаются SaveCopyAs, а copied IAM/IDW перепривязываются; простой `vent_new_product`
     оставлен только для каталогов с уже доказанными относительными ссылками.

> Фаза 1 (сейчас): MCP-клиент = **Claude Desktop** на цеховой Windows-машине, напрямую к Server.exe.
> Фаза 2: через kvz-ai (см. KVZ_AI_INTEGRATION.md). Создание изделий требует edit-профиля / роли.

3. Клиент показывает результат (габарит, дельту, список проверок, пути к файлам). Правьте задачу словами.

## Автономный режим через kvz-ai (Фаза 2)
1. Агент открывает изделие и вызывает `inventor_inspect_model` либо `inventor_inspect_assembly`.
2. `inventor_prepare_job` валидирует mutations/checks/outputs, сохраняет checkpoint и возвращает digest.
3. Конструктор/главный конструктор подтверждает именно этот digest в approval-gate kvz-ai.
4. `inventor_execute_job` делает preflight и вызывает одну транзакционную команду
   `inventor_vent_execute_plan`. FAIL означает rollback; save/export не запускаются.
5. Для JobSpec v1 финал `succeeded`. Product Job v2 после CAD/документации создаёт Excel/manifest и
   останавливается в `awaiting_release_approval` с отдельным release digest.
6. Главный конструктор проверяет артефакты и вызывает `inventor_approve_release` с digest и своим именем.
   Только успешное сообщение в настроенный webhook даёт статус `released`.
7. При финальном статусе в `inventor_job_status` лежат baseline, все проверки, output и release evidence.
   При `uncertain` ничего не повторяйте: сначала осмотрите открытую модель и audit.

Пример полного JobSpec — `phase2/connectors-inventor/README.md`; архитектурные инварианты и Windows
acceptance tests — `docs/AUTONOMOUS_CONNECTOR.md`.

## Правила безопасности (важно для цеха)
- **Экспорт только в разрешённую папку** (`BIMWRIGHT_INVENTOR_EXPORT_ROOT`) — иначе `INVALID_ARGUMENT`.
- **Режим `--read-only`** — для смотровых сессий: скрывает `set_discharge`, `make_gabarit`,
  `batch_flat_dxf`, `set_parameter` и пр.; остаются просмотр и проверки.
- **Несколько Inventor** — выбирайте нужный `inventor_switch_target` по 4-значному году; проверяйте
  активный `inventor_get_current_target`, чтобы не менять не ту модель.
- **Всегда feedback loop**: изменил → `update` → `check_part`/`measure` → и только потом экспорт/габаритка.
- **Автономный save только после PASS:** не вызывайте низкоуровневые write-тулзы в обход `execute_job`.
- **`uncertain` не равен failed:** write мог завершиться до обрыва; автоматический retry запрещён.
- **Долгие операции** (`batch_flat_dxf`) запускайте по одному изделию, не по всему каталогу.

## Диагностика
- Тулзов `vent` нет в списке → toolset не включён (`--toolsets ...,vent`) или не сделаны 3 правки.
- `NO_TARGET` → Inventor не запущен или add-in не загрузился (проверьте папку Addins, перезапуск).
- `WRONG_DOCUMENT_TYPE` → активен не тот тип документа (нужна деталь для параметров, сборка для BOM).
- `discharge parameter not found` → у модели другое имя параметра; вызовите с `param_name` (тул вернёт список имён).

## Оффлайн-слой (без Inventor, на любой машине)
Проверка/спецификация по готовым DXF — `dxf_tools/` (см. корневой README). Работает на Mac/Windows,
не требует Inventor. Та же логика проверок, что и `vent_check_part`, но по выгруженным DXF.
