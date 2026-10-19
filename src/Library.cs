using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

// Where desktop Zayit keeps its library, and moving it: copy, check, point Zayit there, and (unless a copy is to stay
// behind) delete the old one. Zayit resolves its library as SEFORIMAPP_DATABASE_PATH, else the "database_path"
// setting, else seforim.db in its own folder (DatabasePathProvider, desktop 0.6.4). Notes, settings and tabs are not
// the library: they stay in Zayit's own folder whatever happens here.
static class Library
{
    // What a library is made of (what Zayit's onboarding unpacks), and what Zayit leaves next to it while open.
    public static readonly string[] Names =
        { "seforim.db", "seforim.db.lucene", "seforim.db.lookup.lucene", "lexical.db", "catalog.pb", "release_info.txt", "subset_info.txt" };
    public static readonly string[] Leftovers = { "seforim.db-wal", "seforim.db-shm", "seforim.db-journal", "delta-cache" };

    public const string FolderName = "Zayit library";
    public const long Margin = 200L << 20;

    // Zayit's own folder (FileKit's databasesDir); ZAYITMOVE_DEFAULT_DIR replaces it in the tests.
    public static string DefaultDir
    {
        get
        {
            var t = Environment.GetEnvironmentVariable("ZAYITMOVE_DEFAULT_DIR");
            if (!string.IsNullOrEmpty(t)) return Path.GetFullPath(t);
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "io.github.kdroidfilter.seforimapp", "databases");
        }
    }

    // The folder of the library Zayit opens now (by its setting; the environment variable is reported apart).
    public static string CurrentDir
    {
        get
        {
            var p = Prefs.Get("database_path", "").Trim();
            if (p.Length == 0) return DefaultDir;
            try { return Path.GetDirectoryName(Path.GetFullPath(p)); } catch { return DefaultDir; }
        }
    }

    // A library chosen through the environment variable (the small-library tool's "Zayit switches to it"), which
    // Zayit puts before its setting: while it is there, moving changes nothing Zayit sees.
    public static string EnvOverride
    {
        get
        {
            if (Environment.GetEnvironmentVariable("ZAYITMOVE_PREFS_KEY") != null) return null;
            var v = Environment.GetEnvironmentVariable("SEFORIMAPP_DATABASE_PATH", EnvironmentVariableTarget.User);
            if (string.IsNullOrWhiteSpace(v)) v = Environment.GetEnvironmentVariable("SEFORIMAPP_DATABASE_PATH", EnvironmentVariableTarget.Machine);
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
    }

    public static void ClearEnvOverride()
    {
        Environment.SetEnvironmentVariable("SEFORIMAPP_DATABASE_PATH", null, EnvironmentVariableTarget.User);
    }

    // A library Zayit can open. A database cut in pieces is not one (see IsSplit).
    public static bool Exists(string dir) { return dir != null && File.Exists(Path.Combine(dir, "seforim.db")) && !IsSplit(dir); }

    // seforim.db kept as seforim.db, seforim.db001, seforim.db002... (SQLite's multiplex VFS), as Pardes keeps it on a
    // FAT32 memory card. Desktop Zayit's SQLite has no such VFS: it would find seforim.db, then fail on it as
    // "malformed".
    public static bool IsSplit(string dir) { return dir != null && File.Exists(Path.Combine(dir, "seforim.db001")); }

    // The pieces after seforim.db of a split database, as they are on disk.
    public static List<string> Chunks(string dir)
    {
        try { return Directory.GetFiles(dir, "seforim.db???").Where(f => Regex.IsMatch(Path.GetFileName(f), @"^seforim\.db\d{3}$")).ToList(); }
        catch { return new List<string>(); }
    }

    // Whether a database file is whole, by its header: the page count times the page size must be its length (the
    // count is kept only while "version-valid-for" matches the change counter). A piece of a split database, or a copy
    // cut short, fails here, where Zayit would only see it when opening it.
    public static bool Whole(string db)
    {
        try
        {
            using (var f = new FileStream(db, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var h = new byte[100];
                int got = 0, n;
                while (got < h.Length && (n = f.Read(h, got, h.Length - got)) > 0) got += n;
                if (got < h.Length || Encoding.ASCII.GetString(h, 0, 16) != "SQLite format 3\0") return false;
                long page = h[16] << 8 | h[17];
                if (page == 1) page = 65536;
                uint change = BigEndian(h, 24), pages = BigEndian(h, 28), valid = BigEndian(h, 92);
                if (change != valid || pages == 0) return true;
                return page * pages == f.Length;
            }
        }
        catch { return false; }
    }

    static uint BigEndian(byte[] b, int i) { return (uint)(b[i] << 24 | b[i + 1] << 16 | b[i + 2] << 8 | b[i + 3]); }

    public static bool Same(string a, string b)
    {
        if (a == null || b == null) return false;
        return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDefault(string dir) { return Same(dir, DefaultDir); }

    public static long Size(string dir)
    {
        long n = 0;
        foreach (var name in Names)
        {
            var p = Path.Combine(dir, name);
            try
            {
                if (File.Exists(p)) n += new FileInfo(p).Length;
                else if (Directory.Exists(p)) n += new DirectoryInfo(p).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            }
            catch { }
        }
        return n;
    }

    // The library's release ("2026-10-11" and the like), from release_info.txt; null when it has none.
    public static string Version(string dir)
    {
        try
        {
            var f = Path.Combine(dir, "release_info.txt");
            if (!File.Exists(f)) return null;
            var line = File.ReadAllLines(f).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            if (line == null) return null;
            var eq = line.IndexOf('=');
            if (eq >= 0) line = line.Substring(eq + 1).Trim();
            // "20261011223929": the release's date and time
            DateTime d;
            if (line.Length >= 8 && DateTime.TryParseExact(line.Substring(0, 8), "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out d))
                return d.ToString("dd/MM/yyyy");
            return line;
        }
        catch { return null; }
    }

    public static bool IsSmall(string dir) { return File.Exists(Path.Combine(dir, "subset_info.txt")); }

    // Free bytes on the drive of `dir` (or of its nearest existing parent); -1 when the drive is not there.
    public static long Free(string dir)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(dir));
            var d = new DriveInfo(root);
            return d.IsReady ? d.AvailableFreeSpace : -1;
        }
        catch { return -1; }
    }

    // FAT32 (or FAT) on the drive of `dir`: it takes no file of 4 GiB or more, and seforim.db is about 8 GB.
    public static bool IsFat(string dir)
    {
        try
        {
            var d = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir)));
            return d.IsReady && d.DriveFormat.StartsWith("FAT", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static string FatText(string dir)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(dir)).TrimEnd('\\');
        return "הכונן " + root + " מפורמט כ־FAT32, שלא יכול לשמור קובץ של 4 GB ומעלה, והקובץ seforim.db שוקל כ־8 GB. " +
               "אפשר לפרמט אותו כ־exFAT (הפירמוט מוחק את כל מה שעליו), או להמיר אותו ל־NTFS בלי למחוק דבר, בפקודה convert " + root + " /fs:ntfs " +
               "בשורת פקודה של מנהל. כרטיס ב־NTFS לא נקרא בטלפון, ולכן לכרטיס שמשמש גם את פרדס עדיף exFAT.";
    }

    // The largest file of the library in `dir` (seforim.db, about 8 GB).
    public static long Largest(string dir)
    {
        long max = 0;
        foreach (var name in Names)
        {
            var p = Path.Combine(dir, name);
            try
            {
                if (File.Exists(p)) max = Math.Max(max, new FileInfo(p).Length);
                else if (Directory.Exists(p))
                    foreach (var f in new DirectoryInfo(p).EnumerateFiles("*", SearchOption.AllDirectories)) max = Math.Max(max, f.Length);
            }
            catch { }
        }
        return max;
    }

    public static string DriveLabel(string dir)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(dir));
            var d = new DriveInfo(root);
            if (!d.IsReady) return root.TrimEnd('\\');
            var kind = d.DriveType == DriveType.Removable ? "כונן נשלף" : d.DriveType == DriveType.Network ? "כונן רשת" : "כונן";
            var label = string.IsNullOrEmpty(d.VolumeLabel) ? "" : " (" + d.VolumeLabel + ")";
            // The RLM keeps what follows the drive letter on its right-to-left side
            return kind + " " + root.TrimEnd('\\') + label + "\u200F";
        }
        catch { return ""; }
    }

    // Where the library goes when `chosen` is picked: Zayit's own folder as it is, anywhere else a folder of its own,
    // since Zayit's library update deletes every *.db file next to the library it replaces.
    public static string Destination(string chosen)
    {
        if (IsDefault(chosen)) return DefaultDir;
        if (string.Equals(Path.GetFileName(chosen.TrimEnd('\\')), FolderName, StringComparison.OrdinalIgnoreCase)) return chosen;
        return Path.Combine(chosen, FolderName);
    }

    public static bool ZayitRunning()
    {
        if (Environment.GetEnvironmentVariable("ZAYITMOVE_PREFS_KEY") != null) return false;
        try
        {
            foreach (var p in Process.GetProcessesByName("zayit")) { p.Dispose(); return true; }
        }
        catch { }
        return false;
    }

    // What Zayit will open after it is pointed at `dir`: `pointed`, unless the environment variable wins.
    public static string Opens(string dir, string pointed)
    {
        if (EnvOverride == null) return pointed;
        return "הספרייה מוכנה ב־" + Fmt.ShortPath(dir, 60) + ", אבל זית עדיין פותחת את הספרייה שנקבעה ב־SEFORIMAPP_DATABASE_PATH, עד שמבטלים את ההפניה למעלה.";
    }

    // Zayit keeps, in pending-db-cleanup.txt in its own folder, the paths its library update could not delete, and
    // deletes them at its next start (PendingDbCleanup, desktop 0.6.6). A library put in their place must leave that
    // list, or Zayit deletes it.
    public static void DropPendingCleanup(string dir)
    {
        try
        {
            var marker = Path.Combine(DefaultDir, "pending-db-cleanup.txt");
            if (!File.Exists(marker)) return;
            var root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
            var keep = File.ReadAllLines(marker).Where(l =>
            {
                var t = l.Trim();
                if (t.Length == 0) return false;
                try
                {
                    var p = Path.GetFullPath(t).TrimEnd(Path.DirectorySeparatorChar);
                    return !p.Equals(root, StringComparison.OrdinalIgnoreCase) && !p.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                }
                catch { return true; }
            }).ToList();
            if (keep.Count == 0) File.Delete(marker);
            else File.WriteAllText(marker, string.Join("\n", keep));
        }
        catch { }
    }

    // Bytes in a folder and all under it; 0 when it is not there.
    public static long TreeSize(string dir)
    {
        try { return Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0; }
        catch { return 0; }
    }

    // Points Zayit at the library in `dir` ("" for its own folder, as Zayit itself writes it).
    public static void PointZayitAt(string dir)
    {
        Prefs.Set("database_path", IsDefault(dir) ? "" : Path.Combine(Path.GetFullPath(dir), "seforim.db"));
    }

    public static void CheckClosed()
    {
        if (ZayitRunning()) throw new Problem("זית פתוחה. יש לסגור אותה ולנסות שוב.");
    }

    // Copies (keep = true) or moves the library from `from` to `to`, checks the copy, points Zayit at `to`, and when
    // moving deletes what stays at `from`. Until Zayit is pointed at the copy nothing has changed for it: a failure
    // or a stop before that removes the partial copy and leaves the library where it was.
    public static void Move(string from, string to, bool keep, Action<long, long> progress, Func<bool> cancelled, Action<string> log)
    {
        from = Path.GetFullPath(from);
        to = Path.GetFullPath(to);
        if (!Exists(from)) throw new Problem("אין ספרייה בתיקייה " + from);
        if (Same(from, to)) throw new Problem("הספרייה כבר נמצאת שם.");
        if (to.StartsWith(from.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new Problem("אי אפשר להעביר את הספרייה לתוך התיקייה שלה.");
        if (Exists(to)) throw new Problem("כבר יש ספרייה ב־" + to + ".");
        // Library files that are no library (a split database, what a failed update left) are never copied over: a
        // failed copy cleans up the names it writes
        if (IsSplit(to)) throw new Problem("ב־" + to + " יש ספרייה מפוצלת לחלקים, שזית למחשב לא יכולה לפתוח. אפשר להתקין שם ספרייה שלמה במקומה, בכרטיס \"התקנת הספרייה\".");
        var there = Names.Where(n => File.Exists(Path.Combine(to, n)) || Directory.Exists(Path.Combine(to, n))).ToList();
        if (there.Count > 0) throw new Problem("כבר יש ב־" + to + " קבצים של ספרייה (" + string.Join(", ", there) + "). אפשר להתקין שם ספרייה במקומם, בכרטיס \"התקנת הספרייה\", או למחוק אותם ולנסות שוב.");
        CheckClosed();
        var wal = new FileInfo(Path.Combine(from, "seforim.db-wal"));
        if (wal.Exists && wal.Length > 0) throw new Problem("זית לא נסגרה כראוי (seforim.db-wal אינו ריק). יש לפתוח ולסגור אותה, ולנסות שוב.");

        long total = Size(from);
        bool sameDrive = string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase);
        if (!sameDrive && IsFat(to) && Largest(from) > Installer.Fat32Max) throw new Problem(FatText(to));
        if (keep || !sameDrive)
        {
            long free = Free(to);
            if (free < 0) throw new Problem("הכונן " + Path.GetPathRoot(to) + " לא זמין.");
            if (free < total + Margin) throw new Problem("אין מספיק מקום ב־" + Path.GetPathRoot(to) + ": צריך " + Fmt.Size(total + Margin) + ", פנויים " + Fmt.Size(free) + ".");
        }

        bool created = !Directory.Exists(to);
        Directory.CreateDirectory(to);
        if (!keep && sameDrive)
        {
            // Same drive: the files are renamed into place, nothing is copied
            Rename(from, to, log);
            progress(total, total);
        }
        else
        {
            try { Copy(from, to, total, progress, cancelled); }
            catch
            {
                foreach (var n in Names) Remove(Path.Combine(to, n));
                if (created) TryRemoveEmpty(to);
                throw;
            }
            long copied = Size(to);
            if (copied != total)
            {
                foreach (var n in Names) Remove(Path.Combine(to, n));
                if (created) TryRemoveEmpty(to);
                throw new Problem("גודל העותק (" + Fmt.Size(copied) + ") שונה מהמקור (" + Fmt.Size(total) + "). הספרייה נשארה במקומה.");
            }
            log("הועתקו " + Fmt.Size(total) + " ונבדקו.");
        }

        DropPendingCleanup(to);
        PointZayitAt(to);
        log(Opens(to, "זית תפתח מעכשיו את הספרייה מהמקום החדש."));
        History.Add(keep ? "copy" : "move", from, to, total);

        if (!keep && !(sameDrive))
        {
            var failed = new List<string>();
            foreach (var n in Names.Concat(Leftovers))
                if (!Remove(Path.Combine(from, n))) failed.Add(n);
            CleanFolder(from);
            if (failed.Count == 0) log("העותק הישן נמחק.");
            else log("לא נמחקו מהמקום הישן: " + string.Join(", ", failed) + " (אפשר למחוק ידנית).");
        }
        else if (!keep)
        {
            foreach (var n in Leftovers) Remove(Path.Combine(from, n));
            CleanFolder(from);
        }
        else log("עותק נשאר במקום הקודם: אפשר לחזור אליו מיד.");
    }

    // Points Zayit at a copy that is already there (nothing is copied).
    public static void SwitchTo(string dir, Action<string> log)
    {
        if (!Exists(dir)) throw new Problem("אין ספרייה ב־" + dir + ".");
        CheckClosed();
        var before = CurrentDir;
        DropPendingCleanup(dir);
        PointZayitAt(dir);
        History.Add("switch", before, dir, Size(dir));
        log(Opens(dir, "זית תפתח מעכשיו את הספרייה מ־" + Fmt.ShortPath(dir, 60)));
    }

    // Zayit back to its own folder, whatever is there (with no library, Zayit offers to download one).
    public static void Reset(Action<string> log)
    {
        CheckClosed();
        var before = CurrentDir;
        Prefs.Set("database_path", "");
        History.Add("reset", before, DefaultDir, 0);
        log(Opens(DefaultDir, "זית תחפש מעכשיו את הספרייה בתיקייה הרגילה שלה."));
    }

    // Deletes a copy Zayit does not use.
    public static void DeleteCopy(string dir, Action<string> log)
    {
        if (Same(dir, CurrentDir)) throw new Problem("זו הספרייה שזית משתמשת בה עכשיו.");
        var failed = new List<string>();
        foreach (var n in Names.Concat(Leftovers))
            if (!Remove(Path.Combine(dir, n))) failed.Add(n);
        CleanFolder(dir);
        if (failed.Count > 0) throw new Problem("לא נמחקו: " + string.Join(", ", failed));
        log("העותק נמחק.");
    }

    static void Copy(string from, string to, long total, Action<long, long> progress, Func<bool> cancelled)
    {
        var buffer = new byte[4 << 20];
        long done = 0;
        foreach (var name in Names)
        {
            var src = Path.Combine(from, name);
            IEnumerable<string> files;
            if (File.Exists(src)) files = new[] { src };
            else if (Directory.Exists(src)) files = Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories).ToList();
            else continue;
            foreach (var file in files)
            {
                var target = Path.Combine(to, file.Substring(from.TrimEnd('\\').Length + 1));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    int n;
                    while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (cancelled()) throw new Stopped();
                        output.Write(buffer, 0, n);
                        done += n;
                        progress(done, total);
                    }
                    output.Flush(true);
                }
                if (new FileInfo(target).Length != new FileInfo(file).Length)
                    throw new Problem("ההעתקה של " + Path.GetFileName(file) + " לא הושלמה.");
            }
        }
    }

    // Moves each part into `to` on the same drive; if one fails, the ones already moved go back.
    static void Rename(string from, string to, Action<string> log)
    {
        var moved = new List<string>();
        try
        {
            foreach (var n in Names)
            {
                var a = Path.Combine(from, n);
                var b = Path.Combine(to, n);
                if (File.Exists(a)) File.Move(a, b);
                else if (Directory.Exists(a)) Directory.Move(a, b);
                else continue;
                moved.Add(n);
            }
        }
        catch (Exception e)
        {
            foreach (var n in moved)
            {
                try
                {
                    var a = Path.Combine(from, n);
                    var b = Path.Combine(to, n);
                    if (File.Exists(b)) File.Move(b, a); else Directory.Move(b, a);
                }
                catch { }
            }
            TryRemoveEmpty(to);
            throw new Problem("ההעברה נכשלה (" + e.Message + "). הספרייה נשארה במקומה.");
        }
        log("הספרייה הועברה באותו כונן, בלי העתקה.");
    }

    public static bool Remove(string p)
    {
        try
        {
            if (File.Exists(p)) File.Delete(p);
            else if (Directory.Exists(p)) Directory.Delete(p, true);
            return true;
        }
        catch { return false; }
    }

    // A "Zayit library" folder this tool made goes too once it is empty; other folders are left alone.
    static void CleanFolder(string dir)
    {
        if (string.Equals(Path.GetFileName(dir.TrimEnd('\\')), FolderName, StringComparison.OrdinalIgnoreCase)) TryRemoveEmpty(dir);
    }

    public static void TryRemoveEmpty(string dir)
    {
        try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
    }
}

