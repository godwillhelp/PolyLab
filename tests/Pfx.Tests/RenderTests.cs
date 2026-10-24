using System.Numerics;
using Pfx.Core.Model;
using Pfx.Core.Text;
using Pfx.Render;
using Pfx.Sim;

namespace Pfx.Tests;

// Viewport parts that run without a GPU: instance output, graph bake, stress grid, low frame rate, camera, material
// reading, shader compilation. Device creation, device loss and frame timing are checked by PfxView --selftest / --bench.
public class RenderTests
{
    private const string Effect = """
        EffectDef {
         Emitors {
          EmitorDef a {
           ShapeType Box
           ShapeSize 1 1 1
           MaxNum 200
           BirthRate 40
           Material "fx/none.emat"
           SizeMultiplier 0.8
           SizeRND 0.4
           RandomAngle 1
           RotMultiplier 30
           Velocity 2
           VelocityRND 1
           LifeTime 2
           Color {
            0 1 0.5 0 0.4 0.2 0.9 0.3 1 0 0 1
           }
           Alpha {
            0 0 0.3 1 1 0
           }
           Size {
            0 0.2 0.5 1 1 0.6
           }
           EffectTime 3
           Repeat 1
           SizeMast {
            0 0.3 1 1
           }
          }
         }
        }
        """;

    private static PtcDocument Doc() => PtcDocument.From(EnfDocument.Parse(Effect.Replace("\r\n", "\n")));

    private static EffectPreview Preview(PreviewSettings? settings = null) =>
        new(Doc(), Path.Combine(Path.GetTempPath(), "pfx_render_test.ptc"), new DataRoots(Array.Empty<string>()), settings);

    [Fact]
    public void Instances_carry_what_the_cpu_path_computes()
    {
        var sim = EffectSim.From(Doc(), new SimOptions(), new SourceMotion.Turning(new Vector3(1, 2, 3), 10, 45));
        sim.RunTo(4.3f);
        var cpu = sim.Render(0);
        var gpu = new ParticleInstance[sim.Emitters[0].Count];
        Assert.Equal(cpu.Count, sim.WriteInstances(0, gpu));
        var p = sim.Emitters[0].Params;
        for (int i = 0; i < cpu.Count; i++)
        {
            Assert.Equal(cpu[i].Position, gpu[i].Position);
            Assert.Equal(cpu[i].AgeFraction, gpu[i].AgeFraction);
            Assert.Equal(cpu[i].Frame, (int)gpu[i].Frame);
            Assert.Equal(cpu[i].Angle, gpu[i].Angle);
            // The shader's size: half size x 2 x Size graph at the age x SizeMast at the birth fraction.
            float size = gpu[i].HalfSize * 2 * p.Size.EvaluateGpu(gpu[i].AgeFraction) * p.SizeMast.EvaluateGpu(gpu[i].BirthFraction);
            Assert.True(MathF.Abs(size - cpu[i].Size) < 1e-5f, $"{size} vs {cpu[i].Size}");
        }
    }

    [Fact]
    public void Graph_bake_holds_the_games_texels()
    {
        var preview = Preview();
        var p = preview.Params[0];
        var t = preview.ParamsTexture;
        Assert.Equal(EffectPreview.ParamsWidth * EffectPreview.RowsPerEmitter * 4, t.Length);
        int row = EffectPreview.ParamsWidth * 4;
        foreach (int i in new[] { 0, 1, 77, 128, 200, 255 })
        {
            float x = i / 255f;
            Assert.Equal((byte)(p.Color.Evaluate(x, 1) * 255 + 1e-3f), t[i * 4 + 1]);
            Assert.Equal(p.Alpha.Texel(i), t[i * 4 + 3]);
            Assert.Equal(p.SizeMast.Texel(i), t[2 * row + i * 4 + 3]);
            Assert.Equal(p.Size.Texel(i), t[2 * row + i * 4 + 2]);
        }
    }

    [Fact]
    public void Stress_copies_fill_a_grid_with_the_effect_itself_at_the_origin()
    {
        var preview = Preview(new PreviewSettings { Copies = 10, Spacing = 4, Origin = new Vector3(5, 0, 5) });
        Assert.Equal(10, preview.Sims.Count);
        Assert.Equal(Vector3.Zero, preview.CopyOffsets[0]);
        Assert.Equal(10, preview.CopyOffsets.Distinct().Count());
        foreach (var o in preview.CopyOffsets)
            Assert.True(MathF.Abs(o.X % 4) < 1e-4f && MathF.Abs(o.Z % 4) < 1e-4f && o.Y == 0, o.ToString());
        preview.Seek(1);
        Assert.Equal(new Vector3(5, 0, 5) + preview.CopyOffsets[3], preview.Sims[3].Motion.At(0).Position);
    }

