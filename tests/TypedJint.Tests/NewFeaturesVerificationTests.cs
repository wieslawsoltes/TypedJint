using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace TypedJint.Tests;

public class NewFeaturesVerificationTests
{
    [Fact]
    public void TestEsModulesScanner()
    {
        var source = @"
            import { something } from 'module';
            export function add(a, b) {
                return a + b;
            }
            export default class Calculator {
                multiply(x, y) {
                    return x * y;
                }
            }
        ";

        var result = JavaScriptDeclarationScanner.Scan(source);
        Assert.Contains("add", result.Functions);
        Assert.Contains("Calculator", result.Classes);
    }

    [Fact]
    public void TestMathEs6Methods()
    {
        var math = JavaScriptMath.Instance;
        
        // trunc
        Assert.Equal(3, math.trunc(3.14));
        Assert.Equal(-3, math.trunc(-3.14));
        
        // cbrt
        Assert.Equal(3, math.cbrt(27));
        
        // sign
        Assert.Equal(1, math.sign(42));
        Assert.Equal(-1, math.sign(-42));
        Assert.Equal(0, math.sign(0));
        
        // imul
        Assert.Equal(8, math.imul(2, 4));
        
        // log2 & log10
        Assert.Equal(3, math.log2(8));
        Assert.Equal(2, math.log10(100));

        // hypot
        Assert.Equal(5, math.hypot(3, 4));
    }

    [Fact]
    public void TestJsonParseAndStringify()
    {
        var json = JavaScriptJson.Instance;
        
        // parse object
        var parsed = json.parse("{\"name\":\"Miriam\",\"count\":42}");
        Assert.NotNull(parsed);
        var dict = parsed as IDictionary<string, object?>;
        Assert.NotNull(dict);
        Assert.Equal("Miriam", dict["name"]);
        Assert.Equal(42.0, dict["count"]);

        // parse array
        var listParsed = json.parse("[1, 2, 3]");
        var list = listParsed as List<object?>;
        Assert.NotNull(list);
        Assert.Equal(3, list.Count);
        Assert.Equal(2.0, list[1]);

        // stringify
        var serialized = json.stringify(parsed);
        Assert.Contains("\"name\":\"Miriam\"", serialized);
    }

    [Fact]
    public void TestFetchMock()
    {
        // Using a data URI as fetch address to run synchronously without real network
        var dataUri = "data:text/plain;base64,SGVsbG8gV29ybGQ=";
        var response = JavaScriptStandardLibrary.Fetch(dataUri);
        Assert.True(response.ok);
        Assert.Equal("Hello World", response.text());
    }

    [Fact]
    public async Task TestSetTimeoutAndClearTimeout()
    {
        var invoked = false;
        var id = JavaScriptStandardLibrary.setTimeout(() => { invoked = true; }, 10);
        
        Assert.True(id > 0);
        await Task.Delay(50);
        Assert.True(invoked);

        var cancelInvoked = false;
        var cancelId = JavaScriptStandardLibrary.setTimeout(() => { cancelInvoked = true; }, 50);
        JavaScriptStandardLibrary.clearTimeout(cancelId);
        
        await Task.Delay(100);
        Assert.False(cancelInvoked);
    }

    [Fact]
    public async Task TestSetIntervalAndClearInterval()
    {
        var counter = 0;
        var id = JavaScriptStandardLibrary.setInterval(() => { counter++; }, 5);

        Assert.True(id > 0);
        
        var success = false;
        for (int i = 0; i < 50; i++)
        {
            if (counter >= 2)
            {
                success = true;
                break;
            }
            await Task.Delay(5);
        }
        
        Assert.True(success);
        JavaScriptStandardLibrary.clearInterval(id);
        
        var countAfterCancel = counter;
        await Task.Delay(50);
        Assert.Equal(countAfterCancel, counter);
    }