// What the tool did, newest last: the places a library was moved from and to, so it can go back.
static class History
{
    public class Entry
    {
        public string Kind, From, To;
        public DateTime When;
        public long Bytes;
    }

    static string FilePath
    {
        get
        {
            var t = Environment.GetEnvironmentVariable("ZAYITMOVE_HISTORY");
            if (!string.IsNullOrEmpty(t)) return t;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Zayit Move", "history.tsv");
        }
    }

    public static void Add(string kind, string from, string to, long bytes)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.AppendAllText(FilePath, string.Join("\t", kind, DateTime.Now.ToString("yyyy-MM-dd HH:mm"), from, to, bytes.ToString()) + "\r\n", Encoding.UTF8);
        }
        catch { }
    }

    public static List<Entry> All()
    {
        var list = new List<Entry>();
        try
        {
            if (!File.Exists(FilePath)) return list;
            foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
            {
                var f = line.Split('\t');
                if (f.Length < 5) continue;
                DateTime when;
                DateTime.TryParse(f[1], out when);
                long bytes;
                long.TryParse(f[4], out bytes);
                list.Add(new Entry { Kind = f[0], When = when, From = f[2], To = f[3], Bytes = bytes });
            }
        }
        catch { }
        return list;
    }

    // The last move or copy (not a switch between copies): where "back" is.
    public static Entry LastMove()
    {
        return All().LastOrDefault(e => e.Kind == "move" || e.Kind == "copy");
    }

    // Every place a library was, other than `current`, that still holds one: copies Zayit can switch to.
    public static List<string> Copies(string current)
    {
        var seen = new List<string>();
        foreach (var e in All().AsEnumerable().Reverse())
            foreach (var d in new[] { e.To, e.From })
                if (!string.IsNullOrEmpty(d) && !Library.Same(d, current) && !seen.Any(s => Library.Same(s, d)) && Library.Exists(d))
                    seen.Add(d);
        if (!Library.Same(Library.DefaultDir, current) && !seen.Any(s => Library.Same(s, Library.DefaultDir)) && Library.Exists(Library.DefaultDir))
            seen.Add(Library.DefaultDir);
        return seen;
    }
}

