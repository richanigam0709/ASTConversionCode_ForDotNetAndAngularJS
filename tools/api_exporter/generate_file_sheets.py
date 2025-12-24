#!/usr/bin/env python3
"""
generate_file_sheets.py

Scan a workspace root for Angular/Node and .NET API projects, compute file-level
dependencies (syntactic), and write an Excel workbook with one worksheet per source file.
Each sheet contains:
 - file path (project-relative)
 - declared symbols
 - Level 1..N dependencies (each as a semicolon-separated list)

Usage:
  python generate_file_sheets.py --root <workspace_root> --out <excel.xlsx> [--levels N]

Requires:
  pip install openpyxl
"""

import argparse
import os
import re
from collections import defaultdict, deque
from pathlib import Path
from typing import Dict, List, Set
from openpyxl import Workbook

# Extensions and regex patterns (same as previous script)
TS_EXTS = [".ts", ".tsx", ".js", ".jsx"]
CS_EXTS = [".cs"]

IMPORT_FROM_RE = re.compile(r"import\s+(?:[\s\S]+?)\s+from\s+['\"](?P<spec>[^'\"]+)['\"]", re.MULTILINE)
IMPORT_SIMPLE_RE = re.compile(r"import\s+['\"](?P<spec>[^'\"]+)['\"]", re.MULTILINE)
REQUIRE_RE = re.compile(r"require\(['\"](?P<spec>[^'\"]+)['\"]\)")
EXPORT_RE = re.compile(r"export\s+(?:default\s+)?(?:class|function|const|let|var|interface|type)\s+(?P<name>[A-Za-z_][A-Za-z0-9_]*)")
EXPORT_NAMED_RE = re.compile(r"export\s*\{\s*([^\}]+)\s*\}")
MODULE_EXPORTS_RE = re.compile(r"module\.exports\s*=\s*(?P<name>[A-Za-z_][A-Za-z0-9_]*)")
USING_RE = re.compile(r"using\s+(?P<ns>[A-Za-z0-9_.]+)\s*;")
NAMESPACE_RE = re.compile(r"namespace\s+(?P<ns>[A-Za-z0-9_.]+)")
CS_TYPE_DECL_RE = re.compile(r"\b(class|struct|interface|enum)\s+(?P<name>[A-Za-z_][A-Za-z0-9_]*)")
IDENTIFIER_RE = re.compile(r"\b([A-Za-z_][A-Za-z0-9_]*)\b")
CS_METHOD_DECL_RE = re.compile(r"\b(?:public|private|protected|internal)?\s*(?:static\s+)?(?:async\s+)?[\w<>\[\],\s]+\s+(?P<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(")
INVOKE_RE = re.compile(r"([A-Za-z_][A-Za-z0-9_\.\>]*)\s*\(")


def find_projects(root: Path):
    projects = set()
    IGNORED_DIRS = {"node_modules", "obj", "bin", ".git", "packages", "dist", "build", "target"}

    def ignored(p: Path):
        return any(part in IGNORED_DIRS for part in p.parts)

    for p in root.rglob("package.json"):
        if not ignored(p):
            projects.add(p.parent.resolve())
    for p in root.rglob("*.csproj"):
        if not ignored(p):
            projects.add(p.parent.resolve())
    for p in root.rglob("*.sln"):
        if not ignored(p):
            projects.add(p.parent.resolve())
    if not projects:
        projects.add(root.resolve())
    return sorted(projects)


def list_source_files(project_root: Path):
    files = []
    IGNORED_DIRS = {"node_modules", "obj", "bin", ".git", "packages", "dist", "build", "target"}

    def ignored(p: Path):
        return any(part in IGNORED_DIRS for part in p.parts)

    for ext in TS_EXTS + CS_EXTS:
        for f in project_root.rglob(f"*{ext}"):
            if not ignored(f):
                files.append(f)
    return [p for p in files if p.is_file()]


def read_text(path: Path) -> str:
    try:
        return path.read_text(encoding="utf-8", errors="ignore")
    except Exception:
        return ""


def resolve_ts_import(project_root: Path, importer: Path, spec: str):
    if not spec.startswith("."):
        return []
    base = (importer.parent / spec).resolve()
    candidates = []
    for ext in TS_EXTS:
        p = Path(str(base) + ext)
        candidates.append(p)
    for ext in TS_EXTS:
        p = base / ("index" + ext)
        candidates.append(p)
    if base.exists() and base.is_file():
        candidates.insert(0, base)
    return [p for p in candidates if p.exists()]


