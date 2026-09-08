#!/usr/bin/env python3
"""Prototype DXF analyzer for the vent-factory flat parts: spec + pre-cut checks.

Geometry engine (v2): stitches LINE/ARC/POLYLINE segments into closed loops, so a
contour drawn as separate segments is recognized as one loop. The largest-area loop is
the OUTER contour; loops whose centroid lies inside it are HOLES. Net area = outer - holes
(this is what drives mass). Hole-to-edge distance is measured against the real outer
contour, not the bounding box.
"""
import sys, re, math, json
import ezdxf
from ezdxf import bbox
from ezdxf.math import Vec2, bulge_to_arc

# --- flattening / stitching tolerances (mm) ---
SAG = 0.3    # max sagitta when flattening arcs/circles/splines to polylines
TOL = 0.15   # endpoint match tolerance when stitching segments into loops

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

# ---------------------------------------------------------------------------
# geometry helpers
# ---------------------------------------------------------------------------
def _circle_pts(cx, cy, r):
    """Polygon approximation of a full circle (manual, no ezdxf flattening dependency)."""
    if r <= 0:
        return []
    try:
        n = int(math.ceil(math.pi / math.acos(max(-1.0, min(1.0, 1.0 - SAG / r)))))
    except Exception:
        n = 48
    n = max(24, min(n, 360))
    return [Vec2(cx + r*math.cos(2*math.pi*i/n), cy + r*math.sin(2*math.pi*i/n)) for i in range(n)]

def _step_deg(r):
    """Angular step (deg) that keeps sagitta <= SAG for radius r."""
    try:
        return max(1.0, min(30.0, math.degrees(math.acos(max(-1.0, min(1.0, 1.0 - SAG / r))))))
    except Exception:
        return 10.0

def _lwpoly_points(verts, closed):
    """Expand (x, y, bulge) vertices into a point list, turning bulge segments into arcs.
    ezdxf's LWPolyline has no .flattening() in this version, so we do it manually."""
    v = [(float(x), float(y), float(b)) for x, y, b in verts]
    n = len(v)
    if n < 2:
        return [Vec2(x, y) for x, y, _ in v]
    out = []
    segs = n if closed else n - 1
    for i in range(segs):
        x1, y1, b = v[i]
        x2, y2, _ = v[(i + 1) % n]
        p1, p2 = Vec2(x1, y1), Vec2(x2, y2)
        if abs(b) < 1e-9 or (p2 - p1).magnitude < 1e-9:
            out.append(p1)
            continue
        try:
            center, sa, ea, r = bulge_to_arc(p1, p2, b)
            # use the exact included angle from the bulge (signed CCW); avoids
            # angle-wraparound errors that can flip a small arc into a near-full circle
            theta = 4.0 * math.atan(b)
            steps = max(2, int(math.ceil(abs(theta) / math.radians(_step_deg(r)))))
            for k in range(steps):
                ang = sa + theta * k / steps
                out.append(Vec2(center.x + r * math.cos(ang), center.y + r * math.sin(ang)))
        except Exception:
            out.append(p1)
    if not closed:
        out.append(Vec2(v[-1][0], v[-1][1]))
    return out

def _arc_pts(cx, cy, r, a0_deg, a1_deg):
    """Polyline approximation of an arc from a0 to a1 (degrees, CCW)."""
    if r <= 0:
        return []
    sweep = (a1_deg - a0_deg) % 360
    if sweep == 0:
        sweep = 360
    try:
        step = math.degrees(math.acos(max(-1.0, min(1.0, 1.0 - SAG / r))))
    except Exception:
        step = 5.0
    step = max(1.0, min(step, 30.0))
    n = max(2, int(math.ceil(sweep / step)))
    return [Vec2(cx + r*math.cos(math.radians(a0_deg + sweep*i/n)),
                 cy + r*math.sin(math.radians(a0_deg + sweep*i/n))) for i in range(n + 1)]

def _iter_primitives(entities, depth=0):
    """Yield primitive entities, expanding INSERT (block) references to WCS via virtual_entities."""
    for e in entities:
        if e.dxftype() == "INSERT" and depth < 6:
            try:
                yield from _iter_primitives(e.virtual_entities(), depth + 1)
                continue
            except Exception:
                pass
        yield e

