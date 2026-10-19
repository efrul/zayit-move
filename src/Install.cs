using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

// Installing the library straight into a folder of one's choice (a memory card too), from SeforimLibrary's release as
// Zayit installs it: seforim_bundle.tar.zst cut in parts (.part01, .part02...), listed with their size and sha256 in
// seforim_bundle.tar.zst.manifest.json. The parts are downloaded or picked on disk, and read one after another, never
// joined or kept: each is checked as it comes, decompressed and unpacked into a folder next to the destination. Only
// when everything checked out does the new library take the place of what is there, and Zayit is pointed at it.
// Zayit's onboarding skips its download, and its check for 15 GB free on C, when a library is already where its
// setting points (LicenceScreen, desktop 0.6.6), so this works before Zayit's first run too.
static class Installer
{
    // FAT32 takes no file of 4 GiB or more, and seforim.db alone is about 8 GB.
    public const long Fat32Max = (4L << 30) - 1;
    // The release of October 2026: 5.54 GB of parts, 13.06 GB unpacked. Used to estimate before the manifest is read.
    public const long TypicalBundle = 5544091138;

    public static long Unpacked(long bundleSize) { return bundleSize / 5 * 12; }

    // The stage shown while the new library takes the old one's place: a few renames, past stopping.
    public const string Committing = "מכניס את הספרייה למקומה…";

    const string StoppedText = "ההתקנה נעצרה, ומה שכבר נכתב נמחק. שום דבר אחר לא השתנה.";

    // The folders next to `to` that an install uses: the new library while it is unpacked, the old one while it is
    // replaced.
    public static string StagingOf(string to) { return to.TrimEnd(Path.DirectorySeparatorChar) + ".installing"; }

    // A new folder each time, so that nothing already there (an old library a failed undo left, a folder of the
    // user's) is ever deleted with it.
    static string NewOldOf(string to)
    {
        var at = to.TrimEnd(Path.DirectorySeparatorChar) + ".old-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var p = at;
        for (int i = 2; Directory.Exists(p) || File.Exists(p); i++) p = at + "-" + i;
        return p;
    }

    // Written into staging once what is there is unpacked and checked: which bundle it is. A swap that failed keeps
    // staging, and the next install of the same bundle goes on from it instead of unpacking it again.
    const string CheckedName = ".zayitmove-checked";

    static string Identity(Bundle b)
    {
        return string.Join("\n", b.Parts.Select(p => p.Name + " " + p.Size + " " + (p.Sha256 ?? "")));
    }

