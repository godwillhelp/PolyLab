namespace Pfx.Core.Export;

// The client runtime the DayZ-mod profile generates into the user's mod (spec 3.13, O5): the same text for every
// project except the prefix and the parts a project does not use, which are left out. Held to the CG mod rules
// (spec 8.1): no comments, super always called, every name prefixed, no timers (the clock is the frame time of the
// update queue), every timing comes from the project's data.
public static class DayzRuntime
{
    [Flags]
    public enum Uses
    {
        None = 0,
        Lights = 1,
        Sounds = 2,
        Spin = 4,
        Near = 8,
        Stops = 16,
        Paths = 32,
        Strikes = 64,
        Tint = 128,
        Curves = 256,
        Anims = 512,
        Flash = 1024,
        Around = 2048,
    }

    // own_tornado JET_HEADING (CG_AnomalyShowcase): an around-dust emitter's flat cone blows along heading − 270.
    public const int JetHeading = 270;

    // 4_World: definitions, the light class (an entity: the engine creates it by name, so it must be in World) and
    // the player.
    public static string World(string p, Uses u)
    {
        var w = new CodeWriter();
        Defs(w, p, u);
        if (u.HasFlag(Uses.Lights))
            Light(w, p, u);
        Run(w, p, u);
        Player(w, p, u);
        return w.ToString();
    }

