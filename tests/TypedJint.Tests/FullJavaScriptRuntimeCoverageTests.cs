using Xunit;

namespace TypedJint.Tests;

public sealed class JavaScriptRuntimeCoverageTests
{
    [Fact]
    public void ExecutesDynamicJavaScriptComputedPropertiesAndDelete()
    {
        var engine = new JavaScriptRuntimeEngine();

        var result = engine.Execute("""
        function dynamicJavaScript() {
            const obj = {};
            const key = "ans" + "wer";
            obj[key] = 40;
            obj.extra = 100;
            delete obj.extra;
            return obj.answer + ("extra" in obj ? 1000 : 2);
        }
        """);

        Assert.True(result.Verified);
        Assert.True(result.RuntimeFunctions.ContainsKey("dynamicJavaScript"));
        Assert.Equal(42.0, Convert.ToDouble(engine.Invoke("dynamicJavaScript")));
    }

    [Fact]
    public void ExecutesClosuresAndMutableCapturedState()
    {
        var engine = new JavaScriptRuntimeEngine();

        engine.Execute("""
        function closureSemantics() {
            function makeCounter(seed) {
                let value = seed;
                return function(delta) {
                    value = value + delta;
                    return value;
                };
            }

            const counter = makeCounter(10);
            return counter(1) + counter(2) + counter(3);
        }
        """);

        Assert.Equal(40.0, Convert.ToDouble(engine.Invoke("closureSemantics")));
    }

    [Fact]
    public void ExecutesClassesConstructorsInstanceMembersGettersAndStaticMembers()
    {
        var engine = new JavaScriptRuntimeEngine();

        var result = engine.Execute("""
        class Point {
            constructor(x, y) {
                this.x = x;
                this.y = y;
            }

            get sum() {
                return this.x + this.y;
            }

            move(dx, dy) {
                this.x += dx;
                this.y += dy;
                return this;
            }

            static origin() {
                return new Point(0, 0);
            }
        }

        function classSemantics() {
            return new Point(1, 2).move(3, 4).sum + Point.origin().sum;
        }
        """);

        Assert.Contains("Point", result.ClassDeclarations);
        Assert.Equal(10.0, Convert.ToDouble(engine.Invoke("classSemantics")));
    }

    [Fact]
    public void ExecutesArraysObjectLiteralsComputedKeysAndArrayMethods()
    {
        var engine = new JavaScriptRuntimeEngine();

        engine.Execute("""
        function arraysAndObjects() {
            const arr = [1, 2, 3];
            const obj = {
                a: 4,
                nested: { b: 5 },
                [arr[1]]: 6
            };

            return arr.map(x => x * 2)[2] + obj.a + obj.nested.b + obj[2];
        }
        """);

        Assert.Equal(21.0, Convert.ToDouble(engine.Invoke("arraysAndObjects")));
    }

    [Fact]
    public void ExecutesExceptionsTryCatchFinallyAndErrorObjects()
    {
        var engine = new JavaScriptRuntimeEngine();

        engine.Execute("""
        function exceptionSemantics() {
            let marker = 0;
            try {
                throw new Error("boom");
            } catch (error) {
                marker = error.message === "boom" ? 40 : 0;
            } finally {
                marker = marker + 2;
            }

            return marker;
        }
        """);

        Assert.Equal(42.0, Convert.ToDouble(engine.Invoke("exceptionSemantics")));
    }

    [Fact]
    public void ExecutesAsyncAwaitSyntaxAndPromiseShape()
    {
        var engine = new JavaScriptRuntimeEngine();

        engine.Execute("""
        async function asyncIncrement(value) {
            const awaited = await value;
            return awaited + 1;
        }

        function asyncAwaitShape() {
            const promise = asyncIncrement(41);
            return typeof promise.then === "function";
        }
        """);

        Assert.True(Convert.ToBoolean(engine.Invoke("asyncAwaitShape")));
    }

    [Fact]
    public void ExecutesGeneratorsAndIteratorProtocol()
    {
        var engine = new JavaScriptRuntimeEngine();

        engine.Execute("""
        function generatorSemantics() {
            function* sequence() {
                yield 1;
                yield 2;
                return 3;
            }

            const iterator = sequence();
            const a = iterator.next();
            const b = iterator.next();
            const c = iterator.next();
            return a.value + b.value + (c.done ? 39 : 0);
        }
        """);

        Assert.Equal(42.0, Convert.ToDouble(engine.Invoke("generatorSemantics")));
    }

    [Fact]
    public void ExecutesArrayObjectParameterAndRestDestructuring()
    {
        var engine = new JavaScriptRuntimeEngine();

        engine.Execute("""
        function destructuringSemantics() {
            const [a, , c] = [10, 20, 30];
            const { x, y: { z }, missing = 2 } = { x: 5, y: { z: 5 } };

            function rest(first, ...tail) {
                const [ignored, second] = tail;
                return first + second;
            }

            return a + c + x + z + missing + rest(1, 100, -11);
        }
        """);

        Assert.Equal(42.0, Convert.ToDouble(engine.Invoke("destructuringSemantics")));
    }

    [Fact]
    public void ExecutesPrototypeConstructorAndObjectCreateSemantics()
    {
        var engine = new JavaScriptRuntimeEngine();

        engine.Execute("""
        function prototypeSemantics() {
            function Animal(name) {
                this.name = name;
            }

            Animal.prototype.speak = function() {
                return this.name + "!";
            };

            const original = new Animal("typed");
            const derived = Object.create(original);
            derived.name = "jint";

            return original.speak() + " " + derived.speak();
        }
        """);

        Assert.Equal("typed! jint!", engine.Invoke("prototypeSemantics"));
    }

    [Fact]
    public void ScansRuntimeFunctionsWithoutParsingUnsupportedBodies()
    {
        var engine = new JavaScriptRuntimeEngine();

        var result = engine.Execute("""
        function wrapper() {
            class Local {
                constructor(value) {
                    this.value = value;
                }
            }

            const instance = new Local(42);
            const { value } = instance;
            return value;
        }
        """);

        Assert.True(result.RuntimeFunctions.ContainsKey("wrapper"));
        Assert.Equal(42.0, Convert.ToDouble(engine.Invoke("wrapper")));
    }
}
