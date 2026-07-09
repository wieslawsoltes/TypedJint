using System;
using System.CommandLine;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Build.Locator;
using TypedJint;

namespace TypedJint.Cli;

public static class Program
{
    private static readonly HttpClient Http = new();

    public static async Task<int> Main(string[] args)
    {
        // 1. Initialize MSBuild Locator before any MSBuildWorkspace classes are loaded
        try
        {
            if (!MSBuildLocator.IsRegistered)
            {
                MSBuildLocator.RegisterDefaults();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WARNING: MSBuildLocator failed to initialize: {ex.Message}. MSBuild Workspace functionality may be disabled.");
        }

        return await RunCliAsync(args);
    }

    private static async Task<int> RunCliAsync(string[] args)
    {
        // Core options for Compile command
        var inputOption = new Option<string[]>(
            aliases: new[] { "--input", "-i" },
            description: "Paths or URLs to input JavaScript files or directories")
        {
            IsRequired = true,
            Arity = ArgumentArity.OneOrMore
        };

        var outputOption = new Option<string>(
            aliases: new[] { "--output", "-o" },
            description: "Output path to compiled C# file or output directory")
        {
            IsRequired = true
        };

        var definitionsOption = new Option<string>(
            aliases: new[] { "--definitions", "-d" },
            description: "Path to a directory or file containing .d.ts files to load for type resolution");

        var classNameOption = new Option<string>(
            aliases: new[] { "--class-name", "-c" },
            description: "Custom class name for the compiled script (defaults to the JS filename or 'ScriptModule')");

        var buildOption = new Option<bool>(
            aliases: new[] { "--build", "-b" },
            description: "Build the generated code using MSBuild Workspaces API and verify correctness");

        var rootCommand = new RootCommand("TypedJint JS to C# Compiler CLI Tool")
        {
            inputOption,
            outputOption,
            definitionsOption,
            classNameOption,
            buildOption
        };

        rootCommand.SetHandler(async (string[] inputs, string output, string? definitionsPath, string? className, bool build) =>
        {
            try
            {
                var tsRegistry = await LoadDefinitionsAsync(definitionsPath);
                var expandedInputs = await ExpandInputsAsync(inputs);

                if (expandedInputs.Count == 0)
                {
                    Console.Error.WriteLine("ERROR: No valid JS input files found to compile.");
                    return;
                }

                bool isOutputDirectory = expandedInputs.Count > 1 || Directory.Exists(output) || (!output.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !Path.HasExtension(output));

                if (isOutputDirectory)
                {
                    Directory.CreateDirectory(output);
                    Console.WriteLine($"Output will be written as separate files in directory: {output}");
                }

                var compiledPaths = new List<string>();

                foreach (var input in expandedInputs)
                {
                    Console.WriteLine($"Processing input: {input}");
                    string source;
                    string inputName;

                    if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"Downloading JS content from {input}...");
                        source = await Http.GetStringAsync(input);
                        
                        var uri = new Uri(input);
                        inputName = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
                        if (string.IsNullOrEmpty(inputName))
                        {
                            inputName = "DownloadedScript";
                        }
                    }
                    else
                    {
                        if (!File.Exists(input))
                        {
                            Console.Error.WriteLine($"ERROR: Input file '{input}' does not exist.");
                            continue;
                        }
                        source = await File.ReadAllTextAsync(input);
                        inputName = Path.GetFileNameWithoutExtension(input);
                    }

                    var resolvedClassName = className ?? SanitizeClassName(inputName);
                    var genOptions = new OptimizedJavaScriptCSharpGenerationOptions(
                        ClassName: resolvedClassName,
                        TypeScriptRegistry: tsRegistry
                    );

                    Console.WriteLine($"Compiling class '{resolvedClassName}'...");
                    var result = OptimizedJavaScriptCSharpGenerator.Generate(source, genOptions);

                    foreach (var diag in result.Diagnostics)
                    {
                        Console.WriteLine($"{diag.Code} {diag.Severity}: {diag.Message}");
                    }

                    string targetFilePath = isOutputDirectory
                        ? Path.Combine(output, resolvedClassName + ".cs")
                        : output;

                    await File.WriteAllTextAsync(targetFilePath, result.Source);
                    Console.WriteLine($"Successfully compiled and saved to: {targetFilePath}");
                    compiledPaths.Add(targetFilePath);
                }

                // 2. Create .csproj file if build is requested
                if (build && compiledPaths.Count > 0)
                {
                    string csprojPath = isOutputDirectory
                        ? Path.Combine(output, (SanitizeClassName(Path.GetFileName(Path.GetFullPath(output))) ?? "CompiledScripts") + ".csproj")
                        : Path.Combine(Path.GetDirectoryName(output) ?? ".", Path.GetFileNameWithoutExtension(output) + ".csproj");

                    await CreateCsprojFileAsync(csprojPath);

                    // 3. Verify build using MSBuild Workspace API
                    await VerifyBuildUsingMSBuildWorkspaceAsync(csprojPath);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"CRITICAL ERROR during execution: {ex.Message}");
                Console.Error.WriteLine(ex.StackTrace);
            }
        }, inputOption, outputOption, definitionsOption, classNameOption, buildOption);