    // Installs `bundle` (null: the latest release, downloaded) into `to`, replacing a library already there only once
    // the new one is complete and checked, and points Zayit at it.
    public static void Install(Bundle bundle, string to, Action<long, long> progress, Func<bool> cancelled, Action<string> log, Action<string> stage)
    {
        to = Path.GetFullPath(to);
        if (Library.IsFat(to)) throw new Problem(Library.FatText(to));
        string before = Library.CurrentDir;
        string staging = StagingOf(to), stamp = Path.Combine(staging, CheckedName);
        // Windows takes paths of up to 260 characters here (248 for a folder); the bundle's longest name is 54
        if (staging.Length + 64 >= 248) throw new Problem("הנתיב " + Fmt.ShortPath(to, 60) + " ארוך מדי לקובצי הספרייה. כדאי לבחור תיקייה קרובה יותר לשורש הכונן.");
        bool done = false, keep = false;
        try
        {
            if (bundle == null)
            {
                stage("מחפש את המהדורה האחרונה של הספרייה…");
                bundle = Bundle.Latest(cancelled);
            }
            if (cancelled()) throw new Stopped();
            string identity = Identity(bundle);
            if (File.Exists(stamp) && File.ReadAllText(stamp) == identity)
            {
                // The same bundle, unpacked and checked by an install whose swap failed
                stage("ממשיך מההתקנה הקודמת, שהספרייה שלה כבר נפרסה ונבדקה…");
                progress(bundle.Size, bundle.Size);
                Check(staging);
            }
            else
            {
                // What a stop or a crash left goes before the room is measured
                Library.Remove(staging);
                long need = Unpacked(bundle.Size) + Library.Margin;
                long free = Library.Free(to);
                if (free < 0) throw new Problem("הכונן " + Path.GetPathRoot(to) + " לא זמין.");
                if (free < need) throw new Problem("אין מספיק מקום ב־" + Path.GetPathRoot(to) + ": צריך כ־" + Fmt.Size(need) + ", פנויים " + Fmt.Size(free) + ".");
                Directory.CreateDirectory(staging);
                Unpack(bundle, staging, progress, cancelled, stage);
                stage("בודק את הספרייה…");
                Check(staging);
                File.WriteAllText(stamp, identity);
            }
            for (;;)
            {
                if (cancelled()) throw new Stopped();
                if (!Library.ZayitRunning()) break;
                stage("זית פתוחה. כדי לסיים צריך לסגור אותה, וההתקנה תמשיך מעצמה.");
                Thread.Sleep(500);
            }
            stage(Committing);
            try { Swap(staging, to, NewOldOf(to), log); }
            catch (Kept) { keep = true; throw; }
            done = true;
        }
        catch (Stopped) { throw new Stopped(StoppedText); }
        catch (Problem) { throw; }
        catch (InvalidDataException e) { throw new Problem("קובצי הספרייה פגומים (" + e.Message + "). שום דבר לא השתנה."); }
        catch (EndOfStreamException) { throw new Problem("קובצי הספרייה נגמרים לפני הסוף: אולי אחד החלקים לא ירד עד הסוף. שום דבר לא השתנה."); }
        catch (PathTooLongException) { throw new Problem("הנתיב " + Fmt.ShortPath(to, 60) + " ארוך מדי לקובצי הספרייה. כדאי לבחור תיקייה קרובה יותר לשורש הכונן."); }
        catch (NotSupportedException e) { throw new Problem("הכלי לא יודע לפרוס את המהדורה הזו של הספרייה (" + e.Message + "). שום דבר לא השתנה."); }
        catch (OutOfMemoryException) { throw new Problem("אין מספיק זיכרון לפריסת הספרייה (צריך כ־200 MB פנויים). כדאי לסגור תוכנות אחרות ולנסות שוב."); }
        catch (WebException e) { throw new Problem("אין חיבור ל־GitHub (" + e.Message + "). כדאי לבדוק את החיבור לאינטרנט ולנסות שוב."); }
        catch (UnauthorizedAccessException) { throw new Problem("אין הרשאה לכתוב ב־" + Fmt.ShortPath(to, 60) + ". אולי הכונן מוגן מכתיבה."); }
        catch (Exception e)
        {
            if (e is IOException && (e.HResult == unchecked((int)0x80070070) || e.HResult == unchecked((int)0x80070027)))
                throw new Problem("נגמר המקום ב־" + Path.GetPathRoot(to) + " באמצע ההתקנה. שום דבר לא השתנה.");
            throw new Problem("ההתקנה נכשלה: " + e.Message.TrimEnd('.') + ". שום דבר לא השתנה.");
        }
        finally
        {
            if (!done && !keep) Library.Remove(staging);
        }

        Library.DropPendingCleanup(to);
        Library.PointZayitAt(to);
        long size = Library.Size(to);
        History.Add("install", before, to, size);
        var v = Library.Version(to);
        log("הספרייה הותקנה ונבדקה: " + Fmt.Size(size) + (v != null ? ", מהדורה " + v : "") + ".");
        log(Library.Opens(to, "זית תפתח מעכשיו את הספרייה מ־" + Fmt.ShortPath(to, 60)));
        if (Library.EnvOverride == null && Prefs.Get("onboarding_finished", "") != "true")
            log("בפתיחה הראשונה זית תמצא את הספרייה ותדלג על ההורדה שלה ועל בדיקת המקום בכונן C.");
        if (!Library.Same(before, to) && Library.Exists(before))
            log("הספרייה הקודמת נשארה ב־" + Fmt.ShortPath(before, 60) + ". אפשר למחוק אותה למטה, בשחזור.");
    }

    // parts -> sha256 check -> zstd -> tar -> files in `dir`, the writing on a thread of its own
    static void Unpack(Bundle bundle, string dir, Action<long, long> progress, Func<bool> cancelled, Action<string> stage)
    {
        stage(bundle.Online ? "מוריד, בודק ופורס את הספרייה…" : "קורא, בודק ופורס את הספרייה…");
        long total = bundle.Size;
        progress(0, total);
        using (var parts = new PartsStream(bundle, cancelled))
        using (var z = new ZstdDecodeStream(parts))
        using (var writer = new Writer())
        {
            var tar = new TarReader(z);
            TarEntry e;
            while ((e = tar.Next()) != null)
            {
                if (e.Type != '0' && e.Type != '5') continue;
                var rel = TarReader.SafeRelativePath(e.Name);
                if (rel == null) throw new Problem("בקובצי הספרייה יש שם קובץ שאי אפשר ליצור: " + e.Name);
                var path = Path.Combine(dir, rel);
                if (e.Type == '5') { Directory.CreateDirectory(path); continue; }
                long free = Library.Free(dir);
                if (free >= 0 && free < e.Size + Library.Margin)
                    throw new Problem("אין מספיק מקום ב־" + Path.GetPathRoot(dir) + " ל־" + rel + ": צריך " + Fmt.Size(e.Size + Library.Margin) + ", פנויים " + Fmt.Size(free) + ".");
                writer.Begin(path);
                for (;;)
                {
                    var buf = writer.Buffer();
                    int n = 0, r;
                    while (n < buf.Length && (r = tar.Read(buf, n, buf.Length - n)) > 0) n += r;
                    if (n == 0) { writer.Give(buf); break; }
                    writer.Write(buf, n);
                    progress(parts.Done, total);
                }
                writer.End();
            }
            // The rest of the stream: the end of the tar, the zstd checksum, and the last part's sha256
            var rest = new byte[1 << 16];
            while (z.Read(rest, 0, rest.Length) > 0) { }
            if (parts.Done != total) throw new Problem("קובצי הספרייה נקראו רק בחלקם. שום דבר לא השתנה.");
            writer.Finish();
            progress(total, total);
        }
    }

