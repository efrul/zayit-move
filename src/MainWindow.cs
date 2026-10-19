using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.IO.Path;

// One window, one page: where Zayit's library is now, moving it elsewhere (an external drive too), and going back.
class MainWindow : Window
{
    readonly StackPanel content = new StackPanel { Margin = new Thickness(18, 14, 18, 18) };
    readonly ScrollViewer scroll;

    string chosen;                  // where the library is to go (already "…\Zayit library")
    bool keep;                      // leave a copy where it is now
    string confirmDelete;           // a copy whose deletion waits for a second click
    bool confirmReset;
    bool installing;                // the install card is open (it always is when there is no library)
    string installTo;               // where the library is to be installed (already "…\Zayit library")
    bool fromFiles;                 // from parts already downloaded, not from the internet
    Bundle files;                   // those parts, once picked
    string filesError;
    bool busy;
    Thread worker;
    volatile bool stop;
    string task;                    // what runs now ("העברה", "החזרה")
    long done, total;
    volatile string stage;          // what the work does now, under its progress bar
    DateTime started;
    readonly List<KeyValuePair<string, bool>> messages = new List<KeyValuePair<string, bool>>(); // text, is error

    TextBlock progressText, stageText;
    Border progressFill, progressTrack;

    public MainWindow()
    {
        Theme.Dress(this);
        Title = App.Name;
        Icon = Theme.Image("app.png");
        var area = SystemParameters.WorkArea;
        Width = Math.Min(620, area.Width - 40);
        Height = Math.Min(780, area.Height - 30);
        MinWidth = 480;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.Children.Add(Theme.TitleBar(this, App.Name, Theme.Image("app.png")));

        // One island, as Zayit's home page: its olive wallpaper at the top, fading into the panel
        var island = new Grid { Margin = new Thickness(6, 0, 6, 6), Background = Theme.B("PANEL") };
        island.SizeChanged += (s, e) => island.Clip = new RectangleGeometry(new Rect(0, 0, island.ActualWidth, island.ActualHeight), 12, 12);
        var wall = Theme.Image(Theme.Dark ? "wallpaper_dark.jpg" : "wallpaper.jpg");
        if (wall != null)
        {
            var img = new System.Windows.Controls.Image { Source = wall, Stretch = Stretch.UniformToFill, Height = 330, VerticalAlignment = VerticalAlignment.Top, FlowDirection = FlowDirection.LeftToRight };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            island.Children.Add(img);
            var panel = Theme.B("PANEL").Color;
            island.Children.Add(new Rectangle
            {
                Height = 331, VerticalAlignment = VerticalAlignment.Top,
                Fill = new LinearGradientBrush(new GradientStopCollection {
                    new GradientStop(Color.FromArgb(51, panel.R, panel.G, panel.B), 0),
                    new GradientStop(Color.FromArgb(102, panel.R, panel.G, panel.B), 0.35),
                    new GradientStop(Color.FromArgb(204, panel.R, panel.G, panel.B), 0.7),
                    new GradientStop(panel, 1) }, 90)
            });
        }
        scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = content, Focusable = false };
        island.Children.Add(scroll);
        Grid.SetRow(island, 1);
        root.Children.Add(island);
        Content = root;

