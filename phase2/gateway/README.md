# vent-inventor-gateway

HTTP-шлюз поверх stdio-MCP `.NET`-сервера Inventor. Ставится **на Windows-машине с Inventor**.
Отдаёт vent-тулзы наружу для коннектора kvz-ai (топология B). Аналог «внутреннего сервиса» у cad-activity.

## Запуск (Windows)
```powershell
cd phase2\gateway
copy .env.example .env      # заполните SERVER_EXE и GATEWAY_TOKEN
npm ci
# подставьте переменные из .env в окружение (или используйте dotenv-раннер), затем:
npm start
```

## HTTP API
- `GET  /health` — без токена; `{ ok, connected }`.
- `GET  /tools/list` — bearer; список тулзов (с учётом TOOL_ALLOWLIST).
- `POST /tools/call` — bearer; тело `{ "name": "inventor_vent_...", "arguments": { ... } }`.

Авторизация: заголовок `Authorization: Bearer <GATEWAY_TOKEN>`.

## Быстрая проверка
```powershell
curl http://127.0.0.1:8737/health
curl -H "Authorization: Bearer <TOKEN>" http://127.0.0.1:8737/tools/list
curl -X POST -H "Authorization: Bearer <TOKEN>" -H "content-type: application/json" `
  -d '{"name":"inventor_vent_list_products","arguments":{}}' http://127.0.0.1:8737/tools/call
```

## Безопасность
- Прод: обязательно `GATEWAY_TOKEN`; `GATEWAY_HOST` — только нужный интерфейс (LAN/VPN), не 0.0.0.0 без файрвола.
- read-only профиль в `SERVER_ARGS` — базовая защита; write-тулзы гейтит ещё и коннектор + kvz-ai.
- Секреты — в 1Password / секрет-сторе, не в git.
- Это СКЕЛЕТ: добавьте авто-reconnect при падении Server.exe, rate-limit и структурный аудит-лог.