    private static void Defs(CodeWriter w, string p, Uses u)
    {
        w.Open($"class {p}_FxPartDef");
        w.Line("int m_Id;");
        w.Line("float m_Delay;");
        if (u.HasFlag(Uses.Spin))
            w.Line("float m_Spin;");
        if (u.HasFlag(Uses.Near))
            w.Line("float m_Near;");
        if (u.HasFlag(Uses.Stops))
            w.Line("float m_Stop;");
        if (u.HasFlag(Uses.Around))
        {
            w.Line("float m_Around;");
            w.Line("float m_Swirl;");
            w.Line("float m_SwirlMin;");
        }
        if (u.HasFlag(Uses.Paths))
        {
            w.Line("ref array<float> m_PathTimes = new array<float>();");
            w.Line("ref array<vector> m_PathPoints = new array<vector>();");
            w.Line("bool m_YawAlong;");
            w.Blank();
            w.Open("vector PathAt(float t, out float yaw)");
            w.Line("int n = m_PathTimes.Count();");
            w.Line("yaw = 0;");
            w.Line("if (n == 0)");
            w.Line("\treturn vector.Zero;");
            w.Line("if (n == 1)");
            w.Line("\treturn m_PathPoints[0];");
            w.Line("float period = m_PathTimes[n - 1];");
            w.Line("float at = 0;");
            w.Line("if (period > 0)");
            w.Line("\tat = Math.ModFloat(t, period);");
            w.Line("int i = 0;");
            w.Line("while (i < n - 2 && m_PathTimes[i + 1] <= at)");
            w.Line("\ti++;");
            w.Line("float span = m_PathTimes[i + 1] - m_PathTimes[i];");
            w.Line("float k = 0;");
            w.Line("if (span > 0)");
            w.Line("\tk = Math.Clamp((at - m_PathTimes[i]) / span, 0, 1);");
            w.Line("vector a = m_PathPoints[i];");
            w.Line("vector b = m_PathPoints[i + 1];");
            w.Line("vector d = b - a;");
            w.Line("if (d[0] != 0 || d[2] != 0)");
            w.Line("\tyaw = Math.Atan2(d[0], d[2]) * Math.RAD2DEG;");
            w.Line("return a + d * k;");
            w.Close();
        }
        w.Close();
        w.Blank();

        if (u.HasFlag(Uses.Lights))
        {
            w.Open($"class {p}_FxLightDef");
            foreach (var f in new[] { "float m_Delay;", "float m_Radius;", "float m_Brightness;", "vector m_Color;", "vector m_Offset;", "float m_FlickerAmp;",
                         "float m_FlickerSpeed;", "float m_Life;", "float m_Fade;", "bool m_Shadow;", "float m_HazeRadius;", "float m_HazePower;" })
                w.Line(f);
            if (u.HasFlag(Uses.Curves))
            {
                w.Line("ref array<float> m_KeyTimes = new array<float>();");
                w.Line("ref array<float> m_KeyValues = new array<float>();");
                w.Line("bool m_KeyLoop;");
            }
            if (u.HasFlag(Uses.Anims))
            {
                w.Line("ref array<vector> m_Frames = new array<vector>();");
                w.Line("float m_Fps;");
            }
            if (u.HasFlag(Uses.Curves))
            {
                w.Blank();
                w.Open("float CurveAt(float t)");
                w.Line("int n = m_KeyTimes.Count();");
                w.Line("if (n == 0)");
                w.Line("\treturn 1;");
                w.Line("float at = t;");
                w.Line("float period = m_KeyTimes[n - 1];");
                w.Line("if (m_KeyLoop && period > 0)");
                w.Line("\tat = Math.ModFloat(t, period);");
                w.Line("if (at <= m_KeyTimes[0])");
                w.Line("\treturn m_KeyValues[0];");
                w.Line("for (int i = 1; i < n; i++)");
                w.Open();
                w.Line("if (at <= m_KeyTimes[i])");
                w.Open();
                w.Line("float span = m_KeyTimes[i] - m_KeyTimes[i - 1];");
                w.Line("if (span <= 0)");
                w.Line("\treturn m_KeyValues[i];");
                w.Line("float k = (at - m_KeyTimes[i - 1]) / span;");
                w.Line("return m_KeyValues[i - 1] + (m_KeyValues[i] - m_KeyValues[i - 1]) * k;");
                w.Close();
                w.Close();
                w.Line("return m_KeyValues[n - 1];");
                w.Close();
            }
            w.Close();
            w.Blank();
        }

        if (u.HasFlag(Uses.Sounds))
        {
            w.Open($"class {p}_FxSoundDef");
            w.Line("string m_Set;");
            w.Line("float m_Delay;");
            w.Line("bool m_Loop;");
            w.Close();
            w.Blank();
        }

        if (u.HasFlag(Uses.Strikes))
        {
            w.Open($"class {p}_FxStrikeDef");
            foreach (var f in new[] { "int m_Id;", "float m_Delay;", "float m_Height;", "float m_RadiusLow;", "float m_RadiusHigh;", "float m_GapMin;", "float m_GapMax;",
                         "float m_Duration;", "float m_LightRadius;", "float m_LightBrightness;" })
                w.Line(f);
            w.Close();
            w.Blank();
        }

        if (u.HasFlag(Uses.Flash))
        {
            w.Open($"class {p}_FxFlashDef");
            w.Line("vector m_At;");
            w.Line("float m_Full;");
            w.Line("float m_Max;");
            w.Line("float m_Delay;");
            w.Close();
            w.Blank();
        }

        if (u.HasFlag(Uses.Tint))
        {
            w.Open($"class {p}_FxTintDef");
            w.Line("vector m_Color;");
            w.Line("float m_Radius;");
            w.Line("float m_Fade;");
            w.Line("float m_Delay;");
            w.Close();
            w.Blank();
        }

        w.Open($"class {p}_FxStateDef");
        w.Line("string m_Name;");
        w.Line("float m_Duration;");
        w.Line("bool m_OverIdle;");
        w.Line($"ref array<ref {p}_FxPartDef> m_Parts = new array<ref {p}_FxPartDef>();");
        if (u.HasFlag(Uses.Lights))
            w.Line($"ref array<ref {p}_FxLightDef> m_Lights = new array<ref {p}_FxLightDef>();");
        if (u.HasFlag(Uses.Sounds))
            w.Line($"ref array<ref {p}_FxSoundDef> m_Sounds = new array<ref {p}_FxSoundDef>();");
        if (u.HasFlag(Uses.Strikes))
            w.Line($"ref array<ref {p}_FxStrikeDef> m_Strikes = new array<ref {p}_FxStrikeDef>();");
        if (u.HasFlag(Uses.Tint))
            w.Line($"ref array<ref {p}_FxTintDef> m_Tints = new array<ref {p}_FxTintDef>();");
        if (u.HasFlag(Uses.Flash))
            w.Line($"ref array<ref {p}_FxFlashDef> m_Flashes = new array<ref {p}_FxFlashDef>();");
        w.Blank();
        w.Open($"{p}_FxPartDef AddPart(int id, float delay)");
        w.Line($"{p}_FxPartDef d = new {p}_FxPartDef();");
        w.Line("d.m_Id = id;");
        w.Line("d.m_Delay = delay;");
        w.Line("m_Parts.Insert(d);");
        w.Line("return d;");
        w.Close();
        if (u.HasFlag(Uses.Lights))
        {
            w.Blank();
            w.Open($"{p}_FxLightDef AddLight(float delay, float radius, float brightness, vector color, vector offset)");
            w.Line($"{p}_FxLightDef d = new {p}_FxLightDef();");
            w.Line("d.m_Delay = delay;");
            w.Line("d.m_Radius = radius;");
            w.Line("d.m_Brightness = brightness;");
            w.Line("d.m_Color = color;");
            w.Line("d.m_Offset = offset;");
            w.Line("m_Lights.Insert(d);");
            w.Line("return d;");
            w.Close();
        }
        if (u.HasFlag(Uses.Sounds))
        {
            w.Blank();
            w.Open($"void AddSound(string soundSet, float delay, bool loop)");
            w.Line($"{p}_FxSoundDef d = new {p}_FxSoundDef();");
            w.Line("d.m_Set = soundSet;");
            w.Line("d.m_Delay = delay;");
            w.Line("d.m_Loop = loop;");
            w.Line("m_Sounds.Insert(d);");
            w.Close();
        }
        if (u.HasFlag(Uses.Strikes))
        {
            w.Blank();
            w.Open($"{p}_FxStrikeDef AddStrike(int id, float delay)");
            w.Line($"{p}_FxStrikeDef d = new {p}_FxStrikeDef();");
            w.Line("d.m_Id = id;");
            w.Line("d.m_Delay = delay;");
            w.Line("m_Strikes.Insert(d);");
            w.Line("return d;");
            w.Close();
        }
        if (u.HasFlag(Uses.Tint))
        {
            w.Blank();
            w.Open($"void AddTint(vector color, float radius, float fade, float delay)");
            w.Line($"{p}_FxTintDef d = new {p}_FxTintDef();");
            w.Line("d.m_Color = color;");
            w.Line("d.m_Radius = radius;");
            w.Line("d.m_Fade = fade;");
            w.Line("d.m_Delay = delay;");
            w.Line("m_Tints.Insert(d);");
            w.Close();
        }
        if (u.HasFlag(Uses.Flash))
        {
            w.Blank();
            w.Open($"void AddFlash(vector at, float full, float max, float delay)");
            w.Line($"{p}_FxFlashDef d = new {p}_FxFlashDef();");
            w.Line("d.m_At = at;");
            w.Line("d.m_Full = full;");
            w.Line("d.m_Max = max;");
            w.Line("d.m_Delay = delay;");
            w.Line("m_Flashes.Insert(d);");
            w.Close();
        }
        w.Close();
        w.Blank();

        w.Open($"class {p}_FxDef");
        w.Line($"ref array<ref {p}_FxStateDef> m_States = new array<ref {p}_FxStateDef>();");
        w.Blank();
        w.Open($"{p}_FxStateDef AddState(string name, float duration, bool overIdle)");
        w.Line($"{p}_FxStateDef s = new {p}_FxStateDef();");
        w.Line("s.m_Name = name;");
        w.Line("s.m_Duration = duration;");
        w.Line("s.m_OverIdle = overIdle;");
        w.Line("m_States.Insert(s);");
        w.Line("return s;");
        w.Close();
        w.Blank();
        w.Open("int IndexOf(string name)");
        w.Line("for (int i = 0; i < m_States.Count(); i++)");
        w.Open();
        w.Line("if (m_States[i].m_Name == name)");
        w.Line("\treturn i;");
        w.Close();
        w.Line("return -1;");
        w.Close();
        w.Close();
        w.Blank();
    }

