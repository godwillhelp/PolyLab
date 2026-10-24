using System.IO;
using System.Text;

namespace NDZ.PBOGPacker.Core;

// A C++ project for a mod that stays where it is: <mod>_cpp next to the mod folder, with the GRAFT
// boilerplate filled in from the mod itself — plugin name = mod name (the DLL and the declarations are
// looked up by it), the version of GRAFT the tool's host is, and SCRIPTS_DIR pointing at the mod's own
// scripts, so the build prints the declarations straight into the mod the PBO is packed from.
public static class CppScaffold
{
    public static string DefaultDir(ModInfo m)
    {
        var dir = m.Path.TrimEnd('\\', '/');
        var parent = Path.GetDirectoryName(dir);
        return Path.Combine(string.IsNullOrEmpty(parent) ? dir : parent, Path.GetFileName(dir) + "_cpp");
    }

    // A project with CMakeLists.txt and graft_plugin is only linked, never overwritten.
    public static bool IsProject(string dir) =>
        File.Exists(Path.Combine(dir, "CMakeLists.txt"))
        && File.ReadAllText(Path.Combine(dir, "CMakeLists.txt")).Contains("graft_plugin", StringComparison.OrdinalIgnoreCase);

    public static string Create(ModInfo m, string? dir = null)
    {
        dir ??= DefaultDir(m);
        if (Directory.Exists(dir) && IsProject(dir)) return dir;
        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
            throw new InvalidOperationException("папка " + dir + " уже занята и это не C++-проект GRAFT");
        if (PackLayout.IsUnder(dir, m.Path))
            throw new InvalidOperationException("C++-проект не может лежать внутри мода — он попал бы в PBO");
        if (!System.Text.RegularExpressions.Regex.IsMatch(m.Name, "^[A-Za-z_][A-Za-z0-9_]*$"))
            throw new InvalidOperationException("имя мода «" + m.Name + "» не годится для имени плагина: нужны латиница, цифры и _");

        var scripts = Directory.Exists(m.Path)
            ? Directory.GetDirectories(m.Path).FirstOrDefault(d => Path.GetFileName(d).Equals("scripts", StringComparison.OrdinalIgnoreCase))
            : null;
        if (scripts == null) throw new InvalidOperationException("в моде нет папки Scripts — объявлениям плагина некуда лечь");
        // 3_Game when the mod has it: a native declared there is seen from 4_World and 5_Mission too.
        // 1_Core is never used: the engine compiles it without defines, #ifdef GRAFTED_ does not work there.
        var module = new[] { "3_Game", "4_World", "5_Mission" }.FirstOrDefault(n => Directory.Exists(Path.Combine(scripts, n)))
                     ?? throw new InvalidOperationException("в " + scripts + " нет ни 3_Game, ни 4_World, ни 5_Mission");
        var rel = Path.GetRelativePath(dir, scripts).Replace('\\', '/');

        Directory.CreateDirectory(Path.Combine(dir, "src"));
        Write(Path.Combine(dir, "CMakeLists.txt"), CMake(m.Name, module, rel));
        Write(Path.Combine(dir, "CMakePresets.json"), Presets);
        Write(Path.Combine(dir, "src", "plugin.cpp"), Plugin(m.Name, module));
        Write(Path.Combine(dir, ".gitignore"), "build/\nout/\n");
        return dir;
    }

    private static void Write(string path, string text) => File.WriteAllText(path, text.Replace("\r\n", "\n"), new UTF8Encoding(false));

    private static string CMake(string name, string module, string scripts) => $$"""
        cmake_minimum_required(VERSION 3.30)
        project({{name.ToLowerInvariant()}}_graft CXX C)

        set(CMAKE_CXX_STANDARD 23)
        set(CMAKE_CXX_STANDARD_REQUIRED ON)
        if(MSVC)
            # Статический рантайм: MSVC-редиста на хостингах нет. Форма с $<CONFIG:Debug> обязательна:
            # graft наследует это значение, и обе половины должны совпасть.
            set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
        endif()

        set(GRAFT_BOOT_FILE "${CMAKE_BINARY_DIR}/graft.boot.cmake" CACHE FILEPATH "graft.boot.cmake")
        if(NOT EXISTS "${GRAFT_BOOT_FILE}")
            file(DOWNLOAD https://raw.githubusercontent.com/KRdayzmodding/KR_GRAFTED/{{GraftDoctor.Release}}/cmake/graft.boot.cmake "${GRAFT_BOOT_FILE}")
        endif()
        include("${GRAFT_BOOT_FILE}")
        # Та же версия GRAFT, что у хоста на сервере: плагин под другой ABI хост не загрузит.
        graft_import(graft https://github.com/KRdayzmodding/KR_GRAFTED TAG {{GraftDoctor.Release}})

        # NAME = имя мода: по нему PBOG Packer ищет {{name}}.grafted.dll и grafted_natives_{{name}}.c.
        # SCRIPTS_DIR = скрипты самого мода: сборка печатает объявления прямо в мод, откуда пакуется PBO.
        graft_plugin({{name.ToLowerInvariant()}}
            NAME        {{name}}
            VERSION     1
            SOURCES     src/plugin.cpp
            MODULES     {{module}}
            SCRIPTS_DIR "${CMAKE_CURRENT_SOURCE_DIR}/{{scripts}}")

        """;

    private static string Plugin(string name, string module) => $$"""
        // {{name}}: функции на C++, которые скрипты мода зовут как обычные нативы.
        //
        // В скриптах каждый вызов оборачивай, чтобы мод компилировался и без DLL:
        //     #ifdef GRAFTED_{{name}}
        //         int n = {{name}}_Ping(1);
        //     #endif
        #include <string>
        #include <string_view>

        #include "graft/native.hpp"

        GRAFT_PLUGIN("{{name}}", 1);

        namespace {

        int Ping(int value) {
            return value + 1;
        }

        }  // namespace

        // По строке на функцию: функция C++ и её имя в скриптах. "{{module}}" — модуль, куда ляжет
        // объявление; из модулей выше по номеру натив тоже виден.
        GRAFT_BINDINGS("{{module}}") {
            bind.global<&Ping>("{{name}}_Ping");
        }

        """;

    private const string Presets = """
        {
          "version": 6,
          "cmakeMinimumRequired": { "major": 3, "minor": 30 },
          "configurePresets": [
            {
              "name": "base",
              "hidden": true,
              "generator": "Ninja",
              "binaryDir": "${sourceDir}/build/${presetName}",
              "cacheVariables": {
                "CMAKE_RUNTIME_OUTPUT_DIRECTORY": "${sourceDir}/out/${presetName}",
                "CMAKE_LIBRARY_OUTPUT_DIRECTORY": "${sourceDir}/out/${presetName}",
                "CMAKE_ARCHIVE_OUTPUT_DIRECTORY": "${sourceDir}/build/${presetName}/lib"
              }
            },
            { "name": "release", "displayName": "Release", "inherits": "base", "cacheVariables": { "CMAKE_BUILD_TYPE": "Release" } },
            { "name": "debug", "displayName": "Debug", "inherits": "base", "cacheVariables": { "CMAKE_BUILD_TYPE": "Debug" } }
          ],
          "buildPresets": [
            { "name": "release", "configurePreset": "release" },
            { "name": "debug", "configurePreset": "debug" }
          ]
        }

        """;
}
