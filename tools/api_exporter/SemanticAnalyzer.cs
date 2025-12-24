using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AstExporter
{
    // Minimal semantic analyzer using MSBuildWorkspace to resolve symbols.
    // This implementation is best-effort: it returns false from InitializeAsync
    // when MSBuild cannot be located or the solution fails to load.
    class SemanticAnalyzer : IDisposable
    {
        MSBuildWorkspace _workspace;
        Solution _solution;
        List<MethodCallInfo> _collected = new();

        public async Task<bool> InitializeAsync(string solutionPath)
        {
            try
            {
                // Register MSBuild if possible
                try
                {
                    if (!MSBuildLocator.IsRegistered)
                        MSBuildLocator.RegisterDefaults();
                }
                catch
                {
                    // Could not register MSBuild; semantic analysis won't work
                    return false;
                }

                _workspace = MSBuildWorkspace.Create();
                _workspace.WorkspaceFailed += (s, e) => Console.WriteLine($"  MSBuildWorkspace: {e.Diagnostic.Message}");

                _solution = await _workspace.OpenSolutionAsync(solutionPath);
                return _solution != null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  SemanticAnalyzer.InitializeAsync error: {ex.Message}");
                return false;
            }
        }

        public async Task<List<MethodCallInfo>> AnalyzeMethodCallsAsync()
        {
            _collected.Clear();

            if (_solution == null) return _collected;

            foreach (var project in _solution.Projects)
            {
                Compilation compilation = null;
                try
                {
                    compilation = await project.GetCompilationAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  Warning: failed to get compilation for project {project.Name}: {ex.Message}");
                    continue;
                }

                if (compilation == null) continue;

                foreach (var document in project.Documents)
                {
                    SyntaxNode root = null;
                    try { root = await document.GetSyntaxRootAsync(); } catch { }
                    if (root == null) continue;

                    var model = compilation.GetSemanticModel(root.SyntaxTree);

                    var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();
                    foreach (var inv in invocations)
                    {
                        try
                        {
                            var info = new MethodCallInfo();
                            var symbolInfo = model.GetSymbolInfo(inv.Expression);
                            var symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
                            info.Expression = symbol?.ToDisplayString() ?? inv.Expression.ToString();

                            try
                            {
                                var span = inv.GetLocation()?.GetLineSpan();
                                if (span != null && span.HasValue)
                                    info.LineNumber = span.Value.StartLinePosition.Line + 1;
                            }
                            catch { }

                            foreach (var arg in inv.ArgumentList?.Arguments ?? Enumerable.Empty<ArgumentSyntax>())
                                info.Arguments.Add(arg.ToString());

                            _collected.Add(info);
                        }
                        catch { }
                    }
                }
            }

            return _collected;
        }

        public object ExportResults()
        {
            // Return a simple object serializable to JSON
            return new { methods = _collected };
        }

        public void Dispose()
        {
            _workspace?.Dispose();
            _workspace = null;
            _solution = null;
        }
    }
}
