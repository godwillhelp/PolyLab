using System.Globalization;
using System.Numerics;
using Pfx.Render;
using Pfx.Sim;

namespace PfxView;

// Command line. Without --shot / --bench / --selftest the window opens.
//   PfxView [effect.ptc ...] [--data "root1;root2"] [--lang ru|en|uk]
//   scene:   --time s  --origin x,y,z  --yaw deg  --turn deg/s  --circle radius,speed,X|Y|Z  --seed n  --step s
//            --copies n  --spacing m  --lowfps fps
//            (scene options after an effect apply to that effect only, before the first effect to all of them)
//   view:    --eye x,y,z  --look x,y,z  --fov deg | --focal px  --sky noon|dusk|night  --no-ground  --no-fog
//            --helper box|player  --gizmos  --size WxH
//   --shot out.png [--empty] [--report out.txt]   render one frame offscreen (--empty: the same frame without
//                                     particles; --report: per emitter the material, textures and shader light terms)
//   --bench out.txt [--warmup s] [--frames n]   measure frame times in the window, write them, exit
//   --selftest out.txt                resize, minimise and device-loss checks in the window, write them, exit
//   --capture out.png [--after s]     the viewport frame with its overlay after s seconds of playback, then exit
public sealed class Launch
{
    public List<(string Path, PreviewSettings Settings)> Effects { get; } = new();
    public string Roots { get; private set; } = "";
    public string? Lang { get; private set; }
    public string? Shot { get; private set; }
    public string? Bench { get; private set; }
    public string? SelfTest { get; private set; }
    public string? Report { get; private set; }
    public string? Capture { get; private set; }
    public float After { get; private set; } = 3;
    public bool Empty { get; private set; }
    public float Time { get; private set; }
    public float Warmup { get; private set; } = 5;
    public int Frames { get; private set; } = 300;
    public int Width { get; private set; } = 1920;
    public int Height { get; private set; } = 1080;
    public Vector3? Eye { get; private set; }
    public Vector3? Look { get; private set; }
    public float? Fov { get; private set; }
    public float? Focal { get; private set; }
    public PreviewSettings Preview { get; private set; } = new();
    public ViewOptions View { get; private set; } = new();

    public static Launch Parse(string[] args)
    {
        var l = new Launch();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
            float F() => float.Parse(Next(), CultureInfo.InvariantCulture);
            switch (a)
            {
                case "--data": l.Roots = Next(); break;
                case "--lang": l.Lang = Next(); break;
                case "--shot": l.Shot = Next(); break;
                case "--bench": l.Bench = Next(); break;
                case "--selftest": l.SelfTest = Next(); break;
                case "--report": l.Report = Next(); break;
                case "--capture": l.Capture = Next(); break;
                case "--after": l.After = F(); break;
                case "--empty": l.Empty = true; break;
                case "--time": l.Time = F(); break;
                case "--warmup": l.Warmup = F(); break;
                case "--frames": l.Frames = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--size":
                    var wh = Next().Split('x');
                    l.Width = int.Parse(wh[0], CultureInfo.InvariantCulture);
                    l.Height = int.Parse(wh[1], CultureInfo.InvariantCulture);
                    break;
                case "--eye": l.Eye = Vec(Next()); break;
                case "--look": l.Look = Vec(Next()); break;
                case "--fov": l.Fov = F(); break;
                case "--focal": l.Focal = F(); break;
                case "--origin":
                    var origin = Vec(Next());
                    l.Scene(s => s with { Origin = origin });
                    break;
                case "--yaw":
                    float yaw = F();
                    l.Scene(s => s with { Yaw = yaw });
                    break;
                case "--turn":
                    float turn = F();
                    l.Scene(s => s with { Motion = MotionKind.Turn, TurnRate = turn });
                    break;
                case "--circle":
                    var c = Next().Split(',');
                    l.Scene(s => s with
                    {
                        Motion = MotionKind.Circle,
                        CircleRadius = float.Parse(c[0], CultureInfo.InvariantCulture),
                        CircleSpeed = float.Parse(c[1], CultureInfo.InvariantCulture),
                        CircleAxis = Enum.Parse<CircleAxis>(c[2], true),
                    });
                    break;
                case "--seed":
                    ulong seed = ulong.Parse(Next(), CultureInfo.InvariantCulture);
                    l.Scene(s => s with { Seed = seed });
                    break;
                case "--step":
                    float step = F();
                    l.Scene(s => s with { Step = step });
                    break;
                case "--copies":
                    int copies = int.Parse(Next(), CultureInfo.InvariantCulture);
                    l.Scene(s => s with { Copies = copies });
                    break;
                case "--spacing":
                    float spacing = F();
                    l.Scene(s => s with { Spacing = spacing });
                    break;
                case "--lowfps":
                    float fps = F();
                    l.Scene(s => s with { LowFps = fps });
                    break;
                case "--sky":
                    string name = Next();
                    l.View = l.View with { Sky = Sky.All.FirstOrDefault(s => s.Name == name) ?? throw new ArgumentException($"Sky '{name}'") };
                    break;
                case "--no-ground": l.View = l.View with { Ground = false }; break;
                case "--no-fog": l.View = l.View with { Fog = false }; break;
                case "--gizmos": l.View = l.View with { Gizmos = true }; break;
                case "--helper": l.View = l.View with { Helper = Enum.Parse<HelperModel>(Next(), true) }; break;
                default:
                    if (a.StartsWith("--"))
                        throw new ArgumentException($"Unknown option {a}");
                    l.Effects.Add((a, l.Preview));
                    break;
            }
        }
        return l;
    }

    private void Scene(Func<PreviewSettings, PreviewSettings> change)
    {
        if (Effects.Count == 0)
            Preview = change(Preview);
        else
            Effects[^1] = (Effects[^1].Path, change(Effects[^1].Settings));
    }

    public PreviewSettings FirstSettings => Effects.Count > 0 ? Effects[0].Settings : Preview;

    private static Vector3 Vec(string text)
    {
        var p = text.Split(',').Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        return p.Length == 3 ? new Vector3(p[0], p[1], p[2]) : throw new ArgumentException($"x,y,z expected: {text}");
    }

    public IEnumerable<string> RootList => Roots.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // Places the camera from --eye/--look/--fov/--focal; without them it frames the source.
    // --focal is in pixels at an image height of 1080 (the P3 calibration: 719 px), so it fixes the vertical angle.
    public void ApplyCamera(Camera camera)
    {
        if (Fov is { } fov)
            camera.Fov = fov;
        if (Focal is { } f)
            camera.Fov = Camera.FovFromFocal(f, 1080);
        if (Eye is { } eye)
            camera.LookAt(eye, Look ?? FirstSettings.Origin);
        else
            camera.Apply(ViewPreset.Free, FirstSettings.Origin + new Vector3(0, 1, 0));
    }
}