def extract_ts_declarations_and_imports(path: Path, text: str):
    declared = set()
    imports = []
    for m in EXPORT_RE.finditer(text):
        declared.add(m.group("name"))
    for m in EXPORT_NAMED_RE.finditer(text):
        names = m.group(1)
        for n in names.split(","):
            n = n.strip().split(" as ")[0].strip()
            if n:
                declared.add(n)
    for m in MODULE_EXPORTS_RE.finditer(text):
        declared.add(m.group("name"))
    for m in IMPORT_FROM_RE.finditer(text):
        imports.append(m.group("spec"))
    for m in IMPORT_SIMPLE_RE.finditer(text):
        imports.append(m.group("spec"))
    for m in REQUIRE_RE.finditer(text):
        imports.append(m.group("spec"))
    return declared, imports


def extract_cs_declarations_and_usings(path: Path, text: str):
    declared_types = set()
    declared_namespaces = set()
    usings = []
    for m in NAMESPACE_RE.finditer(text):
        declared_namespaces.add(m.group("ns"))
    for m in CS_TYPE_DECL_RE.finditer(text):
        declared_types.add(m.group("name"))
    for m in USING_RE.finditer(text):
        usings.append(m.group("ns"))
    identifiers = set(IDENTIFIER_RE.findall(text))
    return declared_types, declared_namespaces, usings, identifiers


def build_indexes(files: List[Path], project_root: Path):
    ts_exports = {}
    ts_imports = {}
    cs_types = {}
    cs_namespaces = {}
    cs_usings = {}
    cs_identifiers = {}
    file_texts = {}

    for f in files:
        rel = f.relative_to(project_root)
        text = read_text(f)
        file_texts[str(rel)] = text
        if f.suffix in TS_EXTS:
            decls, imports = extract_ts_declarations_and_imports(f, text)
            ts_exports[str(rel)] = decls
            ts_imports[str(rel)] = imports
        elif f.suffix in CS_EXTS:
            types, namespaces, usings, identifiers = extract_cs_declarations_and_usings(f, text)
            cs_types[str(rel)] = types
            cs_namespaces[str(rel)] = namespaces
            cs_usings[str(rel)] = usings
            cs_identifiers[str(rel)] = identifiers
            # Extract method declarations and bodies (simple heuristic)
            methods = {}
            for m in CS_METHOD_DECL_RE.finditer(text):
                name = m.group('name')
                # find the opening brace after the match
                idx = text.find('{', m.end())
                if idx == -1:
                    continue
                # find matching closing brace
                depth = 0
                end = idx
                for i in range(idx, len(text)):
                    if text[i] == '{':
                        depth += 1
                    elif text[i] == '}':
                        depth -= 1
                        if depth == 0:
                            end = i
                            break
                body = text[idx+1:end] if end > idx else ''
                methods[name] = body
            cs_methods = locals().get('cs_methods', {})
            cs_methods[str(rel)] = methods

    symbol_decl_map = defaultdict(set)
    for f, syms in ts_exports.items():
        for s in syms:
            symbol_decl_map[s].add(f)
    for f, syms in cs_types.items():
        for s in syms:
            symbol_decl_map[s].add(f)

    # method -> files map (for C# methods)
    method_decl_map = defaultdict(set)
    try:
        for f, methods in cs_methods.items():
            for mname in methods.keys():
                method_decl_map[mname].add(f)
    except NameError:
        cs_methods = {}

    namespace_decl_map = defaultdict(set)
    for f, nss in cs_namespaces.items():
        for ns in nss:
            namespace_decl_map[ns].add(f)

    return {
        "ts_exports": ts_exports,
        "ts_imports": ts_imports,
        "cs_types": cs_types,
        "cs_namespaces": cs_namespaces,
        "cs_usings": cs_usings,
        "cs_identifiers": cs_identifiers,
        "cs_methods": cs_methods,
        "symbol_decl_map": dict(symbol_decl_map),
        "namespace_decl_map": dict(namespace_decl_map),
        "method_decl_map": dict(method_decl_map),
        "file_texts": file_texts,
    }


