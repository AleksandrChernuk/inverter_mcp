# inventor — автономный MCP-коннектор конструктора для kvz-ai

Коннектор превращает свободную задачу из чата в контролируемый инженерный цикл. LLM в `kvz-ai`
планирует, а этот процесс валидирует и исполняет `JobSpec v1` или полный `Product Job v2`: без
произвольного C#/Python, с точными единицами, двумя SHA-256 approval, проверками и журналом.

## Цикл работы

```text
намерение конструктора
  → inspect_model / inspect_assembly
  → inventor_prepare_job (валидация recipe + immutable plan digest)
  → approval-gate kvz-ai на plan digest
  → inventor_execute_job
      preflight tools → baseline → транзакция mutations → Update2(false)
      → clone/recode → construction → acceptance → save → drawings/PDF/DXF/Excel
      → release digest → approval главного конструктора → webhook → released
```

Для v1 финал — `succeeded`, для v2 — `released`. `uncertain` означает, что связь пропала во время
неидемпотентной операции: коннектор **не повторяет** её автоматически, требуется осмотр Inventor.

## Курированный набор MCP-тулзов

- `inventor_connection_status` — связь и фактический inventory gateway.
- `inventor_list_products`, `inventor_open_product` — выбор изделия.
- `inventor_inspect_model`, `inventor_inspect_sketch` — дерево детали и реальные драйверы.
- `inventor_inspect_assembly` — масса/габарит, BOM+DOF, constraints, interference.
- `inventor_check_part` — быстрый model-side gate перед DXF.
- `inventor_prepare_job`, `inventor_job_status` — план/digest и checkpoint/evidence.
- `inventor_execute_job`, `inventor_approve_release` — появляются только при `WRITE_ENABLED=1`.

Низкоуровневые write-команды не выдаются модели россыпью: они доступны только как типизированные
`mutations` внутри одной проверяемой задачи.

## JobSpec v1

```json
{
  "version": "1",
  "objective": "Поставить диаметр рабочего колеса 500 мм и проверить сборку",
  "product_path": "D:\\Catalog\\VKR-7.1\\VKR-7.1.iam",
  "mutations": [
    {
      "kind": "set_component_parameter",
      "occurrence": "Рабочее колесо:1",
      "name": "Diameter",
      "value": "500 mm"
    },
    { "kind": "set_casing_discharge", "angle": 90, "hand": "right" }
  ],
  "checks": [
    { "kind": "model_health" },
    { "kind": "constraints_healthy", "allowed_unhealthy": 0 },
    { "kind": "no_interference", "max_pairs": 0 },
    {
      "kind": "physical_bounds",
      "min_x_mm": 400,
      "max_x_mm": 900,
      "max_mass_g": 250000
    }
  ],
  "outputs": [
    {
      "kind": "capture_view",
      "orientation": "iso_top_right",
      "output_path": "D:\\DXF_OUT\\VKR-7.1\\proof.png"
    }
  ],
  "save": "after_checks",
  "context": {
    "request_id": "task-1842",
    "actor": "constructor-12",
    "approval_id": "approval-771"
  }
}
```

Поддержанные mutations: `drive_dimension`, `set_component_parameter`, `set_constraint`,
`set_casing_discharge`, `set_material`. Проверки: `model_health`, `part_cut_ready`,
`constraints_healthy`, `no_interference`, `min_distance`, `physical_bounds`.

## Product Job v2

V2 фиксирует source (существующее изделие либо clone/recode), family/revision, инженерные variables,
formula bindings, до 512 typed CAD steps, CAD/motor checks, drawing/PDF/DXF outputs и release rules.

Числа/имена в примере показывают только формат. В рабочей задаче parameter names, coefficients,
двигатель, метизы, section/detail coordinates и допуски берутся из утверждённой КД и подписываются
главным конструктором как конкретная revision.