        // Define "scan" subcommand
        var scanCommand = new Command("scan", "Scan JavaScript files/folders for compilation diagnostics and print a report");
        
        var scanInputOption = new Option<string[]>(
            aliases: new[] { "--input", "-i" },
            description: "Paths to JavaScript files or directories to scan")
        {
            IsRequired = true,
            Arity = ArgumentArity.OneOrMore
        };

        var scanDefinitionsOption = new Option<string>(
            aliases: new[] { "--definitions", "-d" },
            description: "Path to a directory or file containing .d.ts files to load for type resolution");

        scanCommand.AddOption(scanInputOption);
        scanCommand.AddOption(scanDefinitionsOption);

        scanCommand.SetHandler(async (string[] inputs, string? definitionsPath) =>
        {
            try
            {
                var tsRegistry = await LoadDefinitionsAsync(definitionsPath);
                var expandedInputs = await ExpandInputsAsync(inputs);

                if (expandedInputs.Count == 0)
                {
                    Console.Error.WriteLine("ERROR: No JavaScript files found to scan.");
                    return;
                }

                Console.WriteLine($"Starting diagnostic scan of {expandedInputs.Count} files...");
                int totalWarnings = 0;
                int totalErrors = 0;

                foreach (var input in expandedInputs)
                {
                    Console.WriteLine($"Scanning: {input}");
                    string source;
                    if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        source = await Http.GetStringAsync(input);
                    }
                    else
                    {
                        source = await File.ReadAllTextAsync(input);
                    }

                    var genOptions = new OptimizedJavaScriptCSharpGenerationOptions(
                        ClassName: "ScanTemp",
                        TypeScriptRegistry: tsRegistry
                    );

                    var result = OptimizedJavaScriptCSharpGenerator.Generate(source, genOptions);

                    if (result.Diagnostics.Count == 0)
                    {
                        Console.WriteLine($"  [PASS] Clean!");
                    }
                    else
                    {
                        foreach (var diag in result.Diagnostics)
                        {
                            var prefix = diag.Severity == TypedDiagnosticSeverity.Error ? "[ERROR]" : "[WARNING]";
                            if (diag.Severity == TypedDiagnosticSeverity.Error) totalErrors++;
                            else totalWarnings++;

                            Console.WriteLine($"  {prefix} {diag.Code}: {diag.Message}");
                        }
                    }
                }

                Console.WriteLine("\n--- Scan Summary ---");
                Console.WriteLine($"Files scanned: {expandedInputs.Count}");
                Console.WriteLine($"Total warnings: {totalWarnings}");
                Console.WriteLine($"Total errors: {totalErrors}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"CRITICAL ERROR during scan: {ex.Message}");
            }
        }, scanInputOption, scanDefinitionsOption);

        rootCommand.AddCommand(scanCommand);

