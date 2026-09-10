#!/usr/bin/env python3
"""Build a deterministic factory release package from checked Inventor/DXF outputs.

The command never sends messages and never edits CAD sources. It validates the product tree, performs the
deep DXF contour checks, writes CSV/XLSX metal accounting, hashes every released source artifact, and emits
a release digest that a chief engineer must approve through the connector before the gateway notifies the shop.
"""
from __future__ import annotations

import argparse
import csv
import hashlib
import json
import os
from pathlib import Path
import re
import sys
from typing import Any

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from analyze import analyze  # noqa: E402
from spec_xlsx import DENSITY, write_spec  # noqa: E402

MAX_PRODUCT_FILES = 10_000


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _canonical_digest(value: Any) -> str:
    encoded = json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest()


def _check(rule: str, passed: bool, actual: Any, expected: Any) -> dict[str, Any]:
    return {"rule": rule, "pass": bool(passed), "actual": actual, "expected": expected}


def _under(path: Path, root: Path) -> bool:
    try:
        path.resolve().relative_to(root.resolve())
        return True
    except ValueError:
        return False


def build_release(
    product_root: str | os.PathLike[str],
    dxf_dir: str | os.PathLike[str],
    output_dir: str | os.PathLike[str],
    product_code: str,
    pdf_dir: str | os.PathLike[str] | None = None,
    minimum_pdf_count: int = 0,
    required_name_pattern: str | None = None,
    require_clone_manifest: bool = False,
    require_dxf_manifest: bool = False,
) -> dict[str, Any]:
    product = Path(product_root).resolve()
    dxf = Path(dxf_dir).resolve()
    output = Path(output_dir).resolve()
    pdf = Path(pdf_dir).resolve() if pdf_dir else None
    if not product.is_dir():
        raise ValueError(f"product_root does not exist: {product}")
    if not dxf.is_dir() or not _under(dxf, product):
        raise ValueError("dxf_dir must exist under product_root")
    if pdf is not None and (not pdf.is_dir() or not _under(pdf, product)):
        raise ValueError("pdf_dir must exist under product_root")
    if output == product or not _under(output, product):
        raise ValueError("output_dir must be a child directory of product_root")
    if _under(output, dxf) or _under(dxf, output):
        raise ValueError("output_dir and dxf_dir must not contain one another")
    if pdf is not None and (_under(output, pdf) or _under(pdf, output)):
        raise ValueError("output_dir and pdf_dir must not contain one another")
    if not product_code.strip():
        raise ValueError("product_code is required")
    if minimum_pdf_count < 0:
        raise ValueError("minimum_pdf_count must be non-negative")
    name_re = re.compile(required_name_pattern, re.IGNORECASE) if required_name_pattern else None

    symlinks = [path for path in product.rglob("*") if path.is_symlink()]
    if symlinks:
        raise ValueError("product tree must not contain symbolic links: " + str(symlinks[0]))

    output.mkdir(parents=True, exist_ok=True)
    dxf_files = sorted((path for path in dxf.rglob("*.dxf") if path.is_file()), key=lambda p: str(p).casefold())
    analyses: list[dict[str, Any]] = []
    parse_errors: list[dict[str, str]] = []
    for path in dxf_files:
        try:
            row = analyze(str(path))
            row["relative_path"] = path.relative_to(product).as_posix()
            analyses.append(row)
        except Exception as exc:
            parse_errors.append({"file": path.relative_to(product).as_posix(), "error": str(exc)})

    geometry_failures = [row for row in analyses if row.get("checks") != ["OK"]]
    metadata_failures = [row for row in analyses if not all(
        row.get(key) not in (None, "") for key in ("designation", "material", "thickness_mm", "qty")
    )]
    pattern_failures = [row["relative_path"] for row in analyses if name_re and not name_re.search(Path(row["file"]).stem)]
    lower_names: dict[str, list[str]] = {}
    for path in dxf_files:
        lower_names.setdefault(path.name.casefold(), []).append(path.relative_to(product).as_posix())
    duplicate_names = [items for items in lower_names.values() if len(items) > 1]
    incomplete = sorted(path.relative_to(product).as_posix() for path in product.rglob("INCOMPLETE.json"))
    pdf_files = sorted(pdf.rglob("*.pdf"), key=lambda p: str(p).casefold()) if pdf else []
    clone_manifest = product / "clone-manifest.json"
    dxf_manifest = dxf / "dxf-export-manifest.json"
    dxf_manifest_error: str | None = None
    manifest_dxf_names: list[str] = []
    if dxf_manifest.is_file():
        try:
            manifest_data = json.loads(dxf_manifest.read_text(encoding="utf-8"))
            if manifest_data.get("state") != "complete" or manifest_data.get("pass") is not True:
                raise ValueError("manifest state is not complete/pass")
            manifest_dxf_names = sorted(map(str, manifest_data.get("dxf_files", [])), key=str.casefold)
        except Exception as exc:
            dxf_manifest_error = str(exc)
    actual_dxf_names = sorted((path.relative_to(dxf).as_posix() for path in dxf_files), key=str.casefold)

    checks = [
        _check("dxf_files_present", len(dxf_files) > 0, len(dxf_files), "> 0"),
        _check("dxf_parse", len(parse_errors) == 0, len(parse_errors), 0),
        _check("dxf_geometry", len(geometry_failures) == 0, len(geometry_failures), 0),
        _check("dxf_name_metadata", len(metadata_failures) == 0, len(metadata_failures), 0),
        _check("dxf_name_pattern", len(pattern_failures) == 0, len(pattern_failures), 0),
        _check("unique_dxf_filenames", len(duplicate_names) == 0, len(duplicate_names), 0),
        _check("no_incomplete_stage", len(incomplete) == 0, len(incomplete), 0),
        _check("pdf_count", len(pdf_files) >= minimum_pdf_count, len(pdf_files), f">= {minimum_pdf_count}"),
        _check("clone_manifest", not require_clone_manifest or clone_manifest.is_file(), clone_manifest.is_file(), require_clone_manifest),
        _check(
            "dxf_export_manifest",
            not require_dxf_manifest or (
                dxf_manifest.is_file() and dxf_manifest_error is None and manifest_dxf_names == actual_dxf_names
            ),
            {"present": dxf_manifest.is_file(), "error": dxf_manifest_error, "files": manifest_dxf_names},
            {"required": require_dxf_manifest, "files": actual_dxf_names},
        ),
    ]

    csv_path = output / "metal-spec.csv"
    columns = [
        "designation", "material", "thickness_mm", "qty", "width_mm", "height_mm",
        "area_mm2", "cut_length_mm", "holes", "hole_diams", "min_hole_edge_mm", "checks", "relative_path",
    ]
    with csv_path.open("w", newline="", encoding="utf-8-sig") as stream:
        writer = csv.writer(stream)
        writer.writerow(columns)
        for row in analyses:
            writer.writerow([
                " ".join(map(str, row.get(column, []))) if isinstance(row.get(column), list) else row.get(column)
                for column in columns
            ])

    xlsx_rows = []
    for row in analyses:
        thickness = row.get("thickness_mm")
        area = row.get("area_mm2")
        if not thickness or not area or not row.get("material") or not row.get("qty"):
            continue
        xlsx_rows.append({
            "material": f"Лист {thickness:g} {row['material']}",
            "mass": round(float(area) * float(thickness) * DENSITY, 4),
            "qty": int(row["qty"]),
        })
    xlsx_path = output / "metal-spec.xlsx"
    total_mass = write_spec(xlsx_rows, str(xlsx_path))
    checks.append(_check("xlsx_rows", len(xlsx_rows) == len(analyses), len(xlsx_rows), len(analyses)))

    product_files = [
        path for path in product.rglob("*")
        if path.is_file() and not _under(path, output)
    ]
    if len(product_files) > MAX_PRODUCT_FILES:
        raise ValueError(f"product tree exceeds {MAX_PRODUCT_FILES} files")
    manifest_entries = [{
        "path": path.relative_to(product).as_posix(),
        "size": path.stat().st_size,
        "sha256": _sha256(path),
    } for path in sorted(product_files, key=lambda p: str(p).casefold())]

    pass_release = all(check["pass"] for check in checks)
    digest_basis = {
        "schema": "kvz-release-v1",
        "product_code": product_code,
        "checks": checks,
        "files": manifest_entries,
        "dxf": [{
            "path": row["relative_path"], "checks": row.get("checks"),
            "area_mm2": row.get("area_mm2"), "cut_length_mm": row.get("cut_length_mm"),
        } for row in analyses],
        "total_mass_kg": total_mass,
    }
    release_digest = _canonical_digest(digest_basis)
    manifest_path = output / "release-manifest.json"
    manifest_path.write_text(json.dumps({**digest_basis, "release_digest": release_digest}, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    report = {
        "schema": "kvz-release-report-v1",
        "pass": pass_release,
        "state": "awaiting_chief_approval" if pass_release else "blocked",
        "product_code": product_code,
        "product_root": str(product),
        "dxf_dir": str(dxf),
        "pdf_dir": str(pdf) if pdf else None,
        "output_dir": str(output),
        "release_digest": release_digest,
        "checks": checks,
        "counts": {"product_files": len(product_files), "dxf": len(dxf_files), "pdf": len(pdf_files)},
        "total_mass_kg": total_mass,
        "parse_errors": parse_errors,
        "geometry_failures": [{"file": row["relative_path"], "checks": row.get("checks")} for row in geometry_failures],
        "metadata_failures": [row["relative_path"] for row in metadata_failures],
        "pattern_failures": pattern_failures,
        "duplicate_names": duplicate_names,
        "incomplete_markers": incomplete,
        "artifacts": {
            "csv": str(csv_path), "xlsx": str(xlsx_path), "manifest": str(manifest_path),
        },
    }
    report_path = output / "release-report.json"
    report["artifacts"]["report"] = str(report_path)
    report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return report


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--product-root", required=True)
    parser.add_argument("--dxf-dir", required=True)
    parser.add_argument("--output-dir", required=True)
    parser.add_argument("--product-code", required=True)
    parser.add_argument("--pdf-dir")
    parser.add_argument("--minimum-pdf-count", type=int, default=0)
    parser.add_argument("--required-name-pattern")
    parser.add_argument("--require-clone-manifest", action="store_true")
    parser.add_argument("--require-dxf-manifest", action="store_true")
    args = parser.parse_args()
    try:
        report = build_release(
            args.product_root, args.dxf_dir, args.output_dir, args.product_code,
            args.pdf_dir, args.minimum_pdf_count, args.required_name_pattern,
            args.require_clone_manifest, args.require_dxf_manifest,
        )
        print(json.dumps(report, ensure_ascii=False))
        return 0 if report["pass"] else 2
    except Exception as exc:
        print(json.dumps({"pass": False, "state": "error", "error": str(exc)}, ensure_ascii=False))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