def _flatten(e):
    """Return (points[list[Vec2]], already_closed[bool]) for a boundary entity, or None."""
    t = e.dxftype()
    try:
        if t == "LINE":
            return [Vec2(e.dxf.start.x, e.dxf.start.y), Vec2(e.dxf.end.x, e.dxf.end.y)], False
        if t == "ARC":
            return _arc_pts(e.dxf.center.x, e.dxf.center.y, e.dxf.radius,
                            e.dxf.start_angle, e.dxf.end_angle), False
        if t == "CIRCLE":
            return _circle_pts(e.dxf.center.x, e.dxf.center.y, e.dxf.radius), True
        if t == "ELLIPSE":
            pts = [Vec2(p.x, p.y) for p in e.flattening(SAG)]
            closed = len(pts) > 2 and (pts[0] - pts[-1]).magnitude <= TOL
            return pts, closed
        if t == "LWPOLYLINE":
            return _lwpoly_points(e.get_points('xyb'), bool(e.closed)), bool(e.closed)
        if t == "POLYLINE":
            verts = [(v.dxf.location.x, v.dxf.location.y, getattr(v.dxf, 'bulge', 0.0)) for v in e.vertices]
            return _lwpoly_points(verts, bool(e.is_closed)), bool(e.is_closed)
        if t == "SPLINE":
            pts = [Vec2(p.x, p.y) for p in e.flattening(SAG)]
            closed = len(pts) > 2 and (pts[0] - pts[-1]).magnitude <= TOL
            return pts, closed
    except Exception:
        return None
    return None

def _perimeter(pts, closed=True):
    L = 0.0
    for i in range(len(pts) - 1):
        L += (pts[i+1] - pts[i]).magnitude
    if closed and len(pts) > 1:
        L += (pts[0] - pts[-1]).magnitude
    return L

def _signed_area(pts):
    A = 0.0
    n = len(pts)
    for i in range(n):
        x1, y1 = pts[i]
        x2, y2 = pts[(i+1) % n]
        A += x1*y2 - x2*y1
    return A / 2.0

def _centroid(pts):
    return Vec2(sum(p.x for p in pts)/len(pts), sum(p.y for p in pts)/len(pts))

def _point_in_poly(p, poly):
    """Ray-casting point-in-polygon test."""
    inside = False
    n = len(poly)
    j = n - 1
    for i in range(n):
        xi, yi = poly[i]; xj, yj = poly[j]
        if ((yi > p.y) != (yj > p.y)) and \
           (p.x < (xj - xi) * (p.y - yi) / ((yj - yi) or 1e-12) + xi):
            inside = not inside
        j = i
    return inside

def _dist_point_seg(p, a, b):
    ab = b - a
    denom = ab.dot(ab)
    t = 0.0 if denom == 0 else max(0.0, min(1.0, (p - a).dot(ab) / denom))
    return (p - (a + ab * t)).magnitude

def _min_dist_point_poly(p, poly):
    return min(_dist_point_seg(p, poly[i], poly[(i+1) % len(poly)]) for i in range(len(poly)))

def _stitch(segments):
    """Greedily chain open segments (each a list of Vec2) into loops via endpoint matching.
    Returns (closed_loops, open_chains)."""
    segs = [list(s) for s in segments if len(s) >= 2]
    used = [False] * len(segs)
    loops, open_chains = [], []
    for i in range(len(segs)):
        if used[i]:
            continue
        used[i] = True
        chain = list(segs[i])
        extended = True
        while extended:
            extended = False
            for j in range(len(segs)):
                if used[j]:
                    continue
                s = segs[j]
                if (chain[-1] - s[0]).magnitude <= TOL:
                    chain.extend(s[1:]); used[j] = True; extended = True
                elif (chain[-1] - s[-1]).magnitude <= TOL:
                    chain.extend(reversed(s[:-1])); used[j] = True; extended = True
                elif (chain[0] - s[-1]).magnitude <= TOL:
                    chain[:0] = s[:-1]; used[j] = True; extended = True
                elif (chain[0] - s[0]).magnitude <= TOL:
                    chain[:0] = list(reversed(s[1:])); used[j] = True; extended = True
            if (chain[0] - chain[-1]).magnitude <= TOL and len(chain) >= 3:
                break
        if (chain[0] - chain[-1]).magnitude <= TOL and len(chain) >= 3:
            loops.append(chain)
        else:
            open_chains.append(chain)
    return loops, open_chains