    [Fact]
    public void TestLightweightChartsStyleCodeCompilation()
    {
        var source = @"
            class Chart {
                constructor(options) {
                    this.width = options.width || 800;
                    this.height = options.height || 600;
                }
                
                resize(w, h) {
                    this.width = w;
                    this.height = h;
                    return { w: this.width, h: this.height };
                }
            }
        ";

        var classes = JavaScriptClassSourceScanner.Scan(source);
        Assert.Single(classes);
        Assert.Equal("Chart", classes[0].Name);

        var preview = JavaScriptClassCSharpPreviewGenerator.Generate(classes);
        Assert.Contains("public dynamic? width { get; set; }", preview);
        Assert.Contains("public dynamic? height { get; set; }", preview);
        Assert.Contains("public dynamic? resize(dynamic? w, dynamic? h)", preview);
    }

    [Fact]
    public void TestCSharpBackendCompilationAndExecution()
    {
        var options = new TypedJintOptions
        {
            Backend = TypedBackendKind.CSharp,
            CompilationMode = TypedCompilationMode.CompileAnnotatedFunctionsOnly,
            EnableCompilation = true,
            ThrowOnCompilationFailure = true
        };

        var engine = new TypedJintEngine(options);
        var source = @"
            /**
             * @param {number} a
             * @param {number} b
             * @returns {number}
             */
            function add(a, b) {
                return a + b;
            }
        ";

        var result = engine.Execute(source);
        
        Assert.NotEmpty(result.CompiledFunctions);
        Assert.True(result.CompiledFunctions.ContainsKey("add"));
        
        var compiledFn = result.CompiledFunctions["add"];
        Assert.IsType<GeneratedScriptCompiledFunction>(compiledFn);
        
        var invocationResult = engine.Invoke("add", 12.0, 30.0);
        Assert.Equal(42.0, Convert.ToDouble(invocationResult));
    }

    [Fact]
    public void TestStaticTypeInferenceEngine()
    {
        var options = new TypedJintOptions
        {
            Backend = TypedBackendKind.ExpressionTrees,
            CompilationMode = TypedCompilationMode.CompileSafeFunctionsOnly,
            EnableCompilation = true,
            ThrowOnCompilationFailure = true
        };

        var engine = new TypedJintEngine(options);
        var source = @"
            function sum(limit) {
                let acc = 0;
                for (let i = 0; i <= limit; i++) {
                    acc = acc + i;
                }
                return acc;
            }
        ";

        var result = engine.Execute(source);
        
        Assert.NotEmpty(result.CompiledFunctions);
        Assert.True(result.CompiledFunctions.ContainsKey("sum"));
        
        var compiledFn = result.CompiledFunctions["sum"];
        Assert.IsType<CompiledFunction>(compiledFn);
        
        var invocationResult = engine.Invoke("sum", 10.0);
        Assert.Equal(55.0, Convert.ToDouble(invocationResult));
    }

    [Fact]
    public void TestNestedClosuresCompilationAndExecution()
    {
        var options = new TypedJintOptions
        {
            Backend = TypedBackendKind.CSharp,
            CompilationMode = TypedCompilationMode.CompileSafeFunctionsOnly,
            EnableCompilation = true,
            ThrowOnCompilationFailure = true
        };

        var engine = new TypedJintEngine(options);
        var source = @"
            function createCounter() {
                let count = 0;
                return function() {
                    count++;
                    return count;
                };
            }
        ";

        var result = engine.Execute(source);
        
        Assert.NotEmpty(result.CompiledFunctions);
        Assert.True(result.CompiledFunctions.ContainsKey("createCounter"));
        
        var compiledFn = result.CompiledFunctions["createCounter"];
        Assert.IsType<GeneratedScriptCompiledFunction>(compiledFn);
        
        var counterDelegate = engine.Invoke("createCounter");
        Assert.NotNull(counterDelegate);
        Assert.IsAssignableFrom<Delegate>(counterDelegate);
        var del = (Delegate)counterDelegate;
        var r1 = del.DynamicInvoke();
        var r2 = del.DynamicInvoke();
        
        Assert.Equal(1.0, Convert.ToDouble(r1));
        Assert.Equal(2.0, Convert.ToDouble(r2));
    }