        var normal = island.Margin;
        StateChanged += (s, e) => island.Margin = WindowState == WindowState.Maximized ? new Thickness(normal.Left + 7, normal.Top, normal.Right + 7, normal.Bottom + 7) : normal;
        // Zayit may have been opened or closed meanwhile
        Activated += (s, e) => { if (!busy) Render(); };
        Closing += (s, e) =>
        {
            if (!busy) return;
            e.Cancel = true;
            Say("יש לעצור את " + task + " לפני הסגירה.", true);
        };
        Render();
    }

    void Say(string text, bool error)
    {
        messages.Add(new KeyValuePair<string, bool>(text, error));
        Render();
    }

    // ---------- the page ----------

    void Render()
    {
        content.Children.Clear();

        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 0, 14) };
        var branch = Theme.Image("branch.png");
        if (branch != null) head.Children.Add(Theme.Picture(branch, 30, 30));
        var h = Theme.T("הספרייה של זית", 20, "TEXT", true);
        h.Margin = new Thickness(10, 0, 0, 0);
        h.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(h);
        content.Children.Add(head);

        var current = Library.CurrentDir;
        var env = Library.EnvOverride;
        if (env != null) content.Children.Add(EnvNotice(env));
        if (!busy && Library.ZayitRunning())
            content.Children.Add(Theme.Note(Theme.G_WARN, "זית פתוחה. כדי להעביר את הספרייה או לחזור לעותק אחר צריך לסגור אותה.", "WARN", "WARNSOFT"));

        content.Children.Add(Where(current));
        var errors = messages.Where(m => m.Value).Select(m => m.Key).ToList();
        var news = messages.Where(m => !m.Value).Select(m => m.Key).ToList();
        if (errors.Count > 0) content.Children.Add(Theme.Note(Theme.G_WARN, string.Join("\n", errors), "RED", "REDSOFT"));
        else if (news.Count > 0) content.Children.Add(Theme.Note(Theme.G_OK, string.Join("\n", news), "TEXT", "ACCENTSOFT"));
        if (busy) content.Children.Add(Work());
        else if (Library.Exists(current))
        {
            content.Children.Add(MoveCard(current));
            var restore = RestoreCard(current);
            if (restore != null) content.Children.Add(restore);
            content.Children.Add(InstallCard(current, true));
        }
        else
        {
            content.Children.Add(InstallCard(current, false));
            var restore = RestoreCard(current);
            if (restore != null) content.Children.Add(restore);
        }
        // Zayit's license (AGPL v3, its section 7b terms) asks every work built on it to show this
        var credit = Theme.T("Powered by the technologies that drive Zayit — https://zayitapp.com/", 11.5, "SUB");
        credit.FlowDirection = FlowDirection.LeftToRight;
        credit.TextAlignment = TextAlignment.Center;
        credit.Margin = new Thickness(0, 6, 0, 0);
        content.Children.Add(credit);
    }

    UIElement EnvNotice(string env)
    {
        var sp = new StackPanel();
        sp.Children.Add(Theme.Note(Theme.G_INFO, "זית פותחת עכשיו ספרייה אחרת, שנקבעה בכלי הספרייה המוקטנת (משתנה הסביבה SEFORIMAPP_DATABASE_PATH). כל עוד היא קבועה, ההעברה כאן לא משנה מה זית פותחת.", "WARN", "WARNSOFT"));
        var p = Theme.PathText(env);
        p.Margin = new Thickness(0, 0, 0, 4);
        sp.Children.Add(p);
        sp.Children.Add(Theme.Btn(this, "ביטול ההפניה (זית תחזור לספרייה שלה)", "Outlined", () =>
        {
            try { Library.ClearEnvOverride(); messages.Clear(); Say("ההפניה בוטלה. זית תפתח את הספרייה שלה בפתיחה הבאה.", false); }
            catch (Exception e) { Say(e.Message, true); }
        }));
        sp.Margin = new Thickness(0, 0, 0, 12);
        return sp;
    }

    // Where the library is now: the drive, the folder, its size and release, the room left there.
    UIElement Where(string current)
    {
        var rows = new List<UIElement> { Theme.Title("איפה הספרייה עכשיו") };
        if (!Library.Exists(current))
        {
            if (Library.IsSplit(current))
                rows.Add(Theme.Note(Theme.G_WARN, "הספרייה כאן מפוצלת לחלקים (seforim.db001 ואילך), כמו שפרדס שומר אותה בכרטיס FAT32. זית למחשב לא יכולה לפתוח ספרייה כזו. אפשר להתקין למטה ספרייה שלמה, בכונן שאינו FAT32.", "RED", "REDSOFT"));
            else if (Library.IsDefault(current) && Prefs.Get("database_path", "").Trim().Length == 0)
                rows.Add(Theme.Note(Theme.G_INFO, "עדיין אין ספרייה במחשב הזה. אפשר להתקין אותה למטה ישר לכל כונן, גם לכונן חיצוני או לכרטיס זיכרון, בלי 15 GB פנויים בכונן C שזית דורשת כשהיא מתקינה בעצמה.", "TEXT", "ACCENTSOFT"));
            else
                rows.Add(Theme.Note(Theme.G_WARN, "הספרייה לא נמצאת במקום שזית מחפשת אותה. אם היא בכונן חיצוני, חברו אותו (באותה אות כונן) ופתחו את הכלי שוב.", "RED", "REDSOFT"));
            rows.Add(Theme.PathText(current));
            return Theme.Card(rows.ToArray());
        }
        var drive = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
        var removable = Library.DriveLabel(current).StartsWith("כונן נשלף");
        drive.Children.Add(Theme.Glyph(removable ? Theme.G_USB : Theme.G_DRIVE, 15, "ACCENT"));
        var dl = Theme.T(Library.DriveLabel(current) + (Library.IsDefault(current) ? "  ·  התיקייה הרגילה של זית" : ""), 14);
        dl.Margin = new Thickness(8, 0, 0, 0);
        drive.Children.Add(dl);
        rows.Add(drive);
        var path = Theme.PathText(current);
        path.Margin = new Thickness(0, 0, 0, 8);
        rows.Add(path);
        var version = Library.Version(current);
        rows.Add(Theme.Pair("גודל", Fmt.Size(Library.Size(current))));
        if (version != null) rows.Add(Theme.Pair("מהדורה", version + (Library.IsSmall(current) ? "  ·  ספרייה מוקטנת" : "")));
        var free = Library.Free(current);
        if (free >= 0) rows.Add(Theme.Pair("פנויים בכונן", Fmt.Size(free)));
        return Theme.Card(rows.ToArray());
    }

    // ---------- moving ----------

    UIElement MoveCard(string current)
    {
        var rows = new List<UIElement> { Theme.Title("העברה למקום אחר") };
        bool has = Library.Exists(current);
        if (chosen == null)
        {
            rows.Add(Theme.T("בוחרים כונן או תיקייה, והספרייה נכנסת לתיקייה \"" + Library.FolderName + "\" שבתוכם. ההערות, ההגדרות והלשוניות נשארות במחשב.", 13, "SUB"));
            var pick = Theme.IconBtn(this, Theme.G_FOLDER, "בחירת מקום…", "Outlined", () =>
            {
                var f = FolderPicker.Pick(this, "לאן להעביר את הספרייה של זית?");
                if (f == null) return;
                chosen = Library.Destination(f);
                messages.Clear();
                Render();
            });
            pick.IsEnabled = has;
            Button home = null;
            if (has && !Library.IsDefault(current))
                home = Theme.Btn(this, "החזרה לתיקייה הרגילה של זית", "Outlined", () => { chosen = Library.DefaultDir; messages.Clear(); Render(); });
            rows.Add(Theme.Row(pick, home));
            var info = Theme.T("עדכון הספרייה מתוך זית מוחק אותה ממקומה ומתקין את החדשה בתיקייה הרגילה. אחרי עדכון כזה אפשר להעביר אותה שוב.", 12, "SUB");
            info.Margin = new Thickness(0, 10, 0, 0);
            rows.Add(info);
            return Theme.Card(rows.ToArray());
        }

        long size = Library.Size(current);
        bool sameDrive = string.Equals(Path.GetPathRoot(Path.GetFullPath(current)), Path.GetPathRoot(Path.GetFullPath(chosen)), StringComparison.OrdinalIgnoreCase);
        rows.Add(Theme.T("אל " + Library.DriveLabel(chosen) + (Library.IsDefault(chosen) ? "  ·  התיקייה הרגילה של זית" : ""), 14));
        var p = Theme.PathText(chosen);
        p.Margin = new Thickness(0, 0, 0, 8);
        rows.Add(p);

        if (Library.Same(chosen, current))
        {
            rows.Add(Theme.Note(Theme.G_INFO, "הספרייה כבר נמצאת שם.", "TEXT", "ACCENTSOFT"));
            rows.Add(Theme.Row(Theme.Btn(this, "בחירה אחרת", "Outlined", () => { chosen = null; Render(); })));
            return Theme.Card(rows.ToArray());
        }
        if (Library.Exists(chosen))
        {
            var v = Library.Version(chosen);
            rows.Add(Theme.Note(Theme.G_INFO, "כבר יש שם ספרייה (" + Fmt.Size(Library.Size(chosen)) + (v != null ? ", מהדורה " + v : "") + "). אפשר לעבור אליה בלי להעתיק דבר; הספרייה הנוכחית נשארת במקומה.", "TEXT", "ACCENTSOFT"));
            rows.Add(Theme.Row(Theme.Btn(this, "מעבר אליה", "Default", () => Quick(() => Library.SwitchTo(chosen, Log))),
                         Theme.Btn(this, "בחירה אחרת", "Outlined", () => { chosen = null; Render(); })));
            return Theme.Card(rows.ToArray());
        }

        long free = Library.Free(chosen);
        bool copies = keep || !sameDrive;
        bool fat = !sameDrive && Library.IsFat(chosen) && Library.Largest(current) > Installer.Fat32Max;
        rows.Add(Theme.Pair("נדרשים", copies ? Fmt.Size(size) : "בלי מקום נוסף (אותו כונן)"));
        if (free >= 0) rows.Add(Theme.Pair("פנויים שם", Fmt.Size(free)));
        bool room = !copies || (free >= 0 && free > size + (200L << 20));

        var cb = new CheckBox { Content = "להשאיר עותק גם במקום הנוכחי", IsChecked = keep, Margin = new Thickness(0, 12, 0, 0) };
        cb.Checked += (s, e) => { keep = true; Render(); };
        cb.Unchecked += (s, e) => { keep = false; Render(); };
        rows.Add(cb);
        var why = Theme.T(keep
            ? "החזרה אליו תהיה מיידית, בלי העתקה. העותק תופס " + Fmt.Size(size) + " ב־" + Library.DriveLabel(current) + "."
            : sameDrive ? "באותו כונן ההעברה מיידית: הקבצים רק עוברים תיקייה." : "הספרייה מועתקת, נבדקת, ורק אז זית עוברת אליה והעותק הישן נמחק.", 12, "SUB");
        why.Margin = new Thickness(26, 2, 0, 0);
        rows.Add(why);

        if (free < 0) rows.Add(Theme.Note(Theme.G_WARN, "הכונן לא זמין.", "RED", "REDSOFT"));
        else if (fat) rows.Add(Theme.Note(Theme.G_WARN, Library.FatText(chosen), "RED", "REDSOFT"));
        else if (!room) rows.Add(Theme.Note(Theme.G_WARN, "אין שם מספיק מקום: צריך " + Fmt.Size(size + (200L << 20)) + ".", "RED", "REDSOFT"));
        if (Library.DriveLabel(chosen).StartsWith("כונן נשלף"))
            rows.Add(Theme.Note(Theme.G_USB, RemovableText, "TEXT", "ACCENTSOFT"));

        var go = Theme.Btn(this, keep ? "העתקה ומעבר" : "העברה", "Default", () =>
        {
            var to = chosen;
            Run(keep ? "ההעתקה" : "ההעברה", () => Library.Move(current, to, keep, Progress, () => stop, Log), () => { chosen = null; });
        });
        go.IsEnabled = room && free >= 0 && !fat;
        rows.Add(Theme.Row(go, Theme.Btn(this, "ביטול", "Outlined", () => { chosen = null; Render(); })));
        return Theme.Card(rows.ToArray());
    }

    const string RemovableText = "בכונן נשלף: הוא צריך להיות מחובר כשפותחים את זית, ובדיסק־און־קי החיפוש איטי יותר.";

    // ---------- installing ----------

    // The library installed straight into a place of one's choice, from the internet or from parts already
    // downloaded: before Zayit installs it itself (which needs 15 GB free on C), or instead of Zayit's library update
    // (which deletes the library where it is and installs the new one on C).
    UIElement InstallCard(string current, bool has)
    {
        var rows = new List<UIElement> { Theme.Title(has ? "התקנת הספרייה מחדש" : "התקנת הספרייה") };
        if (has && !installing)
        {
            rows.Add(Theme.T("אפשר להתקין את המהדורה האחרונה של הספרייה ישר לכל מקום, גם לכונן חיצוני או לכרטיס זיכרון, מהאינטרנט או מקובצי ההפצה שכבר הורדו. כך אפשר גם לעדכן אותה במקומה: העדכון מתוך זית מוחק אותה ומתקין את החדשה בכונן C.", 13, "SUB"));
            rows.Add(Theme.Row(Theme.IconBtn(this, Theme.G_DOWNLOAD, "התקנה…", "Outlined", () => { installing = true; messages.Clear(); Render(); })));
            return Theme.Card(rows.ToArray());
        }
        if (installTo == null)
        {
            rows.Add(Theme.T("הספרייה נכנסת ישר לכונן או לתיקייה שתבחרו, לתיקייה \"" + Library.FolderName + "\" שבתוכם, בלי לעבור דרך כונן C: מהאינטרנט (הורדה של כ־5.5 GB), או מקובצי ההפצה part01, part02 וכן הלאה שכבר הורדו. אם כבר יש ספרייה במקום שתבחרו, אפשר לעבור אליה בלי להתקין.", 13, "SUB"));
            var pick = Theme.IconBtn(this, Theme.G_FOLDER, "בחירת מקום…", "Outlined", () =>
            {
                var f = FolderPicker.Pick(this, "לאן להתקין את הספרייה של זית?");
                if (f == null) return;
                installTo = Library.Destination(f);
                messages.Clear();
                Render();
            });
            rows.Add(Theme.Row(pick, has ? Theme.Btn(this, "ביטול", "Outlined", () => { installing = false; Render(); }) : null));
            return Theme.Card(rows.ToArray());
        }

        rows.Add(Theme.T("אל " + Library.DriveLabel(installTo) + (Library.IsDefault(installTo) ? "  ·  התיקייה הרגילה של זית" : ""), 14));
        var p = Theme.PathText(installTo);
        p.Margin = new Thickness(0, 0, 0, 8);
        rows.Add(p);
        bool here = has && Library.Same(installTo, current);
        bool there = !here && Library.Exists(installTo);
        if (here)
            rows.Add(Theme.Note(Theme.G_INFO, "זו הספרייה שזית פותחת עכשיו. החדשה תחליף אותה רק אחרי שתותקן ותיבדק כולה, ועד אז היא נשארת כמו שהיא.", "TEXT", "ACCENTSOFT"));
        else if (there)
        {
            var v = Library.Version(installTo);
            rows.Add(Theme.Note(Theme.G_INFO, "כבר יש שם ספרייה (" + Fmt.Size(Library.Size(installTo)) + (v != null ? ", מהדורה " + v : "") + "). אפשר לעבור אליה בלי להתקין דבר, או להתקין במקומה.", "TEXT", "ACCENTSOFT"));
            var to = installTo;
            rows.Add(Theme.Row(Theme.Btn(this, "מעבר אליה", "Default", () => Quick(() => { Library.SwitchTo(to, Log); installTo = null; installing = false; }))));
        }
        else if (Library.IsSplit(installTo))
            rows.Add(Theme.Note(Theme.G_INFO, "יש שם ספרייה מפוצלת לחלקים, כמו שפרדס שומר אותה בכרטיס FAT32, וזית למחשב לא יכולה לפתוח אותה. ההתקנה תחליף אותה בספרייה שלמה.", "TEXT", "ACCENTSOFT"));

        var online = new RadioButton { Content = "הורדה מהאינטרנט (כ־5.5 GB)", IsChecked = !fromFiles, Margin = new Thickness(0, 12, 0, 0) };
        var offline = new RadioButton { Content = "מקובצי ההפצה שכבר הורדו (part01, part02…)", IsChecked = fromFiles, Margin = new Thickness(0, 8, 0, 0) };
        online.Checked += (s, e) => { fromFiles = false; Render(); };
        offline.Checked += (s, e) => { fromFiles = true; Render(); };
        rows.Add(online);
        if (!fromFiles)
        {
            var t = Theme.T("המהדורה האחרונה יורדת מ־GitHub, מאותו מקום שזית מורידה ממנו, ונפרסת תוך כדי ההורדה, בלי לשמור עותק שלה.", 12, "SUB");
            t.Margin = new Thickness(26, 2, 0, 0);
            rows.Add(t);
        }
        rows.Add(offline);
        if (fromFiles)
        {
            var sp = new StackPanel { Margin = new Thickness(26, 2, 0, 0) };
            if (files != null)
            {
                sp.Children.Add(Theme.T(files.Parts.Count + (files.Parts.Count == 1 ? " קובץ, " : " חלקים, ") + Fmt.Size(files.Size) + (files.Hashed
                    ? ". כל חלק ייבדק מול רשימת החלקים (manifest) שפורסמה איתם."
                    : ". אין לידם רשימת חלקים (manifest), ולכן הם ייבדקו לפי הגודל ולפי סכום הביקורת של הדחיסה."), 12, "SUB"));
                sp.Children.Add(Theme.PathText(files.Folder, 12));
            }
            else sp.Children.Add(Theme.T("בוחרים את part01, את כל החלקים או את קובץ ה־manifest. שאר החלקים נמצאים לידו, באותה תיקייה.", 12, "SUB"));
            if (filesError != null) sp.Children.Add(Theme.Note(Theme.G_WARN, filesError, "RED", "REDSOFT"));
            sp.Children.Add(Theme.Row(Theme.IconBtn(this, Theme.G_OPEN, files == null ? "בחירת הקבצים…" : "בחירה אחרת…", "Outlined", PickFiles)));
            rows.Add(sp);
        }

        long need = Installer.Unpacked(fromFiles && files != null ? files.Size : Installer.TypicalBundle) + Library.Margin;
        long free = Library.Free(installTo);
        bool fat = Library.IsFat(installTo);
        var needRow = Theme.Pair("נדרשים", "כ־" + Fmt.Size(need));
        needRow.Margin = new Thickness(0, 12, 0, 2);
        rows.Add(needRow);
        if (free >= 0) rows.Add(Theme.Pair("פנויים שם", Fmt.Size(free)));
        if (free < 0) rows.Add(Theme.Note(Theme.G_WARN, "הכונן לא זמין.", "RED", "REDSOFT"));
        else if (fat) rows.Add(Theme.Note(Theme.G_WARN, Library.FatText(installTo), "RED", "REDSOFT"));
        else if (free < need)
            rows.Add(Theme.Note(Theme.G_WARN, "אין שם מספיק מקום: צריך כ־" + Fmt.Size(need) + "." + (here || there ? " הספרייה שכבר שם נמחקת רק בסוף, אחרי שהחדשה נבדקה, ולכן צריך מקום גם לחדשה." : ""), "RED", "REDSOFT"));
        if (Library.DriveLabel(installTo).StartsWith("כונן נשלף"))
            rows.Add(Theme.Note(Theme.G_USB, RemovableText, "TEXT", "ACCENTSOFT"));

        var go = Theme.Btn(this, here || there ? "התקנה במקומה" : "התקנה", "Default", () =>
        {
            var to = installTo;
            var b = fromFiles ? files : null;
            Run("ההתקנה", () => Installer.Install(b, to, Progress, () => stop, Log, Stage), () => { installTo = null; installing = false; files = null; fromFiles = false; });
        });
        go.IsEnabled = free >= need && !fat && (!fromFiles || files != null);
        rows.Add(Theme.Row(go, Theme.Btn(this, "ביטול", "Outlined", () => { installTo = null; files = null; filesError = null; fromFiles = false; installing = false; Render(); })));
        var info = Theme.T("החלקים נבדקים אחד אחד ונפרסים לתיקייה זמנית ליד היעד. רק כשהכול נבדק הספרייה נכנסת למקומה וזית מכוונת אליה. אפשר לעצור בכל רגע.", 12, "SUB");
        info.Margin = new Thickness(0, 10, 0, 0);
        rows.Add(info);
        return Theme.Card(rows.ToArray());
    }

    void PickFiles()
    {
        var d = new Microsoft.Win32.OpenFileDialog
        {
            Title = "קובצי הספרייה של זית",
            Multiselect = true,
            Filter = "קובצי הספרייה (*.part01, *.manifest.json)|*.part*;*.tar.zst;*.manifest.json|כל הקבצים|*.*"
        };
        if (d.ShowDialog(this) != true) return;
        try { files = Bundle.FromFiles(d.FileNames); filesError = null; }
        catch (Exception e) { files = null; filesError = e.Message; }
        Render();
    }

    // ---------- going back ----------

    UIElement RestoreCard(string current)
    {
        var last = History.LastMove();
        var copies = History.Copies(current);
        bool has = Library.Exists(current);
        bool back = last != null && Library.Same(last.To, current) && has && !Library.Exists(last.From);
        if (last == null && copies.Count == 0 && has) return null;

        var rows = new List<UIElement> { Theme.Title("שחזור") };
        if (last != null)
        {
            rows.Add(Theme.T((last.Kind == "copy" ? "העתקה אחרונה" : "העברה אחרונה") + ": " + last.When.ToString("dd/MM/yyyy HH:mm"), 13, "SUB"));
            rows.Add(Theme.PathRow("מ־", last.From));
            rows.Add(Theme.PathRow("אל", last.To));
        }

        if (back)
        {
            var spacer = Theme.T("", 6);
            rows.Add(spacer);
            rows.Add(Theme.T("במקום הקודם לא נשאר עותק, אז החזרה מעתיקה את הספרייה בחזרה, בודקת אותה, ורק אז מוחקת את העותק שכאן.", 13, "SUB"));
            var target = last.From;
            rows.Add(Theme.Row(Theme.IconBtn(this, Theme.G_BACK, "החזרה למקום הקודם", "Default", () =>
                Run("ההחזרה", () => Library.Move(current, target, false, Progress, () => stop, Log), null))));
        }

        foreach (var dir in copies) rows.Add(CopyRow(dir, last));

        if (!has && copies.Count == 0)
        {
            rows.Add(Theme.T("אין עותק אחר של הספרייה. אפשר להחזיר את זית לתיקייה הרגילה שלה; אם אין שם ספרייה, זית תציע להוריד אותה.", 13, "SUB"));
            if (!confirmReset)
                rows.Add(Theme.Row(Theme.Btn(this, "החזרת זית לתיקייה הרגילה", "Outlined", () => { confirmReset = true; Render(); })));
            else
                rows.Add(Theme.Row(Theme.Btn(this, "כן, להחזיר", "Default", () => { confirmReset = false; Quick(() => Library.Reset(Log)); }),
                             Theme.Btn(this, "ביטול", "Outlined", () => { confirmReset = false; Render(); })));
        }
        return Theme.Card(rows.ToArray());
    }

    // A copy elsewhere: switch Zayit to it at once, or delete it.
    UIElement CopyRow(string dir, History.Entry last)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var v = Library.Version(dir);
        bool previous = last != null && Library.Same(last.From, dir);
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(Theme.Glyph(Theme.G_COPY, 13, "ACCENT"));
        var t = Theme.T((previous ? "עותק במקום הקודם" : "עותק") + "  ·  " + Library.DriveLabel(dir) + "  ·  " + Fmt.Size(Library.Size(dir)) + (v != null ? "  ·  מהדורה " + v : ""), 13.5);
        t.Margin = new Thickness(8, 0, 0, 0);
        line.Children.Add(t);
        sp.Children.Add(line);
        sp.Children.Add(Theme.PathText(dir));
        if (confirmDelete != null && Library.Same(confirmDelete, dir))
        {
            sp.Children.Add(Theme.Note(Theme.G_WARN, "למחוק את העותק הזה (" + Fmt.Size(Library.Size(dir)) + ")? אי אפשר לבטל את זה.", "RED", "REDSOFT"));
            sp.Children.Add(Theme.Row(Theme.Btn(this, "מחיקה", "Danger", () => { confirmDelete = null; Quick(() => Library.DeleteCopy(dir, Log)); }),
                                Theme.Btn(this, "ביטול", "Outlined", () => { confirmDelete = null; Render(); })));
        }
        else
        {
            sp.Children.Add(Theme.Row(Theme.IconBtn(this, previous ? Theme.G_BACK : Theme.G_SWITCH, previous ? "חזרה מיידית לעותק הזה" : "מעבר לעותק הזה", previous ? "Default" : "Outlined", () => Quick(() => Library.SwitchTo(dir, Log))),
                                Theme.Btn(this, "מחיקת העותק", "Link", () => { confirmDelete = dir; Render(); })));
        }
        return sp;
    }

    // ---------- running ----------

    void Log(string text) { Dispatcher.BeginInvoke(new Action(() => { messages.Add(new KeyValuePair<string, bool>(text, false)); })); }

    // A short step (switching, deleting): on the window's thread, its message under the cards.
    void Quick(Action a)
    {
        messages.Clear();
        try { a(); }
        catch (Exception e) { messages.Add(new KeyValuePair<string, bool>(e.Message, true)); }
        Dispatcher.BeginInvoke(new Action(Render), DispatcherPriority.Background);
    }

    // A copy that takes minutes: on its own thread, with a progress bar and a stop button.
    void Run(string name, Action work, Action after)
    {
        messages.Clear();
        busy = true;
        stop = false;
        task = name;
        stage = null;
        done = 0;
        total = 0;
        started = DateTime.Now;
        Render();
        worker = new Thread(() =>
        {
            Exception error = null;
            try { work(); }
            catch (Exception e) { error = e; }
            Dispatcher.BeginInvoke(new Action(() =>
            {
                busy = false;
                if (error == null) { if (after != null) after(); }
                else messages.Add(new KeyValuePair<string, bool>(error is Problem || error is Stopped ? error.Message : "שגיאה: " + error.Message + " הספרייה נשארה במקומה.", true));
                Render();
            }));
        });
        worker.IsBackground = true;
        worker.Start();
    }

    long lastTick;

    void Progress(long d, long t)
    {
        done = d;
        total = t;
        long now = Environment.TickCount;
        if (now - lastTick < 120 && d < t) return;
        lastTick = now;
        Dispatcher.BeginInvoke(new Action(UpdateProgress));
    }

    UIElement Work()
    {
        progressFill = new Border { Background = Theme.B("ACCENT"), CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        progressTrack = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = Theme.B("BORDER"), Child = progressFill, Margin = new Thickness(0, 6, 0, 8) };
        progressTrack.SizeChanged += (s, e) => UpdateProgress();
        progressText = Theme.T("מתחיל…", 13, "SUB");
        stageText = Theme.T(stage ?? "", 13);
        stageText.Visibility = stage == null ? Visibility.Collapsed : Visibility.Visible;
        var stopBtn = Theme.Btn(this, "עצירה", "Outlined", () => { stop = true; progressText.Text = "עוצר…"; });
        return Theme.Card(Theme.Title(task == null ? "" : task.Substring(1)), stageText, progressTrack, progressText, Theme.Row(stopBtn),
                    Theme.T(task == "ההתקנה"
                        ? "אפשר לעצור בכל רגע לפני הסוף: מה שכבר נכתב נמחק, ושום דבר אחר לא משתנה."
                        : "אפשר לעצור בכל רגע לפני הסוף: העותק החלקי נמחק והספרייה נשארת במקומה.", 12, "SUB"));
    }

    void Stage(string s)
    {
        stage = s;
        Dispatcher.BeginInvoke(new Action(UpdateProgress));
    }

    void UpdateProgress()
    {
        if (stageText != null)
        {
            var s = stage;
            stageText.Text = s ?? "";
            stageText.Visibility = s == null ? Visibility.Collapsed : Visibility.Visible;
        }
        if (progressFill == null || total <= 0) return;
        double f = Math.Min(1, done / (double)total);
        progressFill.Width = Math.Max(0, (progressTrack.ActualWidth) * f);
        if (stop) return;
        var secs = (DateTime.Now - started).TotalSeconds;
        string speed = "", left = "";
        if (secs > 2 && done > 0)
        {
            double rate = done / secs;
            speed = "  ·  " + Fmt.Size((long)rate) + "/s";
            var rest = (total - done) / Math.Max(1, rate);
            left = rest < 60 ? "  ·  פחות מדקה" : "  ·  עוד כ־" + Math.Ceiling(rest / 60) + " דק׳";
        }
        progressText.Text = Fmt.Size(done) + " מתוך " + Fmt.Size(total) + speed + left;
    }
}
