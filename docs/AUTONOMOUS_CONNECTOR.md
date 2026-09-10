# Автономный конструктор: архитектура и источники решений

## Цель

Product Job v2 проходит `inspect → plan → approve plan → clone/recode → construct/parameterize →
verify → drawings/PDF/DXF/Excel → hash → approve release → notify`. Успешный финал — только
`released`, а не текст LLM, HTTP 200 или факт сохранения модели.

LLM-планировщик остаётся в `kvz-ai`. Коннектор — детерминированный исполнитель строгого рецепта:
никакого произвольного C#/Python, скрытых повторов и неподтверждённых формул.

## Проверенные источники

- [bimwright/ipt-mcp](https://github.com/bimwright/ipt-mcp) — внешний MCP-сервер + add-in на STA-потоке,
  toolset gating, локальный транспорт, typed CAD handlers.
- [ADN Inventor Training Material](https://github.com/ADN-DevTech/Inventor-Training-Material) — официальные
  Autodesk samples для ReplaceReference, Parts List и balloons.
- [InventorShims](https://github.com/InventorCode/InventorShims) — тонкие typed adapters и явная обработка
  ошибок Inventor API.
- [autodesk-inventor-mcp](https://github.com/xtylerai2026-oss/autodesk-inventor-mcp) — один STA executor,
  mm/deg на границе, запрет локализованных имён Origin/BOM, named work features/iMates вместо face IDs.
- Официальный [Pack and Go](https://help.autodesk.com/cloudhelp/2026/ENU/Inventor-Help/files/GUID-018371A9-B60D-44CB-B70C-8618155CC598.htm)
  требует копировать источник и его ссылки с сохранением связей. Для программной копии применены
  [`Document.SaveAs(..., SaveCopyAs=true)`](https://help.autodesk.com/cloudhelp/2022/ENU/Inventor-API/files/Document_SaveAs.htm)
  и [`FileDescriptor.ReplaceReference`](https://help.autodesk.com/cloudhelp/2022/ENU/Inventor-API/files/FileDescriptor_ReplaceReference.htm).
  Autodesk отдельно требует одинаковый `InternalName`; его сохраняет SaveCopyAs.
- [`Document.Update2`](https://help.autodesk.com/cloudhelp/2022/ENU/Inventor-API/files/Document_Update2.htm),
  [`TransactionManager`](https://help.autodesk.com/cloudhelp/2022/ENU/Inventor-API/files/TransactionManager.htm)
  и [`Transaction.Abort`](https://help.autodesk.com/cloudhelp/2025/ENU/Inventor-API/files/Transaction_Abort.htm)
  являются основой rebuild/rollback. Autodesk показывает у transaction собственный
  [`Document`](https://help.autodesk.com/cloudhelp/2025/ENU/Inventor-API/files/Transaction_Document.htm),
  поэтому referenced `.ipt` дополнительно защищаются snapshot. Для сохранения dirty dependencies используется
  [`Document.Save2`](https://help.autodesk.com/cloudhelp/2021/ENU/Inventor-API/files/PartDocument_Save2.htm),
  а не `Save()` только активной сборки.
- Drawing API: [section](https://help.autodesk.com/cloudhelp/2023/ENU/Inventor-API/files/DrawingViews_AddSectionView.htm),
  [detail](https://help.autodesk.com/cloudhelp/2025/ENU/Inventor-API/files/DrawingViews_AddDetailView.htm),
  [Retrieve dimensions](https://help.autodesk.com/cloudhelp/2024/ENU/Inventor-API/files/GeneralDimensions_Retrieve.htm),
  [Parts List](https://help.autodesk.com/cloudhelp/2022/ENU/Inventor-API/files/PartsLists_Add.htm),
  [balloons](https://help.autodesk.com/cloudhelp/2025/ENU/Inventor-API/files/Balloons_Add.htm),
  [hole table](https://help.autodesk.com/cloudhelp/2024/ENU/Inventor-API/files/HoleTables_Add.htm),
  [PDF translator](https://help.autodesk.com/cloudhelp/2022/ENU/Inventor-API/files/SaveAsPDFTranslator_Sample.htm).
- В X найдены полезные эксплуатационные принципы: официальный
  [Onshape workflow](https://x.com/Onshape/status/2061440183564709951) разделяет geometry/physics/materials;
  [long-running agents](https://x.com/mihail_eric/status/2032145866614849665) рассматривает tests и milestone
  validation как систему управления; [human-in-the-loop](https://x.com/v_shakthi/status/2039889360955609385)
  требует approval boundary и rollback. Проверяемого кода Inventor Pack-and-Go/чертежей в X не найдено,
  поэтому техническая реализация опирается на Autodesk API и исходники, а не на посты.

## Инварианты реализации

1. JobSpec v1 сохранён для одной параметрической операции. Product Job v2 фиксирует source, revision
   family recipe, construction, checks, outputs и release одним SHA-256 плана.
2. `prepare_job` не меняет Inventor. `execute_job` принимает только сохранённый `job_id` и тот же digest.
3. Формула рецепта ограничена `constant + Σ(variable × coefficient)` и optional rounding. Неизвестная
   переменная, NaN, лишнее поле или duplicate step id отклоняются до CAD.
4. Создание детали/сборки — максимум 512 typed steps: document, sketch, extrude/revolve, hole/pattern,
   fillet/chamfer, place occurrence и named-ref constraint. Их числовые поля используют те же
   ограниченные approved formulas; пути привязываются к job roots. `inventor_send_code` не участвует.
5. Clone/recode сначала возвращает dry-run file map. Native файлы создаются SaveCopyAs, copied parents
   перепривязываются, source iProperties/refs восстанавливаются, external shared refs не копируются.
   При сбое остаётся `INCOMPLETE.json`.
6. Параметрические mutations защищены transaction активного документа и отдельным bounded snapshot
   model/user expressions + part material всего referenced-tree. После `Update2(false)` любой failed check
   вызывает Abort + явное restore; любой restore error даёт `rolled_back=false`.
7. Motor/flange gate читает реальные `HoleFeature.HoleCenterPoints`, проверяет count, BCD, диаметр и шаг
   внутри той же rollback-границы. После PASS `vent_save_product` применяет `Document.Save2` только к dirty
   documents под approved product root; dirty external reference блокирует сохранение.
   Отдельный `sheet_metal_checks` открывает указанные IPT и требует model-side flat-pattern PASS до batch DXF.
8. Drawing output может требовать retrieved + overall dimensions, minimum balloons, Parts List, hole table,
   section/detail views и технические требования; неполный drawing даёт FAIL.
9. `release.py` независимо повторяет DXF checks, сверяет точный DXF export manifest, проверяет
   metadata/duplicate names/INCOMPLETE/PDF, формирует CSV/XLSX и manifest с SHA-256 каждого исходного
   артефакта.
10. Product Job останавливается в `awaiting_release_approval`. Только совпавший release digest +
    `approved_by` отправляет webhook и переводит задачу в `released`.
11. Потеря транспорта на write-границе — `uncertain`; автоматического retry нет. Checkpoint и JSONL audit
    пишутся после каждой стадии. Одновременные execute одного job объединяются.
12. Gateway использует bearer, allowlist, body/rate limits; `inventor_send_code` закрыт. Release worker
    запускается без shell, а все его входные product/DXF/PDF/output paths проверяются относительно
    `BIMWRIGHT_INVENTOR_EXPORT_ROOT`.

## Acceptance checks

- CAD: `model_health`, `part_cut_ready`, `constraints_healthy`, `no_interference`, `min_distance`,
  `physical_bounds`, mounting-hole count/diameter/BCD/angular spacing.
- Drawing: views, retrieved model dimensions, Parts List, balloons, hole table, section/detail recipes,
  технические требования, успешный IDW/PDF.
- Release: closed DXF contours, designation/material/thickness/qty, unique/expected DXF filenames,
  отсутствие `INCOMPLETE.json`, minimum PDF, clone/DXF manifests, полный XLSX row count и file hashes.

## Что нельзя честно получить без завода/Windows

- C# overlay на Mac не собирается. Clone/recode, drawing API, PDF translator, mounting pattern и rollback
  должны пройти acceptance на Windows с Inventor 2026.
- В репозитории нет утверждённых формул конкретного ВКР. Engine готов, но реальные parameter names,
  коэффициенты, двигатели, метизы и допуски нужно снять с КД/моделей и подписать как revision рецепта.
  Интернетом или LLM эти числа не заменяются.
- Компоновку drawings и стили надо один раз откалибровать на заводском IDW-шаблоне.
- Webhook отправляет нейтральный JSON (`text`, job/digest/evidence). Целевой чат должен принять его
  напрямую либо через штатный adapter kvz-ai.

## Windows acceptance

- 10 повторов одинаковой задачи дают тот же plan digest, порядок и PASS/FAIL.
- Плохой размер/разболтовка возвращает `rolled_back=true`; исходные expressions/material и dirty-state
  активной сборки и referenced `.ipt` восстановлены.
- После PASS `save_product` сохраняет активную `.iam` и все изменённые `.ipt`; ни одна внешняя dirty
  библиотечная ссылка не сохраняется.
- Clone manifest показывает, что internal refs copied IAM/IDW находятся под destination; source не сохранён.
- Повторное открытие saved model даёт те же mass/bbox/constraints/interference.
- Drawing содержит ожидаемые views/dimensions/Parts List/balloons/hole table и открывается без unresolved refs.
- Flat DXF проходит `analyze.py` и `compare.py` с утверждённым эталоном.
- Release digest воспроизводится на неизменном дереве и меняется при изменении любого файла.
- Webhook не вызывается до chief approval; после него получает `idempotency-key=job_id:digest`.
