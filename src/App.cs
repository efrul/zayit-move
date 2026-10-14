using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;

static class App
{
    public const string Name = "העברת הספרייה של זית";

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--selftest") return SelfTest.Run(args[1]);
        if (args.Length >= 3 && args[0] == "--prefs") return SelfTest.PrefsIo(args[1], args[2]);
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException += (s, e) =>
        {
            MessageBox.Show(e.Exception.Message, Name, MessageBoxButton.OK, MessageBoxImage.Error, MessageBoxResult.OK, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
            e.Handled = true;
        };
        return app.Run(new MainWindow());
    }
}

// Checks without the window, on fake libraries in a folder of their own, with another registry key and history file
// in place of Zayit's: ZayitMove.exe --selftest DIR (the report goes to DIR\report.txt).
static class SelfTest
{
    static readonly StringBuilder report = new StringBuilder();
    static int failures;

    static void Check(bool ok, string what)
    {
        report.AppendLine((ok ? "ok    " : "FAIL  ") + what);
        if (!ok) failures++;
    }

    static void MakeLibrary(string dir, int mb)
    {
        Directory.CreateDirectory(dir);
        var data = new byte[mb << 20];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 31 % 251);
        File.WriteAllBytes(Path.Combine(dir, "seforim.db"), data);
        Directory.CreateDirectory(Path.Combine(dir, "seforim.db.lucene"));
        File.WriteAllBytes(Path.Combine(dir, "seforim.db.lucene", "_0.cfs"), new byte[1 << 20]);
        File.WriteAllText(Path.Combine(dir, "lexical.db"), "lex");
        File.WriteAllText(Path.Combine(dir, "catalog.pb"), "cat");
        File.WriteAllText(Path.Combine(dir, "release_info.txt"), "20261011223929");
    }

    public static int Run(string dir)
    {
        dir = Path.GetFullPath(dir);
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable("ZAYITMOVE_PREFS_KEY", @"Software\ZayitMoveTest\Prefs");
            Environment.SetEnvironmentVariable("ZAYITMOVE_DEFAULT_DIR", Path.Combine(dir, "default"));
            Environment.SetEnvironmentVariable("ZAYITMOVE_HISTORY", Path.Combine(dir, "history.tsv"));
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\ZayitMoveTest", false);
            Action<string> log = m => report.AppendLine("      log: " + m);

            // The registry encoding, against what Java itself wrote for Zayit
            Check(Prefs.Encode(@"C:\Users\Someone\Music") == "/C:///Users///Someone///Music", "encode a path as Java does");
            var tricky = "D:\\Zayit library/ספרים א\u0007~";
            Check(Prefs.Decode(Prefs.Encode(tricky)) == tricky, "encode and decode back: " + Prefs.Encode(tricky));
            Check(Prefs.Decode("/System") == "System" && Prefs.Decode("/u05d0/u05e8") == "אר", "decode Zayit's own values");

            var home = Library.DefaultDir;
            MakeLibrary(home, 3);
            Directory.CreateDirectory(Path.Combine(home, "settings"));
            File.WriteAllText(Path.Combine(home, "settings", "user_settings.db"), "NOTES");
            long size = Library.Size(home);
            Check(Library.Same(Library.CurrentDir, home) && Library.Exists(home), "an empty setting means Zayit's own folder");
            Check(Library.Version(home) == "11/10/2026", "the release as a date: " + Library.Version(home));
            Check(Library.Destination(@"X:\") == @"X:\Zayit library" && Library.Destination(@"X:\a\Zayit library") == @"X:\a\Zayit library" && Library.Same(Library.Destination(home), home), "where a chosen place puts the library");

            // 1. Moved on the same drive: renamed, nothing copied; the notes stay
            var away = Path.Combine(dir, "drive", Library.FolderName);
            Library.Move(home, away, false, (d, t) => { }, () => false, log);
            Check(Library.Exists(away) && !Library.Exists(home) && Library.Size(away) == size, "moved: the library is there and gone from here");
            Check(File.ReadAllText(Path.Combine(home, "settings", "user_settings.db")) == "NOTES", "the notes stayed in Zayit's folder");
            Check(Library.Same(Library.CurrentDir, away) && Prefs.Get("database_path", "") == Path.Combine(away, "seforim.db"), "Zayit points at the new place");
            Check(History.LastMove() != null && History.LastMove().Kind == "move" && Library.Same(History.LastMove().From, home), "the move is in the history");

            // 2. Copied back, keeping the copy: Zayit is home, the other copy is offered
            long seen = 0;
            Library.Move(away, home, true, (d, t) => seen = d, () => false, log);
            Check(seen == size && Library.Exists(home) && Library.Exists(away), "copied with a copy left behind (" + seen + " bytes reported)");
            Check(Library.Same(Library.CurrentDir, home) && Prefs.Get("database_path", "x") == "", "back home is written as an empty setting");
            Check(File.ReadAllBytes(Path.Combine(home, "seforim.db")).SequenceEqual(File.ReadAllBytes(Path.Combine(away, "seforim.db"))), "the copy has the same bytes");
            var copies = History.Copies(Library.CurrentDir);
            Check(copies.Count == 1 && Library.Same(copies[0], away), "the copy left behind is offered");

            // 3. Switching between the two copies, instantly
            Library.SwitchTo(away, log);
            Check(Library.Same(Library.CurrentDir, away), "switched to the other copy");
            Check(History.Copies(Library.CurrentDir).Any(c => Library.Same(c, home)), "and the first is offered in turn");
            Library.SwitchTo(home, log);

            // 4. Stopped half way: the partial copy goes, Zayit still points where it did
            var third = Path.Combine(dir, "third", Library.FolderName);
            int calls = 0;
            try
            {
                Library.Move(home, third, true, (d, t) => { }, () => ++calls > 1, log);
                Check(false, "a stopped copy should not finish");
            }
            catch (Stopped) { Check(true, "a stop ends the copy"); }
            Check(!Directory.Exists(third) && Library.Same(Library.CurrentDir, home) && Library.Exists(home), "after the stop: no partial copy, Zayit unchanged");

            // 5. Refusals: a place inside the library, a place that has one, no room
            Check(Throws(() => Library.Move(home, Path.Combine(home, "x"), false, (d, t) => { }, () => false, log)), "refuses to move into its own folder");
            Check(Throws(() => Library.Move(home, away, false, (d, t) => { }, () => false, log)), "refuses a place that already has a library");
            File.WriteAllBytes(Path.Combine(home, "seforim.db-wal"), new byte[10]);
            Check(Throws(() => Library.Move(home, third, false, (d, t) => { }, () => false, log)), "refuses while seforim.db-wal is not empty");
            File.Delete(Path.Combine(home, "seforim.db-wal"));

            // 6. A copy deleted; never the one in use
            Check(Throws(() => Library.DeleteCopy(home, log)), "refuses to delete the library in use");
            Library.DeleteCopy(away, log);
            Check(!Library.Exists(away) && !Directory.Exists(away), "the copy and its folder are gone");

            // 7. The library missing (a drive not connected): back to Zayit's folder
            Prefs.Set("database_path", @"Q:\Zayit library\seforim.db");
            Check(!Library.Exists(Library.CurrentDir) && Library.Free(Library.CurrentDir) == -1, "a missing drive is seen as missing");
            Library.Reset(log);
            Check(Library.Same(Library.CurrentDir, home), "reset points Zayit at its own folder");

            // 8. Moved back to where it was, without a copy (the "return" of the restore card)
            Library.Move(home, away, false, (d, t) => { }, () => false, log);
            Library.Move(away, home, false, (d, t) => { }, () => false, log);
            Check(Library.Exists(home) && !Directory.Exists(away) && Library.Size(home) == size, "there and back again, same size, empty folder removed");
        }
        catch (Exception e)
        {
            Check(false, "unexpected: " + e);
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\ZayitMoveTest", false);
        }
        report.AppendLine(failures == 0 ? "ALL OK" : failures + " FAILED");
        File.WriteAllText(Path.Combine(dir, "report.txt"), report.ToString(), Encoding.UTF8);
        return failures == 0 ? 0 : 1;
    }

    static bool Throws(Action a)
    {
        try { a(); return false; } catch (Problem) { return true; }
    }

    // For the check against Java: writes the values of `input` (name<TAB>value lines) under the key in
    // ZAYITMOVE_PREFS_KEY, or with "read" reads every value there into `output`, decoded.
    public static int PrefsIo(string mode, string file)
    {
        if (mode == "write")
        {
            foreach (var line in File.ReadAllLines(file, Encoding.UTF8))
            {
                var f = line.Split('\t');
                if (f.Length == 2) Prefs.Set(f[0], f[1]);
            }
            return 0;
        }
        var sb = new StringBuilder();
        using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Environment.GetEnvironmentVariable("ZAYITMOVE_PREFS_KEY")))
            foreach (var n in key.GetValueNames().OrderBy(x => x)) sb.AppendLine(n + "\t" + Prefs.Get(n, ""));
        File.WriteAllText(file, sb.ToString(), Encoding.UTF8);
        return 0;
    }
}
