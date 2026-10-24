"""Builds src/ParticleStudio/Pfx.Core/Schema/schema.json from DayZ Tools binarize.exe.

Emitter keys are read from the registration code of EmitorDef (the strings loaded right before each AddProperty
call), so key, description, default, range and editor come from the engine itself. EffectDef and material keys are
few and registered through code paths this reader does not follow; they are listed below with the code offsets
they were read from (binarize.exe of DayZ Tools 1.29). Needs Python 3.10+, capstone and pefile.

Usage: py -3.12 build_schema.py "<DayZ Tools>/Bin/Binarize/binarize.exe" <out.json>
"""
import hashlib
import json
import struct
import sys

import capstone
import pefile

ADD_PROPERTY = 0x1401A5670
BUILDERS = {0x1401892B0: "plain", 0x140189440: "enum"}
SET_GROUP = 0x1401A5B10
EMITOR_CODE = (0x2D1C40, 0x2D3A00)

KINDS = {1: "float", 3: "vector", 8: "int", 10: "bool", 15: "resource", 130: "graph2", 132: "graph4"}

ENUMS = {
    "ShapeType": ["Box", "Point", "Cone", "Sphere", "Ellipse", "OnSphere"],
    "BillboardingType": ["Full", "LockedAxis", "None"],
}

EFFECT = [
    {"key": "MinFPS", "type": "int", "default": "1", "range": "1 60", "group": "General", "code": "2d1a25",
     "desc": "If the game is running slower then the effect will still be played at this FPS"},
    {"key": "Emitors", "type": "objects", "default": None, "range": None, "group": "General", "code": "2d1aec",
     "desc": None},
]

MATERIAL_BASE = [
    ("AlbedoMap", "texture", None, None, "Albedo map", "sRGB albedo (color reflectance) texture with possible alpha channel", "21c47e"),
    ("UVSrcAlbedoMap", "int", "0", None, "Albedo map", "UV source of main albedo map", "21c511"),
    ("AlbedoMapTexMat", "matrix", "1 0 0 0 0 1 0 0", None, "Albedo map", "Albedo map texture matrix", "21c59b"),
    ("Color", "color", "1 1 1 1", None, "Color modificators", "Color modificator. White by default", "21c7f0"),
    ("Diffuse", "color", "1 1 1 1", None, "Color modificators", "Diffuse light modificator. White by default", "21c8bf"),
    ("Ambient", "color", "1 1 1 1", None, "Color modificators", "Ambient light modificator. White by default", "21c987"),
    ("Emissive", "color", "0 0 0 0", None, "Color modificators", "Emissive color, absolute light value, zero by default", "21ca4f"),
    ("ZEnable", "bool", "true", None, "Basic rendering", "Enables Z-buffer compare", "21cb37"),
    ("ZWrite", "bool", None, None, "Basic rendering", "Enables Z-buffer writes", "21cc06"),
    ("ZBias", "int", "0", "-16 16 1", "Basic rendering", "Z bias, negative values are usefull (-> closer to camera), positive are for special purposes", "21ccd2"),
    ("Cull", "enum", "ccw", None, "Basic rendering", "Enables doubleside material", "21cf90"),
    ("Sort", "enum", "opaque", None, "Basic rendering", "Defines sorting group", "21d010"),
    ("SortBias", "int", "0", "-16 15 1", "Basic rendering", "Fine defines sorting bias", "21d07e"),
    ("CastShadow", "bool", "false", None, "Basic rendering", "Casts shadows", "21d100"),
    ("ReceiveShadow", "bool", None, None, "Basic rendering", "Receive shadow", "21d169"),
    ("DisableFog", "bool", None, None, "Basic rendering", "Disable fog", "21d1d4"),
    ("PhysMaterial", "string", "default", None, "Basic rendering", "Physical material", "21d24a"),
    ("TCModFuncs", "objects", None, None, "Basic rendering", "Texture-coords modifications", "21d2b8"),
]

