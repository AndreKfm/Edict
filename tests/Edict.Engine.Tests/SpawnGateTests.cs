// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Text;
using Edict.Harness;

namespace Edict.Engine.Tests;

/// <summary>
/// The cut-2 gate (docs/PLAN.md section C, step 4): the port spawns each map itself — SV_SpawnServer up to its
/// frame-0 point, ED_LoadFromFile, every spawn function on the port's VM with the port's builtins — and its state must
/// hash as the reference's spawn record (.edh from tools/captures.sh): every entity, the edicts lane and the typed
/// globals lane. The raw globals lane holds string offsets, which differ by design (engine strings), and is not
/// compared. Inputs as the reference had them: maxclients, the cvars and the rand seed, from the capture of the run.
/// </summary>
public sealed unsafe class SpawnGateTests
{
    public static IEnumerable<TheoryDataRow<string>> MapNames => VmReplay.Maps().Select(m => new TheoryDataRow<string>(m));

    /// <summary>The spawn-only runs of tools/captures.sh: skills 0, 2, 3, deathmatch and coop with four client slots.</summary>
    public static IEnumerable<TheoryDataRow<string>> Variants
    {
        get
        {
            string dir = Path.Combine(VmReplay.Directory, "spawn");
            string[] files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.ecp") : [];
            return files.Select(f => Path.GetFileNameWithoutExtension(f)).Order(StringComparer.Ordinal)
                        .Select(v => new TheoryDataRow<string>(v));
        }
    }

    /// <summary>A spawned port server and what it was built from.</summary>
    internal sealed class Spawned : IDisposable
    {
        public readonly QFileSystem Files;
        public readonly Progs Progs;
        public readonly Server Sv;
        public readonly ModelCache Models;
        private readonly HunkStrings _hunk;
        private readonly int* _globals;
        private readonly byte* _edicts, _temp;

        public Spawned(string map, SpawnInputs inputs)
        {
            Files = new QFileSystem(TraceCorpus.DataDirectory);
            QFile f = Files.Find("progs.dat") ?? throw new FileNotFoundException("progs.dat");
            Progs = new Progs(f);
            int stride = inputs.HeaderBytes + Progs.EntityFields * 4;
            _globals = (int*)NativeMemory.AllocZeroed((nuint)Progs.NumGlobals, 4);
            new ReadOnlySpan<int>(Progs.InitialGlobals, Progs.NumGlobals).CopyTo(new Span<int>(_globals, Progs.NumGlobals));
            _edicts = (byte*)NativeMemory.AllocZeroed(Server.MaxEdictsDefault, (nuint)stride);
            _temp = (byte*)NativeMemory.AllocZeroed(Server.StringTempSize);
            int tempOfs = Progs.NumStrings;
            _hunk = new HunkStrings(tempOfs + Server.StringTempSize);
            var vm = new QcVm(Progs, _globals, _edicts, inputs.HeaderBytes, VmReplay.NumBuiltins);
            Sv = new Server(vm, Progs, _globals, _edicts, inputs.HeaderBytes, new Cvars(inputs.Cvars), _hunk, _temp, tempOfs)
            {
                MaxEdicts = Server.MaxEdictsDefault,
                MaxClients = inputs.MaxClients,
            };
            Sv.Hooks = new GameHooks(Sv);
            Sv.Rand.Srand(inputs.RandSeed);
            Models = new ModelCache(Files);
            if (!Sv.SpawnServer(Encoding.Latin1.GetBytes(map), Models))
                throw new InvalidOperationException($"Couldn't spawn server maps/{map}.bsp");
        }

        public void Dispose()
        {
            Models.Dispose();
            _hunk.Dispose();
            Progs.Dispose();
            NativeMemory.Free(_globals);
            NativeMemory.Free(_edicts);
            NativeMemory.Free(_temp);
            Files.Dispose();
        }
    }

    /// <summary>Null if the port's spawn of <paramref name="map"/> hashes as the reference's, else the first difference.</summary>
    internal static string? FirstDivergence(string map) => FirstDivergence(Path.Combine(VmReplay.Directory, map + ".ecp"), out _);

