using System;
using System.Collections.Generic;
using TypedJint;

class Program
{
    static void Main()
    {
        Console.WriteLine("==================================================");
        Console.WriteLine("  TypedJint Hybrid WebGL/Canvas Compiler Demo");
        Console.WriteLine("==================================================");

        var options = new TypedJintOptions
        {
            Backend = TypedBackendKind.CSharp,
            CompilationMode = TypedCompilationMode.CompileSafeFunctionsOnly,
            EnableCompilation = true,
            ThrowOnCompilationFailure = true
        };

        var engine = new TypedJintEngine(options);

        // 1. RoughJS Draw Rect Example
        var roughJsSource = @"
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

        Console.WriteLine("\n[1] Compiling RoughJS-style Canvas2D drawing...");
        var roughJsResult = engine.Execute(roughJsSource);
        var canvas1 = new HTMLCanvasElement();
        var ctx = (CanvasRenderingContext2D)canvas1.getContext("2d")!;
        roughJsResult.CompiledFunctions["drawRoughRect"].Invoke(ctx);
        Console.WriteLine($"-> Success! Drew rect and path. Render commands: {ctx.DrawingContext.Commands.Count}");

        // 2. ThreeJS WebGL Scene Example
        var threeJsSource = @"
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

        Console.WriteLine("\n[2] Compiling ThreeJS-style WebGL shader scene...");
        var threeJsResult = engine.Execute(threeJsSource);
        var canvas2 = new HTMLCanvasElement();
        var gl = (WebGLRenderingContext)canvas2.getContext("webgl")!;
        threeJsResult.CompiledFunctions["renderScene"].Invoke(gl);
        Console.WriteLine("-> Success! Created shaders, bound buffers, and called drawArrays.");

        // 3. TradingView Lightweight Charts Example
        var chartSource = @"
            /**
             * @param {HTMLCanvasElement} canvas
             * @param {object[]} data
             */
            function renderChart(canvas, data) {
                var ctx = canvas.getContext('2d');
                ctx.fillStyle = 'white';
                ctx.fillRect(0, 0, 800, 600);

                ctx.beginPath();
                ctx.strokeStyle = 'green';
                ctx.lineWidth = 1.5;

                for (var i = 0; i < data.length; i++) {
                    var item = data[i];
                    if (i === 0) {
                        ctx.moveTo(item.x, item.y);
                    } else {
                        ctx.lineTo(item.x, item.y);
                    }
                }
                ctx.stroke();
            }
        ";

        Console.WriteLine("\n[3] Compiling TradingView Lightweight-Charts-style rendering...");
        var chartResult = engine.Execute(chartSource);
        var canvas3 = new HTMLCanvasElement();
        var chartData = new object[]
        {
            new Dictionary<string, object> { { "x", 10.0 }, { "y", 20.0 } },
            new Dictionary<string, object> { { "x", 30.0 }, { "y", 40.0 } },
            new Dictionary<string, object> { { "x", 50.0 }, { "y", 80.0 } }
        };
        chartResult.CompiledFunctions["renderChart"].Invoke(canvas3, chartData);
        var ctx3 = (CanvasRenderingContext2D)canvas3.getContext("2d")!;
        Console.WriteLine($"-> Success! Compiled loop, indexed dynamic array elements, and rendered chart path. Commands: {ctx3.DrawingContext.Commands.Count}");
        
        Console.WriteLine("\n==================================================");
        Console.WriteLine("  All compilation runs completed natively in C#!");
        Console.WriteLine("==================================================");
    }
}