    // What Zayit needs to take the library without its own download: the database, whole, and release_info.txt as
    // the 14 digits it compares with its minimum (anything else and Zayit sends the library to its update, which
    // deletes it).
    static void Check(string dir)
    {
        foreach (var n in new[] { "seforim.db", "release_info.txt" })
            if (!File.Exists(Path.Combine(dir, n))) throw new Problem("בקובצי הספרייה חסר " + n + ". שום דבר לא השתנה.");
        if (!Directory.Exists(Path.Combine(dir, "seforim.db.lucene"))) throw new Problem("בקובצי הספרייה חסר האינדקס seforim.db.lucene. שום דבר לא השתנה.");
        var release = File.ReadAllText(Path.Combine(dir, "release_info.txt")).Trim();
        if (!Regex.IsMatch(release, @"^\d{14}$")) throw new Problem("המהדורה שבקובצי הספרייה (release_info.txt) אינה בצורה שזית מקבלת: " + release + ". שום דבר לא השתנה.");
        if (!Library.Whole(Path.Combine(dir, "seforim.db"))) throw new Problem("מסד הנתונים seforim.db שבקובצי הספרייה אינו שלם. שום דבר לא השתנה.");
    }

    // Puts what is in `staging` in place of the library at `to`. The old library is moved aside into `old` first, its
    // database first of all, and the new database comes in last, so the folder never looks like a library while it is
    // half the one and half the other. Staging and `to` are side by side, so these are renames; if one fails, those
    // done are undone and the old library stays.
    static void Swap(string staging, string to, string old, Action<string> log)
    {
        Directory.CreateDirectory(to);
        Directory.CreateDirectory(old);
        var incoming = Directory.GetFileSystemEntries(staging).Select(Path.GetFileName).Where(n => n != CheckedName).ToList();
        var aside = new List<string> { "seforim.db" };
        aside.AddRange(Library.Chunks(to).Select(Path.GetFileName));
        aside.AddRange(Library.Leftovers);
        aside.AddRange(Library.Names);
        aside.AddRange(incoming);
        var moves = new List<KeyValuePair<string, string>>();
        try
        {
            foreach (var n in aside.Distinct(StringComparer.OrdinalIgnoreCase))
                Rename(Path.Combine(to, n), Path.Combine(old, n), moves);
            foreach (var n in incoming.Where(n => !n.Equals("seforim.db", StringComparison.OrdinalIgnoreCase)))
                Rename(Path.Combine(staging, n), Path.Combine(to, n), moves);
            Rename(Path.Combine(staging, "seforim.db"), Path.Combine(to, "seforim.db"), moves);
        }
        catch (Exception e)
        {
            var oldStuck = new List<string>();
            var newStuck = new List<string>();
            for (int i = moves.Count - 1; i >= 0; i--)
            {
                try { Move(moves[i].Value, moves[i].Key); }
                catch
                {
                    if (moves[i].Value.StartsWith(old, StringComparison.OrdinalIgnoreCase)) oldStuck.Add(Path.GetFileName(moves[i].Key));
                    else newStuck.Add(Path.GetFileName(moves[i].Value));
                }
            }
            Library.TryRemoveEmpty(old);
            var why = "הכנסת הספרייה למקומה נכשלה (" + e.Message.TrimEnd('.') + ")";
            if (oldStuck.Count == 0 && newStuck.Count == 0)
                throw new Kept(why + ". הספרייה שהייתה שם נשארה כמו שהייתה, והחדשה, שכבר נבדקה, נשמרה ליד: ההתקנה הבאה תמשיך ממנה.");
            File.Delete(Path.Combine(staging, CheckedName));
            var msg = why + ", וגם ההחזרה לא הושלמה.";
            if (oldStuck.Count > 0) msg += " מהספרייה הקודמת נשארו בתיקייה " + old + ": " + string.Join(", ", oldStuck) + ". אפשר להחזיר אותם ידנית ל־" + to + ".";
            if (newStuck.Count > 0) msg += " מהספרייה החדשה נשארו ב־" + to + ": " + string.Join(", ", newStuck) + ".";
            throw new Problem(msg);
        }
        if (!Library.Remove(old)) log("מה שנשאר מהספרייה הקודמת, בתיקייה " + Fmt.ShortPath(old, 60) + ", לא נמחק כולו. אפשר למחוק אותו ידנית.");
        try { File.Delete(Path.Combine(staging, CheckedName)); } catch { }
        Library.TryRemoveEmpty(staging);
    }

