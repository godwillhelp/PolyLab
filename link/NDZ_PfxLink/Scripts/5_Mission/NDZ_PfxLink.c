class NDZ_PfxLinkRest : RestCallback
{
	protected NDZ_PfxLink m_Link;
	protected bool m_Events;

	void NDZ_PfxLinkRest(NDZ_PfxLink link, bool events)
	{
		m_Link = link;
		m_Events = events;
	}

	override void OnSuccess(string data, int dataSize)
	{
		if (!m_Link)
			return;
		if (m_Events)
			m_Link.EventsDelivered();
		else
			m_Link.Answered(data);
	}

	override void OnError(int errorCode)
	{
		if (!m_Link)
			return;
		if (m_Events)
			m_Link.EventsRefused();
		else
			m_Link.Refused(errorCode);
	}

	override void OnTimeout()
	{
		OnError(-1);
	}
}

class NDZ_PfxLinkShot
{
	string m_Name;
	string m_File;
	int m_Time;
	float m_FrameMs;
	int m_Frames;
}

class NDZ_PfxLinkSeries
{
	string m_Prefix;
	int m_Count;
	int m_Every;
	int m_Index;
	int m_Wait;
}

class NDZ_PfxLink
{
	static const int NDZ_PFXLINK_PORT = 18150;
	static const float NDZ_PFXLINK_RETRY = 2.3;
	static const float NDZ_PFXLINK_MARK = 6.0;
	static const int NDZ_PFXLINK_SHOT_FRAMES = 2;
	static const int NDZ_PFXLINK_EMITTERS = 64;
	static const string NDZ_PFXLINK_DIR = "$profile:NDZ_PfxLink";
	static const string NDZ_PFXLINK_SHOTS = "$profile:NDZ_PfxLink/shots";

	protected ref NDZ_PfxLinkRest m_Rest;
	protected ref NDZ_PfxLinkRest m_EvRest;
	protected RestContext m_Ctx;
	protected RestContext m_EvCtx;
	protected bool m_EvBusy;
	protected ref array<string> m_EvSent = new array<string>();
	protected string m_Session;
	protected int m_Applied;
	protected bool m_Busy;
	protected bool m_Linked;
	protected float m_Wait;
	protected float m_Dt;
	protected bool m_ShotThisFrame;
	protected vector m_Anchor;
	protected bool m_HasAnchor;
	protected Camera m_Cam;
	protected ref array<string> m_Out = new array<string>();
	protected ref array<string> m_Sent = new array<string>();
	protected ref array<string> m_Cmds = new array<string>();
	protected ref map<int, ParticleSource> m_Sources = new map<int, ParticleSource>();
	protected ref array<ref NDZ_PfxLinkShot> m_Pending = new array<ref NDZ_PfxLinkShot>();
	protected ref array<string> m_ShotQueue = new array<string>();
	protected ref array<ref NDZ_PfxLinkSeries> m_Series = new array<ref NDZ_PfxLinkSeries>();

	void NDZ_PfxLink()
	{
		int port = NDZ_PFXLINK_PORT;
		string arg;
		if (GetCLIParam("ndzpfxlink", arg) && arg.ToInt() > 0)
			port = arg.ToInt();
		RestApi api = GetRestApi();
		if (!api)
			api = CreateRestApi();
		string url = "http://127.0.0.1:" + port.ToString() + "/";
		if (api)
		{
			m_Ctx = api.GetRestContext(url);
			m_EvCtx = api.GetRestContext(url + "ev/");
		}
		if (m_Ctx)
			m_Ctx.SetHeader("text/plain");
		if (m_EvCtx)
			m_EvCtx.SetHeader("text/plain");
		m_Rest = new NDZ_PfxLinkRest(this, false);
		m_EvRest = new NDZ_PfxLinkRest(this, true);
		int a = Math.RandomInt(100000, 999999);
		m_Session = a.ToString() + "x" + TickCount(0).ToString();
		QueueHello();
	}

	void ~NDZ_PfxLink()
	{
		if (m_Ctx)
			m_Ctx.reset();
		if (m_EvCtx)
			m_EvCtx.reset();
		ClearAll();
		CamOff();
	}

	void Tick(float dt)
	{
		m_Dt = dt;
		m_ShotThisFrame = false;
		CheckShots();
		RunSeries();
		TakeQueuedShot();
		RunCommands();
		if (m_Busy && m_Linked && !m_EvBusy && m_EvCtx && m_Out.Count() > 0)
			SendEvents();
		if (!m_Ctx || m_Busy || m_Cmds.Count() > 0)
			return;
		if (m_Wait > 0)
		{
			m_Wait -= dt;
			return;
		}
		Send();
	}