    private static byte[] Instances(EffectPreview p)
    {
        var bytes = new List<byte>();
        foreach (var sim in p.Sims)
            for (int e = 0; e < sim.Emitters.Count; e++)
            {
                var into = new ParticleInstance[sim.Emitters[e].Count];
                sim.WriteInstances(e, into);
                bytes.AddRange(System.Runtime.InteropServices.MemoryMarshal.AsBytes(into.AsSpan()).ToArray());
            }
        return bytes.ToArray();
    }

    // Spec 7 P6: scrubbing to t gives the same frame whatever the preview did before (played on, seeked back to a
    // checkpoint, or a worker's seek job applied).
    [Fact]
    public void Seeking_to_a_time_gives_the_same_particles_from_any_starting_point()
    {
        var settings = new PreviewSettings { Copies = 2, Motion = MotionKind.Turn, TurnRate = 30 };
        var played = Preview(settings);
        for (int i = 0; i < 9 * 60; i++)
        {
            played.Advance(1f / 60);
            if (i == 3 * 60)
                played.AddCheckpoint(played.Capture());
        }
        var direct = Preview(settings);
        direct.Seek(5.3f);
        played.Seek(5.3f);
        Assert.Equal(direct.ShownSteps, played.ShownSteps);
        Assert.Equal(Instances(direct), Instances(played));
        var job = played.PrepareSeek(2.4f)!;
        Assert.Equal(0, job.FromTime);
        job.Run();
        Assert.True(played.Apply(job));
        var back = Preview(settings);
        back.Seek(2.4f);
        Assert.Equal(Instances(back), Instances(played));
        var forward = played.PrepareSeek(8.05f)!;
        Assert.True(forward.FromTime >= 2.4f - 1e-4f);
        forward.Run();
        played.Restart();
        Assert.False(played.Apply(forward));
        Assert.True(direct.AliveTotal > 0);
    }

    // A preview rebuilt at a later time has no checkpoints before it; the background fill simulates that stretch and the
    // seeks into it then equal a run from 0.
    [Fact]
    public void Background_fill_covers_a_jump_and_keeps_seeks_exact()
    {
        var settings = new PreviewSettings { Copies = 2 };
        var rebuilt = Preview(settings);
        rebuilt.StartAt(6.2f, null);
        Assert.Equal(0, rebuilt.FilledSteps);
        var fill = rebuilt.PrepareFill()!;
        fill.Run(CancellationToken.None);
        Assert.True(rebuilt.Apply(fill));
        Assert.Equal(rebuilt.StepsOf(6.2f), rebuilt.FilledSteps);
        Assert.Null(rebuilt.PrepareFill());
        rebuilt.Seek(3.7f);
        var direct = Preview(settings);
        direct.Seek(3.7f);
        Assert.Equal(Instances(direct), Instances(rebuilt));
        var cancelled = Preview(settings);
        cancelled.StartAt(4f, null);
        var job = cancelled.PrepareFill()!;
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        job.Run(cancel.Token);
        Assert.False(cancelled.Apply(job));
    }

    [Fact]
    public void Checkpoints_stay_in_step_order_and_within_their_budget()
    {
        var preview = Preview();
        preview.Seek(1);
        var at1 = preview.Capture();
        preview.Seek(3);
        preview.AddCheckpoint(preview.Capture());
        preview.AddCheckpoint(at1);
        preview.AddCheckpoint(at1);
        Assert.Equal(new long[] { 0, 60, 180 }, preview.Checkpoints.Select(c => c.Steps).ToArray());
        Assert.Equal(60, preview.CheckpointAtOrBefore(179).Steps);
        Assert.Equal(180, preview.CheckpointAtOrBefore(500).Steps);
        Assert.Equal(0, preview.CheckpointAtOrBefore(59).Steps);
    }

    [Fact]
    public void Low_frame_rate_shows_only_its_frames()
    {
        var preview = Preview(new PreviewSettings { LowFps = 16 });
        int changes = 0;
        for (int i = 0; i < 60; i++)
            if (preview.Advance(1f / 60))
                changes++;
        // One real second at 60 Hz: the shown frame changes 16 times, always on a multiple of 1/16 s.
        Assert.Equal(16, changes);
        Assert.True(MathF.Abs(preview.ShownTime * 16 - MathF.Round(preview.ShownTime * 16)) < 1e-3f, preview.ShownTime.ToString());
    }

