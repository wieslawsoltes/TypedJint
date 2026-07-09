using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using Jint;
using TypedJint;
using TypedJint.Runtime;

namespace TypedJint.Tests;

public class ScratchJintEvaluationTests
{
    private readonly ITestOutputHelper _output;

    public ScratchJintEvaluationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void EvaluateRoughJsInitInJint()
    {
        var engine = new JavaScriptRuntimeEngine();
        engine.RegisterStandardLibrary();
        
        string initScript = """
        function init() {
            var canvas = document.createElement('canvas');
            canvas.width = 300;
            canvas.height = 200;
            canvas.style.width = '300px';
            canvas.style.height = '200px';
            document.body.appendChild(canvas);

            var rc = rough.canvas(canvas);
            
            rc.rectangle(15, 15, 110, 90, {
                fill: 'rgba(255, 0, 100, 0.3)',
                fillStyle: 'solid',
                stroke: 'red',
                strokeWidth: 2
            });
        }
        """;
        
        try
        {
            engine.Execute(initScript);
            engine.Invoke("init");
            _output.WriteLine("SUCCESS: rough init executed successfully!");
        }
        catch (Exception ex)
        {
            _output.WriteLine("EXCEPTION during rough init execution:");
            _output.WriteLine(ex.ToString());
        }
    }

    [Fact]
    public void TestRoughJsCodeGenerationWithTypeRegistry()
    {
        var tsContent = """
        interface RoughCanvas {
            rectangle(x: number, y: number, width: number, height: number, options?: any): void;
        }
        interface Rough {
            canvas(canvas: HTMLCanvasElement): RoughCanvas;
        }
        declare const rough: Rough;
        """;
        var registry = TypeScriptDefParser.Parse(tsContent);
        
        var script = """
        /**
         * @param {HTMLCanvasElement} canvas
         */
        function draw(canvas) {
            var rc = rough.canvas(canvas);
            rc.rectangle(10, 10, 100, 100);
        }
        """;
        
        var options = new OptimizedJavaScriptCSharpGenerationOptions(TypeScriptRegistry: registry);
        var result = OptimizedJavaScriptCSharpGenerator.Generate(script, options);
        
        _output.WriteLine("GENERATED C#:");
        _output.WriteLine(result.Source);
        
        Assert.Contains("draw", result.Source);
    }

    [Fact]
    public void EvaluateThreeJsInitInJint()
    {
        var engine = new JavaScriptRuntimeEngine();
        engine.RegisterStandardLibrary();
        
        string initScript = """
        function init() {
            var canvas = document.createElement('canvas');
            canvas.width = 300;
            canvas.height = 200;
            canvas.style.width = '300px';
            canvas.style.height = '200px';
            document.body.appendChild(canvas);

            var gl = canvas.getContext('webgl');
            gl.viewport(0, 0, 300, 200);

            var buffer = gl.createBuffer();
            gl.bindBuffer(gl.ARRAY_BUFFER, buffer);

            var vertices = [
                 0.0,  0.6,  0.0,
                -0.6, -0.6,  0.0,
                 0.6, -0.6,  0.0
            ];
            gl.bufferData(gl.ARRAY_BUFFER, vertices, gl.STATIC_DRAW);
            gl.drawArrays(gl.TRIANGLES, 0, 3);
        }
        """;
        
        try
        {
            engine.Execute(initScript);
            engine.Invoke("init");
            _output.WriteLine("SUCCESS: three init executed successfully!");
        }
        catch (Exception ex)
        {
            _output.WriteLine("EXCEPTION during three init execution:");
            _output.WriteLine(ex.ToString());
            throw;
        }
    }

