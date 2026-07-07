# TypedJint

TypedJint is a pure-.NET typed execution layer around [Jint](https://github.com/sebastienros/jint).

It keeps Jint as the JavaScript semantic runtime, then adds:

- JSDoc-based type annotations
- safe-function compilation to .NET delegates through expression trees
- verified compiler output: semantic signature, delegate signature, normalized IR, C# preview, and diagnostics
- optional runtime equivalence checks against Jint for pure functions
- direct typed CLR/DOM interop
- a small native .NET DOM, event, and HTMLML-ready object model
- a full JavaScript runtime backend for dynamic ECMAScript features that are not statically compiled

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
- postfix/prefix update expressions: `++`, `--`
- expression statements
- assignment to locals and CLR/DOM properties
- numeric/string/boolean binary expressions
- `%`, `===`, `!==`
- member access
- method calls on typed CLR/DOM objects
- direct DOM calls such as `document.createElement`, `appendChild`, `classList.add`, `dispatchEvent`

The full JavaScript runtime backend covers dynamic JavaScript features through Jint semantics:

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

var engine = new TypedJintEngine();

var source = """
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

Console.WriteLine(verified.CompilerOutputs["sumEven"].SemanticSignature);
Console.WriteLine(verified.CompilerOutputs["sumEven"].DelegateSignature);
Console.WriteLine(verified.CompilerOutputs["sumEven"].NormalizedIr);
Console.WriteLine(verified.CompilerOutputs["sumEven"].CSharpPreview);
```

## Full JavaScript runtime execution

```csharp
using TypedJint;

var engine = new FullJavaScriptRuntimeEngine();

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

var engine = new TypedJintEngine();

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

## Avalonia playground

Run the desktop playground:

```bash
dotnet run --project samples/TypedJint.Playground
```

The playground shows:

- JavaScript input
- generated C# preview
- normalized IR
- compiler diagnostics
- verified runtime results
- DOM interop output

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
