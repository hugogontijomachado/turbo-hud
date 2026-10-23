// TurboHUD: overlay leve de desempenho (CPU, GPU, RAM, VRAM, rede, ping, turbo).
// Abrir o .exe de novo (ou Ctrl+Alt+M) mostra/esconde o overlay; escondido, ele só escuta os atalhos.
// Argumentos: --oculto (inicia escondido), --sair (fecha a instância aberta).
// Ctrl+Alt+N move o overlay para o próximo monitor (a escolha fica salva em tela.txt).
// Ctrl+Alt+G liga/desliga os minigráficos dos últimos 60 s (a escolha fica salva em graficos.txt).
// Ctrl+Alt+T alterna o modo do Avell Custom Control (Escritório ↔ Turbo) e, junto, o plano de energia
// (Equilibrado ↔ "Jogos"). Sem o serviço da Avell, troca só o plano.
// FPS e latência vêm do PresentMon.exe (Intel) na mesma pasta do .exe.
// Argumento opcional: host para o ping (padrão 1.1.1.1).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("TurboHUD")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

namespace TurboHUD
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool quit = Array.IndexOf(args, "--sair") >= 0, startHidden = Array.IndexOf(args, "--oculto") >= 0;
            bool createdNew;
            EventWaitHandle toggle = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\TurboHUD_Toggle", out createdNew);
            EventWaitHandle quitEvt = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\TurboHUD_Quit");
            if (!createdNew)
            {
                // Já está rodando: abrir de novo mostra/esconde; --sair fecha de vez.
                if (quit) quitEvt.Set(); else toggle.Set();
                return;
            }
            if (quit) return;

            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { Native.SetProcessDPIAware(); }
            string host = "1.1.1.1";
            foreach (string a in args) if (!a.StartsWith("--")) host = a;

            using (Overlay w = new Overlay(host, startHidden))
            {
                ThreadPool.RegisterWaitForSingleObject(toggle, delegate
                {
                    try { w.BeginInvoke((MethodInvoker)delegate { w.ToggleVisible(); }); } catch { }
                }, null, Timeout.Infinite, false);
                ThreadPool.RegisterWaitForSingleObject(quitEvt, delegate
                {
                    try { w.BeginInvoke((MethodInvoker)delegate { w.Close(); }); } catch { }
                }, null, Timeout.Infinite, true);
                Application.Run(w);
            }
            GC.KeepAlive(toggle);
            GC.KeepAlive(quitEvt);
        }
    }

    sealed class Overlay : Form
    {
        static readonly Color Bg = Color.FromArgb(200, 13, 15, 19);
        static readonly Color Border = Color.FromArgb(38, 255, 255, 255);
        static readonly Color Track = Color.FromArgb(30, 255, 255, 255);
        static readonly Color LabelC = Color.FromArgb(255, 139, 147, 161);
        static readonly Color ValueC = Color.FromArgb(255, 232, 234, 237);
        static readonly Color Green = Color.FromArgb(255, 63, 185, 80);
        static readonly Color Amber = Color.FromArgb(255, 222, 165, 50);
        static readonly Color Red = Color.FromArgb(255, 248, 81, 73);
        static readonly Color Blue = Color.FromArgb(255, 88, 166, 255);

        const int HotkeyNextScreen = 1, HotkeyGraphs = 2, HotkeyTurbo = 3, HotkeyShowHide = 4;
        static readonly string ScreenFile = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"TurboHUD\tela.txt");
        static readonly string GraphsFile = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"TurboHUD\graficos.txt");

        // Últimos 60 s de cada métrica; gravados sempre, desenhados só com os gráficos ligados.
        readonly History hCpu = new History(60), hGpu = new History(60), hRam = new History(60), hVram = new History(60),
                         hFps = new History(60), hNet = new History(60), hPing = new History(60);
        bool graphs;

        // O widget deixa o clique passar, então o "?" é detectado consultando a posição do mouse.
        readonly System.Windows.Forms.Timer hoverTimer = new System.Windows.Forms.Timer();
        RectangleF helpIcon;
        Point lastPos;
        bool help;

        readonly Sensors d;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        int tick;
        float s = 0f;
        Bitmap frame;
        Font fLabel, fValue, fSmall;
        StringFormat sfRight;
        string screenName;

        bool startHidden, hidden;

        public Overlay(string pingHost, bool startHidden)
        {
            this.startHidden = startHidden;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "TurboHUD";
            d = new Sensors(pingHost);
            timer.Interval = 1000;
            timer.Tick += OnTick;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // LAYERED | TRANSPARENT (clique atravessa) | TOOLWINDOW (fora do Alt+Tab) | NOACTIVATE | TOPMOST
                cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x8000000 | 0x8;
                return cp;
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            sfRight = new StringFormat();
            sfRight.Alignment = StringAlignment.Far;
            try { screenName = System.IO.File.ReadAllText(ScreenFile).Trim(); } catch { screenName = null; }
            try { graphs = System.IO.File.ReadAllText(GraphsFile).Trim() == "1"; } catch { graphs = false; }
            // Ctrl+Alt+N: move o widget para o próximo monitor; Ctrl+Alt+G: liga/desliga os gráficos
            Native.RegisterHotKey(Handle, HotkeyNextScreen, 0x0001 | 0x0002 | 0x4000, (uint)Keys.N);
            Native.RegisterHotKey(Handle, HotkeyGraphs, 0x0001 | 0x0002 | 0x4000, (uint)Keys.G);
            Native.RegisterHotKey(Handle, HotkeyTurbo, 0x0001 | 0x0002 | 0x4000, (uint)Keys.T);
            Native.RegisterHotKey(Handle, HotkeyShowHide, 0x0001 | 0x0002 | 0x4000, (uint)Keys.M);
            d.Update(true);
            Record();
            Render();
            timer.Start();
            hoverTimer.Interval = 150;
            hoverTimer.Tick += OnHover;
            hoverTimer.Start();
        }

        void OnHover(object sender, EventArgs e)
        {
            Native.POINT p;
            if (!Native.GetCursorPos(out p)) return;
            bool over = helpIcon.Contains(p.X - lastPos.X, p.Y - lastPos.Y);
            if (over != help) { help = over; Render(); }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Native.UnregisterHotKey(Handle, HotkeyNextScreen);
            Native.UnregisterHotKey(Handle, HotkeyGraphs);
            Native.UnregisterHotKey(Handle, HotkeyTurbo);
            Native.UnregisterHotKey(Handle, HotkeyShowHide);
            hoverTimer.Stop();
            timer.Stop();
            d.Dispose();
            base.OnFormClosed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312 && m.WParam.ToInt32() == HotkeyNextScreen) NextScreen();
            if (m.Msg == 0x0312 && m.WParam.ToInt32() == HotkeyGraphs) ToggleGraphs();
            if (m.Msg == 0x0312 && m.WParam.ToInt32() == HotkeyTurbo) { d.ToggleTurbo(); if (!hidden) Render(); }
            if (m.Msg == 0x0312 && m.WParam.ToInt32() == HotkeyShowHide) ToggleVisible();
            base.WndProc(ref m);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (startHidden) ToggleVisible();
        }

        // Escondido, o widget só fica escutando os atalhos: medições e PresentMon param.
        public void ToggleVisible()
        {
            hidden = !hidden;
            if (hidden)
            {
                timer.Stop();
                hoverTimer.Stop();
                d.Frames.Stop();
                help = false;
                Hide();
            }
            else
            {
                Show();
                d.Avell.Refresh(); // o modo pode ter mudado pelo app ou pelo botão da Avell
                d.Update(true);
                Record();
                Render();
                timer.Start();
                hoverTimer.Start();
            }
        }

        void ToggleGraphs()
        {
            graphs = !graphs;
            try { System.IO.File.WriteAllText(GraphsFile, graphs ? "1" : "0"); } catch { }
            Render();
        }

        void Record()
        {
            hCpu.Add(d.Cpu);
            hGpu.Add(d.Gpu);
            hRam.Add(d.RamTotal > 0 ? d.RamUsed * 100 / d.RamTotal : 0);
            hVram.Add(d.VramTotal > 0 ? d.VramUsed * 100 / d.VramTotal : 0);
            hFps.Add(d.Frames.Status.Length > 0 ? 0 : d.Frames.Fps);
            hNet.Add(d.NetRx);
            hPing.Add(d.Samples == 0 ? 0 : d.PingMs); // -1 = pacote perdido
        }

        void NextScreen()
        {
            Screen[] all = Screen.AllScreens;
            if (all.Length < 2) return;
            Screen cur = TargetScreen();
            int i = Array.FindIndex(all, delegate(Screen x) { return x.DeviceName == cur.DeviceName; });
            screenName = all[(i + 1) % all.Length].DeviceName;
            try { System.IO.File.WriteAllText(ScreenFile, screenName); } catch { }
            Render();
        }

        // Monitor escolhido (lembrado entre execuções); se ele não estiver conectado, usa o principal.
        Screen TargetScreen()
        {
            if (screenName != null)
                foreach (Screen x in Screen.AllScreens)
                    if (x.DeviceName == screenName) return x;
            return Screen.PrimaryScreen;
        }

        // Ajusta o tamanho ao DPI do monitor onde o widget está.
        void UpdateScale(Screen sc)
        {
            float ns = 1f;
            try
            {
                Rectangle b = sc.Bounds;
                IntPtr mon = Native.MonitorFromPoint(new Native.POINT(b.Left + b.Width / 2, b.Top + b.Height / 2), 2);
                uint dx, dy;
                if (Native.GetDpiForMonitor(mon, 0, out dx, out dy) == 0) ns = dx / 96f;
            }
            catch { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) ns = g.DpiX / 96f; }
            if (ns == s && fLabel != null) return;
            s = ns;
            if (fLabel != null) { fLabel.Dispose(); fValue.Dispose(); fSmall.Dispose(); }
            fLabel = new Font("Segoe UI", 11.5f * s, FontStyle.Regular, GraphicsUnit.Pixel);
            fValue = new Font("Segoe UI Semibold", 13f * s, FontStyle.Regular, GraphicsUnit.Pixel);
            fSmall = new Font("Segoe UI", 11f * s, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        void OnTick(object sender, EventArgs e)
        {
            tick++;
            bool slow = tick % 5 == 0;
            d.Update(slow);
            Record();
            Render();
            if (slow) Native.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        }

        void Render()
        {
            if (hidden) return;
            Screen sc = TargetScreen();
            UpdateScale(sc);
            int pad = (int)(12 * s);
            int w = (int)(236 * s);
            int barRow = (int)((graphs ? 48 : 28) * s), textRow = (int)(23 * s), footer = (int)(24 * s);
            int sparkRow = graphs ? (int)(24 * s) : 0;
            int h = pad * 2 + barRow * 4 + (textRow + sparkRow) * 3 + footer;
            if (frame == null || frame.Width != w || frame.Height != h)
            {
                if (frame != null) frame.Dispose();
                frame = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            }

            using (Graphics g = Graphics.FromImage(frame))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                using (GraphicsPath p = Rounded(new RectangleF(0.5f, 0.5f, w - 1, h - 1), 10 * s))
                using (SolidBrush b = new SolidBrush(Bg))
                using (Pen pen = new Pen(Border, 1f))
                {
                    g.FillPath(b, p);
                    g.DrawPath(pen, p);
                }

                float x = pad, y = pad, inner = w - pad * 2;

                FrameStats fs = d.Frames;
                if (fs.Status.Length > 0)
                    TextRow(g, ref y, x, inner, textRow, "FPS", "—", LabelC, fs.Status, Amber);
                else if (fs.Fps < 0.5)
                    TextRow(g, ref y, x, inner, textRow, "FPS", "—", LabelC, "nenhum jogo rodando", LabelC);
                else
                {
                    Color fpsC = fs.Fps >= 60 ? Green : (fs.Fps >= 30 ? Amber : Red);
                    string name = fs.GameName.Length > 10 ? fs.GameName.Substring(0, 10) : fs.GameName;
                    string lat = name + (fs.LatencyMs > 0 ? " · lat " + fs.LatencyMs.ToString("0") + " ms" : "");
                    Color latC = fs.LatencyMs < 30 ? LabelC : (fs.LatencyMs < 60 ? Amber : Red);
                    TextRow(g, ref y, x, inner, textRow, "FPS", fs.Fps.ToString("0"), fpsC, lat, latC);
                }
                if (graphs)
                {
                    double fpsMax = Math.Max(60, hFps.Max() * 1.1);
                    Spark(g, x, y - 2 * s, inner, 18 * s, hFps, fpsMax, fs.Fps >= 60 || fs.Fps < 0.5 ? Green : (fs.Fps >= 30 ? Amber : Red));
                    y += sparkRow;
                }

                BarRow(g, ref y, x, inner, barRow, "CPU", Pct(d.Cpu),
                    d.CpuGhz > 0 ? d.CpuGhz.ToString("0.0") + " GHz" : "", LabelC, d.Cpu, hCpu);

                string gpuSub = d.GpuTemp > 0 ? d.GpuTemp + "°C" : (d.Gpu < 1 ? "ocioso" : "");
                Color gpuSubC = d.GpuTemp >= 87 ? Red : (d.GpuTemp >= 78 ? Amber : LabelC);
                BarRow(g, ref y, x, inner, barRow, "GPU", d.GpuOk ? Pct(d.Gpu) : "n/d", gpuSub, gpuSubC, d.Gpu, hGpu);

                double ramPct = d.RamTotal > 0 ? d.RamUsed * 100 / d.RamTotal : 0;
                BarRow(g, ref y, x, inner, barRow, "RAM", Pct(ramPct), Gb(d.RamUsed) + " / " + Gb(d.RamTotal) + " GB", LabelC, ramPct, hRam);

                double vramPct = d.VramTotal > 0 ? d.VramUsed * 100 / d.VramTotal : 0;
                BarRow(g, ref y, x, inner, barRow, "VRAM", d.GpuOk ? Pct(vramPct) : "n/d",
                    d.VramTotal > 0 ? Gb(d.VramUsed) + " / " + Gb(d.VramTotal) + " GB" : "", LabelC, vramPct, hVram);

                TextRow(g, ref y, x, inner, textRow, "REDE", "↓ " + Rate(d.NetRx), ValueC, "↑ " + Rate(d.NetTx), LabelC);
                if (graphs)
                {
                    Spark(g, x, y - 2 * s, inner, 18 * s, hNet, Math.Max(102400, hNet.Max() * 1.1), Blue);
                    y += sparkRow;
                }

                string ping; Color pingC;
                if (d.PingMs < 0) { ping = d.Samples == 0 ? "…" : "perdido"; pingC = d.Samples == 0 ? LabelC : Red; }
                else { ping = d.PingMs + " ms"; pingC = d.PingMs < 60 ? Green : (d.PingMs < 100 ? Amber : Red); }
                Color lossC = d.Loss <= 0 ? LabelC : (d.Loss < 2 ? Amber : Red);
                TextRow(g, ref y, x, inner, textRow, "PING", ping, pingC,
                    "±" + d.Jitter.ToString("0.0") + " · perda " + d.Loss.ToString("0.#") + "%", lossC);
                if (graphs)
                {
                    Spark(g, x, y - 2 * s, inner, 18 * s, hPing, Math.Max(100, hPing.Max() * 1.2), d.PingMs < 0 ? LabelC : pingC);
                    y += sparkRow;
                }

                using (Pen sep = new Pen(Track, 1f)) g.DrawLine(sep, x, y + 3 * s, x + inner, y + 3 * s);
                float fy = y + 7 * s;
                // Modo do Avell Custom Control; sem o serviço da Avell, o turbo do plano do Windows.
                string mode; bool hot;
                switch (d.Avell.Mode)
                {
                    case AvellControl.Office: mode = "ESCRITÓRIO"; hot = false; break;
                    case AvellControl.Gaming: mode = "GAMING"; hot = true; break;
                    case AvellControl.Turbo: mode = "TURBO"; hot = true; break;
                    case 3: mode = "PERSONALIZADO"; hot = true; break;
                    default: mode = d.Turbo ? "TURBO ON" : "TURBO OFF"; hot = d.Turbo; break;
                }
                using (SolidBrush b = new SolidBrush(hot ? Amber : Green))
                    g.DrawString(mode, fSmall, b, x, fy);
                string power = d.OnAc ? "Tomada" : (d.Battery >= 0 ? "Bateria " + d.Battery + "%" : "Bateria");
                string right = (d.PlanName.Length > 0 ? d.PlanName + " · " : "") + power;
                float iconR = 7 * s, gap = 6 * s;
                using (SolidBrush b = new SolidBrush(d.OnAc ? LabelC : Amber))
                    g.DrawString(right, fSmall, b, new RectangleF(x, fy, inner - 2 * iconR - gap, 18 * s), sfRight);

                // "?" no canto: passar o mouse mostra os atalhos
                float icx = x + inner - iconR, icy = fy + 8 * s;
                helpIcon = new RectangleF(icx - iconR - 5 * s, icy - iconR - 5 * s, 2 * iconR + 10 * s, 2 * iconR + 10 * s);
                Color ic = help ? Blue : LabelC;
                using (Pen ip = new Pen(ic, 1.2f * s)) g.DrawEllipse(ip, icx - iconR, icy - iconR, 2 * iconR, 2 * iconR);
                using (SolidBrush ib = new SolidBrush(ic))
                using (StringFormat sc2 = new StringFormat())
                {
                    sc2.Alignment = StringAlignment.Center;
                    sc2.LineAlignment = StringAlignment.Center;
                    g.DrawString("?", fSmall, ib, new RectangleF(icx - iconR, icy - iconR + 0.5f * s, 2 * iconR, 2 * iconR), sc2);
                }

                if (help) HelpCard(g, x, pad, inner, y - pad);
            }
            Present(w, h, sc);
        }

        void BarRow(Graphics g, ref float y, float x, float inner, int rowH, string label, string value, string sub, Color subC, double pct, History hist)
        {
            using (SolidBrush lb = new SolidBrush(LabelC)) g.DrawString(label, fLabel, lb, x, y + 1 * s);
            using (SolidBrush vb = new SolidBrush(ValueC)) g.DrawString(value, fValue, vb, x + 46 * s, y);
            using (SolidBrush sb = new SolidBrush(subC)) g.DrawString(sub, fSmall, sb, new RectangleF(x, y + 1.5f * s, inner, 18 * s), sfRight);

            double p = Math.Max(0, Math.Min(100, pct));
            float by = y + 20 * s;
            if (graphs)
                Spark(g, x, by, inner, 22 * s, hist, 100, Level(p));
            else
            {
                float bh = 3 * s;
                using (SolidBrush tb = new SolidBrush(Track)) g.FillRectangle(tb, x, by, inner, bh);
                if (p > 0.5)
                    using (SolidBrush fb = new SolidBrush(Level(p))) g.FillRectangle(fb, x, by, (float)(inner * p / 100), bh);
            }
            y += rowH;
        }

        // Cartão com os atalhos, desenhado por cima das métricas enquanto o mouse está no "?".
        void HelpCard(Graphics g, float x, float y0, float inner, float height)
        {
            float cardH = Math.Min(height + 4 * s, 144 * s);
            using (GraphicsPath p = Rounded(new RectangleF(x - 4 * s, y0 - 4 * s, inner + 8 * s, cardH), 8 * s))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 20, 23, 29)))
            using (Pen pen = new Pen(Border, 1f))
            {
                g.FillPath(b, p);
                g.DrawPath(pen, p);
            }
            float ty = y0 + 6 * s;
            using (SolidBrush vb = new SolidBrush(ValueC)) g.DrawString("Atalhos", fValue, vb, x + 6 * s, ty);
            ty += 30 * s;
            string[,] rows = {
                { "Ctrl+Alt+M", "mostra / esconde o widget" },
                { "Ctrl+Alt+N", "troca de monitor" },
                { "Ctrl+Alt+G", "liga / desliga gráficos" },
                { "Ctrl+Alt+T", "Escritório ↔ Turbo" }
            };
            using (SolidBrush kb = new SolidBrush(Blue))
            using (SolidBrush lb = new SolidBrush(LabelC))
                for (int i = 0; i < rows.GetLength(0); i++)
                {
                    g.DrawString(rows[i, 0], fLabel, kb, x + 6 * s, ty);
                    g.DrawString(rows[i, 1], fSmall, lb, x + 88 * s, ty + 0.5f * s);
                    ty += 24 * s;
                }
        }

        // Minigráfico dos últimos 60 s: o mais recente à direita; valores negativos = falha (risco vermelho).
        void Spark(Graphics g, float x, float y, float w, float h, History hist, double max, Color c)
        {
            using (SolidBrush tb = new SolidBrush(Color.FromArgb(14, 255, 255, 255))) g.FillRectangle(tb, x, y, w, h);
            int n = hist.Count;
            if (n < 2 || max <= 0) return;
            float step = w / (hist.Capacity - 1);
            PointF[] line = new PointF[n];
            for (int i = 0; i < n; i++)
            {
                double v = Math.Max(0, Math.Min(max, hist[i]));
                line[i] = new PointF(x + w - (n - 1 - i) * step, y + h - (float)(v / max) * h);
            }
            PointF[] area = new PointF[n + 2];
            Array.Copy(line, area, n);
            area[n] = new PointF(line[n - 1].X, y + h);
            area[n + 1] = new PointF(line[0].X, y + h);
            using (SolidBrush fb = new SolidBrush(Color.FromArgb(50, c))) g.FillPolygon(fb, area);
            using (Pen pen = new Pen(c, 1.25f * s)) g.DrawLines(pen, line);
            using (Pen lost = new Pen(Color.FromArgb(220, Red), 1.5f * s))
                for (int i = 0; i < n; i++)
                    if (hist[i] < 0) g.DrawLine(lost, line[i].X, y, line[i].X, y + h);
        }

        void TextRow(Graphics g, ref float y, float x, float inner, int rowH, string label, string value, Color valueC, string sub, Color subC)
        {
            using (SolidBrush lb = new SolidBrush(LabelC)) g.DrawString(label, fLabel, lb, x, y + 1 * s);
            using (SolidBrush vb = new SolidBrush(valueC)) g.DrawString(value, fValue, vb, x + 46 * s, y);
            using (SolidBrush sb = new SolidBrush(subC)) g.DrawString(sub, fSmall, sb, new RectangleF(x, y + 1.5f * s, inner, 18 * s), sfRight);
            y += rowH;
        }

        void Present(int w, int h, Screen sc)
        {
            Rectangle wa = sc.WorkingArea;
            Native.POINT dst = new Native.POINT(wa.Right - w - (int)(10 * s), wa.Top + (wa.Height - h) / 2);
            lastPos = new Point(dst.X, dst.Y);
            Native.POINT src = new Native.POINT(0, 0);
            Native.SIZE size = new Native.SIZE(w, h);
            Native.BLENDFUNCTION blend = new Native.BLENDFUNCTION();
            blend.SourceConstantAlpha = 255;
            blend.AlphaFormat = 1; // AC_SRC_ALPHA

            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr memDc = Native.CreateCompatibleDC(screenDc);
            IntPtr hBmp = frame.GetHbitmap(Color.FromArgb(0));
            IntPtr old = Native.SelectObject(memDc, hBmp);
            Native.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2);
            Native.SelectObject(memDc, old);
            Native.DeleteObject(hBmp);
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }

        static Color Level(double p) { return p < 60 ? Green : (p < 85 ? Amber : Red); }
        static string Pct(double v) { return ((int)Math.Round(Math.Max(0, Math.Min(100, v)))) + "%"; }
        static string Gb(double bytes) { return (bytes / 1073741824.0).ToString("0.0"); }
        static string Rate(double b)
        {
            if (b >= 1048576) return (b / 1048576).ToString("0.0") + " MB/s";
            return (b / 1024).ToString("0") + " KB/s";
        }

        static GraphicsPath Rounded(RectangleF r, float rad)
        {
            float dia = rad * 2;
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, dia, dia, 180, 90);
            p.AddArc(r.Right - dia, r.Y, dia, dia, 270, 90);
            p.AddArc(r.Right - dia, r.Bottom - dia, dia, dia, 0, 90);
            p.AddArc(r.X, r.Bottom - dia, dia, dia, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // Buffer circular de amostras (uma por segundo).
    sealed class History
    {
        readonly double[] v;
        int pos, count;

        public History(int capacity) { v = new double[capacity]; }
        public int Count { get { return count; } }
        public int Capacity { get { return v.Length; } }
        public double this[int i] { get { return v[(pos - count + i + v.Length) % v.Length]; } } // 0 = mais antiga

        public void Add(double x)
        {
            v[pos] = x;
            pos = (pos + 1) % v.Length;
            if (count < v.Length) count++;
        }

        public double Max()
        {
            double m = 0;
            for (int i = 0; i < count; i++) if (this[i] > m) m = this[i];
            return m;
        }
    }

    sealed class Sensors : IDisposable
    {
        public double Cpu, CpuGhz, Gpu, RamUsed, RamTotal, VramUsed, VramTotal, NetRx, NetTx, Jitter, Loss;
        public int GpuTemp, PingMs = -1, Samples, Battery = -1;
        public bool GpuOk, OnAc = true, Turbo;
        public string PlanName = "";

        const uint FMT = 0x200 | 0x8000; // PDH_FMT_DOUBLE | PDH_FMT_NOCAP100
        const int PDH_MORE_DATA = unchecked((int)0x800007D2);

        IntPtr query, cCpu, cPerf, cFreq, cRx, cTx, cGpu, cVram;
        IntPtr buf = IntPtr.Zero; int bufSize;
        readonly string nvLuid;
        readonly double baseMhz;

        readonly Ping ping = new Ping();
        readonly string host;
        readonly byte[] payload = new byte[32];
        bool pingBusy;
        readonly int[] ring = new int[60]; // ms; -1 = pacote perdido
        int ringPos, ringCount;

        Nvml nvml;
        DateTime gpuLastActive = DateTime.MinValue, nvmlRetryAt = DateTime.MinValue;

        public readonly FrameStats Frames = new FrameStats(Path.Combine(Application.StartupPath, "PresentMon.exe"));
        public readonly AvellControl Avell = new AvellControl();

        static readonly Guid SubProcessor = new Guid("54533251-82be-4824-96c1-47b60b740d00");
        static readonly Guid ProcThrottleMax = new Guid("bc5038f7-23e0-4960-96da-33abaf5935ec");
        static readonly Guid PerfBoostMode = new Guid("be337238-0d82-4146-a960-4f3749d470c7");
        static readonly Guid Balanced = new Guid("381b4222-f694-41f0-9685-ff5bb260df2e");

        public Sensors(string pingHost)
        {
            host = pingHost;
            ping.PingCompleted += OnPing;

            object mhz = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "~MHz", 0);
            baseMhz = mhz is int ? (int)mhz : 0;

            long vram;
            nvLuid = FindGpu(out vram);
            VramTotal = vram;

            if (Native.PdhOpenQuery(null, IntPtr.Zero, out query) != 0) return;
            cCpu = Add(@"\Processor Information(_Total)\% Processor Utility");
            if (cCpu == IntPtr.Zero) cCpu = Add(@"\Processor(_Total)\% Processor Time");
            cPerf = Add(@"\Processor Information(_Total)\% Processor Performance");
            cFreq = Add(@"\Processor Information(_Total)\Processor Frequency");
            cRx = Add(@"\Network Interface(*)\Bytes Received/sec");
            cTx = Add(@"\Network Interface(*)\Bytes Sent/sec");
            cGpu = Add(@"\GPU Engine(*engtype_3D)\Utilization Percentage");
            if (cGpu == IntPtr.Zero) cGpu = Add(@"\GPU Engine(*)\Utilization Percentage");
            cVram = Add(@"\GPU Adapter Memory(*)\Dedicated Usage");
            GpuOk = nvLuid != null && cGpu != IntPtr.Zero;
            Native.PdhCollectQueryData(query);
        }

        // Acha a GPU dedicada (NVIDIA de preferência) pelo cache do DirectX: LUID para filtrar os contadores e VRAM total.
        static string FindGpu(out long vram)
        {
            vram = 0;
            string best = null; long bestScore = -1;
            try
            {
                using (RegistryKey dx = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\DirectX"))
                {
                    if (dx == null) return null;
                    foreach (string name in dx.GetSubKeyNames())
                    {
                        using (RegistryKey k = dx.OpenSubKey(name))
                        {
                            if (k == null) continue;
                            object luidO = k.GetValue("AdapterLuid"), memO = k.GetValue("DedicatedVideoMemory"), venO = k.GetValue("VendorId"), seenO = k.GetValue("LastSeen");
                            if (luidO == null || memO == null) continue;
                            long luid = Convert.ToInt64(luidO), mem = Convert.ToInt64(memO);
                            int vendor = venO == null ? 0 : Convert.ToInt32(venO);
                            long seen = seenO == null ? 0 : Convert.ToInt64(seenO);
                            if (vendor == 0x1414 || mem <= 0) continue; // Microsoft Basic Render
                            long score = (vendor == 0x10DE ? (1L << 62) : 0) + (seen >> 2) + (mem >> 20);
                            if (score > bestScore)
                            {
                                bestScore = score;
                                vram = mem;
                                best = string.Format("luid_0x{0:x8}_0x{1:x8}", (uint)(luid >> 32), (uint)(luid & 0xffffffff));
                            }
                        }
                    }
                }
            }
            catch { }
            return best;
        }

        IntPtr Add(string path)
        {
            IntPtr c;
            return Native.PdhAddEnglishCounter(query, path, IntPtr.Zero, out c) == 0 ? c : IntPtr.Zero;
        }

        public void Update(bool slow)
        {
            if (query != IntPtr.Zero && Native.PdhCollectQueryData(query) == 0)
            {
                Cpu = Math.Min(100, Single(cCpu));
                double nominal = Single(cFreq);
                CpuGhz = (nominal > 0 ? nominal : baseMhz) * Single(cPerf) / 100000.0;
                NetRx = Sum(cRx, null, null);
                NetTx = Sum(cTx, null, null);
                if (GpuOk)
                {
                    Gpu = Math.Min(100, Sum(cGpu, nvLuid, "engtype_3D"));
                    VramUsed = Sum(cVram, nvLuid, null);
                }
            }

            Native.MEMORYSTATUSEX m = new Native.MEMORYSTATUSEX();
            if (Native.GlobalMemoryStatusEx(m)) { RamTotal = m.ullTotalPhys; RamUsed = m.ullTotalPhys - m.ullAvailPhys; }

            UpdateGpuTemp();
            Frames.Tick(Native.ForegroundPid());

            if (!pingBusy)
            {
                pingBusy = true;
                try { ping.SendAsync(host, 1000, payload, null); }
                catch { pingBusy = false; Push(-1); }
            }

            if (slow) UpdatePower();
        }

        // A temperatura vem da NVML, consultada só com a GPU já em uso, para não acordar a placa dedicada em repouso.
        void UpdateGpuTemp()
        {
            DateTime now = DateTime.UtcNow;
            if (Gpu >= 1) gpuLastActive = now;
            if (now - gpuLastActive < TimeSpan.FromSeconds(15))
            {
                if (nvml == null && now >= nvmlRetryAt)
                {
                    nvml = Nvml.TryOpen();
                    if (nvml == null) nvmlRetryAt = now.AddSeconds(30);
                }
                GpuTemp = nvml != null ? nvml.Temperature() : 0;
            }
            else
            {
                if (nvml != null) { nvml.Dispose(); nvml = null; }
                GpuTemp = 0;
            }
        }

        void UpdatePower()
        {
            Native.SYSTEM_POWER_STATUS ps;
            if (Native.GetSystemPowerStatus(out ps))
            {
                OnAc = ps.ACLineStatus != 0;
                Battery = ps.BatteryLifePercent <= 100 ? ps.BatteryLifePercent : -1;
            }
            IntPtr pg;
            if (Native.PowerGetActiveScheme(IntPtr.Zero, out pg) != 0) return;
            Guid scheme = (Guid)Marshal.PtrToStructure(pg, typeof(Guid));
            Native.LocalFree(pg);

            // Turbo só fica ligado com estado máximo em 100% e modo de boost diferente de "Desabilitado".
            uint max = ReadPower(scheme, ProcThrottleMax, 100);
            uint boost = ReadPower(scheme, PerfBoostMode, 1);
            Turbo = max >= 100 && boost != 0;

            PlanName = SchemeName(scheme);
        }

        static string SchemeName(Guid scheme)
        {
            uint size = 0;
            string name = "";
            Native.PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
            if (size > 0 && size < 1024)
            {
                IntPtr nb = Marshal.AllocHGlobal((int)size);
                if (Native.PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, nb, ref size) == 0)
                    name = Marshal.PtrToStringUni(nb) ?? "";
                Marshal.FreeHGlobal(nb);
            }
            return name;
        }

        // Ctrl+Alt+T: alterna Escritório ↔ Turbo no Avell Custom Control e, junto, o plano do Windows
        // ("Jogos" = turbo ligado, Equilibrado = turbo desligado); o modo Turbo da Avell não mexe no plano sozinho.
        // Sem o serviço da Avell, decide pelo plano ativo.
        public void ToggleTurbo()
        {
            Guid games;
            bool hasGames = FindScheme("Jogos", out games);
            IntPtr pg;
            if (Native.PowerGetActiveScheme(IntPtr.Zero, out pg) != 0) return;
            Guid active = (Guid)Marshal.PtrToStructure(pg, typeof(Guid));
            Native.LocalFree(pg);

            int mode = Avell.Mode;
            bool on = mode >= 0 ? mode != AvellControl.Turbo : active != games;
            if (mode >= 0) Avell.SetMode(on ? AvellControl.Turbo : AvellControl.Office);
            if (hasGames)
            {
                Guid target = on ? games : Balanced;
                Native.PowerSetActiveScheme(IntPtr.Zero, ref target);
            }
            UpdatePower();
        }

        static bool FindScheme(string name, out Guid found)
        {
            found = Guid.Empty;
            IntPtr buf = Marshal.AllocHGlobal(16);
            try
            {
                for (uint i = 0; ; i++)
                {
                    uint size = 16;
                    if (Native.PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 16, i, buf, ref size) != 0) return false; // 16 = ACCESS_SCHEME
                    Guid g = (Guid)Marshal.PtrToStructure(buf, typeof(Guid));
                    if (string.Equals(SchemeName(g), name, StringComparison.OrdinalIgnoreCase)) { found = g; return true; }
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        uint ReadPower(Guid scheme, Guid setting, uint fallback)
        {
            Guid sub = SubProcessor;
            uint v;
            uint r = OnAc ? Native.PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out v)
                          : Native.PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out v);
            return r == 0 ? v : fallback;
        }

        void OnPing(object sender, PingCompletedEventArgs e)
        {
            pingBusy = false;
            bool ok = e.Error == null && !e.Cancelled && e.Reply != null && e.Reply.Status == IPStatus.Success;
            Push(ok ? (int)e.Reply.RoundtripTime : -1);
        }

        void Push(int ms)
        {
            ring[ringPos] = ms;
            ringPos = (ringPos + 1) % ring.Length;
            if (ringCount < ring.Length) ringCount++;
            Samples = ringCount;
            PingMs = ms;

            int lost = 0, prev = -1, diffs = 0; double jit = 0;
            for (int i = 0; i < ringCount; i++)
            {
                int v = ring[(ringPos - ringCount + i + ring.Length) % ring.Length];
                if (v < 0) { lost++; prev = -1; continue; }
                if (prev >= 0 && i >= ringCount - 20) { jit += Math.Abs(v - prev); diffs++; }
                prev = v;
            }
            Loss = ringCount > 0 ? lost * 100.0 / ringCount : 0;
            Jitter = diffs > 0 ? jit / diffs : 0;
        }

        static double Single(IntPtr c)
        {
            if (c == IntPtr.Zero) return 0;
            Native.PDH_FMT_COUNTERVALUE v;
            if (Native.PdhGetFormattedCounterValue(c, FMT, IntPtr.Zero, out v) != 0) return 0;
            return v.CStatus <= 1 ? v.doubleValue : 0;
        }

        // Soma as instâncias de um contador curinga, opcionalmente filtrando pelo nome da instância.
        double Sum(IntPtr c, string f1, string f2)
        {
            if (c == IntPtr.Zero) return 0;
            uint size = 0, count;
            int r = Native.PdhGetFormattedCounterArray(c, FMT, ref size, out count, IntPtr.Zero);
            if (r != PDH_MORE_DATA || size == 0) return 0;
            if (size > bufSize)
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                buf = Marshal.AllocHGlobal((int)size);
                bufSize = (int)size;
            }
            if (Native.PdhGetFormattedCounterArray(c, FMT, ref size, out count, buf) != 0) return 0;

            double sum = 0;
            for (int i = 0; i < count; i++)
            {
                IntPtr p = buf + i * 24; // PDH_FMT_COUNTERVALUE_ITEM_W: nome(ptr) + status + double
                if (Marshal.ReadInt32(p, 8) > 1) continue;
                if (f1 != null || f2 != null)
                {
                    string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(p)) ?? "";
                    if (f1 != null && name.IndexOf(f1, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (f2 != null && name.IndexOf(f2, StringComparison.OrdinalIgnoreCase) < 0) continue;
                }
                sum += BitConverter.Int64BitsToDouble(Marshal.ReadInt64(p, 16));
            }
            return sum;
        }

        public void Dispose()
        {
            Frames.Dispose();
            Avell.Dispose();
            if (nvml != null) { nvml.Dispose(); nvml = null; }
            if (query != IntPtr.Zero) { Native.PdhCloseQuery(query); query = IntPtr.Zero; }
            if (buf != IntPtr.Zero) { Marshal.FreeHGlobal(buf); buf = IntPtr.Zero; }
            ping.Dispose();
        }
    }

    // Modo de desempenho do Avell Custom Control (Escritório, Gaming, Turbo), que define os limites de potência
    // da CPU no firmware (Escritório: 45 W; Turbo: 140/165 W). O app da Avell conversa com o serviço GCUService
    // por MQTT no broker local do GCUBridge (porta 13688); o widget entra como mais um cliente, assina o status
    // e publica o mesmo comando que o app manda ao trocar de modo.
    sealed class AvellControl : IDisposable
    {
        public const int Office = 0, Gaming = 1, Turbo = 2;
        // Cliente que o GCUBridge aceita para uma ferramenta de overclock que não vem instalada neste notebook.
        const string ClientId = "OcTool", User = "OcToolUser", Password = "OcToolPwd123";

        public volatile int Mode = -1; // 0 Escritório, 1 Gaming, 2 Turbo, 3 Personalizado; -1 = serviço fora do ar
        readonly object writeLock = new object();
        volatile bool stop;
        TcpClient tcp;
        NetworkStream stream;
        readonly System.Threading.Timer refreshTimer;

        public AvellControl()
        {
            refreshTimer = new System.Threading.Timer(delegate { Refresh(); }, null, Timeout.Infinite, Timeout.Infinite);
            Thread t = new Thread(Run);
            t.IsBackground = true;
            t.Start();
        }

        public bool SetMode(int mode)
        {
            string action = mode == Turbo ? "OPERATING_TURBO_MODE" : (mode == Gaming ? "OPERATING_GAMING_MODE" : "OPERATING_OFFICE_MODE");
            if (!Publish("Fan/Control", "{\"Action\":\"" + action + "\",\"ProfileIndex\":\"0\"}")) return false;
            Mode = mode; // o serviço confirma em Tray/Status logo depois
            // Depois de uma troca, o Fan/Status que o serviço publica vem com "OperatingMode":null e o painel da Avell
            // não se atualiza (quando é ele quem troca, já sabe o modo). Um GETSTATUS quando a troca termina faz o
            // serviço publicar o status completo para todos os clientes, inclusive o painel.
            refreshTimer.Change(2500, Timeout.Infinite);
            return true;
        }

        // Pede o status atual; a resposta chega em Fan/Status.
        public void Refresh() { Publish("Fan/Control", "{\"Action\":\"GETSTATUS\"}"); }

        void Run()
        {
            while (!stop)
            {
                try { Session(); } catch { }
                Close();
                Mode = -1;
                for (int i = 0; i < 20 && !stop; i++) Thread.Sleep(500);
            }
        }

        void Session()
        {
            TcpClient c = new TcpClient();
            lock (writeLock) tcp = c;
            if (!c.ConnectAsync("127.0.0.1", 13688).Wait(2000)) return;
            lock (writeLock) { stream = c.GetStream(); stream.WriteTimeout = 2000; }

            List<byte> body = new List<byte>();
            Str(body, "MQTT");
            body.AddRange(new byte[] { 4, 0xC2, 0, 60 }); // MQTT 3.1.1, usuário + senha + sessão limpa, keep-alive 60 s
            Str(body, ClientId); Str(body, User); Str(body, Password);
            Send(0x10, body);
            stream.ReadTimeout = 5000;
            byte type;
            byte[] data = Read(out type);
            if (type != 0x20 || data.Length < 2 || data[1] != 0) return; // CONNACK recusado

            body = new List<byte> { 0, 1 };
            Str(body, "Tray/Status"); body.Add(0);
            Str(body, "Fan/Status"); body.Add(0);
            Send(0x82, body);
            Refresh();

            stream.ReadTimeout = 30000;
            while (!stop)
            {
                try { data = Read(out type); }
                catch (IOException e)
                {
                    SocketException se = e.InnerException as SocketException;
                    if (se == null || se.SocketErrorCode != SocketError.TimedOut) throw;
                    Send(0xC0, new List<byte>()); // PINGREQ: 30 s sem mensagens, mantém a conexão viva
                    continue;
                }
                if ((type & 0xF0) == 0x30) OnPublish(type, data);
            }
        }

        void OnPublish(byte type, byte[] data)
        {
            if (data.Length < 2) return;
            int p = 2 + (data[0] << 8 | data[1]) + (((type >> 1) & 3) > 0 ? 2 : 0); // tópico + id do pacote (QoS > 0)
            if (p > data.Length) return;
            Match m = Regex.Match(Encoding.UTF8.GetString(data, p, data.Length - p), "\"OperatingMode\":\"(\\d)\"");
            if (m.Success) Mode = m.Groups[1].Value[0] - '0';
        }

        bool Publish(string topic, string payload)
        {
            List<byte> body = new List<byte>();
            Str(body, topic);
            body.AddRange(Encoding.UTF8.GetBytes(payload));
            try { Send(0x30, body); return true; }
            catch { return false; }
        }

        void Send(byte header, List<byte> body)
        {
            List<byte> pkt = new List<byte> { header };
            int len = body.Count;
            do
            {
                byte b = (byte)(len % 128);
                len /= 128;
                if (len > 0) b |= 128;
                pkt.Add(b);
            } while (len > 0);
            pkt.AddRange(body);
            byte[] a = pkt.ToArray();
            lock (writeLock)
            {
                if (stream == null) throw new IOException("sem conexão");
                stream.Write(a, 0, a.Length);
            }
        }

        byte[] Read(out byte type)
        {
            type = ReadByte();
            int len = 0, mult = 1, b;
            do { b = ReadByte(); len += (b & 127) * mult; mult *= 128; } while ((b & 128) != 0);
            byte[] data = new byte[len];
            for (int got = 0; got < len; )
            {
                int n = stream.Read(data, got, len - got);
                if (n <= 0) throw new IOException("conexão fechada");
                got += n;
            }
            return data;
        }

        byte ReadByte()
        {
            int b = stream.ReadByte();
            if (b < 0) throw new IOException("conexão fechada");
            return (byte)b;
        }

        static void Str(List<byte> body, string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s);
            body.Add((byte)(b.Length >> 8));
            body.Add((byte)b.Length);
            body.AddRange(b);
        }

        void Close()
        {
            lock (writeLock)
            {
                if (tcp != null) { tcp.Close(); tcp = null; }
                stream = null;
            }
        }

        public void Dispose()
        {
            stop = true;
            refreshTimer.Dispose();
            Close();
        }
    }

    // FPS e latência do app em primeiro plano, lidos do PresentMon (Intel) via CSV no stdout.
    // Precisa de admin ou do grupo "Usuários de log de desempenho" para abrir a sessão ETW.
    sealed class FrameStats : IDisposable
    {
        const string Session = "TurboHUD";

        public double Fps, LatencyMs;
        public string Status = "", GameName = "";

        readonly string exe;
        readonly object gate = new object();
        readonly Stopwatch clock = Stopwatch.StartNew();
        Process proc;
        IntPtr job;
        volatile bool denied;
        // Quadros por processo no último segundo: [quadros, soma da latência, amostras de latência]
        Dictionary<int, double[]> acc = new Dictionary<int, double[]>();
        int game;
        long lastTick;
        DateTime retryAt = DateTime.MinValue;
        int iPid = -1, iLat = -1;

        public FrameStats(string exePath)
        {
            exe = exePath;
            if (!File.Exists(exe)) Status = "PresentMon ausente";
            else Start();
        }

        void Start()
        {
            iPid = -1; iLat = -1; denied = false;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe,
                    "--v2_metrics --output_stdout --no_console_stats --no_track_input --exclude dwm.exe --session_name " + Session + " --stop_existing_session");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                proc = new Process();
                proc.StartInfo = psi;
                proc.OutputDataReceived += OnLine;
                proc.ErrorDataReceived += OnError;
                proc.Start();
                AttachToJob(proc);
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                Status = "";
            }
            catch { proc = null; Status = "PresentMon falhou"; }
        }

        // Se o widget morrer, o job fecha o PresentMon junto.
        void AttachToJob(Process p)
        {
            try
            {
                if (job == IntPtr.Zero)
                {
                    job = Native.CreateJobObject(IntPtr.Zero, null);
                    Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION info = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                    info.BasicLimitInformation.LimitFlags = 0x2000; // KILL_ON_JOB_CLOSE
                    Native.SetInformationJobObject(job, 9, ref info, (uint)Marshal.SizeOf(info));
                }
                Native.AssignProcessToJobObject(job, p.Handle);
            }
            catch { }
        }

        void OnError(object sender, DataReceivedEventArgs e)
        {
            if (e.Data != null && e.Data.IndexOf("access denied", StringComparison.OrdinalIgnoreCase) >= 0) denied = true;
        }

        void OnLine(object sender, DataReceivedEventArgs e)
        {
            string line = e.Data;
            if (line == null) return;
            string[] f = line.Split(',');
            if (iPid < 0)
            {
                if (line.StartsWith("Application"))
                {
                    iPid = Array.IndexOf(f, "ProcessID");
                    iLat = Array.IndexOf(f, "DisplayLatency");
                    if (iLat < 0) iLat = Array.IndexOf(f, "MsUntilDisplayed");
                }
                return;
            }
            int pid;
            if (f.Length <= iPid || !int.TryParse(f[iPid], out pid)) return;
            double lat = 0;
            bool hasLat = iLat >= 0 && iLat < f.Length &&
                double.TryParse(f[iLat], NumberStyles.Float, CultureInfo.InvariantCulture, out lat) && lat > 0 && lat < 1000;
            lock (gate)
            {
                double[] a;
                if (!acc.TryGetValue(pid, out a)) { a = new double[3]; acc[pid] = a; }
                a[0]++;
                if (hasLat) { a[1] += lat; a[2]++; }
            }
        }

        // Mede o app em foco quando ele está renderizando (≥ 20 quadros/s); se você sair do jogo,
        // continua mostrando o último jogo enquanto ele ainda renderiza.
        public void Tick(int foregroundPid)
        {
            long now = clock.ElapsedMilliseconds;
            Dictionary<int, double[]> snap;
            lock (gate) { snap = acc; acc = new Dictionary<int, double[]>(); }
            double secs = Math.Max(0.001, (now - lastTick) / 1000.0);
            lastTick = now;

            double[] a;
            if (foregroundPid != 0 && foregroundPid != game && snap.TryGetValue(foregroundPid, out a) && a[0] / secs >= 20)
            {
                game = foregroundPid;
                GameName = ProcessName(game);
            }
            if (game != 0 && snap.TryGetValue(game, out a))
            {
                Fps = a[0] / secs;
                LatencyMs = a[2] > 0 ? a[1] / a[2] : 0;
            }
            else
            {
                Fps = 0;
                LatencyMs = 0;
                if (game != 0 && ProcessName(game).Length == 0) { game = 0; GameName = ""; }
            }

            if (proc != null && proc.HasExited)
            {
                Status = denied ? "sem permissão" : "PresentMon parou";
                proc.Dispose();
                proc = null;
                retryAt = DateTime.UtcNow.AddSeconds(60);
            }
            else if (proc == null && File.Exists(exe) && DateTime.UtcNow >= retryAt)
                Start();
        }

        static string ProcessName(int pid)
        {
            try { using (Process p = Process.GetProcessById(pid)) return p.ProcessName; }
            catch { return ""; }
        }

        // Para a captura (widget escondido); o próximo Tick religa.
        public void Stop()
        {
            if (proc != null)
            {
                try { if (!proc.HasExited) proc.Kill(); } catch { }
                proc.Dispose();
                proc = null;
                // Encerrar à força deixa a sessão ETW aberta; pede ao PresentMon para fechá-la.
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(exe, "--terminate_existing_session --session_name " + Session);
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    using (Process t = Process.Start(psi)) t.WaitForExit(3000);
                }
                catch { }
            }
            retryAt = DateTime.MinValue;
            Fps = 0;
            LatencyMs = 0;
        }

        public void Dispose()
        {
            Stop();
            if (job != IntPtr.Zero) { Native.CloseHandle(job); job = IntPtr.Zero; }
        }
    }

    sealed class Nvml : IDisposable
    {
        [DllImport("nvml.dll")] static extern int nvmlInit_v2();
        [DllImport("nvml.dll")] static extern int nvmlShutdown();
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint temp);

        IntPtr dev;

        public static Nvml TryOpen()
        {
            try
            {
                if (nvmlInit_v2() != 0) return null;
                Nvml n = new Nvml();
                if (nvmlDeviceGetHandleByIndex_v2(0, out n.dev) != 0) { nvmlShutdown(); return null; }
                return n;
            }
            catch { return null; }
        }

        public int Temperature()
        {
            try { uint t; return nvmlDeviceGetTemperature(dev, 0, out t) == 0 ? (int)t : 0; }
            catch { return 0; }
        }

        public void Dispose() { try { nvmlShutdown(); } catch { } }
    }

    static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int W, H; public SIZE(int w, int h) { W = w; H = h; } }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [StructLayout(LayoutKind.Explicit)]
        public struct PDH_FMT_COUNTERVALUE { [FieldOffset(0)] public uint CStatus; [FieldOffset(8)] public double doubleValue; }

        [StructLayout(LayoutKind.Sequential)]
        public class MEMORYSTATUSEX
        {
            public uint dwLength = 64, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }

        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr CreateJobObject(IntPtr attrs, string name);
        [DllImport("kernel32.dll")] public static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint size);
        [DllImport("kernel32.dll")] public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        public static int ForegroundPid()
        {
            uint pid;
            IntPtr h = GetForegroundWindow();
            return h == IntPtr.Zero || GetWindowThreadProcessId(h, out pid) == 0 ? 0 : (int)pid;
        }

        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr hmon, int type, out uint dpiX, out uint dpiY);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
        [DllImport("user32.dll")] public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int flags);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);

        [DllImport("kernel32.dll")] public static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX m);
        [DllImport("kernel32.dll")] public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);
        [DllImport("kernel32.dll")] public static extern IntPtr LocalFree(IntPtr h);

        [DllImport("powrprof.dll")] public static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
        [DllImport("powrprof.dll")] public static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] public static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] public static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
        [DllImport("powrprof.dll")] public static extern uint PowerEnumerate(IntPtr root, IntPtr scheme, IntPtr sub, uint access, uint index, IntPtr buffer, ref uint size);
        [DllImport("powrprof.dll")] public static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr sub, IntPtr setting, IntPtr buffer, ref uint size);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern int PdhOpenQuery(string source, IntPtr user, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
        [DllImport("pdh.dll")] public static extern int PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll")] public static extern int PdhCloseQuery(IntPtr query);
        [DllImport("pdh.dll")] public static extern int PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out PDH_FMT_COUNTERVALUE value);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr items);
    }
}