    // A swap that failed and was undone in full: staging holds the checked library and is kept.
    sealed class Kept : Problem { public Kept(string m) : base(m) { } }

    static void Rename(string from, string to, List<KeyValuePair<string, string>> moves)
    {
        if (!File.Exists(from) && !Directory.Exists(from)) return;
        Move(from, to);
        moves.Add(new KeyValuePair<string, string>(from, to));
    }

    // A rename, tried again for 10 seconds: a file just closed may still be held for a while by an indexer or an
    // antivirus.
    static void Move(string from, string to)
    {
        for (int i = 0; ; i++)
        {
            try
            {
                if (Directory.Exists(from)) Directory.Move(from, to); else File.Move(from, to);
                return;
            }
            catch (IOException) { if (i >= 20) throw; }
            catch (UnauthorizedAccessException) { if (i >= 20) throw; }
            Thread.Sleep(500);
        }
    }

    // Writes the files on a thread of its own: Windows writes to removable drives without a cache, so the card's
    // writing overlaps the download and the decompression instead of following them.
    sealed class Writer : IDisposable
    {
        sealed class Job { public string Path; public byte[] Data; public int Count; }

        readonly BlockingCollection<Job> jobs = new BlockingCollection<Job>(16);
        readonly BlockingCollection<byte[]> free = new BlockingCollection<byte[]>();
        readonly Thread thread;
        volatile Exception error;
        bool finished;

        public Writer()
        {
            for (int i = 0; i < 18; i++) free.Add(new byte[1 << 20]);
            thread = new Thread(Run) { IsBackground = true, Name = "ZayitMove writer" };
            thread.Start();
        }

        public void Begin(string path) { Put(new Job { Path = path }); }
        public void Write(byte[] data, int count) { Put(new Job { Data = data, Count = count }); }
        public void End() { Put(new Job()); }
        public void Give(byte[] data) { free.Add(data); }

        public byte[] Buffer()
        {
            byte[] b;
            while (!free.TryTake(out b, 200)) Fail();
            return b;
        }

        public void Finish()
        {
            finished = true;
            jobs.CompleteAdding();
            thread.Join();
            Fail();
        }

        public void Dispose()
        {
            if (!finished)
            {
                finished = true;
                jobs.CompleteAdding();
                thread.Join();
            }
        }

        void Put(Job j)
        {
            while (!jobs.TryAdd(j, 200)) Fail();
            Fail();
        }

        void Fail()
        {
            var e = error;
            if (e != null) ExceptionDispatchInfo.Capture(e).Throw();
        }

        void Run()
        {
            FileStream f = null;
            foreach (var j in jobs.GetConsumingEnumerable())
            {
                // After a failure the rest only gives the buffers back
                try
                {
                    if (error == null)
                    {
                        if (j.Path != null)
                        {
                            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(j.Path));
                            f = new FileStream(j.Path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
                        }
                        else if (j.Data != null) f.Write(j.Data, 0, j.Count);
                        else { f.Flush(true); f.Dispose(); f = null; }
                    }
                }
                catch (Exception e)
                {
                    error = e;
                    if (f != null) { try { f.Dispose(); } catch { } f = null; }
                }
                if (j.Data != null) free.Add(j.Data);
            }
            if (f != null) { try { f.Dispose(); } catch { } }
        }
    }
}

// The library as SeforimLibrary releases it: its parts in order, each with its size and (from the manifest) sha256.
sealed class Bundle
{
    public sealed class Part
    {
        public string Name, File, Url, Sha256;   // File for a part on disk, Url for one to download
        public long Size;
    }

    public const string Name = "seforim_bundle.tar.zst";
    const string ManifestSuffix = ".manifest.json";
    static readonly Regex PartName = new Regex(@"^(.+\.tar\.zst)\.part(\d+)$", RegexOptions.IgnoreCase);

    public readonly List<Part> Parts = new List<Part>();
    public string Release;   // the release's tag, for a download

    public long Size { get { return Parts.Sum(p => p.Size); } }
    public bool Online { get { return Parts.Count > 0 && Parts[0].Url != null; } }
    public bool Hashed { get { return Parts.All(p => p.Sha256 != null); } }
    public string Folder { get { return Parts.Count > 0 && Parts[0].File != null ? Path.GetDirectoryName(Parts[0].File) : null; } }

