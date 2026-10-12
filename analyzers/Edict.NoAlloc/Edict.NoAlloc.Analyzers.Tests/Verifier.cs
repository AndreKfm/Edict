// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Edict.NoAlloc.Analyzers.Tests;

/// <summary>
/// Compiles C# against the running runtime's own assemblies (no package downloads), injects the attributes with
/// the real generator, runs the analyzer and compares diagnostics exactly with the markup: <c>{|NA001:code|}</c>
/// marks an expected diagnostic on <c>code</c>; markers may nest. Unmarked code must produce no diagnostic.
/// </summary>
internal static class Verifier
{
    private static readonly Lazy<ImmutableArray<MetadataReference>> RuntimeReferences = new(() =>
    {
        var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        return ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(path => string.Equals(Path.GetDirectoryName(path), runtimeDirectory, StringComparison.OrdinalIgnoreCase))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToImmutableArray();
    });

    public static ImmutableArray<MetadataReference> References => RuntimeReferences.Value;

    public static async Task VerifyAsync(string markup, params AdditionalText[] additionalFiles)
    {
        var (source, expected) = Markup.Parse(markup);
        var compilation = Compile(source, "Test");
        var diagnostics = await AnalyzeAsync(compilation, additionalFiles);

        var actual = diagnostics
            .Where(d => d.Location.IsInSource && d.Location.SourceTree!.FilePath == "Test.cs")
            .Select(d => new Markup.Expected(d.Id, d.Location.SourceSpan))
            .ToList();

        var missing = expected.Except(actual).ToList();
        var unexpected = actual.Except(expected).ToList();
        if (missing.Count > 0 || unexpected.Count > 0)
        {
            var message = new StringBuilder();
            foreach (var item in missing)
                message.AppendLine($"missing:    {item.Id} at {item.Span} '{source.Substring(item.Span.Start, item.Span.Length)}'");
            foreach (var item in unexpected)
            {
                var diagnostic = diagnostics.First(d => d.Id == item.Id && d.Location.SourceSpan == item.Span);
                message.AppendLine($"unexpected: {item.Id} at {item.Span} '{source.Substring(item.Span.Start, item.Span.Length)}': {diagnostic.GetMessage()}");
            }
            Assert.Fail(message.ToString());
        }
    }

    public static CSharpCompilation Compile(string source, string assemblyName, IEnumerable<MetadataReference>? extraReferences = null, bool injectAttributes = true)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview), path: "Test.cs");
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [tree],
            References.AddRange(extraReferences ?? []),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable));

        var options = new TestOptionsProvider(injectAttributes ? null : "false");
        CSharpGeneratorDriver.Create([new AttributeInjector().AsSourceGenerator()], optionsProvider: options, parseOptions: (CSharpParseOptions)tree.Options)
            .RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _);

        var errors = updated.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0)
            Assert.Fail("Test source does not compile:\n" + string.Join("\n", errors));
        return (CSharpCompilation)updated;
    }

    public static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(Compilation compilation, params AdditionalText[] additionalFiles)
    {
        var withAnalyzers = compilation.WithAnalyzers(
            [new NoAllocAnalyzer()],
            new AnalyzerOptions([.. additionalFiles]));
        return await withAnalyzers.GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);
    }
}

internal sealed class InMemoryAdditionalText(string path, string content) : AdditionalText
{
    public override string Path { get; } = path;

    public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(content);
}

internal sealed class TestOptionsProvider(string? injectAttributes) : AnalyzerConfigOptionsProvider
{
    public override AnalyzerConfigOptions GlobalOptions { get; } = new Options(injectAttributes);

    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => new Options(null);

    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => new Options(null);

    private sealed class Options(string? injectAttributes) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value)
        {
            value = injectAttributes ?? "";
            return injectAttributes is not null && key == AttributeInjector.OptionName;
        }
    }
}

internal static class Markup
{
    public readonly record struct Expected(string Id, TextSpan Span);

    public static (string Source, List<Expected> Expected) Parse(string markup)
    {
        var source = new StringBuilder();
        var open = new Stack<(string Id, int Start)>();
        var expected = new List<Expected>();
        for (var i = 0; i < markup.Length; i++)
        {
            if (markup[i] == '{' && i + 1 < markup.Length && markup[i + 1] == '|')
            {
                var colon = markup.IndexOf(':', i + 2);
                open.Push((markup.Substring(i + 2, colon - i - 2), source.Length));
                i = colon;
            }
            else if (markup[i] == '|' && i + 1 < markup.Length && markup[i + 1] == '}' && open.Count > 0)
            {
                var (id, start) = open.Pop();
                expected.Add(new Expected(id, TextSpan.FromBounds(start, source.Length)));
                i++;
            }
            else
            {
                source.Append(markup[i]);
            }
        }
        return (source.ToString(), expected);
    }
}