MATERIAL_PARTICLE = [
    ("NormalMap", "texture", None, None, "Particle", "Normalmap texture, if set, the fake lighting for sprites will be enabled", "25855f"),
    ("HeightMap", "texture", None, None, "Particle", "Height map for parallax mapping", "258639"),
    ("AlphaTest", "int", None, "0 255 1", "Particle", "Defines value above which pixels are rendered", "258706"),
    ("BlendMode", "enum", "AlphaBlend", None, "Particle", "Defines how the material will be blended", "2587b9"),
    ("TileU", "int", None, None, "Particle", "Number of tiles in U axis", "258821"),
    ("TileV", "int", None, None, "Particle", "Number of tiles in V axis", "2588d0"),
    ("Softness", "float", "0", "0 2 0.01", "Particle", "Softness of particle", "25897f"),
    ("DepthScale", "float", None, "0 4 0.05", "Particle", "Alpha to depth multiplier", "258a3c"),
    ("CameraBlendNear", "float", None, "0 32 0.001", "Particle", "Distance from camera where blend starts (the result is zero opacity)", "258af2"),
    ("CameraBlendFar", "float", None, "0.001 32 0.001", "Particle", "Distance from camera where blend ends (the result is full opacity)", "258ba8"),
    ("LightScale", "float", "1.0", "0 1 0.01", "Particle", "Light multiplicator on side of global light vector", "258c5e"),
    ("BidirLightScale", "float", None, None, "Particle", "Light multiplicator on opposite side of global light vector (when normal map is set)", "258d1f"),
]

MATERIAL_ENUMS = {
    "Cull": ["ccw", "none"],
    "Sort": ["opaque", "translucent", "decal", "mark", "near", "overlay", "overall"],
    "BlendMode": ["AlphaBlend", "Additive", "Additive_AlphaModulate", "ColorModulate", "Replace_AlphaModulate"],
}

MATERIAL_CLASSES = {
    "Particle": "Basic particle material",
    "ParticleSprite": "Basic particle material",
    "ParticleStreak": "Particle material able to render streaks -> extended sprites to the direction of movement",
}


def read_emitor(path):
    pe = pefile.PE(path, fast_load=True)
    base = pe.OPTIONAL_HEADER.ImageBase
    raw = pe.__data__
    text = next(s for s in pe.sections if s.Name.startswith(b".text"))
    code = text.get_data()
    tva = text.VirtualAddress

    def cstr(rva):
        try:
            off = pe.get_offset_from_rva(rva)
        except Exception:
            return None
        if off is None or off >= len(raw):
            return None
        end = raw.find(b"\0", off, off + 300)
        if end < off:
            return None
        s = raw[off:end]
        if any(c < 32 or c > 126 for c in s):
            return None
        return s.decode()

    def deref(rva):
        try:
            off = pe.get_offset_from_rva(rva)
        except Exception:
            return None
        if off is None:
            return None
        q = struct.unpack_from("<Q", raw, off)[0]
        return cstr(q - base) if q else None

    def value(v):
        kind, x = v
        if kind == "str":
            return x
        if kind == "glob":
            return deref(x)
        if kind == "imm":
            return x
        return None

    def canon(r):
        return {"eax": "rax", "ebx": "rbx", "ecx": "rcx", "edx": "rdx", "esi": "rsi", "edi": "rdi", "r8d": "r8",
                "r9d": "r9", "r9b": "r9", "r12d": "r12", "r13d": "r13", "r14d": "r14", "r15d": "r15",
                "bl": "rbx"}.get(r, r)

    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    md.detail = True
    x86 = capstone.x86
    none = ("other", None)
    regs, rbp, rsp = {}, {}, {}
    built, group, out = None, None, []
    lo, hi = EMITOR_CODE
    for ins in md.disasm(code[lo:hi], base + tva + lo):
        ops, m = ins.operands, ins.mnemonic
        if m == "lea" and len(ops) == 2 and ops[1].type == x86.X86_OP_MEM:
            dst, mem = canon(md.reg_name(ops[0].reg)), ops[1].mem
            if mem.base == x86.X86_REG_RIP:
                s = cstr(ins.address + ins.size + mem.disp - base)
                regs[dst] = ("str", s) if s is not None else none
            elif mem.base and md.reg_name(mem.base) == "rbx" and mem.index == 0:
                regs[dst] = ("imm", mem.disp)
            else:
                regs[dst] = none
        elif m == "mov" and len(ops) == 2:
            if ops[0].type == x86.X86_OP_REG:
                dst = canon(md.reg_name(ops[0].reg))
                if ops[1].type == x86.X86_OP_IMM:
                    regs[dst] = ("imm", ops[1].imm)
                elif ops[1].type == x86.X86_OP_MEM and ops[1].mem.base == x86.X86_REG_RIP:
                    regs[dst] = ("glob", ins.address + ins.size + ops[1].mem.disp - base)
                elif ops[1].type == x86.X86_OP_REG:
                    regs[dst] = regs.get(canon(md.reg_name(ops[1].reg)), none)
                else:
                    regs[dst] = none
            elif ops[0].type == x86.X86_OP_MEM:
                mem = ops[0].mem
                b = md.reg_name(mem.base) if mem.base else ""
                if ops[1].type == x86.X86_OP_REG:
                    v = regs.get(canon(md.reg_name(ops[1].reg)), none)
                elif ops[1].type == x86.X86_OP_IMM:
                    v = ("imm", ops[1].imm)
                else:
                    v = none
                if b == "rbp":
                    rbp[mem.disp] = v
                elif b == "rsp":
                    rsp[mem.disp] = v
        elif m == "call" and ops and ops[0].type == x86.X86_OP_IMM:
            target = ops[0].imm
            if target in BUILDERS:
                built = dict(kind=value(regs.get("rdx", none)), key=value(regs.get("r8", none)),
                             default=value(rsp.get(0x20, none)), editor=value(rsp.get(0x28, none)),
                             range=value(rsp.get(0x30, none)), desc=value(rsp.get(0x38, none)))
            elif target == ADD_PROPERTY:
                if built is None:
                    built = dict(kind=value(rbp.get(-0x41, none)), key=value(rbp.get(0x67, none)),
                                 default=value(rbp.get(0x6F, none)), editor=value(rbp.get(0x77, none)),
                                 range=value(rbp.get(0x7F, none)), desc=value(rbp.get(-0x49, none)))
                built["group"] = group
                built["code"] = "%x" % (ins.address - base - tva)
                out.append(built)
                built, rbp, rsp = None, {}, {}
            elif target == SET_GROUP:
                group = value(regs.get("rdx", none))
            for r in ("rax", "rcx", "rdx", "r8", "r9", "r10", "r11"):
                regs.pop(r, None)
    return out


