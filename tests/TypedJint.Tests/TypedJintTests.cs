using Xunit;
using TypedJint.Runtime;

namespace TypedJint.Tests;

public sealed class TypedJintTests
{
    [Fact]
    public void CompilesAnnotatedNumericFunction()
    {
        var engine = new TypedJintEngine();

        var result = engine.Execute("""
        /**
         * @param {number} a
         * @param {number} b
         * @returns {number}
         */
        function add(a, b) {
            let c = a + b;
            return c;
        }
        """);

        Assert.True(result.CompiledFunctions.ContainsKey("add"));
        Assert.Equal(30.0, engine.Invoke("add", 10.0, 20.0));
    }

    [Fact]
    public void FallsBackToJintForUnannotatedFunction()
    {
        var engine = new TypedJintEngine();

        var result = engine.Execute("""
        function add(a, b) {
            return a + b;
        }
        """);

        Assert.True(result.Fallbacks.ContainsKey("add"));
        Assert.Equal(30.0, Convert.ToDouble(engine.Invoke("add", 10, 20)));
    }

    [Fact]
    public void CompilesDomPropertyAndMethodInterop()
    {
        var engine = new TypedJintEngine();
        var button = engine.Document.createElement("button");

        var result = engine.Execute("""
        /**
         * @param {DomElement} button
         * @returns {void}
         */
        function setup(button) {
            button.textContent = "Ready";
            button.classList.add("primary");
            button.style.backgroundColor = "red";
        }
        """);

        Assert.True(result.CompiledFunctions.ContainsKey("setup"));

        engine.Invoke("setup", button);

        Assert.Equal("Ready", button.textContent);
        Assert.True(button.classList.contains("primary"));
        Assert.Equal("red", button.style.backgroundColor);
    }

    [Fact]
    public void DispatchesNativeDomEvents()
    {
        var button = new DomElement("button");
        var clicked = false;

        button.addEventListener("click", new Action<DomEvent>(_ => clicked = true));
        var result = button.dispatchEvent(new DomEvent("click"));

        Assert.True(result);
        Assert.True(clicked);
    }

    [Fact]
    public void SupportsQuerySelectorForIdClassAndTag()
    {
        var document = new DomDocument();
        var panel = document.createElement("div");
        panel.id = "root";
        panel.classList.add("panel");
        document.body.appendChild(panel);

        Assert.Same(panel, document.querySelector("#root"));
        Assert.Same(panel, document.querySelector(".panel"));
        Assert.Same(panel, document.querySelector("div"));
    }
}
