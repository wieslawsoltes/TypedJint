# Workspace Rules: TypedJint Compiler Design Guidelines

This project implements a pure static transpiler compiling JavaScript to native C# assemblies. To preserve extensible design, extreme performance, and correctness, all code contributions must satisfy the following architectural rules:

## 1. Compiler Architecture and Phases
- Keep compiler phases decoupled and clean:
  - **Parsing**: Done strictly via Acornima standard-compliant AST parsing (no ad-hoc regex scanners or string search hacks).
  - **Transpilation / IR**: Walk standard AST nodes. The `EmitExpression` and `EmitStatement` pipeline in the C# generator or Expression Tree builder must handle translation statically.
  - **Target Backends**: Retain the decoupled design supporting `ExpressionTrees` (dynamic expression tree compilation) and `CSharp` (Roslyn-based pure C# assembly compilation).
  - **IL Backend**: The `TypedBackendKind.IL` backend is routed through the expression tree compiler which compiles dynamically to C# MSIL.

## 2. Invocation and Run-time Performance
- **Avoid DynamicInvoke**: Never call `Delegate.DynamicInvoke(object[])` on hot paths. It incurs excessive reflection costs (up to 100x slower than direct invocations).
- **Compile Invoker Lambdas**: When invoking custom-signature compiled functions dynamically (which only accept `object?[]`), compile a parameter-casting, type-specialized lambda wrapper using Expression Trees (see `CompiledFunction.CreateInvoker`).
- **Signature Extraction**: When inspecting delegate parameter signatures, read the delegate's `Invoke` method signature (`delegate.GetType().GetMethod("Invoke")`), not `delegate.Method` (which physically contains closure target objects like `Closure` as the first argument).

## 3. Extensibility & Robustness
- **Extending Stdlib**: Global object helpers (like `fetch`, `JSON`, `Math`, and timers) must be registered globally and transpiled statically to their C# equivalents inside `TypedJintTranspiler`.
