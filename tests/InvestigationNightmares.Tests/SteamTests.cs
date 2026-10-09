using System.IO;
using InvestigationNightmares.Util;
using Xunit;

namespace InvestigationNightmares.Tests;

public class SteamTests
{
    [Fact]
    public void Parses_current_libraryfolders_format()
    {
        var vdf = SteamLibraries.ParseVdf("""
            "libraryfolders"
            {
                "0" { "path" "C:\\Program Files (x86)\\Steam" "apps" { "893180" "123" } }
                "1" { "path" "D:\\SteamLibrary" }
            }
            """);
        var lib = (System.Collections.Generic.Dictionary<string, object>)vdf["libraryfolders"];
        var one = (System.Collections.Generic.Dictionary<string, object>)lib["1"];
        Assert.Equal(@"D:\SteamLibrary", one["PATH"]);
    }

    [Fact]
    public void Finds_apps_in_a_secondary_library_by_manifest()
    {
        var tmp = Directory.CreateTempSubdirectory("in-steam").FullName;
        var steam = Path.Combine(tmp, "Steam");
        var lib2 = Path.Combine(tmp, "Lib2");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        Directory.CreateDirectory(Path.Combine(lib2, "steamapps", "common", "P4G Install"));
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
            $"\"libraryfolders\" {{ \"0\" {{ \"path\" \"{steam.Replace("\\", "\\\\")}\" }} \"1\" {{ \"path\" \"{lib2.Replace("\\", "\\\\")}\" }} }}");
        File.WriteAllText(Path.Combine(lib2, "steamapps", "appmanifest_1113000.acf"),
            "\"AppState\" { \"appid\" \"1113000\" \"installdir\" \"P4G Install\" }");

        Assert.Equal(Path.Combine(lib2, "steamapps", "common", "P4G Install"), SteamLibraries.FindApp(new[] { steam }, 1113000, "Persona 4 Golden"));
        Assert.Null(SteamLibraries.FindApp(new[] { steam }, 893180, "CatherineClassic"));
        Directory.Delete(tmp, true);
    }

    [Fact]
    public void Old_format_numbered_paths_are_libraries()
    {
        var tmp = Directory.CreateTempSubdirectory("in-steam-old").FullName;
        Directory.CreateDirectory(Path.Combine(tmp, "steamapps"));
        File.WriteAllText(Path.Combine(tmp, "steamapps", "libraryfolders.vdf"),
            "\"LibraryFolders\" { \"TimeNextStatsReport\" \"1\" \"ContentStatsID\" \"2\" \"1\" \"E:\\\\Games\" }");
        var roots = SteamLibraries.LibraryRoots(tmp);
        Assert.Contains(@"E:\Games", roots);
        Directory.Delete(tmp, true);
    }
}