	void Answered(string data)
	{
		m_Busy = false;
		m_Sent.Clear();
		if (!m_Linked)
		{
			m_Linked = true;
			Print("NDZ_PfxLink: linked, session " + m_Session);
		}
		array<string> lines = new array<string>();
		data.Split("\n", lines);
		foreach (string line : lines)
		{
			line.TrimInPlace();
			if (line != "")
				m_Cmds.Insert(line);
		}
	}

	void Refused(int code)
	{
		m_Busy = false;
		if (m_Linked)
		{
			m_Linked = false;
			Print("NDZ_PfxLink: link lost, code " + code.ToString());
		}
		for (int i = m_Sent.Count() - 1; i >= 0; i--)
			m_Out.InsertAt(m_Sent[i], 0);
		m_Sent.Clear();
		m_Wait = NDZ_PFXLINK_RETRY;
	}

	protected void Send()
	{
		string body = "c " + m_Session + " " + m_Applied.ToString() + "\n";
		foreach (string line : m_Out)
		{
			body = body + line + "\n";
			m_Sent.Insert(line);
		}
		m_Out.Clear();
		m_Busy = true;
		m_Ctx.POST(m_Rest, "ndzpfx", body);
	}

	protected void SendEvents()
	{
		string body = "e " + m_Session + "\n";
		foreach (string line : m_Out)
		{
			body = body + line + "\n";
			m_EvSent.Insert(line);
		}
		m_Out.Clear();
		m_EvBusy = true;
		m_EvCtx.POST(m_EvRest, "ndzpfx", body);
	}

	void EventsDelivered()
	{
		m_EvBusy = false;
		m_EvSent.Clear();
	}

	void EventsRefused()
	{
		m_EvBusy = false;
		for (int i = m_EvSent.Count() - 1; i >= 0; i--)
			m_Out.InsertAt(m_EvSent[i], 0);
		m_EvSent.Clear();
	}

	protected void Out(string line)
	{
		m_Out.Insert(line);
	}

	protected void QueueHello()
	{
		string name;
		g_Game.GetPlayerName(name);
		name.Replace(" ", "_");
		if (name == "")
			name = "player";
		int w;
		int h;
		GetScreenSize(w, h);
		string profiles;
		if (!GetCLIParam("profiles", profiles))
			profiles = "-";
		string fp = "0";
		if (IsCLIParam("filePatching"))
			fp = "1";
		string at = "0";
		if (IsCLIParam("autotest"))
			at = "1";
		string line = "hello 1 " + name + " " + fp + " " + at;
		line = line + " " + w.ToString() + " " + h.ToString() + " " + profiles;
		Out(line);
		string names = "params";
		for (int p = 0; p <= EmitorParam.SPRING; p++)
			names = names + " " + typename.EnumToString(EmitorParam, p);
		Out(names);
	}

	protected void RunCommands()
	{
		while (m_Cmds.Count() > 0)
		{
			string line = m_Cmds[0];
			m_Cmds.RemoveOrdered(0);
			if (Run(line))
				return;
		}
	}

	protected bool Run(string line)
	{
		array<string> a = new array<string>();
		line.Split(" ", a);
		if (a.Count() < 2)
			return false;
		string cmd = a[1];
		if (cmd == "hello")
		{
			QueueHello();
			return false;
		}
		int seq = a[0].ToInt();
		if (seq <= m_Applied)
			return false;
		m_Applied = seq;
		if (cmd == "mark" && a.Count() > 3 && a[2] == "cam")
		{
			MarkInFront(a[3].ToFloat());
			return false;
		}
		if (cmd == "mark" && a.Count() > 5 && a[2] == "at")
		{
			m_Anchor = Vector(a[3].ToFloat(), a[4].ToFloat(), a[5].ToFloat());
			m_HasAnchor = true;
			return false;
		}
		if (cmd == "spawn" && a.Count() > 3)
		{
			Spawn(a[2].ToInt(), a[3], seq);
			return false;
		}
		if (cmd == "stop" && a.Count() > 2)
		{
			StopSource(a[2].ToInt());
			return false;
		}
		if (cmd == "clear")
		{
			ClearAll();
			return false;
		}
		if (cmd == "set" && a.Count() > 6)
		{
			SetParam(a, seq);
			return false;
		}
		if (cmd == "orig" && a.Count() > 2)
		{
			Originals(a[2].ToInt());
			return false;
		}
		if (cmd == "cam" && a.Count() > 8)
		{
			CamAt(a);
			return true;
		}
		if (cmd == "camoff")
		{
			CamOff();
			return true;
		}
		if (cmd == "shot" && a.Count() > 2)
		{
			m_ShotQueue.Insert(a[2]);
			return false;
		}
		if (cmd == "shots" && a.Count() > 4)
		{
			NDZ_PfxLinkSeries s = new NDZ_PfxLinkSeries();
			s.m_Prefix = a[2];
			s.m_Count = a[3].ToInt();
			s.m_Every = a[4].ToInt();
			m_Series.Insert(s);
			return false;
		}
		Out("fail unknown " + cmd);
		return false;
	}

