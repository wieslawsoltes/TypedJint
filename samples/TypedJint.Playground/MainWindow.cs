using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TypedJint.Runtime;

namespace TypedJint.Playground;

public sealed class MainWindow : Window
{
    private readonly TextBox _source;
    private readonly TextBox _csharp;
    private readonly TextBox _dependenciesCsharp;
    private readonly TextBox _libraryCsharp;
    private readonly TextBox _ir;
    private readonly TextBox _diagnostics;
    private readonly TextBox _result;
    private readonly Border _visualHost;
    private readonly ComboBox _sampleSelector;
    private TypeScriptTypeRegistry _typeRegistry = new();
    private readonly System.Collections.Generic.Dictionary<string, string> _libraryCsharpCache = new();

    public MainWindow()
    {
        Title = "TypedJint Playground";
        Width = 1400;
        Height = 900;
        MinWidth = 900;
        MinHeight = 600;

        _source = CreateEditor(DefaultScript, readOnly: false);
        _csharp = CreateEditor(string.Empty, readOnly: true);
        _dependenciesCsharp = CreateEditor(string.Empty, readOnly: true);
        _libraryCsharp = CreateEditor(string.Empty, readOnly: true);
        _ir = CreateEditor(string.Empty, readOnly: true);
        _diagnostics = CreateEditor(string.Empty, readOnly: true);
        _result = CreateEditor(string.Empty, readOnly: true);
        
        _visualHost = new Border
        {
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8),
            Background = Brushes.Black,
            Margin = new Thickness(12, 0, 0, 0)
        };

        _sampleSelector = new ComboBox
        {
            Width = 260,
            Margin = new Thickness(0, 0, 16, 8),
            VerticalAlignment = VerticalAlignment.Center
        };

        Content = BuildUi();
        RunScript();

