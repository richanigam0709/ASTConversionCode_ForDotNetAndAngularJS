/**
 * UI AST Exporter (ts-morph + Angular)
 *
 * Usage:
 *   node export_ui_ast.js --project /path/to/tsconfig.json --output /path/to/asts
 *
 * Exports:
 *   - Per-file AST JSON under output/ui/files/
 *   - Combined summary at output/ui/summary.json
 */

const fs = require("fs");
const path = require("path");
const { Project } = require("ts-morph");

function parseArgs() {
  const args = {};
  for (let i = 2; i < process.argv.length; i++) {
    if (process.argv[i].startsWith("--")) {
      const key = process.argv[i].substring(2);
      args[key] = process.argv[i + 1];
      i++;
    }
  }
  return args;
}

function extractSymbols(sourceFile) {
  const symbols = {
    components: [],
    services: [],
    interfaces: [],
    classes: [],
    functions: [],
    imports: [],
  };

  // Extract classes (potential components/services)
  sourceFile.getClasses().forEach((cls) => {
    const decorators = cls
      .getDecorators()
      .map((d) => d.getText());
    const classSummary = {
      name: cls.getName(),
      decorators,
      methods: cls
        .getMethods()
        .map((m) => ({ name: m.getName(), isPublic: m.isPublic() })),
      properties: cls
        .getProperties()
        .map((p) => ({ name: p.getName(), isPublic: p.isPublic() })),
    };

    if (decorators.some((d) => d.includes("@Component"))) {
      symbols.components.push(classSummary);
    } else if (decorators.some((d) => d.includes("@Injectable"))) {
      symbols.services.push(classSummary);
    } else {
      symbols.classes.push(classSummary);
    }
  });

  // Extract interfaces
  sourceFile.getInterfaces().forEach((iface) => {
    symbols.interfaces.push({
      name: iface.getName(),
      properties: iface
        .getProperties()
        .map((p) => ({ name: p.getName(), type: p.getType().getText() })),
    });
  });

  // Extract functions
  sourceFile.getFunctions().forEach((fn) => {
    symbols.functions.push({
      name: fn.getName(),
      isExported: fn.isExported(),
      parameters: fn.getParameters().map((p) => p.getName()),
    });
  });

  // Extract imports
  sourceFile.getImportDeclarations().forEach((imp) => {
    symbols.imports.push({
      moduleSpecifier: imp.getModuleSpecifierValue(),
      namedImports: imp
        .getNamedImports()
        .map((ni) => ni.getName()),
    });
  });

  return symbols;
}

function exportUIAST(tsconfigPath, outputDir) {
  const project = new Project({
    tsConfigFilePath: tsconfigPath,
  });

  const results = [];
  const uiDir = path.join(outputDir, "ui");
  const filesDir = path.join(uiDir, "files");

  fs.mkdirSync(filesDir, { recursive: true });

  project.getSourceFiles().forEach((sourceFile) => {
    const filePath = sourceFile.getFilePath();
    const relativePath = path.relative(project.getCompilerOptions().baseUrl || ".", filePath);

    const fileEntry = {
      path: relativePath,
      absolutePath: filePath,
      symbols: extractSymbols(sourceFile),
    };

    results.push(fileEntry);

    // Write per-file JSON
    const outFile = path.join(filesDir, relativePath + ".json");
    fs.mkdirSync(path.dirname(outFile), { recursive: true });
    fs.writeFileSync(outFile, JSON.stringify(fileEntry, null, 2));
  });

  // Write combined summary
  fs.writeFileSync(
    path.join(uiDir, "summary.json"),
    JSON.stringify(
      {
        total_files: results.length,
        total_components: results.reduce((sum, r) => sum + r.symbols.components.length, 0),
        total_services: results.reduce((sum, r) => sum + r.symbols.services.length, 0),
        files: results,
      },
      null,
      2
    )
  );

  console.log(`✓ Exported UI AST from ${tsconfigPath}`);
  console.log(`  - ${results.length} source files processed`);
  console.log(`  - Output written to ${uiDir}`);

  return results;
}

// Main
if (require.main === module) {
  const args = parseArgs();
  const tsconfigPath = args.project || "./tsconfig.json";
  const outputDir = args.output || "./asts";

  if (!fs.existsSync(tsconfigPath)) {
    console.error(`Error: tsconfig not found at ${tsconfigPath}`);
    process.exit(1);
  }

  try {
    exportUIAST(tsconfigPath, outputDir);
  } catch (err) {
    console.error(`Error exporting UI AST: ${err.message}`);
    process.exit(1);
  }
}

module.exports = { exportUIAST };
