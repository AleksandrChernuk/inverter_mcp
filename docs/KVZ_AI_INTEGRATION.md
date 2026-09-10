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
   kvz-ai: очередь → worker → inspect → формирует строгий JobSpec
        │
   connector: prepare_job → plan SHA-256 → approval gate → execute_job
              → clone/recode → family recipe → transaction + checks → PDF/DXF/Excel
              → release SHA-256 → chief approval → workshop webhook
        │
   Inventor на цеховой Windows-машине выполняет, worker возвращает результат в чат.
```
Чтение идёт свободно; **любое изменение/экспорт — за approval-gate и по роли**. Approval связан с
digest точного плана: после подтверждения модель не может незаметно подменить действия.

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

## Что уже есть в коннекторе
- Курированные MCP tools: connection/catalog/open/inspect/check + prepare/status/execute/release approval.
- `JobSpec v1` для единичных правок и `Product Job v2` для полного изделия: clone/recode, signed family
  recipe, construction/assembly, motor/drawing/release gates; лишние поля запрещены.
- Persistent checkpoint после каждой стадии и append-only JSONL audit.
- `inventor_execute_job` регистрируется только при `WRITE_ENABLED=1`.
- Preflight фактического gateway inventory, baseline/final evidence и отсутствие auto-retry при
  неопределённом результате write-вызова.
- Offline DXF/папки/PDF/Excel manifest + отдельный chief release digest; webhook только после approval.

Следующий шаг — скопировать в `kvz-ai/connectors/inventor/`, зарегистрировать tools и поставить
approval-gate именно перед `inventor_execute_job` (см. `phase2/connectors-inventor/README.md`).
Для v2 нужен второй approval-gate перед `inventor_approve_release`; UI должен показывать release report,
manifest/digest и имя утверждающего.

## Каталог и безопасность
- Внутри kvz-ai видеть **весь каталог** уместно: платформа — это security boundary (роли, аудит,
  подтверждения, on-prem). `VENT_CATALOG_ROOT` = корень каталога изделий.
- Наружу (в облачную модель) уходит только то, что коннектор вернул (имена, параметры, размеры, BOM) —
  не сами файлы. Экспорт — только в `BIMWRIGHT_INVENTOR_EXPORT_ROOT`.
- Вне kvz-ai (прямой клиент) — держите профиль **read-only** по умолчанию (см.
  `scripts/windows/claude_mcp_config.example.json`), edit-профиль включайте осознанно.
