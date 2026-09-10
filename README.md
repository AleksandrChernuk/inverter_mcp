# inverter_mcp — Ultra MCP для вентзавода

Управление Autodesk Inventor из чата: изолированное клонирование/перекодирование изделия,
формульные рецепты типоразмеров, создание деталей и сборок, проверки двигателя/зазоров,
чертежи и спецификации, PDF/DXF/Excel и контролируемый выпуск в цех.

- **База:** форк [bimwright/ipt-mcp](https://github.com/bimwright/ipt-mcp) (.NET add-in, Inventor 2022–2027, Apache-2.0).
- **Архитектура и план:** [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).
- **Автономный конструктор:** [docs/AUTONOMOUS_CONNECTOR.md](docs/AUTONOMOUS_CONNECTOR.md) —
  Product Job v2 → digest/approval → Pack-and-Go/recode → recipe → checks → PDF/DXF/Excel →
  release digest → согласование главного конструктора → сообщение в цех.
- **Покрытие этапов:** [docs/CAPABILITY_MATRIX.md](docs/CAPABILITY_MATRIX.md); правила реальных формул и
  КД — [docs/FAMILY_RECIPES.md](docs/FAMILY_RECIPES.md).
- **C#/Inventor-часть** собирается и работает только на Windows с установленным Inventor.

## `dxf_tools/` — кросс-платформенный слой (работает без Inventor)

Анализ готовых DXF: спецификация + проверка перед резкой. Python + ezdxf.

```bash
python3 -m venv venv && ./venv/bin/pip install -r dxf_tools/requirements.txt
./venv/bin/python dxf_tools/analyze.py "путь/к/детали.dxf"      # одна деталь → JSON
./venv/bin/python dxf_tools/batch.py "путь/к/каталогу"           # весь каталог → spec.csv
./venv/bin/python dxf_tools/compare.py ЭТАЛОН.dxf НОВЫЙ.dxf      # регрессия .ipt→DXF ↔ эталон
./venv/bin/python dxf_tools/spec_xlsx.py --from-dxf "каталог" -o Спецификация.xlsx  # спец. по учёту металла
./venv/bin/python dxf_tools/release.py --product-root "изделие" --dxf-dir "изделие/DXF" \
  --pdf-dir "изделие/PDF" --minimum-pdf-count 1 --output-dir "изделие/release" \
  --product-code "КВЗ.ВКР-7,1"                       # финальный release gate + SHA-256
```

Извлекает: материал, толщину, кол-во, обозначение, габарит, площадь, длину реза,
отверстия (кол-во/диаметры), отступ от края; помечает проблемные детали.
`release.py` дополнительно проверяет кодирование и структуру папок, формирует CSV/XLSX,
хеширует артефакты и не выдаёт статус готовности при любой ошибке.
Пример результата по 250 деталям: [docs/spec_sample.csv](docs/spec_sample.csv).
