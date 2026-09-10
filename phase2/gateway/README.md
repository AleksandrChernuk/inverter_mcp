# vent-inventor-gateway

HTTP-шлюз на Windows поверх локального stdio-MCP `Bimwright.Ipt.Server.exe`. Он не принимает
инженерных решений: обеспечивает надёжный и ограниченный транспорт между kvz-ai и Inventor.

## Что реализовано

- `GET /health`, `GET /tools/list`, `POST /tools/call`;
- bearer-проверка в constant time; в `NODE_ENV=production` пустой токен запрещён;
- безопасный allowlist по умолчанию (произвольный `inventor_send_code` отсутствует);
- автоматическое переподключение после падения `Server.exe`;
- отсутствие автоматического повтора write-вызова при обрыве связи;
- rate limit на IP и ограничение JSON-body;
- структурный JSONL-аудит без токена и содержимого ответа;
- корректное распознавание вложенного `{ "ok": false }` от Inventor как ошибки.
- локальный `inventor_vent_build_release`: запускает pinned `dxf_tools/release.py` без shell, сверяет
  exact DXF export manifest и принимает input paths только под `BIMWRIGHT_INVENTOR_EXPORT_ROOT`;
- `inventor_vent_publish_release`: JSON webhook после chief approval, bearer только из env,
  `idempotency-key=job_id:digest`.

## Запуск на Windows

```powershell
cd phase2\gateway
copy .env.example .env
pnpm install --frozen-lockfile
pnpm test
pnpm start
```

Для просмотра оставьте `SERVER_ARGS=--read-only ...` и `WRITE_ENABLED=0` у коннектора. Для автономной
рабочей сессии уберите `--read-only`, оставьте только нужные toolsets, а `WRITE_ENABLED=1` включайте
ролью конструктора после approval-gate.

Для Product Job v2 установите `dxf_tools/requirements.txt`, затем задайте export root,
`RELEASE_PYTHON` и `RELEASE_SCRIPT`. Для сообщения в цех задайте `RELEASE_WEBHOOK_URL` через runtime/
1Password. Если worker или webhook не настроен, соответствующий local tool отсутствует в `/tools/list`,
а preflight/approval останавливает задачу.

Не публикуйте порт прямо в интернет: bind на конкретный LAN/VPN-интерфейс, firewall allowlist и
уникальный токен из 1Password/секрет-стора обязательны.

## API

```powershell
curl http://127.0.0.1:8737/health
curl -H "Authorization: Bearer <TOKEN>" http://127.0.0.1:8737/tools/list
curl -X POST -H "Authorization: Bearer <TOKEN>" -H "content-type: application/json" `
  -d '{"name":"inventor_health","arguments":{}}' http://127.0.0.1:8737/tools/call
```

`TOOL_ALLOWLIST` при задании полностью заменяет встроенный список. После изменений проверяйте, что
в нём остаются все `required_tools`, возвращённые `inventor_prepare_job`.
