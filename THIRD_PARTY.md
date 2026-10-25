# Third-party components

Poly Lab's own code is MIT (see `LICENSE`). Everything below keeps its own licence. Nothing here is GPL; GRAFT
(GPL-3.0 with the "GRAFT plugin exception") is not part of this repository and is downloaded by the user or the launcher from
its upstream releases.

## NuGet packages

Restored at build time, not stored in the repository. Licences as stated in each package's `.nuspec`.

| Package | Version | Licence | Used by |
|---|---|---|---|
| Dirkster.AvalonDock, Dirkster.AvalonDock.Themes.VS2013 | 4.74.1 | Ms-PL | Particle Studio (docking) |
| NAudio.Core, NAudio.WinMM | 2.2.1 | MIT (© Mark Heath) | Particle Studio (sound preview) |
| NVorbis | 0.10.5 | MIT (© Andrew Ward) | Particle Studio (.ogg preview) |
| Vortice.Direct3D11, Vortice.D3DCompiler, Vortice.DXGI, Vortice.DirectX | 3.8.3 | MIT (© Amer Koleci and contributors) | Particle Studio (viewport) |
| Vortice.Mathematics | 2.1.0 | MIT | Particle Studio (via Vortice) |
| SharpGen.Runtime, SharpGen.Runtime.COM | 2.4.2-beta | MIT | Particle Studio (via Vortice) |
| K4os.Compression.LZ4 | 1.3.8 | MIT (© Milosz Krajewski) | Particle Studio (EDDS) |
| System.Text.Json, System.Text.Encodings.Web, System.IO.Pipelines | 9.0.1 | MIT (© Microsoft) | referenced with `ExcludeAssets="compile;runtime"`, not shipped |
| Microsoft.Win32.Registry, System.Memory, System.ValueTuple, System.Security.* | 4.x | MIT (© Microsoft) | transitive, framework-provided |
| xunit, xunit.runner.visualstudio | 2.9.3, 3.1.4 | Apache-2.0 (© .NET Foundation) | tests only |
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT (© Microsoft) | tests only |

## Code ported from other projects

### lzokay — `src/ParticleStudio/Pfx.Textures/Lzo1x.cs`

The LZO1X decompressor is a C# port of the decompressor of [lzokay](https://github.com/jackoalan/lzokay).

```
The MIT License

Copyright (c) 2018 Jack Andersen

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### Pillow — `src/ParticleStudio/Pfx.Textures/Resample.cs`, `BlockCodec.cs`

The Lanczos resize reproduces the fixed-point arithmetic of Pillow's `Resample.c`, and the BC1/BC3/BC4/BC5 decoder
uses the integer interpolation of Pillow's `BcnDecode.c`, so results match Pillow exactly.

```
The Python Imaging Library (PIL) is

    Copyright © 1997-2011 by Secret Labs AB
    Copyright © 1995-2011 by Fredrik Lundh and contributors

Pillow is the friendly PIL fork. It is

    Copyright © 2010 by Jeffrey 'Alex' Clark and contributors

Like PIL, Pillow is licensed under the open source MIT-CMU License:

By obtaining, using, and/or copying this software and/or its associated
documentation, you agree that you have read, understood, and will comply
with the following terms and conditions:

Permission to use, copy, modify and distribute this software and its
documentation for any purpose and without fee is hereby granted,
provided that the above copyright notice appears in all copies, and that
both that copyright notice and this permission notice appear in supporting
documentation, and that the name of Secret Labs AB or the author not be
used in advertising or publicity pertaining to distribution of the software
without specific, written prior permission.