    private static void Light(CodeWriter w, string p, Uses u)
    {
        w.Open($"class {p}_FxLight extends PointLightBase");
        w.Line($"protected {p}_FxLightDef m_{p}Def;");
        w.Line($"protected float m_{p}Clock;");
        w.Line($"protected bool m_{p}Fading;");
        w.Blank();
        w.Open($"void {p}Begin({p}_FxLightDef def, float since)");
        w.Line($"m_{p}Def = def;");
        w.Line($"m_{p}Clock = since;");
        w.Close();
        w.Blank();
        w.Open($"void {p}Fade(float time)");
        w.Line($"if (m_{p}Fading)");
        w.Line("\treturn;");
        w.Line($"m_{p}Fading = true;");
        w.Line("if (time > 0)");
        w.Line("\tFadeOut(time);");
        w.Line("else");
        w.Line("\tDestroy();");
        w.Close();
        w.Blank();
        w.Open("override void OnFrameLightSource(IEntity other, float timeSlice)");
        w.Line("super.OnFrameLightSource(other, timeSlice);");
        w.Line($"if (!m_{p}Def || m_{p}Fading)");
        w.Line("\treturn;");
        w.Line($"m_{p}Clock += timeSlice;");
        if (u.HasFlag(Uses.Curves))
        {
            w.Line($"if (m_{p}Def.m_KeyTimes.Count() > 0)");
            w.Line($"\tSetBrightnessTo(m_{p}Def.m_Brightness * m_{p}Def.CurveAt(m_{p}Clock));");
        }
        if (u.HasFlag(Uses.Anims))
        {
            w.Line($"int frames = m_{p}Def.m_Frames.Count();");
            w.Line($"if (frames > 0 && m_{p}Def.m_Fps > 0)");
            w.Open();
            w.Line($"int frame = ((int)(m_{p}Clock * m_{p}Def.m_Fps)) % frames;");
            w.Line($"vector color = m_{p}Def.m_Frames[frame];");
            w.Line("SetDiffuseColorV(color);");
            w.Line("SetAmbientColorV(color);");
            w.Close();
        }
        w.Close();
        w.Close();
        w.Blank();
    }

