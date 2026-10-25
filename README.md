# Poly Lab

Free, open-source tools for DayZ modding, for Windows. The interface is in English, Russian and Ukrainian.

[Русский](#по-русски)

| Program | What it does |
|---|---|
| **Poly Lab** (`PolyLab.exe`) | The launcher. Installs, updates and starts the tools from this repository's GitHub releases, and downloads GRAFT for PBOG Packer. |
| **PBOG Packer** (`PBOG_Packer.exe`) | Packs and signs client and server mods like pboProject (configs, rvmats and models binarized), builds native server plugins on [GRAFT](https://github.com/KRdayzmodding/KR_GRAFTED) from C++, deploys to a local server, edits the server's `-mod` / `-serverMod` lines and starts or stops it. |
| **Particle Studio** (`Particle_Studio.exe`) | Editor for DayZ particle effects (`.ptc`): emitters, curves, textures, ready-made recipes, a live viewport, import of X-Ray (S.T.A.L.K.E.R.) effects and a live link that shows the effect in the running game. |

## Install

Download the Poly Lab installer from the [releases](../../releases) page and run it. The launcher lists the tools;
**Install** downloads a tool, checks its SHA-256 against the release and unpacks it next to the launcher. Updates and
release notes appear in the same window. Each tool can also be downloaded as a zip from its release and run without the
launcher.

The interface language follows the Windows display language on the first start (Ukrainian → Ukrainian; Russian,
Belarusian, Kazakh → Russian; anything else → English). The globe button in the launcher, the menu or settings in each
tool switch it; the choice is saved.

Requirements: Windows 10 or 11 x64. PBOG Packer needs [DayZ Tools](https://store.steampowered.com/app/830640/) for
packing and signing; GRAFT plugins also need Visual Studio 2022 with C++ (MSVC and the Windows SDK), the C++ Clang tools
and CMake. Its **Environment** tab checks all of them and tells you how to install what is missing.

## GRAFT

GRAFT (by 6wingSeraph, GPL-3.0-or-later with a plugin exception) is not part of this repository. PBOG Packer pins the
GRAFT release it supports; the launcher downloads exactly that release from the
[GRAFT releases](https://github.com/KRdayzmodding/KR_GRAFTED/releases) and checks its digest, so a new GRAFT ABI never
reaches you before PBOG Packer supports it. Without the launcher, set the paths to `graft.exe` and `hid.dll` in PBOG
Packer's settings.

## Build from source

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) on Windows.

```
git clone <this repository>
cd PolyLab
dotnet build PolyLab.slnx -c Release
```

| Project | Output |
|---|---|
| `src/Launcher` | `PolyLab.exe` |
| `src/PBOGPacker` | `PBOG_Packer.exe` |
| `src/ParticleStudio/Pfx.App` | `Particle_Studio.exe` (with `Pfx.Core`, `Pfx.Sim`, `Pfx.Render`, `Pfx.Textures`, `Pfx.XRay`, `Pfx.Link`) |
| `src/Ui` | `PolyLab.Ui`, the shared theme and controls |

**Live-link mod.** Particle Studio carries the small DayZ mod `@PolyLab_PfxLink` inside its exe and hands it out on
first use. Pack it before building Particle Studio (needs DayZ Tools):

```
powershell -File link/pack.ps1 -Out link/dist
```

A build without `link/dist` works, but has no live-link mod to hand out.

**Interface texts** live in `Lang/en.xaml`, `ru.xaml` and `uk.xaml` of each program; all three must have the same keys
(the tests check it). Particle Studio's tables can be merged with `tools/ParticleStudio/lang/merge_lang.py`.

## Tests

```
dotnet test PolyLab.slnx
```

Most tests need nothing else. Some run only when an environment variable is set:

| Variable | Tests |
|---|---|
| `POLYLAB_NET=1` | download the real GRAFT release from GitHub |
| `POLYLAB_E2E=1` | end to end: the built launcher installs the tools from a local fake of the GitHub API and starts PBOG Packer (`POLYLAB_E2E_SHOTS=<folder>` keeps its screenshots) |
| `PFX_CORPUS=<folders>` | Particle Studio against a corpus of `.ptc`, `.edds` and `.paa` files (`;`-separated folders, for example an unpacked `P:\graphics`) |
| `PFX_TEXCONV=<texconv.exe>` | texture decoding compared with DirectXTex `texconv` |
| `PFX_XRAY_*`, `PFX_TORNADO_GOLDEN` | X-Ray import and simulation against reference data you provide |

Programs can render a window to a PNG without user data, for layout checks:
`PolyLab.exe --root <tmp> --data <tmp> --shot out.png --size 1280x720 --state installed --lang en`;
`PBOG_Packer.exe --shot out.png --size 1280x720 --lang en` (mock data; set `POLYLAB_STATE_DIR` to an empty folder);
`Particle_Studio.exe effect.ptc --checks <out> --settings-dir <tmp>` (its own UI checks: screenshots in the three
languages at three sizes, clipped text, tooltips).

## Licence

Poly Lab is under the [MIT licence](LICENSE). Fonts, photos, ported code and packages keep their own licences, listed in
[THIRD_PARTY.md](THIRD_PARTY.md). DayZ is a trademark of Bohemia Interactive; Poly Lab is not affiliated with Bohemia
Interactive.

---

## По-русски

Poly Lab — бесплатные инструменты с открытым кодом для моддинга DayZ под Windows. Интерфейс на английском, русском и
украинском.

- **Poly Lab** (`PolyLab.exe`) — лаунчер: устанавливает, обновляет и запускает программы из релизов этого репозитория на
  GitHub и скачивает GRAFT для PBOG Packer.
- **PBOG Packer** (`PBOG_Packer.exe`) — упаковка и подпись клиентских и серверных модов как в pboProject (с бинаризацией
  конфигов, rvmat и моделей), сборка нативных серверных плагинов на GRAFT из C++, деплой на локальный сервер, правка
  строк `-mod` / `-serverMod` в батнике, запуск и остановка сервера.
- **Particle Studio** (`Particle_Studio.exe`) — редактор частиц DayZ (`.ptc`): эмиттеры, кривые, текстуры, готовые
  решения, живой предпросмотр, импорт эффектов X-Ray (S.T.A.L.K.E.R.) и связь с запущенной игрой.

**Установка.** Скачайте установщик Poly Lab со страницы [релизов](../../releases). Кнопка «Установить» в лаунчере скачивает
программу, сверяет её SHA-256 с релизом и распаковывает рядом с лаунчером; обновления и заметки к релизам видны там же.
Любую программу можно скачать и zip-архивом из её релиза и запускать без лаунчера.

**Язык.** При первом запуске берётся язык интерфейса Windows: украинский → украинский; русский, белорусский, казахский →
русский; остальные → английский. Переключатель — кнопка с глобусом в лаунчере, меню или настройки в программах; выбор
сохраняется.

**Что нужно.** Windows 10 или 11 x64. PBOG Packer нужны DayZ Tools; для плагинов GRAFT ещё Visual Studio 2022 с C++,
компонент C++ Clang tools и CMake. Вкладка «Окружение» проверяет всё это и подсказывает, как поставить недостающее.

**GRAFT** (автор 6wingSeraph, GPL-3.0-or-later с исключением для плагинов) в репозиторий не входит. PBOG Packer
закрепляет поддерживаемый релиз GRAFT, лаунчер скачивает именно его с GitHub и сверяет контрольную сумму.

**Сборка из исходников.** Нужен .NET 8 SDK: `dotnet build PolyLab.slnx -c Release`. Мод связи с игрой для Particle Studio
пакуется заранее командой `powershell -File link/pack.ps1 -Out link/dist` (нужны DayZ Tools). Тесты: `dotnet test
PolyLab.slnx`; тесты с сетью, сквозной тест и тесты на корпусе файлов включаются переменными из таблицы выше.

**Лицензия** — MIT, сторонние шрифты, фото и код — в [THIRD_PARTY.md](THIRD_PARTY.md).
