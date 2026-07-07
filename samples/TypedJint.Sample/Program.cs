using TypedJint;

var engine = new TypedJintEngine(new TypedJintOptions
{
    DiagnosticSink = diagnostic => Console.WriteLine($"{diagnostic.Code}: {diagnostic.Message}")
});

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

/**
 * @param {DomElement} button
 * @returns {void}
 */
function setupButton(button) {
    button.textContent = "Ready";
    button.classList.add("primary");
    button.style.backgroundColor = "red";
}

function dynamicFallback(a, b) {
    return a + b;
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

Console.WriteLine($"compiled: {string.Join(", ", verified.Compilation.CompiledFunctions.Keys)}");
Console.WriteLine($"fallback: {string.Join(", ", verified.Compilation.Fallbacks.Keys)}");

foreach (var output in verified.CompilerOutputs.Values)
{
    Console.WriteLine(output.ToMarkdown());
}

foreach (var output in verified.RuntimeOutputs.Values)
{
    Console.WriteLine(output.ToMarkdown());
}

Console.WriteLine($"add(10, 20) = {engine.Invoke("add", 10.0, 20.0)}");
Console.WriteLine($"dynamicFallback(10, 20) = {engine.Invoke("dynamicFallback", 10, 20)}");

var button = engine.Document.createElement("button");
engine.Invoke("setupButton", button);

Console.WriteLine($"button.textContent = {button.textContent}");
Console.WriteLine($"button.classList = {button.classList}");
Console.WriteLine($"button.style.backgroundColor = {button.style.backgroundColor}");

var clicked = false;
button.addEventListener("click", new Action<DomEvent>(_ => clicked = true));
button.dispatchEvent(new DomEvent("click"));
Console.WriteLine($"button clicked = {clicked}");
