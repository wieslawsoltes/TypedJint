# TypedJint

TypedJint is a pure-.NET typed execution layer around [Jint](https://github.com/sebastienros/jint).

It keeps Jint as the JavaScript correctness and fallback runtime, then adds:

- JSDoc-based type annotations
- safe-function compilation to .NET delegates through expression trees
- direct typed CLR/DOM interop
- a small native .NET DOM, event, and HTMLML-ready object model
- transparent fallback to Jint for unsupported JavaScript

## Current prototype

The first implementation supports a deliberately strict typed subset:

- annotated `function` declarations
- primitive parameters: `number`, `string`, `boolean`, `void`
- DOM parameters: `Document`, `Element`, `HTMLElement`, `HTMLButtonElement`, `TextNode`
- `let` / `const` / `var`
- `return`
- expression statements
- assignment to CLR/DOM properties
- numeric/string/boolean binary expressions
- member access
- method calls on typed CLR/DOM objects
- direct DOM calls such as `document.createElement`, `appendChild`, `classList.add`, `addEventListener`
- Jint fallback for dynamic/unsupported functions

## Example

```csharp
using TypedJint;

var engine = new TypedJintEngine()
    .RegisterDom(new Document());

engine.Execute("""
/**
 * @param {number} a
 * @param {number} b
 * @returns {number}
 */
function add(a, b) {
    let c = a + b;
    return c;
}

/**
 * @param {HTMLButtonElement} button
 * @returns {void}
 */
function setup(button) {
    button.textContent = "Ready";
    button.classList.add("primary");
}
""");

var result = engine.Invoke("add", 10.0, 20.0);

var button = new HtmlButtonElement(engine.Document);
engine.Invoke("setup", button);
```

## Build

```bash
dotnet build
```

## Architecture

See [`docs/architecture/TypedJint.md`](docs/architecture/TypedJint.md).
