using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TypedJint.Playground;

public sealed class MainWindow : Window
{
    private readonly TextBox _source;
    private readonly TextBox _csharp;
    private readonly TextBox _ir;
    private readonly TextBox _diagnostics;
    private readonly TextBox _result;

    public MainWindow()
    {
        Title = "TypedJint Playground";
        Width = 1400;
        Height = 900;
        MinWidth = 900;
        MinHeight = 600;

        _source = CreateEditor(DefaultScript, readOnly: false);
        _csharp = CreateEditor(string.Empty, readOnly: true);
        _ir = CreateEditor(string.Empty, readOnly: true);
        _diagnostics = CreateEditor(string.Empty, readOnly: true);
        _result = CreateEditor(string.Empty, readOnly: true);

        Content = BuildUi();
        RunScript();
    }

    private Control BuildUi()
    {
        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var runButton = new Button
        {
            Content = "Compile, verify, build, run",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 8, 8)
        };
        runButton.Click += (_, _) => RunScript();

        var title = new TextBlock
        {
            Text = "TypedJint JS → native C# where safe + runtime fallback where needed + Roslyn build/run",
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal };
        toolbar.Children.Add(runButton);
        toolbar.Children.Add(title);
        Grid.SetRow(toolbar, 0);
        Grid.SetColumnSpan(toolbar, 2);
        root.Children.Add(toolbar);

        var sourcePanel = CreateLabeledPanel("JavaScript input", _source);
        Grid.SetRow(sourcePanel, 1);
        Grid.SetColumn(sourcePanel, 0);
        root.Children.Add(sourcePanel);

        var outputGrid = new Grid { Margin = new Thickness(12, 0, 0, 0) };
        outputGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        outputGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        outputGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        outputGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        AddOutputPanel(outputGrid, "Generated C# preview", _csharp, 0);
        AddOutputPanel(outputGrid, "Normalized native IR", _ir, 1);
        AddOutputPanel(outputGrid, "Diagnostics + Roslyn build", _diagnostics, 2);
        AddOutputPanel(outputGrid, "Execution result", _result, 3);

        Grid.SetRow(outputGrid, 1);
        Grid.SetColumn(outputGrid, 1);
        root.Children.Add(outputGrid);

