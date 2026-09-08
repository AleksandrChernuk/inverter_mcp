#!/usr/bin/env python3
"""Автономный самотест dxf_tools для CI — без внешних файлов.
Создаёт DXF в temp, прогоняет analyze; проверяет spec_xlsx на фиксированных строках."""
import os, sys, tempfile
sys.path.insert(0, os.path.dirname(__file__))
import ezdxf
from analyze import analyze, parse_name
from spec_xlsx import write_spec


def make_dxf(path):
    doc = ezdxf.new("R2018")
    doc.header["$INSUNITS"] = 4  # mm
    msp = doc.modelspace()
    msp.add_lwpolyline([(0, 0), (100, 0), (100, 50), (0, 50)], close=True)  # 100x50 контур
    msp.add_circle((20, 25), radius=5)   # Ø10
    msp.add_circle((80, 25), radius=5)   # Ø10
    doc.saveas(path)


def approx(a, b, tol=0.01):
    return abs(a - b) <= tol


def main():
    fails = []
    with tempfile.TemporaryDirectory() as d:
        p = os.path.join(d, "3мм Ст3 2шт SELFTEST.dxf")
        make_dxf(p)
        a = analyze(p)
        if not approx(a["width_mm"], 100): fails.append(f"width {a['width_mm']} != 100")
        if not approx(a["height_mm"], 50): fails.append(f"height {a['height_mm']} != 50")
        if a["holes"] != 2: fails.append(f"holes {a['holes']} != 2")
        if a["hole_diams"] != [10.0]: fails.append(f"hole_diams {a['hole_diams']} != [10.0]")
        if a["thickness_mm"] != 3.0: fails.append(f"thickness {a['thickness_mm']} != 3.0")
        if a["qty"] != 2: fails.append(f"qty {a['qty']} != 2")

        # spec_xlsx round-trip
        out = os.path.join(d, "spec.xlsx")
        total = write_spec([
            {"material": "Лист 4 ст 3пс", "mass": 9.556, "qty": 1},
            {"material": "Кутник 40(3) ст 3пс", "mass": 1.221, "qty": 8},
        ], out)
        expected = round(9.556 + 1.221 * 8, 4)
        if not approx(total, expected): fails.append(f"spec total {total} != {expected}")
        if not os.path.exists(out): fails.append("spec.xlsx not written")

    if fails:
        print("SELFTEST FAILED:")
        for f in fails:
            print("  -", f)
        sys.exit(1)
    print("SELFTEST OK")


if __name__ == "__main__":
    main()
