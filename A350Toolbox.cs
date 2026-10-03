// 微软2024工具箱 —— MSFS2024 相关工具的下载器客户端
// 下载 → 自动解压 → 「启动」直接运行；自动检测各项目最新版本 vs 本地已装版本，有新版提示更新。
// 直链在 exe 旁的 links.txt 里改：  {key}=下载直链   {key}_home=项目主页   （# 开头为注释）
// 编译:
//   csc /nologo /target:winexe /codepage:65001 /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /out:微软2024工具箱.exe A350Toolbox.cs
// 自检:
//   微软2024工具箱.exe --selftest          链接状态 + 最新版本 + 本地版本
//   微软2024工具箱.exe --get amdb|efb      无窗完整走一遍 下载→解压→定位主程序（测试用）

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

class A350Toolbox : Form
{
    class Card
    {
        public string Key;
        public string Title;
        public string Desc;
        public string MainExe;      // 解压后要启动的主程序文件名
        public string DefUrl = "";
        public string DefHome = "";
        public string Url;
        public string Home;
        public string LatestVer;    // GitHub 最新版（null=检测中，"?"=检测失败）
        public string LocalVer;     // 本地已装版本（""=未知）
        public string AdoptedPath;  // 非空=本机已有的副本（非工具箱下载）
        public Label Status;
        public Label LblVer;
        public Button BtnDown;      // 下载 / 启动 / 更新（三态）
        public Button BtnHome;
        public Button BtnRe;        // 重新下载
        public ProgressBar Bar;
        public WebClient Client;
    }

    static string ExeDir { get { return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'); } }
    static string DlDir { get { return Path.Combine(ExeDir, "downloads"); } }
    readonly List<Card> cards = new List<Card>();

    static List<Card> BuildCardList()
    {
        var list = new List<Card>();

        var a = new Card();
        a.Key = "amdb";
        a.Title = "AMDB Bridge 中文壳";
        a.Desc = "把免费的机场地图工具 AMDB Bridge 的界面换成中文。需先安装 AMDB Bridge 本体。";
        a.MainExe = "AMDB-ZH.exe";
        a.DefUrl = "https://github.com/267916/amdb-bridge-zh/releases/latest/download/AMDB-Bridge-ZH-v1.1.zip";
        a.DefHome = "https://github.com/267916/amdb-bridge-zh";
        list.Add(a);

        var b = new Card();
        b.Key = "efb";
        b.Title = "A350 座舱与 EFB 汉化";
        b.Desc = "座舱提示 91.9% 中文 + EFB 平板中文层（中/EN 一键切换）。启动后双击应用器写入机模。";
        b.MainExe = "A350座舱中文.exe";
        b.DefUrl = "https://github.com/267916/a350-zh/releases/latest/download/A350-ZH-v1.1.zip";
        b.DefHome = "https://github.com/267916/a350-zh";
        list.Add(b);

        var c = new Card();
        c.Key = "copilot";
        c.Title = "A350 语音副驾驶";
        c.Desc = "语音控制 + 云端 AI + 中文语音。开发测试中，尚未发布；发布后把直链填进 links.txt 即可。";
        c.MainExe = "Copilot-P0.exe";
        c.DefUrl = "";
        c.DefHome = "";
        list.Add(c);

        return list;
    }

    A350Toolbox()
    {
        cards.AddRange(BuildCardList());
        LoadLinks();

        Text = "微软2024工具箱";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(464, 448);
        Font = new Font("Microsoft YaHei UI", 9F);

        try
        {
            var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(13, 17, 23));
                using (var br = new SolidBrush(Color.FromArgb(0, 255, 255)))
                    g.DrawString("24", new Font("Segoe UI", 13F, FontStyle.Bold), br, 2, 6);
            }
            Icon = Icon.FromHandle(bmp.GetHicon());
        }
        catch { }

        int y = 12;
        foreach (var c in cards) { AddCard(c, y); y += 128; }

        var foot = new Label();
        foot.Text = "下载后自动解压，检测到新版本会提示更新";
        foot.ForeColor = Color.Gray;
        foot.AutoSize = true;
        foot.Location = new Point(14, y + 4);
        Controls.Add(foot);

        var open = new LinkLabel();
        open.Text = "打开下载文件夹";
        open.AutoSize = true;
        open.Location = new Point(356, y + 4);
        open.LinkClicked += delegate { OpenDlDir(); };
        Controls.Add(open);
    }

