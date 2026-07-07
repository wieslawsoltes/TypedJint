# TypedJint Architecture Specification

## 1. Vision

TypedJint is a compiler/runtime layer for JavaScript hosted entirely in .NET. It is designed for applications that need JavaScript-like scripting but also need high-performance typed execution, safe .NET interop, DOM-style object access, headless testing, and future HTMLML/SvgML integration.

TypedJint does **not** try to replace Jint. Jint remains the semantic authority and compatibility fallback. TypedJint adds a typed fast path around it.

```text
JavaScript source
    ↓
Esprima validation / parsing
    ↓
JSDoc type metadata
    ↓
Typed function extraction
    ↓
Symbol binding
    ↓
Typed expression/statement model
    ↓
Safety analysis
    ↓
Expression Tree backend
    ↓
Compiled .NET delegate cache

Fallback path:
Unsupported JavaScript → Jint
```

## 2. Core Principle

TypedJint uses a dual execution model:

```text
Statically safe typed JavaScript → compiled .NET delegate
Dynamic JavaScript              → Jint interpreter
```

This gives compatibility from day one while allowing selected hot functions to compile into direct CLR calls.

## 3. Runtime Components

```text
TypedJintEngine
    ├── Jint.Engine fallback runtime
    ├── TypedJsCompiler
    ├── Compiled function registry
    ├── Host object registry
    ├── DOM document/window globals
    ├── Event loop
    └── Optional renderer adapter
```

## 4. Project Layout

```text
/src/TypedJint
    TypedJint.csproj
    TypedJint.cs

/samples/TypedJint.Sample
    TypedJint.Sample.csproj
    Program.cs

/tests/TypedJint.Tests
    TypedJint.Tests.csproj
    TypedJintTests.cs

/docs/architecture
    TypedJint.md
```

The prototype is intentionally compact. As the implementation grows, split it into:

```text
TypedJint.Abstractions
TypedJint.Compiler
TypedJint.Runtime
TypedJint.Dom
TypedJint.HtmlML
TypedJint.Backend.ExpressionTrees
TypedJint.Backend.CSharp
TypedJint.Backend.IL
TypedJint.Tests
```

## 5. Type Sources

TypedJint receives static types from:

1. JSDoc annotations
2. registered host objects
3. built-in DOM type registry
4. literal inference
5. local variable inference

Example:

```js
/**
 * @param {number} a
 * @param {number} b
 * @returns {number}
 */
function add(a, b) {
    return a + b;
}
```

The compiler sees:

```text
add(double a, double b): double
```

## 6. Supported Phase-1 JavaScript Subset

Phase 1 compiles only functions that are safe and statically typed.

Supported:

```text
function declarations
JSDoc @param and @returns
number/string/boolean/void
let/const/var locals
return statements
expression statements
property assignment
binary operators
member access
method calls
host object calls
DOM object calls
```

Unsupported by the compiler, but supported through Jint fallback:

```text
eval
with
Proxy
prototype mutation
this rebinding
try/catch
async/await
generators
classes
closures
spread/rest
destructuring
computed dynamic property access
```

## 7. DOM and HTMLML Direction

TypedJint includes a native .NET DOM model so JavaScript can manipulate UI-like trees without depending on a browser.

```text
JavaScript
    ↓
TypedJint compiler/Jint fallback
    ↓
.NET DOM object model
    ↓
Renderer adapter
    ↓
Avalonia / Skia / headless / future HTMLML
```

The DOM layer exposes:

```text
Document
Node
Element
HTMLElement
HTMLButtonElement
TextNode
DocumentFragment
DomTokenList
CssStyleDeclaration
EventTarget
Event
CustomEvent
MouseEvent
KeyboardEvent
```

## 8. DOM Interop

DOM interop is direct CLR interop when compiled.

JavaScript:

```js
/**
 * @param {HTMLButtonElement} button
 * @returns {void}
 */
function setup(button) {
    button.textContent = "Ready";
    button.classList.add("primary");
}
```

Compiled equivalent:

```csharp
button.TextContent = "Ready";
button.ClassList.Add("primary");
```

This avoids dynamic property lookup in the fast path.

## 9. DOM Events

The event model mirrors browser-style `EventTarget`.

```js
button.addEventListener("click", function (e) {
    button.textContent = "Clicked";
});
```

In the prototype, event listeners can be registered and dispatched in the native DOM. Compiled listener functions are a future extension.

## 10. Renderer Adapter

DOM mutation should not depend directly on Avalonia.

Future renderer abstraction:

```csharp
public interface IRenderAdapter
{
    void Attach(Document document);
    void OnNodeInserted(Node parent, Node child, int index);
    void OnNodeRemoved(Node parent, Node child);
    void OnAttributeChanged(Element element, string name, string? oldValue, string? newValue);
    void OnTextChanged(Node node, string? oldText, string? newText);
    void OnStyleChanged(Element element, string propertyName, string? value);
}
```

Planned adapters:

```text
HeadlessDomRenderer
AvaloniaDomRenderer
SkiaDomRenderer
SvgML adapter
HTMLML component adapter
```

## 11. Execution Modes

### Compatibility Mode

Jint executes source. TypedJint compiles safe functions for external invocation only.

### DOM Accelerated Mode

Typed DOM calls are compiled into direct CLR calls. Unsupported functions remain in Jint.

### Headless Automation Mode

No renderer is required. DOM + events + scripts run fully in memory.

### Future Pure Compiled Mode

Unsupported constructs become errors. Useful for trusted scripts and AOT.

## 12. Compiler Backend Strategy

The first backend is Expression Trees:

```text
Typed JS function → expression tree → compiled delegate
```

Later backends:

```text
C# backend → Roslyn → DLL/PDB
IL backend → DynamicMethod/AssemblyBuilder
AOT backend → source-generated C#
```

## 13. Host Interop

Host objects are registered with the engine and exposed both to Jint and to the typed compiler.

```csharp
engine.RegisterHostObject("app", new AppHost());
```

JavaScript:

```js
/**
 * @returns {number}
 */
function count() {
    return app.countOpenDocuments();
}
```

TypedJint resolves `app.countOpenDocuments()` to a CLR method call when the host object is known.

## 14. Diagnostics

Diagnostics should report:

```text
compiled functions
fallback functions
missing JSDoc
unsupported syntax
unknown identifiers
unknown types
ambiguous host calls
invalid member access
```

The prototype exposes fallback reasons through `TypedCompilationResult`.

## 15. Roadmap

### Milestone 1

- JSDoc function typing
- expression tree compilation
- Jint fallback
- primitive expressions
- property assignment
- method calls
- native DOM model
- sample app
- tests

### Milestone 2

- `if`/`else`
- loops
- richer parser model
- typed arrays
- nullability and union types
- flow narrowing

### Milestone 3

- host overload resolution
- custom DOM elements
- renderer adapter
- HTMLML parser integration
- SvgML integration

### Milestone 4

- C# backend
- Roslyn compilation
- source-generated bindings
- delegate/assembly cache

### Milestone 5

- IL backend
- async/Promise bridge
- compiled event listeners
- profiler-guided tiered compilation

## 16. Summary

TypedJint should be treated as four cooperating systems:

```text
Jint runtime      = correctness and compatibility
Typed compiler   = safe fast path
DOM/HTMLML layer = scriptable .NET object tree
Renderer layer   = Avalonia/Skia/headless projection
```

The decisive architectural choice is fallback. TypedJint becomes useful immediately because unsupported JavaScript still runs in Jint while typed code progressively accelerates into direct .NET execution.