def text_or_none(v):
    return v if isinstance(v, str) and v != "" else None


def main():
    exe, dst = sys.argv[1], sys.argv[2]
    raw = open(exe, "rb").read()
    emitor = []
    for p in read_emitor(exe):
        key = p["key"]
        kind = p["kind"] if isinstance(p["kind"], int) else None
        typ = "enum" if key in ENUMS else KINDS.get(kind, "unknown")
        if typ == "unknown":
            raise SystemExit("unknown kind %r for %s" % (p["kind"], key))
        emitor.append({"key": key, "type": typ, "default": text_or_none(p["default"]),
                       "range": text_or_none(p["range"]) if typ != "resource" else None,
                       "editor": text_or_none(p["editor"]), "group": p["group"],
                       "desc": text_or_none(p["desc"]), "values": ENUMS.get(key), "code": p["code"],
                       "resource": p["range"] if typ == "resource" else None})

    def mat(rows):
        return [{"key": k, "type": t, "default": d, "range": r, "group": g, "desc": desc, "code": c,
                 "values": MATERIAL_ENUMS.get(k)} for k, t, d, r, g, desc, c in rows]

    schema = {
        "source": {"file": "binarize.exe", "sha256": hashlib.sha256(raw).hexdigest(), "size": len(raw)},
        "effect": EFFECT,
        "emitor": emitor,
        "material": {"classes": MATERIAL_CLASSES, "base": mat(MATERIAL_BASE), "particle": mat(MATERIAL_PARTICLE)},
    }
    with open(dst, "w", encoding="utf-8", newline="\n") as f:
        json.dump(schema, f, indent=1)
        f.write("\n")
    print("emitor keys %d, effect %d, material %d + %d" % (len(emitor), len(EFFECT), len(MATERIAL_BASE),
                                                         len(MATERIAL_PARTICLE)))


main()