    // Where Zayit downloads its library from; ZAYITMOVE_RELEASES replaces it in the tests.
    static string Releases
    {
        get
        {
            var t = Environment.GetEnvironmentVariable("ZAYITMOVE_RELEASES");
            if (!string.IsNullOrEmpty(t) && Environment.GetEnvironmentVariable("ZAYITMOVE_PREFS_KEY") != null) return t.TrimEnd('/');
            return "https://github.com/kdroidFilter/SeforimLibrary/releases";
        }
    }

    // The latest release: its tag from where GitHub sends .../releases/latest/download/<manifest>, then that release's
    // manifest, so every part comes from the same release even if a new one comes out meanwhile.
    public static Bundle Latest(Func<bool> cancelled)
    {
        using (var watch = new RequestWatch(cancelled))
        {
            var at = Fetch(() => Http.Redirect(new Uri(Releases + "/latest/download/" + Name + ManifestSuffix), watch.Set), cancelled);
            var m = Regex.Match(at == null ? "" : at.AbsolutePath, "/releases/download/([^/]+)/");
            if (!m.Success) throw new Problem("לא נמצאה מהדורה של הספרייה להורדה. אפשר לנסות שוב מאוחר יותר.");
            var tag = Uri.UnescapeDataString(m.Groups[1].Value);
            var base_ = Releases + "/download/" + Uri.EscapeDataString(tag) + "/";
            var b = FromManifest(Fetch(() => Http.Text(new Uri(base_ + Name + ManifestSuffix), watch.Set), cancelled), null, base_);
            b.Release = tag;
            return b;
        }
    }

    // A small request, tried three times (2 and 4 seconds apart) when GitHub or the network fails for a moment.
    static T Fetch<T>(Func<T> get, Func<bool> cancelled)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { return get(); }
            catch (Exception e)
            {
                if (cancelled()) throw new Stopped();
                var http = e as HttpError;
                bool passing = http != null ? http.Code >= 500 || http.Code == 408 || http.Code == 429 : e is IOException || e is WebException;
                if (!passing || attempt == 3)
                {
                    if (http != null && http.Code == 404) throw new Problem("לא נמצאה מהדורה של הספרייה להורדה. אפשר לנסות שוב מאוחר יותר.");
                    if (http != null) throw new Problem("GitHub השיב " + http.Code + ". אפשר לנסות שוב מאוחר יותר.");
                    throw;
                }
            }
            for (int t = 0; t < 2000 * attempt; t += 250)
            {
                if (cancelled()) throw new Stopped();
                Thread.Sleep(250);
            }
        }
    }

    // Parts on disk, from the files picked: any part (the others are found next to it), all of them, or the manifest.
    // With the manifest next to them, every part must be there with its size, and is checked against its sha256 while
    // it is read; without it, as Zayit does, the parts must run from 01 with no gap, all of one size but the last.
    public static Bundle FromFiles(IList<string> picked)
    {
        string dir = null, name = null;
        foreach (var f in picked)
        {
            var n = Path.GetFileName(f);
            var m = PartName.Match(n);
            if (m.Success) name = m.Groups[1].Value;
            else if (n.EndsWith(".tar.zst" + ManifestSuffix, StringComparison.OrdinalIgnoreCase)) name = n.Substring(0, n.Length - ManifestSuffix.Length);
            else if (n.EndsWith(".tar.zst", StringComparison.OrdinalIgnoreCase)) name = n;
            else continue;
            dir = Path.GetDirectoryName(Path.GetFullPath(f));
            break;
        }
        if (dir == null) throw new Problem("אלה לא קובצי הספרייה. צריך לבחור את הקבצים " + Name + ".part01, part02 וכן הלאה.");

        var manifest = Path.Combine(dir, name + ManifestSuffix);
        if (File.Exists(manifest))
        {
            var b = FromManifest(File.ReadAllText(manifest, Encoding.UTF8), dir, null);
            foreach (var p in b.Parts)
            {
                if (!File.Exists(p.File)) throw new Problem("חסר החלק " + p.Name + " בתיקייה " + dir + ".");
                long len = new FileInfo(p.File).Length;
                if (len != p.Size) throw new Problem("החלק " + p.Name + " אינו שלם: " + Fmt.Size(len) + " במקום " + Fmt.Size(p.Size) + ". צריך להוריד אותו מחדש.");
            }
            return b;
        }

        var bundle = new Bundle();
        var numbered = new SortedDictionary<int, string>();
        foreach (var f in Directory.GetFiles(dir))
        {
            var m = PartName.Match(Path.GetFileName(f));
            int k;
            if (m.Success && m.Groups[1].Value.Equals(name, StringComparison.OrdinalIgnoreCase) && int.TryParse(m.Groups[2].Value, out k)) numbered[k] = f;
        }
        if (numbered.Count == 0)
        {
            var whole = Path.Combine(dir, name);
            if (!File.Exists(whole)) throw new Problem("לא נמצאו קובצי הספרייה בתיקייה " + dir + ".");
            bundle.Parts.Add(new Part { Name = name, File = whole, Size = new FileInfo(whole).Length });
            return bundle;
        }
        int expect = 1;
        foreach (var kv in numbered)
        {
            if (kv.Key != expect) throw new Problem("חסר החלק " + name + ".part" + expect.ToString("00") + " בתיקייה " + dir + ".");
            bundle.Parts.Add(new Part { Name = Path.GetFileName(kv.Value), File = kv.Value, Size = new FileInfo(kv.Value).Length });
            expect++;
        }
        if (bundle.Parts.Count < 2) throw new Problem("נמצא רק חלק אחד של הספרייה. צריך את כל החלקים, part01, part02 וכן הלאה, באותה תיקייה.");
        long full = bundle.Parts[0].Size;
        for (int i = 1; i < bundle.Parts.Count - 1; i++)
            if (bundle.Parts[i].Size != full) throw new Problem("החלק " + bundle.Parts[i].Name + " אינו בגודל של החלקים האחרים: אולי הוא לא ירד עד הסוף.");
        if (bundle.Parts[bundle.Parts.Count - 1].Size >= full)
            throw new Problem("נראה שחסר החלק האחרון (" + name + ".part" + expect.ToString("00") + "), או שאחד החלקים לא ירד עד הסוף.");
        return bundle;
    }

    // The manifest (formatVersion 1): {"parts": [{"file", "size", "sha256"}...]} in the order they join.
    static Bundle FromManifest(string text, string dir, string baseUrl)
    {
        Dictionary<string, object> root = null;
        try { root = Json.Parse(text) as Dictionary<string, object>; } catch (FormatException) { }
        var parts = root == null ? null : Json.Get(root, "parts") as List<object>;
        if (root == null || Json.Long(root, "formatVersion") != 1 || parts == null || parts.Count == 0)
            throw new Problem("רשימת החלקים (" + Name + ManifestSuffix + ") אינה בצורה שהכלי מכיר.");
        var b = new Bundle();
        foreach (var o in parts)
        {
            var p = o as Dictionary<string, object>;
            var file = p == null ? null : Json.Get(p, "file") as string;
            var sha = p == null ? null : Json.Get(p, "sha256") as string;
            long size = p == null ? -1 : Json.Long(p, "size");
            if (file == null || (!PartName.IsMatch(file) && !file.EndsWith(".tar.zst", StringComparison.OrdinalIgnoreCase)) || file.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 ||
                size <= 0 || sha == null || !Regex.IsMatch(sha, "^[0-9a-fA-F]{64}$"))
                throw new Problem("רשימת החלקים (" + Name + ManifestSuffix + ") אינה תקינה.");
            b.Parts.Add(new Part
            {
                Name = file, Size = size, Sha256 = sha.ToLowerInvariant(),
                File = dir == null ? null : Path.Combine(dir, file),
                Url = baseUrl == null ? null : baseUrl + Uri.EscapeDataString(file)
            });
        }
        return b;
    }
}

