// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using static Edict.Engine.Tests.VmOpcodeFuzz;

namespace Edict.Engine.Tests;

/// <summary>
/// The port's VM against the reference's PR_ExecuteProgram, one statement at a time (<see cref="VmOpcodeFuzz"/>):
/// every opcode except the calls (covered by the capture replay), on all pairs of edge values and on random inputs.
/// </summary>
[Collection(OracleCollection.Name)]
public sealed class VmOpcodeTests
{
    private static readonly Op[] FloatBinary =
        [Op.MulF, Op.DivF, Op.AddF, Op.SubF, Op.EqF, Op.NeF, Op.Le, Op.Ge, Op.Lt, Op.Gt, Op.And, Op.Or, Op.BitAnd, Op.BitOr];

    private static readonly Op[] VectorBinary = [Op.MulV, Op.MulFV, Op.MulVF, Op.AddV, Op.SubV, Op.EqV, Op.NeV];

    /// <summary>
    /// The first divergence over all cases, or null. With <paramref name="all"/>, every divergence is collected
    /// there instead and the run continues (diagnostics).
    /// </summary>
    internal static string? FirstDivergence(Func<VmOpcodeFuzz, IVm> make, int randomPerOp = 20_000, List<string>? all = null)
    {
        using var f = new VmOpcodeFuzz();
        IVm vm = make(f);
        var rng = new Rng(1);
        uint[] edges = Rng.Edges;

        // arithmetic: NaN results compare as a class (docs/adr/D8); copies, branches and addresses: every bit
        string? Case(string what, bool nanClass = true)
        {
            string? d = f.Run(vm, nanClass);
            if (d is null)
                return null;
            if (all is null)
                return $"{what}: {d}";
            all.Add($"{what}: {d}");
            return null;
        }

        void Program(Op op, int a, int b, int c)
        {
            f.Statement(1, op, a, b, c);
            f.Statement(2, Op.Done);
            f.Statement(3, Op.Done);
        }

        // float and vector operators: all pairs of edges (vectors: the pair in every component), then random inputs
        foreach (Op op in FloatBinary.Concat(VectorBinary))
        {
            for (int i = 0; i < edges.Length; i++)
                for (int j = 0; j < edges.Length; j++)
                {
                    f.Scramble(ref rng);
                    for (int k = 0; k < 3; k++)
                    {
                        f.Global(A + k, edges[i]);
                        f.Global(B + k, edges[j]);
                    }
                    Program(op, A, B, C);
                    if (Case($"{op} {Hex(edges[i])}, {Hex(edges[j])}") is { } d)
                        return d;
                }
            for (int n = 0; n < randomPerOp; n++)
            {
                f.Scramble(ref rng);
                for (int k = 0; k < 3; k++)
                {
                    f.Global(A + k, rng.Float());
                    f.Global(B + k, rng.Float());
                }
                Program(op, A, B, C);
                if (Case($"{op} random #{n}") is { } d)
                    return d;
            }
        }

        // unary operators and integer comparisons on edge words, entity offsets, string offsets
        uint[] words =
        [
            .. edges, 0, 1, 2, (uint)f.EdictOffset(1), (uint)f.EdictOffset(3),
            .. Enumerable.Range(0, f.StringCount).Select(i => (uint)f.StringOffset(i)),
        ];
        foreach (Op op in new[] { Op.NotF, Op.NotV, Op.NotFnc, Op.NotEnt, Op.NotS })
            foreach (uint w in words)
            {
                if (op == Op.NotS && w >= (uint)f.Progs.NumStrings)
                    continue;               // a string offset must point into the strings
                f.Scramble(ref rng);
                f.Global(A, w);
                f.Global(A + 1, w);
                f.Global(A + 2, w);
                Program(op, A, 0, C);
                if (Case($"{op} {Hex(w)}") is { } d)
                    return d;
            }
        foreach (Op op in new[] { Op.EqE, Op.NeE, Op.EqFnc, Op.NeFnc })
            foreach (uint x in words)
                foreach (uint y in words)
                {
                    f.Scramble(ref rng);
                    f.Global(A, x);
                    f.Global(B, y);
                    Program(op, A, B, C);
                    if (Case($"{op} {Hex(x)}, {Hex(y)}") is { } d)
                        return d;
                }

        // strings: every pair of offsets, empty strings and equal strings at different offsets included
        foreach (Op op in new[] { Op.EqS, Op.NeS })
            for (int i = 0; i < f.StringCount; i++)
                for (int j = 0; j < f.StringCount; j++)
                {
                    f.Scramble(ref rng);
                    f.Global(A, (uint)f.StringOffset(i));
                    f.Global(B, (uint)f.StringOffset(j));
                    Program(op, A, B, C);
                    if (Case($"{op} string {i}, string {j}") is { } d)
                        return d;
                }

        // stores, loads, addresses and stores through pointers
        for (int n = 0; n < randomPerOp; n++)
        {
            int e = rng.Below(Edicts), field = rng.Below(Fields - 2);
            uint v0 = rng.Float(), v1 = rng.Float(), v2 = rng.Float();
            foreach (Op op in new[] { Op.StoreF, Op.StoreV, Op.StoreS, Op.StoreEnt, Op.StoreFld, Op.StoreFnc })
            {
                f.Scramble(ref rng);
                f.Global(A, v0);
                f.Global(A + 1, v1);
                f.Global(A + 2, v2);
                Program(op, A, B, 0);
                if (Case($"{op} #{n}", nanClass: false) is { } d)
                    return d;
            }
            foreach (Op op in new[] { Op.LoadF, Op.LoadV, Op.LoadS, Op.LoadEnt, Op.LoadFld, Op.LoadFnc, Op.Address })
            {
                f.Scramble(ref rng);
                f.Active = rng.Below(2) == 0 && e != 0;    // world entity assignment while active is a PR_RunError
                f.Global(A, (uint)f.EdictOffset(e));
                f.Global(B, (uint)field);
                Program(op, A, B, C);
                if (Case($"{op} edict {e} field {field} #{n}", nanClass: false) is { } d)
                    return d;
            }
            foreach (Op op in new[] { Op.StorePF, Op.StorePV, Op.StorePS, Op.StorePEnt, Op.StorePFld, Op.StorePFnc })
            {
                f.Scramble(ref rng);
                f.Global(A, v0);
                f.Global(A + 1, v1);
                f.Global(A + 2, v2);
                f.Global(B, (uint)f.FieldPointer(e, field));
                Program(op, A, B, 0);
                if (Case($"{op} edict {e} field {field} #{n}", nanClass: false) is { } d)
                    return d;
            }
        }
        f.Active = false;

        // branches on edge words: the store of statement 2 runs only when the branch is not taken
        foreach (Op op in new[] { Op.If, Op.IfNot })
            foreach (uint w in words)
            {
                f.Scramble(ref rng);
                f.Global(A, w);
                f.Global(One, 0x3F800000);
                f.Statement(1, op, A, 2, 0);
                f.Statement(2, Op.StoreF, One, C);
                f.Statement(3, Op.Done);
                if (Case($"{op} {Hex(w)}", nanClass: false) is { } d)
                    return d;
            }
        f.Scramble(ref rng);
        f.Statement(1, Op.Goto, 2);
        f.Statement(2, Op.StoreF, One, C);
        f.Statement(3, Op.Done);
        if (Case("Goto", nanClass: false) is { } goto_)
            return goto_;

        // return values
        for (int n = 0; n < 1000; n++)
        {
            f.Scramble(ref rng);
            f.Statement(1, Op.Return, A);
            f.Statement(2, Op.Done);
            if (Case($"Return #{n}", nanClass: false) is { } d)
                return d;
        }

        // OP_STATE: frames from edges and random words against equal (also with the other sign of zero) and other
        // frames; times from edges and random words, from a run's frame times, and anywhere up to 10^5 s
        for (int n = 0; n < randomPerOp * 4; n++)
        {
            int e = rng.Below(Edicts);
            f.Scramble(ref rng);
            f.Global(GlobalVars.Self, (uint)f.EdictOffset(e));
            uint time = rng.Below(3) switch
            {
                0 => rng.Float(),
                1 => BitConverter.SingleToUInt32Bits((float)(rng.Below(1 << 20) * (double)(float)0.013889)),
                _ => BitConverter.SingleToUInt32Bits((float)((rng.Next() >> 11) * (1.0 / 9007199254740992.0) * 100000)),
            };
            f.Global(GlobalVars.Time, time);
            uint frame = rng.Float();
            f.Global(A, frame);
            f.Field(e, EntVars.Frame, rng.Below(2) == 0 ? frame ^ (rng.Below(2) == 0 ? 0x80000000u : 0) : rng.Float());
            f.Statement(1, Op.State, A, B);
            f.Statement(2, Op.Done);
            if (Case($"State edict {e}, time {Hex(time)}, frame {Hex(frame)}") is { } d)
                return d;
        }
        return null;
    }

    [Fact(Explicit = true)]
    public void Survey_all_divergences()
    {
        var all = new List<string>();
        FirstDivergence(f => f.MakePortVm(), all: all);
        string path = Path.Combine(Path.GetTempPath(), "edict-vm-survey.txt");
        File.WriteAllLines(path, all);
        Assert.Fail($"{all.Count} divergences, written to {path}");
    }

    /// <summary>
    /// The edict header of the oracle in this process and of the build that wrote the captures (docs/adr/D7): on
    /// Windows the MSVC ABI against the Linux captures' System V ABI.
    /// </summary>
    [Fact]
    public void Edict_header_size_is_the_same_in_oracle_and_captures()
    {
        foreach (string map in VmReplay.Maps())
            Assert.Equal(HeaderBytes, VmReplay.HeaderBytesOf(Path.Combine(VmReplay.Directory, map + ".ecp")));
    }

    [Fact]
    public void Every_opcode_matches_the_reference_on_edge_and_random_inputs()
    {
        string? d = FirstDivergence(f => f.MakePortVm());
        Assert.True(d is null, "first divergence: " + d);
    }
}