SECRET LABS AB AND THE AUTHOR DISCLAIMS ALL WARRANTIES WITH REGARD TO THIS
SOFTWARE, INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS.
IN NO EVENT SHALL SECRET LABS AB OR THE AUTHOR BE LIABLE FOR ANY SPECIAL,
INDIRECT OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER RESULTING FROM
LOSS OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE
OR OTHER TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE USE OR
PERFORMANCE OF THIS SOFTWARE.
```

## Fonts

Shipped in `src/Ui/Assets/Fonts` and `src/PBOGPacker/Assets/Fonts`, licence text in `OFL.txt` beside them.

| Font | Copyright | Licence |
|---|---|---|
| Big Shoulders Stencil Display Black 2.001 | 2019 The Big Shoulders Project Authors | SIL OFL 1.1 |
| JetBrains Mono Regular, Medium 2.211 | 2020 The JetBrains Mono Project Authors | SIL OFL 1.1 |
| Play Regular, Bold 2.101 | 2011 Jonas Hecksher, Playtypes, e-types AS; Reserved Font Name "Play", "Playtype", "Playtype Sans" ([google/fonts](https://github.com/google/fonts/tree/main/ofl/play), files unmodified) | SIL OFL 1.1 |

The interface font of the paid version, Bender, was removed: its files say "All rights reserved" and no
redistribution licence could be confirmed. `F.Ui` of PBOG Packer and the shared UI is Play; Particle Studio keeps
Segoe UI Variable (part of Windows, not shipped).

## Photographs

Wikimedia Commons photographs, cropped, scaled and colour-graded by `src/PBOGPacker/tools/photoprep.py`. The
graded files are adaptations: CC BY-SA ones stay under their CC BY-SA licence. Authors are also listed in
`src/PBOGPacker/Assets/Photos/credits.json` and on the PBOG Packer About tab. The six files in `src/Ui/Assets/Photos` are
byte-identical copies of the PBOG Packer files of the same name.

| File | Source | Author | Licence | In |
|---|---|---|---|---|
| `bg_settings.jpg` | [Aerial view of Pripyat.jpg](https://commons.wikimedia.org/wiki/File:Aerial_view_of_Pripyat.jpg) | Omar David Sandoval Sida | [CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0) | PBOG Packer, Ui |
| `logo_stats.jpg` | [ChNPP Unit1control.JPG](https://commons.wikimedia.org/wiki/File:ChNPP_Unit1control.JPG) | Carl A. Willis - User: (WT-shared) Carlwillis at wts wikivoyage | [CC BY-SA 3.0](https://creativecommons.org/licenses/by-sa/3.0) | PBOG Packer |
| `st_deploy.jpg` | [Chernobyl - vehicles 01.jpg](https://commons.wikimedia.org/wiki/File:Chernobyl_-_vehicles_01.jpg) | Paweł 'pbm' Szubert (talk) | [CC BY-SA 3.0](https://creativecommons.org/licenses/by-sa/3.0) | PBOG Packer |
| `bg_env.jpg` | [Control Room of Chernobyl Nuclear Power Plant Unit 3.jpg](https://commons.wikimedia.org/wiki/File:Control_Room_of_Chernobyl_Nuclear_Power_Plant_Unit_3.jpg) | IAEA Imagebank | [CC BY-SA 2.0](https://creativecommons.org/licenses/by-sa/2.0) | PBOG Packer, Ui |
| `st_pack.jpg` | [Crates of Soviet 7.62mm small arms ammunition found in storage on Grenada.jpg](https://commons.wikimedia.org/wiki/File:Crates_of_Soviet_7.62mm_small_arms_ammunition_found_in_storage_on_Grenada.jpg) | Department of State and the Department of Defense employees | Public domain | PBOG Packer, Ui |
| `st_sign.jpg` | [Czech office time stamp with inkpillow.jpg](https://commons.wikimedia.org/wiki/File:Czech_office_time_stamp_with_inkpillow.jpg) | Jiří Sedláček (Frettie) | [CC BY 3.0](https://creativecommons.org/licenses/by/3.0) | PBOG Packer |
| `bg_build.jpg` | [DUGA Radar Array near Chernobyl, Ukraine 2014.jpg](https://commons.wikimedia.org/wiki/File:DUGA_Radar_Array_near_Chernobyl,_Ukraine_2014.jpg) | Ingmar Runge | [CC BY 3.0](https://creativecommons.org/licenses/by/3.0) | PBOG Packer, Ui |
| `st_env.jpg` | [Dosimeter in Chernobyl.jpg](https://commons.wikimedia.org/wiki/File:Dosimeter_in_Chernobyl.jpg) | Gabriella Clare Marino | [CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0) | PBOG Packer |
| `st_build.jpg` | [Duga radar system- wreckage of electronic devices (37885983114).jpg](https://commons.wikimedia.org/wiki/File:Duga_radar_system-_wreckage_of_electronic_devices_(37885983114).jpg) | Jorge Franganillo from Barcelona, Spain | [CC BY 2.0](https://creativecommons.org/licenses/by/2.0) | PBOG Packer |
| `logo_radio.jpg` | [Duga-1 radar 20190504-1.jpg](https://commons.wikimedia.org/wiki/File:Duga-1_radar_20190504-1.jpg) | Suicasmo | [CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0) | PBOG Packer |
| `dlg_ok.jpg` | [Duga-1 radar 20190504-2.jpg](https://commons.wikimedia.org/wiki/File:Duga-1_radar_20190504-2.jpg) | Suicasmo | [CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0) | PBOG Packer |
| `st_host.jpg` | [Funkstation R-123 IMG 1390.JPG](https://commons.wikimedia.org/wiki/File:Funkstation_R-123_IMG_1390.JPG) | Bunkerfunker | [CC BY-SA 3.0](https://creativecommons.org/licenses/by-sa/3.0) | PBOG Packer |
| `dlg_err.jpg` | [Gas Masks Littered on Floor of Abandoned Schoolhouse - Pripyat Ghost Town - Chernobyl Exclusion Zone - Northern Ukraine (27031462131).jpg](https://commons.wikimedia.org/wiki/File:Gas_Masks_Littered_on_Floor_of_Abandoned_Schoolhouse_-_Pripyat_Ghost_Town_-_Chernobyl_Exclusion_Zone_-_Northern_Ukraine_(27031462131).jpg) | Adam Jones from Kelowna, BC, Canada | [CC BY-SA 2.0](https://creativecommons.org/licenses/by-sa/2.0) | PBOG Packer, Ui |
| `tc_tile.jpg` | [Geiger counter (38365895582).jpg](https://commons.wikimedia.org/wiki/File:Geiger_counter_(38365895582).jpg) | Jorge Franganillo from Barcelona, Spain | [CC BY 2.0](https://creativecommons.org/licenses/by/2.0) | PBOG Packer |
| `st_assemble.jpg` | [Military backpack 01.jpg](https://commons.wikimedia.org/wiki/File:Military_backpack_01.jpg) | Pegasovagante | [CC0](http://creativecommons.org/publicdomain/zero/1.0/deed.en) | PBOG Packer, Ui |
| `bg_about.jpg` | [Pripyat amusement park (37746771195).jpg](https://commons.wikimedia.org/wiki/File:Pripyat_amusement_park_(37746771195).jpg) | Jorge Franganillo from Barcelona, Spain | [CC BY 2.0](https://creativecommons.org/licenses/by/2.0) | PBOG Packer |
| `bg_keys.jpg` | [Pripyat ghost town.jpg](https://commons.wikimedia.org/wiki/File:Pripyat_ghost_town.jpg) | Wendelin Jacober | [CC0](http://creativecommons.org/publicdomain/zero/1.0/deed.en) | PBOG Packer |
| `bg_servers.jpg` | [Pripyat panorama 2009-001.jpg](https://commons.wikimedia.org/wiki/File:Pripyat_panorama_2009-001.jpg) | Matti Paavonen | [CC BY-SA 3.0](https://creativecommons.org/licenses/by-sa/3.0) | PBOG Packer |
| `st_protogen.jpg` | [Печатная машинка.jpg](https://commons.wikimedia.org/wiki/File:%D0%9F%D0%B5%D1%87%D0%B0%D1%82%D0%BD%D0%B0%D1%8F_%D0%BC%D0%B0%D1%88%D0%B8%D0%BD%D0%BA%D0%B0.jpg) | Andrey Butko | [CC BY-SA 3.0](https://creativecommons.org/licenses/by-sa/3.0) | PBOG Packer |

## Generated and own assets

- `Assets/Art/grunge.png`, `sand.png`: generated by `src/PBOGPacker/tools/artgen.py` (MIT, this repository).
- Poly Lab marks, drawn from shapes by `tools/brandgen.py` (MIT, this repository) after the owner's logo:
  `src/Ui/Assets/Brand/polylab.ico`, `polylab.png`, `src/PBOGPacker/Assets/app.ico`, `Assets/Brand/logo.png`,
  `src/ParticleStudio/Pfx.App/Assets/app.ico`, `logo.png`.
- Test data in `tests/Pfx.Tests/Golden`: synthetic, seeded images (`tools/ParticleStudio/textures/make_golden.py`),
  their texconv (DirectXTex, MIT) encodes, and `spline_undershoot.csv`, numbers traced from a Workbench Particle
  Editor screenshot of a hand-made test curve (`tools/ParticleStudio/sim/make_spline_golden.py`; the screenshot is not
  stored).
- `tools/ParticleStudio/render/*.ptc`: our own benchmark effects (they name a vanilla material path, no game data).

## Game knowledge, not game data

No Bohemia Interactive files are stored here. Some code reproduces engine behaviour so the tools can interoperate
with DayZ:

- `src/ParticleStudio/Pfx.Core/Schema/schema.json`: key names, defaults and ranges read from DayZ Tools'
  `binarize.exe` by `tools/ParticleStudio/schema/build_schema.py` (which needs the user's own DayZ Tools).
- `src/ParticleStudio/Pfx.Render/Shaders/Viewport.hlsl`: hand-written HLSL reproducing the particle shading the game
  performs, measured with RenderDoc.
- `src/ParticleStudio/Pfx.Sim/XRay`, `Pfx.XRay`: readers and a simulator for X-Ray (S.T.A.L.K.E.R.) particle files,
  written from the file formats and observed behaviour.

## External tools (not shipped)

DayZ Tools (Bohemia Interactive), texconv (Microsoft DirectXTex, MIT), Mikero tools, Python with Pillow/numpy, and
GRAFT are found on the user's machine or downloaded by the user; none of them is redistributed here.