// The parts read one after another as one stream, each checked at its end against its size and sha256. A part on the
// network is read straight from the response; when the connection breaks, it is opened again where it stopped.
sealed class PartsStream : Stream
{
    readonly Bundle bundle;
    readonly Func<bool> cancelled;
    readonly RequestWatch watch;
    int index = -1;
    long partPos, done;
    Stream cur;
    HttpWebResponse response;
    HashAlgorithm sha;
    int failures;

    public PartsStream(Bundle bundle, Func<bool> cancelled)
    {
        this.bundle = bundle;
        this.cancelled = cancelled;
        watch = new RequestWatch(cancelled);
    }

    // Bytes of all parts read so far.
    public long Done { get { return done; } }

    public override int Read(byte[] buffer, int offset, int count)
    {
        for (;;)
        {
            if (cancelled()) throw new Stopped();
            if (index >= bundle.Parts.Count) return 0;
            if (index < 0 || partPos == bundle.Parts[index].Size)
            {
                Next();
                continue;
            }
            var p = bundle.Parts[index];
            int n;
            try
            {
                if (cur == null) Connect();
                n = cur.Read(buffer, offset, (int)Math.Min(count, p.Size - partPos));
            }
            catch (Exception e)
            {
                if (cancelled()) throw new Stopped();
                if (p.Url == null || !(e is IOException || e is WebException)) throw;
                Retry(e);
                continue;
            }
            if (n <= 0)
            {
                if (p.Url == null) throw new Problem("החלק " + p.Name + " קצר ממה שהיה כשנבחר.");
                Retry(new IOException("the connection closed early"));
                continue;
            }
            if (sha != null) sha.TransformBlock(buffer, offset, n, null, 0);
            partPos += n;
            done += n;
            failures = 0;
            if (partPos == p.Size) Finish(p);
            return n;
        }
    }

