// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using static Edict.NoAlloc.Analyzers.Tests.Snippets;

namespace Edict.NoAlloc.Analyzers.Tests;

/// <summary>NoAlloc: NA001 allocation, NA002 boxing or closure capture, NA012 pool rent.</summary>
public sealed class AllocationTests
{
    [Fact]
    public Task Reference_type_creation_allocates() =>
        Verifier.VerifyAsync(InHotMethod("var x = {|NA001:new object()|};"));

    [Fact]
    public Task Array_creation_allocates() =>
        Verifier.VerifyAsync(InHotMethod("var x = {|NA001:new int[4]|};"));

    [Fact]
    public Task Struct_with_marked_constructor_does_not_allocate() =>
        Verifier.VerifyAsync(InHotMethod("var x = new S(1); var y = new S(); var z = default(S);",
            "struct S { [NoAlloc(Constraints.All)] public S(int v) { } }"));

    [Fact]
    public Task Struct_with_unmarked_constructor_has_no_contract() =>
        Verifier.VerifyAsync(InHotMethod("var x = {|NA008:new S(1)|};", "struct S { public S(int v) { } }"));

    [Fact]
    public Task Stackalloc_and_allowlisted_span_constructor_are_clean() =>
        Verifier.VerifyAsync(InHotMethod("int* p = stackalloc int[4]; var x = new Span<int>(p, 4); Span<byte> b = stackalloc byte[8];"));

    [Fact]
    public Task Interpolated_string_allocates() =>
        Verifier.VerifyAsync(InHotMethod("var t = {|NA001:$\"{i}\"|};"));

    [Fact]
    public Task Constant_interpolation_and_concatenation_are_folded() =>
        Verifier.VerifyAsync(InHotMethod("const string k = \"a\"; var t = $\"{k}b\"; var u = \"a\" + \"b\" + k;"));

    [Fact]
    public Task String_concatenation_allocates() =>
        Verifier.VerifyAsync(InHotMethod("var t = {|NA001:s + s|}; {|NA001:s += s|};"));

    [Fact]
    public Task Nameof_is_not_evaluated() =>
        Verifier.VerifyAsync(InHotMethod("var n = nameof(M); var m = nameof(list.Count);"));

    [Fact]
    public Task Boxing_to_object_interface_and_from_type_parameter() =>
        Verifier.VerifyAsync(InHotMethod(
            "o = {|NA002:i|}; IComparable c = {|NA002:i|};",
            "[NoAlloc] static object G<T>(T t) => {|NA002:t|};"));

    [Fact]
    public Task Params_array_allocates_and_boxes() =>
        Verifier.VerifyAsync(InHotMethod(
            "{|NA001:P({|NA002:i|}, {|NA002:i|})|}; P();",
            "[NoAlloc(Constraints.All)] static void P(params object[] xs) { }"));

    [Fact]
    public Task Collection_expressions_allocate_unless_targeting_span() =>
        Verifier.VerifyAsync(InHotMethod("Span<int> x = [1, 2]; int[] y = {|NA001:[1, 2]|}; List<int> z = {|NA001:[1]|};"));

    [Fact]
    public Task With_allocates_for_record_class_only() =>
        Verifier.VerifyAsync(InHotMethod("",
            """
            record R(int X);
            record struct RS(int X);
            [NoAlloc] R W(R r) => {|NA001:r with { X = 2 }|};
            [NoAlloc] RS W2(RS r) => r with { X = 2 };
            """));

    [Fact]
    public Task Iterator_allocates_at_its_declaration() =>
        Verifier.VerifyAsync(InHotMethod("", "[NoAlloc] IEnumerable<int> {|NA001:It|}() { yield return 1; }"));

    [Fact]
    public Task Yield_in_nested_local_function_is_reported_on_the_local_function_only() =>
        Verifier.VerifyAsync(InHotMethod("IEnumerable<int> {|NA001:L|}() { yield return 1; }"));

    [Fact]
    public Task Tuples_and_string_equality_are_clean() =>
        Verifier.VerifyAsync(InHotMethod("var t = (i, i); var (x, y) = t; bool b = s == s; var l = s.Length + a.Length + span.Length;"));

    [Fact]
    public Task Anonymous_object_allocates_once() =>
        Verifier.VerifyAsync(InHotMethod("var x = {|NA001:new { A = i }|}; var y = x.A;"));

    [Fact]
    public Task New_of_type_parameter_allocates_unless_struct() =>
        Verifier.VerifyAsync(InHotMethod("",
            """
            [NoAlloc] static T Make<T>() where T : new() => {|NA001:new T()|};
            [NoAlloc] static T MakeValue<T>() where T : struct => new T();
            """));

    [Fact]
    public Task Array_range_copies() =>
        Verifier.VerifyAsync(InHotMethod("var r = {|NA001:a[1..2]|}; var e = a[^1]; var sl = span[1..2];"));

    [Fact]
    public Task Field_initializer_of_marked_type_is_analyzed() =>
        Verifier.VerifyAsync("""
            using Edict.NoAlloc;
            [NoAlloc] class F { object _f = {|NA001:new object()|}; int _n = 1; }
            class G { object _f = new object(); }
            """);

    [Fact]
    public Task Pool_rent_is_its_own_finding() =>
        Verifier.VerifyAsync(InHotMethod("var r = {|NA012:{|NA008:System.Buffers.ArrayPool<int>.Shared|}.Rent(4)|};"));

    [Fact]
    public Task NoAlloc_alone_checks_allocations_only() =>
        Verifier.VerifyAsync("""
            using System;
            using Edict.NoAlloc;
            class C
            {
                [NoAlloc]
                void N(object o, int i)
                {
                    lock (o) { }
                    if (i < 0) throw {|NA001:new InvalidOperationException()|};
                }
            }
            """);
}
