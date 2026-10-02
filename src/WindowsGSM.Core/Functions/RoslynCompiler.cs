using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System;
using ICSharpCode.SharpZipLib.Zip;
using System.Text;
using WindowsGSM.Functions;
using Microsoft.Extensions.DependencyModel;

public class RoslynCompiler
{
    readonly CSharpCompilation _compilation;
    Assembly _generatedAssembly;
    Type _proxyType;
    string _assemblyName;
    string _typeName;
    PluginMetadata _pluginMetadata;

    public RoslynCompiler(string typeName, string code, Type[] typesToReference, PluginMetadata pluginMetadata)
    {
        _pluginMetadata = pluginMetadata;
        _typeName = typeName;

        var refs = DependencyContext.Default.CompileLibraries // filter out some libs?
            .SelectMany(cl => cl.ResolveReferencePaths())
            .Select(asm => MetadataReference.CreateFromFile(asm))
            .ToList();

        refs.Add(MetadataReference.CreateFromFile(typeof(RoslynCompiler).Assembly.Location)); 
        refs.Add(MetadataReference.CreateFromFile(typeof(Newtonsoft.Json.JsonConvert).Assembly.Location));
        refs.Add(MetadataReference.CreateFromFile(typeof(ZipFile).Assembly.Location));

        //generate syntax tree from code and config compilation options
        var syntax = CSharpSyntaxTree.ParseText(code);
        var options = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            allowUnsafe: true,
            optimizationLevel: OptimizationLevel.Release);

        _compilation = CSharpCompilation.Create(_assemblyName = Guid.NewGuid().ToString(), new List<SyntaxTree> { syntax }, refs, options);
    }

    private const string StandardUsings =
        "global using System; global using System.Collections.Generic; global using System.IO; global using System.Linq; " +
        "global using System.Net.Http; global using System.Text; global using System.Text.RegularExpressions; global using System.Threading.Tasks;";

    /// <summary>Every error is "name/type not found" (CS0103, CS0246) — what a forgotten using looks like.</summary>
    private static bool OnlyMissingNames(IEnumerable<Diagnostic> diagnostics)
    {
        var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error || d.IsWarningAsError).ToList();
        return errors.Count > 0 && errors.All(d => d.Id is "CS0103" or "CS0246");
    }

    public Type Compile()
    {

        if (_proxyType != null) return _proxyType;

        using (var ms = new MemoryStream())
        {
            var result = _compilation.Emit(ms);
            if (!result.Success && OnlyMissingNames(result.Diagnostics))
            {
                // NEXT: a plugin that only fails for a missing using (e.g. Regex without System.Text.RegularExpressions)
                // gets one retry with the usual namespaces imported. Plugins that compile as written are never changed.
                var retry = _compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(StandardUsings));
                ms.SetLength(0);
                var second = retry.Emit(ms);
                if (second.Success)
                {
                    Console.WriteLine($"{_typeName}: compiled with standard usings added (the plugin is missing a using).");
                    result = second;
                }
                else { ms.SetLength(0); }
            }
            if (!result.Success)
            {
                var compilationErrors = result.Diagnostics.Where(diagnostic =>
                        diagnostic.IsWarningAsError ||
                        diagnostic.Severity == DiagnosticSeverity.Error)
                    .ToList();
                if (compilationErrors.Any())
                {
                    var firstError = compilationErrors.First();
                    var errorNumber = firstError.Id;
                    var errorDescription = firstError.GetMessage();
                    var firstErrorMessage = $"{errorNumber}: {errorDescription};";
                    var exception = new Exception($"Compilation failed, first error is: {firstErrorMessage}");
                    compilationErrors.ForEach(e => { if (!exception.Data.Contains(e.Id)) exception.Data.Add(e.Id, e.GetMessage()); });

                    var sb = new StringBuilder();
                    foreach (var data in compilationErrors)
                    {
                        sb.Append($"{data.Id}\nLine: {data.Location} - Properties: {string.Join(";", data.Properties.Values)}\n\n");
                    }


                    _pluginMetadata.Error = sb.ToString();
                        Console.WriteLine(_pluginMetadata.Error);
                    
                    throw exception;
                }
            }
            ms.Seek(0, SeekOrigin.Begin);

            _generatedAssembly = AssemblyLoadContext.Default.LoadFromStream(ms);

            _proxyType = _generatedAssembly.GetType(_typeName);
            return _proxyType;
        }
    }
}
