# inverter_mcp — Ultra MCP для вентзавода

Управление Autodesk Inventor из чата: правка параметров изделий, разворот корпуса,
проверка контуров, габаритка, пакетный экспорт DXF на резку.

- **База:** форк [bimwright/ipt-mcp](https://github.com/bimwright/ipt-mcp) (.NET add-in, Inventor 2022–2027, Apache-2.0).
- **Архитектура и план:** [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).
- **C#/Inventor-часть** собирается и работает только на Windows с установленным Inventor.

## `dxf_tools/` — кросс-платформенный слой (работает без Inventor)

Анализ готовых DXF: спецификация + проверка перед резкой. Python + ezdxf.

```bash
python3 -m venv venv && ./venv/bin/pip install -r dxf_tools/requirements.txt
./venv/bin/python dxf_tools/analyze.py "путь/к/детали.dxf"      # одна деталь → JSON
./venv/bin/python dxf_tools/batch.py "путь/к/каталогу"           # весь каталог → spec.csv
./venv/bin/python dxf_tools/compare.py ЭТАЛОН.dxf НОВЫЙ.dxf      # регрессия .ipt→DXF ↔ эталон
./venv/bin/python dxf_tools/spec_xlsx.py --from-dxf "каталог" -o Спецификация.xlsx  # спец. по учёту металла
```

Извлекает: материал, толщину, кол-во, обозначение, габарит, площадь, длину реза,
отверстия (кол-во/диаметры), отступ от края; помечает проблемные детали.
Пример результата по 250 деталям: [docs/spec_sample.csv](docs/spec_sample.csv).
