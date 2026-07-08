using System.Globalization;
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
            Text = "TypedJint JS → readable C# preview + Roslyn executable build/run",
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
            _csharp.Text = generated.PreviewSource;

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
            var nativeSource = BuildNativeSource(source, generated.NativeFunctions);
            var verified = TryExecuteVerified(nativeSource, diagnostics);

            _ir.Text = verified is null
                ? generated.NativeFunctions.Count == 0
                    ? "No native typed IR was generated. Runtime functions are represented by generated C# facades and executed by the generated runtime module."
                    : "Native methods were generated. Native IR verification was skipped."
                : string.Join(Environment.NewLine + Environment.NewLine, verified.CompilerOutputs.Values.Select(x => x.NormalizedIr));

            var resultBuilder = new StringBuilder();
            if (verified is not null)
            {
                var typedEngine = new TypedJintEngine().RegisterStandardLibrary();
                typedEngine.Execute(nativeSource);
                resultBuilder.AppendLine(FormatExecutionResult(typedEngine, verified));
                diagnostics.AppendLine(FormatDiagnostics(verified));
            }

            resultBuilder.AppendLine(generatedRunText);
            _result.Text = resultBuilder.ToString();
            _diagnostics.Text = diagnostics.ToString();
        }
        catch (Exception ex)
        {
            _csharp.Text = JavaScriptCSharpGenerator.GenerateRuntimeTopLevelStatements(source);
            using var capture = JavaScriptConsole.Capture();
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
                ? "Fallback runtime-compatible generated C# compiled and ran." + Environment.NewLine + FormatConsoleOutput(capture)
                : "Execution failed.";
        }
    }

    private static string BuildNativeSource(string source, IReadOnlyList<string> nativeFunctions)
    {
        if (nativeFunctions.Count == 0)
        {
            return string.Empty;
        }

        var names = nativeFunctions.ToHashSet(StringComparer.Ordinal);
        var builder = new StringBuilder();
        foreach (var function in JavaScriptFunctionSourceScanner.Scan(source))
        {
            if (names.Contains(function.Name))
            {
                builder.AppendLine(function.Source);
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }

    private static VerifiedTypedCompilationResult? TryExecuteVerified(string nativeSource, StringBuilder diagnostics)
    {
        if (string.IsNullOrWhiteSpace(nativeSource))
        {
            diagnostics.AppendLine("Native typed verification: no native functions emitted.");
            diagnostics.AppendLine();
            return null;
        }

        try
        {
            var engine = new TypedJintEngine().RegisterStandardLibrary();
            return engine.ExecuteVerified(nativeSource, CreateRuntimeCases(nativeSource));
        }
        catch (Exception ex)
        {
            diagnostics.AppendLine("Native typed verification skipped for emitted native functions.");
            diagnostics.AppendLine(ex.GetType().Name + ": " + ex.Message);
            diagnostics.AppendLine();
            return null;
        }
    }

    private static string CompileAndRunGeneratedCSharp(OptimizedJavaScriptCSharpGenerationResult generated, StringBuilder diagnostics)
    {
        using var capture = JavaScriptConsole.Capture();
        var execution = GeneratedCSharpCompiler.CreateScriptInstance(generated.Source, "ScriptModule");
        diagnostics.AppendLine("Generated executable C# Roslyn build:");
        diagnostics.AppendLine(FormatGeneratedBuild(execution.Build));
        diagnostics.AppendLine();

        if (!execution.Build.Success)
        {
            return "generated executable C# build failed" + Environment.NewLine + execution.Build.DiagnosticsText;
        }

        if (execution.Exception is not null)
        {
            return "generated executable C# instantiation failed" + Environment.NewLine + execution.Exception.Message;
        }

        if (execution.Instance is not GeneratedCSharpScriptInstance script)
        {
            return "generated executable C# build succeeded, but no script instance was created.";
        }

        var builder = new StringBuilder();
        builder.AppendLine("generated executable C# build: succeeded");
        builder.Append("generated type: ").AppendLine(script.ScriptType.FullName);
        builder.AppendLine();

        foreach (var method in script.PublicScriptMethods.Where(IsCallableNativePreviewMethod))
        {
            try
            {
                var value = script.InvokeMethod(method.Name);
                builder.Append("native ").Append(method.Name).Append("() = ").AppendLine(FormatResultValue(value));
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
                builder.Append("runtime ").Append(function).Append("() = ").AppendLine(FormatResultValue(value));
            }
            catch (Exception ex)
            {
                builder.Append("runtime ").Append(function).Append("() skipped: ").AppendLine(GetUserMessage(ex));
            }
        }

        var consoleText = FormatConsoleOutput(capture);
        if (!string.IsNullOrWhiteSpace(consoleText))
        {
            builder.AppendLine();
            builder.Append(consoleText);
        }

        return builder.ToString();
    }

    private static bool IsCallableNativePreviewMethod(System.Reflection.MethodInfo method)
    {
        return method.Name is not ("Invoke" or "Evaluate") && method.GetParameters().Length == 0;
    }

    private static string FormatGeneratedBuild(GeneratedCSharpBuildResult build)
    {
        var builder = new StringBuilder();
        builder.Append("success: ").AppendLine(build.Success.ToString());
        builder.AppendLine(build.Diagnostics.Count > 0 ? build.DiagnosticsText : "No diagnostics.");
        return builder.ToString();
    }

    private static string FormatConsoleOutput(JavaScriptConsoleCapture capture)
    {
        var builder = new StringBuilder();
        var stdout = capture.Output.ToString();
        var stderr = capture.Error.ToString();

        if (!string.IsNullOrWhiteSpace(stdout))
        {
            builder.AppendLine("console output:");
            builder.Append(stdout.TrimEnd()).AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            builder.AppendLine("console error:");
            builder.Append(stderr.TrimEnd()).AppendLine();
        }

        return builder.ToString();
    }

    private static string FormatResultValue(object? value)
    {
        if (value is null)
        {
            return "null";
        }

        if (value is Delegate || value.GetType().FullName?.StartsWith("System.Func`", StringComparison.Ordinal) == true)
        {
            return "[function]";
        }

        return value is Array array
            ? "[" + string.Join(", ", array.Cast<object?>().Select(FormatResultValue)) + "]"
            : Convert.ToString(value, CultureInfo.InvariantCulture) ?? value.ToString() ?? string.Empty;
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
            builder.Append("sumEven(10) = ").AppendLine(FormatResultValue(engine.Invoke("sumEven", 10.0)));
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
    let count = 0; // This variable is enclosed

    return function() {
        count++;
        return count;
    };
}

const counter = createCounter();
console.log(counter());
console.log(counter());

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