# ---------------------------------------------------------------------------
def analyze(path):
    doc = ezdxf.readfile(path)
    msp = doc.modelspace()
    res = {"file": path.replace("\\", "/").split("/")[-1]}
    res.update(parse_name(res["file"]))

    loops = []           # closed loops (list[Vec2])
    open_segs = []       # open segments to stitch
    circles = []         # (center, diameter) for exact round-hole reporting
    ent_counts = {}

    for e in _iter_primitives(msp):
        t = e.dxftype()
        ent_counts[t] = ent_counts.get(t, 0) + 1
        if t == "CIRCLE":
            circles.append((Vec2(e.dxf.center.x, e.dxf.center.y), e.dxf.radius * 2.0))
        fl = _flatten(e)
        if fl is None:
            continue
        pts, closed = fl
        if len(pts) < 2:
            continue
        if closed and len(pts) >= 3:
            loops.append(pts)
        else:
            open_segs.append(pts)

    stitched, open_chains = _stitch(open_segs)
    loops.extend(stitched)

    # significant leftover open chains (ignore tiny noise/witness lines)
    open_contours = sum(1 for c in open_chains if _perimeter(c, closed=False) > 1.0)

    # total cut path = every closed loop perimeter + leftover open chain lengths
    cut_len = sum(_perimeter(l, True) for l in loops) + \
              sum(_perimeter(c, False) for c in open_chains)

    area = 0.0
    holes = []           # (centroid, area, equiv_diam, points)
    if loops:
        loop_area = [abs(_signed_area(l)) for l in loops]
        oi = max(range(len(loops)), key=lambda k: loop_area[k])
        outer = loops[oi]
        outer_area = loop_area[oi]
        hole_area_sum = 0.0
        for k, l in enumerate(loops):
            if k == oi:
                continue
            a = loop_area[k]
            # skip near-duplicates of the outer boundary (same contour drawn twice / concentric)
            if a >= 0.98 * outer_area:
                continue
            c = _centroid(l)
            if _point_in_poly(c, outer):
                hole_area_sum += a
                deq = 2.0 * math.sqrt(a / math.pi) if a > 0 else 0.0
                for cc, cd in circles:          # prefer exact diameter for round holes
                    if (cc - c).magnitude <= max(TOL, 0.02 * cd):
                        deq = cd
                        break
                holes.append((c, a, deq, l))
        area = max(0.0, outer_area - hole_area_sum)
    else:
        outer = None

    # bounding box over all geometry (overall flat size)
    bb = bbox.extents(msp, fast=True)
    w = bb.size.x if bb.has_data else 0.0
    h = bb.size.y if bb.has_data else 0.0

    # min hole-to-edge distance, measured against the real outer contour
    min_edge = None
    if holes and outer:
        for _, _, _, hp in holes:
            for p in hp:
                d = _min_dist_point_poly(p, outer)
                min_edge = d if min_edge is None else min(min_edge, d)

    checks = []
    if not loops:
        checks.append("NO_CLOSED_CONTOUR: не удалось собрать ни одного замкнутого контура")
    if open_contours:
        checks.append(f"OPEN_CONTOUR:{open_contours} незамкнутых контура(ов)")
    th = res.get("thickness_mm") or 0
    if th and min_edge is not None and min_edge + 1e-6 < th:
        checks.append(f"HOLE_NEAR_EDGE: отверстие {min_edge:.1f}мм от края < толщины {th}мм")

    res.update({
        "width_mm": round(w, 2), "height_mm": round(h, 2),
        "area_mm2": round(area, 1),
        "cut_length_mm": round(cut_len, 1),
        "holes": len(holes),
        "hole_diams": sorted({round(d, 1) for _, _, d, _ in holes}),
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
            print(json.dumps({"file": p.replace("\\", "/").split("/")[-1], "error": str(ex)}, ensure_ascii=False))
