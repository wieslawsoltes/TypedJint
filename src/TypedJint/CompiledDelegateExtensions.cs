namespace TypedJint;

public static class CompiledDelegateExtensions
{
    public static TDelegate GetDelegate<TDelegate>(this TypedCompilationResult result, string functionName)
        where TDelegate : Delegate
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(functionName);

        if (!result.CompiledFunctions.TryGetValue(functionName, out var function))
        {
            throw new KeyNotFoundException($"Compiled function '{functionName}' was not found.");
        }

        if (function.Delegate is not TDelegate typed)
        {
            throw new InvalidCastException(
                $"Compiled function '{functionName}' has delegate type '{function.Delegate.GetType().FullName}', not '{typeof(TDelegate).FullName}'.");
        }

        return typed;
    }

    public static bool TryGetDelegate<TDelegate>(this TypedCompilationResult result, string functionName, out TDelegate? @delegate)
        where TDelegate : Delegate
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(functionName);

        if (result.CompiledFunctions.TryGetValue(functionName, out var function) && function.Delegate is TDelegate typed)
        {
            @delegate = typed;
            return true;
        }

        @delegate = null;
        return false;
    }

    public static object? InvokeCompiled(this TypedCompilationResult result, string functionName, params object?[] arguments)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(functionName);

        if (!result.CompiledFunctions.TryGetValue(functionName, out var function))
        {
            throw new KeyNotFoundException($"Compiled function '{functionName}' was not found.");
        }

        return function.Invoke(arguments);
    }
}
