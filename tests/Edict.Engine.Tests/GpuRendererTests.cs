// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using Edict.Gl;
using Edict.Harness;
using Edict.Platform.Win32;

namespace Edict.Engine.Tests;

/// <summary>
/// P6, step 7: the GPU renderer against the exact one, on demos of the full-frame corpus. Both draw every frame from
/// the same state (the software renderer first, then the GPU renderer reads what it left), and whatever the exact
/// renderer found visible — every world surface it drew a span of, every alias model it drew — the GPU renderer
/// must draw too (it draws more: the whole PVS in the frustum, z-buffered). No GL error may occur. With
/// EDICT_GPUSHOTS=&lt;dir&gt; every 250th frame of both is saved for a look (golden images are compared by eye: a
/// GPU's rasterization is not bit-pinned). Windows only, where a hidden window gives a GL 3.3 context; skipped where
/// none can be made.
/// </summary>
public sealed unsafe class GpuRendererTests
{
    public static IEnumerable<TheoryDataRow<string>> Runs =>
        new[] { "captures_lq_e0m1", "captures_lq_e0m5", "captures_dev", "fuzz_7" }
            .Where(r => File.Exists(Path.Combine(FullFrameGateTests.FullDirectory, r + ".edh")))
            .Select(r => new TheoryDataRow<string>(r));

    [Theory]
    [MemberData(nameof(Runs))]
    public void The_gpu_renderer_draws_what_the_exact_one_finds_visible(string run)
    {
        using OffscreenGl? gl = OffscreenGl.TryCreate();
        Assert.SkipWhen(gl == null, "no OpenGL 3.3 context here");
        var edh = new EdhFile(Path.Combine(FullFrameGateTests.FullDirectory, run + ".edh"));
        using var files = new QFileSystem(TraceCorpus.DataDirectory);
        string cmdline = Encoding.Latin1.GetString(edh.ClientCvars.First(c => Encoding.Latin1.GetString(c.Name) == "cmdline").Value);
        string[] args = cmdline.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1..];
        using var host = new ClientHost(files, edh.ClientRandState, args, cmdline, withScreen: true);
        Screen s = host.Screen!;
        using var renderer = new GlRenderer(s);
        const int W = 640, H = 400;
        renderer.UseOffscreen(W, H);
        string? shots = Environment.GetEnvironmentVariable("EDICT_GPUSHOTS");
        float time = float.Parse(ClientHost.HarnessFrameRate, System.Globalization.CultureInfo.InvariantCulture);
        int views = 0, surfaces = 0, models = 0;
        var missing = new List<string>();
        for (int frame = 0; frame < 6000 && !host.Ended && missing.Count < 10; frame++)
        {
            host.Frame(time);
            if (!s.ViewDrawn)
                continue;
            renderer.DrawFrame(s.FrameBuffer, s.Palette, W, H);
            Assert.Equal(0u, Gl.Gl.GetError());
            views++;
            Model* world = host.Cl.WorldModel;
            for (int i = 0; i < world->NumSurfaces; i++)
            {
                if (!s.SoftwareDrewSurface(i))
                    continue;
                surfaces++;
                if (!renderer.DrewWorldSurface(i))
                    missing.Add($"frame {frame}: world surface {i} drawn by the software renderer, not by the GPU's: {renderer.Explain(i)}");
            }
            foreach (EntityRef r in s.SoftwareDrewAlias)
            {
                models++;
                if (!renderer.DrewAlias.Contains(r))
                {
                    ref ClientEntity e = ref s.EntityOf(r);
                    var am = (AliasModel*)e.Model->Extra;
                    missing.Add($"frame {frame}: alias entity {r.Array}/{r.Index} (origin {e.Origin.X} {e.Origin.Y} {e.Origin.Z}, "
                                + $"eye {s.ViewOrg.X} {s.ViewOrg.Y} {s.ViewOrg.Z}, scale {am->Scale.X} origin {am->ScaleOrigin.X} radius {am->BoundingRadius}) drawn by the software renderer, not by the GPU's");
                }
            }
            if (shots != null && (frame % 250 == 0 || Environment.GetEnvironmentVariable("EDICT_GPUFRAME") == frame.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            {
                Directory.CreateDirectory(shots);
                GlRenderer.WriteBmp(Path.Combine(shots, $"{run}_{frame}_gpu.bmp"), GlRenderer.ReadBack(W, H), W, H);
                File.WriteAllBytes(Path.Combine(shots, $"{run}_{frame}_sw.raw"), s.FrameBuffer.ToArray());
            }
        }
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{run}: {views} views; {surfaces} surfaces and {models} alias models of the exact renderer, all drawn by the GPU's");
        Assert.True(missing.Count == 0, string.Join("\n", missing));
        Assert.True(views > 100, $"{run}: only {views} views drawn");
    }
}
