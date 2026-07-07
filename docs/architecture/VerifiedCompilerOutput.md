# Verified Compiler Output

TypedJint exposes a verification layer through `ExecuteVerified` and `TypedCompilerOutputVerifier`.

The verifier treats compiler output as an artifact, not just an implementation detail. For every compiled function it emits:

```text
semantic signature
compiled delegate signature
normalized IR
verification diagnostics
```

Verification checks:

```text
compiled function exists in parsed source
function has JSDoc metadata
parameter count matches
parameter CLR types match JSDoc types
return CLR type matches JSDoc return type
normalized IR is present and deterministic
optional runtime cases match Jint results
```

Example:

```csharp
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
```

A verified function produces output similar to:

```text
Semantic signature: add(number a, number b): number
Delegate signature: Double (Double a, Double b)

fn add
{
  let c = (a + b)
  return c
}
```

This is the first stage of compiler trust hardening. The next stage should move the expression-tree backend behind a typed IR so the verifier can validate AST -> bound tree -> IR -> backend output, and not only source -> delegate output.