    [Fact]
    public void TestAdvancedJavaScriptNodesCompilationAndExecution()
    {
        var options = new TypedJintOptions
        {
            Backend = TypedBackendKind.CSharp,
            CompilationMode = TypedCompilationMode.CompileSafeFunctionsOnly,
            EnableCompilation = true,
            ThrowOnCompilationFailure = true
        };

        var engine = new TypedJintEngine(options);
        var source = @"
            function process(a, b) {
                let obj = { x: a, y: b };
                let msg = `Sum: ${a + b}`;
                let cat = '';
                switch (a) {
                    case 1:
                        cat = 'One';
                        break;
                    default:
                        cat = 'Other';
                        break;
                }
                
                try {
                    if (b < 0) {
                        throw new Error('negative value');
                    }
                } catch (e) {
                    msg = e;
                }
                
                return {
                    obj: obj,
                    msg: msg,
                    cat: cat
                };
            }
        ";

        var result = engine.Execute(source);
        
        Assert.NotEmpty(result.CompiledFunctions);
        Assert.True(result.CompiledFunctions.ContainsKey("process"));
        
        var r1 = engine.Invoke("process", 1.0, 5.0);
        Assert.NotNull(r1);
        
        var dict1 = Assert.IsAssignableFrom<System.Collections.Generic.IDictionary<string, object?>>(r1);
        Assert.Equal("One", dict1["cat"]);
        Assert.Equal("Sum: 6", dict1["msg"]);
        
        var innerObj = Assert.IsAssignableFrom<System.Collections.Generic.IDictionary<string, object?>>(dict1["obj"]);
        Assert.Equal(1.0, Convert.ToDouble(innerObj["x"]));
        Assert.Equal(5.0, Convert.ToDouble(innerObj["y"]));

        var r2 = engine.Invoke("process", 3.0, -2.0);
        Assert.NotNull(r2);
        var dict2 = Assert.IsAssignableFrom<System.Collections.Generic.IDictionary<string, object?>>(r2);
        Assert.Equal("Other", dict2["cat"]);
        Assert.Equal("negative value", dict2["msg"]);
    }

    [Fact]
    public void TestLogicalOperatorsAndAssignmentExpressions()
    {
        var options = new TypedJintOptions
        {
            Backend = TypedBackendKind.CSharp,
            CompilationMode = TypedCompilationMode.CompileSafeFunctionsOnly,
            EnableCompilation = true,
            ThrowOnCompilationFailure = true
        };

        var engine = new TypedJintEngine(options);
        var source = @"
            function testOps(a, b, c) {
                let x = a || 10;
                let y = b && 20;
                let z = c ?? 30;
                let val = 0;
                let res = (val = x + y);
                return {
                    x: x,
                    y: y,
                    z: z,
                    val: val,
                    res: res
                };
            }
        ";

        var result = engine.Execute(source);
        
        if (result.CompiledFunctions.Count == 0)
        {
            foreach (var diag in result.Diagnostics)
            {
                Console.WriteLine($"DIAG: {diag.Code}: {diag.Message}");
            }
        }
        Assert.NotEmpty(result.CompiledFunctions);
        Assert.True(result.CompiledFunctions.ContainsKey("testOps"));
        
        var r = engine.Invoke("testOps", 0.0, 5.0, null);
        Assert.NotNull(r);
        
        var dict = Assert.IsAssignableFrom<System.Collections.Generic.IDictionary<string, object?>>(r);
        Assert.Equal(10.0, Convert.ToDouble(dict["x"]));
        Assert.Equal(20.0, Convert.ToDouble(dict["y"]));
        Assert.Equal(30.0, Convert.ToDouble(dict["z"]));
        Assert.Equal(30.0, Convert.ToDouble(dict["val"]));
        Assert.Equal(30.0, Convert.ToDouble(dict["res"]));
    }

