#!/usr/bin/env python3
"""Prototype DXF analyzer for the vent-factory flat parts: spec + pre-cut checks."""
import sys, re, math, json
import ezdxf
from ezdxf import bbox
from ezdxf.math import Vec2

# Two naming conventions seen in the catalog:
#   A) "10мм ст09Г2С 4шт. ДН-26.02.01.04.007 Планка.dxf"   (th, mat, qty first)
#   B) "2мм КВЗ.ВКРН-5-ДВ.05.00.00.001 Ребро Ст3 2Шт.dxf"   (th first, mat+qty last)
TH = re.compile(r'([\d]+(?:[.,]\d+)?)\s*мм', re.I)
QTY = re.compile(r'(\d+)\s*шт', re.I)
MATS = ["ст09Г2С", "09Г2С", "Ст3сп", "Ст3", "СТ3", "Ст3", "AISI304", "12Х18Н10Т", "Zn", "Ст"]

def parse_name(fn):
    d = {"thickness_mm": None, "material": None, "qty": None}
    m = TH.search(fn)
    if m:
        d["thickness_mm"] = float(m.group(1).replace(",", "."))
    q = QTY.search(fn)
    if q:
        d["qty"] = int(q.group(1))
    for mat in MATS:
        if re.search(re.escape(mat), fn, re.I):
            d["material"] = mat
            break
    # designation like ДН-26.02.01.04.007 or КВЗ.ВКРН-5-ДВ.05.00.00.001
    dm = re.search(r'([А-ЯA-Z]{2,4}[.\-][.\-\dА-ЯA-Z,]{4,})', fn)
    d["designation"] = dm.group(1).rstrip(" .") if dm else None
    return d

def poly_len_area(pts, closed):
    n = len(pts)
    if n < 2:
        return 0.0, 0.0
    L = 0.0
    for i in range(n - 1):
        L += (pts[i+1] - pts[i]).magnitude
    if closed:
        L += (pts[0] - pts[-1]).magnitude
    # shoelace
    A = 0.0
    for i in range(n):
        x1, y1 = pts[i]
        x2, y2 = pts[(i+1) % n]
        A += x1*y2 - x2*y1
    return L, abs(A)/2.0

def analyze(path):
    doc = ezdxf.readfile(path)
    msp = doc.modelspace()
    res = {"file": path.split("/")[-1]}
    res.update(parse_name(path.split("/")[-1]))

    cut_len = 0.0        # total cut path length (contour + holes + interior)
    contour_len = 0.0    # longest closed loop = outer contour
    area = 0.0
    holes = []           # circles: (cx, cy, d)
    open_contours = 0
    ent_counts = {}

    for e in msp:
        t = e.dxftype()
        ent_counts[t] = ent_counts.get(t, 0) + 1
        if t == "CIRCLE":
            d = e.dxf.radius * 2
            holes.append((e.dxf.center.x, e.dxf.center.y, d))
            cut_len += math.pi * d
        elif t in ("LWPOLYLINE", "POLYLINE"):
            if t == "LWPOLYLINE":
                pts = [Vec2(p[0], p[1]) for p in e.get_points()]
                closed = bool(e.closed)
            else:
                pts = [Vec2(v.dxf.location.x, v.dxf.location.y) for v in e.vertices]
                closed = bool(e.is_closed)
            L, A = poly_len_area(pts, closed)
            cut_len += L
            if not closed:
                open_contours += 1
            if A > area:
                area = A
                contour_len = L
        elif t == "LINE":
            cut_len += (Vec2(e.dxf.end.x, e.dxf.end.y) - Vec2(e.dxf.start.x, e.dxf.start.y)).magnitude
        elif t == "ARC":
            ang = abs(e.dxf.end_angle - e.dxf.start_angle) % 360
            cut_len += math.radians(ang) * e.dxf.radius

    # bounding box over all geometry
    bb = bbox.extents(msp, fast=True)
    if bb.has_data:
        w = bb.size.x; h = bb.size.y
    else:
        w = h = 0.0

    # pre-cut checks
    checks = []
    if open_contours:
        checks.append(f"OPEN_CONTOUR:{open_contours} незамкнутых контура(ов)")
    # min hole edge distance to bbox border (rough proxy for edge distance)
    min_edge = None
    if holes and bb.has_data:
        x0, y0 = bb.extmin.x, bb.extmin.y
        x1, y1 = bb.extmax.x, bb.extmax.y
        for cx, cy, d in holes:
            r = d/2
            dist = min(cx-x0-r, x1-cx-r, cy-y0-r, y1-cy-r)
            min_edge = dist if min_edge is None else min(min_edge, dist)
        th = res.get("thickness_mm") or 0
        if th and min_edge is not None and min_edge < th:
            checks.append(f"HOLE_NEAR_EDGE: отверстие {min_edge:.1f}мм от края < толщины {th}мм")

    res.update({
        "width_mm": round(w, 2), "height_mm": round(h, 2),
        "area_mm2": round(area, 1),
        "cut_length_mm": round(cut_len, 1),
        "holes": len(holes),
        "hole_diams": sorted({round(d, 1) for _, _, d in holes}),
        "min_hole_edge_mm": round(min_edge, 2) if min_edge is not None else None,
        "dxf_version": doc.dxfversion,
        "checks": checks or ["OK"],
    })
    return res

if __name__ == "__main__":
    for p in sys.argv[1:]:
        try:
            print(json.dumps(analyze(p), ensure_ascii=False))
        except Exception as ex:
            print(json.dumps({"file": p.split("/")[-1], "error": str(ex)}, ensure_ascii=False))
