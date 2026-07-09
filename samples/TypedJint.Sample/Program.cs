using System;
using TypedJint;

class Program
{
    static void Main()
    {
        var source = @"
            function test() {
                var canvas = document.createElement('canvas');
                return canvas;
            }
        ";

        var csharp = TypedJintTranspiler.TranspileToCSharp(source);
        Console.WriteLine("=== Generated C# ===");
        Console.WriteLine(csharp);
    }
}