def compute_dependencies_for_file(relpath: str, path: Path, project_root: Path, idxs, max_levels: int):
    deps_levels = [set() for _ in range(max_levels + 1)]
    visited = set()

    if path.suffix in TS_EXTS:
        imports = idxs["ts_imports"].get(relpath, [])
        for spec in imports:
            resolved = resolve_ts_import(project_root, path, spec)
            if resolved:
                for r in resolved:
                    try:
                        deps_levels[1].add(str(Path(r).resolve().relative_to(project_root.resolve())))
                    except Exception:
                        deps_levels[1].add(str(Path(r).resolve()))

    if path.suffix in CS_EXTS:
        usings = idxs["cs_usings"].get(relpath, [])
        for ns in usings:
            for f in idxs["namespace_decl_map"].get(ns, []):
                if f != relpath:
                    deps_levels[1].add(f)
        identifiers = idxs["cs_identifiers"].get(relpath, set())
        for ident in identifiers:
            for f in idxs["symbol_decl_map"].get(ident, []):
                if f != relpath:
                    deps_levels[1].add(f)

    if path.suffix in TS_EXTS:
        text = idxs["file_texts"].get(relpath, "")
        ids = set(IDENTIFIER_RE.findall(text))
        for ident in ids:
            for f in idxs["symbol_decl_map"].get(ident, []):
                if f != relpath:
                    deps_levels[1].add(f)

    for lvl in range(1, max_levels):
        for f in list(deps_levels[lvl]):
            if f in visited:
                continue
            visited.add(f)
            fpath = (project_root / f)
            if not fpath.exists():
                continue
            direct = set()
            if fpath.suffix in TS_EXTS:
                imports = idxs["ts_imports"].get(f, [])
                for spec in imports:
                    resolved = resolve_ts_import(project_root, fpath, spec)
                    if resolved:
                        for r in resolved:
                            try:
                                direct.add(str(Path(r).resolve().relative_to(project_root.resolve())))
                            except Exception:
                                direct.add(str(Path(r).resolve()))
            if fpath.suffix in CS_EXTS:
                usings = idxs["cs_usings"].get(f, [])
                for ns in usings:
                    for ff in idxs["namespace_decl_map"].get(ns, []):
                        if ff != f:
                            direct.add(ff)
                identifiers = idxs["cs_identifiers"].get(f, set())
                for ident in identifiers:
                    for ff in idxs["symbol_decl_map"].get(ident, []):
                        if ff != f:
                            direct.add(ff)
            for d in direct:
                if d not in deps_levels[1] and all(d not in deps_levels[x] for x in range(1, lvl+1)):
                    deps_levels[lvl+1].add(d)

    return deps_levels


def compute_method_calls_for_file(relpath: str, path: Path, project_root: Path, idxs, max_levels: int):
    """Compute method-level call graph for methods declared in this file.
    Returns dict: method_name -> list of sets for levels (index 1..max_levels)
    Each entry in sets is string 'relativepath::method'
    """
    result = {}
    cs_methods = idxs.get("cs_methods", {})
    method_decl_map = idxs.get("method_decl_map", {})

    methods = cs_methods.get(relpath, {})
    if not methods:
        return result

    # For each method declared in this file
    for mname, body in methods.items():
        levels = [set() for _ in range(max_levels + 1)]
        # find invoked identifiers
        invoked = set()
        for inv in INVOKE_RE.finditer(body):
            full = inv.group(1)
            # take last identifier after dot or '::'
            last = full.split('.')[-1].split('::')[-1]
            # strip generic angle brackets
            last = re.sub(r"<.*>$", "", last)
            if last:
                invoked.add(last)

        # map invoked names to declared methods (files)
        for name in invoked:
            for f in method_decl_map.get(name, []):
                if f != relpath:
                    levels[1].add(f + "::" + name)
                else:
                    levels[1].add(f + "::" + name)

        # BFS for further levels
        for lvl in range(1, max_levels):
            for item in list(levels[lvl]):
                # item is 'file::method'
                try:
                    fpath_str, method_name = item.split("::", 1)
                except ValueError:
                    continue
                # find method body in that file
                other_methods = cs_methods.get(fpath_str, {})
                body2 = other_methods.get(method_name)
                if not body2:
                    continue
                # find invoked in body2
                invoked2 = set()
                for inv in INVOKE_RE.finditer(body2):
                    full = inv.group(1)
                    last = full.split('.')[-1].split('::')[-1]
                    last = re.sub(r"<.*>$", "", last)
                    if last:
                        invoked2.add(last)
                for name2 in invoked2:
                    for f2 in method_decl_map.get(name2, []):
                        key = f2 + "::" + name2
                        # avoid duplicates across earlier levels
                        if all(key not in levels[x] for x in range(1, lvl+1)):
                            levels[lvl+1].add(key)

        result[mname] = levels

    return result


