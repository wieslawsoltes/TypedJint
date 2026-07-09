using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TypedJint.Runtime;
using Jint.Native;

namespace TypedJint;

public static class JavaScriptStandardLibraryExtensions
{
    public static TypedJintEngine RegisterStandardLibrary(this TypedJintEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        engine.SetValue("Math", JavaScriptMath.Instance);
        engine.SetValue("console", JavaScriptConsole.Instance);
        engine.SetValue("net", JavaScriptNetwork.Instance);
        engine.SetValue("encoding", JavaScriptEncoding.Instance);
        engine.SetValue("json", JavaScriptJson.Instance);
        engine.SetValue("JSON", JavaScriptJson.Instance);
        engine.SetValue("time", JavaScriptTime.Instance);
        engine.SetValue("navigator", new JsNavigator());
        engine.SetValue("performance", new JsPerformance());

        // Register DOM/Canvas type constructors
        engine.SetValue("HTMLCanvasElement", typeof(HTMLCanvasElement));
        engine.SetValue("CanvasRenderingContext2D", typeof(CanvasRenderingContext2D));
        engine.SetValue("WebGLRenderingContext", typeof(WebGLRenderingContext));
        engine.SetValue("WebGLShader", typeof(WebGLShader));
        engine.SetValue("WebGLProgram", typeof(WebGLProgram));
        engine.SetValue("WebGLBuffer", typeof(WebGLBuffer));
        engine.SetValue("WebGLTexture", typeof(WebGLTexture));
        engine.SetValue("WebGLUniformLocation", typeof(WebGLUniformLocation));
        engine.SetValue("WebGLActiveInfo", typeof(WebGLActiveInfo));
        engine.SetValue("TextMetrics", typeof(TextMetrics));
        engine.SetValue("CanvasGradient", typeof(CanvasGradient));
        engine.SetValue("CanvasPattern", typeof(CanvasPattern));
        engine.SetValue("HTMLImageElement", typeof(HTMLImageElement));
        
        // Register Global Functions
        engine.SetValue("fetch", new Func<string, JavaScriptResponse>(JavaScriptStandardLibrary.Fetch));
        engine.SetValue("setTimeout", new Func<Action, double, double>(JavaScriptStandardLibrary.setTimeout));
        engine.SetValue("clearTimeout", new Action<double>(JavaScriptStandardLibrary.clearTimeout));
        engine.SetValue("setInterval", new Func<Action, double, double>(JavaScriptStandardLibrary.setInterval));
        engine.SetValue("clearInterval", new Action<double>(JavaScriptStandardLibrary.clearInterval));
        engine.SetValue("requestAnimationFrame", new Func<object, double>(JavaScriptStandardLibrary.requestAnimationFrame));
        engine.SetValue("cancelAnimationFrame", new Action<double>(JavaScriptStandardLibrary.cancelAnimationFrame));
        engine.SetValue("addEventListener", new Action<string, object, object?>(JavaScriptStandardLibrary.addEventListener));
        engine.SetValue("removeEventListener", new Action<string, object, object?>(JavaScriptStandardLibrary.removeEventListener));

        return engine;
    }