    // One started state: its live sources, lights and sounds, by the index of their definitions.
    private static void Run(CodeWriter w, string p, Uses u)
    {
        w.Open($"class {p}_FxRun");
        w.Line($"{p}_FxStateDef m_Def;");
        w.Line("float m_Clock;");
        w.Line("float m_Since;");
        w.Line("ref array<bool> m_PartDone = new array<bool>();");
        w.Line("ref array<ParticleSource> m_Sources = new array<ParticleSource>();");
        if (u.HasFlag(Uses.Near))
            w.Line("ref array<bool> m_NearIn = new array<bool>();");
        if (u.HasFlag(Uses.Lights))
        {
            w.Line("ref array<bool> m_LightDone = new array<bool>();");
            w.Line($"ref array<{p}_FxLight> m_Lights = new array<{p}_FxLight>();");
        }
        if (u.HasFlag(Uses.Sounds))
        {
            w.Line("ref array<bool> m_SoundDone = new array<bool>();");
            w.Line("ref array<EffectSound> m_Loops = new array<EffectSound>();");
        }
        if (u.HasFlag(Uses.Strikes))
        {
            w.Line("ref array<float> m_StrikeNext = new array<float>();");
            w.Line("ref array<float> m_StrikeEnd = new array<float>();");
            w.Line("ref array<ParticleSource> m_StrikeSources = new array<ParticleSource>();");
        }
        if (u.HasFlag(Uses.Tint))
            w.Line("bool m_Tinted;");
        if (u.HasFlag(Uses.Flash))
            w.Line("ref array<bool> m_FlashDone = new array<bool>();");
        w.Blank();
        w.Open($"void {p}_FxRun({p}_FxStateDef def, float since)");
        w.Line("m_Def = def;");
        w.Line("m_Clock = since;");
        w.Line("m_Since = since;");
        w.Line("for (int i = 0; i < def.m_Parts.Count(); i++)");
        w.Open();
        w.Line("m_PartDone.Insert(false);");
        w.Line("m_Sources.Insert(null);");
        if (u.HasFlag(Uses.Near))
            w.Line("m_NearIn.Insert(true);");
        w.Close();
        if (u.HasFlag(Uses.Lights))
        {
            w.Line("for (int j = 0; j < def.m_Lights.Count(); j++)");
            w.Open();
            w.Line("m_LightDone.Insert(false);");
            w.Line("m_Lights.Insert(null);");
            w.Close();
        }
        if (u.HasFlag(Uses.Sounds))
        {
            w.Line("for (int k = 0; k < def.m_Sounds.Count(); k++)");
            w.Line("\tm_SoundDone.Insert(false);");
        }
        if (u.HasFlag(Uses.Strikes))
        {
            w.Line("for (int s = 0; s < def.m_Strikes.Count(); s++)");
            w.Open();
            w.Line($"{p}_FxStrikeDef strike = def.m_Strikes[s];");
            w.Line("m_StrikeNext.Insert(strike.m_Delay + Math.RandomFloat(strike.m_GapMin, strike.m_GapMax));");
            w.Line("m_StrikeEnd.Insert(0);");
            w.Line("m_StrikeSources.Insert(null);");
            w.Close();
        }
        if (u.HasFlag(Uses.Flash))
        {
            w.Line("for (int f = 0; f < def.m_Flashes.Count(); f++)");
            w.Line("\tm_FlashDone.Insert(false);");
        }
        w.Close();
        w.Close();
        w.Blank();
    }

