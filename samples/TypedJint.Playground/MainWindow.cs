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
            Content = "Compile, verify, run",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 8, 8)
        };
        runButton.Click += (_, _) => RunScript();

        var title = new TextBlock
        {
            Text = "TypedJint JS → verified delegate + C# preview playground",
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
        AddOutputPanel(outputGrid, "Normalized IR", _ir, 1);
        AddOutputPanel(outputGrid, "Diagnostics", _diagnostics, 2);
        AddOutputPanel(outputGrid, "Execution result", _result, 3);

        Grid.SetRow(outputGrid, 1);
        Grid.SetColumn(outputGrid, 1);
        root.Children.Add(outputGrid);

        return root;
    }

    private void RunScript()
    {
        try
        {
            var source = _source.Text ?? string.Empty;
            var engine = new TypedJintEngine();
            var runtimeCases = CreateRuntimeCases(source);
            var verified = engine.ExecuteVerified(source, runtimeCases);

            _csharp.Text = TypedJintTranspiler.TranspileToCSharp(source);
            _ir.Text = string.Join(Environment.NewLine + Environment.NewLine, verified.CompilerOutputs.Values.Select(x => x.NormalizedIr));
            _diagnostics.Text = FormatDiagnostics(verified);
            _result.Text = FormatExecutionResult(engine, verified);
        }
        catch (Exception ex)
        {
            _diagnostics.Text = ex.ToString();
            _result.Text = "Execution failed.";
        }
    }

    private static Dictionary<string, object?[][]> CreateRuntimeCases(string source)
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

/**
 * @param {number} n
 * @returns {number}
 */
function factorial(n) {
    let acc = 1;
    while (n > 1) {
        acc = acc * n;
        n = n - 1;
    }

    return acc;
}

/**
 * @param {number} x
 * @returns {number}
 */
function abs(x) {
    if (x < 0) {
        return -x;
    }

    return x;
}

/**
 * @param {DomElement} button
 * @returns {void}
 */
function setupButton(button) {
    button.textContent = "Ready";
    button.classList.add("primary");
    button.style.backgroundColor = "red";
}

function dynamicFallback(a, b) {
    return a + b;
}
""";
}