    /// <summary>
    /// The cvars as the command line of a variant (tools/captures.sh) set them before SV_SpawnServer: the capture's map
    /// record is written after SV_SpawnServer has rounded skill and, for coop, set deathmatch to 0 (-dedicated 4 had
    /// set it to 1), so that the port's spawn does the same adjustments again.
    /// </summary>
    private static readonly Dictionary<string, (string Name, string Value)[]> PreSpawn = new()
    {
        ["skill0"] = [("skill", "0")],
        ["skill2"] = [("skill", "2")],
        ["skill3"] = [("skill", "3")],
        ["skill26"] = [("skill", "2.6")],
        ["skill06"] = [("skill", "0.6")],
        ["skill5"] = [("skill", "5")],
        ["dm"] = [("deathmatch", "2")],
        ["coop"] = [("deathmatch", "1.000000"), ("coop", "1")],
    };

    /// <param name="capture">A capture whose first map record and first entry give the inputs; its .edh the hashes.</param>
    internal static string? FirstDivergence(string capture, out string what)
    {
        SpawnInputs inputs = VmReplay.FirstSpawn(capture);
        string name = Path.GetFileNameWithoutExtension(capture);
        string variant = name.Contains('.') ? name[(name.IndexOf('.') + 1)..] : "";
        if (PreSpawn.TryGetValue(variant, out var set))
            foreach ((string n, string v) in set)
            {
                int i = inputs.Cvars.FindIndex(c => c.Name.AsSpan().SequenceEqual(Encoding.ASCII.GetBytes(n)));
                if (i < 0)
                    throw new InvalidDataException($"{capture}: no cvar {n}");
                inputs.Cvars[i] = (inputs.Cvars[i].Name, Encoding.ASCII.GetBytes(v));
            }
        string map = inputs.Map;
        EdhFile.Spawn r = new EdhFile(Path.ChangeExtension(capture, ".edh")).Spawns[0];
        using var s = new Spawned(map, inputs);
        Server sv = s.Sv;
        StateHash.Lanes l = new StateHash(s.Progs).Of(sv);
        int free = 0, styles = 0;
        for (int e = 0; e < sv.NumEdicts; e++)
            free += sv.EdictNum(e)->Free != 0 ? 1 : 0;
        for (int i = 0; i < Server.MaxLightstyles; i++)
            styles += sv.Lightstyle(i) != null ? 1 : 0;
        what = $"{sv.NumEdicts} edicts ({free} free), {styles} light styles, skill {sv.CurrentSkill}, maxclients " +
               $"{sv.MaxClients}, deathmatch {*sv.G(GlobalVars.Deathmatch)}, coop {*sv.G(GlobalVars.Coop)}, rand seed after " +
               $"0x{sv.Rand.Seed:X8}; edicts lane {l.Edicts:x16} (reference {r.Edicts:x16})";
        if (r.Map != map)
            return $"the first spawn record is for {r.Map}";
        if (sv.NumEdicts != r.NumEdicts)
            return $"num_edicts: port {sv.NumEdicts}, reference {r.NumEdicts}";
        for (int e = 0; e < r.NumEdicts; e++)
            if (l.Entities[e] != r.Entities[e])
                return $"edict {e} ({Classname(sv, e)}) hashes differently";
        if (l.GlobalsTyped != r.GlobalsTyped)
            return "typed globals lane differs (every entity matches)";
        if (l.Edicts != r.Edicts)
            return "edicts lane differs (every entity and the globals match: sv.time, state, serverflags or light styles)";
        return null;
    }

    internal static string Classname(Server sv, int e)
    {
        int c = ((int*)sv.Fields(e))[EntVars.Classname];
        return c == 0 ? "no classname" : Encoding.Latin1.GetString(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(sv.String(c)));
    }

    [Theory]
    [MemberData(nameof(MapNames))]
    public void Spawn_hashes_as_the_reference(string map)
    {
        string? d = FirstDivergence(Path.Combine(VmReplay.Directory, map + ".ecp"), out string what);
        Assert.True(d is null, $"{map}: {d}");
        TestContext.Current.TestOutputHelper?.WriteLine($"{map}: {what}");
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public void Spawn_variant_hashes_as_the_reference(string variant)
    {
        string? d = FirstDivergence(Path.Combine(VmReplay.Directory, "spawn", variant + ".ecp"), out string what);
        Assert.True(d is null, $"{variant}: {d}");
        TestContext.Current.TestOutputHelper?.WriteLine($"{variant}: {what}");
    }
}
