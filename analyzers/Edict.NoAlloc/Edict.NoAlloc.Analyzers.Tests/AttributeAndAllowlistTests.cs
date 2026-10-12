// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using static Edict.NoAlloc.Analyzers.Tests.Snippets;

namespace Edict.NoAlloc.Analyzers.Tests;

public sealed class AttributeAndAllowlistTests
{
    [Fact]
    public Task Relaxation_without_reason_is_reported() =>
        Verifier.VerifyAsync("""
            using Edict.NoAlloc;
            class C
            {
                [NoAllocUnchecked("")] void {|NA009:A|}() { }
                [NoAllocUnchecked("   ")] void {|NA009:B|}() { }
                [NoAllocUnchecked(null!)] void {|NA009:D|}() { }
                [NoAllocUnchecked("reads a cached value")] void E() { }
                [NoAlloc(Constraints.All, Allow = Allow.Lock)] void {|NA009:F|}() { }
                [NoAlloc(Constraints.All, Allow = Allow.Lock, Reason = "registry lock at startup")] void G() { }
            }
            """);

    [Fact]
    public Task Project_allowlist_assumes_the_listed_constraints_only() =>
        Verifier.VerifyAsync(
            InHotMethod("Plain(); {|NA008:Partial()|}; var l = {|NA001:new Lib(1)|};",
                "class Lib { public Lib(int x) { } } static void Partial() { }"),
            new InMemoryAdditionalText("noalloc-allowlist.toml", """
                # project allowlist
                [[member]]
                symbol = "cs:Test/M:C.Plain"   # trailing comment
                constraints = ["All"]
                reason = "does nothing"

                [[member]]
                symbol = "cs:*/M:C.Partial"
                constraints = ["NoAlloc", "NoThrow"]
                """));

    [Fact]
    public async Task Unresolved_and_malformed_entries_are_reported_at_their_table()
    {
        var compilation = Verifier.Compile(InHotMethod(""), "Test");
        var file = new InMemoryAdditionalText("noalloc-allowlist.toml", """
            [[member]]
            symbol = "cs:Test/M:C.Plain"
            constraints = ["All"]
            [[member]]
            symbol = "cs:Test/M:C.DoesNotExist"
            constraints = ["All"]
            [[member]]
            symbol = "M:C.Plain"
            constraints = ["All"]
            [[member]]
            symbol = "cs:Test/M:C.Plain"
            constraints = ["Fast"]
            [[member]]
            symbol = "cs:Other/M:C.Plain"
            constraints = ["All"]
            [[member]]
            symbol = "cs:Test/M:C.DoesNotExist"
            constraints = ["All"]
            runtime = "1.0"
            """);

        var diagnostics = await Verifier.AnalyzeAsync(compilation, file);

        Assert.Equal(
            [("NA014", 3), ("NA015", 6), ("NA015", 9), ("NA014", 12)],
            diagnostics.OrderBy(d => d.Location.GetLineSpan().StartLinePosition.Line)
                .Select(d => (d.Id, d.Location.GetLineSpan().StartLinePosition.Line)));
    }

    [Fact]
    public async Task Files_with_other_names_are_not_allowlists()
    {
        var compilation = Verifier.Compile(InHotMethod("Plain();"), "Test");
        var diagnostics = await Verifier.AnalyzeAsync(compilation,
            new InMemoryAdditionalText("notes.toml", "[[member]]\nsymbol = \"cs:*/M:C.Plain\"\nconstraints = [\"All\"]"));

        Assert.Equal(["NA008"], diagnostics.Select(d => d.Id));
    }

    [Fact]
    public void Parser_reads_strings_arrays_and_comments_and_keeps_hash_inside_strings()
    {
        var entries = Allowlist.Parse(SourceText.From("""
            # header
            [[member]]
            symbol = "cs:*/M:A.#ctor(System.Int32)"   # why
            constraints = ["NoAlloc", "NoThrow"]
            runtime = "10.0"
            reason = "a \"quoted\" reason"
            """));

        var entry = Assert.Single(entries);
        Assert.Null(entry.Error);
        Assert.Equal(("*", "M:A.#ctor(System.Int32)"), (entry.Assembly, entry.DocumentationId));
        Assert.Equal(Constraint.NoAlloc | Constraint.NoThrow, entry.Constraints);
        Assert.Equal(("10.0", "a \"quoted\" reason"), (entry.Runtime, entry.Reason));
    }

    [Fact]
    public void Every_baseline_entry_is_well_formed_and_resolves_on_the_test_runtime()
    {
        var compilation = Verifier.Compile("class Empty { }", "Baseline");

        Assert.All(NoAllocModel.Baseline, entry => Assert.Null(entry.Error));
        Assert.Empty(NoAllocModel.Baseline.Where(entry => Allowlist.Resolve(entry, compilation).IsEmpty).Select(entry => entry.Symbol));
    }

    [Fact]
    public void Without_attributes_the_analyzer_registers_nothing()
    {
        var compilation = Verifier.Compile("class Empty { }", "NoAttributes", injectAttributes: false);

        Assert.Null(NoAllocModel.TryCreate(compilation, [], TestContext.Current.CancellationToken));
        Assert.Empty(compilation.GetTypesByMetadataName("Edict.NoAlloc.NoAllocAttribute"));
    }

    [Fact]
    public async Task Markers_are_recognized_across_assemblies()
    {
        var library = Verifier.Compile("""
            using Edict.NoAlloc;
            public interface IStage { [NoAlloc(Constraints.All)] void Run(); }
            public static class Lib
            {
                [NoAlloc(Constraints.All)] public static int Fast(int x) => x;
                public static int Slow(int x) => x;
            }
            """, "Library");
        using var image = new MemoryStream();
        Assert.True(library.Emit(image, cancellationToken: TestContext.Current.CancellationToken).Success);
        var reference = MetadataReference.CreateFromImage(image.ToArray());

        var consumer = Verifier.Compile("""
            using Edict.NoAlloc;
            class Stage : IStage { public void Run() { var o = new object(); } }
            class C { [NoAlloc(Constraints.All)] int M(int x) => Lib.Fast(x) + Lib.Slow(x); }
            """, "Consumer", [reference]);

        var diagnostics = await Verifier.AnalyzeAsync(consumer);

        Assert.Equal(["NA001", "NA008"], diagnostics.Select(d => d.Id).Order());
    }
}