    void Next()
    {
        Disconnect();
        index++;
        partPos = 0;
        if (sha != null) { sha.Dispose(); sha = null; }
        if (index < bundle.Parts.Count && bundle.Parts[index].Sha256 != null) sha = new SHA256CryptoServiceProvider();
    }

    void Finish(Bundle.Part p)
    {
        Disconnect();
        if (sha == null) return;
        sha.TransformFinalBlock(new byte[0], 0, 0);
        var got = string.Concat(sha.Hash.Select(b => b.ToString("x2")));
        if (got != p.Sha256)
            throw new Problem("החלק " + p.Name + " פגום: הוא אינו תואם לרשימת החלקים (sha256)." + (p.Url != null ? " אפשר לנסות שוב." : " צריך להוריד אותו מחדש."));
    }

    void Connect()
    {
        var p = bundle.Parts[index];
        if (p.Url == null)
        {
            cur = new FileStream(p.File, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            return;
        }
        response = Http.Open(new Uri(p.Url), partPos, watch.Set);
        cur = response.GetResponseStream();
        // A server that ignored the range starts from the beginning
        long skip = partPos - Http.StartOf(response);
        if (skip < 0) throw new IOException("the server answered from the wrong place");
        var junk = new byte[1 << 16];
        while (skip > 0)
        {
            int r = cur.Read(junk, 0, (int)Math.Min(skip, junk.Length));
            if (r <= 0) throw new IOException("the connection closed early");
            skip -= r;
        }
    }

    void Disconnect()
    {
        watch.Set(null);
        if (cur != null) { try { cur.Dispose(); } catch { } cur = null; }
        if (response != null) { try { response.Close(); } catch { } response = null; }
    }

    // Waits 2, 4, 8... seconds (at most 30) and lets the next read connect again; a refusal is final.
    void Retry(Exception e)
    {
        Disconnect();
        var p = bundle.Parts[index];
        var http = e as HttpError;
        if (http != null && http.Code >= 400 && http.Code < 500 && http.Code != 408 && http.Code != 429)
            throw new Problem("ההורדה נכשלה: השרת השיב " + http.Code + " על " + p.Name + ".");
        if (++failures > 8) throw new Problem("ההורדה נכשלה (" + e.Message + "). כדאי לבדוק את החיבור לאינטרנט ולנסות שוב.");
        int wait = Math.Min(30, 1 << failures) * 1000;
        for (int t = 0; t < wait; t += 250)
        {
            if (cancelled()) throw new Stopped();
            Thread.Sleep(250);
        }
    }

    public override bool CanRead { get { return true; } }
    public override bool CanSeek { get { return false; } }
    public override bool CanWrite { get { return false; } }
    public override long Length { get { return bundle.Size; } }
    public override long Position { get { return done; } set { throw new NotSupportedException(); } }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
    public override void SetLength(long value) { throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            watch.Dispose();
            Disconnect();
            if (sha != null) { sha.Dispose(); sha = null; }
        }
        base.Dispose(disposing);
    }
}

// Aborts the request on its way when the user stops, so a stop also ends a wait on the network.
sealed class RequestWatch : IDisposable
{
    readonly Timer timer;
    volatile HttpWebRequest current;

    public RequestWatch(Func<bool> cancelled)
    {
        timer = new Timer(_ =>
        {
            var r = current;
            if (r != null && cancelled()) { try { r.Abort(); } catch { } }
        }, null, 500, 500);
    }

    public void Set(HttpWebRequest r) { current = r; }
    public void Dispose() { timer.Dispose(); }
}

// HTTP for the download. Redirects are followed by hand: GitHub sends each release file to a signed address that
// expires within the hour, so every connection, a resumed one too, starts again from the github.com address.
static class Http
{
    static Http()
    {
        // The .NET Framework runs this tool as a 4.0 program, whose defaults may leave out TLS 1.2, which GitHub needs
        ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
        try { if (WebRequest.DefaultWebProxy != null) WebRequest.DefaultWebProxy.Credentials = CredentialCache.DefaultCredentials; } catch { }
    }

    static HttpWebRequest Request(Uri url, long from)
    {
        var r = (HttpWebRequest)WebRequest.Create(url);
        r.AllowAutoRedirect = false;
        r.UserAgent = "ZayitMove";
        r.Timeout = 30000;
        r.ReadWriteTimeout = 60000;
        r.AutomaticDecompression = DecompressionMethods.None;
        if (from > 0) r.AddRange(from);
        return r;
    }

    static HttpWebResponse Get(HttpWebRequest r)
    {
        try { return (HttpWebResponse)r.GetResponse(); }
        catch (WebException e)
        {
            var resp = e.Response as HttpWebResponse;
            if (resp == null) throw;
            return resp;
        }
    }

