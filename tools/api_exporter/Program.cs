using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
// Simple syntactic AST exporter (single-file Program)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AstExporter
{
    class Program
    {
        static int Main(string[] args)
        {
            var (root, output) = ParseArgs(args);
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(output))
            {
                Console.WriteLine("Usage: AstExporter --root <path> --output <outdir>");
                return 1;
            }

            Run(root, output).GetAwaiter().GetResult();
            return 0;
        }

        static async Task Run(string rootPath, string outputDir)
        {
            var apiDir = Path.Combine(outputDir, "api");
            var filesDir = Path.Combine(apiDir, "files");
            Directory.CreateDirectory(filesDir);

            var csFiles = Directory.GetFiles(rootPath, "*.cs", SearchOption.AllDirectories);
            Console.WriteLine($"Found {csFiles.Length} .cs files to analyze");

            var entries = new List<CSharpFileEntry>();

            int processed = 0;
            foreach (var path in csFiles)
            {
                try
                {
                    var entry = AnalyzeFile(path, rootPath);
                    entries.Add(entry);

                    // We'll write files after a post-processing pass (so we can resolve implements across files)
                    var outFile = Path.Combine(filesDir, entry.Path.Replace("\\", "/") + ".json");
                    Directory.CreateDirectory(Path.GetDirectoryName(outFile));
                    var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true });
                    await File.WriteAllTextAsync(outFile, json);
                    processed++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error analyzing {path}: {ex.Message}");
                }
            }

            // write summary
            var summary = new
            {
                mode = "syntactic",
                total_files = entries.Count,
                files = entries.Select(e => new { e.Path, e.Namespace, e.Namespaces }).ToList()
            };

            var summaryJson = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(apiDir, "summary.json"), summaryJson);

            // Post-process entries to resolve base types (implements) across files
            // Build a lookup: fullName (namespace + '.' + name) and short name -> (fileIndex, relPath)
            var typeLookupFull = new Dictionary<string, (int idx, string path)>();
            var typeLookupShort = new Dictionary<string, List<(int idx, string path, string fullName)>>();
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                int fileId = i + 1;
                foreach (var dt in e.DeclaredTypes)
                {
                    var full = string.IsNullOrWhiteSpace(dt.Namespace) ? dt.Name : dt.Namespace + "." + dt.Name;
                    if (!typeLookupFull.ContainsKey(full)) typeLookupFull[full] = (fileId, e.Path);
                    if (!typeLookupShort.ContainsKey(dt.Name)) typeLookupShort[dt.Name] = new List<(int, string, string)>();
                    typeLookupShort[dt.Name].Add((fileId, e.Path, full));
                }
            }

            // For each file, for each declared type, resolve its BaseTypes
            // Before resolving base types, annotate referenced field and parameter types with resolved kind/file when possible
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                e.ResolvedImplements = new List<ResolvedImplementInfo>();
                e.ImplementedBy = new List<ImplementedByInfo>();
            }

            // helper to extract short name (strip generics and namespace)
            static string ShortName(string typeName)
            {
                if (string.IsNullOrWhiteSpace(typeName)) return typeName;
                var t = typeName.Trim();
                var lt = t.IndexOf('<');
                if (lt >= 0) t = t.Substring(0, lt);
                if (t.Contains('.')) t = t.Split('.').Last();
                return t;
            }

            // annotate fields and parameter types across all entries
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                int fileId = i + 1;
                // scan controllers and classes
                var containers = e.Symbols.Controllers.Cast<CSharpClassInfo>().Concat(e.Symbols.Classes);
                foreach (var c in containers)
                {
                    // fields
                    foreach (var f in c.Fields)
                    {
                        var typeName = f.Type ?? string.Empty;
                        var resolved = (kind: "Unknown", id: 0, path: (string)null);
                        // try full match
                        if (!string.IsNullOrWhiteSpace(typeName) && typeLookupFull.TryGetValue(typeName, out var fm))
                        {
                            var targetEntry = entries[fm.idx - 1];
                            var match = targetEntry.DeclaredTypes.FirstOrDefault(d => (string.IsNullOrWhiteSpace(d.Namespace) ? d.Name : d.Namespace + "." + d.Name) == typeName) ?? targetEntry.DeclaredTypes.FirstOrDefault(d => d.Name == ShortName(typeName));
                            resolved.kind = match?.Kind ?? "Unknown";
                            resolved.id = fm.idx;
                            resolved.path = fm.path;
                        }
                        else
                        {
                            var shortName = ShortName(typeName);
                            if (typeLookupShort.TryGetValue(shortName, out var list))
                            {
                                var candidate = list.First();
                                var targetEntry = entries[candidate.idx - 1];
                                var match = targetEntry.DeclaredTypes.FirstOrDefault(d => d.Name == shortName);
                                resolved.kind = match?.Kind ?? "Unknown";
                                resolved.id = candidate.idx;
                                resolved.path = candidate.path;
                            }
                            else
                            {
                                // heuristic: I-prefixed names -> Interface
                                resolved.kind = (shortName.Length > 1 && shortName.StartsWith("I") && char.IsUpper(shortName[1])) ? "Interface" : "Class";
                            }
                        }
                        f.TypeKind = resolved.kind;
                        f.TypeTargetFileId = resolved.id;
                        f.TypeTargetPath = resolved.path;
                    }

                    // properties
                    foreach (var p in c.Properties)
                    {
                        var typeName = p.Type ?? string.Empty;
                        var resolved = (kind: "Unknown", id: 0, path: (string)null);
                        if (!string.IsNullOrWhiteSpace(typeName) && typeLookupFull.TryGetValue(typeName, out var fm))
                        {
                            var targetEntry = entries[fm.idx - 1];
                            var match = targetEntry.DeclaredTypes.FirstOrDefault(d => (string.IsNullOrWhiteSpace(d.Namespace) ? d.Name : d.Namespace + "." + d.Name) == typeName) ?? targetEntry.DeclaredTypes.FirstOrDefault(d => d.Name == ShortName(typeName));
                            resolved.kind = match?.Kind ?? "Unknown";
                            resolved.id = fm.idx;
                            resolved.path = fm.path;
                        }
                        else
                        {
                            var shortName = ShortName(typeName);
                            if (typeLookupShort.TryGetValue(shortName, out var list))
                            {
                                var candidate = list.First();
                                var targetEntry = entries[candidate.idx - 1];
                                var match = targetEntry.DeclaredTypes.FirstOrDefault(d => d.Name == shortName);
                                resolved.kind = match?.Kind ?? "Unknown";
                                resolved.id = candidate.idx;
                                resolved.path = candidate.path;
                            }
                            else
                            {
                                resolved.kind = (shortName.Length > 1 && shortName.StartsWith("I") && char.IsUpper(shortName[1])) ? "Interface" : "Class";
                            }
                        }
                        p.TypeKind = resolved.kind;
                        p.TypeTargetFileId = resolved.id;
                        p.TypeTargetPath = resolved.path;
                    }

                    // method parameter types
                    foreach (var m in c.Methods)
                    {
                        foreach (var param in m.Parameters)
                        {
                            var typeName = param.Type ?? string.Empty;
                            var resolved = (kind: "Unknown", id: 0, path: (string)null);
                            if (!string.IsNullOrWhiteSpace(typeName) && typeLookupFull.TryGetValue(typeName, out var fm))
                            {
                                var targetEntry = entries[fm.idx - 1];
                                var match = targetEntry.DeclaredTypes.FirstOrDefault(d => (string.IsNullOrWhiteSpace(d.Namespace) ? d.Name : d.Namespace + "." + d.Name) == typeName) ?? targetEntry.DeclaredTypes.FirstOrDefault(d => d.Name == ShortName(typeName));
                                resolved.kind = match?.Kind ?? "Unknown";
                                resolved.id = fm.idx;
                                resolved.path = fm.path;
                            }
                            else
                            {
                                var shortName = ShortName(typeName);
                                if (typeLookupShort.TryGetValue(shortName, out var list))
                                {
                                    var candidate = list.First();
                                    var targetEntry = entries[candidate.idx - 1];
                                    var match = targetEntry.DeclaredTypes.FirstOrDefault(d => d.Name == shortName);
                                    resolved.kind = match?.Kind ?? "Unknown";
                                    resolved.id = candidate.idx;
                                    resolved.path = candidate.path;
                                }
                                else
                                {
                                    resolved.kind = (shortName.Length > 1 && shortName.StartsWith("I") && char.IsUpper(shortName[1])) ? "Interface" : "Class";
                                }
                            }
                            param.TypeKind = resolved.kind;
                            param.TypeTargetFileId = resolved.id;
                            param.TypeTargetPath = resolved.path;
                        }
                    }
                }
            }

            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                int fileId = i + 1;
                foreach (var dt in e.DeclaredTypes)
                {
                    foreach (var baseType in dt.BaseTypes ?? new List<string>())
                    {
                        // try full match first
                        var candidates = new List<(int idx, string path, string fullName)>();
                        if (typeLookupFull.TryGetValue(baseType, out var fullMatch))
                        {
                            candidates.Add((fullMatch.idx, fullMatch.path, baseType));
                        }
                        else
                        {
                            // try short-name matches
                            var shortName = baseType.Contains('.') ? baseType.Split('.').Last() : baseType;
                            if (typeLookupShort.TryGetValue(shortName, out var lst))
                            {
                                candidates.AddRange(lst);
                            }
                        }

                        // pick best candidate (prefer same namespace or any)
                        (int tgtId, string tgtPath, string tgtFull) chosen = (0, null, null);
                        if (candidates.Count == 1)
                        {
                            chosen = candidates[0];
                        }
                        else if (candidates.Count > 1)
                        {
                            // prefer candidate whose fullName ends with the baseType (handles partial qualification)
                            var match = candidates.FirstOrDefault(c => c.fullName == baseType || c.fullName.EndsWith("." + baseType));
                            if (match != default) chosen = match;
                            else chosen = candidates[0];
                        }

                        // ensure baseTypeDetails list exists
                        if (dt.BaseTypeDetails == null) dt.BaseTypeDetails = new List<BaseTypeDetail>();
                        if (chosen.tgtId != 0)
                        {
                            e.ResolvedImplements.Add(new ResolvedImplementInfo { SourceType = dt.Name, TargetType = chosen.tgtFull ?? baseType, TargetFileId = chosen.tgtId, TargetPath = chosen.tgtPath });
                            // annotate the declared type's base type details (kind/class/interface) when we can resolve the target
                            var targetEntry = entries[chosen.tgtId - 1];
                            // try to find the declared type in the target file that matches the resolved full name
                            string targetKind = "Unknown";
                            try
                            {
                                var match = targetEntry.DeclaredTypes.FirstOrDefault(d => (string.IsNullOrWhiteSpace(d.Namespace) ? d.Name : d.Namespace + "." + d.Name) == (chosen.tgtFull ?? baseType));
                                if (match == null)
                                {
                                    // fallback to short-name match
                                    var shortName = (chosen.tgtFull ?? baseType).Contains('.') ? (chosen.tgtFull ?? baseType).Split('.').Last() : (chosen.tgtFull ?? baseType);
                                    match = targetEntry.DeclaredTypes.FirstOrDefault(d => d.Name == shortName);
                                }
                                if (match != null) targetKind = match.Kind ?? "Unknown";
                            }
                            catch { }
                            dt.BaseTypeDetails.Add(new BaseTypeDetail { Name = baseType, Kind = targetKind, TargetFileId = chosen.tgtId, TargetPath = chosen.tgtPath });
                            // add reverse mapping
                            if (targetEntry.ImplementedBy == null) targetEntry.ImplementedBy = new List<ImplementedByInfo>();
                            targetEntry.ImplementedBy.Add(new ImplementedByInfo { ImplementerType = dt.Name, ImplementerFileId = fileId, ImplementerPath = e.Path });
                        }
                        else
                        {
                            // unresolved: provide a heuristic guess for kind (interfaces often start with 'I')
                            string shortName = baseType.Contains('.') ? baseType.Split('.').Last() : baseType;
                            string heuristicKind = (shortName.Length > 1 && shortName.StartsWith("I") && char.IsUpper(shortName[1])) ? "Interface" : "Class";
                            dt.BaseTypeDetails.Add(new BaseTypeDetail { Name = baseType, Kind = heuristicKind, TargetFileId = 0, TargetPath = null });
                        }
                    }
                }
            }

            // Re-write per-file JSONs with resolved info merged
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                var outFile = Path.Combine(filesDir, entry.Path.Replace("\\", "/") + ".json");
                var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(outFile, json);
            }

            // Infer [FromBody] for endpoint parameters when not explicitly present
            // Rule: if method has HttpPost/HttpPut/HttpPatch attribute and parameter has no attributes
            // and parameter type is a complex type (inferred TypeKind == "Class"), mark as FromBody (inferred)
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                var containers = entry.Symbols.Controllers.Cast<CSharpClassInfo>().Concat(entry.Symbols.Classes);
                foreach (var c in containers)
                {
                    foreach (var m in c.Methods)
                    {
                        var hasPost = m.Attributes != null && m.Attributes.Any(a => a.IndexOf("HttpPost", StringComparison.OrdinalIgnoreCase) >= 0 || a.IndexOf("HttpPut", StringComparison.OrdinalIgnoreCase) >= 0 || a.IndexOf("HttpPatch", StringComparison.OrdinalIgnoreCase) >= 0);
                        if (!hasPost) continue;
                        foreach (var p in m.Parameters)
                        {
                            try
                            {
                                if ((p.Attributes == null || p.Attributes.Count == 0) && string.Equals(p.TypeKind, "Class", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (p.Attributes == null) p.Attributes = new List<string>();
                                    p.Attributes.Add("FromBody (inferred)");
                                }
                            }
                            catch { }
                        }
                    }
                }
            }

            // Re-write per-file JSONs again to persist inferred parameter attributes
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                var outFile = Path.Combine(filesDir, entry.Path.Replace("\\", "/") + ".json");
                var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(outFile, json);
            }

            Console.WriteLine($"Wrote {processed} per-file ASTs to {filesDir}");
        }

        static CSharpFileEntry AnalyzeFile(string filePath, string rootPath)
        {
            var code = File.ReadAllText(filePath);
            var tree = CSharpSyntaxTree.ParseText(code);
            var root = tree.GetCompilationUnitRoot();

            // determine namespaces declared in this file (may be multiple)
            var nsList = new List<string>();
            nsList.AddRange(root.DescendantNodes().OfType<NamespaceDeclarationSyntax>().Select(n => n.Name.ToString()));
            nsList.AddRange(root.DescendantNodes().OfType<FileScopedNamespaceDeclarationSyntax>().Select(n => n.Name.ToString()));
            nsList = nsList.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
            // filter to user-defined namespaces: exclude common framework/vendor prefixes
            var excludePrefixes = new[] { "System", "Microsoft", "Newtonsoft", "Serilog", "NUnit", "xunit", "Swashbuckle", "AutoMapper", "EntityFramework", "Microsoft.Extensions" };
            nsList = nsList.Where(ns => !ns.StartsWith("global::") && !excludePrefixes.Any(p => ns.StartsWith(p, StringComparison.OrdinalIgnoreCase))).ToList();
            var namespaceName = nsList.FirstOrDefault() ?? "global";

            // collect using/import directives (both file-level and namespace-level)
            var usingList = root.DescendantNodes().OfType<UsingDirectiveSyntax>()
                .Select(u => u.Name?.ToString() ?? string.Empty)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct()
                .ToList();
            // filter using/imports to user-defined (exclude framework/vendor prefixes)
            usingList = usingList.Where(u => !u.StartsWith("global::") && !excludePrefixes.Any(p => u.StartsWith(p, StringComparison.OrdinalIgnoreCase))).ToList();

            var symbols = new CSharpSymbols();

            // classes
            foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var clsNs = namespaceName;
                // if class has nested namespace declaration, prefer that
                var parentNs = cls.Ancestors().OfType<NamespaceDeclarationSyntax>().FirstOrDefault();
                if (parentNs != null) clsNs = parentNs.Name.ToString();

                var info = new CSharpClassInfo
                {
                    Name = cls.Identifier.Text,
                    Namespace = clsNs,
                    BaseTypes = cls.BaseList?.Types.Select(t => t.ToString()).ToList() ?? new List<string>(),
                    Methods = ExtractMethods(cls, code),
                    Properties = ExtractProperties(cls),
                    Fields = ExtractFields(cls),
                    Attributes = cls.AttributeLists.SelectMany(a => a.Attributes).Select(a => a.Name.ToString()).ToList(),
                    IsAbstract = cls.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword))
                };

                // treat as controller if attribute name contains Controller or class name ends with Controller
                if (info.Attributes.Any(a => a.Contains("Controller")) || info.Name.EndsWith("Controller"))
                    symbols.Controllers.Add(info);
                else
                    symbols.Classes.Add(info);
            }

            // interfaces
            foreach (var iface in root.DescendantNodes().OfType<InterfaceDeclarationSyntax>())
            {
                var ifNs = iface.Ancestors().OfType<NamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString() ?? namespaceName;
                symbols.Interfaces.Add(new CSharpInterfaceInfo
                {
                    Name = iface.Identifier.Text,
                    Namespace = ifNs,
                    BaseTypes = iface.BaseList?.Types.Select(t => t.ToString()).ToList() ?? new List<string>(),
                    Methods = iface.Members.OfType<MethodDeclarationSyntax>().Select(m => new CSharpMethod
                    {
                        Name = m.Identifier.Text,
                        ReturnType = m.ReturnType.ToString(),
                        AccessModifier = "Public",
                        Parameters = m.ParameterList.Parameters.Select(p => new CSharpParameter {
                            Name = p.Identifier.Text,
                            Type = p.Type?.ToString() ?? "unknown",
                            Attributes = p.AttributeLists.SelectMany(a => a.Attributes).Select(a => a.Name.ToString()).ToList()
                        }).ToList()
                    }).ToList(),
                    Properties = ExtractProperties(iface)
                });
            }

            // enums
            foreach (var en in root.DescendantNodes().OfType<EnumDeclarationSyntax>())
            {
                var enNs = en.Ancestors().OfType<NamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString() ?? namespaceName;
                symbols.Enums.Add(new CSharpEnumInfo
                {
                    Name = en.Identifier.Text,
                    Namespace = enNs,
                    Members = en.Members.Select(m => m.Identifier.Text).ToList()
                });
            }

            var entry = new CSharpFileEntry
            {
                Path = Path.GetRelativePath(rootPath, filePath),
                AbsolutePath = filePath,
                Namespace = namespaceName,
                Namespaces = nsList,
                Usings = usingList,
                Symbols = symbols
            };

            // populate DeclaredTypes for a flat view
            var declared = new List<DeclaredTypeInfo>();
            foreach (var c in symbols.Controllers)
            {
                declared.Add(new DeclaredTypeInfo { Name = c.Name, Kind = "Controller", Namespace = c.Namespace, BaseTypes = c.BaseTypes ?? new List<string>() });
            }
            foreach (var c in symbols.Classes)
            {
                declared.Add(new DeclaredTypeInfo { Name = c.Name, Kind = "Class", Namespace = c.Namespace, BaseTypes = c.BaseTypes ?? new List<string>() });
            }
            foreach (var i in symbols.Interfaces)
            {
                declared.Add(new DeclaredTypeInfo { Name = i.Name, Kind = "Interface", Namespace = i.Namespace, BaseTypes = i.BaseTypes ?? new List<string>() });
            }
            foreach (var e in symbols.Enums)
            {
                declared.Add(new DeclaredTypeInfo { Name = e.Name, Kind = "Enum", Namespace = e.Namespace, BaseTypes = new List<string>() });
            }
            entry.DeclaredTypes = declared;

            return entry;
        }

        static List<CSharpMethod> ExtractMethods(ClassDeclarationSyntax cls, string fileText)
        {
            var list = new List<CSharpMethod>();
            foreach (var m in cls.Members.OfType<MethodDeclarationSyntax>())
            {
                var method = new CSharpMethod
                {
                    Name = m.Identifier.Text,
                    ReturnType = m.ReturnType.ToString(),
                    AccessModifier = GetAccessModifier(m.Modifiers),
                    Parameters = m.ParameterList.Parameters.Select(p => new CSharpParameter { Name = p.Identifier.Text, Type = p.Type?.ToString() ?? "unknown", Attributes = p.AttributeLists.SelectMany(a => a.Attributes).Select(a => a.Name.ToString()).ToList() }).ToList(),
                    Attributes = m.AttributeLists.SelectMany(a => a.Attributes).Select(a => a.Name.ToString()).ToList()
                };

                // lines
                try
                {
                    var declSpan = m.GetLocation().GetLineSpan();
                    method.StartLine = declSpan.StartLinePosition.Line + 1;
                    method.EndLine = declSpan.EndLinePosition.Line + 1;
                }
                catch { }

                // body text
                method.BodyText = m.Body?.ToFullString() ?? m.ExpressionBody?.ToFullString() ?? string.Empty;

                // LOC: count non-empty lines in body
                if (!string.IsNullOrEmpty(method.BodyText))
                {
                    var lines = method.BodyText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                    method.LinesOfCode = lines.Count;
                }

                // statements
                var stmts = new List<StatementInfo>();
                var stmtNodes = m.DescendantNodes().Where(n => n is IfStatementSyntax || n is ForStatementSyntax || n is ForEachStatementSyntax || n is WhileStatementSyntax || n is DoStatementSyntax || n is SwitchStatementSyntax || n is TryStatementSyntax);
                foreach (var node in stmtNodes)
                {
                    try
                    {
                        var span = node.GetLocation().GetLineSpan();
                        stmts.Add(new StatementInfo
                        {
                            Kind = node.Kind().ToString(),
                            Text = node.ToFullString(),
                            StartLine = span.StartLinePosition.Line + 1,
                            EndLine = span.EndLinePosition.Line + 1
                        });
                    }
                    catch { }
                }
                method.Statements = stmts;

                // method calls
                var calls = new List<MethodCallInfo>();
                var invocations = m.DescendantNodes().OfType<InvocationExpressionSyntax>();
                foreach (var inv in invocations)
                {
                    try
                    {
                        var info = new MethodCallInfo
                        {
                            Expression = inv.Expression.ToString(),
                            Arguments = inv.ArgumentList?.Arguments.Select(a => a.ToString()).ToList() ?? new List<string>()
                        };
                        var span = inv.GetLocation().GetLineSpan();
                        info.LineNumber = span.StartLinePosition.Line + 1;

                        // simple chain: split expression by '.' to show call chain pieces
                        info.Chain = info.Expression.Split('.').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

                        calls.Add(info);
                    }
                    catch { }
                }
                method.MethodCalls = calls;

                list.Add(method);
            }
            return list;
        }

        static List<CSharpProperty> ExtractProperties(SyntaxNode container)
        {
            return container.DescendantNodes().OfType<PropertyDeclarationSyntax>()
                .Where(p => p.Parent == container)
                .Select(p => new CSharpProperty { Name = p.Identifier.Text, Type = p.Type?.ToString() ?? "unknown", AccessModifier = GetAccessModifier(p.Modifiers) })
                .ToList();
        }

        static List<CSharpProperty> ExtractFields(ClassDeclarationSyntax cls)
        {
            return cls.Members.OfType<FieldDeclarationSyntax>()
                .SelectMany(f => f.Declaration.Variables.Select(v => new CSharpProperty { Name = v.Identifier.Text, Type = f.Declaration.Type.ToString(), AccessModifier = GetAccessModifier(f.Modifiers) }))
                .ToList();
        }

        static (string root, string output) ParseArgs(string[] args)
        {
            string root = null, output = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--root" && i + 1 < args.Length) root = args[++i];
                else if (args[i] == "--output" && i + 1 < args.Length) output = args[++i];
            }
            return (root, output);
        }

        static string GetAccessModifier(SyntaxTokenList modifiers)
        {
            // order matters: check combined modifiers first
            bool isProtected = modifiers.Any(m => m.IsKind(SyntaxKind.ProtectedKeyword));
            bool isInternal = modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword));
            bool isPrivate = modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword));
            bool isPublic = modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword));

            if (isProtected && isInternal) return "ProtectedInternal";
            if (isPrivate && isProtected) return "PrivateProtected";
            if (isPublic) return "Public";
            if (isProtected) return "Protected";
            if (isInternal) return "Internal";
            if (isPrivate) return "Private";

            // default for class members is private; for others caller may override
            return "Private";
        }
    }

    // Data models - lightweight and JSON-ready
    class CSharpFileEntry
    {
        [JsonPropertyName("path")]
        public string Path { get; set; }
        [JsonPropertyName("absolutePath")]
        public string AbsolutePath { get; set; }
        [JsonPropertyName("namespace")]
        public string Namespace { get; set; }
        [JsonPropertyName("namespaces")]
        public List<string> Namespaces { get; set; } = new();
        [JsonPropertyName("usings")]
        public List<string> Usings { get; set; } = new();
        [JsonPropertyName("declaredTypes")]
        public List<DeclaredTypeInfo> DeclaredTypes { get; set; } = new();
        [JsonPropertyName("resolvedImplements")]
        public List<ResolvedImplementInfo> ResolvedImplements { get; set; } = new();
        [JsonPropertyName("implementedBy")]
        public List<ImplementedByInfo> ImplementedBy { get; set; } = new();
        [JsonPropertyName("symbols")]
        public CSharpSymbols Symbols { get; set; } = new();
    }

    class ResolvedImplementInfo
    {
        [JsonPropertyName("sourceType")] public string SourceType { get; set; }
        [JsonPropertyName("targetType")] public string TargetType { get; set; }
        [JsonPropertyName("targetFileId")] public int TargetFileId { get; set; }
        [JsonPropertyName("targetPath")] public string TargetPath { get; set; }
    }

    class ImplementedByInfo
    {
        [JsonPropertyName("implementerType")] public string ImplementerType { get; set; }
        [JsonPropertyName("implementerFileId")] public int ImplementerFileId { get; set; }
        [JsonPropertyName("implementerPath")] public string ImplementerPath { get; set; }
    }

    class CSharpSymbols
    {
        [JsonPropertyName("controllers")]
        public List<CSharpClassInfo> Controllers { get; set; } = new();
        [JsonPropertyName("classes")]
        public List<CSharpClassInfo> Classes { get; set; } = new();
        [JsonPropertyName("interfaces")]
        public List<CSharpInterfaceInfo> Interfaces { get; set; } = new();
        [JsonPropertyName("enums")]
        public List<CSharpEnumInfo> Enums { get; set; } = new();
    }

    class CSharpClassInfo
    {
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("namespace")] public string Namespace { get; set; }
        [JsonPropertyName("baseTypes")] public List<string> BaseTypes { get; set; } = new();
        [JsonPropertyName("methods")] public List<CSharpMethod> Methods { get; set; } = new();
        [JsonPropertyName("properties")] public List<CSharpProperty> Properties { get; set; } = new();
        [JsonPropertyName("fields")] public List<CSharpProperty> Fields { get; set; } = new();
        [JsonPropertyName("attributes")] public List<string> Attributes { get; set; } = new();
        [JsonPropertyName("isAbstract")] public bool IsAbstract { get; set; }
    }

    class CSharpInterfaceInfo
    {
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("namespace")] public string Namespace { get; set; }
        [JsonPropertyName("baseTypes")] public List<string> BaseTypes { get; set; } = new();
        [JsonPropertyName("methods")] public List<CSharpMethod> Methods { get; set; } = new();
        [JsonPropertyName("properties")] public List<CSharpProperty> Properties { get; set; } = new();
    }

    class CSharpEnumInfo
    {
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("namespace")] public string Namespace { get; set; }
        [JsonPropertyName("members")] public List<string> Members { get; set; } = new();
    }

    class DeclaredTypeInfo
    {
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("kind")] public string Kind { get; set; }
        [JsonPropertyName("namespace")] public string Namespace { get; set; }
        [JsonPropertyName("baseTypes")] public List<string> BaseTypes { get; set; } = new();
        [JsonPropertyName("baseTypeDetails")] public List<BaseTypeDetail> BaseTypeDetails { get; set; } = new();
    }

    class BaseTypeDetail
    {
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("kind")] public string Kind { get; set; }
        [JsonPropertyName("targetFileId")] public int TargetFileId { get; set; }
        [JsonPropertyName("targetPath")] public string TargetPath { get; set; }
    }

    class CSharpMethod
    {
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("returnType")] public string ReturnType { get; set; }
        [JsonPropertyName("accessModifier")] public string AccessModifier { get; set; }
        [JsonPropertyName("parameters")] public List<CSharpParameter> Parameters { get; set; } = new();
        [JsonPropertyName("attributes")] public List<string> Attributes { get; set; } = new();
        [JsonPropertyName("startLine")] public int StartLine { get; set; }
        [JsonPropertyName("endLine")] public int EndLine { get; set; }
        [JsonPropertyName("linesOfCode")] public int LinesOfCode { get; set; }
        [JsonPropertyName("bodyText")] public string BodyText { get; set; }
        [JsonPropertyName("statements")] public List<StatementInfo> Statements { get; set; } = new();
        [JsonPropertyName("methodCalls")] public List<MethodCallInfo> MethodCalls { get; set; } = new();
    }

    class CSharpParameter
    {
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("type")] public string Type { get; set; }
        [JsonPropertyName("attributes")] public List<string> Attributes { get; set; } = new();
        [JsonPropertyName("typeKind")] public string TypeKind { get; set; }
        [JsonPropertyName("typeTargetFileId")] public int TypeTargetFileId { get; set; }
        [JsonPropertyName("typeTargetPath")] public string TypeTargetPath { get; set; }
    }

    class CSharpProperty
    {
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("type")] public string Type { get; set; }
        [JsonPropertyName("accessModifier")] public string AccessModifier { get; set; }
        [JsonPropertyName("typeKind")] public string TypeKind { get; set; }
        [JsonPropertyName("typeTargetFileId")] public int TypeTargetFileId { get; set; }
        [JsonPropertyName("typeTargetPath")] public string TypeTargetPath { get; set; }
    }

    class MethodCallInfo
    {
        [JsonPropertyName("expression")] public string Expression { get; set; }
        [JsonPropertyName("lineNumber")] public int LineNumber { get; set; }
        [JsonPropertyName("arguments")] public List<string> Arguments { get; set; } = new();
        [JsonPropertyName("chain")] public List<string> Chain { get; set; } = new();
    }

    class StatementInfo
    {
        [JsonPropertyName("kind")] public string Kind { get; set; }
        [JsonPropertyName("text")] public string Text { get; set; }
        [JsonPropertyName("startLine")] public int StartLine { get; set; }
        [JsonPropertyName("endLine")] public int EndLine { get; set; }
    }
}