    // 只在 UI 模式下调用（命令行模式构造窗体后不做网络异步回调，避免跨线程）
    public void StartVersionChecks()
    {
        foreach (var c in cards) { RefreshCard(c); FetchLatestAsync(c); }
    }

    // links.txt 覆盖 + 本地版本读取（静态，命令行模式共用）
    static void LoadLinksInto(List<Card> list)
    {
        foreach (var c in list) { c.Url = c.DefUrl; c.Home = c.DefHome; c.LocalVer = null; }
        string p = Path.Combine(ExeDir, "links.txt");
        if (File.Exists(p))
        {
            string[] lines;
            try { lines = File.ReadAllLines(p, Encoding.UTF8); } catch { lines = new string[0]; }
            foreach (string raw in lines)
            {
                string s = (raw ?? "").Trim();
                if (s.Length == 0 || s[0] == '#') continue;
                int i = s.IndexOf('=');
                if (i <= 0) continue;
                string k = s.Substring(0, i).Trim();
                string v = s.Substring(i + 1).Trim();
                foreach (var c in list)
                {
                    if (k == c.Key) c.Url = v;
                    else if (k == c.Key + "_home") c.Home = v;
                }
            }
        }
        foreach (var c in list) { c.LocalVer = ReadLocalVer(c); }
    }

    void LoadLinks()
    {
        LoadLinksInto(cards);
    }

    // ---------- 版本 ----------
    static string VerFile(Card c) { return Path.Combine(DestDir(c), "version.txt"); }

    static string ReadLocalVer(Card c)
    {
        if (!File.Exists(VerFile(c))) return null;      // null = 未安装过
        try { return File.ReadAllText(VerFile(c), Encoding.UTF8).Trim(); }  // "" = 未知
        catch { return ""; }
    }

    static void SaveLocalVer(Card c, string ver)
    {
        try { File.WriteAllText(VerFile(c), ver ?? "", Encoding.UTF8); } catch { }
        c.LocalVer = ver ?? "";
    }

