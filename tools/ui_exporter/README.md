# UI AST Exporter (ts-morph + Angular)

Exports AST and symbol information from Angular TypeScript projects using **ts-morph** (wrapper around tsc).

## Features

- Parses TypeScript files in an Angular project using the actual TypeScript compiler.
- Extracts:
  - **Components** (classes decorated with `@Component`)
  - **Services** (classes decorated with `@Injectable`)
  - **Classes**, **Interfaces**, **Functions**
  - **Imports** and **Exports**
  - Method/property visibility and types
- Writes per-file AST JSON and a combined summary.
- Zero runtime dependencies beyond Node.js + ts-morph.

## Installation

From the `tools/ui_exporter/` directory:

```bash
npm install
```

## Usage

### Basic

```bash
node export_ui_ast.js --project /path/to/tsconfig.json --output /path/to/asts
```

### Windows PowerShell

```powershell
node export_ui_ast.js --project "C:\MyProject\tsconfig.json" --output "C:\MyProject\asts"
```

## Output

```
asts/
  ui/
    summary.json          # Combined summary of all files
    files/
      component.ts.json   # Per-file AST
      service.ts.json
      ...
```

### Sample Output (per-file)

```json
{
  "path": "src/app/components/example.component.ts",
  "absolutePath": "/full/path/to/example.component.ts",
  "symbols": {
    "components": [
      {
        "name": "ExampleComponent",
        "decorators": ["@Component({...})"],
        "methods": [
          {"name": "ngOnInit", "isPublic": true},
          {"name": "handleClick", "isPublic": true}
        ],
        "properties": [
          {"name": "title", "isPublic": true},
          {"name": "count", "isPublic": false}
        ]
      }
    ],
    "services": [],
    "interfaces": [],
    "classes": [],
    "functions": [],
    "imports": [
      {
        "moduleSpecifier": "@angular/core",
        "namedImports": ["Component", "OnInit"]
      }
    ]
  }
}
```

## Notes

- Requires a valid `tsconfig.json` in the Angular project root.
- Designed to work with Angular 12+.
- If template parsing is needed (`.component.html`), enhance the exporter to use `@angular/compiler`.