    [Fact]
    public void EvaluateLightweightChartsInitInJint()
    {
        var engine = new JavaScriptRuntimeEngine();
        engine.RegisterStandardLibrary();
        
        string initScript = """
        function init() {
            var container = document.createElement('div');
            container.style.width = '300px';
            container.style.height = '200px';
            container.style.backgroundColor = '#131722';
            document.body.appendChild(container);

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
                { time: '2026-07-02', value: 96.63 }
            ]);
        }
        """;
        
        try
        {
            engine.Execute(initScript);
            engine.Invoke("init");
            _output.WriteLine("SUCCESS: lightweight-charts init executed successfully!");
        }
        catch (Exception ex)
        {
            _output.WriteLine("EXCEPTION during lightweight-charts init execution:");
            _output.WriteLine(ex.ToString());
            throw;
        }
    }

    [Fact]
    public void EvaluateD3JsInitInJint()
    {
        var engine = new JavaScriptRuntimeEngine();
        engine.RegisterStandardLibrary();
        
        string initScript = """
        function init() {
            var container = d3.select(document.body)
                .append("div")
                .style("width", "300px")
                .style("height", "200px")
                .style("background-color", "#111116");

            container.append("h3")
                .text("Real D3.js Data Binding")
                .style("color", "#00ffcc");

            var data = [10, 20, 30];
            container.selectAll("div.item")
                .data(data)
                .enter()
                .append("div")
                .text(function(d) { return "D3 Item Val: " + d; });
        }
        """;
        
        try
        {
            engine.Execute(initScript);
            engine.Invoke("init");
            _output.WriteLine("SUCCESS: d3 init executed successfully!");
        }
        catch (Exception ex)
        {
            _output.WriteLine("EXCEPTION during d3 init execution:");
            _output.WriteLine(ex.ToString());
            throw;
        }
    }

    [Fact(Skip = "Scratch diagnostic test relies on headless WebGL context")]
    public void EvaluatePixiJsInitInJint()
    {
        var engine = new JavaScriptRuntimeEngine();
        engine.RegisterStandardLibrary();
        
        string initScript = """
        function init() {
            var container = document.createElement('div');
            container.style.width = '300px';
            container.style.height = '200px';
            document.body.appendChild(container);

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
            app.stage.addChild(graphics);
        }
        """;
        
        try
        {
            engine.Execute(initScript);
            engine.Invoke("init");
            _output.WriteLine("SUCCESS: pixi init executed successfully!");
        }
        catch (Exception ex)
        {
            _output.WriteLine("EXCEPTION during pixi init execution:");
            _output.WriteLine(ex.ToString());
            throw;
        }
    }

    [Fact]
    public async Task EvaluateDownloadedLibrariesInJint()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            _output.WriteLine($"Downloading libraries to {tempDir}...");
            await LibraryDownloader.DownloadLibrariesAsync(tempDir);
            
            var jsFiles = new[]
            {
                ("rough.js", "rough"),
                ("three.js", "THREE"),
                ("lightweight-charts.js", "LightweightCharts"),
                ("d3.js", "d3"),
                ("pixi.js", "PIXI")
            };

            foreach (var (jsFile, globalName) in jsFiles)
            {
                var filePath = Path.Combine(tempDir, jsFile);
                if (!File.Exists(filePath))
                {
                    _output.WriteLine($"ERROR: {jsFile} was not downloaded.");
                    continue;
                }

                _output.WriteLine($"--------------------------------------------------");
                _output.WriteLine($"Evaluating {jsFile} in JavaScriptRuntimeEngine...");
                var code = await File.ReadAllTextAsync(filePath);

                var runtimeEngine = new JavaScriptRuntimeEngine().RegisterStandardLibrary();

                try
                {
                    runtimeEngine.Jint.Execute(code);
                    _output.WriteLine($"SUCCESS: Evaluated {jsFile} without exceptions.");
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"EXCEPTION during evaluation of {jsFile}:");
                    _output.WriteLine(ex.ToString());
                }