```json
{
  "version": "2",
  "objective": "Создать новый типоразмер ВКР и подготовить проверенный выпуск",
  "source": {
    "mode": "clone_recode",
    "source_root": "D:\\Factory\\VKR-BASE",
    "destination_root": "D:\\DXF_OUT\\VKR-NEW",
    "top_document": "D:\\Factory\\VKR-BASE\\VKR-BASE.iam",
    "source_code": "VKR-BASE",
    "target_code": "VKR-NEW"
  },
  "recipe": {
    "family": "VKR",
    "revision": "APPROVED-REVISION-ID",
    "approval_digest": "0000000000000000000000000000000000000000000000000000000000000000",
    "variables": { "wheel_diameter_mm": 0 },
    "variable_limits": { "wheel_diameter_mm": { "min": 0, "max": 0, "unit": "mm" } },
    "bindings": [{
      "target": { "kind": "component_parameter", "occurrence": "APPROVED_OCCURRENCE", "name": "APPROVED_PARAMETER" },
      "formula": { "terms": { "wheel_diameter_mm": 1 } },
      "unit": "mm"
    }],
    "construction_steps": []
  },
  "checks": [
    { "kind": "model_health" },
    { "kind": "constraints_healthy", "allowed_unhealthy": 0 },
    { "kind": "no_interference", "max_pairs": 0 }
  ],
  "sheet_metal_checks": [],
  "mounting_checks": [],
  "outputs": [
    {
      "kind": "flat_dxf",
      "output_dir": "D:\\DXF_OUT\\VKR-NEW\\DXF",
      "material_aliases": { "APPROVED_INVENTOR_MATERIAL": "APPROVED_FACTORY_ALIAS" }
    },
    { "kind": "batch_pdf", "drawings_dir": "D:\\DXF_OUT\\VKR-NEW", "output_dir": "D:\\DXF_OUT\\VKR-NEW\\PDF" }
  ],
  "release": {
    "product_root": "D:\\DXF_OUT\\VKR-NEW",
    "dxf_dir": "D:\\DXF_OUT\\VKR-NEW\\DXF",
    "pdf_dir": "D:\\DXF_OUT\\VKR-NEW\\PDF",
    "output_dir": "D:\\DXF_OUT\\VKR-NEW\\release",
    "product_code": "VKR-NEW",
    "minimum_pdf_count": 1,
    "require_clone_manifest": true,
    "require_dxf_manifest": true
  }
}
```

Нули в `approval_digest` — только синтаксически валидная заглушка примера. Рабочая задача с ней не
пройдёт: вставьте digest, который вернул `inventor_recipe_template_digest` для подписанного шаблона.
Числовые поля construction steps могут быть формулами от approved variables, а recoded пути — безопасными
шаблонами `job_path`/`job_root`; полный формат описан в `docs/FAMILY_RECIPES.md`.

После execute статус `awaiting_release_approval`. Главный конструктор сверяет evidence и вызывает
`inventor_approve_release(job_id, release_digest, approved_by)`. Только успешный webhook даёт `released`.
До prepare администратор рассчитывает candidate через `inventor_recipe_template_digest`, проверяет КД и
добавляет утверждённый 64-hex digest в `APPROVED_RECIPE_DIGESTS` runtime коннектора.

## Установка

```bash
cp .env.example .env
pnpm install --frozen-lockfile
pnpm test
pnpm start
```

Секрет `GATEWAY_TOKEN` подаётся из 1Password/секрет-стора, не хранится в `.env` в production.
`JOB_STATE_DIR` содержит JSON-checkpoint после каждой стадии, `AUDIT_LOG_PATH` — JSONL без токенов и
ответов модели. При `NODE_ENV=production` пустой gateway-токен запрещён.

На один `JOB_STATE_DIR` запускается один экземпляр коннектора. Внутри процесса повторный одновременный
`execute_job` для того же `job_id` объединяется с уже идущим вызовом и не запускает вторую мутацию.

## Граница ответственности

Коннектор сам исполняет и проверяет работу, но не содержит отдельный LLM. Перевод естественного языка
в `JobSpec` делает агент `kvz-ai`; это сохраняет единый role/approval/audit boundary. Глубокая проверка
выпущенного DXF всё ещё выполняется `dxf_tools` — model-side `part_cut_ready` её не заменяет.
