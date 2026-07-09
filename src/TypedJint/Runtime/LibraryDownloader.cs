using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace TypedJint;

public static class LibraryDownloader
{
    private static readonly HttpClient Http = new();

    private static readonly Dictionary<string, string> FilesToDownload = new()
    {
        { "rough.js", "https://unpkg.com/roughjs@4.5.2/bundled/rough.js" },
        { "rough.d.ts", "https://unpkg.com/roughjs@4.5.2/bin/rough.d.ts" },
        { "three.js", "https://cdnjs.cloudflare.com/ajax/libs/three.js/r128/three.js" },
        { "three.d.ts", "https://raw.githubusercontent.com/three-types/three-ts-types/master/types/three/index.d.ts" },
        { "lightweight-charts.js", "https://unpkg.com/lightweight-charts@3.8.0/dist/lightweight-charts.standalone.production.js" },
        { "lightweight-charts.d.ts", "https://unpkg.com/lightweight-charts@3.8.0/dist/lightweight-charts.d.ts" },
        { "d3.js", "https://cdnjs.cloudflare.com/ajax/libs/d3/7.8.5/d3.js" },
        { "d3.d.ts", "https://unpkg.com/@types/d3/index.d.ts" },
        { "pixi.js", "https://cdnjs.cloudflare.com/ajax/libs/pixi.js/7.3.2/pixi.js" },
        { "pixi.d.ts", "https://unpkg.com/pixi.js@7.3.2/pixi.js.d.ts" }
    };

    private static readonly Dictionary<string, string> EmbeddedDefinitions = new()
    {
        {
            "rough.d.ts",
            """
            interface RoughCanvas {
                rectangle(x: number, y: number, width: number, height: number, options?: any): void;
                circle(x: number, y: number, diameter: number, options?: any): void;
                line(x1: number, y1: number, x2: number, y2: number, options?: any): void;
                polygon(vertices: number[][], options?: any): void;
            }

            interface Rough {
                canvas(canvas: HTMLCanvasElement, options?: any): RoughCanvas;
            }

            declare const rough: Rough;
            """
        },
        {
            "three.d.ts",
            """
            interface WebGLRenderer {
                setSize(width: number, height: number): void;
                render(scene: Scene, camera: Camera): void;
            }

            interface Scene {
                add(object: any): void;
            }

            interface Camera {
                position: any;
            }

            interface PerspectiveCamera extends Camera {
            }

            interface BoxGeometry {
            }

            interface MeshBasicMaterial {
                color: any;
            }

            interface Mesh {
                rotation: any;
            }

            interface Three {
                WebGLRenderer: { new(options?: any): WebGLRenderer };
                Scene: { new(): Scene };
                PerspectiveCamera: { new(fov: number, aspect: number, near: number, far: number): PerspectiveCamera };
                BoxGeometry: { new(width: number, height: number, depth: number): BoxGeometry };
                MeshBasicMaterial: { new(parameters?: any): MeshBasicMaterial };
                Mesh: { new(geometry: BoxGeometry, material: MeshBasicMaterial): Mesh };
            }

            declare const THREE: Three;
            """
        },
        {
            "lightweight-charts.d.ts",
            """
            interface ISeriesApi {
                setData(data: any[]): void;
            }

            interface IChartApi {
                addAreaSeries(options?: any): ISeriesApi;
                addLineSeries(options?: any): ISeriesApi;
                addCandlestickSeries(options?: any): ISeriesApi;
                timeScale(): any;
            }

            interface LightweightCharts {
                createChart(container: string | HTMLElement, options?: any): IChartApi;
            }

            declare const LightweightCharts: LightweightCharts;
            """
        },
        {
            "d3.d.ts",
            """
            interface D3Selection {
                select(selector: string): D3Selection;
                selectAll(selector: string): D3Selection;
                data(data: any[]): D3Selection;
                enter(): D3Selection;
                append(name: string): D3Selection;
                attr(name: string, value: any): D3Selection;
                style(name: string, value: any): D3Selection;
                text(value: any): D3Selection;
                on(event: string, listener: any): D3Selection;
            }

            interface D3 {
                select(selector: string | HTMLElement): D3Selection;
                selectAll(selector: string | HTMLElement): D3Selection;
                scaleLinear(): any;
                max(data: any[], accessor?: any): any;
            }

            declare const d3: D3;
            """
        },
        {
            "pixi.d.ts",
            """
            interface PixiApplication {
                view: HTMLCanvasElement;
                stage: any;
                ticker: any;
                destroy(removeView?: boolean, stageOptions?: any): void;
            }

            interface Pixi {
                Application: { new(options?: any): PixiApplication };
                Graphics: { new(): any };
                Container: { new(): any };
                Sprite: { new(): any };
            }

            declare const PIXI: Pixi;
            """
        }
    };

    public static async Task DownloadLibrariesAsync(string targetDir)
    {
        if (!Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        foreach (var kvp in FilesToDownload)
        {
            var filePath = Path.Combine(targetDir, kvp.Key);
            if (!File.Exists(filePath))
            {
                try
                {
                    var data = await Http.GetByteArrayAsync(kvp.Value);
                    await File.WriteAllBytesAsync(filePath, data);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to download {kvp.Key}: {ex.Message}");
                }
            }
        }

        foreach (var kvp in EmbeddedDefinitions)
        {
            var filePath = Path.Combine(targetDir, kvp.Key);
            if (!File.Exists(filePath))
            {
                try
                {
                    await File.WriteAllTextAsync(filePath, kvp.Value);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to write embedded types {kvp.Key}: {ex.Message}");
                }
            }
        }
    }

    public static string GetSharedDirectory()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "TypedJint.sln")))
            {
                var dir = Path.Combine(current, "samples", "shared");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return dir;
            }
            current = Path.GetDirectoryName(current);
        }
        var fallbackDir = Path.Combine(AppContext.BaseDirectory, "shared");
        if (!Directory.Exists(fallbackDir)) Directory.CreateDirectory(fallbackDir);
        return fallbackDir;
    }

    public static TypeScriptTypeRegistry LoadDefinitions(string sharedDir)
    {
        var registry = new TypeScriptTypeRegistry();
        if (!Directory.Exists(sharedDir)) return registry;

        foreach (var file in Directory.GetFiles(sharedDir, "*.d.ts"))
        {
            try
            {
                var content = File.ReadAllText(file);
                var reg = TypeScriptDefParser.Parse(content);
                registry.Merge(reg);
            }
            catch {}
        }
        return registry;
    }
}