    // 从直链附件名解析版本：.../AMDB-Bridge-ZH-v1.1.zip -> 1.1
    static string ParseVerFromUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        try
        {
            string name = Uri.UnescapeDataString(url.Split('?')[0]);
            int i = name.LastIndexOf('/');
            if (i >= 0) name = name.Substring(i + 1);
            return ParseVerFromName(name);
        }
        catch { return ""; }
    }

    static string ParseVerFromName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        try
        {
            // 匹配结尾的版本号段：兼容 "X-v1.1.zip" / "X-v1.1"(目录名) / "X-1.2"
            var m = Regex.Match(name.Trim(), @"[.\-_ \[]?[vV]?(\d+(?:\.\d+){1,3})(?:\.zip)?\s*$");
            if (m.Success) return m.Groups[1].Value;
            var m2 = Regex.Match(name, @"(\d+(\.\d+)+)");
            return m2.Success ? m2.Groups[1].Value : "";
        }
        catch { return ""; }
    }

    static string NormVer(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Trim().TrimStart('v', 'V');
    }

    static int CmpVer(string a, string b)
    {
        if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return 0;
        if (string.IsNullOrEmpty(a)) return -1;
        if (string.IsNullOrEmpty(b)) return 1;
        string[] pa = a.Split('.'), pb = b.Split('.');
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            int x = 0, y = 0;
            if (i < pa.Length) int.TryParse(pa[i], out x);
            if (i < pb.Length) int.TryParse(pb[i], out y);
            if (x != y) return x > y ? 1 : -1;
        }
        return 0;
    }

    // GitHub Releases API 取最新 tag（匿名请求，够用）
    void FetchLatestAsync(Card c)
    {
        if (string.IsNullOrEmpty(c.Url)) return;
        Match m = Regex.Match(c.Url, @"^https://github\.com/([^/]+)/([^/]+)/");
        if (!m.Success) { c.LatestVer = "?"; UpdateVerLabel(c); RefreshCard(c); return; }
        string api = "https://api.github.com/repos/" + m.Groups[1].Value + "/" + m.Groups[2].Value + "/releases/latest";
        var wc = new WebClient();
        wc.Headers[HttpRequestHeader.UserAgent] = "msfs2024-toolbox";
        wc.DownloadStringCompleted += delegate(object s, DownloadStringCompletedEventArgs e)
        {
            if (e.Error != null) { c.LatestVer = "?"; }
            else
            {
                try
                {
                    Match tm = Regex.Match(e.Result, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                    c.LatestVer = tm.Success ? NormVer(tm.Groups[1].Value) : "?";
                }
                catch { c.LatestVer = "?"; }
            }
            Action upd = delegate { UpdateVerLabel(c); RefreshCard(c); };
            try
            {
                if (IsHandleCreated && InvokeRequired) BeginInvoke(upd);
                else upd();
            }
            catch { }
        };
        wc.DownloadStringAsync(new Uri(api));
    }

    static string LatestText(Card c)
    {
        if (c.LatestVer == null) return "检测中…";
        if (c.LatestVer == "?" || c.LatestVer.Length == 0) return "未知";
        return "v" + c.LatestVer;
    }

    // ---------- 机模/依赖状态（版本行第二行） ----------
    static string _a350Pkg;
    static bool _pkgSearched;

    // 照搬 A350座舱中文 的查找逻辑：UserCfg.opt -> Community
    static string FindA350Package()
    {
        if (_pkgSearched) return _a350Pkg;
        _pkgSearched = true;
        try
        {
            string la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string ad = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string[] opts = {
                la + @"\Packages\Microsoft.Limitless_8wekyb3d8bbwe\LocalCache\UserCfg.opt",
                ad + @"\Microsoft Flight Simulator 2024\UserCfg.opt",
                la + @"\Packages\Microsoft.FlightSimulator_8wekyb3d8bbwe\LocalCache\UserCfg.opt",
                ad + @"\Microsoft Flight Simulator\UserCfg.opt"
            };
            var roots = new List<string>();
            foreach (string o in opts)
            {
                if (!File.Exists(o)) continue;
                foreach (string line in File.ReadAllLines(o))
                {
                    int i = line.IndexOf("InstalledPackagesPath");
                    if (i < 0) continue;
                    int a = line.IndexOf('"', i), b = line.LastIndexOf('"');
                    if (a >= 0 && b > a) roots.Add(line.Substring(a + 1, b - a - 1));
                }
            }
            roots.Add(la + @"\Packages\Microsoft.Limitless_8wekyb3d8bbwe\LocalCache\Packages");
            roots.Add(ad + @"\Microsoft Flight Simulator 2024\Packages");
            foreach (string r in roots)
            {
                foreach (string sub in new string[] { "Community", "Community2024" })
                {
                    string p = Path.Combine(r, sub, "inibuilds-aircraft-a350");
                    if (Directory.Exists(p)) { _a350Pkg = p; return _a350Pkg; }
                }
            }
        }
        catch { }
        return _a350Pkg;
    }

    static string SimStatusLine(Card c)
    {
        try
        {
            if (c.Key == "efb")
            {
                string pkg = FindA350Package();
                if (string.IsNullOrEmpty(pkg)) return "机模内汉化：未找到 A350 安装";
                bool efbZh = File.Exists(Path.Combine(pkg, @"html_ui\Pages\VCockpit\Instruments\ini-efb-a350\ini-efb-zh.js"));
                bool locBak = File.Exists(Path.Combine(pkg, "en-US.locPak.a350zh.bak"));
                return "机模内汉化：" + ((efbZh || locBak) ? "已应用" : "未应用（启动应用器即可）");
            }
            if (c.Key == "amdb")
            {
                string bridge = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Programs\AMDB Bridge\AMDB Bridge.exe");
                return "AMDB 本体：" + (File.Exists(bridge) ? "已安装" : "未安装（中文壳需要它）");
            }
        }
        catch { }
        return "";
    }

    void UpdateVerLabel(Card c)
    {
        string local;
        if (c.LocalVer == null) local = "未安装";
        else if (c.LocalVer.Length == 0) local = "未知";
        else local = "v" + c.LocalVer;
        if (!string.IsNullOrEmpty(c.AdoptedPath)) local += "（本机已有）";
        string extra = SimStatusLine(c);
        c.LblVer.Text = "最新版本：" + LatestText(c) + "      本地版本：" + local
            + (extra.Length > 0 ? "\r\n" + extra : "");
    }

    // ---------- 路径约定 ----------
    static string DestDir(Card c) { return Path.Combine(DlDir, c.Key); }
    static string MainExePath(Card c) { return Path.Combine(DestDir(c), c.MainExe); }
    static string ZipPath(Card c) { return Path.Combine(DlDir, FileNameFromUrl(c.Url)); }
    static bool IsReady(Card c) { return File.Exists(MainExePath(c)) || !string.IsNullOrEmpty(c.AdoptedPath); }
    static string ExeToLaunch(Card c) { return string.IsNullOrEmpty(c.AdoptedPath) ? MainExePath(c) : c.AdoptedPath; }
    static string DirToLaunch(Card c) { string d = Path.GetDirectoryName(ExeToLaunch(c)); return string.IsNullOrEmpty(d) ? ExeDir : d; }

    void AddCard(Card c, int y)
    {
        var g = new GroupBox();
        g.Text = c.Title;
        g.Size = new Size(440, 118);
        g.Location = new Point(12, y);
        Controls.Add(g);

        var desc = new Label();
        desc.Text = c.Desc;
        desc.ForeColor = Color.DimGray;
        desc.Font = new Font("Microsoft YaHei UI", 8.25F);
        desc.Location = new Point(12, 18);
        desc.Size = new Size(270, 32);
        g.Controls.Add(desc);

        var ver = new Label();
        ver.Font = new Font("Microsoft YaHei UI", 8.25F);
        ver.ForeColor = Color.Black;
        ver.Location = new Point(12, 52);
        ver.AutoSize = true;
        g.Controls.Add(ver);
        c.LblVer = ver;

        var bar = new ProgressBar();
        bar.Location = new Point(12, 72);
        bar.Size = new Size(270, 13);
        bar.Visible = false;
        g.Controls.Add(bar);
        c.Bar = bar;

        var st = new Label();
        st.Font = new Font("Microsoft YaHei UI", 8.25F, FontStyle.Bold);
        st.Location = new Point(12, 92);
        st.AutoSize = true;
        g.Controls.Add(st);
        c.Status = st;

        var down = new Button();
        down.Size = new Size(132, 32);
        down.Location = new Point(292, 20);
        down.Click += delegate { BtnDownClick(c); };
        g.Controls.Add(down);
        c.BtnDown = down;

        var home = new Button();
        home.Size = new Size(132, 26);
        home.Location = new Point(292, 58);
        home.Text = "打开项目主页";
        home.Click += delegate { OpenUrl(c.Home); };
        g.Controls.Add(home);
        c.BtnHome = home;

        var re = new Button();
        re.Size = new Size(132, 22);
        re.Location = new Point(292, 90);
        re.Text = "重新下载";
        re.Font = new Font("Microsoft YaHei UI", 8F);
        re.Click += delegate { ReDownload(c); };
        g.Controls.Add(re);
        c.BtnRe = re;

        UpdateVerLabel(c);
        RefreshCard(c);
    }

    void RefreshCard(Card c)
    {
        bool ready = IsReady(c);
        if (string.IsNullOrEmpty(c.Url))
        {
            c.Status.Text = "○ 待发布 · 接口已预留（links.txt）";
            c.Status.ForeColor = Color.Firebrick;
            c.BtnDown.Text = "待发布";
            c.BtnDown.Enabled = false;
            c.BtnHome.Enabled = false;
            c.BtnRe.Enabled = false;
            UpdateVerLabel(c);
            return;
        }
        c.BtnHome.Enabled = !string.IsNullOrEmpty(c.Home);
        c.BtnRe.Enabled = true;
        UpdateVerLabel(c);

        if (ready)
        {
            bool haveBoth = !string.IsNullOrEmpty(c.LatestVer) && c.LatestVer != "?" && c.LatestVer != null
                            && !string.IsNullOrEmpty(c.LocalVer);
            if (haveBoth && CmpVer(c.LatestVer, c.LocalVer) > 0)
            {
                c.Status.Text = "↑ 有新版本 v" + c.LatestVer + "，点「更新」";
                c.Status.ForeColor = Color.FromArgb(200, 100, 0);
                c.BtnDown.Text = "更新";
                c.BtnDown.Enabled = true;
            }
            else if (!string.IsNullOrEmpty(c.AdoptedPath))
            {
                c.Status.Text = "✓ 检测到本机已有副本";
                c.Status.ForeColor = Color.FromArgb(0, 128, 0);
                c.BtnDown.Text = "启动 " + c.MainExe;
                c.BtnDown.Enabled = true;
            }
            else
            {
                c.Status.Text = "✓ 已是最新";
                c.Status.ForeColor = Color.FromArgb(0, 128, 0);
                c.BtnDown.Text = "启动 " + c.MainExe;
                c.BtnDown.Enabled = true;
            }
        }
        else
        {
            c.Status.Text = "● 已发布 · 点击下载";
            c.Status.ForeColor = Color.FromArgb(0, 100, 160);
            c.BtnDown.Text = "下载";
            c.BtnDown.Enabled = true;
        }
    }

    void BtnDownClick(Card c)
    {
        if (IsReady(c) &&
            !(c.LatestVer != null && c.LatestVer != "?" && !string.IsNullOrEmpty(c.LocalVer)
              && CmpVer(c.LatestVer, c.LocalVer) > 0))
        { Launch(c); return; }
        StartDownload(c);
    }

    void Launch(Card c)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo();
            psi.FileName = ExeToLaunch(c);
            psi.WorkingDirectory = DirToLaunch(c);
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show("启动失败：" + ex.Message, "微软2024工具箱",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    void StartDownload(Card c)
    {
        if (c.Client != null) return;
        if (string.IsNullOrEmpty(c.Url)) return;
        try
        {
            Directory.CreateDirectory(DlDir);
            string zip = ZipPath(c);
            var wc = new WebClient();
            wc.Headers[HttpRequestHeader.UserAgent] = "msfs2024-toolbox";
            wc.DownloadProgressChanged += delegate(object s, DownloadProgressChangedEventArgs e)
            {
                c.Bar.Value = Math.Min(100, e.ProgressPercentage);
                c.Status.Text = "↓ " + e.ProgressPercentage + "%  (" + (e.BytesReceived / 1024) + " KB)";
            };
            wc.DownloadFileCompleted += delegate(object s, System.ComponentModel.AsyncCompletedEventArgs e)
            {
                c.Client = null;
                c.Bar.Visible = false;
                if (e.Error != null)
                {
                    RefreshCard(c);
                    c.Status.Text = "✗ 下载失败";
                    c.Status.ForeColor = Color.Firebrick;
                    MessageBox.Show("下载失败：" + e.Error.Message, "微软2024工具箱",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (e.Cancelled) { RefreshCard(c); c.Status.Text = "已取消"; return; }
                ExtractCard(c, zip);
            };
            if (File.Exists(zip)) { try { File.Delete(zip); } catch { } }
            c.AdoptedPath = null;
            c.Bar.Style = ProgressBarStyle.Blocks;
            c.Bar.Value = 0;
            c.Bar.Visible = true;
            c.Status.ForeColor = Color.DarkSlateBlue;
            c.Status.Text = "连接中…";
            c.Client = wc;
            wc.DownloadFileAsync(new Uri(c.Url), zip);
        }
        catch (Exception ex)
        {
            c.Client = null;
            c.Bar.Visible = false;
            RefreshCard(c);
            MessageBox.Show("下载失败：" + ex.Message, "微软2024工具箱",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // 解压核心（不碰任何 UI，命令行模式也走这里）。ver = 写入 version.txt 的本地版本
    static string ExtractCore(Card c, string zip, string ver)
    {
        string dest = DestDir(c);
        if (Directory.Exists(dest))
        {
            try { Directory.Delete(dest, true); }
            catch { return "旧文件夹删不掉（多半是程序还开着）。请先关闭 " + c.MainExe + " 再试。"; }
        }
        Directory.CreateDirectory(dest);
        string destRoot = Path.GetFullPath(dest);
        if (!destRoot.EndsWith("\\")) destRoot += "\\";
        using (var za = ZipFile.OpenRead(zip))
        {
            foreach (var entry in za.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                string full = Path.GetFullPath(Path.Combine(dest, entry.FullName.Replace('/', '\\')));
                if (!full.StartsWith(destRoot, StringComparison.OrdinalIgnoreCase)) continue;   // 防(zip-slip)
                string dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                entry.ExtractToFile(full, true);
            }
        }
        SaveLocalVer(c, ver);
        return null;   // null = 成功
    }

    void ExtractCard(Card c, string zip)
    {
        c.Status.ForeColor = Color.DarkSlateBlue;
        c.Status.Text = "正在解压…";
        c.Bar.Style = ProgressBarStyle.Marquee;
        string err = null;
        try { err = ExtractCore(c, zip, ParseVerFromUrl(c.Url)); }
        catch (Exception ex) { err = ex.Message; }
        finally
        {
            c.Bar.Style = ProgressBarStyle.Blocks;
            c.Bar.Visible = false;
        }
        if (err != null)
        {
            RefreshCard(c);
            c.Status.Text = "✗ 解压失败";
            c.Status.ForeColor = Color.Firebrick;
            MessageBox.Show(err, "微软2024工具箱", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        c.AdoptedPath = null;   // 有了工具箱管理的副本，本机检测到的旧副本不再采用
        RefreshCard(c);
        if (!File.Exists(MainExePath(c)))
        {
            c.Status.Text = "✗ 解压完成，但没找到 " + c.MainExe;
            c.Status.ForeColor = Color.Firebrick;
        }
    }

    void ReDownload(Card c)
    {
        if (c.Client != null) return;
        try
        {
            string zip = ZipPath(c);
            string dest = DestDir(c);
            try
            {
                if (Directory.Exists(dest)) Directory.Delete(dest, true);
                if (File.Exists(zip)) File.Delete(zip);
            }
            catch
            {
                MessageBox.Show("旧文件删不掉（多半是程序还开着）。请先关闭 " + c.MainExe + " 再试。",
                    "微软2024工具箱", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            c.LocalVer = null;
            c.AdoptedPath = null;
            RefreshCard(c);
            StartDownload(c);
        }
        catch (Exception ex)
        {
            MessageBox.Show("操作失败：" + ex.Message, "微软2024工具箱",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ---------- 本机已有副本检测 ----------
    // 三层：① downloads 里已有 zip 但没解压 → 直接解压；② 常见位置扫描主程序 → 采用；
    //      （机模应用状态见 SimStatusLine）

    static string[] AdoptRoots()
    {
        var list = new List<string>();
        list.Add(ExeDir);
        list.Add(DlDir);
        string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string dl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (!string.IsNullOrEmpty(desk) && Directory.Exists(desk)) list.Add(desk);
        if (!string.IsNullOrEmpty(dl) && Directory.Exists(dl)) list.Add(dl);
        return list.ToArray();
    }

    static string SearchFile(string dir, string name, int depth, int maxDepth, string skipDir)
    {
        try
        {
            if (depth > maxDepth || string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
            string full = Path.GetFullPath(dir);
            if (string.Equals(full, skipDir, StringComparison.OrdinalIgnoreCase)) return null;
            foreach (string f in Directory.GetFiles(dir))
            {
                if (string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase)) return f;
            }
            if (depth == maxDepth) return null;
            foreach (string d in Directory.GetDirectories(dir))
            {
                string r = SearchFile(d, name, depth + 1, maxDepth, skipDir);
                if (r != null) return r;
            }
        }
        catch { }
        return null;
    }

    static string FindAdopted(Card c)
    {
        string skip = DestDir(c);
        foreach (string root in AdoptRoots())
        {
            string found = SearchFile(root, c.MainExe, 0, 3, skip);
            if (found != null) return found;
        }
        return null;
    }

    // 从副本周边猜测版本：旁边 version.txt → 同目录/上级的 zip 名 → 目录名
    static string GuessVerNear(string exePath)
    {
        try
        {
            string dir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrEmpty(dir)) return "";
            string vf = Path.Combine(dir, "version.txt");
            if (File.Exists(vf))
            {
                string t = File.ReadAllText(vf, Encoding.UTF8).Trim();
                if (t.Length > 0) return t;
            }
            string[] looks = { dir, Path.GetDirectoryName(dir) };
            foreach (string d in looks)
            {
                if (string.IsNullOrEmpty(d) || !Directory.Exists(d)) continue;
                foreach (string z in Directory.GetFiles(d, "*.zip"))
                {
                    string v = ParseVerFromName(Path.GetFileName(z));
                    if (v.Length > 0) return v;
                }
                string dv = ParseVerFromName(new DirectoryInfo(d).Name);
                if (dv.Length > 0) return dv;
            }
        }
        catch { }
        return "";
    }

    static void AdoptPass(Card c)
    {
        if (IsReady(c)) return;
        // ① 已下载但没解压的 zip：直接解压（版本以压缩包文件名为准）
        if (!string.IsNullOrEmpty(c.Url))
        {
            string zip = ZipPath(c);
            if (File.Exists(zip))
            {
                string err = ExtractCore(c, zip, ParseVerFromName(Path.GetFileName(zip)));
                if (err == null && File.Exists(MainExePath(c))) return;
            }
        }
        // ② 常见位置扫描
        string found = FindAdopted(c);
        if (found != null)
        {
            c.AdoptedPath = found;
            c.LocalVer = GuessVerNear(found);
        }
    }

    public void StartAdoptionChecks()
    {
        foreach (var c in cards)
        {
            if (IsReady(c)) { RefreshCard(c); continue; }
            var cc = c;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try { AdoptPass(cc); } catch { }
                Action upd = delegate { UpdateVerLabel(cc); RefreshCard(cc); };
                try
                {
                    if (IsHandleCreated && InvokeRequired) BeginInvoke(upd);
                    else upd();
                }
                catch { }
            });
        }
    }

    static string FileNameFromUrl(string url)
    {
        try
        {
            string p = url.Split('?')[0];
            int i = p.LastIndexOf('/');
            string name = i >= 0 ? p.Substring(i + 1) : p;
            name = Uri.UnescapeDataString(name);
            if (name.Length == 0) name = "download.zip";
            return name;
        }
        catch { return "download.zip"; }
    }

    void OpenUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return;
        try { System.Diagnostics.Process.Start(url); }
        catch (Exception ex) { MessageBox.Show("打开失败：" + ex.Message); }
    }

    void OpenDlDir()
    {
        try
        {
            Directory.CreateDirectory(DlDir);
            System.Diagnostics.Process.Start("explorer.exe", "\"" + DlDir + "\"");
        }
        catch (Exception ex) { MessageBox.Show("打开失败：" + ex.Message); }
    }

    // ---------- 命令行自检（不构造任何窗体） ----------
    static List<Card> CmdCards()
    {
        var list = BuildCardList();
        LoadLinksInto(list);
        return list;
    }

    static void LogCrash(Exception ex)
    {
        try
        {
            File.AppendAllText(Path.Combine(ExeDir, "toolbox-crash.log"),
                DateTime.Now + " " + (ex == null ? "??" : ex.ToString()) + "\r\n");
        }
        catch { }
    }

    static int SelfTest()
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        int bad = 0;
        foreach (var c in CmdCards())
        {
            if (string.IsNullOrEmpty(c.Url))
            {
                Console.WriteLine("[待发布] " + c.Key + "  " + c.Title);
                Console.WriteLine("      " + SimStatusLine(c));
                continue;
            }
            int code = HeadCode(c.Url);
            string latest = FetchLatestSync(c);
            AdoptPass(c);   // 顺带跑本机检测：解压已有 zip / 扫描已有副本
            string local = c.LocalVer == null ? "未安装" : (c.LocalVer.Length == 0 ? "未知" : "v" + c.LocalVer);
            string where = !string.IsNullOrEmpty(c.AdoptedPath) ? "  [本机已有: " + c.AdoptedPath + "]"
                         : (IsReady(c) ? "  [工具箱副本]" : "");
            Console.WriteLine("[" + (code == 200 ? "OK " : "BAD") + "] " + c.Key + "  HTTP " + code
                + "  最新=" + (latest.Length == 0 ? "?" : latest) + "  本地=" + local + where);
            Console.WriteLine("      " + c.Url);
            Console.WriteLine("      " + SimStatusLine(c));
            if (code != 200) bad++;
        }
        return bad;
    }

    static string FetchLatestSync(Card c)
    {
        try
        {
            Match m = Regex.Match(c.Url, @"^https://github\.com/([^/]+)/([^/]+)/");
            if (!m.Success) return "?";
            string api = "https://api.github.com/repos/" + m.Groups[1].Value + "/" + m.Groups[2].Value + "/releases/latest";
            var wc = new WebClient();
            wc.Headers[HttpRequestHeader.UserAgent] = "msfs2024-toolbox";
            string json = wc.DownloadString(api);
            Match tm = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
            return tm.Success ? NormVer(tm.Groups[1].Value) : "?";
        }
        catch { return "?"; }
    }

    // --get {key}：无窗完整走一遍 下载→解压→定位主程序
    static int GetTest(string key)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        try
        {
            Card hit = null;
            foreach (var c in CmdCards()) { if (c.Key == key) hit = c; }
            if (hit == null) { Console.WriteLine("unknown key: " + key); return 2; }
            if (string.IsNullOrEmpty(hit.Url)) { Console.WriteLine("[待发布] " + key); return 2; }
            Console.WriteLine("下载: " + hit.Url);
            Directory.CreateDirectory(DlDir);
            string zip = ZipPath(hit);
            var wc = new WebClient();
            wc.Headers[HttpRequestHeader.UserAgent] = "msfs2024-toolbox";
            byte[] data = wc.DownloadData(hit.Url);
            File.WriteAllBytes(zip, data);
            Console.WriteLine("  zip: " + data.Length + " bytes -> " + zip);
            string err = ExtractCore(hit, zip, ParseVerFromName(Path.GetFileName(zip)));
            if (err != null) { Console.WriteLine("  解压失败: " + err); return 1; }
            string exe = MainExePath(hit);
            Console.WriteLine("  主程序存在: " + File.Exists(exe) + "  " + exe);
            Console.WriteLine("  本地版本记录: " + (ReadLocalVer(hit) ?? "null") + "  (直链解析: " + ParseVerFromUrl(hit.Url) + ")");
            return File.Exists(exe) ? 0 : 1;
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            try { Console.WriteLine("  异常: " + ex.GetType().Name + " " + ex.Message); } catch { }
            return 1;
        }
    }

    static int HeadCode(string url)
    {
        try
        {
            // 用 Range-GET 代替 HEAD：GitHub 的 302 重定向链对 HEAD 偶发重置，GET 稳定得多
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.UserAgent = "msfs2024-toolbox";
            req.AllowAutoRedirect = true;
            req.Timeout = 20000;
            req.AddRange(0, 0);
            var resp = (HttpWebResponse)req.GetResponse();
            int code = (int)resp.StatusCode;
            resp.Close();
            return (code == 206 || code == 200) ? 200 : code;
        }
        catch (WebException we)
        {
            var r = we.Response as HttpWebResponse;
            return r == null ? -1 : (int)r.StatusCode;
        }
        catch { return -1; }
    }

    [STAThread]
    static int Main(string[] args)
    {
        try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
        if (args != null && Array.IndexOf(args, "--selftest") >= 0) return SelfTest();
        if (args != null && args.Length >= 2 && args[0] == "--get") return GetTest(args[1]);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var form = new A350Toolbox();
        form.Shown += delegate
        {
            form.StartVersionChecks();     // GitHub 最新版本
            form.StartAdoptionChecks();    // 本机已有副本 / 已下载未解压
        };
        Application.Run(form);
        return 0;
    }
}