class Problem : Exception { public Problem(string m) : base(m) { } }

class Stopped : Exception
{
    public Stopped() : base("ההעתקה נעצרה. הספרייה נשארה במקומה.") { }
    public Stopped(string m) : base(m) { }
}

static class Fmt
{
    // A long path made to fit: its drive, then as many of its last folders as fit ("C:\…\seforimapp\databases").
    public static string ShortPath(string p, int max)
    {
        if (p == null || p.Length <= max) return p;
        var root = Path.GetPathRoot(p) ?? "";
        var parts = p.Substring(root.Length).TrimEnd('\\').Split('\\');
        var tail = parts[parts.Length - 1];
        for (int i = parts.Length - 2; i >= 0; i--)
        {
            var next = parts[i] + "\\" + tail;
            if (root.Length + 2 + next.Length > max) break;
            tail = next;
        }
        return root + "…\\" + tail;
    }

    // The LRM keeps "12.5 MB" in order inside Hebrew text.
    public static string Size(long bytes)
    {
        if (bytes >= 1L << 30) return "‎" + (bytes / (double)(1L << 30)).ToString("0.0") + " GB";
        if (bytes >= 1L << 20) return "‎" + (bytes / (double)(1L << 20)).ToString("0.0") + " MB";
        return "‎" + Math.Max(0, bytes / 1024) + " KB";
    }
}
