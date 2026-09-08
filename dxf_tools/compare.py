#!/usr/bin/env python3
"""Сравнение двух DXF (эталон vs свежая выгрузка из .ipt) — регрессионный тест детали.

Использование:
    compare.py ЭТАЛОН.dxf НОВЫЙ.dxf [--tol-mm 0.5] [--json]

Сравнивает габарит, площадь, длину реза и набор отверстий (кол-во, диаметры, позиции)
в пределах допуска и выдаёт вердикт PASS / DIFF со списком расхождений.
"""
import sys, os, json, argparse, math
sys.path.insert(0, os.path.dirname(__file__))
from analyze import analyze
import ezdxf
from ezdxf import bbox as _bbox
from ezdxf.math import Vec2


def holes(path):
    doc = ezdxf.readfile(path)
    out = []
    for e in doc.modelspace():
        if e.dxftype() == "CIRCLE":
            out.append((e.dxf.center.x, e.dxf.center.y, round(e.dxf.radius * 2, 2)))
    return out


def match_holes(a, b, tol):
    """Жадное сопоставление отверстий по позиции+диаметру; вернуть (matched, only_a, only_b)."""
    b_left = list(b)
    matched, only_a = [], []
    for ha in a:
        best, bi = None, None
        for i, hb in enumerate(b_left):
            if abs(ha[2] - hb[2]) > tol:
                continue
            dist = math.hypot(ha[0] - hb[0], ha[1] - hb[1])
            if best is None or dist < best:
                best, bi = dist, i
        if bi is not None and best <= max(tol, 1.0):
            matched.append((ha, b_left.pop(bi), round(best, 3)))
        else:
            only_a.append(ha)
    return matched, only_a, b_left


def compare(ref, new, tol):
    ra, na = analyze(ref), analyze(new)
    diffs = []

    def chk(label, va, vb, t=tol):
        if va is None or vb is None:
            if va != vb:
                diffs.append(f"{label}: {va} vs {vb}")
            return
        if abs(va - vb) > t:
            diffs.append(f"{label}: {va} vs {vb} (Δ={round(vb - va, 2)})")

    chk("width_mm", ra["width_mm"], na["width_mm"])
    chk("height_mm", ra["height_mm"], na["height_mm"])
    chk("cut_length_mm", ra["cut_length_mm"], na["cut_length_mm"], t=max(tol, ra["cut_length_mm"] * 0.005))
    chk("area_mm2", ra["area_mm2"], na["area_mm2"], t=max(tol * 10, ra["area_mm2"] * 0.01))

    ha, hb = holes(ref), holes(new)
    matched, only_ref, only_new = match_holes(ha, hb, tol)
    if len(ha) != len(hb):
        diffs.append(f"holes: {len(ha)} vs {len(hb)}")
    for h in only_ref:
        diffs.append(f"отверстие есть в эталоне, нет в новом: Ø{h[2]} @({round(h[0],1)},{round(h[1],1)})")
    for h in only_new:
        diffs.append(f"отверстие есть в новом, нет в эталоне: Ø{h[2]} @({round(h[0],1)},{round(h[1],1)})")
    for m in matched:
        if m[2] > tol:
            diffs.append(f"отверстие сдвинуто на {m[2]} мм: Ø{m[0][2]}")

    return {
        "ref": os.path.basename(ref),
        "new": os.path.basename(new),
        "tolerance_mm": tol,
        "verdict": "PASS" if not diffs else "DIFF",
        "holes_ref": len(ha), "holes_new": len(hb), "holes_matched": len(matched),
        "diffs": diffs,
    }


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("ref"); ap.add_argument("new")
    ap.add_argument("--tol-mm", type=float, default=0.5)
    ap.add_argument("--json", action="store_true")
    a = ap.parse_args()
    r = compare(a.ref, a.new, a.tol_mm)
    if a.json:
        print(json.dumps(r, ensure_ascii=False, indent=2))
    else:
        print(f"{r['verdict']}  {r['ref']}  ↔  {r['new']}  (tol {r['tolerance_mm']} мм)")
        print(f"  отверстия: эталон {r['holes_ref']} / новый {r['holes_new']} / совпало {r['holes_matched']}")
        for d in r["diffs"]:
            print("  •", d)
    sys.exit(0 if r["verdict"] == "PASS" else 1)
