# API AST Exporter (Roslyn / .NET)

Exports AST and symbol information from .NET C# projects using **Roslyn** (Microsoft.CodeAnalysis).

## Features

- Parses C# source code using the official Roslyn compiler/analyzer.
- Extracts:
  - **Controllers** (classes decorated with `[controller]` or `[ApiController]`)
  - **Classes**, **Interfaces**
  - **Methods** with return types, parameters, attributes
  - **Properties** with types and visibility
  - Method attributes (e.g., `[HttpGet]`, `[HttpPost]`)
- Reads `.sln` and `.csproj` files to discover projects.
- Writes per-file AST JSON and a combined summary.
- Dependency: `Microsoft.CodeAnalysis.CSharp.Workspaces` NuGet package.

## Installation

From the `tools/api_exporter/` directory:

```bash
dotnet restore
```

## Usage

### Basic

```bash
dotnet run -- --solution C:\MyProject\MySolution.sln --output C:\MyProject\asts
```

### With explicit paths

```bash
dotnet run -- --solution /path/to/solution.sln --output /path/to/asts
```

## Output

```
asts/
  api/
    summary.json        # Combined summary of all files, controllers, endpoints
    files/
      Controller.cs.json  # Per-file AST
      Service.cs.json
      ...
```

### Sample Output (per-file)

```json
{
  "path": "Controllers/WeatherForecastController.cs",
  "absolutePath": "/full/path/to/Controllers/WeatherForecastController.cs",
  "symbols": {
    "controllers": [
      {
        "name": "WeatherForecastController",
        "methods": [
          {
            "name": "Get",
            "returnType": "IEnumerable<WeatherForecast>",
            "isPublic": true,
            "parameters": [
              {"name": "id", "type": "int"}
            ],
            "attributes": ["[HttpGet(\"{id}\")]"]
          }
        ],
        "properties": [],
        "attributes": ["[ApiController]", "[Route(\"api/[controller]\")]"]
      }
    ],
    "classes": [],
    "interfaces": []
  }
}
```

### Summary Output

```json
{
  "total_files": 25,
  "total_controllers": 5,
  "total_endpoints": 42,
  "files": [...]
}
```

## Notes

- Requires .NET 6.0 SDK or later (easily adaptable to other .NET versions).
- Works with any modern C# / ASP.NET Core project.
- Captures full semantic information (types, visibility, attributes).
