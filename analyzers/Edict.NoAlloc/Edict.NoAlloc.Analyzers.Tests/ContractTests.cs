// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Edict.NoAlloc.Analyzers.Tests;

/// <summary>Overrides and implementations inherit the marker and may only tighten it (NA010).</summary>
public sealed class ContractTests
{
    private const string Interfaces = """
        using Edict.NoAlloc;
        interface I { [NoAlloc(Constraints.All)] void Run(); }
        [NoAlloc(Constraints.All)] interface IT { void Run(); }
        interface IP { [NoAlloc(Constraints.All)] int P { get; } }
        """;

    [Fact]
    public Task Unmarked_implicit_and_explicit_implementations_are_verified_under_the_inherited_contract() =>
        Verifier.VerifyAsync(Interfaces + """
            class A : I { public void Run() { var o = {|NA001:new object()|}; } }
            class B : I { void I.Run() { var o = {|NA001:new object()|}; } }
            class Ok : I { public void Run() { } }
            """);

    [Fact]
    public Task Implementations_that_relax_the_inherited_marker_are_reported() =>
        Verifier.VerifyAsync(Interfaces + """
            class R : I { [NoAlloc] public void {|NA010:Run|}() { } }
            class U : I { [NoAllocUnchecked("calls into native code")] public void {|NA010:Run|}() { } }
            class T : I { [NoAlloc(Constraints.All)] public void Run() { } }
            """);

    [Fact]
    public Task Type_level_interface_marker_is_inherited() =>
        Verifier.VerifyAsync(Interfaces + "class A : IT { public void Run() { var o = {|NA001:new object()|}; } }");

    [Fact]
    public Task Property_contract_applies_to_accessors_and_auto_properties_keep_it() =>
        Verifier.VerifyAsync(Interfaces + """
            class A : IP { public int P { get { var o = {|NA001:new object()|}; return 1; } } }
            class B : IP { public int P { get; } }
            """);

    [Fact]
    public Task Overrides_inherit_the_marker_and_may_only_tighten_it() =>
        Verifier.VerifyAsync("""
            using Edict.NoAlloc;
            class Base
            {
                [NoAlloc(Constraints.All)] public virtual void V() { }
                [NoAlloc(Constraints.All)] public virtual void W() { }
                public virtual void Free() { }
            }
            class Derived : Base
            {
                public override void V() { var o = {|NA001:new object()|}; }
                [NoAlloc] public override void {|NA010:W|}() { }
                public override void Free() { var o = new object(); }
            }
            [NoAlloc(Constraints.All)] abstract class TypeLevel { public abstract void Run(); }
            class Impl : TypeLevel { public override void Run() { var o = {|NA001:new object()|}; } }
            """);

    [Fact]
    public Task Implementation_inherited_from_base_class_is_reported_on_the_type() =>
        Verifier.VerifyAsync(Interfaces + """
            class BaseImpl { public void Run() { } }
            class {|NA010:Derived|} : BaseImpl, I { }
            """);

    [Fact]
    public Task Marked_type_covers_its_members_but_not_nested_types() =>
        Verifier.VerifyAsync("""
            using Edict.NoAlloc;
            [NoAlloc]
            class H
            {
                public int Auto { get; set; }
                public object Computed => {|NA001:new object()|};
                class Nested { void N() { var o = new object(); } }
            }
            """);

    [Fact]
    public Task Member_marker_and_unchecked_win_over_the_type_marker() =>
        Verifier.VerifyAsync("""
            using Edict.NoAlloc;
            [NoAlloc(Constraints.All)]
            class W
            {
                void Heavy(object o) { lock ({|NA003:o|}) { } }
                [NoAlloc] void Light(object o) { lock (o) { } }
                [NoAllocUnchecked("runs once at startup")] void Setup() { var x = new object(); }
            }
            """);
}
