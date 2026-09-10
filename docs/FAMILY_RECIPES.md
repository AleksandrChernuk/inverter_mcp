# Family recipes: от КД к исполняемому типоразмеру

Рецепт — это контролируемая замена ручному «подогнать по пропорциям». Он хранится в Product Job v2 и
входит в plan digest. Агент не выводит коэффициенты по внешнему виду модели и не переносит числа из X.

## Формулы

Каждый binding имеет target (`active_parameter`, `component_parameter`, `constraint`), unit и формулу:

```text
value = constant + Σ(recipe.variables[name] × coefficient[name])
```

Можно задать `round_to`. Все coefficients, parameter/occurrence names и диапазоны переменных снимаются с
утверждённых моделей/чертежей, проверяются главным конструктором и получают неизменяемый `revision`.

`inventor_recipe_template_digest` считает candidate SHA-256 по family/revision, `variable_limits`, bindings
и construction steps. Он не является утверждением. В production Product Job v2 принимается только если
digest совпадает с `recipe.approval_digest` и заранее внесён администратором в
`APPROVED_RECIPE_DIGESTS` после подписи главного конструктора.

Для нелинейной или табличной зависимости завод должен заранее вычислить проверенное значение и передать
его отдельной variable либо разделить семейство на несколько revision. Исполнение произвольного выражения
или кода намеренно запрещено.

## Создание с нуля и сборка

`construction_steps` поддерживает ограниченный набор:

- document: new/open/save/close, user parameters, material и ограниченный набор iProperties;
- sketch: project, line/circle/rectangle/arc, dimensions, geometric constraints и close;
- feature: work planes/axes, extrude/revolve/hole, circular/rectangular pattern, fillet/chamfer;
- assembly: iMate, place occurrence и named-ref mate/flush/insert/angle constraint.

Каждый step имеет уникальный `id`. Следующий step может получить стабильное строковое поле предыдущего
результата через `{ "from_step": "id", "path": "field" }`, например имя только что созданного sketch.
Координаты и длины в recipe — мм, углы — градусы. Числовое поле construction-step принимает либо
фиксированное число, либо `{ "formula": { "constant": ..., "terms": ... } }`; поэтому один
утверждённый шаблон построения работает для всего разрешённого диапазона типоразмеров, а не только для
одного заказа. Formula terms также обязаны присутствовать в `variable_limits`.

Пути нового дерева не зашиваются под один код изделия. `open_document`, `save_document` и
`place_occurrence` принимают `{ "job_path": "product_path" }` либо
`{ "job_root": "destination_root", "relative_template": "parts/{target_code}-wheel.ipt" }`.
Разрешены только job-токены `target_code`/`product_code`, а выход за утверждённый корень отклоняется.

Метизы ставятся теми же assembly steps: путь к утверждённому библиотечному `.ipt`, затем constraints по
iMate/work-feature. Геометрические face indexes и локализованные `XY Plane` в production recipe не
используются без отдельной проверки.

## Drawing recipe

Каждый output `gabarit` может указать model/template/output, auto или фиксированный scale, извлечение
model dimensions, ассоциативные overall width/height, Parts List, minimum balloons, hole table,
technical notes и массивы section/detail views.
Координаты section/detail задаются в мм листа и калибруются на конкретном заводском шаблоне.

## Порядок ввода семейства

1. На копии шаблона выполнить inspect и выписать driving parameters/named refs.
2. Главный конструктор утверждает variables, formulas, materials, motor/fastener catalog и допуски.
3. Собрать recipe revision и выполнить dry-run/digest review.
4. Прогнать минимум хороший, граничный и заведомо плохой типоразмер; плохой обязан rollback/fail.
5. Проверить каждый IDW/PDF/DXF, Excel и release manifest.
6. Только после этого разрешить revision в production registry kvz-ai.
