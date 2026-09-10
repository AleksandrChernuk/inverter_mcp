# Фаза 2 — автономный конструктор через kvz-ai

Worker `kvz-ai` работает отдельно, Inventor — на Windows-машине. Реализованы два компонента:

```text
задача в чате
  → kvz-ai planner (JobSpec v1 / Product Job v2)
  → connectors-inventor (plan + release approval/checkpoints/audit)
  → gateway (auth/allowlist/release worker/webhook)
  → Bimwright.Ipt.Server.exe
  → add-in: vent_execute_plan (Inventor transaction + objective checks)
  → Inventor 2026 → PDF/DXF → offline DXF/Excel/hash gate → цех
```

`connectors-inventor/` — MCP-сервер для worker. Он даёт агенту компактный набор inspect/job-тулзов,
хранит план и доказательства, связывает approval с SHA-256 digest и никогда автоматически не повторяет
операцию с неопределённым результатом.

`gateway/` — Windows HTTP-транспорт поверх локального stdio-MCP: bearer, безопасный allowlist,
rate-limit, лимит тела, reconnect и JSONL-аудит.

Изменения применяются не последовательностью беззащитных HTTP-вызовов, а одной командой
`inventor_vent_execute_plan`: она открывает транзакцию Inventor, выполняет ограниченные mutations,
делает `Update2(false)`, проверяет здоровье/constraints/interference/min-distance/физические границы и
откатывает всё при любом FAIL. Сохранение и экспорт идут только после PASS.

Product Job v2 добавляет clone/recode через SaveCopyAs+ReplaceReference, signed family formulas,
typed создание деталей/сборок/метизов, motor-hole checks, drawing recipes, batch PDF/DXF, Excel и
release manifest. После первого approval задача доходит только до `awaiting_release_approval`;
сообщение в цех и статус `released` возможны после второго digest approval главного конструктора.

Остаётся интеграционная работа на Windows/в `kvz-ai`: принять C# handlers на реальных сборках Inventor
2026, снять утверждённые formulas/named refs/drawing coordinates из КД, подключить IDW-шаблоны и webhook.
