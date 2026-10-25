using System.Text;
using PolyLab.PBOGPacker.Core;

namespace PolyLab.PBOGPacker.Tests;

// Synthetic mods on a scratch "work drive": <tmp>\PLTest is the mod (prefix PLTest), <tmp>\DZ the game data,
// <tmp>\PLOther another mod. Every case the reference check must find, and what it must leave alone.
public sealed class RefCheckTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "pbog_refs_" + Guid.NewGuid().ToString("N"));
    private string Mod => Path.Combine(_work, "PLTest");

    public RefCheckTests()
    {
        Put(@"PLTest\data\item_co.paa", "x");
        Put(@"PLTest\data\item.rvmat", "class Stage1 { texture = \"PLTest\\data\\item_co.paa\"; };");
        Put(@"PLTest\sounds\beep.ogg", "x");
        Put(@"PLTest\scripts\4_World\keep.c", "class A {};");
        Put(@"PLTest\gui\menu.layout", "FrameWidgetClass root {}");
        Put(@"DZ\gear\food\data\apple_co.paa", "x");
        Put(@"DZ\data\data\default.rvmat", "x");
        Put(@"PLOther\data\other_co.paa", "x");
    }

    public void Dispose()
    {
        try { Directory.Delete(_work, true); } catch (IOException) { }
    }

    private void Put(string rel, string text) => Put(rel, Encoding.UTF8.GetBytes(text));

    private void Put(string rel, byte[] data)
    {
        var p = Path.Combine(_work, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, data);
    }

    private RefCheck.Report Scan(bool reachable = true) =>
        RefCheck.Scan(Mod, "PLTest", reachable ? _work : null, reachable ? Path.Combine(_work, "DZ") : null);

    private static string Config(string body) => "class CfgPatches { class PLTest { requiredAddons[] = {}; }; };\n" + body;

    private static byte[] Mlod(params (float Res, string[] Faces)[] lods)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms, Encoding.Latin1);
        w.Write(Encoding.ASCII.GetBytes("MLOD"));
        w.Write(257);
        w.Write(lods.Length);
        foreach (var (res, faces) in lods)
        {
            w.Write(Encoding.ASCII.GetBytes("P3DM"));
            w.Write(28);
            w.Write(256);
            w.Write(3);
            w.Write(1);
            w.Write(faces.Length / 2);
            w.Write(0);
            w.Write(new byte[3 * 16 + 12]);
            for (var f = 0; f < faces.Length; f += 2)
            {
                w.Write(3);
                w.Write(new byte[64]);
                w.Write(0);
                w.Write(Encoding.Latin1.GetBytes(faces[f] + "\0" + faces[f + 1] + "\0"));
            }
            w.Write(Encoding.ASCII.GetBytes("TAGG"));
            w.Write((byte)1);
            w.Write(Encoding.ASCII.GetBytes("#SharpEdges#\0"));
            w.Write(4);
            w.Write(0);
            w.Write((byte)1);
            w.Write(Encoding.ASCII.GetBytes("#EndOfFile#\0"));
            w.Write(0);
            w.Write(res);
        }
        return ms.ToArray();
    }

    private static byte[] Binary(string magic, params string[] names)
    {
        var b = new List<byte>(Encoding.ASCII.GetBytes(magic)) { 56, 0, 0, 0, 0x41, 0x7F, 0x00 };
        foreach (var n in names) b.AddRange(Encoding.Latin1.GetBytes(n + "\0"));
        b.AddRange(new byte[] { 0x10, 0x41, 0x42, 0x43 });
        return b.ToArray();
    }

    [Fact]
    public void A_clean_mod_passes()
    {
        Put(@"PLTest\config.cpp", Config(
            "class CfgVehicles { class PLT_Item { model = \"\\PLTest\\models\\item.p3d\";\n" +
            "  hiddenSelectionsTextures[] = {\"PLTest\\data\\item_co.paa\", \"\\dz\\gear\\food\\data\\apple_co.paa\", \"PLOther\\data\\other_co.paa\", \"#(argb,8,8,3)color(1,0,0,1,co)\"};\n" +
            "  hiddenSelectionsMaterials[] = {\"PLTest\\data\\item.rvmat\"}; }; };\n" +
            "class CfgSoundShaders { class S { samples[] = {{\"PLTest\\sounds\\beep\", 1}}; }; };\n" +
            "class CfgMods { class PLTest { class defs { class worldScriptModule { value = \"\"; files[] = {\"PLTest/scripts/4_World\"}; };\n" +
            "  class imageSets { files[] = {\"PLTest/gui/icons.imageset\"}; }; }; }; };"));
        Put(@"PLTest\gui\icons.imageset", "ImageSetClass { Name \"icons\" }");
        Put(@"PLTest\models\item.p3d", Mlod((0f, new[] { @"PLTest\data\item_co.paa", @"PLTest\data\item.rvmat" }), (1e13f, new[] { "", "" })));
        Put(@"PLTest\models\vanilla.p3d", Binary("ODOL", @"dz\gear\food\data\apple_co.paa", @"dz\data\data\default.rvmat"));
        Put(@"PLTest\scripts\4_World\menu.c", "void Open() { GetGame().GetWorkspace().CreateWidgets(\"PLTest\\\\gui\\\\menu.layout\"); }");
        Put(@"PLTest\gui\list.layout", "ImageWidgetClass i {\n image0 \"{1234567890ABCDEF}PLTest/data/item_co.paa\"\n}");

        var r = Scan();

        Assert.Empty(r.Missing);
        Assert.Empty(r.OdolOther);
        Assert.Empty(r.Unchecked);
        Assert.Equal(15, r.Refs);
        Assert.Equal(8, r.Files);
    }

    [Fact]
    public void Every_source_reports_its_missing_files_with_line_or_lod()
    {
        Put(@"PLTest\config.cpp", Config(
            "class CfgVehicles { class PLT_Item {\n" +
            "  model = \"\\PLTest\\models\\nomodel.p3d\";\n" +
            "  hiddenSelectionsTextures[] = {\"PLTest\\data\\missing_co.paa\", \"\\dz\\gear\\food\\data\\nothere_co.paa\", \"\\PLOther\\data\\gone_co.paa\", \"\\PLNowhere\\x_co.paa\"};\n" +
            "  hiddenSelectionsMaterials[] = {\"PLTest\\data\\nomat.rvmat\"}; }; };\n" +
            "class CfgSoundShaders { class S { samples[] = {{\"PLTest\\sounds\\beep\", 1}, {\"PLTest\\sounds\\nobeep\", 1}}; }; };\n" +
            "class CfgMods { class PLTest { class defs { class worldScriptModule { value = \"\"; files[] = {\"PLTest/scripts/4_World\", \"PLTest/scripts/nofolder\", \"PLTest/gui/none.imageset\"}; }; }; }; };\n" +
            "#include \"macros.hpp\""));
        Put(@"PLTest\macros.hpp", "\n\nicon = \"PLTest\\data\\noicon_ca.paa\";");
        Put(@"PLTest\data\text.rvmat", "class Stage1\n{\n\ttexture = \"PLTest\\data\\missing_nohq.paa\";\n};");
        Put(@"PLTest\data\bin.rvmat", Binary("\0raP", "texture", @"PLTest\data\missing_smdi.paa"));
        Put(@"PLTest\models\mlod.p3d", Mlod((0f, new[] { @"PLTest\data\gone_co.paa", "", @"PLTest\data\gone_co.paa", "" }),
            (1e13f, new[] { "", @"PLTest\data\geo.rvmat" }), (10000f, new[] { @"pltest\DATA\gone_co.paa", "" })));
        Put(@"PLTest\models\odol.p3d", Binary("ODOL", @"PLTest\data\odol_co.paa", @"PLTest\data\inlined.rvmat", @"dz\gear\food\data\apple_co.paa",
            @"a3\data_f\leftover.rvmat", @"dz\gone\x_co.paa"));
        Put(@"PLTest\scripts\4_World\menu.c", "void Open()\n{\n\tCreateWidgets(\"PLTest\\\\gui\\\\nolayout.layout\");\n}");
        Put(@"PLTest\gui\menu.layout", "ImageWidgetClass i {\n image0 \"{1234567890ABCDEF}PLTest/gui/noimg.edds\"\n image1 \"{655A1BF79F5B291}PLTest/gui/noimg2.edds\"\n}");

        var r = Scan();
        var got = r.Missing.Select(f => f.ToString()).ToList();

        Assert.Equal(new[]
        {
            @"config.cpp:3  \PLTest\models\nomodel.p3d",
            @"config.cpp:4  PLTest\data\missing_co.paa",
            @"config.cpp:4  \PLOther\data\gone_co.paa",
            @"config.cpp:4  \dz\gear\food\data\nothere_co.paa",
            @"config.cpp:5  PLTest\data\nomat.rvmat",
            @"config.cpp:6  PLTest\sounds\nobeep",
            @"config.cpp:7  PLTest/gui/none.imageset",
            @"config.cpp:7  PLTest/scripts/nofolder",
            @"data\bin.rvmat  PLTest\data\missing_smdi.paa",
            @"data\text.rvmat:3  PLTest\data\missing_nohq.paa",
            @"gui\menu.layout:2  {1234567890ABCDEF}PLTest/gui/noimg.edds",
            @"gui\menu.layout:3  {655A1BF79F5B291}PLTest/gui/noimg2.edds",
            @"macros.hpp:3  PLTest\data\noicon_ca.paa",
            @"models\mlod.p3d (Geometry)  PLTest\data\geo.rvmat",
            @"models\mlod.p3d (LOD 0.000, ShadowVolume 0.000)  PLTest\data\gone_co.paa",
            @"models\odol.p3d  PLTest\data\odol_co.paa",
            @"scripts\4_World\menu.c:3  PLTest\gui\nolayout.layout",
        }, got.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { (@"models\odol.p3d", 2) }, r.OdolOther);
        Assert.Equal(new[] { "a3", "PLNowhere" }, r.Unchecked);
    }

    [Fact]
    public void Case_and_slash_styles_resolve()
    {
        Put(@"PLTest\config.cpp", Config(
            "a = \"plTEST/Data/Item_CO.paa\";\nb = \"\\\\PLTest\\\\\\\\data\\\\item_co.paa\";\nc = \"/DZ/Gear/Food/Data/APPLE_co.PAA\";\n" +
            "d = \"\\plother/data\\other_co.paa\";\ne = \"PLTest\\data\\ITEM.RVMAT\";\nsamples[] = {{\"plTest\\Sounds\\BEEP\", 1}};"));

        var r = Scan();

        Assert.Empty(r.Missing);
        Assert.Equal(7, r.Refs);
    }

    [Fact]
    public void Strings_that_are_no_file_paths_are_left_alone()
    {
        Put(@"PLTest\config.cpp", Config(
            "a = \"#(argb,8,8,3)color(0.5,0.5,0.5,1,DT)\";\nb = \"$STR_PLT_name\";\nc = \"https://example.com/page.html\";\n" +
            "d = \"none.paa\";\ne = \"1.5\";\nf = \"PLTest/data/version 1.2\";\ng = \"PLTest\\data\\\";\nh = \"PLTest\\data\\x.unknownext\";\n" +
            "// i = \"PLTest\\data\\commented.paa\";\n/* j = \"PLTest\\data\\block.paa\";\n k = \"PLTest\\data\\block2.paa\"; */\n" +
            "#define TEX \"PLTest\\sub\\relative.paa\"\nl = \"Say \"\"hi\"\" / bye\";"));
        Put(@"PLTest\scripts\4_World\paths.c",
            "string a = \"$profile:PLTest\\\\cfg.json\";\nstring b = string.Format(\"PLTest\\\\data\\\\%1.paa\", n);\nstring c = \"PLTest\\\\gui\\\\\" + name + \".layout\";\n" +
            "string d = \"set:dayz_gui image:icon\";");

        var r = Scan();

        Assert.Empty(r.Missing);
        Assert.Equal(1, r.Refs);
    }

    [Fact]
    public void Paa_stands_for_tga_and_png_but_not_the_other_way()
    {
        Put(@"PLTest\data\only_co.png", "x");
        Put(@"PLTest\config.cpp", Config("a = \"PLTest\\data\\item_co.png\";\nb = \"PLTest\\data\\item_co.tga\";\nc = \"PLTest\\data\\only_co.paa\";\nd = \"PLTest\\data\\only_co.png\";"));

        var r = Scan();

        Assert.Equal(new[] { @"config.cpp:4  PLTest\data\only_co.paa" }, r.Missing.Select(f => f.ToString()));
    }

    [Fact]
    public void Folders_that_cannot_be_reached_are_listed_instead_of_reported()
    {
        Put(@"PLTest\config.cpp", Config("a = \"\\dz\\gear\\x.paa\";\nb = \"PLOther\\data\\other_co.paa\";\nc = \"PLTest\\data\\gone.paa\";"));

        var r = Scan(reachable: false);

        Assert.Equal(new[] { @"config.cpp:4  PLTest\data\gone.paa" }, r.Missing.Select(f => f.ToString()));
        Assert.Equal(new[] { "dz", "PLOther" }, r.Unchecked);
    }

    [Fact]
    public void A_broken_mlod_falls_back_to_its_strings()
    {
        var bytes = Mlod((0f, new[] { @"PLTest\data\gone_co.paa", "" }));
        Put(@"PLTest\models\cut.p3d", bytes[..(bytes.Length - 30)]);

        var r = Scan();

        Assert.Equal(new[] { @"models\cut.p3d  PLTest\data\gone_co.paa" }, r.Missing.Select(f => f.ToString()));
    }
}
