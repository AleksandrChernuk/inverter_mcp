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

3. Клиент показывает результат (габарит, дельту, список проверок, пути к файлам). Правьте задачу словами.

## Правила безопасности (важно для цеха)
- **Экспорт только в разрешённую папку** (`BIMWRIGHT_INVENTOR_EXPORT_ROOT`) — иначе `INVALID_ARGUMENT`.
- **Режим `--read-only`** — для смотровых сессий: скрывает `set_discharge`, `make_gabarit`,
  `batch_flat_dxf`, `set_parameter` и пр.; остаются просмотр и проверки.
- **Несколько Inventor** — выбирайте нужный `inventor_switch_target` по 4-значному году; проверяйте
  активный `inventor_get_current_target`, чтобы не менять не ту модель.
- **Всегда feedback loop**: изменил → `update` → `check_part`/`measure` → и только потом экспорт/габаритка.
- **Долгие операции** (`batch_flat_dxf`) запускайте по одному изделию, не по всему каталогу.

## Диагностика
- Тулзов `vent` нет в списке → toolset не включён (`--toolsets ...,vent`) или не сделаны 3 правки.
- `NO_TARGET` → Inventor не запущен или add-in не загрузился (проверьте папку Addins, перезапуск).
- `WRONG_DOCUMENT_TYPE` → активен не тот тип документа (нужна деталь для параметров, сборка для BOM).
- `discharge parameter not found` → у модели другое имя параметра; вызовите с `param_name` (тул вернёт список имён).

## Оффлайн-слой (без Inventor, на любой машине)
Проверка/спецификация по готовым DXF — `dxf_tools/` (см. корневой README). Работает на Mac/Windows,
не требует Inventor. Та же логика проверок, что и `vent_check_part`, но по выгруженным DXF.