    [Fact]
    public void TestCSharpBackendClassCompilationAndExecution()
    {
        var options = new TypedJintOptions
        {
            Backend = TypedBackendKind.CSharp,
            CompilationMode = TypedCompilationMode.CompileSafeFunctionsOnly,
            EnableCompilation = true,
            ThrowOnCompilationFailure = true
        };

        var engine = new TypedJintEngine(options);
        var source = @"
            class Counter {
                constructor(value) {
                    this.value = value;
                }

                next() {
                    return ++this.value;
                }
            }

            /**
             * @param {Counter} counter
             */
            function getCounterValue(counter) {
                return counter.value;
            }
        ";

        var result = engine.Execute(source);
        if (result.CompiledFunctions.Count == 0)
        {
            throw new Exception("Diagnostics: " + string.Join("\n", result.Diagnostics.Select(d => $"{d.Code}: [{d.Severity}] {d.Message}")));
        }
        Assert.NotEmpty(result.CompiledFunctions);
        Assert.True(result.CompiledFunctions.ContainsKey("getCounterValue"));

        var compiledFn = (GeneratedScriptCompiledFunction)result.CompiledFunctions["getCounterValue"];
        var assembly = compiledFn.Delegate.Method.DeclaringType!.Assembly;
        
        var counterType = assembly.GetType("Counter")
            ?? assembly.GetExportedTypes().Single(type => type.Name == "Counter");
        var counter = Activator.CreateInstance(counterType, 41.0)!;
        var next = counterType.GetMethod("next")!;

        Assert.Equal(42.0, Convert.ToDouble(next.Invoke(counter, null)));
    }

    [Fact]
    public void TestRoughJsCompilationAndDrawing()
    {
        var options = new TypedJintOptions
        {
            Backend = TypedBackendKind.CSharp,
            CompilationMode = TypedCompilationMode.CompileSafeFunctionsOnly,
            EnableCompilation = true,
            ThrowOnCompilationFailure = true
        };

        var engine = new TypedJintEngine(options);
        var source = @"
            /**
             * @param {CanvasRenderingContext2D} ctx
             */
            function drawRoughRect(ctx) {
                ctx.fillStyle = 'red';
                ctx.beginPath();
                ctx.rect(10, 10, 100, 100);
                ctx.fill();
                
                ctx.strokeStyle = 'blue';
                ctx.lineWidth = 2;
                ctx.beginPath();
                ctx.moveTo(10, 10);
                ctx.lineTo(110, 110);
                ctx.stroke();
            }
        ";

        var result = engine.Execute(source);
        if (result.CompiledFunctions.Count == 0)
        {
            throw new Exception("Diagnostics: " + string.Join("\n", result.Diagnostics.Select(d => $"{d.Code}: [{d.Severity}] {d.Message}")));
        }
        Assert.NotEmpty(result.CompiledFunctions);
        Assert.True(result.CompiledFunctions.ContainsKey("drawRoughRect"));

        var canvas = new HTMLCanvasElement();
        var ctx = (CanvasRenderingContext2D)canvas.getContext("2d")!;
        var drawRoughRect = result.CompiledFunctions["drawRoughRect"];
        drawRoughRect.Invoke(ctx);

        // Verify ProGPU DrawingContext has received the rectangle and path drawing commands
        Assert.NotEmpty(ctx.DrawingContext.Commands);
        var drawRectCmd = ctx.DrawingContext.Commands.First(c => c.Type == ProGPU.Scene.RenderCommandType.DrawRect);
        Assert.Equal(new ProGPU.Scene.Rect(10f, 10f, 100f, 100f), drawRectCmd.Rect);
        
        var drawPathCmd = ctx.DrawingContext.Commands.First(c => c.Type == ProGPU.Scene.RenderCommandType.DrawPath);
        Assert.NotNull(drawPathCmd.Path);
    }

