"""Builds the test models of OdolTests: MLOD sources written here, binarized with DayZ Tools' binarize.

    py -3 -I make_models.py [binarize.exe]

Writes src\\polylab\\test\\ (plbox.p3d with model.cfg and two rvmats: skeleton, animations, a section, a proxy, point
flags, bone weights, a second UV set, masses) and src_plain\\polylab\\test\\plplain.p3d (no model.cfg, so binarize
keeps every selection's points), then binarizes both into plbox.p3d and plplain.p3d next to this script. Everything
here is made from scratch; no game file is used.
"""
import os, shutil, struct, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))
BINARIZE = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
    os.environ.get("DAYZ_TOOLS", r"C:\Program Files (x86)\Steam\steamapps\common\DayZ Tools"), "Bin", "Binarize", "binarize.exe")


def asciiz(s):
    return s.encode("latin-1") + b"\0"


class Lod:
    def __init__(self, res):
        self.res, self.pts, self.nors, self.faces, self.sels, self.tags = res, [], [], [], [], []

    def pt(self, x, y, z, flags=0):
        self.pts.append((x, y, z, flags))
        return len(self.pts) - 1

    def nor(self, x, y, z):
        self.nors.append((x, y, z))
        return len(self.nors) - 1

    def face(self, verts, tex="", mat="", flags=0):
        self.faces.append((verts, flags, tex, mat))
        return len(self.faces) - 1

    def sel(self, name, pts, faces=(), weights=None):
        self.sels.append((name, list(pts), list(faces), dict(weights or {})))

    def tag(self, name, data):
        self.tags.append((name, data))

    def data(self):
        o = bytearray(b"P3DM" + struct.pack("<IIiiiI", 0x1C, 0x100, len(self.pts), len(self.nors), len(self.faces), 0))
        for x, y, z, f in self.pts:
            o += struct.pack("<3fI", x, y, z, f)
        for n in self.nors:
            o += struct.pack("<3f", *n)
        for verts, flags, tex, mat in self.faces:
            o += struct.pack("<i", len(verts))
            for k in range(4):
                p, n, u, v = verts[k] if k < len(verts) else (0, 0, 0.0, 0.0)
                o += struct.pack("<iiff", p, n, u, v)
            o += struct.pack("<I", flags) + asciiz(tex) + asciiz(mat)
        o += b"TAGG"
        tags = list(self.tags)
        for name, pts, faces, weights in self.sels:
            d = bytearray(len(self.pts) + len(self.faces))
            for i in pts:
                w = weights.get(i, 1.0)
                d[i] = 1 if w >= 1.0 else round(256 - w * 255)
            for f in faces:
                d[len(self.pts) + f] = 1
            tags.append((name, bytes(d)))
        for name, data in tags + [("#EndOfFile#", b"")]:
            o += b"\1" + asciiz(name) + struct.pack("<I", len(data)) + data
        o += struct.pack("<f", self.res)
        return bytes(o)


def mlod(lods):
    return b"MLOD" + struct.pack("<Ii", 0x101, len(lods)) + b"".join(l.data() for l in lods)


CUBE = [(-0.5, 0, -0.5), (0.5, 0, -0.5), (0.5, 0, 0.5), (-0.5, 0, 0.5), (-0.5, 1, -0.5), (0.5, 1, -0.5), (0.5, 1, 0.5), (-0.5, 1, 0.5)]
QUADS = [((0, 1, 2, 3), (0, -1, 0)), ((4, 7, 6, 5), (0, 1, 0)), ((0, 4, 5, 1), (0, 0, -1)), ((1, 5, 6, 2), (1, 0, 0)),
         ((2, 6, 7, 3), (0, 0, 1)), ((3, 7, 4, 0), (-1, 0, 0))]
UV = [(0, 0), (1, 0), (1, 1), (0, 1)]


def cube(l, off=(0, 0, 0), scale=1.0, flags=None, texs=None):
    base = len(l.pts)
    for i, (x, y, z) in enumerate(CUBE):
        l.pt(x * scale + off[0], y * scale + off[1], z * scale + off[2], (flags or {}).get(i, 0))
    faces = []
    for qi, (q, n) in enumerate(QUADS):
        ni = l.nor(*n)
        tex, mat = texs[qi] if texs else ("", "")
        faces.append(l.face([(base + p, ni, UV[k][0], UV[k][1]) for k, p in enumerate(q)], tex, mat))
    return list(range(base, base + 8)), faces