	protected void MarkInFront(float metres)
	{
		vector pos = g_Game.GetCurrentCameraPosition();
		vector dir = g_Game.GetCurrentCameraDirection();
		dir[1] = 0;
		dir.Normalize();
		vector at = pos + dir * metres;
		at[1] = g_Game.SurfaceY(at[0], at[2]);
		m_Anchor = at;
		m_HasAnchor = true;
	}

	protected ParticleSource Source(int id)
	{
		ParticleSource ps;
		if (!m_Sources.Find(id, ps))
			return null;
		if (!ps || ps.GetOwner() != this)
		{
			m_Sources.Remove(id);
			return null;
		}
		return ps;
	}

	protected void Spawn(int id, string path, int seq)
	{
		StopSource(id);
		if (!m_HasAnchor)
			MarkInFront(NDZ_PFXLINK_MARK);
		ParticleProperties props = new ParticleProperties(m_Anchor, ParticlePropertiesFlags.PLAY_ON_CREATION, null, vector.Zero, this);
		ParticleSource ps = ParticleManager.GetInstance().CreateParticleByPath(path, props);
		if (!ps)
		{
			Out("nospawn " + id.ToString() + " " + seq.ToString() + " " + path);
			return;
		}
		m_Sources.Set(id, ps);
		string names[NDZ_PFXLINK_EMITTERS];
		int count = GetParticleEmitors(ps, names, NDZ_PFXLINK_EMITTERS);
		string line = "spawned " + id.ToString() + " " + seq.ToString() + " " + count.ToString();
		for (int i = 0; i < count; i++)
			line = line + " " + names[i];
		Out(line);
	}

	protected void StopSource(int id)
	{
		ParticleSource ps = Source(id);
		if (ps)
			ps.StopParticle(StopParticleFlags.IMMEDIATE);
		m_Sources.Remove(id);
	}

	protected void ClearAll()
	{
		for (int i = 0; i < m_Sources.Count(); i++)
		{
			ParticleSource ps = m_Sources.GetElement(i);
			if (ps && ps.GetOwner() == this)
				ps.StopParticle(StopParticleFlags.IMMEDIATE);
		}
		m_Sources.Clear();
	}

	protected void SetParam(array<string> a, int seq)
	{
		int id = a[2].ToInt();
		ParticleSource ps = Source(id);
		if (!ps)
		{
			Out("fail set " + id.ToString() + " no source");
			return;
		}
		int emitter = a[3].ToInt();
		int param = a[4].ToInt();
		string kind = a[5];
		int readFrom = emitter;
		if (readFrom < 0)
			readFrom = 0;
		string got = "got " + seq.ToString() + " " + id.ToString() + " " + emitter.ToString() + " " + param.ToString();
		if (kind == "v" && a.Count() > 8)
		{
			vector vv = Vector(a[6].ToFloat(), a[7].ToFloat(), a[8].ToFloat());
			SetParticleParm(ps, emitter, param, vv);
			vector vr;
			GetParticleParm(ps, readFrom, param, vr);
			Out(got + " " + VecText(vr));
			return;
		}
		if (kind == "b")
		{
			bool bv = a[6].ToInt() != 0;
			SetParticleParm(ps, emitter, param, bv);
			bool br;
			GetParticleParm(ps, readFrom, param, br);
			Out(got + " " + BoolText(br));
			return;
		}
		float fv = a[6].ToFloat();
		SetParticleParm(ps, emitter, param, fv);
		float fr;
		GetParticleParm(ps, readFrom, param, fr);
		Out(got + " " + fr.ToString());
	}

	protected void Originals(int id)
	{
		ParticleSource ps = Source(id);
		if (!ps)
		{
			Out("fail orig " + id.ToString() + " no source");
			return;
		}
		int count = GetParticleEmitorCount(ps);
		for (int e = 0; e < count; e++)
		{
			string line = "orig " + id.ToString() + " " + e.ToString();
			for (int p = 0; p <= EmitorParam.SPRING; p++)
				line = line + " " + OriginalText(ps, e, p);
			Out(line);
		}
		Out("origend " + id.ToString());
	}

	protected string OriginalText(ParticleSource ps, int e, int p)
	{
		if (p == EmitorParam.CONEANGLE || p == EmitorParam.EMITOFFSET)
		{
			vector v;
			GetParticleParmOriginal(ps, e, p, v);
			string text = VecText(v);
			text.Replace(" ", ",");
			return text;
		}
		if (p == EmitorParam.ACTIVE_PARTICLES)
		{
			int n;
			GetParticleParm(ps, e, p, n);
			return n.ToString();
		}
		if (IsBoolParam(p))
		{
			bool b;
			GetParticleParmOriginal(ps, e, p, b);
			return BoolText(b);
		}
		float f;
		GetParticleParmOriginal(ps, e, p, f);
		return f.ToString();
	}

