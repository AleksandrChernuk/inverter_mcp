# inventor — MCP connector для kvz-ai (скелет)

Проксирует курированные vent/Inventor-тулзы на on-prem шлюз (`phase2/gateway`). Паттерн — как
`connectors/cad-activity`: bounded tools, bearer-токен, read-only по умолчанию, write за ролью/approval.

## Установка в kvz-ai
Скопировать в `kvz-ai/connectors/inventor/`, затем зарегистрировать в реестре коннекторов/tools
kvz-ai и настроить role-gating (см. `connectors/AGENTS.md` в kvz-ai).

```bash
cp .env.example .env      # GATEWAY_URL, GATEWAY_TOKEN; WRITE_ENABLED=0 для read-only
npm ci && npm run build
node dist/server.js        # stdio MCP — worker/ContextForge подключается сюда
```

## Тулзы
- Read (всегда): `inventor_list_products`, `inventor_open_product`, `inventor_check_part`, `inventor_bom_report`.
- Write (только `WRITE_ENABLED=1`): `inventor_new_product`, `inventor_set_parameter`,
  `inventor_set_casing_discharge`, `inventor_make_gabarit`, `inventor_batch_flat_dxf`.

## Безопасность
- read-only по умолчанию (`WRITE_ENABLED=0`); write включает kvz-ai по роли конструктора + approval-gate.
- Egress — только на `GATEWAY_URL`; bearer `GATEWAY_TOKEN` (fail-closed в проде); секреты в 1Password.
- Это СКЕЛЕТ: добавьте zod-валидацию ответов, аудит-лог, rate-limit, и тесты (vitest, как у cad-activity).
