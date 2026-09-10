# Реальный маршрут конструктора (ВКР 7,1 из ВКР 6,3) → тулзы MCP

Записано со слов конструктора (пример ВКР №7,1, базовая комплектация, простой вентилятор).
Это приёмочный сквозной сценарий, а не единственный канон работы. Другие сімейства/комплектации
подключаются отдельными versioned recipes. Не всё автоматизируется одинаково — колонка
«Реализуемость» честно это разделяет.

## Структура реального изделия (подтверждена по папке ВКР №7,1)
`.ipj` (проект Inventor), top `…00.00.000 СК Вентилятор.iam/.idw/.pdf/.stp`, узлы
`01.01.000 Робоче колесо`, `02.01.000 Дифузор`, `07.00.000 Ковпак`, `00.03.000 Рама`,
папки `Библиотека` (покупные/крепёж), `OldVersions`, `!Архів`, `Специфікація.xlsx`.
Итого ~56 .ipt, ~45 .idw, ~16 dxf, ~23 pdf. Кодировка: `ВКР 7,1 УЗ.ДЕ.ПОЗ Название`.

## Шаги процесса и покрытие тулзами
| # | Шаг конструктора | Тулза / подход | Реализуемость |
|---|---|---|---|
| 1 | Взять базу (ВКР 6,3) и сделать **Pack and Go** — не рисовать с нуля | `vent_clone_recode_product`: dry-run → SaveCopyAs → ReplaceReference → manifest | Реализовано; Windows acceptance |
| 2 | Переименовать все файлы по кодировке (в проводнике **и** в свойствах документа) | тот же тул: file map + Part Number/Stock Number/Description/Title | Реализовано; Windows acceptance |
| 3 | Проверить размеры, начать подгонку **с рабочего колеса** (типоразмер) | Product Job v2 → signed family recipe → formula bindings → `execute_plan` | Реализован engine; нужны заводские формулы |
| 4 | Подогнать остальные по пропорциям; менять **толщину/тип металла** (рама, дифузор, обребровка, разболтовка) | recipe bindings: top/component parameters, constraints, material | Реализован engine; нужны заводские bindings |
| 5 | Собрать узлы → основная сборка (сначала без метизов) | typed construction steps: new/open/save assembly, place occurrence, named-ref constraints | Реализован engine; нужны assembly recipes |
| 6 | Проверить отверстия под двигатель, зазоры, что ничего не мешает | `check_mounting_pattern` + `check_interference` + `measure_min_distance` | Реализовано; нужны размеры двигателя |
| 7 | Файлы с плохой геометрией — перемоделировать с нуля | typed part recipe: constrained sketches/work geometry → features/patterns/iProperties | Базовые/средние детали; сложным нужны новые typed tools |
| 8 | Прикрепить метизы | recipe `place_occurrence` + named iMate/work-feature constraints | Реализован engine; нужны библиотечные пути/refs |
| 9 | **Чертежи**: деталировка, сборочные, размеры, техтребования, разрезы/выноски | drawing recipe: projections, Retrieve, section/detail, Parts List, balloons, hole table, notes | Реализовано; нужна калибровка шаблона |
| 10 | Спецификации в модели + правки «хвостов»; позиции на чертеже | `bom_report`, Parts List и balloons; minimum gates | Реализовано; BOM-сверка по evidence |
| 11 | Экспорт **PDF** чертежей по правильной кодировке | `vent_batch_pdf_drawings` с сохранением иерархии | Реализовано; Windows acceptance |
| 12 | Экспорт **DXF** для лазера с правильной иерархией/кодировкой | `vent_batch_flat_dxf`: nested parts + thickness/material/qty/Part Number | Реализовано; material aliases калибруются |
| 13 | Габаритное (монтажное) → сборочное всего изделия с покупными/узлами/техчастью | тот же drawing recipe с assembly Parts List/balloons/notes | Реализовано; recipe-specific |
| 14 | Проверка спецификации и кодировки; проверка папок изделия | `dxf_tools/release.py`: metadata, duplicates, incomplete markers, PDF/DXF, hashes | Реализовано |
| 15 | Выгрузка спецификации в **Excel** по правилам учёта металла | release gate вызывает `spec_xlsx.write_spec` автоматически | Реализовано и self-tested |
| 16 | Отписаться в чат «цех-конструктора» (сначала на проверку гл. конструктору) | `awaiting_release_approval` → exact digest + approved_by → webhook → `released` | Реализовано; настроить webhook |

## Выводы для дорожной карты
- **Pack and Go/recode** теперь одна атомарно планируемая стадия, но её нужно принять на Windows по
  реальным абсолютным ссылкам и заводскому `.ipj`.
- **Формулы и drawing coordinates не берутся из интернета.** Они должны быть извлечены из утверждённых
  моделей/КД, записаны в family recipe и подписаны главным конструктором как конкретная revision.
- **LLM не получает произвольный код.** Новые детали, сборки и метизы строятся ограниченными typed steps;
  связи задаются устойчивыми named work features/iMates.
- **Готовность = `released`, не «агент сказал готово».** До этого нужны CAD checks, deep DXF gate,
  file manifest и второй digest approval.
