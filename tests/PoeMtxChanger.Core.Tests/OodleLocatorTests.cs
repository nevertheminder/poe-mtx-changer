using PoeMtxChanger.Core.Compression;

namespace PoeMtxChanger.Core.Tests;

public class OodleLocatorTests
{
    [Fact]
    public void Program_folder_comes_first_with_the_newest_version_first()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateTempSubdirectory("pmc-oodle-").FullName;
        try
        {
            foreach (var name in new[] { "oo2core_8_win64.dll", "oo2core_9_win64.dll", "oo2core_9_win32.dll", "notes.txt" })
                File.WriteAllText(Path.Combine(dir, name), "");
            var first = OodleLocator.Candidates(dir).Take(2).ToArray();
            Assert.Equal(["oo2core_9_win64.dll", "oo2core_8_win64.dll"], first.Select(c => Path.GetFileName(c.Path)));
            Assert.All(first, c => Assert.Equal("program folder", c.Source));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_file_that_is_not_a_working_library_is_not_loaded()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateTempSubdirectory("pmc-oodle-").FullName;
        try
        {
            var fake = Path.Combine(dir, "oo2core_9_win64.dll");
            File.WriteAllText(fake, "not a library");
            var codec = NativeOodleCodec.TryLoad(fake);
            Assert.False(codec.IsAvailable);
        }
        finally { Directory.Delete(dir, true); }
    }
}