    public static JavaScriptRuntimeEngine RegisterStandardLibrary(this JavaScriptRuntimeEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        engine.SetValue("console", JavaScriptConsole.Instance);
        engine.SetValue("net", JavaScriptNetwork.Instance);
        engine.SetValue("encoding", JavaScriptEncoding.Instance);
        engine.SetValue("json", JavaScriptJson.Instance);
        engine.SetValue("JSON", JavaScriptJson.Instance);
        engine.SetValue("time", JavaScriptTime.Instance);
        engine.SetValue("navigator", new JsNavigator());
        engine.SetValue("performance", new JsPerformance());

        // Register DOM/Canvas type constructors
        engine.SetValue("HTMLCanvasElement", typeof(HTMLCanvasElement));
        engine.SetValue("CanvasRenderingContext2D", typeof(CanvasRenderingContext2D));
        engine.SetValue("WebGLRenderingContext", typeof(WebGLRenderingContext));
        engine.SetValue("WebGLShader", typeof(WebGLShader));
        engine.SetValue("WebGLProgram", typeof(WebGLProgram));
        engine.SetValue("WebGLBuffer", typeof(WebGLBuffer));
        engine.SetValue("WebGLTexture", typeof(WebGLTexture));
        engine.SetValue("WebGLUniformLocation", typeof(WebGLUniformLocation));
        engine.SetValue("WebGLActiveInfo", typeof(WebGLActiveInfo));
        engine.SetValue("TextMetrics", typeof(TextMetrics));
        engine.SetValue("CanvasGradient", typeof(CanvasGradient));
        engine.SetValue("CanvasPattern", typeof(CanvasPattern));
        engine.SetValue("HTMLImageElement", typeof(HTMLImageElement));

        // Register Global Functions
        engine.SetValue("fetch", new Func<string, JavaScriptResponse>(JavaScriptStandardLibrary.Fetch));
        engine.SetValue("setTimeout", new Func<Action, double, double>(JavaScriptStandardLibrary.setTimeout));
        engine.SetValue("clearTimeout", new Action<double>(JavaScriptStandardLibrary.clearTimeout));
        engine.SetValue("setInterval", new Func<Action, double, double>(JavaScriptStandardLibrary.setInterval));
        engine.SetValue("clearInterval", new Action<double>(JavaScriptStandardLibrary.clearInterval));
        engine.SetValue("requestAnimationFrame", new Func<object, double>(JavaScriptStandardLibrary.requestAnimationFrame));
        engine.SetValue("cancelAnimationFrame", new Action<double>(JavaScriptStandardLibrary.cancelAnimationFrame));
        engine.SetValue("addEventListener", new Action<string, object, object?>(JavaScriptStandardLibrary.addEventListener));
        engine.SetValue("removeEventListener", new Action<string, object, object?>(JavaScriptStandardLibrary.removeEventListener));

        // Load external JS libraries
        try
        {
            var sharedDir = LibraryDownloader.GetSharedDirectory();
            if (Directory.Exists(sharedDir))
            {
                foreach (var file in Directory.GetFiles(sharedDir, "*.js"))
                {
                    try
                    {
                        var code = File.ReadAllText(file);
                        engine.Jint.Execute(code);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"ERROR: Failed to load external JS library file '{Path.GetFileName(file)}': {ex.Message}");
                    }
                }

                // If LightweightCharts is registered on window but not globally, copy it
                try
                {
                    engine.Jint.Execute("if (typeof LightweightCharts === 'undefined' && typeof window !== 'undefined' && window.LightweightCharts) { LightweightCharts = window.LightweightCharts; }");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"ERROR: Failed to map window.LightweightCharts to global: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR: Failed to load external JS libraries: {ex.Message}");
        }

        return engine;
    }
}

public static class JavaScriptStandardLibrary
{
    private static readonly HttpClient Http = new();
    private static int _nextTimerId = 1;
    private static readonly ConcurrentDictionary<int, CancellationTokenSource> ActiveTimers = new();

    public static void addEventListener(string type, object listener, object? options)
    {
        JavaScriptRuntimeEngine.CurrentWindow.addEventListener(type, listener, options);
    }

    public static void removeEventListener(string type, object listener, object? options)
    {
        JavaScriptRuntimeEngine.CurrentWindow.removeEventListener(type, listener, options);
    }

    public static void ClearAllTimers()
    {
        foreach (var cts in ActiveTimers.Values)
        {
            try { cts.Cancel(); } catch {}
        }
        ActiveTimers.Clear();
    }