MAT = r"polylab\test\data\box.rvmat"
MAT2 = r"polylab\test\data\box2.rvmat"
TEX = r"polylab\test\data\box_co.paa"
RED = "#(argb,8,8,3)color(1,0,0,1,co)"
RVMAT = """ambient[]={1,1,1,1};
diffuse[]={1,1,1,1};
forcedDiffuse[]={0,0,0,0};
emmisive[]={0,0,0,1};
specular[]={0.1,0.1,0.1,1};
specularPower=30;
PixelShaderID="Super";
VertexShaderID="Super";
class Stage1
{
	texture="#(argb,8,8,3)color(0.5,0.5,1,1,nohq)";
	uvSource="tex";
	class uvTransform
	{
		aside[]={1,0,0};
		up[]={0,1,0};
		dir[]={0,0,0};
		pos[]={0,0,0};
	};
};
class Stage2
{
	texture="#(argb,8,8,3)color(0.5,0.5,0.5,1,dt)";
	uvSource="tex";
	class uvTransform
	{
		aside[]={2,0,0};
		up[]={0,3,0};
		dir[]={0,0,0};
		pos[]={0.5,0.25,0};
	};
};
"""
MODEL_CFG = """class CfgSkeletons
{
	class plbox_skel
	{
		isDiscrete=1;
		skeletonInherit="";
		skeletonBones[]={"door","","lid","door"};
	};
};
class CfgModels
{
	class plbox
	{
		skeletonName="plbox_skel";
		sectionsInherit="";
		sections[]={"camo"};
		class Animations
		{
			class door_rot { type="rotation"; source="user"; selection="door"; axis="axis_door"; memory=1; sourceAddress="loop"; minValue=0; maxValue=1; angle0=0; angle1=1.5707964; };
			class lid_slide { type="translation"; source="door"; selection="lid"; axis="axis_slide"; memory=1; sourceAddress="mirror"; minValue=0.1; maxValue=0.9; offset0=0; offset1=0.5; };
			class door_hide { type="hide"; source="damage"; selection="door"; minValue=0; maxValue=1; hideValue=0.5; };
		};
	};
};
"""


def build(root, skeletal):
    folder = os.path.join(root, "polylab", "test")
    shutil.rmtree(root, ignore_errors=True)
    os.makedirs(os.path.join(folder, "data"))
    l0 = Lod(0.0)
    # Point flags: on land, decal, no fog, user value 5, no light (binarize keeps it as user value 200).
    flags = {0: 0x1, 1: 0x100, 6: 0x50000, 7: 0x1000, 2: 0x10}
    pts, faces = cube(l0, flags=flags, texs=[(TEX, MAT)] * 2 + [(RED, MAT2)] * 4)
    verts, _, tex, mat = l0.faces[faces[2]]
    l0.faces[faces[2]] = (verts, 0x8, tex, mat)
    dpts, dfaces = cube(l0, off=(1.5, 0, 0), scale=0.5, texs=[(TEX, MAT)] * 6)
    a, b, c = l0.pt(0, 2, 0), l0.pt(0, 2, 2), l0.pt(0, 3, 0)
    pn = l0.nor(1, 0, 0)
    pf = l0.face([(a, pn, 0, 0), (b, pn, 0, 0), (c, pn, 0, 0)])
    l0.sel("camo", pts, faces)
    l0.sel("door", dpts, dfaces)
    l0.sel("lid", dpts[:4], (), {dpts[0]: 0.5, dpts[1]: 0.25, dpts[2]: 0.75})
    l0.sel("proxy:\\polylab\\test\\plproxy.001", [a, b, c], [pf])
    l0.tag("#UVSet#", struct.pack("<I", 1) + b"".join(struct.pack("<2f", 0.25 * k, 0.5) for f in l0.faces for k in range(len(f[0]))))
    l1 = Lod(1.0)
    p1, f1 = cube(l1, texs=[(TEX, MAT)] * 6)
    dp1, df1 = cube(l1, off=(1.5, 0, 0), scale=0.5, texs=[(TEX, MAT)] * 6)
    l1.sel("camo", p1, f1)
    l1.sel("door", dp1, df1)
    mem = Lod(1e15)
    m0, m1, m2, m3 = mem.pt(1.25, 0, 0), mem.pt(1.25, 1, 0), mem.pt(0, 0.5, 0), mem.pt(0, 0.5, 2)
    mem.sel("axis_door", [m0, m1])
    mem.sel("axis_slide", [m2, m3])
    geo = Lod(1e13)
    gp, gf = cube(geo)
    geo.sel("component01", gp, gf)
    geo.tag("#Mass#", struct.pack("<8f", 1, 2, 3, 4, 5, 6, 7, 8.5))
    geo.tag("#Property#", b"autocenter".ljust(64, b"\0") + b"0".ljust(64, b"\0"))
    name = "plbox" if skeletal else "plplain"
    open(os.path.join(folder, name + ".p3d"), "wb").write(mlod([l0, l1, mem, geo]))
    open(os.path.join(folder, "data", "box.rvmat"), "w", newline="\n").write(RVMAT)
    open(os.path.join(folder, "data", "box2.rvmat"), "w", newline="\n").write(
        RVMAT.replace('PixelShaderID="Super"', 'PixelShaderID="Normal"').replace("specularPower=30", "specularPower=70"))
    if skeletal:
        open(os.path.join(folder, "model.cfg"), "w", newline="\n").write(MODEL_CFG)
    return folder, name


if __name__ == "__main__":
    for sub, skeletal in (("src", True), ("src_plain", False)):
        folder, name = build(os.path.join(HERE, sub), skeletal)
        out = os.path.join(HERE, "out_" + sub)
        shutil.rmtree(out, ignore_errors=True)
        subprocess.run([BINARIZE, "-always", "-silent", "-noLogs", folder, out], cwd=os.path.join(HERE, sub), stdin=subprocess.DEVNULL, check=False)
        shutil.copy2(os.path.join(out, name + ".p3d"), os.path.join(HERE, name + ".p3d"))
        shutil.rmtree(out)
        print(name, os.path.getsize(os.path.join(HERE, name + ".p3d")))
