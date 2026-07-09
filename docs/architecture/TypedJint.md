# TypedJint Architecture Specification

## 1. Vision

TypedJint is a compiler/runtime layer for JavaScript hosted entirely in .NET. It is designed for applications that need JavaScript-like scripting but also need high-performance typed execution, safe .NET interop, DOM-style object access, headless testing, and future HTMLML/SvgML integration.

TypedJint compiles JavaScript source code directly to pure C# assemblies, enabling full static compilation, optimized execution, and zero Jint interpreter reliance at runtime.

```text
JavaScript source
    ↓
Acornima validation / parsing
    ↓
JSDoc type metadata
    ↓
Typed function extraction
    ↓
Symbol binding
    ↓
Typed expression/statement model
    ↓
C# / Expression Tree Transpiler
    ↓
Compiled .NET delegate cache / Roslyn DLL Assembly
```

## 2. Core Principle

TypedJint compiles all JavaScript statements, functions, classes, and variables directly into a native C# class (by default compiled with Roslyn at runtime):

```text
JavaScript source → C# Class Output → compiled .NET Assembly / Delegate
```

This delivers high-performance .NET execution for scripting applications.

## 3. Runtime Components

```text
TypedJintEngine
    ├── TypedJsCompiler
    ├── Compiled function registry
    ├── Host object registry
    ├── DOM Window / Document context
```

## 4. Supported JavaScript Subset

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

## 5. DOM and HTMLML Direction

TypedJint includes a native .NET DOM model so JavaScript can manipulate UI-like trees without depending on a browser.

```text
JavaScript
    ↓
TypedJint compiler
    ↓
.NET DOM object model
    ↓
Renderer adapter
    ↓
Avalonia / Skia / headless / future HTMLML
```

The DOM layer exposes:

- Document
- Node
- Element
- HTMLElement
- HTMLButtonElement
- TextNode
- DocumentFragment
- DomTokenList
- CssStyleDeclaration
- EventTarget
- Event
- CustomEvent
- MouseEvent
- KeyboardEvent
- KeyboardEvent

## 6. DOM Interop

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

## 7. DOM Events

The event model mirrors browser-style `EventTarget`.

```js
button.addEventListener("click", function (e) {
    button.textContent = "Clicked";
});
```

In the prototype, event listeners can be registered and dispatched in the native DOM.

## 8. Compiler Backend Strategy

The primary backend compiles the source directly to C# files:

```text
JS source → C# Class → Roslyn Compilation → .NET Assembly DLL
```

Alternatively, single safe functions can compile dynamically to .NET delegates using Expression Trees:

```text
Typed JS function → expression tree → compiled delegate
```

## 9. Diagnostics

Diagnostics report:

```text
compiled functions
missing JSDoc
unsupported syntax
unknown identifiers
unknown types
ambiguous host calls
invalid member access
```