    private static void Player(CodeWriter w, string p, Uses u)
    {
        w.Open($"class {p}_Fx");
        w.Line($"protected {p}_FxDef m_Def;");
        w.Line("protected vector m_Pos;");
        w.Line("protected vector m_Ori;");
        w.Line("protected int m_State = -1;");
        w.Line("protected bool m_Cycle;");
        w.Line("protected bool m_Ticking;");
        w.Line($"protected ref {p}_FxRun m_Base;");
        w.Line($"protected ref {p}_FxRun m_Over;");
        w.Blank();
        w.Open($"void {p}_Fx(string effect, vector pos, vector ori = \"0 0 0\")");
        w.Line($"m_Def = {p}_FxDefs.Get(effect);");
        w.Line("m_Pos = pos;");
        w.Line("m_Ori = ori;");
        w.Close();
        w.Blank();
        w.Open($"void ~{p}_Fx()");
        w.Line("FxStopAll();");
        w.Close();
        w.Blank();
        w.Open("bool IsPlaying()");
        w.Line("return m_State >= 0;");
        w.Close();
        w.Blank();
        w.Open("string StateName()");
        w.Line("if (m_State < 0)");
        w.Line("\treturn \"\";");
        w.Line("return m_Def.m_States[m_State].m_Name;");
        w.Close();
        w.Blank();
        w.Open("void Play(string state, float since = 0, bool cycle = false)");
        w.Line("if (!m_Def || g_Game.IsDedicatedServer())");
        w.Line("\treturn;");
        w.Line("int index = m_Def.IndexOf(state);");
        w.Line("if (index < 0)");
        w.Line("\treturn;");
        w.Line("m_Cycle = cycle;");
        w.Line("FxEnter(index, since);");
        w.Line("if (!m_Ticking)");
        w.Open();
        w.Line("m_Ticking = true;");
        w.Line("g_Game.GetUpdateQueue(CALL_CATEGORY_GAMEPLAY).Insert(FxTick);");
        w.Close();
        w.Close();
        w.Blank();
        w.Open("void Stop()");
        w.Line("FxStopAll();");
        w.Close();
        w.Blank();
        w.Open("protected void FxStopAll()");
        w.Line("if (m_Ticking && g_Game)");
        w.Line("\tg_Game.GetUpdateQueue(CALL_CATEGORY_GAMEPLAY).Remove(FxTick);");
        w.Line("m_Ticking = false;");
        w.Line("FxEndRun(m_Over);");
        w.Line("FxEndRun(m_Base);");
        w.Line("m_Over = null;");
        w.Line("m_Base = null;");
        w.Line("m_State = -1;");
        w.Close();
        w.Blank();
        w.Open("protected void FxEnter(int index, float since)");
        w.Line($"{p}_FxStateDef def = m_Def.m_States[index];");
        w.Line("FxEndRun(m_Over);");
        w.Line("m_Over = null;");
        w.Line("if (def.m_OverIdle && index > 0 && m_Base && m_Base.m_Def == m_Def.m_States[0])");
        w.Line($"\tm_Over = new {p}_FxRun(def, since);");
        w.Line("else");
        w.Open();
        w.Line("FxEndRun(m_Base);");
        w.Line($"m_Base = new {p}_FxRun(def, since);");
        w.Close();
        w.Line("m_State = index;");
        w.Line("FxAdvance(m_Base, 0);");
        w.Line("FxAdvance(m_Over, 0);");
        w.Close();
        w.Blank();
        w.Open("protected void FxTick(float timeslice)");
        w.Line("FxAdvance(m_Base, timeslice);");
        w.Line("FxAdvance(m_Over, timeslice);");
        w.Line("if (!m_Cycle || m_State < 0)");
        w.Line("\treturn;");
        w.Line($"{p}_FxStateDef def = m_Def.m_States[m_State];");
        w.Line($"{p}_FxRun current = m_Base;");
        w.Line("if (m_Over)");
        w.Line("\tcurrent = m_Over;");
        w.Line("if (def.m_Duration > 0 && current.m_Clock >= def.m_Duration)");
        w.Line("\tFxEnter((m_State + 1) % m_Def.m_States.Count(), 0);");
        w.Close();
        w.Blank();

        // Every due item of a run starts; script motion, near switches and hard stops apply.
        w.Open($"protected void FxAdvance({p}_FxRun run, float dt)");
        w.Line("if (!run)");
        w.Line("\treturn;");
        w.Line("run.m_Clock += dt;");
        w.Line("float t = run.m_Clock;");
        w.Line($"{p}_FxStateDef def = run.m_Def;");
        if (u.HasFlag(Uses.Near))
            w.Line("float camSq = vector.DistanceSq(g_Game.GetCurrentCameraPosition(), m_Pos);");
        w.Line("for (int i = 0; i < def.m_Parts.Count(); i++)");
        w.Open();
        w.Line($"{p}_FxPartDef part = def.m_Parts[i];");
        if (u.HasFlag(Uses.Around))
        {
            w.Line("if (part.m_Around > 0)");
            w.Open();
            w.Line("FxAround(run, i, part, t);");
            w.Line("continue;");
            w.Close();
        }
        w.Line("if (!run.m_PartDone[i] && t >= part.m_Delay)");
        w.Open();
        w.Line("run.m_PartDone[i] = true;");
        if (u.HasFlag(Uses.Stops))
        {
            w.Line("if (part.m_Stop <= 0 || t < part.m_Delay + part.m_Stop)");
            w.Line("\trun.m_Sources[i] = ParticleManager.GetInstance().CreateParticle(part.m_Id, m_Pos, true, null, m_Ori, false, this);");
        }
        else
        {
            w.Line("run.m_Sources[i] = ParticleManager.GetInstance().CreateParticle(part.m_Id, m_Pos, true, null, m_Ori, false, this);");
        }
        w.Close();
        w.Line("ParticleSource ps = run.m_Sources[i];");
        w.Line("if (!ps)");
        w.Line("\tcontinue;");
        w.Line("if (ps.GetOwner() != this)");
        w.Open();
        w.Line("run.m_Sources[i] = null;");
        w.Line("continue;");
        w.Close();
        if (u.HasFlag(Uses.Stops))
        {
            w.Line("if (part.m_Stop > 0 && t >= part.m_Delay + part.m_Stop)");
            w.Open();
            w.Line("ps.StopParticle(StopParticleFlags.IMMEDIATE);");
            w.Line("run.m_Sources[i] = null;");
            w.Line("continue;");
            w.Close();
        }
        if (u.HasFlag(Uses.Paths))
        {
            w.Line("if (part.m_PathTimes.Count() > 0)");
            w.Open();
            w.Line("float heading;");
            w.Line("vector along = part.PathAt(t - part.m_Delay, heading);");
            w.Line("ps.SetPosition(m_Pos + along);");
            w.Line("if (part.m_YawAlong)");
            w.Open();
            w.Line("vector turn = m_Ori;");
            w.Line("turn[0] = heading;");
            w.Line("ps.SetOrientation(turn);");
            w.Close();
            w.Close();
        }
        if (u.HasFlag(Uses.Spin))
        {
            w.Line("if (part.m_Spin != 0)");
            w.Open();
            w.Line("vector ori = ps.GetOrientation();");
            w.Line("ori[0] = Math.ModFloat(ori[0] + part.m_Spin * dt, 360);");
            w.Line("ps.SetOrientation(ori);");
            w.Close();
        }
        if (u.HasFlag(Uses.Near))
        {
            w.Line("if (part.m_Near > 0)");
            w.Open();
            w.Line("bool inside = camSq <= part.m_Near * part.m_Near;");
            w.Line("if (inside != run.m_NearIn[i])");
            w.Open();
            w.Line("run.m_NearIn[i] = inside;");
            w.Line("float births = 0;");
            w.Line("if (inside)");
            w.Line("\tbirths = 1;");
            w.Line("ps.ScaleParticleParamFromOriginal(EmitorParam.BIRTH_RATE, births);");
            w.Close();
            w.Close();
        }
        w.Close();

        if (u.HasFlag(Uses.Lights))
        {
            w.Line("for (int j = 0; j < def.m_Lights.Count(); j++)");
            w.Open();
            w.Line($"{p}_FxLightDef light = def.m_Lights[j];");
            w.Line("if (!run.m_LightDone[j] && t >= light.m_Delay)");
            w.Open();
            w.Line("run.m_LightDone[j] = true;");
            w.Line("if (light.m_Life <= 0 || t < light.m_Delay + light.m_Life)");
            w.Line($"\trun.m_Lights[j] = FxSpawnLight(light, t - light.m_Delay);");
            w.Close();
            w.Line($"{p}_FxLight lit = run.m_Lights[j];");
            w.Line("if (lit && light.m_Life > 0 && t >= light.m_Delay + light.m_Life)");
            w.Open();
            w.Line($"lit.{p}Fade(light.m_Fade);");
            w.Line("run.m_Lights[j] = null;");
            w.Close();
            w.Close();
        }

        if (u.HasFlag(Uses.Sounds))
        {
            w.Line("for (int k = 0; k < def.m_Sounds.Count(); k++)");
            w.Open();
            w.Line($"{p}_FxSoundDef sound = def.m_Sounds[k];");
            w.Line("if (run.m_SoundDone[k] || t < sound.m_Delay)");
            w.Line("\tcontinue;");
            w.Line("run.m_SoundDone[k] = true;");
            w.Line("if (!sound.m_Loop && sound.m_Delay < run.m_Since)");
            w.Line("\tcontinue;");
            w.Line("EffectSound es = SEffectManager.PlaySound(sound.m_Set, m_Pos, 0, 0, sound.m_Loop);");
            w.Line("if (!es)");
            w.Line("\tcontinue;");
            w.Line("if (sound.m_Loop)");
            w.Line("\trun.m_Loops.Insert(es);");
            w.Line("else");
            w.Line("\tes.SetAutodestroy(true);");
            w.Close();
        }

        if (u.HasFlag(Uses.Strikes))
        {
            w.Line("for (int s = 0; s < def.m_Strikes.Count(); s++)");
            w.Open();
            w.Line($"{p}_FxStrikeDef strike = def.m_Strikes[s];");
            w.Line("ParticleSource bolt = run.m_StrikeSources[s];");
            w.Line("if (bolt && t >= run.m_StrikeEnd[s])");
            w.Open();
            w.Line("if (bolt.GetOwner() == this)");
            w.Line("\tbolt.StopParticle();");
            w.Line("run.m_StrikeSources[s] = null;");
            w.Line("bolt = null;");
            w.Close();
            w.Line("if (bolt || t < run.m_StrikeNext[s])");
            w.Line("\tcontinue;");
            w.Line("run.m_StrikeNext[s] = t + Math.RandomFloat(strike.m_GapMin, strike.m_GapMax);");
            w.Line("float h = Math.RandomFloat(0, strike.m_Height);");
            w.Line("float r = strike.m_RadiusLow;");
            w.Line("if (strike.m_Height > 0)");
            w.Line("\tr = strike.m_RadiusLow + (strike.m_RadiusHigh - strike.m_RadiusLow) * h / strike.m_Height;");
            w.Line("vector toCam = g_Game.GetCurrentCameraPosition() - m_Pos;");
            w.Line("toCam[1] = 0;");
            w.Line("toCam.Normalize();");
            w.Line("vector at = m_Pos + Vector(0, h, 0) + toCam * r;");
            w.Line("run.m_StrikeSources[s] = ParticleManager.GetInstance().CreateParticle(strike.m_Id, at, true, null, vector.Zero, false, this);");
            w.Line("run.m_StrikeEnd[s] = t + strike.m_Duration;");
            if (u.HasFlag(Uses.Lights))
            {
                w.Line($"{p}_FxLight flash = {p}_FxLight.Cast(ScriptedLightBase.CreateLight({p}_FxLight, at));");
                w.Line("if (flash)");
                w.Open();
                w.Line("flash.SetVisibleDuringDaylight(true);");
                w.Line("flash.SetFlareVisible(false);");
                w.Line("flash.SetRadiusTo(strike.m_LightRadius);");
                w.Line("flash.SetBrightnessTo(strike.m_LightBrightness);");
                w.Line("flash.SetDiffuseColorV(\"1 1 1\");");
                w.Line("flash.SetAmbientColorV(\"1 1 1\");");
                w.Line("flash.SetFadeOutTime(strike.m_Duration);");
                w.Line("flash.SetLifetime(strike.m_Duration);");
                w.Close();
            }
            w.Close();
        }

        if (u.HasFlag(Uses.Tint))
        {
            w.Line("for (int n = 0; n < def.m_Tints.Count(); n++)");
            w.Open();
            w.Line($"{p}_FxTintDef tint = def.m_Tints[n];");
            w.Line("bool wanted = false;");
            w.Line("PlayerBase player = PlayerBase.Cast(g_Game.GetPlayer());");
            w.Line("if (player && t >= tint.m_Delay)");
            w.Line("\twanted = vector.DistanceSq(player.GetPosition(), m_Pos) <= tint.m_Radius * tint.m_Radius;");
            w.Line("if (wanted != run.m_Tinted)");
            w.Open();
            w.Line("run.m_Tinted = wanted;");
            w.Line($"{p}_FxTint.{p}Show(wanted, tint.m_Color, tint.m_Fade);");
            w.Close();
            w.Close();
        }

        if (u.HasFlag(Uses.Flash))
        {
            w.Line("for (int f = 0; f < def.m_Flashes.Count(); f++)");
            w.Open();
            w.Line($"{p}_FxFlashDef flashDef = def.m_Flashes[f];");
            w.Line("if (run.m_FlashDone[f] || t < flashDef.m_Delay)");
            w.Line("\tcontinue;");
            w.Line("run.m_FlashDone[f] = true;");
            w.Line("if (flashDef.m_Delay >= run.m_Since)");
            w.Line("\tFxBlind(m_Pos + flashDef.m_At, flashDef.m_Full, flashDef.m_Max);");
            w.Close();
        }
        w.Close();
        w.Blank();

        if (u.HasFlag(Uses.Around))
        {
            w.Open($"protected void FxAround({p}_FxRun run, int i, {p}_FxPartDef part, float t)");
            w.Line("vector at = g_Game.GetCurrentCameraPosition();");
            w.Line("ParticleSource ps = run.m_Sources[i];");
            w.Line("if (t < part.m_Delay || vector.DistanceSq(at, m_Pos) > part.m_Around * part.m_Around)");
            w.Open();
            w.Line("if (ps && ps.GetOwner() == this)");
            w.Line("\tps.StopParticle();");
            w.Line("run.m_Sources[i] = null;");
            w.Line("return;");
            w.Close();
            w.Line("at[1] = g_Game.SurfaceY(at[0], at[2]);");
            w.Line("if (!ps)");
            w.Open();
            w.Line("ps = ParticleManager.GetInstance().CreateParticle(part.m_Id, at, true, null, vector.Zero, false, this);");
            w.Line("run.m_Sources[i] = ps;");
            w.Close();
            w.Line("if (!ps || ps.GetOwner() != this)");
            w.Open();
            w.Line("run.m_Sources[i] = null;");
            w.Line("return;");
            w.Close();
            w.Line("ps.SetPosition(at);");
            w.Line("if (part.m_Swirl == 0)");
            w.Line("\treturn;");
            w.Line("vector arm = at - m_Pos;");
            w.Line("float reach = Math.Max(Math.Sqrt(arm[0] * arm[0] + arm[2] * arm[2]), part.m_SwirlMin);");
            w.Line("float heading = Math.Atan2(part.m_Swirl * arm[2], -part.m_Swirl * arm[0]) * Math.RAD2DEG;");
            w.Line($"ps.SetOrientation(Vector(heading - {JetHeading}, 0, 0));");
            w.Line("ps.SetParameter(-1, EmitorParam.VELOCITY, Math.AbsFloat(part.m_Swirl) / reach);");
            w.Close();
            w.Blank();
        }

        if (u.HasFlag(Uses.Flash))
        {
            w.Open("protected void FxBlind(vector pos, float rangeFull, float rangeMax)");
            w.Line("PlayerBase player = PlayerBase.Cast(g_Game.GetPlayer());");
            w.Line("if (!player)");
            w.Line("\treturn;");
            w.Line("vector head = player.GetDamageZonePos(\"Head\");");
            w.Line("float dist = vector.Distance(head, pos);");
            w.Line("if (dist > rangeMax)");
            w.Line("\treturn;");
            w.Line("array<ref RaycastRVResult> results = new array<ref RaycastRVResult>();");
            w.Line("RaycastRVParams ray = new RaycastRVParams(pos, head);");
            w.Line("ray.flags = CollisionFlags.ALLOBJECTS;");
            w.Line("DayZPhysics.RaycastRVProxy(ray, results);");
            w.Line("Object first;");
            w.Line("foreach (RaycastRVResult result : results)");
            w.Open();
            w.Line("if (result.obj && !result.obj.IsInherited(ItemBase))");
            w.Open();
            w.Line("first = result.obj;");
            w.Line("break;");
            w.Close();
            w.Close();
            w.Line("if (!PlayerBase.Cast(first))");
            w.Line("\treturn;");
            w.Line("float coef = 1;");
            w.Line("if (rangeMax > rangeFull)");
            w.Line("\tcoef = 1 - (dist - rangeFull) / (rangeMax - rangeFull);");
            w.Line("coef = Math.Clamp(coef, 0.1, 100.0);");
            w.Line("player.OnPlayerReceiveFlashbangHitStart(MiscGameplayFunctions.IsPlayerOrientedTowardPos(player, pos, 60));");
            w.Line("player.GetFlashbangEffect().SetupFlashbangValues(coef, coef, coef);");
            w.Close();
            w.Blank();
        }

        if (u.HasFlag(Uses.Lights))
        {
            w.Open($"protected {p}_FxLight FxSpawnLight({p}_FxLightDef def, float since)");
            w.Line($"{p}_FxLight light = {p}_FxLight.Cast(ScriptedLightBase.CreateLight({p}_FxLight, m_Pos + def.m_Offset));");
            w.Line("if (!light)");
            w.Line("\treturn null;");
            w.Line("light.SetVisibleDuringDaylight(true);");
            w.Line("light.SetCastShadow(def.m_Shadow);");
            w.Line("light.SetFlareVisible(false);");
            w.Line("light.SetRadiusTo(def.m_Radius);");
            w.Line("light.SetBrightnessTo(def.m_Brightness);");
            w.Line("light.SetDiffuseColorV(def.m_Color);");
            w.Line("light.SetAmbientColorV(def.m_Color);");
            w.Line("light.SetFlickerAmplitude(def.m_FlickerAmp);");
            w.Line("light.SetFlickerSpeed(def.m_FlickerSpeed);");
            w.Line("if (def.m_HazeRadius > 0)");
            w.Open();
            w.Line("light.EnableHeatHaze(true);");
            w.Line("light.SetHeatHazeRadius(def.m_HazeRadius);");
            w.Line("light.SetHeatHazePower(def.m_HazePower);");
            w.Close();
            w.Line($"light.{p}Begin(def, since);");
            w.Line("return light;");
            w.Close();
            w.Blank();
        }

        w.Open($"protected void FxEndRun({p}_FxRun run)");
        w.Line("if (!run)");
        w.Line("\treturn;");
        w.Line("foreach (ParticleSource ps : run.m_Sources)");
        w.Open();
        w.Line("if (ps && ps.GetOwner() == this)");
        w.Line("\tps.StopParticle();");
        w.Close();
        w.Line("run.m_Sources.Clear();");
        if (u.HasFlag(Uses.Lights))
        {
            w.Line("for (int j = 0; j < run.m_Lights.Count(); j++)");
            w.Open();
            w.Line($"{p}_FxLight lit = run.m_Lights[j];");
            w.Line("if (lit)");
            w.Line($"\tlit.{p}Fade(run.m_Def.m_Lights[j].m_Fade);");
            w.Close();
            w.Line("run.m_Lights.Clear();");
        }
        if (u.HasFlag(Uses.Sounds))
        {
            w.Line("foreach (EffectSound es : run.m_Loops)");
            w.Open();
            w.Line("if (es)");
            w.Line("\tSEffectManager.DestroyEffect(es);");
            w.Close();
            w.Line("run.m_Loops.Clear();");
        }
        if (u.HasFlag(Uses.Strikes))
        {
            w.Line("foreach (ParticleSource bolt : run.m_StrikeSources)");
            w.Open();
            w.Line("if (bolt && bolt.GetOwner() == this)");
            w.Line("\tbolt.StopParticle(StopParticleFlags.IMMEDIATE);");
            w.Close();
            w.Line("run.m_StrikeSources.Clear();");
        }
        if (u.HasFlag(Uses.Tint))
        {
            w.Line("if (run.m_Tinted)");
            w.Line($"\t{p}_FxTint.{p}Show(false, \"1 1 1\", 0);");
        }
        w.Close();
        w.Close();
    }