    [Fact]
    public void TestThreeJsWebGLSceneCompilation()
    {
        var options = new TypedJintOptions
        {
            Backend = TypedBackendKind.CSharp,
            CompilationMode = TypedCompilationMode.CompileSafeFunctionsOnly,
            EnableCompilation = true,
            ThrowOnCompilationFailure = true
        };

        var engine = new TypedJintEngine(options);
        var source = @"
            /**
             * @param {WebGLRenderingContext} gl
             */
            function renderScene(gl) {
                gl.viewport(0, 0, 800, 600);
                gl.clearColor(0, 0, 0, 1);
                gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);

                var vs = gl.createShader(gl.VERTEX_SHADER);
                gl.shaderSource(vs, 'void main() {}');
                gl.compileShader(vs);

                var fs = gl.createShader(gl.FRAGMENT_SHADER);
                gl.shaderSource(fs, 'void main() {}');
                gl.compileShader(fs);

                var prog = gl.createProgram();
                gl.attachShader(prog, vs);
                gl.attachShader(prog, fs);
                gl.linkProgram(prog);
                gl.useProgram(prog);

                var buf = gl.createBuffer();
                gl.bindBuffer(gl.ARRAY_BUFFER, buf);
                var vertices = [0, 0, 0, 1, 0, 0, 0, 1, 0];
                gl.bufferData(gl.ARRAY_BUFFER, vertices, gl.STATIC_DRAW);

                gl.enableVertexAttribArray(0);
                gl.vertexAttribPointer(0, 3, gl.FLOAT, false, 0, 0);
                gl.drawArrays(gl.TRIANGLES, 0, 3);
            }
        ";

        var result = engine.Execute(source);
        Assert.NotEmpty(result.CompiledFunctions);
        Assert.True(result.CompiledFunctions.ContainsKey("renderScene"));

        var canvas = new HTMLCanvasElement();
        var gl = (WebGLRenderingContext)canvas.getContext("webgl")!;
        var renderScene = result.CompiledFunctions["renderScene"];
        renderScene.Invoke(gl);
    }

    [Fact]
    public void TestLightweightChartsCompilationAndExecution()
    {
        var options = new TypedJintOptions
        {
            Backend = TypedBackendKind.CSharp,
            CompilationMode = TypedCompilationMode.CompileSafeFunctionsOnly,
            EnableCompilation = true,
            ThrowOnCompilationFailure = true
        };

        var engine = new TypedJintEngine(options);
        var source = @"
            /**
             * @param {HTMLCanvasElement} canvas
             * @param {object[]} data
             */
            function renderChart(canvas, data) {
                var ctx = canvas.getContext('2d');
                ctx.strokeStyle = 'green';
                ctx.lineWidth = 3;
                ctx.beginPath();
                for (var i = 0; i < data.length; i++) {
                    var point = data[i];
                    if (i === 0) {
                        ctx.moveTo(point.x, point.y);
                    } else {
                        ctx.lineTo(point.x, point.y);
                    }
                }
                ctx.stroke();
            }
        ";

        var result = engine.Execute(source);
        if (result.CompiledFunctions.Count == 0)
        {
            throw new Exception("Diagnostics: " + string.Join("\n", result.Diagnostics.Select(d => $"{d.Code}: [{d.Severity}] {d.Message}")));
        }
        Assert.NotEmpty(result.CompiledFunctions);
        Assert.True(result.CompiledFunctions.ContainsKey("renderChart"));

        var canvas = new HTMLCanvasElement();
        var data = new object[]
        {
            new Dictionary<string, object> { { "x", 10.0 }, { "y", 20.0 } },
            new Dictionary<string, object> { { "x", 30.0 }, { "y", 40.0 } }
        };

        var renderChart = result.CompiledFunctions["renderChart"];
        renderChart.Invoke(canvas, data);

        var ctx = (CanvasRenderingContext2D)canvas.getContext("2d")!;
        Assert.NotEmpty(ctx.DrawingContext.Commands);
        var drawPathCmd = ctx.DrawingContext.Commands.First(c => c.Type == ProGPU.Scene.RenderCommandType.DrawPath);
        Assert.NotNull(drawPathCmd.Path);
    }
}
