# Фаза 2 — интеграция с kvz-ai (топология B)

Worker kvz-ai работает отдельно (Linux/облако), Inventor — на цеховой Windows-машине.
Два компонента-скелета:

```
worker kvz-ai ──stdio(MCP)──> connectors/inventor  ──HTTP(+токен)──>  gateway (Windows)
                              (TS MCP-сервер,                         (Node, HTTP поверх
                               паттерн cad-activity)                   stdio-MCP .NET-сервера)
                                                                              │ stdio(MCP)
                                                                     Bimwright.Ipt.Server.exe
                                                                              │ pipe
                                                                          Inventor 2026
```

- `gateway/` — ставится и запускается **на Windows-машине с Inventor**. Поднимает `Server.exe`
  (MCP по stdio) и отдаёт его наружу как HTTP-сервис (`/health`, `/tools/list`, `/tools/call`)
  с bearer-токеном и allowlist'ом. Это «внутренний Inventor-сервис» (аналог ACTIVITY_SERVICE_URL у cad-activity).
- `connectors-inventor/` — копируется в kvz-ai как `connectors/inventor/`. TS MCP-сервер (stdio),
  который worker подключает; проксирует курированный набор vent-тулзов на gateway по HTTP.
  read-only по умолчанию; write-тулзы за `WRITE_ENABLED`/ролью + approval-gate kvz-ai.

## Оба — СКЕЛЕТЫ
Компилируются/структурно готовы, но требуют доводки на Windows: реальный запуск `Server.exe`,
токены из 1Password/секрет-стора, привязка к сети (LAN/VPN), регистрация коннектора в реестре kvz-ai.
См. `docs/KVZ_AI_INTEGRATION.md` и `TODO.md` (раздел «Фаза 2»).

## Защита (defense-in-depth)
1. `Server.exe` профиль `--read-only` для смотровых сессий.
2. gateway: allowlist тулзов + bearer + bind только на нужный интерфейс.
3. connector: `WRITE_ENABLED` gate + role-gating и approval-gate самого kvz-ai.
