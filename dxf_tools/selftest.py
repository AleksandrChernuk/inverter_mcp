#!/usr/bin/env python3
"""Автономный самотест dxf_tools для CI — без внешних файлов.
Создаёт DXF в temp, прогоняет analyze; проверяет spec_xlsx на фиксированных строках."""
import json, os, re, sys, tempfile
sys.path.insert(0, os.path.dirname(__file__))
import ezdxf
from analyze import analyze, parse_name
from spec_xlsx import write_spec
from release import build_release


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

        # complete release gate: checked DXF + PDF + hashes + approval digest
        product = os.path.join(d, "product")
        dxf_dir = os.path.join(product, "DXF")
        pdf_dir = os.path.join(product, "PDF")
        release_dir = os.path.join(product, "release")
        os.makedirs(dxf_dir); os.makedirs(pdf_dir)
        release_dxf = os.path.join(dxf_dir, "3мм Ст3 2шт КВЗ.ВКР-7.1.00.001 SELFTEST.dxf")
        make_dxf(release_dxf)
        with open(os.path.join(dxf_dir, "dxf-export-manifest.json"), "w", encoding="utf-8") as stream:
            json.dump({
                "state": "complete", "pass": True,
                "dxf_files": [os.path.basename(release_dxf)],
            }, stream)
        with open(os.path.join(pdf_dir, "КВЗ.ВКР-7.1.00.001.pdf"), "wb") as stream:
            stream.write(b"%PDF-1.4 selftest")
        report = build_release(
            product, dxf_dir, release_dir, "КВЗ.ВКР-7.1",
            pdf_dir=pdf_dir, minimum_pdf_count=1, required_name_pattern=r"КВЗ[.]ВКР",
            require_dxf_manifest=True,
        )
        if not report["pass"]: fails.append(f"release did not pass: {report['checks']}")
        if not re.fullmatch(r"[a-f0-9]{64}", report["release_digest"]): fails.append("bad release digest")
        if not os.path.exists(report["artifacts"]["xlsx"]): fails.append("release XLSX not written")
        repeated = build_release(
            product, dxf_dir, release_dir, "КВЗ.ВКР-7.1",
            pdf_dir=pdf_dir, minimum_pdf_count=1, required_name_pattern=r"КВЗ[.]ВКР",
            require_dxf_manifest=True,
        )
        if repeated["release_digest"] != report["release_digest"]:
            fails.append("unchanged release digest is not reproducible")
        with open(os.path.join(pdf_dir, "КВЗ.ВКР-7.1.00.001.pdf"), "ab") as stream:
            stream.write(b" changed")
        changed = build_release(
            product, dxf_dir, release_dir, "КВЗ.ВКР-7.1",
            pdf_dir=pdf_dir, minimum_pdf_count=1, required_name_pattern=r"КВЗ[.]ВКР",
            require_dxf_manifest=True,
        )
        if changed["release_digest"] == report["release_digest"]:
            fails.append("release digest did not change after a source artifact changed")

    if fails:
        print("SELFTEST FAILED:")
        for f in fails:
            print("  -", f)
        sys.exit(1)
    print("SELFTEST OK")


if __name__ == "__main__":
    main()
