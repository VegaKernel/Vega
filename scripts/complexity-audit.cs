using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Syntax-only metrics: no compilation, semantic assumptions or external packages.
string root = Path.GetFullPath(args.Single());
string source = Path.Combine(root, "src");
var methods = new List<Metric>();
int files = 0, lines = 0, parseErrors = 0;
foreach (string path in Files(source))
{
    string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
    string project = relative.Split('/')[1];
    string text = File.ReadAllText(path);
    var tree = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Preview));
    var syntax = tree.GetCompilationUnitRoot();
    files++;
    lines += tree.GetText().Lines.Count;
    parseErrors += tree.GetDiagnostics().Count(d => d.Severity == DiagnosticSeverity.Error);
    foreach (SyntaxNode member in syntax.DescendantNodes().Where(IsCallable))
    {
        if (member is AccessorDeclarationSyntax accessor && accessor.Body is null && accessor.ExpressionBody is null) continue;
        if (member is BaseMethodDeclarationSyntax method && method.Body is null && method.ExpressionBody is null) continue;
        Add(member, [member], Name(member), relative, project, tree, methods);
    }
    var top = syntax.Members.OfType<GlobalStatementSyntax>().ToArray();
    if (top.Length != 0) Add(syntax, top, "<top-level entrypoint>", relative, project, tree, methods);
}

Console.WriteLine("# Vega complexity audit (Roslyn syntax metrics)\n");
Console.WriteLine($"Source files: **{files}** · physical lines: **{lines:N0}** · callable bodies: **{methods.Count:N0}** · syntax errors: **{parseErrors}**\n");
Console.WriteLine("Decision score = 1 + if/loops/catch/case/conditional/switch arm/logical and-or/coalesce/pattern and-or. Max depth counts nested control constructs. Nested callables are measured separately. Span lines include formatting/comments. These are reproducible screening metrics, not a semantic proof of defect risk.\n");
Console.WriteLine("## By project\n\n| Project | Bodies | Max decision score | Score >= 15 | Score >= 30 | Span >= 100 |\n|---|---:|---:|---:|---:|---:|");
foreach (var group in methods.GroupBy(m => m.Project).OrderByDescending(g => g.Count(m => m.Score >= 15)).ThenBy(g => g.Key))
    Console.WriteLine($"| {group.Key} | {group.Count()} | {group.Max(m => m.Score)} | {group.Count(m => m.Score >= 15)} | {group.Count(m => m.Score >= 30)} | {group.Count(m => m.Lines >= 100)} |");