def process_project(project_root: Path, max_levels: int = 3):
    files = list_source_files(project_root)
    idxs = build_indexes(files, project_root)
    results = []
    for p in files:
        rel = str(p.relative_to(project_root))
        deps_levels = compute_dependencies_for_file(rel, p, project_root, idxs, max_levels)
        method_calls = compute_method_calls_for_file(rel, p, project_root, idxs, max_levels)
        declared = []
        if p.suffix in TS_EXTS:
            declared = sorted(idxs["ts_exports"].get(rel, []))
        elif p.suffix in CS_EXTS:
            declared = sorted(list(idxs["cs_types"].get(rel, set()) | idxs["cs_namespaces"].get(rel, set())))
        results.append({
            "file": rel,
            "declared": declared,
            "levels": deps_levels,
            "method_calls": method_calls,
            "project_root": str(project_root)
        })
    return results


def safe_sheet_name(name: str, idx: int):
    invalid = r'[]:*?/\\'
    s = "".join(ch for ch in name if ch not in invalid)
    s = s.replace(" ", "_")
    s = s[:24]
    return f"F{idx:04d}_{s}"


def write_excel_one_sheet_per_file(all_file_rows: List[Dict], out_path: Path, max_levels: int):
    wb = Workbook()
    default = wb.active
    wb.remove(default)

    for i, row in enumerate(all_file_rows):
        sheet_name = safe_sheet_name(Path(row["file"]).stem, i+1)
        ws = wb.create_sheet(title=sheet_name)
        ws.append(["ProjectRoot", row["project_root"]])
        ws.append(["FilePath", row["file"]])
        ws.append(["DeclaredSymbols", "; ".join(row["declared"])])
        ws.append([])
        ws.append(["Level", "Dependencies (semicolon-separated)"])
        for lvl in range(1, max_levels+1):
            deps = sorted(row["levels"][lvl])
            deps_s = "; ".join(deps)
            ws.append([f"Level {lvl}", deps_s])
        ws.append([])
        ws.append(["Dependency (each on new row)"])
        for lvl in range(1, max_levels+1):
            for d in sorted(row["levels"][lvl]):
                ws.append([f"Level {lvl} -> {d}"])
        # Method-level call graph section (for C# methods)
        method_calls = row.get("method_calls", {})
        if method_calls:
            ws.append([])
            ws.append(["Methods and their call-chains:"])
            for mname, levels in method_calls.items():
                ws.append([f"Method: {mname}"])
                for lvl in range(1, max_levels+1):
                    vals = sorted(levels[lvl])
                    ws.append([f"  Level {lvl}", "; ".join(vals)])
                ws.append([])
        for col in ws.columns:
            max_len = 0
            col_letter = col[0].column_letter
            for cell in col:
                try:
                    v = cell.value or ""
                    l = len(str(v))
                    if l > max_len:
                        max_len = l
                except Exception:
                    pass
            ws.column_dimensions[col_letter].width = min(120, max(10, max_len + 2))

    wb.save(out_path)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", required=True, help="Workspace root to scan")
    ap.add_argument("--out", required=True, help="Output Excel file path")
    ap.add_argument("--levels", type=int, default=3, help="Max dependency levels to compute")
    args = ap.parse_args()

    root = Path(args.root).resolve()
    out = Path(args.out).resolve()
    max_levels = args.levels

    projects = find_projects(root)
    print(f"Found {len(projects)} projects.")

    all_rows = []
    for proj in projects:
        print(f"Processing project: {proj}")
        rows = process_project(proj, max_levels=max_levels)
        print(f"  files: {len(rows)}")
        all_rows.extend(rows)

    print(f"Writing Excel file with {len(all_rows)} sheets to: {out}")
    write_excel_one_sheet_per_file(all_rows, out, max_levels)
    print("Done.")

if __name__ == "__main__":
    main()