    // Where `url` redirects to, or null when it does not.
    public static Uri Redirect(Uri url, Action<HttpWebRequest> started)
    {
        var r = Request(url, 0);
        if (started != null) started(r);
        using (var resp = Get(r))
        {
            int code = (int)resp.StatusCode;
            var loc = resp.Headers["Location"];
            if (code >= 300 && code < 400 && loc != null) return new Uri(url, loc);
            if (code >= 400) throw new HttpError(code);
            return null;
        }
    }

    // A small text file (the manifest).
    public static string Text(Uri url, Action<HttpWebRequest> started)
    {
        using (var resp = Open(url, 0, started))
        using (var s = resp.GetResponseStream())
        using (var mem = new MemoryStream())
        {
            var buf = new byte[1 << 16];
            int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0)
            {
                mem.Write(buf, 0, n);
                if (mem.Length > 1 << 20) throw new IOException("the manifest is too large");
            }
            return Encoding.UTF8.GetString(mem.ToArray());
        }
    }

    // The response to `url` from byte `from` on (200 or 206), its redirects followed with the range kept.
    public static HttpWebResponse Open(Uri url, long from, Action<HttpWebRequest> started)
    {
        for (int hop = 0; hop < 8; hop++)
        {
            var r = Request(url, from);
            if (started != null) started(r);
            var resp = Get(r);
            int code = (int)resp.StatusCode;
            var loc = resp.Headers["Location"];
            if (code >= 300 && code < 400 && loc != null)
            {
                resp.Close();
                url = new Uri(url, loc);
                continue;
            }
            if (code == 200 || code == 206) return resp;
            resp.Close();
            throw new HttpError(code);
        }
        throw new IOException("too many redirects");
    }

    // Where the body of a response starts in the file: 0, or the start of its Content-Range.
    public static long StartOf(HttpWebResponse resp)
    {
        if ((int)resp.StatusCode != 206) return 0;
        var m = Regex.Match(resp.Headers["Content-Range"] ?? "", @"bytes\s+(\d+)-");
        if (!m.Success) throw new IOException("a partial answer without its range");
        return long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
    }
}

class HttpError : IOException
{
    public readonly int Code;
    public HttpError(int code) : base("HTTP " + code) { Code = code; }
}

// Just enough JSON for the manifest: objects, arrays, strings, numbers, true, false, null.
static class Json
{
    public static object Parse(string s)
    {
        int i = 0;
        var v = Value(s, ref i);
        Space(s, ref i);
        if (i != s.Length) throw new FormatException("json: text after the value");
        return v;
    }

    public static object Get(Dictionary<string, object> o, string key)
    {
        object v;
        return o.TryGetValue(key, out v) ? v : null;
    }

    public static long Long(Dictionary<string, object> o, string key)
    {
        var v = Get(o, key);
        return v is long ? (long)v : -1;
    }

    static void Space(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

    static object Value(string s, ref int i)
    {
        Space(s, ref i);
        if (i >= s.Length) throw new FormatException("json: ends too soon");
        char c = s[i];
        if (c == '{')
        {
            var o = new Dictionary<string, object>();
            i++;
            Space(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return o; }
            for (;;)
            {
                Space(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new FormatException("json: a key is missing");
                var k = Str(s, ref i);
                Space(s, ref i);
                if (i >= s.Length || s[i++] != ':') throw new FormatException("json: ':' is missing");
                o[k] = Value(s, ref i);
                Space(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; return o; }
                throw new FormatException("json: ',' or '}' is missing");
            }
        }
        if (c == '[')
        {
            var a = new List<object>();
            i++;
            Space(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return a; }
            for (;;)
            {
                a.Add(Value(s, ref i));
                Space(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; return a; }
                throw new FormatException("json: ',' or ']' is missing");
            }
        }
        if (c == '"') return Str(s, ref i);
        if (string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
        if (string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
        if (string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
        int start = i;
        while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
        var num = s.Substring(start, i - start);
        long l;
        if (long.TryParse(num, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out l)) return l;
        double d;
        if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
        throw new FormatException("json: not a value");
    }

    static string Str(string s, ref int i)
    {
        var sb = new StringBuilder();
        i++;
        while (i < s.Length)
        {
            char c = s[i++];
            if (c == '"') return sb.ToString();
            if (c != '\\') { sb.Append(c); continue; }
            if (i >= s.Length) break;
            char e = s[i++];
            if (e == 'u')
            {
                if (i + 4 > s.Length) break;
                sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                i += 4;
            }
            else sb.Append(e == 'n' ? '\n' : e == 't' ? '\t' : e == 'r' ? '\r' : e == 'b' ? '\b' : e == 'f' ? '\f' : e);
        }
        throw new FormatException("json: a string does not end");
    }
}