    [Fact]
    public void Camera_projects_like_the_P3_probe_camera()
    {
        // trail.py: x = 960 + f (q.r)/z, y = 540 - f (q.u)/z with r = (d.z, 0, -d.x), u = d x r, f = 719 at 1080 lines.
        var camera = new Camera { Fov = Camera.FovFromFocal(719, 1080) };
        var eye = new Vector3(0, 8.6598f, -6);
        var look = new Vector3(0, 8.88f, 8);
        camera.LookAt(eye, look);
        var vp = camera.View() * camera.Projection(1920f / 1080f);
        var d = Vector3.Normalize(look - eye);
        var r = Vector3.Normalize(new Vector3(d.Z, 0, -d.X));
        var u = Vector3.Cross(d, r);
        foreach (var p in new[] { new Vector3(1.5f, 9.3f, 8), new Vector3(-2, 7, 4), new Vector3(0.3f, 10.5f, 12) })
        {
            var q = p - eye;
            float z = Vector3.Dot(q, d);
            var expected = new Vector2(960 + 719 * Vector3.Dot(q, r) / z, 540 - 719 * Vector3.Dot(q, u) / z);
            var c = Vector4.Transform(new Vector4(p, 1), vp);
            var actual = new Vector2(960 + c.X / c.W * 960, 540 - c.Y / c.W * 540);
            Assert.True(Vector2.Distance(expected, actual) < 0.01f, $"{expected} vs {actual}");
            Assert.InRange(c.Z / c.W, 0, 1);
        }
    }

    [Fact]
    public void Camera_controls_move_as_named()
    {
        var c = new Camera { Target = new Vector3(1, 2, 3), Yaw = 0, Pitch = 0, Distance = 10 };
        Assert.Equal(new Vector3(1, 2, -7), c.Eye);

        var eye = c.Eye;
        c.Look(30, 10);
        Assert.True(Vector3.Distance(eye, c.Eye) < 1e-4f, "look keeps the eye");
        Assert.Equal(30, c.Yaw, 3);

        c.Orbit(0, 500);
        Assert.Equal(89.9f, c.Pitch, 3);

        var target = c.Target;
        var (right, up, forward) = c.Basis();
        c.Pan(2, 1);
        Assert.True(Vector3.Distance(target + right * 2 + up, c.Target) < 1e-4f);
        Assert.True(MathF.Abs(Vector3.Dot(right, forward)) < 1e-5f && MathF.Abs(Vector3.Dot(up, forward)) < 1e-5f);

        c.Zoom(0.5f);
        Assert.Equal(5, c.Distance, 3);
        target = c.Target;
        c.Fly(new Vector3(0, 0, 1));
        Assert.True(Vector3.Distance(target + c.Forward, c.Target) < 1e-4f);

        // Top: north up and east right on screen; side: from the east looking west; eye level: 1.7 m, level.
        c.Apply(ViewPreset.Top, Vector3.Zero);
        var (r, u, _) = c.Basis();
        Assert.True(r.X > 0.99f && u.Z > 0.99f, $"{r} {u}");
        c.Apply(ViewPreset.Side, Vector3.Zero);
        Assert.True(c.Forward.X < -0.99f);
        c.Apply(ViewPreset.Eye, new Vector3(4, 0, 4));
        Assert.Equal(Camera.EyeHeight, c.Eye.Y, 4);
        c.Fov = 500;
        Assert.Equal(Camera.MaxFov, c.Fov);
    }

    [Fact]
    public void Material_reads_texture_paths_with_flags_and_the_capture_defaults()
    {
        string path = Path.Combine(Path.GetTempPath(), "pfx_render_test.emat");
        File.WriteAllText(path, "Particle {\n AlbedoMap \"{016B7879D4775122}Graphics/Particles/sprites/spark.edds\" alpha \n Emissive 1 1 1 0\n BlendMode Additive_AlphaModulate\n ZEnable 0\n TileU 4\n}");
        try
        {
            var m = MaterialInfo.Read(path);
            Assert.Equal("{016B7879D4775122}Graphics/Particles/sprites/spark.edds", m.AlbedoMap);
            Assert.True(m.Additive);
            Assert.False(m.ZEnable);
            Assert.Equal(new Vector3(1, 1, 1), m.Color);
            Assert.Equal(new Vector3(1, 1, 1), m.Emissive);
            Assert.Equal(1, m.CameraBlendFar);
            Assert.Equal(0, m.CameraBlendNear);
            Assert.Equal(1, m.BidirLightScale);
            Assert.Equal(1, m.LightScale);
            Assert.Equal((4, 1), (m.TileU, m.TileV));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Material_reads_a_numeric_blend_mode_as_the_schema_index()
    {
        string path = Path.Combine(Path.GetTempPath(), "pfx_render_blend_test.emat");
        try
        {
            File.WriteAllText(path, "Particle {\n Emissive 1 1 1 0\n BlendMode 2\n}");
            Assert.True(MaterialInfo.Read(path).Additive);
            File.WriteAllText(path, "Particle {\n BlendMode 0\n}");
            Assert.False(MaterialInfo.Read(path).Additive);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Viewport_shaders_compile()
    {
        var code = Gpu.ShaderBytecode;
        foreach (var entry in new[] { "VS", "PS", "MeshVS", "MeshPS", "LineVS", "LinePS", "OverlayVS", "OverlayPS" })
            Assert.True(code[entry].Length > 0, entry);
    }
}