        // Down-load libraries asynchronously in the background to avoid freezing app startup
        _diagnostics.Text = "Checking and downloading external JS libraries and TypeScript definitions asynchronously...\n";
        System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                var sharedDir = LibraryDownloader.GetSharedDirectory();
                await LibraryDownloader.DownloadLibrariesAsync(sharedDir);
                var registry = LibraryDownloader.LoadDefinitions(sharedDir);
                
                Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
                {
                    _typeRegistry = registry;
                    _diagnostics.Text += "External type definitions loaded successfully!\n\n";
                    RunScript();
                    
                    if (Environment.GetCommandLineArgs().Contains("--auto-test"))
                    {
                        await RunVerificationCycle();
                    }
                });
            }
            catch (Exception ex)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    _diagnostics.Text += $"Failed to load external type definitions: {ex.Message}\n\n";
                });
            }
        });
    }

    private Control BuildUi()
    {
        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });

        var runButton = new Button
        {
            Content = "Compile, verify, build, run",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 16, 8)
        };
        runButton.Click += (_, _) => RunScript();

        var selectLabel = new TextBlock
        {
            Text = "Sample Code:",
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 8, 8)
        };

        _sampleSelector.ItemsSource = new[]
        {
            "Math / Counters Loop (Default)",
            "Rough.js - 2D Vector Drawing",
            "Three.js - Projected 3D WebGL Mesh",
            "TradingView - Lightweight Charts",
            "D3.js - Dynamic Bar Chart Visualizer",
            "PixiJS - 60FPS Interactive Particles"
        };
        _sampleSelector.SelectedIndex = 0;
        _sampleSelector.SelectionChanged += (s, e) =>
        {
            var idx = _sampleSelector.SelectedIndex;
            _source.Text = idx switch
            {
                1 => RoughJsTemplate,
                2 => ThreeJsTemplate,
                3 => LightweightChartsTemplate,
                4 => D3JsTemplate,
                5 => PixiJsTemplate,
                _ => DefaultScript
            };
        };

        var title = new TextBlock
        {
            Text = "TypedJint JS → C# preview + Live Interactive DOM Visual Host (Avalonia/ProGPU)",
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8)
        };

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal };
        toolbar.Children.Add(runButton);
        toolbar.Children.Add(selectLabel);
        toolbar.Children.Add(_sampleSelector);
        toolbar.Children.Add(title);
        
        Grid.SetRow(toolbar, 0);
        Grid.SetColumnSpan(toolbar, 3);
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

        var tabControl = new TabControl();
        tabControl.Items.Add(new TabItem { Header = "User C# Code", Content = _csharp });
        tabControl.Items.Add(new TabItem { Header = "Dependencies (C# Interfaces)", Content = _dependenciesCsharp });
        tabControl.Items.Add(new TabItem { Header = "Compiled Library C# Code", Content = _libraryCsharp });
        AddOutputPanel(outputGrid, "Generated C# preview", tabControl, 0);
        AddOutputPanel(outputGrid, "Normalized native IR", _ir, 1);
        AddOutputPanel(outputGrid, "Diagnostics + Roslyn build", _diagnostics, 2);
        AddOutputPanel(outputGrid, "Execution result", _result, 3);

        Grid.SetRow(outputGrid, 1);
        Grid.SetColumn(outputGrid, 1);
        root.Children.Add(outputGrid);

        var visualPanel = CreateLabeledPanel("Live DOM & Interactivity (Avalonia)", _visualHost);
        Grid.SetRow(visualPanel, 1);
        Grid.SetColumn(visualPanel, 2);
        root.Children.Add(visualPanel);

        return root;
    }

    private void RunScript()
    {
        // Clear background timers and animation frames from any previous runs
        JavaScriptStandardLibrary.ClearAllTimers();
        JavaScriptStandardLibrary.ClearAllRafs();

        var source = _source.Text ?? string.Empty;
        var diagnostics = new StringBuilder();

        // Create the active DOM document for this execution run
        var domDoc = new DomDocument();
        JavaScriptRuntimeEngine.CurrentDocument = domDoc;
        JavaScriptRuntimeEngine.CurrentWindow = new DomWindow(domDoc);

        var runtimeEngine = new JavaScriptRuntimeEngine(new JavaScriptRuntimeOptions
        {
            Document = domDoc
        }).RegisterStandardLibrary();
        JavaScriptRuntimeEngine.CurrentEngine = runtimeEngine;

        try
        {
            var genOptions = new OptimizedJavaScriptCSharpGenerationOptions(EmitRuntimeFallback: false, TypeScriptRegistry: _typeRegistry);
            var generated = OptimizedJavaScriptCSharpGenerator.Generate(source, genOptions);
            _csharp.Text = generated.PreviewSource;
            _dependenciesCsharp.Text = TypeScriptCSharpGenerator.Generate(_typeRegistry);

            var idx = _sampleSelector.SelectedIndex;
            string? libFileName = idx switch
            {
                1 => "rough.js",
                2 => "three.js",
                3 => "lightweight-charts.js",
                4 => "d3.js",
                5 => "pixi.js",
                _ => null
            };
            string? libClassName = idx switch
            {
                1 => "RoughLibraryModule",
                2 => "ThreeLibraryModule",
                3 => "LightweightChartsLibraryModule",
                4 => "D3LibraryModule",
                5 => "PixiLibraryModule",
                _ => null
            };

            string? libCsharpSource = null;
            if (libFileName != null)
            {
                if (!_libraryCsharpCache.TryGetValue(libFileName, out libCsharpSource))
                {
                    var libPath = System.IO.Path.Combine(LibraryDownloader.GetSharedDirectory(), libFileName);
                    if (System.IO.File.Exists(libPath))
                    {
                        var libJs = System.IO.File.ReadAllText(libPath);
                        var libGenOptions = new OptimizedJavaScriptCSharpGenerationOptions(ClassName: libClassName ?? "ScriptModule", EmitRuntimeFallback: false);
                        var generatedLib = OptimizedJavaScriptCSharpGenerator.Generate(libJs, libGenOptions);
                        libCsharpSource = generatedLib.PreviewSource;
                        _libraryCsharpCache[libFileName] = libCsharpSource;
                    }
                }
                _libraryCsharp.Text = libCsharpSource ?? $"Library file not found.";
            }
            else
            {
                _libraryCsharp.Text = "No external library dependency for this sample.";
            }

            diagnostics.AppendLine("Generated C# mode: pure C# compilation");
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
            var generatedRunText = CompileAndRunGeneratedCSharp(generated, libCsharpSource, libClassName, diagnostics);
            var nativeSource = BuildNativeSource(source, generated.NativeFunctions);
            var verified = TryExecuteVerified(nativeSource, diagnostics, _typeRegistry);

            _ir.Text = verified is null
                ? generated.NativeFunctions.Count == 0
                    ? "No native typed IR was generated. Runtime functions are represented by generated C# facades and executed by the generated runtime module."
                    : "Native methods were generated. Native IR verification was skipped."
                : string.Join(Environment.NewLine + Environment.NewLine, verified.CompilerOutputs.Values.Select(x => x.NormalizedIr));

            var resultBuilder = new StringBuilder();
            if (verified is not null)
            {
                var typedEngine = new TypedJintEngine().RegisterStandardLibrary().RegisterDom(domDoc);
                typedEngine.TypeScriptRegistry.Merge(_typeRegistry);
                typedEngine.Execute(nativeSource);
                resultBuilder.AppendLine(FormatExecutionResult(typedEngine, verified));
                diagnostics.AppendLine(FormatDiagnostics(verified));
            }

            resultBuilder.AppendLine(generatedRunText);
            _result.Text = resultBuilder.ToString();
            _diagnostics.Text = diagnostics.ToString();

            Console.WriteLine($"\n================== SCRIPT RUN RESULT ==================");
            Console.WriteLine(_result.Text);
            Console.WriteLine($"=======================================================");

            // Render visual controls tree natively in Playground border host
            _visualHost.Child = domDoc.body.AvaloniaControl;
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

    private static VerifiedTypedCompilationResult? TryExecuteVerified(string nativeSource, StringBuilder diagnostics, TypeScriptTypeRegistry typeRegistry)
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
            engine.TypeScriptRegistry.Merge(typeRegistry);
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

    private static string CompileAndRunGeneratedCSharp(
        OptimizedJavaScriptCSharpGenerationResult generated,
        string? libraryCsharpSource,
        string? libraryClassName,
        StringBuilder diagnostics)
    {
        using var capture = JavaScriptConsole.Capture();
        
        var sources = new List<string> { generated.Source };
        if (!string.IsNullOrEmpty(libraryCsharpSource))
        {
            sources.Add(libraryCsharpSource);
        }

        var execution = GeneratedCSharpCompiler.CreateScriptInstance(sources, "ScriptModule");
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

        // Initialize the library module (running its top-level code in its constructor)
        if (!string.IsNullOrEmpty(libraryClassName))
        {
            try
            {
                var libType = execution.Build.Assembly?.GetType(libraryClassName);
                if (libType != null)
                {
                    var libInstance = Activator.CreateInstance(libType);
                    if (libInstance != null)
                    {
                        var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;
                        foreach (var prop in libType.GetProperties(flags))
                        {
                            if (prop.CanRead)
                            {
                                var val = prop.GetValue(libInstance);
                                JavaScriptRuntime.SetGlobal(prop.Name, val);
                            }
                        }
                        foreach (var field in libType.GetFields(flags))
                        {
                            var val = field.GetValue(libInstance);
                            JavaScriptRuntime.SetGlobal(field.Name, val);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                diagnostics.AppendLine($"WARNING: Failed to initialize library class '{libraryClassName}': {ex.Message}");
            }
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
        return ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null
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
        builder.Append("unannotated: ").AppendLine(string.Join(", ", verified.Compilation.Fallbacks.Keys));
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

    private const string RoughJsTemplate = """
/**
 * @returns {void}
 */
function init() {
    var canvas = document.createElement('canvas');
    canvas.width = 300;
    canvas.height = 200;
    canvas.style.width = '300px';
    canvas.style.height = '200px';
    document.body.appendChild(canvas);

    // Call the real rough.js!
    var rc = rough.canvas(canvas);
    
    // Draw sketch rectangle
    rc.rectangle(15, 15, 110, 90, {
        fill: 'rgba(255, 0, 100, 0.3)',
        fillStyle: 'solid',
        stroke: 'red',
        strokeWidth: 2
    });
    
    // Draw sketch circle
    rc.circle(200, 60, 70, {
        fill: 'rgba(0, 200, 255, 0.4)',
        stroke: 'blue',
        strokeWidth: 1.5
    });

    // Draw sketch line
    rc.line(15, 140, 280, 140, {
        stroke: 'green',
        strokeWidth: 3
    });

    // Draw sketch circle interactively on click
    canvas.addEventListener('pointerdown', function(e) {
        rc.circle(e.clientX, e.clientY, 20, {
            fill: 'rgba(255, 200, 0, 0.8)',
            fillStyle: 'solid'
        });
    });
}
""";

    private const string ThreeJsTemplate = """
/**
 * @returns {void}
 */
function init() {
    var scene = new THREE.Scene();
    var camera = new THREE.PerspectiveCamera(75, 1.5, 0.1, 1000);
    var renderer = new THREE.WebGLRenderer();
    renderer.setSize(300, 200);
    document.body.appendChild(renderer.domElement);

    // Create a cube wireframe mesh using Three.js APIs
    var geometry = new THREE.BoxGeometry(1, 1, 1);
    var material = new THREE.MeshBasicMaterial({ color: 0x00d8ff });
    var cube = new THREE.Mesh(geometry, material);
    scene.add(cube);

    camera.position.z = 2;

    setInterval(function() {
        cube.rotation.x += 0.02;
        cube.rotation.y += 0.03;
        renderer.render(scene, camera);
    }, 16);
}
""";

    private const string LightweightChartsTemplate = """
/**
 * @returns {void}
 */
function init() {
    var container = document.createElement('div');
    container.style.width = '300px';
    container.style.height = '200px';
    container.style.backgroundColor = '#131722';
    document.body.appendChild(container);

    // Call the real lightweight-charts.js!
    var chart = LightweightCharts.createChart(container, {
        width: 300,
        height: 200,
        layout: {
            backgroundColor: '#131722',
            textColor: '#d1d4dc',
        },
        grid: {
            vertLines: { color: 'rgba(42, 46, 57, 0.5)' },
            horzLines: { color: 'rgba(42, 46, 57, 0.5)' },
        }
    });

    var lineSeries = chart.addLineSeries({
        color: 'rgba(4, 111, 232, 1)',
        lineWidth: 2,
    });

    lineSeries.setData([
        { time: '2026-07-01', value: 80.01 },
        { time: '2026-07-02', value: 96.63 },
        { time: '2026-07-03', value: 76.64 },
        { time: '2026-07-04', value: 81.89 },
        { time: '2026-07-05', value: 74.43 },
        { time: '2026-07-06', value: 85.01 },
        { time: '2026-07-07', value: 96.63 },
        { time: '2026-07-08', value: 110.15 }
    ]);
}
""";

    private const string D3JsTemplate = """
/**
 * @returns {void}
 */
function init() {
    // Call the real d3.js DOM selection and binding!
    var container = d3.select(document.body)
        .append("div")
        .style("width", "300px")
        .style("height", "200px")
        .style("background-color", "#111116");

    container.append("h3")
        .text("Real D3.js Data Binding")
        .style("color", "#00ffcc")
        .style("margin-top", "10px")
        .style("margin-left", "10px");

    var data = [10, 20, 30];
    container.selectAll("div.item")
        .data(data)
        .enter()
        .append("div")
        .style("margin-top", "5px")
        .style("margin-left", "10px")
        .style("padding", "2px")
        .style("color", "#00ffff")
        .text(function(d) { return "D3 Item Val: " + d; });
}
""";

    private const string PixiJsTemplate = """
/**
 * @returns {void}
 */
function init() {
    var container = document.createElement('div');
    container.style.width = '300px';
    container.style.height = '200px';
    document.body.appendChild(container);

    // Call the real pixi.js application!
    var app = new PIXI.Application({
        width: 300,
        height: 200,
        backgroundColor: 0x1099bb
    });
    container.appendChild(app.view);

    var graphics = new PIXI.Graphics();
    graphics.beginFill(0xde3249);
    graphics.drawRect(-35, -35, 70, 70);
    graphics.endFill();
    
    graphics.x = 150;
    graphics.y = 100;

    app.stage.addChild(graphics);

    app.ticker.add(function(delta) {
        graphics.rotation += 0.02 * delta;
    });
}
""";

    private async System.Threading.Tasks.Task RunVerificationCycle()
    {
        var artifactDir = "/Users/wieslawsoltes/.gemini/antigravity/brain/9d5c81f5-11f6-42ed-abc6-273c3a08c377";
        var names = new[] { "math", "rough", "three", "charts", "d3", "pixi" };

        for (int i = 0; i < names.Length; i++)
        {
            _sampleSelector.SelectedIndex = i;
            RunScript();
            // Let the UI and timers render/execute
            await System.Threading.Tasks.Task.Delay(1000);
            
            var fileName = System.IO.Path.Combine(artifactDir, $"{names[i]}_jint.png");
            SaveVisualToPng(_visualHost.Child, fileName);
        }

        // Close playground window cleanly
        Close();
    }

    private void SaveVisualToPng(Control? control, string filePath)
    {
        if (control == null) return;
        try
        {
            var bounds = control.Bounds;
            var width = (int)Math.Max(bounds.Width, 300);
            var height = (int)Math.Max(bounds.Height, 200);

            Console.WriteLine($"[DEBUG] Sizing visual {control.GetType().Name} (hash={control.GetHashCode()}) to {width}x{height}");
            if (control is Avalonia.Controls.Panel panel)
            {
                Console.WriteLine($"[DEBUG] Panel children count: {panel.Children.Count} (hash={panel.GetHashCode()})");
                foreach (var child in panel.Children)
                {
                    Console.WriteLine($"[DEBUG]   Child: {child.GetType().Name}, Size: {child.Bounds.Width}x{child.Bounds.Height}");
                    if (child is AvaloniaCanvasHost canvasHost)
                    {
                        var ctx = canvasHost.GetType().GetField("_canvas", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(canvasHost) as HTMLCanvasElement;
                        var ctx2d = ctx?.getContext("2d") as CanvasRenderingContext2D;
                        Console.WriteLine($"[DEBUG]     Canvas 2D commands count: {ctx2d?.DrawingContext.Commands.Count ?? 0}");
                    }
                }
            }

            ForceRecursiveLayout(control, new Size(width, height));

            var pixelSize = new PixelSize(width, height);
            var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(pixelSize, new Vector(96, 96));
            bitmap.Render(control);
            bitmap.Save(filePath);
            Console.WriteLine($"[DEBUG] Saved screenshot to {filePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DEBUG] Error saving screenshot: {ex.Message}");
        }
    }

    private void ForceRecursiveLayout(Control control, Size availableSize)
    {
        control.Measure(availableSize);
        control.Arrange(new Rect(0, 0, availableSize.Width, availableSize.Height));
        control.UpdateLayout();
    }
}
