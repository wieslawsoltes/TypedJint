# TypedJint

TypedJint is a pure-.NET typed execution layer around [Jint](https://github.com/sebastienros/jint).

It keeps Jint as the JavaScript correctness and fallback runtime, then adds:

- JSDoc-based type annotations
- safe-function compilation to .NET delegates through expression trees
- verified compiler output: semantic signature, delegate signature, normalized IR, and diagnostics
- optional runtime equivalence checks against Jint for pure functions
- direct typed CLR/DOM interop
- a small native .NET DOM, event, and HTMLML-ready object model
- transparent fallback to Jint for unsupported JavaScript

## Current implementation

The implementation supports a deliberately strict typed subset:

- annotated `function` declarations
- primitive parameters: `number`, `string`, `boolean`, `void`
- DOM parameters: `Document`, `Element`, `HTMLElement`, `HTMLButtonElement`, `TextNode`, `DomElement`
- `let` / `const` / `var`
- `return`
- expression statements
- assignment to CLR/DOM properties
- numeric/string/boolean binary expressions
- member access
- method calls on typed CLR/DOM objects
- direct DOM calls such as `document.createElement`, `appendChild`, `classList.add`, `dispatchEvent`
- Jint fallback for dynamic/unsupported functions

## Verified execution

```csharp
using TypedJint;

var engine = new TypedJintEngine();

var source = """
/**
 * @param {number} a
 * @param {number} b
 * @returns {number}
 */
function add(a, b) {
    let c = a + b;
    return c;
}
""";

var verified = engine.ExecuteVerified(
    source,
    new Dictionary<string, object?[][]>
    {
        ["add"] = new[]
        {
            new object?[] { 10.0, 20.0 },
            new object?[] { -2.0, 2.0 }
        }
    });

verified.ThrowIfUnverified();

Console.WriteLine(verified.CompilerOutputs["add"].SemanticSignature);
Console.WriteLine(verified.CompilerOutputs["add"].DelegateSignature);
Console.WriteLine(verified.CompilerOutputs["add"].NormalizedIr);
```

The verification layer checks:

- every compiled function exists in parsed source
- compiled delegate parameter count matches the parsed function
- compiled delegate parameter CLR types match JSDoc types
- compiled delegate return type matches JSDoc return type
- normalized IR is generated deterministically
- optional runtime cases produce equivalent compiled and Jint results

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
