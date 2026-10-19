using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

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

            InstallTests(dir, log);
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

    // ---------- installing ----------

    // A release made here as SeforimLibrary makes one: a tar with a pax header before each entry, in one zstd frame,
    // cut in three parts, with a manifest. The frame's blocks are stored, not compressed; the decoding itself is
    // checked on Sample, compressed by zstd.
    static void InstallTests(string dir, Action<string> log)
    {
        Action<long, long> none = (d, t) => { };
        Action<string> quiet = s => { };
        var lib = Release("20261012193827");
        var parts = Path.Combine(dir, "release");
        MakeBundle(lib, parts, true);

        // 9. From the parts on disk: found from any one of them, checked against the manifest, unpacked, pointed at
        var b = Bundle.FromFiles(new[] { Path.Combine(parts, Bundle.Name + ".part02") });
        Check(b.Parts.Count == 3 && b.Hashed && !b.Online, "the parts found from one of them, with their manifest");
        var card = Path.Combine(dir, "card", Library.FolderName);
        Installer.Install(b, card, none, () => false, log, quiet);
        Check(Same(lib, card), "installed from the parts: every file as in the release");
        Check(Library.Same(Library.CurrentDir, card) && Library.Exists(card) && Library.Version(card) == "12/10/2026", "Zayit points at the installed library");
        Check(!Directory.Exists(card + ".installing") && !Directory.Exists(card + ".old"), "no temporary folder is left");
        Check(History.All().Any(e => e.Kind == "install" && Library.Same(e.To, card)), "the install is in the history");

        // 10. A damaged part: refused, and the library there stays as it was
        var bad = Path.Combine(dir, "damaged");
        CopyFolder(parts, bad);
        var p2 = Path.Combine(bad, Bundle.Name + ".part02");
        var bytes = File.ReadAllBytes(p2);
        bytes[bytes.Length / 2] ^= 0x20;
        File.WriteAllBytes(p2, bytes);
        try
        {
            Installer.Install(Bundle.FromFiles(new[] { p2 }), card, none, () => false, log, quiet);
            Check(false, "a damaged part is refused");
        }
        catch (Problem e) { Check(e.Message.Contains(Bundle.Name + ".part02") && e.Message.Contains("sha256"), "a damaged part is refused by its sha256: " + e.Message); }
        Check(Same(lib, card) && !Directory.Exists(card + ".installing") && Library.Same(Library.CurrentDir, card), "after the refusal the library is as it was");

        // 11. A newer release over it, in its place
        var lib2 = Release("20261019000000");
        var parts2 = Path.Combine(dir, "release2");
        MakeBundle(lib2, parts2, true);
        Installer.Install(Bundle.FromFiles(new[] { Path.Combine(parts2, Bundle.Name + ".manifest.json") }), card, none, () => false, log, quiet);
        Check(Same(lib2, card) && Library.Same(Library.CurrentDir, card) && !Directory.Exists(card + ".old"), "a newer release replaces the library in its place");

        // 12. Stopped half way: nothing changes
        int calls = 0;
        try
        {
            Installer.Install(Bundle.FromFiles(new[] { Path.Combine(parts, Bundle.Name + ".part01") }), card, none, () => Interlocked.Increment(ref calls) > 3, log, quiet);
            Check(false, "a stopped install should not finish");
        }
        catch (Stopped) { Check(true, "a stop ends the install"); }
        Check(Same(lib2, card) && !Directory.Exists(card + ".installing"), "after the stop the library is as it was");

        // 13. Without the manifest, the parts are taken as Zayit takes them; a missing last part is noticed
        var bare = Path.Combine(dir, "bare");
        CopyFolder(parts, bare);
        File.Delete(Path.Combine(bare, Bundle.Name + ".manifest.json"));
        var nb = Bundle.FromFiles(new[] { Path.Combine(bare, Bundle.Name + ".part01") });
        Check(nb.Parts.Count == 3 && !nb.Hashed, "parts without a manifest are found in order");
        var other = Path.Combine(dir, "other", Library.FolderName);
        Installer.Install(nb, other, none, () => false, log, quiet);
        Check(Same(lib, other), "installed from parts without a manifest");
        File.Delete(Path.Combine(bare, Bundle.Name + ".part03"));
        Check(Throws(() => Bundle.FromFiles(new[] { Path.Combine(bare, Bundle.Name + ".part01") })), "a missing last part is noticed without the manifest");

        // ... and without it a damaged part is caught by the zstd checksum instead
        var badBare = Path.Combine(dir, "damaged-bare");
        CopyFolder(bad, badBare);
        File.Delete(Path.Combine(badBare, Bundle.Name + ".manifest.json"));
        var third = Path.Combine(dir, "third", Library.FolderName);
        try
        {
            Installer.Install(Bundle.FromFiles(new[] { Path.Combine(badBare, Bundle.Name + ".part01") }), third, none, () => false, log, quiet);
            Check(false, "a damaged part without a manifest is refused");
        }
        catch (Problem e) { Check(e.Message.Contains("checksum") && !Directory.Exists(third), "a damaged part without a manifest is refused by the zstd checksum"); }

        // 14. A database cut in pieces, as Pardes keeps it on a FAT32 card, is no library for Zayit on the computer
        var split = Path.Combine(dir, "split");
        Directory.CreateDirectory(split);
        var db = lib.First(e => e.Key == "seforim.db").Value;
        File.WriteAllBytes(Path.Combine(split, "seforim.db"), db.Take(8192).ToArray());
        File.WriteAllBytes(Path.Combine(split, "seforim.db001"), db.Skip(8192).ToArray());
        File.WriteAllText(Path.Combine(split, "release_info.txt"), "20261012193827");
        Check(Library.IsSplit(split) && !Library.Exists(split) && !Library.Whole(Path.Combine(split, "seforim.db")), "a split database is told apart");
        Check(Library.Whole(Path.Combine(card, "seforim.db")), "a whole database passes the header check");
        Check(Throws(() => Library.Move(card, split, true, none, () => false, log)) && Library.IsSplit(split) && File.Exists(Path.Combine(split, "seforim.db")),
              "a move onto a split library is refused, and leaves it as it was");

        // 15. What an install that did not end left, and Zayit's list of paths to delete at its next start
        Directory.CreateDirectory(Path.Combine(Installer.StagingOf(card), "seforim.db.lucene"));
        File.WriteAllText(Path.Combine(Installer.StagingOf(card), "seforim.db"), "half");
        Directory.CreateDirectory(Library.DefaultDir);
        var marker = Path.Combine(Library.DefaultDir, "pending-db-cleanup.txt");
        var elsewhere = Path.Combine(dir, "elsewhere", "seforim.db");
        File.WriteAllText(marker, Path.Combine(card, "seforim.db") + "\n" + Path.Combine(card, "seforim.db.lucene") + "\n" + elsewhere);
        Installer.Install(Bundle.FromFiles(new[] { Path.Combine(parts2, Bundle.Name + ".part01") }), card, none, () => false, log, quiet);
        Check(Same(lib2, card) && !Directory.Exists(Installer.StagingOf(card)), "a leftover staging folder is cleared and the install goes through");
        Check(File.ReadAllText(marker).Trim() == elsewhere, "Zayit's cleanup list loses the paths of the new library, and keeps the others");
        File.Delete(marker);
        var old = card + ".old";
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "seforim.db"), "the only copy");
        Installer.Install(Bundle.FromFiles(new[] { Path.Combine(parts2, Bundle.Name + ".part01") }), card, none, () => false, log, quiet);
        Check(File.ReadAllText(Path.Combine(old, "seforim.db")) == "the only copy" && Same(lib2, card) &&
              !Directory.GetDirectories(Path.GetDirectoryName(card), Library.FolderName + ".old-*").Any(), "a folder of that name next to it is left alone");
        Directory.Delete(old, true);

        // A swap that fails (a file held open in the library) keeps the checked library, and the next install goes on
        // from it without reading the parts again. Only Windows refuses to rename a file held open.
        if (Path.DirectorySeparatorChar == '\\')
        {
            var held = Path.Combine(dir, "held");
            CopyFolder(parts, held);
            var hb = Bundle.FromFiles(new[] { Path.Combine(held, Bundle.Name + ".part01") });
            bool refused;
            using (new FileStream(Path.Combine(card, "lexical.db"), FileMode.Open, FileAccess.Read, FileShare.None))
                refused = Throws(() => Installer.Install(hb, card, none, () => false, log, quiet));
            Check(refused && Same(lib2, card) && File.Exists(Path.Combine(Installer.StagingOf(card), ".zayitmove-checked")),
                  "a failed swap keeps the library there and the checked new one");
            foreach (var f in Directory.GetFiles(held)) File.Delete(f);
            Installer.Install(hb, card, none, () => false, log, quiet);
            Check(Same(lib, card) && !Directory.Exists(Installer.StagingOf(card)), "the next install goes on from it, without the parts");
            Installer.Install(Bundle.FromFiles(new[] { Path.Combine(parts2, Bundle.Name + ".part01") }), card, none, () => false, log, quiet);
        }

        // 16. Downloaded, from a small server that redirects as GitHub does and breaks one connection half way
        using (var server = new TestServer(parts2, "v3-20261019000000"))
        {
            Environment.SetEnvironmentVariable("ZAYITMOVE_RELEASES", server.Url + "releases");
            var latest = Bundle.Latest(() => false);
            Check(latest.Online && latest.Release == "v3-20261019000000" && latest.Parts.Count == 3, "the latest release found through its redirect");
            var web = Path.Combine(dir, "web", Library.FolderName);
            Installer.Install(null, web, none, () => false, log, quiet);
            Check(Same(lib2, web) && Library.Same(Library.CurrentDir, web), "downloaded and installed: every file as in the release");
            Check(server.Broken && server.Resumed, "a connection broken half way was resumed from where it stopped");
            Environment.SetEnvironmentVariable("ZAYITMOVE_RELEASES", null);
        }

        // 17. zstd's own compression (level 19: Huffman literals in 4 streams, FSE tables, every kind of repeat offset)
        using (var z = new ZstdDecodeStream(new MemoryStream(Convert.FromBase64String(Sample))))
        using (var o = new MemoryStream())
        {
            z.CopyTo(o);
            Check(o.Length == 19278 && Hex(Sha(o.ToArray(), 0, (int)o.Length)) == "9cf1b4affcfe5ac89c59f4392d8ff281fa973b75937b03e7f574097e37c8e2ae", "a frame compressed by zstd decodes exactly");
        }
    }

    // A small library: the two indexes, the database (with a header SQLite would take), lexical.db, catalog.pb and the
    // release, in the bundle's order. A null value is a folder.
    static List<KeyValuePair<string, byte[]>> Release(string release)
    {
        var db = new byte[3 * 4096];
        Encoding.ASCII.GetBytes("SQLite format 3\0").CopyTo(db, 0);
        db[16] = 0x10;                  // pages of 4096 bytes
        db[27] = 1; db[95] = 1;         // the change counter, and "version-valid-for" equal to it
        db[31] = 3;                     // 3 pages
        for (int i = 100; i < db.Length; i++) db[i] = (byte)(i * 7 + release[13]);
        var index = new byte[400 << 10];
        for (int i = 0; i < index.Length; i++) index[i] = (byte)(i * 31 % 251 + release[13]);
        return new List<KeyValuePair<string, byte[]>>
        {
            new KeyValuePair<string, byte[]>("seforim.db.lookup.lucene", null),
            new KeyValuePair<string, byte[]>("seforim.db.lookup.lucene/segments_1", Encoding.ASCII.GetBytes("lookup")),
            new KeyValuePair<string, byte[]>("seforim.db.lucene", null),
            new KeyValuePair<string, byte[]>("seforim.db.lucene/_0.cfs", index),
            new KeyValuePair<string, byte[]>("seforim.db", db),
            new KeyValuePair<string, byte[]>("lexical.db", Encoding.ASCII.GetBytes("lex")),
            new KeyValuePair<string, byte[]>("catalog.pb", Encoding.ASCII.GetBytes("cat")),
            new KeyValuePair<string, byte[]>("release_info.txt", Encoding.ASCII.GetBytes(release)),
        };
    }

    static bool Same(List<KeyValuePair<string, byte[]>> lib, string dir)
    {
        foreach (var e in lib)
        {
            var p = Path.Combine(dir, e.Key.Replace('/', Path.DirectorySeparatorChar));
            if (e.Value == null ? !Directory.Exists(p) : !File.Exists(p) || !File.ReadAllBytes(p).SequenceEqual(e.Value)) return false;
        }
        return true;
    }

    static void MakeBundle(List<KeyValuePair<string, byte[]>> lib, string dir, bool manifest)
    {
        Directory.CreateDirectory(dir);
        var tar = new MemoryStream();
        foreach (var e in lib)
        {
            var pax = Pax("mtime", "1760000000");
            TarHeader(tar, "PaxHeader/" + e.Key, 'x', pax.Length);
            TarData(tar, pax);
            TarHeader(tar, e.Key, e.Value == null ? '5' : '0', e.Value == null ? 0 : e.Value.Length);
            if (e.Value != null) TarData(tar, e.Value);
        }
        tar.Write(new byte[1024], 0, 1024);
        tar.Write(new byte[(10240 - tar.Length % 10240) % 10240], 0, (int)((10240 - tar.Length % 10240) % 10240));
        var z = StoredZstd(tar.ToArray());
        // Three parts, the first two of one size and the last smaller, as the release cuts them
        int k = z.Length / 3 + 1;
        var json = new StringBuilder("{\n  \"formatVersion\": 1,\n  \"file\": \"" + Bundle.Name + "\",\n  \"size\": " + z.Length + ",\n  \"sha256\": \"" + Hex(Sha(z, 0, z.Length)) + "\",\n  \"parts\": [\n");
        for (int i = 0; i < 3; i++)
        {
            int at = i * k, n = Math.Min(k, z.Length - at);
            var name = Bundle.Name + ".part0" + (i + 1);
            using (var f = File.Create(Path.Combine(dir, name))) f.Write(z, at, n);
            json.Append("    { \"file\": \"" + name + "\", \"size\": " + n + ", \"sha256\": \"" + Hex(Sha(z, at, n)) + "\" }" + (i < 2 ? "," : "") + "\n");
        }
        json.Append("  ]\n}\n");
        if (manifest) File.WriteAllText(Path.Combine(dir, Bundle.Name + ".manifest.json"), json.ToString());
    }

    // "<length> key=value\n", the length counting itself
    static byte[] Pax(string key, string value)
    {
        var body = " " + key + "=" + value + "\n";
        int len = body.Length + 1;
        while ((len + body).Length != len) len++;
        return Encoding.ASCII.GetBytes(len + body);
    }

    static void TarHeader(Stream o, string name, char type, long size)
    {
        var h = new byte[512];
        Encoding.ASCII.GetBytes(name).CopyTo(h, 0);
        Octal(h, 100, 8, type == '5' ? 493 : 420);
        Octal(h, 108, 8, 0);
        Octal(h, 116, 8, 0);
        Octal(h, 124, 12, size);
        Octal(h, 136, 12, 1760000000);
        for (int i = 148; i < 156; i++) h[i] = 32;
        h[156] = (byte)type;
        Encoding.ASCII.GetBytes("ustar\0" + "00").CopyTo(h, 257);
        int sum = h.Sum(x => (int)x);
        Octal(h, 148, 7, sum);
        o.Write(h, 0, 512);
    }

    static void Octal(byte[] h, int at, int len, long v)
    {
        Encoding.ASCII.GetBytes(Convert.ToString(v, 8).PadLeft(len - 1, '0')).CopyTo(h, at);
    }

    static void TarData(Stream o, byte[] data)
    {
        o.Write(data, 0, data.Length);
        o.Write(new byte[(512 - data.Length % 512) % 512], 0, (512 - data.Length % 512) % 512);
    }

    // One zstd frame of stored blocks, with its content checksum
    static byte[] StoredZstd(byte[] data)
    {
        var o = new MemoryStream();
        o.Write(new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0x04, 0x50 }, 0, 6);   // magic; a checksum; a window of 1 MiB
        int pos = 0;
        do
        {
            int n = Math.Min(data.Length - pos, 1 << 17);
            int h = (pos + n == data.Length ? 1 : 0) | n << 3;
            o.Write(new[] { (byte)h, (byte)(h >> 8), (byte)(h >> 16) }, 0, 3);
            o.Write(data, pos, n);
            pos += n;
        } while (pos < data.Length);
        var x = new XXH64();
        x.Update(data, 0, data.Length);
        o.Write(BitConverter.GetBytes((uint)x.Digest()), 0, 4);
        return o.ToArray();
    }

    static byte[] Sha(byte[] b, int at, int n)
    {
        using (var h = new SHA256CryptoServiceProvider()) return h.ComputeHash(b, at, n);
    }

    static string Hex(byte[] b) { return string.Concat(b.Select(x => x.ToString("x2"))); }

    static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
    }

    // GitHub's way with release files, on 127.0.0.1: .../latest/download/X goes to .../download/<tag>/X, which goes on
    // to a signed address. The first time part02 is asked for, the connection breaks half way through it.
    sealed class TestServer : IDisposable
    {
        readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        readonly string dir, tag;
        readonly Thread thread;
        public volatile bool Broken, Resumed;

        public TestServer(string dir, string tag)
        {
            this.dir = dir;
            this.tag = tag;
            listener.Start();
            thread = new Thread(() =>
            {
                try { for (;;) { var c = listener.AcceptTcpClient(); new Thread(() => Serve(c)) { IsBackground = true }.Start(); } }
                catch (SocketException) { }
            }) { IsBackground = true };
            thread.Start();
        }

        public string Url { get { return "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/"; } }

        void Serve(TcpClient c)
        {
            try
            {
                using (c)
                using (var s = c.GetStream())
                {
                    var head = new StringBuilder();
                    while (!head.ToString().EndsWith("\r\n\r\n")) { int b = s.ReadByte(); if (b < 0) return; head.Append((char)b); }
                    var lines = head.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None);
                    var path = lines[0].Split(' ')[1];
                    long from = 0;
                    foreach (var l in lines)
                        if (l.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) from = long.Parse(l.Substring(l.IndexOf('=') + 1).TrimEnd('-'));
                    string latest = "/releases/latest/download/", release = "/releases/download/" + tag + "/";
                    if (path.StartsWith(latest)) Send(s, "302 Found", "Location: " + release + path.Substring(latest.Length) + "\r\n", null, 0, 0);
                    else if (path.StartsWith(release)) Send(s, "302 Found", "Location: " + Url + "assets/" + path.Substring(release.Length) + "?sig=1&se=1\r\n", null, 0, 0);
                    else if (path.StartsWith("/assets/"))
                    {
                        var name = path.Substring(8, path.IndexOf('?') - 8);
                        var data = File.ReadAllBytes(Path.Combine(dir, name));
                        if (from > 0)
                        {
                            Resumed = true;
                            Send(s, "206 Partial Content", "Content-Range: bytes " + from + "-" + (data.Length - 1) + "/" + data.Length + "\r\n", data, from, data.Length - from);
                        }
                        else if (name.EndsWith(".part02") && !Broken)
                        {
                            Broken = true;
                            Send(s, "200 OK", "", data, 0, data.Length, data.Length / 2);
                        }
                        else Send(s, "200 OK", "", data, 0, data.Length);
                    }
                    else Send(s, "404 Not Found", "", null, 0, 0);
                }
            }
            catch (IOException) { }
        }

        static void Send(Stream s, string status, string headers, byte[] data, long at, long n, long cut = -1)
        {
            var h = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\n" + headers + "Content-Length: " + n + "\r\nConnection: close\r\n\r\n");
            s.Write(h, 0, h.Length);
            if (data != null) s.Write(data, (int)at, (int)(cut >= 0 ? cut : n));
            s.Flush();
        }

        public void Dispose() { listener.Stop(); }
    }

    // zstd -19 --check over Hebrew lines, zeros, all byte values and a repeated word (19,278 bytes)
    const string Sample =
        "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" +
        "DoOYFqT82aA/YrTt6i4CFuoDrg==";

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