        return root;
    }

    private void RunScript()
    {
        var source = _source.Text ?? string.Empty;
        var diagnostics = new StringBuilder();

        try
        {
            var generated = OptimizedJavaScriptCSharpGenerator.Generate(source);
            _csharp.Text = generated.Source;

            diagnostics.AppendLine("Generated C# mode: optimized hybrid");
            diagnostics.Append("native functions: ").AppendLine(FormatList(generated.NativeFunctions));
            diagnostics.Append("runtime functions: ").AppendLine(FormatList(generated.RuntimeFunctions));
            diagnostics.AppendLine();

            foreach (var diagnostic in generated.Diagnostics)
            {
                diagnostics.Append(diagnostic.Code)
                    .Append(' ')
                    .Append(diagnostic.Severity)
                    .Append(": ")
                    .AppendLine(diagnostic.Message);
            }

            diagnostics.AppendLine();

            var generatedRunText = CompileAndRunGeneratedCSharp(generated, diagnostics);
            var verified = TryExecuteVerified(source, diagnostics);
            if (verified is not null)
            {
                _ir.Text = string.Join(Environment.NewLine + Environment.NewLine, verified.CompilerOutputs.Values.Select(x => x.NormalizedIr));
                var typedEngine = new TypedJintEngine().RegisterStandardLibrary();
                typedEngine.Execute(source);

                var resultBuilder = new StringBuilder();
                resultBuilder.AppendLine(FormatExecutionResult(typedEngine, verified));
                resultBuilder.AppendLine();
                resultBuilder.AppendLine(generatedRunText);
                _result.Text = resultBuilder.ToString();

                diagnostics.AppendLine(FormatDiagnostics(verified));
            }
            else
            {
                _ir.Text = generated.NativeFunctions.Count == 0
                    ? "No native typed IR was generated. The source is still represented by runtime-compatible generated C#."
                    : "Native methods were generated. Verified IR is unavailable because the whole script contains dynamic JavaScript.";

                _result.Text = generatedRunText;
            }

            _diagnostics.Text = diagnostics.ToString();
        }
        catch (Exception ex)
        {
            _csharp.Text = JavaScriptCSharpGenerator.GenerateRuntimeTopLevelStatements(source);
            var topLevelRun = GeneratedCSharpCompiler.RunTopLevelProgram(_csharp.Text);
            _ir.Text = "No native typed IR was generated.";
            _diagnostics.Text = diagnostics
                .AppendLine()
                .AppendLine("Runtime execution failed:")
                .AppendLine(ex.Message)
                .AppendLine()
                .AppendLine("Fallback generated C# build:")
                .AppendLine(FormatGeneratedBuild(topLevelRun.Build))
                .ToString();
            _result.Text = topLevelRun.Success
                ? "Fallback runtime-compatible generated C# compiled and ran."
                : "Execution failed.";
        }
    }

    private static VerifiedTypedCompilationResult? TryExecuteVerified(string source, StringBuilder diagnostics)
    {
        try
        {
            var engine = new TypedJintEngine().RegisterStandardLibrary();
            return engine.ExecuteVerified(source, CreateRuntimeCases(source));
        }
        catch (Exception ex)
        {
            diagnostics.AppendLine("Typed native verification skipped for complete script.");
            diagnostics.AppendLine(ex.GetType().Name + ": " + ex.Message);
            diagnostics.AppendLine("The playground will execute through generated C# / JavaScriptRuntimeEngine instead of failing.");
            diagnostics.AppendLine();
            return null;
        }
    }

    private static string CompileAndRunGeneratedCSharp(
        OptimizedJavaScriptCSharpGenerationResult generated,
        StringBuilder diagnostics)
    {
        var execution = GeneratedCSharpCompiler.CreateScriptInstance(generated.Source, "ScriptModule");
        diagnostics.AppendLine("Generated C# Roslyn build:");
        diagnostics.AppendLine(FormatGeneratedBuild(execution.Build));
        diagnostics.AppendLine();

        if (!execution.Build.Success)
        {
            return "generated C# build failed" + Environment.NewLine + execution.Build.DiagnosticsText;
        }

        if (execution.Exception is not null)
        {
            return "generated C# instantiation failed" + Environment.NewLine + execution.Exception.Message;
        }

        if (execution.Instance is not GeneratedCSharpScriptInstance script)
        {
            return "generated C# build succeeded, but no script instance was created.";
        }

        var builder = new StringBuilder();
        builder.AppendLine("generated C# build: succeeded");
        builder.Append("generated type: ").AppendLine(script.ScriptType.FullName);
        builder.AppendLine();

        foreach (var method in script.PublicScriptMethods.Where(IsCallableNativePreviewMethod))
        {
            try
            {
                var value = script.InvokeMethod(method.Name);
                builder.Append("native ").Append(method.Name).Append("() = ").AppendLine(value?.ToString() ?? "null");
            }
            catch (Exception ex)
            {
                builder.Append("native ").Append(method.Name).Append("() skipped: ").AppendLine(GetUserMessage(ex));
            }
        }

        foreach (var function in generated.RuntimeFunctions.Take(8))
        {
            try
            {
                var value = script.InvokeRuntime(function);
                builder.Append("runtime ").Append(function).Append("() = ").AppendLine(value?.ToString() ?? "null");
            }
            catch (Exception ex)
            {
                builder.Append("runtime ").Append(function).Append("() skipped: ").AppendLine(GetUserMessage(ex));
            }
        }

        if (generated.NativeFunctions.Count == 0 && generated.RuntimeFunctions.Count == 0)
        {
            builder.AppendLine("No callable functions were discovered in generated code.");
        }

        return builder.ToString();
    }

    private static bool IsCallableNativePreviewMethod(System.Reflection.MethodInfo method)
    {
        if (method.Name is "Invoke" or "Evaluate")
        {
            return false;
        }

        return method.GetParameters().Length == 0;
    }

    private static string FormatGeneratedBuild(GeneratedCSharpBuildResult build)
    {
        var builder = new StringBuilder();
        builder.Append("success: ").AppendLine(build.Success.ToString());
        if (build.Diagnostics.Count > 0)
        {
            builder.AppendLine(build.DiagnosticsText);
        }
        else
        {
            builder.AppendLine("No diagnostics.");
        }

        return builder.ToString();
    }

    private static string GetUserMessage(Exception ex)
    {
        return ex is System.Reflection.TargetInvocationException { InnerException: not null } tie
            ? tie.InnerException.Message
            : ex.Message;
    }

    private static Dictionary<string, object?[][]> CreateRuntimeCases(string source)
    {
        try
        {
            var names = SimpleJsParser.ParseFunctions(source).Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
            var cases = new Dictionary<string, object?[][]>(StringComparer.Ordinal);

            if (names.Contains("sumEven"))
            {
                cases["sumEven"] = new[]
                {
                    new object?[] { 0.0 },
                    new object?[] { 6.0 },
                    new object?[] { 10.0 }
                };
            }

            if (names.Contains("factorial"))
            {
                cases["factorial"] = new[]
                {
                    new object?[] { 1.0 },
                    new object?[] { 3.0 },
                    new object?[] { 5.0 }
                };
            }

            if (names.Contains("abs"))
            {
                cases["abs"] = new[]
                {
                    new object?[] { -10.0 },
                    new object?[] { 0.0 },
                    new object?[] { 42.0 }
                };
            }

            return cases;
        }
        catch
        {
            return new Dictionary<string, object?[][]>(StringComparer.Ordinal);
        }
    }

    private static string FormatDiagnostics(VerifiedTypedCompilationResult verified)
    {
        var builder = new StringBuilder();
        builder.Append("verified: ").AppendLine(verified.Verified.ToString());
        builder.AppendLine();

        foreach (var diagnostic in verified.Compilation.Diagnostics)
        {
            builder.Append(diagnostic.Code)
                .Append(' ')
                .Append(diagnostic.Severity)
                .Append(": ")
                .AppendLine(diagnostic.Message);
        }

        foreach (var output in verified.CompilerOutputs.Values)
        {
            builder.AppendLine();
            builder.AppendLine(output.ToMarkdown());
        }

        foreach (var output in verified.RuntimeOutputs.Values)
        {
            builder.AppendLine();
            builder.AppendLine(output.ToMarkdown());
        }

        return builder.ToString();
    }

    private static string FormatExecutionResult(TypedJintEngine engine, VerifiedTypedCompilationResult verified)
    {
        var builder = new StringBuilder();
        builder.Append("compiled: ").AppendLine(string.Join(", ", verified.Compilation.CompiledFunctions.Keys));
        builder.Append("fallback: ").AppendLine(string.Join(", ", verified.Compilation.Fallbacks.Keys));
        builder.AppendLine();

        if (verified.Compilation.CompiledFunctions.ContainsKey("sumEven"))
        {
            builder.Append("sumEven(10) = ").AppendLine(Convert.ToString(engine.Invoke("sumEven", 10.0), System.Globalization.CultureInfo.InvariantCulture));
        }

        if (verified.Compilation.CompiledFunctions.ContainsKey("factorial"))
        {
            builder.Append("factorial(5) = ").AppendLine(Convert.ToString(engine.Invoke("factorial", 5.0), System.Globalization.CultureInfo.InvariantCulture));
        }

        if (verified.Compilation.CompiledFunctions.ContainsKey("abs"))
        {
            builder.Append("abs(-42) = ").AppendLine(Convert.ToString(engine.Invoke("abs", -42.0), System.Globalization.CultureInfo.InvariantCulture));
        }

        if (verified.Compilation.CompiledFunctions.ContainsKey("setupButton"))
        {
            var button = engine.Document.createElement("button");
            engine.Invoke("setupButton", button);
            builder.AppendLine();
            builder.AppendLine("DOM interop:");
            builder.Append("button.textContent = ").AppendLine(button.textContent);
            builder.Append("button.classList = ").AppendLine(button.classList.ToString());
            builder.Append("button.style.backgroundColor = ").AppendLine(button.style.backgroundColor);
        }

        return builder.ToString();
    }

    private static string FormatList(IEnumerable<string> values)
    {
        var array = values.ToArray();
        return array.Length == 0 ? "(none)" : string.Join(", ", array);
    }

    private static TextBox CreateEditor(string text, bool readOnly)
    {
        return new TextBox
        {
            Text = text,
            AcceptsReturn = true,
            IsReadOnly = readOnly,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = FontFamily.Parse("Consolas, Menlo, Monaco, monospace"),
            FontSize = 13
        };
    }

    private static Control CreateLabeledPanel(string label, Control content)
    {
        var panel = new DockPanel();
        var text = new TextBlock
        {
            Text = label,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        };
        DockPanel.SetDock(text, Dock.Top);
        panel.Children.Add(text);
        panel.Children.Add(content);
        return panel;
    }

    private static void AddOutputPanel(Grid grid, string label, Control content, int row)
    {
        var panel = CreateLabeledPanel(label, content);
        panel.Margin = row == 0 ? new Thickness(0, 0, 0, 8) : new Thickness(0, 8, 0, 0);
        Grid.SetRow(panel, row);
        grid.Children.Add(panel);
    }

    private const string DefaultScript = """
/**
 * @param {number} limit
 * @returns {number}
 */
function sumEven(limit) {
    let acc = 0;
    for (let i = 0; i <= limit; i++) {
        if (i % 2 === 0) {
            acc = acc + i;
        }
    }

    return acc;
}

function createCounter() {
    let count = 0;

    return function() {
        count++;
        return count;
    };
}

class Counter {
    constructor(value) {
        this.value = value;
    }

    next() {
        return ++this.value;
    }
}

function runDynamic() {
    const counter = new Counter(41);
    console.log("runtime", counter.next());
    return counter.next();
}
""";
}
