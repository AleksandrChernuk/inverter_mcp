# AGENTS.md — docs/

Документация проекта.

- `ARCHITECTURE.md` — архитектура ultra-MCP: база ipt-mcp, точки расширения, toolset `vent`,
  «разворот корпуса» и «габаритка», кросс-платформенный слой, план работ.
- `VERSIONS.md` — версии окружения/зависимостей, версия базы (commit), совместимость Inventor, changelog.
- `OPERATING.md` — как этим управлять напрямую (клиент, профили, безопасность, диагностика).
- `KVZ_AI_INTEGRATION.md` — как это подключается к kvz-ai как коннектор и как работает конструктор.
- `WORKFLOW.md` — реальный сквозной маршрут конструктора (ВКР 7,1 из ВКР 6,3) → тулзы, реализуемость.
- `AUTONOMOUS_CONNECTOR.md` — исследованные CAD-agent практики, JobSpec/transaction/checkpoint инварианты
  и Windows-критерии приёмки автономного конструктора.
- `CAPABILITY_MATRIX.md` — честный статус каждого этапа: код, Windows acceptance, заводской recipe.
- `FAMILY_RECIPES.md` — как фиксировать формулы, construction/assembly/drawing recipes без выдуманных чисел.
- `spec_sample.csv` — пример спецификации по 250 реальным DXF (результат `dxf_tools/batch.py`).

## Правило
Меняешь возможности (тулзы, зависимости, версию базы) — обнови `VERSIONS.md` (changelog + версии)
и, если поменялась схема управления, `OPERATING.md`. Держи документацию синхронной с `overlay/` и `dxf_tools/`.
