# TypedJint

TypedJint is a pure-.NET typed execution layer around [Jint](https://github.com/sebastienros/jint).

It keeps Jint as the JavaScript semantic runtime, then adds:

- JSDoc-based type annotations
- safe-function compilation to .NET delegates through expression trees
- JavaScript-to-C# generation in class, top-level statement, runtime-compatible, and optimized hybrid modes
- Roslyn-based generated C# build/run support
- pure C# code generation mode (with `EmitRuntimeFallback = false`) that compiles the entire JS file directly to native C# classes
- dynamic Jint `JsValue` interop (preserving prototype lookup, constructors, member lookups, and indexers)
- strongly typed compiled delegate access for low-overhead invocation
- verified compiler output: semantic signature, delegate signature, normalized IR, C# preview, and diagnostics
- direct typed CLR/DOM interop
- a small native .NET DOM, event, and HTML/canvas-ready object model
- a visual rendering layer powered by Avalonia and ProGPU for drawing commands
- a JavaScript runtime backend for dynamic ECMAScript features that are not statically compiled

## Current implementation

The statically compiled subset supports:

- annotated `function` declarations
- primitive parameters: `number`, `string`, `boolean`, `void`
- DOM parameters: `Document`, `Element`, `HTMLElement`, `HTMLButtonElement`, `TextNode`, `DomElement`
- `let` / `const` / `var`
- `return`
- block statements
- `if` / `else`
- `while`
- `for`
- `break` / `continue`
- postfix/prefix update expressions: `++`, `--`
- expression statements
- assignment and compound assignment
- local variables and CLR/DOM properties
- numeric/string/boolean binary expressions
- `%`, `===`, `!==`
- conditional expressions: `test ? a : b`
- array literals and index access
- member access
- method calls on typed CLR/DOM objects
- .NET-backed standard library calls through `RegisterStandardLibrary()`
- direct DOM calls such as `document.createElement`, `appendChild`, `classList.add`, `dispatchEvent`

The JavaScript runtime backend covers dynamic JavaScript features through Jint semantics:

- closures
- classes
- arrays
- object literals
- computed properties
- exceptions
- async/await syntax and Promise shape
- generators
- destructuring
- prototype semantics

## Verified execution

```csharp
using TypedJint;

var engine = new TypedJintEngine().RegisterStandardLibrary();

var source = """
/**
 * @param {number} limit
 * @returns {number}
 */
function sumEven(limit) {
    let acc = 0;
    for (let i = 0; i <= limit; i++) {
        if (i % 2 === 0) {
            acc += i;
        }
    }

    return acc;
}
""";

var verified = engine.ExecuteVerified(
    source,
    new Dictionary<string, object?[][]>
    {
        ["sumEven"] = new[]
        {
            new object?[] { 0.0 },
            new object?[] { 6.0 },
            new object?[] { 10.0 }
        }
    });

verified.ThrowIfUnverified();

var sumEven = verified.Compilation.GetDelegate<Func<double, double>>("sumEven");
Console.WriteLine(sumEven(10));

Console.WriteLine(verified.CompilerOutputs["sumEven"].SemanticSignature);
Console.WriteLine(verified.CompilerOutputs["sumEven"].DelegateSignature);
Console.WriteLine(verified.CompilerOutputs["sumEven"].NormalizedIr);
Console.WriteLine(verified.CompilerOutputs["sumEven"].CSharpPreview);
```

## JavaScript to C# generation

Static class mode:

```csharp
var csharp = JavaScriptCSharpGenerator.GenerateStaticClass(source, "ScriptModule");
```

Top-level C# statements mode emits local functions and global C# statements:

```csharp
var csharp = JavaScriptCSharpGenerator.GenerateTopLevelStatements("""
/**
 * @param {number} a
 * @param {number} b
 * @returns {number}
 */
function add(a, b) {
    return a + b;
}

let value = add(10, 32);
value += 1;
""");
```

Runtime-compatible top-level mode preserves arbitrary JavaScript semantics by emitting a C# program backed by `JavaScriptRuntimeEngine`:

```csharp
var csharp = JavaScriptCSharpGenerator.GenerateRuntimeTopLevelStatements(source);
```

Optimized hybrid class mode emits native C# methods for statically safe functions and keeps a runtime fallback for the rest of the JavaScript program:

```csharp
var generated = OptimizedJavaScriptCSharpGenerator.Generate(source);
Console.WriteLine(generated.Source);
```

Pure C# generation mode (bypasses Jint fallback completely):

```csharp
var options = new OptimizedJavaScriptCSharpGenerationOptions { EmitRuntimeFallback = false };
var generated = OptimizedJavaScriptCSharpGenerator.Generate(source, options);
```

## Build and run generated C# with Roslyn

```csharp
var generated = OptimizedJavaScriptCSharpGenerator.Generate("""
/**
 * @param {number} a
 * @param {number} b
 * @returns {number}
 */
function add(a, b) {
    return a + b;
}

function answer() {
    return 42;
}
""");

var execution = GeneratedCSharpCompiler.CreateScriptInstance(generated.Source);
if (!execution.Success)
{
    Console.WriteLine(execution.Build.DiagnosticsText);
    Console.WriteLine(execution.Exception);
    return;
}

var script = (GeneratedCSharpScriptInstance)execution.Instance!;
Console.WriteLine(script.InvokeMethod("add", 10.0, 32.0));
Console.WriteLine(script.InvokeRuntime("answer"));
```

## JavaScript runtime execution

```csharp
using TypedJint;

var engine = new JavaScriptRuntimeEngine().RegisterStandardLibrary();

engine.Execute("""
class Counter {
    constructor(value) {
        this.value = value;
    }

    next() {
        return ++this.value;
    }
}

function runDynamic() {
    const counter = new Counter(40);
    const { value } = { value: counter.next() };
    return value + 1;
}
""");

Console.WriteLine(engine.Invoke("runDynamic")); // 42
```

## DOM interop example

```csharp
using TypedJint;

var engine = new TypedJintEngine().RegisterStandardLibrary();

engine.ExecuteVerified("""
/**
 * @param {DomElement} button
 * @returns {void}
 */
function setup(button) {
    button.textContent = "Ready";
    button.classList.add("primary");
    button.style.backgroundColor = "red";
}
""").ThrowIfUnverified();

var button = engine.Document.createElement("button");
engine.Invoke("setup", button);
```

## Avalonia desktop playground & verification suite

Run the desktop playground:

```bash
dotnet run --project samples/TypedJint.Playground
```

Run the automated verify, compile, and capture cycle:

```bash
dotnet run --project samples/TypedJint.Playground -- --auto-test
```

This verify cycle compiles and executes pure C# transpiled code for five templates (Math, Rough.js, Three.js, Lightweight Charts, D3.js, PixiJS) and exports layout-propagated screenshots to PNG format in the artifacts directory.

## Benchmarks

Run BenchmarkDotNet benchmarks:

```bash
dotnet run --project benchmarks/TypedJint.Benchmarks -c Release
```

The benchmark suite compares:

- `TypedJintEngine.Invoke` using the object-oriented invocation path
- `ICompiledFunction.Invoke`
- direct strongly typed compiled delegates
- baseline Jint invocation

## Build

```bash
dotnet build TypedJint.sln
```

## Test

```bash
dotnet test TypedJint.sln
```

## Architecture

See [`docs/architecture/TypedJint.md`](docs/architecture/TypedJint.md).