        return await rootCommand.InvokeAsync(args);
    }

    private static async Task<TypeScriptTypeRegistry> LoadDefinitionsAsync(string? definitionsPath)
    {
        var tsRegistry = new TypeScriptTypeRegistry();
        if (string.IsNullOrEmpty(definitionsPath))
        {
            return tsRegistry;
        }

        if (Directory.Exists(definitionsPath))
        {
            Console.WriteLine($"Loading TypeScript definitions from directory: {definitionsPath}");
            var files = Directory.GetFiles(definitionsPath, "*.d.ts");
            foreach (var file in files)
            {
                var content = await File.ReadAllTextAsync(file);
                var registry = TypeScriptDefParser.Parse(content);
                tsRegistry.Merge(registry);
                Console.WriteLine($"Loaded definition file: {Path.GetFileName(file)}");
            }
        }
        else if (File.Exists(definitionsPath))
        {
            Console.WriteLine($"Loading TypeScript definition file: {definitionsPath}");
            var content = await File.ReadAllTextAsync(definitionsPath);
            var registry = TypeScriptDefParser.Parse(content);
            tsRegistry.Merge(registry);
        }
        else
        {
            Console.WriteLine($"WARNING: Definitions path '{definitionsPath}' does not exist.");
        }

        return tsRegistry;
    }

    private static async Task<List<string>> ExpandInputsAsync(string[] inputs)
    {
        var result = new List<string>();
        foreach (var input in inputs)
        {
            if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(input);
            }
            else if (Directory.Exists(input))
            {
                var files = Directory.GetFiles(input, "*.js", SearchOption.AllDirectories);
                result.AddRange(files);
            }
            else if (File.Exists(input))
            {
                result.Add(input);
            }
            else
            {
                Console.Error.WriteLine($"WARNING: Input '{input}' was not found as a file, folder, or URL.");
            }
        }
        return result;
    }

    private static async Task CreateCsprojFileAsync(string csprojPath)
    {
        var typedJintCsproj = FindTypedJintCsprojPath();
        
        string projectReferenceXml = "";
        string packageReferenceXml = "";

        if (!string.IsNullOrEmpty(typedJintCsproj))
        {
            projectReferenceXml = $@"
    <ProjectReference Include=""{typedJintCsproj}"" />";
        }
        else
        {
            packageReferenceXml = @"
    <PackageReference Include=""Jint"" Version=""4.10.1"" />
    <PackageReference Include=""Microsoft.CodeAnalysis.CSharp"" Version=""4.14.0"" />";
        }

        var content = $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <OutputType>Library</OutputType>
  </PropertyGroup>
  <ItemGroup>{projectReferenceXml}{packageReferenceXml}
  </ItemGroup>
</Project>
";
        await File.WriteAllTextAsync(csprojPath, content);
        Console.WriteLine($"Generated project file: {csprojPath}");
    }

    private static string? FindTypedJintCsprojPath()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            var testPath = Path.Combine(current, "src", "TypedJint", "TypedJint.csproj");
            if (File.Exists(testPath))
            {
                return Path.GetFullPath(testPath);
            }
            var testPath2 = Path.Combine(current, "TypedJint", "TypedJint.csproj");
            if (File.Exists(testPath2))
            {
                return Path.GetFullPath(testPath2);
            }
            current = Path.GetDirectoryName(current);
        }
        return null;
    }

    private static async Task<bool> VerifyBuildUsingMSBuildWorkspaceAsync(string csprojPath)
    {
        Console.WriteLine($"[MSBuildWorkspace] Loading project '{csprojPath}'...");
        try
        {
            using var workspace = MSBuildWorkspace.Create();
            workspace.WorkspaceFailed += (s, e) =>
            {
                Console.WriteLine($"[MSBuildWorkspace Warning] {e.Diagnostic.Message}");
            };

            var project = await workspace.OpenProjectAsync(csprojPath);
            Console.WriteLine($"[MSBuildWorkspace] Compilation started...");
            var compilation = await project.GetCompilationAsync();
            if (compilation == null)
            {
                Console.Error.WriteLine("ERROR: MSBuildWorkspace failed to compile the project.");
                return false;
            }

            var diagnostics = compilation.GetDiagnostics();
            bool hasErrors = false;
            foreach (var diag in diagnostics)
            {
                if (diag.Severity == DiagnosticSeverity.Error)
                {
                    Console.Error.WriteLine($"[MSBuildWorkspace Error] {diag.Id}: {diag.GetMessage()} at {diag.Location.GetLineSpan()}");
                    hasErrors = true;
                }
                else
                {
                    Console.WriteLine($"[MSBuildWorkspace {diag.Severity}] {diag.Id}: {diag.GetMessage()}");
                }
            }

            if (hasErrors)
            {
                Console.Error.WriteLine("ERROR: Build verification failed with errors.");
                return false;
            }

            Console.WriteLine("SUCCESS: MSBuild workspace verification succeeded! Build builds clean!");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: Exception during MSBuild Workspace build verification: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return false;
        }
    }

    private static string SanitizeClassName(string name)
    {
        var sb = new System.Text.StringBuilder();
        bool nextUpper = true;
        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(nextUpper ? char.ToUpperInvariant(c) : c);
                nextUpper = false;
            }
            else
            {
                nextUpper = true;
            }
        }

        var result = sb.ToString();
        if (result.Length > 0 && char.IsDigit(result[0]))
        {
            result = "Class" + result;
        }

        return string.IsNullOrEmpty(result) ? "ScriptModule" : result;
    }
}