	protected bool IsBoolParam(int p)
	{
		if (p == EmitorParam.RANDOM_ANGLE || p == EmitorParam.RANDOM_ROT || p == EmitorParam.LIFETIME_BY_ANIM)
			return true;
		if (p == EmitorParam.ANIM_ONCE || p == EmitorParam.RAND_FRAME || p == EmitorParam.REPEAT)
			return true;
		return p == EmitorParam.SORT || p == EmitorParam.WIND;
	}

	protected string VecText(vector v)
	{
		return v[0].ToString() + " " + v[1].ToString() + " " + v[2].ToString();
	}

	protected string BoolText(bool b)
	{
		if (b)
			return "1";
		return "0";
	}

	protected void CamAt(array<string> a)
	{
		if (!m_HasAnchor)
			MarkInFront(NDZ_PFXLINK_MARK);
		vector eye = m_Anchor + Vector(a[2].ToFloat(), a[3].ToFloat(), a[4].ToFloat());
		vector target = m_Anchor + Vector(a[5].ToFloat(), a[6].ToFloat(), a[7].ToFloat());
		if (!m_Cam)
			m_Cam = Camera.Cast(g_Game.CreateObject("staticcamera", eye, true));
		if (!m_Cam)
		{
			Out("fail cam staticcamera");
			return;
		}
		m_Cam.SetPosition(eye);
		vector view = target - eye;
		view.Normalize();
		m_Cam.SetDirection(view);
		m_Cam.SetFOV(a[8].ToFloat());
		m_Cam.SetActive(true);
	}

	protected void CamOff()
	{
		if (!m_Cam)
			return;
		m_Cam.SetActive(false);
		g_Game.ObjectDelete(m_Cam);
		m_Cam = null;
	}

	protected void TakeQueuedShot()
	{
		if (m_ShotThisFrame || m_ShotQueue.Count() == 0)
			return;
		string name = m_ShotQueue[0];
		m_ShotQueue.RemoveOrdered(0);
		TakeShot(name);
	}

	protected void TakeShot(string name)
	{
		MakeDirectory(NDZ_PFXLINK_DIR);
		MakeDirectory(NDZ_PFXLINK_SHOTS);
		NDZ_PfxLinkShot s = new NDZ_PfxLinkShot();
		s.m_Name = name;
		s.m_File = NDZ_PFXLINK_SHOTS + "/" + name + ".bmp";
		s.m_Time = g_Game.GetTime();
		s.m_FrameMs = m_Dt * 1000.0;
		if (FileExist(s.m_File))
			DeleteFile(s.m_File);
		MakeScreenshot(s.m_File);
		m_ShotThisFrame = true;
		m_Pending.Insert(s);
	}

	protected void CheckShots()
	{
		for (int i = m_Pending.Count() - 1; i >= 0; i--)
		{
			NDZ_PfxLinkShot s = m_Pending[i];
			s.m_Frames++;
			if (FileExist(s.m_File))
			{
				int w;
				int h;
				GetScreenSize(w, h);
				string line = "shot " + s.m_Name + " " + s.m_Time.ToString() + " " + s.m_FrameMs.ToString();
				line = line + " " + w.ToString() + " " + h.ToString();
				float fov = Camera.GetCurrentFOV();
				line = line + " " + fov.ToString();
				Out(line);
				m_Pending.Remove(i);
			}
			else if (s.m_Frames >= NDZ_PFXLINK_SHOT_FRAMES)
			{
				Out("noshot " + s.m_Name + " no file");
				m_Pending.Remove(i);
			}
		}
	}

	protected void RunSeries()
	{
		for (int i = m_Series.Count() - 1; i >= 0; i--)
		{
			NDZ_PfxLinkSeries s = m_Series[i];
			if (m_ShotThisFrame)
				return;
			if (s.m_Wait > 0)
			{
				s.m_Wait--;
				continue;
			}
			string index = s.m_Index.ToStringLen(2);
			TakeShot(s.m_Prefix + "_" + index);
			s.m_Index++;
			s.m_Wait = s.m_Every - 1;
			if (s.m_Index >= s.m_Count)
				m_Series.Remove(i);
		}
	}
}

modded class MissionGameplay
{
	protected ref NDZ_PfxLink m_NDZPfxLink;

	override void OnUpdate(float timeslice)
	{
		super.OnUpdate(timeslice);
		if (!m_NDZPfxLink)
			m_NDZPfxLink = new NDZ_PfxLink();
		m_NDZPfxLink.Tick(timeslice);
	}
}
