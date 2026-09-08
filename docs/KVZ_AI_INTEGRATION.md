# Подключение к kvz-ai — как этим работает конструктор

`kvz-ai` уже даёт всё, что нужно вокруг: чат, очередь задач, worker, роли, аудит,
approval-gate'ы, реестр MCP-коннекторов (kb-docs, cad-activity, bitrix, 1C). Наш Inventor-MCP
подключается как **ещё один коннектор** — по образцу `connectors/cad-activity`.

Конструктор не запускает никаких серверов и не выбирает профили — он просто **пишет задачу в чат
kvz-ai**, а платформа маршрутизирует её в коннектор Inventor.

## Как это выглядит для конструктора
```
Конструктор в чате kvz-ai:
  «в изделии ВКРН-5 поставь диаметр колеса 500, разворот корпуса Rd90 правый,
   проверь контуры и сделай габаритку»
        │
   kvz-ai: очередь → worker → роль разрешает? → executor разбирает запрос
        │
   вызовы коннектора Inventor:  vent_open_product → set_parameter → vent_set_casing_discharge
                                → vent_check_part → (approval gate) → vent_make_gabarit
        │
   Inventor на цеховой Windows-машине выполняет, worker возвращает результат в чат.
```
Чтение (список изделий, параметры, проверки, спецификация) идёт свободно; **любое изменение/экспорт —
за approval-gate и по роли** (как write-операции у bitrix).

## Фазность (решено)
- **Фаза 1 — сейчас, напрямую через Claude app (Claude Desktop).** MCP-клиент = Claude Desktop на
  цеховой Windows-машине, подключается к нашему `Bimwright.Ipt.Server.exe` по stdio. Без kvz-ai.
  Профиль по умолчанию — read-only; для правок/создания изделий — edit-профиль осознанно.
- **Фаза 2 — через kvz-ai, топология B** (worker отдельно на Linux/облаке, Inventor на цеховой машине).

## Топология Фазы 2 — Вариант B (выбран)
Наш .NET-сервер + add-in обязаны жить **на Windows-машине с Inventor** (add-in внутри Inventor,
local pipe). Worker kvz-ai — отдельно (Linux/облако), поэтому:
- На Windows поднимается **сетевой шлюз** (HTTP, bearer-токен, egress только к этой машине) поверх
  stdio-сервера — потому что базовый сервер общается только локально.
- Коннектор `connectors/inventor/` (TS, паттерн cad-activity) ходит в этот шлюз.
- (Вариант A — worker на самой Windows-машине, коннектор spawn'ит Server.exe по stdio — не наш случай.)

## Что нужно добавить в kvz-ai (следующий шаг)
Коннектор `connectors/inventor/` в стиле `cad-activity`:
- TS + `@modelcontextprotocol/sdk` + zod-схемы, bearer `CONNECTOR_TOKEN`, аудит, строгие входные схемы.
- **read-only по умолчанию**; write-тулзы (`set_parameter`, `vent_set_casing_discharge`,
  `vent_make_gabarit`, `vent_batch_flat_dxf`) — отдельная capability, включается ролью + approval-gate.
- Проксирует vent-тулзы на `Bimwright.Ipt.Server.exe` (Вариант A — spawn stdio; Вариант B — HTTP-шлюз).
- Регистрация в реестре коннекторов/tools kvz-ai + role-gating (см. `connectors/AGENTS.md` в kvz-ai).

## Каталог и безопасность
- Внутри kvz-ai видеть **весь каталог** уместно: платформа — это security boundary (роли, аудит,
  подтверждения, on-prem). `VENT_CATALOG_ROOT` = корень каталога изделий.
- Наружу (в облачную модель) уходит только то, что коннектор вернул (имена, параметры, размеры, BOM) —
  не сами файлы. Экспорт — только в `BIMWRIGHT_INVENTOR_EXPORT_ROOT`.
- Вне kvz-ai (прямой клиент) — держите профиль **read-only** по умолчанию (см.
  `scripts/windows/claude_mcp_config.example.json`), edit-профиль включайте осознанно.