    // 3_Game: the screen tint requester (whole screen, local player only), registered in the vanilla requester bank.
    public static string Tint(string p) =>
        new CodeWriter()
            .Open($"class {p}_FxTint extends PPERequester_GameplayBase")
            .Line("static const int LAYER = 210;")
            .Line($"float m_{p}Level;")
            .Line($"bool m_{p}Rising;")
            .Line($"vector m_{p}Color;")
            .Line($"float m_{p}Fade;")
            .Blank()
            .Open($"static void {p}Show(bool show, vector color, float fade)")
            .Line($"{p}_FxTint tint = {p}_FxTint.Cast(PPERequesterBank.GetRequester({p}_FxTint));")
            .Line("if (!tint)")
            .Line("\treturn;")
            .Line("if (show)")
            .Line($"\ttint.m_{p}Color = color;")
            .Line($"tint.m_{p}Fade = fade;")
            .Line($"tint.m_{p}Rising = show;")
            .Line("if (show && !tint.IsRequesterRunning())")
            .Line("\ttint.Start();")
            .Line("else if (tint.IsRequesterRunning())")
            .Line("\ttint.SetRequesterUpdating(true);")
            .Close()
            .Blank()
            .Open("override protected void OnUpdate(float delta)")
            .Line("super.OnUpdate(delta);")
            .Line("if (!IsRequesterRunning())")
            .Line("\treturn;")
            .Line("float step = 1;")
            .Line($"if (m_{p}Fade > 0)")
            .Line($"\tstep = delta / m_{p}Fade;")
            .Line($"float was = m_{p}Level;")
            .Line($"if (m_{p}Rising)")
            .Line($"\tm_{p}Level = Math.Min(m_{p}Level + step, 1);")
            .Line("else")
            .Line($"\tm_{p}Level = Math.Max(m_{p}Level - step, 0);")
            .Line($"if (m_{p}Rising && was >= 1)")
            .Line("\treturn;")
            .Line($"if (!m_{p}Rising && m_{p}Level <= 0)")
            .Open()
            .Line("Stop();")
            .Line("return;")
            .Close()
            .Line($"float k = Easing.EaseInOutSine(m_{p}Level);")
            .Line($"float r = 1 - k * (1 - m_{p}Color[0]);")
            .Line($"float g = 1 - k * (1 - m_{p}Color[1]);")
            .Line($"float b = 1 - k * (1 - m_{p}Color[2]);")
            .Line("SetTargetValueColor(PostProcessEffectType.Glow, PPEGlow.PARAM_COLORIZATIONCOLOR, {r, g, b, 0.0}, LAYER, PPOperators.MULTIPLICATIVE);")
            .Close()
            .Close()
            .Blank()
            .Open("modded class PPERequesterRegistrations")
            .Open("override protected void RegisterAdditionalRequesters()")
            .Line("super.RegisterAdditionalRequesters();")
            .Line($"PPERequesterBank.RegisterRequester({p}_FxTint);")
            .Close()
            .Close()
            .ToString();
}

// Indented text with tabs, CG style braces on their own lines.
public sealed class CodeWriter
{
    private readonly System.Text.StringBuilder _sb = new();
    private int _depth;

    public CodeWriter Line(string text)
    {
        _sb.Append('\t', _depth).Append(text).Append('\n');
        return this;
    }

    public CodeWriter Blank()
    {
        _sb.Append('\n');
        return this;
    }

    public CodeWriter Open(string? header = null)
    {
        if (header != null)
            Line(header);
        Line("{");
        _depth++;
        return this;
    }

    public CodeWriter Close(string end = "}")
    {
        _depth--;
        return Line(end);
    }

    public override string ToString() => _sb.ToString();
}