                var globalVal = runtimeEngine.Jint.GetValue(globalName);
                if (globalVal == null || globalVal.IsUndefined())
                {
                    _output.WriteLine($"Global variable '{globalName}' is UNDEFINED.");
                    
                    // Check if it is defined on the window object
                    try
                    {
                        var windowVal = runtimeEngine.Jint.Evaluate($"window.{globalName}");
                        if (windowVal != null && !windowVal.IsUndefined())
                        {
                            _output.WriteLine($"Global variable '{globalName}' is DEFINED on the window object (Type: {windowVal.Type}).");
                        }
                    }
                    catch (Exception ex)
                    {
                        _output.WriteLine($"Error evaluating window.{globalName}: {ex.Message}");
                    }
                }
                else
                {
                    _output.WriteLine($"Global variable '{globalName}' is DEFINED (Type: {globalVal.Type}).");
                }
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, true);
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"Failed to delete temp dir {tempDir}: {ex.Message}");
                }
            }
        }
    }

    [Fact]
    public void EvaluateLightweightChartsCompiledCSharp()
    {
        var domDoc = new DomDocument();
        JavaScriptRuntimeEngine.CurrentDocument = domDoc;
        JavaScriptRuntimeEngine.CurrentWindow = new DomWindow(domDoc);

        var runtimeEngine = new JavaScriptRuntimeEngine(new JavaScriptRuntimeOptions
        {
            Document = domDoc
        }).RegisterStandardLibrary();
        JavaScriptRuntimeEngine.CurrentEngine = runtimeEngine;

        string initScript = """
        function init() {
            var container = document.createElement('div');
            container.style.width = '300px';
            container.style.height = '200px';
            container.style.backgroundColor = '#131722';
            document.body.appendChild(container);

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
                { time: '2026-07-02', value: 96.63 }
            ]);
        }
        """;

        try
        {
            var genOptions = new OptimizedJavaScriptCSharpGenerationOptions(EmitRuntimeFallback: false);
            var generated = OptimizedJavaScriptCSharpGenerator.Generate(initScript, genOptions);
            _output.WriteLine("Generated C# Source:");
            _output.WriteLine(generated.Source);

            var buildResult = GeneratedCSharpCompiler.CreateScriptInstance(generated.Source, "ScriptModule");
            if (!buildResult.Success || buildResult.Instance is null)
            {
                _output.WriteLine("Roslyn compilation failed:");
                _output.WriteLine(buildResult.Build.DiagnosticsText);
                Assert.Fail("Roslyn compilation failed");
            }

            var scriptInstance = (GeneratedCSharpScriptInstance)buildResult.Instance;
            scriptInstance.InvokeMethod("init");
            _output.WriteLine("SUCCESS: compiled C# init executed successfully!");
            _output.WriteLine("DOM Tree Structure:");
            PrintDom(domDoc.body, 0);
        }
        catch (Exception ex)
        {
            _output.WriteLine("EXCEPTION during compiled C# init execution:");
            _output.WriteLine(ex.ToString());
            throw;
        }
    }

    private void PrintDom(DomNode node, int indent)
    {
        var pad = new string(' ', indent * 2);
        if (node is DomElement elem)
        {
            var styleStr = string.Join("; ", elem.style.width != null ? $"width: {elem.style.width}" : "", elem.style.height != null ? $"height: {elem.style.height}" : "");
            var widthStr = elem is HTMLCanvasElement canvas ? $" [canvas width={canvas.width} height={canvas.height}]" : "";
            _output.WriteLine($"{pad}<{elem.tagName} id=\"{elem.id}\" style=\"{styleStr}\">{widthStr}");
            
            if (elem is HTMLCanvasElement cv)
            {
                var ctx2d = cv.getContext("2d") as CanvasRenderingContext2D;
                _output.WriteLine($"{pad}  Canvas 2D commands count: {ctx2d?.DrawingContext.Commands.Count ?? 0}");
                if (ctx2d != null)
                {
                    foreach (var cmd in ctx2d.DrawingContext.Commands)
                    {
                        _output.WriteLine($"{pad}    Command: {cmd.Type} rect={cmd.Rect} text={cmd.Text}");
                    }
                }
            }
        }
        else
        {
            _output.WriteLine($"{pad}{node.nodeName}: {node.textContent}");
        }
        
        foreach (var child in node.childNodes)
        {
            PrintDom(child, indent + 1);
        }
    }
}