    public static JavaScriptResponse Fetch(string url)
    {
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = url.IndexOf(',', StringComparison.Ordinal);
            if (comma < 0)
            {
                throw new FormatException("Invalid data URI.");
            }

            var metadata = url[5..comma];
            var data = url[(comma + 1)..];
            var contentStr = metadata.Contains(";base64", StringComparison.OrdinalIgnoreCase)
                ? Encoding.UTF8.GetString(Convert.FromBase64String(data))
                : WebUtility.UrlDecode(data);

            var mockResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(contentStr)
            };
            return new JavaScriptResponse(mockResponse);
        }

        var response = Http.GetAsync(url).GetAwaiter().GetResult();
        return new JavaScriptResponse(response);
    }

    public static double setTimeout(Action callback, double delay)
    {
        var id = Interlocked.Increment(ref _nextTimerId);
        var cts = new CancellationTokenSource();
        ActiveTimers[id] = cts;

        Task.Delay((int)delay, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
            {
                try
                {
                    callback();
                }
                catch
                {
                    // ignore callback exceptions
                }
            }
            ActiveTimers.TryRemove(id, out _);
        }, TaskScheduler.Default);

        return id;
    }

    public static double setTimeout(object callback, double delay)
    {
        if (callback is Action act) return setTimeout(act, delay);
        return setTimeout(new Action(() =>
        {
            try
            {
                ((dynamic)callback)();
            }
            catch
            {
                if (callback is Jint.Native.JsValue jsVal)
                {
                    var runtime = JavaScriptRuntimeEngine.CurrentEngine;
                    try { runtime?.Jint.Invoke(jsVal); } catch {}
                }
            }
        }), delay);
    }

    public static void clearTimeout(double id)
    {
        if (ActiveTimers.TryRemove((int)id, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    public static double setInterval(Action callback, double delay)
    {
        var id = Interlocked.Increment(ref _nextTimerId);
        var cts = new CancellationTokenSource();
        ActiveTimers[id] = cts;

        Task.Run(async () =>
        {
            var token = cts.Token;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay((int)delay, token);
                    if (!token.IsCancellationRequested)
                    {
                        callback();
                    }
                }
                catch
                {
                    break;
                }
            }
            ActiveTimers.TryRemove(id, out _);
        });

        return id;
    }

    public static double setInterval(object callback, double delay)
    {
        if (callback is Action act) return setInterval(act, delay);
        return setInterval(new Action(() =>
        {
            try
            {
                ((dynamic)callback)();
            }
            catch
            {
                if (callback is Jint.Native.JsValue jsVal)
                {
                    var runtime = JavaScriptRuntimeEngine.CurrentEngine;
                    try { runtime?.Jint.Invoke(jsVal); } catch {}
                }
            }
        }), delay);
    }

    public static void clearInterval(double id) => clearTimeout(id);

    private static int _nextRafId = 1;
    private static readonly ConcurrentDictionary<int, CancellationTokenSource> RafCts = new();

    public static double requestAnimationFrame(object callback)
    {
        var id = Interlocked.Increment(ref _nextRafId);
        Console.WriteLine($"[requestAnimationFrame] ID: {id}, Callback type: {callback?.GetType().FullName}");
        var cts = new CancellationTokenSource();
        RafCts[id] = cts;

        var token = cts.Token;
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(16, token);
                if (!token.IsCancellationRequested)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (token.IsCancellationRequested) return;

                        var now = (DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds;
                        if (callback is Action<double> actionDouble) actionDouble(now);
                        else if (callback is Action action) action();
                        else if (callback is Delegate del)
                        {
                            try
                            {
                                var method = del.Method;
                                var parameters = method.GetParameters();
                                if (parameters.Length == 1 && (parameters[0].ParameterType == typeof(double) || parameters[0].ParameterType == typeof(float)))
                                {
                                    del.DynamicInvoke(now);
                                }
                                else if (parameters.Length == 0)
                                {
                                    del.DynamicInvoke();
                                }
                                else if (parameters.Length == 2 && parameters[0].ParameterType == typeof(JsValue) && parameters[1].ParameterType == typeof(JsValue[]))
                                {
                                    var runtime = JavaScriptRuntimeEngine.CurrentEngine;
                                    var jintEngine = runtime?.Jint;
                                    var thisVal = jintEngine != null ? JsValue.FromObject(jintEngine, runtime) : JsValue.Undefined;
                                    var argsVal = jintEngine != null ? new JsValue[] { JsValue.FromObject(jintEngine, now) } : Array.Empty<JsValue>();
                                    del.DynamicInvoke(thisVal, argsVal);
                                }
                                else
                                {
                                    try { del.DynamicInvoke(now); }
                                    catch { del.DynamicInvoke(); }
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[requestAnimationFrame ERROR invoking delegate]: {ex}");
                            }
                        }
                        else if (callback is Jint.Native.JsValue jsVal)
                        {
                            var runtime = JavaScriptRuntimeEngine.CurrentEngine;
                            if (runtime != null)
                            {
                                try
                                {
                                    runtime.Jint.Invoke(jsVal, Jint.Native.JsValue.FromObject(runtime.Jint, now));
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[requestAnimationFrame ERROR]: {ex}");
                                }
                            }
                        }
                    });
                }
            }
            catch {}
            RafCts.TryRemove(id, out _);
        });

        return id;
    }

    public static void cancelAnimationFrame(double id)
    {
        if (RafCts.TryRemove((int)id, out var cts))
        {
            try { cts.Cancel(); } catch {}
        }
    }

    public static void ClearAllRafs()
    {
        foreach (var cts in RafCts.Values)
        {
            try { cts.Cancel(); } catch {}
        }
        RafCts.Clear();
    }
}

