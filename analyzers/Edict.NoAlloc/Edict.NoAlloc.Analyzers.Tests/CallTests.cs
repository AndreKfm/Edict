// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using static Edict.NoAlloc.Analyzers.Tests.Snippets;

namespace Edict.NoAlloc.Analyzers.Tests;

/// <summary>Callees must promise every constraint the caller requires: NA008 when a promise is missing, NA013 when it is relaxed.</summary>
public sealed class CallTests
{
    [Fact]
    public Task Unmarked_callee_has_no_contract() =>
        Verifier.VerifyAsync(InHotMethod("{|NA008:Plain()|};"));

    [Fact]
    public Task Marked_intrinsic_and_allowlisted_callees_are_allowed() =>
        Verifier.VerifyAsync(InHotMethod("Hot(); var m = Math.Max(i, 2); var l = span.Length; span.Fill(0); var c = s[0];"));

    [Fact]
    public Task Callee_promising_fewer_constraints_has_no_contract_for_the_rest() =>
        Verifier.VerifyAsync(InHotMethod("{|NA008:AllocFree()|};"));

    [Fact]
    public Task Unchecked_callee_is_a_contract_breach() =>
        Verifier.VerifyAsync(InHotMethod("{|NA013:Unchecked()|};"));

    [Fact]
    public Task Callee_relaxed_by_allow_is_a_contract_breach_for_stricter_callers_only() =>
        Verifier.VerifyAsync(InHotMethod("{|NA013:Locks()|};",
            """
            [NoAlloc(Constraints.All, Allow = Allow.Lock, Reason = "takes the registry lock once")] static void Locks() { }
            [NoAlloc] static void Light() => Locks();
            """));

    [Fact]
    public Task Members_of_a_marked_type_are_allowed() =>
        Verifier.VerifyAsync(InHotMethod("H.N(); var p = H.P; H.P = 2;",
            "[NoAlloc(Constraints.All)] class Hh { public int P { get; set; } public void N() { } } Hh H = null!;"));

    [Fact]
    public Task Non_virtual_auto_property_of_any_source_type_is_allowed_but_virtual_is_not() =>
        Verifier.VerifyAsync(InHotMethod("var p = U.P; U.P = 1; var v = {|NA008:U.V|};",
            "class Uu { public int P { get; set; } public virtual int V { get; set; } } Uu U = null!;"));

    [Fact]
    public Task Unmarked_user_defined_operators_and_conversions_have_no_contract() =>
        Verifier.VerifyAsync(InHotMethod(
            "var v = new V(); var w = {|NA008:v + v|}; {|NA008:v += v|}; {|NA008:v++|}; int n = {|NA008:v|};",
            """
            struct V
            {
                public static V operator +(V a, V b) => a;
                public static V operator ++(V a) => a;
                public static implicit operator int(V v) => 0;
            }
            """));

    [Fact]
    public Task Explicit_base_constructor_call_is_checked() =>
        Verifier.VerifyAsync("""
            using Edict.NoAlloc;
            class B { public B(int x) { } }
            class D : B { [NoAlloc] public D() {|NA008:: base(1)|} { } }
            class E { [NoAlloc] public E() { } }
            """);

    [Fact]
    public Task Implicit_base_constructor_call_is_checked() =>
        Verifier.VerifyAsync("""
            using Edict.NoAlloc;
            class B { public B() { } }
            class D : B { [NoAlloc] public {|NA008:D|}() { } }
            class H { [NoAlloc] public H() { } }
            class F : H { [NoAlloc] public F() { } }
            """);

    [Fact]
    public Task Struct_get_hash_code_is_a_call_like_any_other() =>
        Verifier.VerifyAsync(InHotMethod(
            "var h = {|NA008:new G1().GetHashCode()|}; var h2 = {|NA008:new G2().GetHashCode()|}; var h3 = new G3().GetHashCode();",
            """
            struct G1 { }
            struct G2 { public override int GetHashCode() => 1; }
            struct G3 { [NoAlloc(Constraints.All)] public override int GetHashCode() => 1; }
            """));

    [Fact]
    public Task Foreach_over_list_is_one_finding_and_over_array_span_string_is_clean() =>
        Verifier.VerifyAsync(InHotMethod(
            "foreach (var x in {|NA008:list|}) { } foreach (var x in a) { } foreach (var x in span) { } foreach (var ch in s) { }"));

    [Fact]
    public Task Using_checks_dispose() =>
        Verifier.VerifyAsync(InHotMethod(
            "using var {|NA008:d = new SD()|}; using var h = new HD();",
            """
            struct SD : IDisposable { public void Dispose() { } }
            struct HD : IDisposable { [NoAlloc(Constraints.All)] public void Dispose() { } }
            """));

    [Fact]
    public Task Deconstruction_checks_deconstruct() =>
        Verifier.VerifyAsync(InHotMethod(
            "{|NA008:var (x, y) = new P()|};",
            "struct P { public void Deconstruct(out int a, out int b) { a = 1; b = 2; } }"));

    [Fact]
    public Task Calling_an_unmarked_sibling_local_function_has_no_contract() =>
        Verifier.VerifyAsync("""
            using Edict.NoAlloc;
            class C
            {
                void Outer()
                {
                    [NoAlloc] void A() { {|NA008:B()|}; }
                    void B() { var x = new object(); }
                    A();
                }
            }
            """);

    [Fact]
    public Task Ref_argument_from_unmarked_async_method_is_clean() =>
        Verifier.VerifyAsync("""
            using System.Threading.Tasks;
            using Edict.NoAlloc;
            class C
            {
                [NoAlloc] static void Fill(ref int x) => x = 1;
                async Task Run() { await Task.Yield(); int v = 0; Fill(ref v); }
            }
            """);
}
