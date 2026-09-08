#!/usr/bin/env python3
"""Генератор спецификации в XLSX по правилам учёта металла КВЗ.

Формат (как в реальном Специфікація.xlsx): A=Материал (с кодом /*NNNNNN), B=Масса ед. (кг),
C=КОЛ., D=Одиниця (кг), E=Загальна (B*C). Внизу: итог массы + строка «Дріт зварювальний» = 1%.

Использование как библиотеки:
    from spec_xlsx import write_spec
    write_spec(rows, "out.xlsx")   # rows = [{"material","mass","qty"}]

Или из DXF-папки (масса = площадь*толщина*плотность):
    spec_xlsx.py --from-dxf "каталог" -o out.xlsx
"""
import os, json, argparse
import openpyxl

HERE = os.path.dirname(__file__)
with open(os.path.join(HERE, "materials.json"), encoding="utf-8") as f:
    MAT = json.load(f)
CODES = MAT["codes"]
DENSITY = MAT["_density_kg_mm3"]
WW_FRAC = MAT["welding_wire_fraction"]
WW_NAME = MAT["welding_wire_name"]


def with_code(material: str) -> str:
    """Добавить код склада к названию материала, если он известен и ещё не проставлен."""
    if "/*" in material:
        return material
    base = material.strip()
    code = CODES.get(base)
    return f"{base}/*{code}" if code else base


def write_spec(rows, out_path, add_welding_wire=True):
    """rows: [{'material': str, 'mass': float (кг/ед), 'qty': int}]. Возвращает итог массы."""
    wb = openpyxl.Workbook()
    ws = wb.active
    ws.title = "Спецификация"
    ws.append(["Материал", "Масса", "КОЛ.", "Одиниця", "Загальна к-сть"])

    total = 0.0
    for r in rows:
        mass = float(r["mass"]); qty = int(r["qty"])
        line = round(mass * qty, 4)
        total += line
        ws.append([with_code(r["material"]), mass, qty, "кг", line])

    total = round(total, 4)
    ws.append([])                                    # пустая
    ws.append(["", "", "", "", total])               # итог массы (как E28)
    if add_welding_wire:
        ww = round(total * WW_FRAC, 4)
        ws.append([with_code(WW_NAME), "", "", "кг", ww, "", "Маса, кг.", total])
    wb.save(out_path)
    return total


def rows_from_dxf(folder):
    """Собрать строки из DXF: масса = площадь(мм²)*толщина(мм)*плотность. Материал — из имени."""
    import analyze as A
    rows = []
    for dp, _, fns in os.walk(folder):
        for fn in fns:
            if not fn.lower().endswith(".dxf"):
                continue
            try:
                a = A.analyze(os.path.join(dp, fn))
            except Exception:
                continue
            th = a.get("thickness_mm"); area = a.get("area_mm2") or 0
            if not th or not area:
                continue
            mass = round(area * th * DENSITY, 4)
            mat = f"Лист {str(th).rstrip('0').rstrip('.')} {a.get('material') or 'ст 3пс'}"
            rows.append({"material": mat, "mass": mass, "qty": a.get("qty") or 1})
    return rows


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--from-dxf", metavar="DIR")
    ap.add_argument("-o", "--out", default="Спецификация.xlsx")
    a = ap.parse_args()
    if a.from_dxf:
        rows = rows_from_dxf(a.from_dxf)
        total = write_spec(rows, a.out)
        print(f"rows={len(rows)} total_mass_kg={total} -> {a.out}")
    else:
        print("нужен --from-dxf DIR (или используйте write_spec как библиотеку)")
