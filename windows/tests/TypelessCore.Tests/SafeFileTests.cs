namespace TypelessCore.Tests;

/// <summary>The app's own files survive damage: one that can't be read is moved aside, not saved over.</summary>
public class SafeFileTests
{
    private static string TemporaryFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), "safefile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void MovesADamagedFileAsideWithTheTime()
    {
        var folder = TemporaryFolder();
        try
        {
            var file = Path.Combine(folder, "usage.json");
            var date = new DateTime(2026, 10, 11, 14, 30, 5);
            File.WriteAllText(file, "{broken");
            var aside = SafeFile.MoveAside(file, date);
            Assert.Equal("usage.json.corrupt-20261011-143005", Path.GetFileName(aside));
            Assert.False(File.Exists(file));
            Assert.Equal("{broken", File.ReadAllText(aside!));
            // A second one in the same second doesn't overwrite the first.
            File.WriteAllText(file, "{again");
            Assert.Equal("usage.json.corrupt-20261011-143005-2", Path.GetFileName(SafeFile.MoveAside(file, date)));
            Assert.Null(SafeFile.MoveAside(file, date)); // nothing left to move
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void WritesWholeFilesWithoutAByteOrderMark()
    {
        var folder = TemporaryFolder();
        try
        {
            var file = Path.Combine(folder, "settings.json");
            SafeFile.WriteAllText(file, "{\"a\":\"中文\"}");
            SafeFile.WriteAllText(file, "{\"b\":1}");
            Assert.Equal("{\"b\":1}"u8.ToArray(), File.ReadAllBytes(file));
            Assert.Equal("{\"b\":1}", SafeFile.ReadAllText(file));
            Assert.Equal(["settings.json"], Directory.GetFiles(folder).Select(Path.GetFileName));
            Assert.Throws<FileNotFoundException>(() => SafeFile.ReadAllText(Path.Combine(folder, "missing.json")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void DamagedLedgerIsKeptAsideNotOverwritten()
    {
        var folder = TemporaryFolder();
        try
        {
            var file = Path.Combine(folder, "usage.json");
            File.WriteAllBytes(file, new byte[64]); // what a power cut mid-write can leave
            var ledger = UsageLedger.Load(file);
            Assert.NotNull(ledger);
            Assert.True(ledger.IsEmpty);
            var names = Directory.GetFiles(folder).Select(Path.GetFileName).ToList();
            Assert.Single(names);
            Assert.StartsWith("usage.json.corrupt-", names[0]);
            ledger.Save(file);
            Assert.Equal(2, Directory.GetFiles(folder).Length);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
