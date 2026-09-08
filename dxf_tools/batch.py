#!/usr/bin/env python3
import os, sys, csv, json
sys.path.insert(0, os.path.dirname(__file__))
from analyze import analyze

root = sys.argv[1]
rows = []
errors = []
for dp, _, fns in os.walk(root):
    for fn in fns:
        if fn.lower().endswith(".dxf"):
            p = os.path.join(dp, fn)
            try:
                r = analyze(p)
                r["_rel"] = os.path.relpath(p, root)
                rows.append(r)
            except Exception as ex:
                errors.append((os.path.relpath(p, root), str(ex)))

out = os.path.join(os.path.dirname(__file__), "spec.csv")
cols = ["designation","material","thickness_mm","qty","width_mm","height_mm",
        "area_mm2","cut_length_mm","holes","hole_diams","min_hole_edge_mm","checks","_rel"]
with open(out, "w", newline="", encoding="utf-8-sig") as f:
    w = csv.writer(f)
    w.writerow(cols)
    for r in rows:
        w.writerow([r.get(c) if not isinstance(r.get(c), list) else " ".join(map(str, r.get(c))) for c in cols])

parsed = sum(1 for r in rows if r.get("thickness_mm"))
problems = [r for r in rows if r.get("checks") != ["OK"]]
by_th = {}
for r in rows:
    th = r.get("thickness_mm")
    if th: by_th[th] = by_th.get(th, 0) + (r.get("qty") or 1)
tot_cut = sum(r.get("cut_length_mm",0)*(r.get("qty") or 1) for r in rows)/1000.0

print(f"files_ok={len(rows)} parse_errors={len(errors)} name_parsed={parsed}")
print(f"total_cut_length_m={tot_cut:.1f} (with qty)")
print("qty_by_thickness_mm=", json.dumps(by_th, ensure_ascii=False))
print(f"problem_parts={len(problems)}")
for r in problems[:15]:
    print("  -", r.get("_rel"), "=>", "; ".join(r.get("checks")))
for rel, ex in errors[:10]:
    print("  ERR", rel, ex[:80])
print("csv=", out)
