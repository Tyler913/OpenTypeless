namespace TypelessCore.Tests;

/// <summary>The app's own files survive damage: one that can't be read is moved aside, not saved over.</summary>
public class SafeFileTests
{

    [Fact]
    public void FailedMoveAsideMustNotAllowLaterSaveToDestroyCorruptBytes()
    {
        // Held files block renaming and reading only on Windows (other systems' locks are advisory).
        if (!OperatingSystem.IsWindows()) return;
        var folder = TemporaryFolder();
        try
        {
            var file = Path.Combine(folder, "usage.json");
            const string original = "{damaged lifetime totals";
            File.WriteAllText(file, original);
            UsageLedger ledger;
            // Windows permits reading but denies renaming until this handle is released.
            using (var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.Null(SafeFile.MoveAside(file));
                ledger = Assert.IsType<UsageLedger>(UsageLedger.Load(file));
                Assert.Equal(original, File.ReadAllText(file));
            }
            ledger.Save(file);
            Assert.Equal(original, File.ReadAllText(file));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }


    [Fact]
    public void UnreadableLedgerIsNotRenamedOrSavedAfterAccessReturns()
    {
        // Held files block renaming and reading only on Windows (other systems' locks are advisory).
        if (!OperatingSystem.IsWindows()) return;
        var folder = TemporaryFolder();
        try
        {
            var file = Path.Combine(folder, "usage.json");
            const string original = "{not JSON, but unreadable is not corrupt";
            File.WriteAllText(file, original);
            UsageLedger ledger;
            using (var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Throws<IOException>(() => SafeFile.ReadAllText(file, attempts: 1));
                ledger = Assert.IsType<UsageLedger>(UsageLedger.Load(file));
                Assert.True(ledger.IsEmpty);
                Assert.Equal(["usage.json"], Directory.GetFiles(folder).Select(Path.GetFileName));
            }
            ledger.Save(file);
            Assert.Equal(original, File.ReadAllText(file));
            Assert.Equal(["usage.json"], Directory.GetFiles(folder).Select(Path.GetFileName));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void MoveAsidePreservesEveryCollision()
    {
        var folder = TemporaryFolder();
        try
        {
            var file = Path.Combine(folder, "usage.json");
            var date = new DateTime(2026, 10, 11, 14, 30, 5);
            var saved = new List<string>();
            foreach (var contents in new[] { "first", "second", "third" })
            {
                File.WriteAllText(file, contents);
                saved.Add(Assert.IsType<string>(SafeFile.MoveAside(file, date)));
            }
            Assert.EndsWith("-3", saved[2]);
            Assert.Equal(["first", "second", "third"], saved.Select(File.ReadAllText));
            Assert.Null(SafeFile.MoveAside(file, date));
            Assert.Equal(3, Directory.GetFiles(folder).Length);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

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
