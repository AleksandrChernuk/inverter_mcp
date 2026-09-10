# Матрица полного процесса конструктора

`Реализовано` означает, что есть исполняемый код и автоматические тесты там, где платформа доступна.
Все Inventor API строки дополнительно имеют обязательный статус `Windows acceptance`, потому что C# add-in
невозможно собрать/запустить на машине разработки macOS. `Нужен recipe` означает не недостающий код, а
отсутствующие заводские формулы/параметры/допуски, которые нельзя безопасно угадать.

| Этап | Покрытие | Что является доказательством |
|---|---|---|
| Открыть шаблон | Реализовано | path/document type |
| Изменить параметры, материал, толщину, разворот | Реализовано через recipe/transaction | Update2 + health/bounds |
| Откатить неудачную модель | Реализовано; Windows acceptance | transaction + referenced-tree snapshot, `rolled_back=true` |
| Масса, габариты, constraints, зазоры, пересечения | Реализовано | численные acceptance checks |
| Листовая деталь перед DXF | Реализовано model-side по списку parts + deep DXF | flat-pattern evidence + contour report |
| Pack and Go и ссылки | Реализовано; Windows acceptance | dry-run map + clone manifest + copied refs |
| Файлы и iProperties по коду | Реализовано; Windows acceptance | mapping + Part/Stock/Description/Title |
| Размеры нового типоразмера | Engine реализован; нужен утверждённый family recipe | revision + variables + resolved mutations |
| Перестроить деталь с нуля | Typed engine для constrained sketches/basic features; сложной геометрии нужен новый reviewed tool | step ledger + model health |
| Узлы, основная сборка, метизы | Typed engine реализован; нужны paths/named refs | occurrence/constraint ledger |
| Двигатель, отверстия, разболтовка | Реализовано; нужны данные двигателя | HoleFeature count/diameter/BCD/step внутри rollback-границы + clearance |
| Виды, разрезы, выноски, размеры | Реализовано; нужна калибровка IDW recipe | retrieved + overall dimensions, view counts |
| Parts List, positions, balloons, техтребования | Реализовано | minimum gates + IDW/PDF |
| Пакетный PDF | Реализовано; Windows acceptance | per-drawing export ledger |
| Плоский DXF | Реализовано | nested parts, BOM qty, metadata name, exact export manifest + offline checks |
| Excel учёта металла | Реализовано и self-tested | CSV/XLSX + total mass |
| Сохранить изменённые детали и сборку | Реализовано; Windows acceptance | bounded `Document.Save2`, exact dirty ledger, external refs blocked |
| Папки, согласование, сообщение | Реализовано; настроить webhook | release report/digest/approved_by/status released |

Живое производство разрешено только после прохождения чек-листа Windows acceptance из
`AUTONOMOUS_CONNECTOR.md` и регистрации утверждённой revision family/drawing recipe.