Console.WriteLine("\n## Direct project dependencies (source ProjectReference graph)\n\n| Project | References | Direct dependents |\n|---|---:|---:|");
var projects = Directory.EnumerateFiles(source, "*.csproj", SearchOption.AllDirectories)
    .ToDictionary(path => Path.GetFileNameWithoutExtension(path), path => XDocument.Load(path).Descendants("ProjectReference")
        .Select(node => Path.GetFileNameWithoutExtension(((string?)node.Attribute("Include") ?? "").Replace('\\', '/')))
        .Where(name => name.Length != 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
foreach (var (project, references) in projects.OrderByDescending(entry => entry.Value.Length).ThenBy(entry => entry.Key))
    Console.WriteLine($"| {project} | {references.Length} | {projects.Count(entry => entry.Value.Contains(project, StringComparer.OrdinalIgnoreCase))} |");
var cycles = projects.Keys.Where(project => HasCycle(project, project, projects, new(StringComparer.OrdinalIgnoreCase))).ToArray();
Console.WriteLine(cycles.Length == 0 ? "\nNo cycles in the source ProjectReference graph." : $"\nProjects in cycles: {string.Join(", ", cycles)}.");
Table("Highest decision scores", methods.OrderByDescending(m => m.Score).ThenByDescending(m => m.Lines).Take(20));
Table("Deepest control nesting", methods.OrderByDescending(m => m.Depth).ThenByDescending(m => m.Score).Take(12));
Table("Longest callable bodies", methods.OrderByDescending(m => m.Lines).Take(12));
Table("Refactored entry points", methods.Where(m => m.Name is "OfflineMigrationCommands.TryRun" or
    "RestAdministration.WriteAsync" or "WorldEventCommand.ExecuteAsync" ||
    m.Name.StartsWith("RestAdministration.Write") && m.Name.EndsWith("Async") ||
    m.Name.StartsWith("OfflineMigrationCommands.Import") || m.Name.StartsWith("WorldEventCommand.") || m.Name.StartsWith("GroupCommand.") ||
    m.Name.StartsWith("RuntimeHost.") || m.Name.StartsWith("TerrariaWorldEnvironmentService.") ||
    m.Name.StartsWith("TerrariaWorldNativeActions.") || m.Name.StartsWith("LoopbackRestService.") ||
    m.Name.StartsWith("BanCommand.") || m.Name.StartsWith("MuteCommand.") ||
    m.Name.StartsWith("NavigationModule.") || m.Name.StartsWith("HomeCommand.") ||
    m.Name.StartsWith("TerrariaSchematicObjects.") || m.Name.StartsWith("FakeProviderCommand.") ||
    m.Name.StartsWith("TerrariaPlayerInventoryService.") && m.File.EndsWith("VirtualChests.cs") ||
    m.Name.StartsWith("NativeGameplayEventHooks.") && m.File.EndsWith("PlayerState.cs") ||
    m.Name.StartsWith("TerrariaPlayerBoundary.") && m.File.EndsWith("Administration.cs") ||
    m.Name.StartsWith("SandboxControlService.") || m.Name.StartsWith("SchematicValidator.") ||
    m.Name.StartsWith("TerrariaSchematicPlacement.") || m.Name.StartsWith("RegionObservations.") ||
    m.Name.StartsWith("ItemCommand.") || m.Name.StartsWith("VegaWorldImageReader.Validate") ||
    m.Name.StartsWith("RestAdministration.CreateSandbox")).Distinct());
return parseErrors == 0 ? 0 : 1;

static bool HasCycle(string start, string current, Dictionary<string, string[]> projects, HashSet<string> visited)
{
    if (!visited.Add(current) || !projects.TryGetValue(current, out var references)) return false;
    return references.Any(reference => string.Equals(reference, start, StringComparison.OrdinalIgnoreCase) ||
        HasCycle(start, reference, projects, visited));
}

static void Table(string title, IEnumerable<Metric> rows)
{
    Console.WriteLine($"\n## {title}\n\n| Method | File:line | Decision score | Depth | Lines |\n|---|---|---:|---:|---:|");
    foreach (var row in rows) Console.WriteLine($"| `{row.Name}` | `{row.File}:{row.Line}` | {row.Score} | {row.Depth} | {row.Lines} |");
}

static IEnumerable<string> Files(string directory)
{
    foreach (string path in Directory.EnumerateFiles(directory, "*.cs")) yield return path;
    foreach (string child in Directory.EnumerateDirectories(directory))
        if (Path.GetFileName(child) is not ("bin" or "obj" or ".git"))
            foreach (string path in Files(child)) yield return path;
}

static bool IsCallable(SyntaxNode node) => node is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AccessorDeclarationSyntax or AnonymousFunctionExpressionSyntax;
static string Name(SyntaxNode node)
{
    string local = node switch
    {
        MethodDeclarationSyntax method => method.Identifier.ValueText,
        ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText + "(ctor)",
        LocalFunctionStatementSyntax function => function.Identifier.ValueText,
        AccessorDeclarationSyntax accessor => accessor.Keyword.ValueText,
        OperatorDeclarationSyntax op => "operator " + op.OperatorToken.ValueText,
        ConversionOperatorDeclarationSyntax => "conversion operator",
        _ => "lambda"
    };
    return string.Join('.', node.Ancestors().OfType<TypeDeclarationSyntax>().Reverse().Select(type => type.Identifier.ValueText).Append(local));
}

static bool Decision(SyntaxNode node) => node.Kind() is SyntaxKind.IfStatement or SyntaxKind.ForStatement or SyntaxKind.ForEachStatement or
    SyntaxKind.ForEachVariableStatement or SyntaxKind.WhileStatement or SyntaxKind.DoStatement or SyntaxKind.CatchClause or SyntaxKind.CaseSwitchLabel or
    SyntaxKind.CasePatternSwitchLabel or SyntaxKind.ConditionalExpression or SyntaxKind.SwitchExpressionArm or SyntaxKind.LogicalAndExpression or
    SyntaxKind.LogicalOrExpression or SyntaxKind.CoalesceExpression or SyntaxKind.CoalesceAssignmentExpression or SyntaxKind.AndPattern or SyntaxKind.OrPattern;
static bool Nesting(SyntaxNode node) => node is IfStatementSyntax or ForStatementSyntax or CommonForEachStatementSyntax or WhileStatementSyntax or
    DoStatementSyntax or SwitchStatementSyntax or SwitchExpressionSyntax or CatchClauseSyntax or ConditionalExpressionSyntax;

static void Add(SyntaxNode scope, IReadOnlyList<SyntaxNode> roots, string name, string file, string project, SyntaxTree tree, List<Metric> output)
{
    int score = 1, depth = 0;
    foreach (var root in roots)
    {
        foreach (SyntaxNode node in root.DescendantNodesAndSelf(descendIntoChildren: child => child == root || !IsCallable(child)))
        {
            if (!Decision(node)) continue;
            score++;
            var ancestors = node.Ancestors().TakeWhile(parent => parent != scope);
            depth = Math.Max(depth, 1 + ancestors.Count(Nesting));
        }
    }
    var first = tree.GetLineSpan(roots[0].Span).StartLinePosition.Line + 1;
    var last = tree.GetLineSpan(roots[^1].Span).EndLinePosition.Line + 1;
    output.Add(new(project, file, name, first, score, depth, last - first + 1));
}

sealed record Metric(string Project, string File, string Name, int Line, int Score, int Depth, int Lines);
