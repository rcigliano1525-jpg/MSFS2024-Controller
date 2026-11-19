using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Xml;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace MSFS2024Start
{
    static class Program
    {
        static Mutex instanceMutex;
        static EventWaitHandle showEvent;
        static MainForm mainForm;

        [STAThread]
        static void Main()
        {
            try
            {
                if (!AcquireInstance())
                    return;

                Native.SetPerMonitorDpiAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
                {
                    Log.Write(e.Exception.ToString());
                    MessageBox.Show(e.Exception.Message, "MSFS 2024 Start");
                };

                mainForm = new MainForm();
                Thread signal = new Thread(WaitForShowSignal);
                signal.IsBackground = true;
                signal.Start();
                Application.Run(mainForm);
            }
            catch (Exception ex)
            {
                Log.Write(ex.ToString());
                MessageBox.Show(ex.Message, "MSFS 2024 Start");
            }
        }

        static bool AcquireInstance()
        {
            bool created;
            instanceMutex = new Mutex(true, @"Local\MSFS2024Start", out created);
            if (created)
            {
                showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\MSFS2024Start.Show");
                return true;
            }

            try
            {
                EventWaitHandle.OpenExisting(@"Local\MSFS2024Start.Show").Set();
            }
            catch (Exception ex)
            {
                Log.Write(ex.ToString());
            }
            return false;
        }

        static void WaitForShowSignal()
        {
            while (showEvent.WaitOne())
            {
                MainForm form = mainForm;
                if (form == null || form.IsDisposed)
                    continue;
                form.BeginInvoke(new Action(delegate { form.ShowFromTray(); }));
            }
        }
    }

    sealed class MainForm : Form
    {
        const string IntroText = "Start and Quit are for the opening screen. Start Flight and Ready to Fly continue a Free Flight.";

        readonly Label introLabel;
        readonly Label statusLabel;
        readonly RoundButton startButton;
        readonly RoundButton flightButton;
        readonly RoundButton readyButton;
        readonly RoundButton quitButton;
        readonly CheckBox hookBox;
        readonly LinkLabel alignLink;
        readonly NotifyIcon tray;
        readonly System.Windows.Forms.Timer timer;

        Settings settings;
        bool suppressHook;
        bool busy;
        bool aligning;
        bool eatMouseUp;
        int alignStep;
        bool wasRunning;
        int goneTicks;
        bool reallyExit;
        DateTime statusHoldUntil;
        IntPtr mouseHook;
        Native.LowLevelMouseProc mouseProc;

        public MainForm()
        {
            Text = "MSFS2024 Controller";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(656, 328);
            BackColor = Color.FromArgb(128, 132, 138);
            DoubleBuffered = true;
            ForeColor = Color.FromArgb(28, 30, 34);
            Font = new Font("Segoe UI", 10f);
            TopMost = true;
            Icon = MakeIcon();

            introLabel = new Label();
            introLabel.Text = IntroText;
            introLabel.ForeColor = Color.FromArgb(28, 30, 34);
            introLabel.Font = new Font("Segoe UI", 9.5f);
            introLabel.SetBounds(24, 16, 608, 40);

            statusLabel = new Label();
            statusLabel.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            statusLabel.ForeColor = Color.FromArgb(28, 30, 34);
            statusLabel.SetBounds(24, 56, 608, 32);

            startButton = MakeButton("Start", 24, 98, 296, 76, 16f, Color.FromArgb(0, 122, 204), Color.FromArgb(32, 152, 232), Color.FromArgb(0, 96, 168));
            flightButton = MakeButton("Start Flight", 336, 98, 296, 76, 15f, Color.FromArgb(0, 122, 204), Color.FromArgb(32, 152, 232), Color.FromArgb(0, 96, 168));
            readyButton = MakeButton("Ready to Fly", 24, 184, 296, 76, 14f, Color.FromArgb(0, 150, 108), Color.FromArgb(18, 176, 130), Color.FromArgb(0, 118, 84));
            quitButton = MakeButton("Quit to Desktop", 336, 184, 296, 76, 13f, Color.FromArgb(54, 60, 72), Color.FromArgb(72, 80, 94), Color.FromArgb(38, 42, 52));
            startButton.Click += OnStartClicked;
            flightButton.Click += OnFlightClicked;
            readyButton.Click += OnReadyClicked;
            quitButton.Click += OnQuitClicked;
            AcceptButton = startButton;

            hookBox = new CheckBox();
            hookBox.Text = "Open when MSFS 2024 starts";
            hookBox.ForeColor = Color.FromArgb(28, 30, 34);
            hookBox.BackColor = BackColor;
            hookBox.Font = new Font("Segoe UI", 9f);
            hookBox.SetBounds(24, 274, 340, 28);
            hookBox.CheckedChanged += OnHookChanged;

            alignLink = new LinkLabel();
            alignLink.Text = "Set click position";
            alignLink.Font = new Font("Segoe UI", 9f);
            alignLink.LinkColor = Color.FromArgb(130, 186, 232);
            alignLink.ActiveLinkColor = Color.White;
            alignLink.VisitedLinkColor = alignLink.LinkColor;
            alignLink.LinkBehavior = LinkBehavior.HoverUnderline;
            alignLink.BackColor = BackColor;
            alignLink.SetBounds(430, 278, 202, 22);
            alignLink.TextAlign = ContentAlignment.MiddleRight;
            alignLink.Click += OnAlignClicked;

            Controls.Add(introLabel);
            Controls.Add(statusLabel);
            Controls.Add(startButton);
            Controls.Add(flightButton);
            Controls.Add(readyButton);
            Controls.Add(quitButton);
            Controls.Add(hookBox);
            Controls.Add(alignLink);

            tray = new NotifyIcon();
            tray.Icon = Icon;
            tray.Text = "MSFS 2024 Start";
            tray.Visible = true;
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Show", null, delegate { ShowFromTray(); });
            menu.Items.Add("Start Flight", null, delegate { OnFlightClicked(this, EventArgs.Empty); });
            menu.Items.Add("Ready to Fly", null, delegate { OnReadyClicked(this, EventArgs.Empty); });
            menu.Items.Add("Quit to Desktop", null, delegate { OnQuitClicked(this, EventArgs.Empty); });
            menu.Items.Add("Exit", null, delegate { ExitHelper(); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ShowFromTray(); };

            settings = Settings.Load();

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 1000;
            timer.Tick += OnTick;
            timer.Start();

            wasRunning = SimScreen.IsRunning();
            RefreshButtons();
            SetIdleStatus();
            ApplyStartupHook();
            KeyPreview = true;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int caption = ColorRef(Color.Black);
            int text = ColorRef(Color.White);
            Native.DwmSetWindowAttribute(Handle, Native.DWMWA_CAPTION_COLOR, ref caption, 4);
            Native.DwmSetWindowAttribute(Handle, Native.DWMWA_TEXT_COLOR, ref text, 4);
            Native.DwmSetWindowAttribute(Handle, Native.DWMWA_BORDER_COLOR, ref caption, 4);
        }

        static int ColorRef(Color color)
        {
            return color.R | (color.G << 8) | (color.B << 16);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            PlaceOnSimScreen();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!reallyExit && SimScreen.IsRunning())
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            EndAlign();
            timer.Stop();
            tray.Visible = false;
            tray.Dispose();
            base.OnFormClosing(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (aligning && keyData == Keys.Escape)
            {
                EndAlign();
                SetStatus("Click position was not changed.", StatusKind.Normal, 6);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        public void ShowFromTray()
        {
            ShowInTaskbar = true;
            Show();
            WindowState = FormWindowState.Normal;
            TopMost = true;
            PlaceOnSimScreen();
            Activate();
        }

        void HideToTray()
        {
            Hide();
            ShowInTaskbar = false;
        }

        void ExitHelper()
        {
            reallyExit = true;
            Close();
        }

        void PlaceOnSimScreen()
        {
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            IntPtr hwnd = SimScreen.FindWindow();
            if (hwnd != IntPtr.Zero)
            {
                Rectangle client = SimScreen.ClientBounds(hwnd);
                if (client.Width > 0)
                    area = Screen.FromRectangle(client).WorkingArea;
            }
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + 36);
        }

        void ApplyStartupHook()
        {
            suppressHook = true;
            hookBox.Checked = settings.AutoStart;
            suppressHook = false;
            if (!settings.AutoStart)
                return;

            string error = StartupHook.SetEnabled(true, Application.ExecutablePath);
            if (error != null)
            {
                Log.Write(error);
                SetStatus(error, StatusKind.Warn, 12);
            }
        }

        void OnHookChanged(object sender, EventArgs e)
        {
            if (suppressHook)
                return;

            settings.AutoStart = hookBox.Checked;
            settings.Save();
            string error = StartupHook.SetEnabled(hookBox.Checked, Application.ExecutablePath);
            if (error != null)
            {
                SetStatus(error, StatusKind.Warn, 12);
                return;
            }

            if (hookBox.Checked)
                SetStatus("This window will open when MSFS 2024 starts.", StatusKind.Ok, 8);
            else
                SetStatus("This window will no longer open with the simulator.", StatusKind.Normal, 8);
        }

        void OnTick(object sender, EventArgs e)
        {
            if (Visible)
                TopMost = true;

            bool running = SimScreen.IsRunning();
            if (running)
            {
                goneTicks = 0;
                if (!wasRunning)
                    ShowFromTray();
            }
            else if (wasRunning)
            {
                goneTicks++;
                if (goneTicks >= 2)
                {
                    reallyExit = true;
                    Close();
                    return;
                }
            }

            wasRunning = running;
            RefreshButtons();
            if (DateTime.UtcNow >= statusHoldUntil && !aligning)
                SetIdleStatus();
        }

        void SetIdleStatus()
        {
            if (SimScreen.IsRunning())
                SetStatus("Choose the button for the screen you are on.", StatusKind.Normal, 0);
            else
                SetStatus("Waiting for Microsoft Flight Simulator 2024…", StatusKind.Normal, 0);
        }

        void OnStartClicked(object sender, EventArgs e)
        {
            if (busy || aligning)
                return;
            RunAction(delegate { PerformStart(); });
        }

        void OnFlightClicked(object sender, EventArgs e)
        {
            if (busy || aligning)
                return;
            RunAction(delegate { PerformStartFlight(); });
        }

        void OnReadyClicked(object sender, EventArgs e)
        {
            if (busy || aligning)
                return;
            RunAction(delegate { PerformReadyToFly(); });
        }

        void OnQuitClicked(object sender, EventArgs e)
        {
            if (busy || aligning)
                return;
            RunAction(delegate { PerformQuit(); });
        }

        void RunAction(Action work)
        {
            SetBusy(true);
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    Log.Write(ex.ToString());
                    SetStatus("Something went wrong. Details are in the log.", StatusKind.Warn, 10);
                    ShowAgain();
                }
                finally
                {
                    SetBusy(false);
                }
            });
        }

        void PerformStart()
        {
            SetStatus("Looking for the Start button…", StatusKind.Normal, 30);
            IntPtr hwnd = RequireSim();
            if (hwnd == IntPtr.Zero)
                return;

            PrepareSim(hwnd);
            List<ScreenLine> lines = SimScreen.Read(hwnd);
            ScreenLine start = FindStart(lines, SimScreen.ClientBounds(hwnd));
            if (start != null)
            {
                Point point = Center(start.Bounds);
                Log.Write("Click Start at " + point.X + "," + point.Y + " text=" + start.Text);
                SimScreen.Click(point);
                if (ResolveStart(hwnd, point))
                    return;
            }

            if (settings.HasStart)
            {
                ClickSaved(hwnd, settings.StartX, settings.StartY, "Start");
                return;
            }

            SetStatus("The Start button isn't on screen yet.", StatusKind.Warn, 8);
            ShowAgain();
        }

        void PerformStartFlight()
        {
            SetStatus("Sending Ctrl+Enter…", StatusKind.Normal, 30);
            IntPtr hwnd = RequireSim();
            if (hwnd == IntPtr.Zero)
                return;

            PrepareSim(hwnd);
            SimScreen.Focus(hwnd);
            Thread.Sleep(160);
            SimScreen.TapCtrlEnter();
            Log.Write("Sent Ctrl+Enter for Start Flight");
            SetStatus("Start Flight sent (Ctrl+Enter).", StatusKind.Ok, 8);
            ShowAgain();
        }

        void PerformReadyToFly()
        {
            SetStatus("Looking for Ready to Fly…", StatusKind.Normal, 30);
            IntPtr hwnd = RequireSim();
            if (hwnd == IntPtr.Zero)
                return;

            PrepareSim(hwnd);
            Rectangle window = SimScreen.ClientBounds(hwnd);
            ScreenLine ready = FindReady(SimScreen.Read(hwnd), window);
            if (ready == null)
            {
                SetStatus("Ready to Fly isn't on screen yet.", StatusKind.Warn, 8);
                ShowAgain();
                return;
            }

            Point point = Center(ready.Bounds);
            Log.Write("Click Ready to Fly at " + point.X + "," + point.Y + " text=" + ready.Text);
            SimScreen.Focus(hwnd);
            SimScreen.PressButton(point);
            Thread.Sleep(700);
            if (FindReady(SimScreen.Read(hwnd), SimScreen.ClientBounds(hwnd)) == null)
            {
                SetStatus("Ready to Fly pressed.", StatusKind.Ok, 8);
                ShowAgain();
                return;
            }

            Log.Write("Ready to Fly is still up. Pressing it again.");
            SimScreen.Focus(hwnd);
            SimScreen.PressButton(point);
            Thread.Sleep(500);
            if (FindReady(SimScreen.Read(hwnd), SimScreen.ClientBounds(hwnd)) == null)
            {
                SetStatus("Ready to Fly pressed.", StatusKind.Ok, 8);
                ShowAgain();
                return;
            }

            Log.Write("Ready to Fly is still up. Pressing Enter.");
            SimScreen.Focus(hwnd);
            Thread.Sleep(120);
            SimScreen.TapEnter();
            Thread.Sleep(600);
            if (FindReady(SimScreen.Read(hwnd), SimScreen.ClientBounds(hwnd)) != null)
                SetStatus("Ready to Fly is still there. Click it again.", StatusKind.Warn, 10);
            else
                SetStatus("Ready to Fly pressed.", StatusKind.Ok, 8);
            ShowAgain();
        }

        void PerformQuit()
        {
            SetStatus("Looking for Quit to Desktop…", StatusKind.Normal, 30);
            IntPtr hwnd = RequireSim();
            if (hwnd == IntPtr.Zero)
                return;

            PrepareSim(hwnd);
            Rectangle window = SimScreen.ClientBounds(hwnd);
            List<ScreenLine> lines = SimScreen.Read(hwnd);
            ScreenLine confirm = FindQuit(lines, window, true);
            if (confirm != null)
            {
                Log.Write("Click quit confirm at " + confirm.Bounds.X + "," + confirm.Bounds.Y);
                SimScreen.Click(Center(confirm.Bounds));
                SetStatus("Quit confirmation pressed.", StatusKind.Ok, 8);
                ShowAgain();
                return;
            }

            ScreenLine quit = FindQuit(lines, window, false);
            if (quit != null)
            {
                Log.Write("Click Quit to Desktop at " + quit.Bounds.X + "," + quit.Bounds.Y + " text=" + quit.Text);
                SimScreen.Click(Center(quit.Bounds));
                Thread.Sleep(900);
                if (!SimScreen.IsRunning())
                {
                    SetStatus("Quit to Desktop pressed.", StatusKind.Ok, 8);
                    return;
                }

                List<ScreenLine> after = SimScreen.Read(hwnd);
                ScreenLine second = FindQuit(after, SimScreen.ClientBounds(hwnd), true);
                if (second != null)
                {
                    Log.Write("Click quit confirm at " + second.Bounds.X + "," + second.Bounds.Y);
                    SimScreen.Click(Center(second.Bounds));
                    SetStatus("Quit to Desktop pressed.", StatusKind.Ok, 8);
                    ShowAgain();
                    return;
                }

                SetStatus("Quit to Desktop was clicked. Click it again if the simulator is asking to confirm.", StatusKind.Warn, 10);
                ShowAgain();
                return;
            }

            if (settings.HasQuit)
            {
                ClickSaved(hwnd, settings.QuitX, settings.QuitY, "Quit to Desktop");
                return;
            }

            SetStatus("Quit to Desktop isn't on screen yet.", StatusKind.Warn, 8);
            ShowAgain();
        }

        void FinishStart()
        {
            SetStatus("Start pressed. This window stays open.", StatusKind.Ok, 8);
            ShowAgain();
        }

        bool ResolveStart(IntPtr hwnd, Point point)
        {
            StartResult first = WaitForMainMenu(hwnd);
            if (first == StartResult.InMenu)
            {
                FinishStart();
                return true;
            }
            if (first != StartResult.StillThere)
            {
                SetStatus("If the start screen is still up, click Start again.", StatusKind.Warn, 12);
                ShowAgain();
                return true;
            }

            Log.Write("Start is still up. Sending a direct click.");
            SimScreen.Focus(hwnd);
            SimScreen.ClickWindow(hwnd, point);
            if (WaitForMainMenu(hwnd) == StartResult.InMenu)
            {
                FinishStart();
                return true;
            }

            Log.Write("Start is still up. Pressing Enter.");
            SimScreen.Focus(hwnd);
            SimScreen.TapEnter();
            if (WaitForMainMenu(hwnd) == StartResult.InMenu)
            {
                FinishStart();
                return true;
            }

            SetStatus("The Start button is still there. Click Start again.", StatusKind.Warn, 12);
            ShowAgain();
            return true;
        }

        StartResult WaitForMainMenu(IntPtr hwnd)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(2.5);
            int stillCount = 0;
            while (DateTime.UtcNow < until)
            {
                Thread.Sleep(400);
                StartResult now = InspectScreen(hwnd);
                if (now == StartResult.InMenu)
                    return now;
                if (now == StartResult.StillThere)
                {
                    stillCount++;
                    if (stillCount >= 2)
                        return StartResult.StillThere;
                }
                else
                {
                    stillCount = 0;
                }
            }
            return StartResult.Unknown;
        }

        StartResult InspectScreen(IntPtr hwnd)
        {
            Rectangle window = SimScreen.ClientBounds(hwnd);
            List<ScreenLine> lines = SimScreen.Read(hwnd);
            bool startVisible = FindStart(lines, window) != null;
            bool mainMenu = LinesContain(lines, "Free Flight")
                || LinesContain(lines, "Activities")
                || LinesContain(lines, "Career");
            Log.Write("After Start: startVisible=" + startVisible + " mainMenu=" + mainMenu);
            if (startVisible)
                return StartResult.StillThere;
            if (mainMenu)
                return StartResult.InMenu;
            return StartResult.Unknown;
        }

        static bool LinesContain(List<ScreenLine> lines, string phrase)
        {
            foreach (ScreenLine line in lines)
            {
                if (line.Text != null && line.Text.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        void ClickSaved(IntPtr hwnd, double nx, double ny, string name)
        {
            Rectangle window = SimScreen.ClientBounds(hwnd);
            Point point = new Point(
                window.Left + (int)Math.Round(window.Width * nx),
                window.Top + (int)Math.Round(window.Height * ny));
            Log.Write("Saved click " + name + " at " + point.X + "," + point.Y);
            SimScreen.Click(point);
            SetStatus(name + " pressed at the saved position.", StatusKind.Ok, 8);
            ShowAgain();
        }

        IntPtr RequireSim()
        {
            IntPtr hwnd = SimScreen.FindWindow();
            if (hwnd != IntPtr.Zero)
                return hwnd;
            SetStatus("Microsoft Flight Simulator 2024 isn't running.", StatusKind.Warn, 8);
            ShowAgain();
            return IntPtr.Zero;
        }

        void PrepareSim(IntPtr hwnd)
        {
            SimScreen.Focus(hwnd);
            Invoke(new Action(delegate
            {
                Hide();
                Application.DoEvents();
            }));
            Thread.Sleep(220);
        }

        void ShowAgain()
        {
            if (IsDisposed)
                return;
            BeginInvoke(new Action(delegate { ShowFromTray(); }));
        }

        void HideAgain()
        {
            if (IsDisposed)
                return;
            BeginInvoke(new Action(delegate { HideToTray(); }));
        }

        void OnAlignClicked(object sender, EventArgs e)
        {
            if (busy || aligning)
                return;
            if (!SimScreen.IsRunning())
            {
                SetStatus("Start the simulator first, then set the click position.", StatusKind.Warn, 8);
                return;
            }

            aligning = true;
            alignStep = 1;
            eatMouseUp = false;
            RefreshButtons();
            SetStatus("Click Start in the simulator. That click is only recorded. Esc cancels.", StatusKind.Normal, 60);
            mouseProc = OnAlignMouse;
            mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, IntPtr.Zero, 0);
            if (mouseHook == IntPtr.Zero)
            {
                aligning = false;
                RefreshButtons();
                SetStatus("Couldn't watch the mouse. Try again.", StatusKind.Warn, 8);
            }
        }

        IntPtr OnAlignMouse(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && aligning)
            {
                int message = wParam.ToInt32();
                Native.MSLLHOOKSTRUCT info = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                Point point = new Point(info.pt.X, info.pt.Y);
                bool outside = !Bounds.Contains(point);
                if (message == Native.WM_LBUTTONDOWN && outside)
                {
                    eatMouseUp = true;
                    BeginInvoke(new Action(delegate { RecordAlignPoint(point); }));
                    return (IntPtr)1;
                }
                if (message == Native.WM_LBUTTONUP && eatMouseUp && outside)
                {
                    eatMouseUp = false;
                    return (IntPtr)1;
                }
            }
            return Native.CallNextHookEx(mouseHook, nCode, wParam, lParam);
        }

        void RecordAlignPoint(Point screen)
        {
            if (!aligning)
                return;

            IntPtr hwnd = SimScreen.FindWindow();
            Rectangle client = hwnd == IntPtr.Zero ? Rectangle.Empty : SimScreen.ClientBounds(hwnd);
            if (client.Width < 10 || client.Height < 10)
            {
                EndAlign();
                SetStatus("The simulator window could not be measured.", StatusKind.Warn, 8);
                return;
            }

            double nx = (screen.X - client.Left) / (double)client.Width;
            double ny = (screen.Y - client.Top) / (double)client.Height;
            nx = Math.Max(0, Math.Min(1, nx));
            ny = Math.Max(0, Math.Min(1, ny));

            if (alignStep == 1)
            {
                settings.HasStart = true;
                settings.StartX = nx;
                settings.StartY = ny;
                settings.Save();
                alignStep = 2;
                SetStatus("Click Quit to Desktop in the simulator. Esc cancels.", StatusKind.Normal, 60);
                return;
            }

            settings.HasQuit = true;
            settings.QuitX = nx;
            settings.QuitY = ny;
            settings.Save();
            EndAlign();
            SetStatus("Click positions saved. They are used if the button text can't be read.", StatusKind.Ok, 8);
        }

        void EndAlign()
        {
            aligning = false;
            eatMouseUp = false;
            if (mouseHook != IntPtr.Zero)
            {
                Native.UnhookWindowsHookEx(mouseHook);
                mouseHook = IntPtr.Zero;
            }
            mouseProc = null;
            if (!IsDisposed)
                RefreshButtons();
        }

        static ScreenLine FindStart(List<ScreenLine> lines, Rectangle window)
        {
            ScreenLine best = null;
            foreach (ScreenLine line in lines)
            {
                if (ContainsWord(line, "Restart"))
                    continue;
                foreach (ScreenWord word in line.Words)
                {
                    if (!TextEquals(word.Text, "Start"))
                        continue;
                    if (!IsBottomBand(word.Bounds, window))
                        continue;
                    if (word.Bounds.Left < window.Left + window.Width * 0.55)
                        continue;
                    if (best == null || word.Bounds.Left > best.Bounds.Left)
                    {
                        best = new ScreenLine();
                        best.Text = line.Text;
                        best.Bounds = word.Bounds;
                        best.Words = line.Words;
                    }
                }
            }
            return best;
        }

        static ScreenLine FindQuit(List<ScreenLine> lines, Rectangle window, bool confirmDialog)
        {
            ScreenLine best = null;
            foreach (ScreenLine line in lines)
            {
                Rectangle phrase = QuitPhraseBounds(line);
                if (phrase.Width <= 0)
                    continue;

                bool bottom = IsBottomBand(phrase, window);
                bool center = IsCenterBand(phrase, window);
                if (confirmDialog)
                {
                    if (!center)
                        continue;
                }
                else if (!bottom)
                {
                    continue;
                }

                if (best == null || phrase.Top > best.Bounds.Top)
                {
                    best = new ScreenLine();
                    best.Text = line.Text;
                    best.Bounds = phrase;
                    best.Words = line.Words;
                }
            }
            return best;
        }

        static ScreenLine FindReady(List<ScreenLine> lines, Rectangle window)
        {
            ScreenLine best = null;
            foreach (ScreenLine line in lines)
            {
                Rectangle phrase = PhraseBounds(line, "Ready", "Fly", 3);
                if (phrase.Width <= 0)
                    continue;
                int cx = phrase.Left + phrase.Width / 2;
                int cy = phrase.Top + phrase.Height / 2;
                if (cy < window.Top + (int)(window.Height * 0.55))
                    continue;
                if (cx < window.Left + (int)(window.Width * 0.40))
                    continue;
                if (best == null || phrase.Left > best.Bounds.Left)
                {
                    best = new ScreenLine();
                    best.Text = line.Text;
                    best.Bounds = phrase;
                    best.Words = line.Words;
                }
            }
            return best;
        }

        static Rectangle QuitPhraseBounds(ScreenLine line)
        {
            return PhraseBounds(line, "Quit", "Desktop", 3);
        }

        static Rectangle PhraseBounds(ScreenLine line, string firstWord, string lastWord, int maxGap)
        {
            int firstIndex = -1;
            int lastIndex = -1;
            for (int i = 0; i < line.Words.Count; i++)
            {
                if (firstIndex < 0 && TextEquals(line.Words[i].Text, firstWord))
                    firstIndex = i;
                else if (firstIndex >= 0 && TextEquals(line.Words[i].Text, lastWord))
                {
                    lastIndex = i;
                    break;
                }
            }
            if (firstIndex < 0 || lastIndex < 0 || lastIndex - firstIndex > maxGap)
                return Rectangle.Empty;

            int left = int.MaxValue;
            int top = int.MaxValue;
            int right = int.MinValue;
            int bottom = int.MinValue;
            for (int i = firstIndex; i <= lastIndex; i++)
            {
                Rectangle word = line.Words[i].Bounds;
                left = Math.Min(left, word.Left);
                top = Math.Min(top, word.Top);
                right = Math.Max(right, word.Right);
                bottom = Math.Max(bottom, word.Bottom);
            }
            return Rectangle.FromLTRB(left, top, right, bottom);
        }

        static bool IsBottomBand(Rectangle bounds, Rectangle window)
        {
            int y = bounds.Top + bounds.Height / 2;
            return y >= window.Top + (int)(window.Height * 0.86);
        }

        static bool IsCenterBand(Rectangle bounds, Rectangle window)
        {
            int y = bounds.Top + bounds.Height / 2;
            return y >= window.Top + (int)(window.Height * 0.30)
                && y < window.Top + (int)(window.Height * 0.86);
        }

        static bool ContainsWord(ScreenLine line, string word)
        {
            foreach (ScreenWord item in line.Words)
            {
                if (TextEquals(item.Text, word))
                    return true;
            }
            return false;
        }

        static bool TextEquals(string left, string right)
        {
            return string.Equals(CleanWord(left), right, StringComparison.OrdinalIgnoreCase);
        }

        static string CleanWord(string value)
        {
            if (value == null)
                return "";
            return value.Trim().Trim('.', ',', '!', '?', ':', ';', '"', '\'');
        }

        static Point Center(Rectangle bounds)
        {
            return new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        }

        void SetBusy(bool value)
        {
            Action apply = delegate
            {
                busy = value;
                RefreshButtons();
            };
            if (InvokeRequired)
                Invoke(apply);
            else
                apply();
        }

        void RefreshButtons()
        {
            if (IsDisposed)
                return;
            bool running = SimScreen.IsRunning();
            startButton.Enabled = running && !busy && !aligning;
            flightButton.Enabled = running && !busy && !aligning;
            readyButton.Enabled = running && !busy && !aligning;
            quitButton.Enabled = running && !busy && !aligning;
            alignLink.Enabled = running && !busy && !aligning;
            hookBox.Enabled = !busy && !aligning;
        }

        void SetStatus(string text, StatusKind kind, int holdSeconds)
        {
            Color color = Color.FromArgb(28, 30, 34);
            if (kind == StatusKind.Ok)
                color = Color.FromArgb(0, 96, 64);
            else if (kind == StatusKind.Warn)
                color = Color.FromArgb(140, 72, 0);

            Action apply = delegate
            {
                statusLabel.Text = text;
                statusLabel.ForeColor = color;
                statusHoldUntil = holdSeconds <= 0
                    ? DateTime.MinValue
                    : DateTime.UtcNow.AddSeconds(holdSeconds);
            };
            if (IsDisposed)
                return;
            if (InvokeRequired)
                BeginInvoke(apply);
            else
                apply();
        }

        static RoundButton MakeButton(string text, int x, int y, int width, int height, float fontSize, Color face, Color hover, Color pressed)
        {
            RoundButton button = new RoundButton();
            button.Text = text;
            button.SetBounds(x, y, width, height);
            button.Font = new Font("Segoe UI", fontSize, FontStyle.Bold);
            button.FaceColor = face;
            button.HoverColor = hover;
            button.PressedColor = pressed;
            button.ForeColor = Color.White;
            return button;
        }

        static Icon MakeIcon()
        {
            Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MSFS2024Start.controller.ico");
            if (stream != null)
                return new Icon(stream);
            return SystemIcons.Application;
        }
    }

    sealed class RoundButton : Control, IButtonControl
    {
        const int Radius = 16;
        const int Shadow = 12;

        bool hover;
        bool pressed;
        bool isDefault;

        public Color FaceColor = Color.FromArgb(0, 122, 204);
        public Color HoverColor = Color.FromArgb(32, 152, 232);
        public Color PressedColor = Color.FromArgb(0, 96, 168);
        public DialogResult DialogResult { get; set; }

        public RoundButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            ForeColor = Color.White;
            BackColor = Color.FromArgb(128, 132, 138);
        }

        public void NotifyDefault(bool value)
        {
            isDefault = value;
            Invalidate();
        }

        public void PerformClick()
        {
            if (Enabled)
                OnClick(EventArgs.Empty);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hover = false;
            pressed = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                pressed = true;
                Invalidate();
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                pressed = false;
                Invalidate();
            }
            base.OnMouseUp(e);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            Invalidate();
            base.OnEnabledChanged(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Parent == null ? BackColor : Parent.BackColor);

            Rectangle face = new Rectangle(3, 2, Width - Shadow - 8, Height - Shadow - 8);
            if (face.Width < 8 || face.Height < 8)
                return;

            Color fill = FaceColor;
            if (!Enabled)
                fill = Blend(FaceColor, Color.FromArgb(48, 52, 60), 0.55f);
            else if (pressed)
                fill = PressedColor;
            else if (hover)
                fill = HoverColor;

            for (int layer = Shadow; layer >= 1; layer--)
            {
                Rectangle shadow = face;
                shadow.Offset(2 + layer / 3, 3 + layer);
                int alpha = 36 + (Shadow - layer) * 14;
                using (GraphicsPath path = RoundRect(shadow, Radius))
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(Math.Min(alpha, 210), 0, 0, 0)))
                    g.FillPath(brush, path);
            }

            using (GraphicsPath path = RoundRect(face, Radius))
            {
                Color top = Lighten(fill, pressed ? 8 : 32);
                using (LinearGradientBrush brush = new LinearGradientBrush(face, top, fill, LinearGradientMode.Vertical))
                    g.FillPath(brush, path);
                Color edge = isDefault || hover ? Color.FromArgb(90, 255, 255, 255) : Color.FromArgb(45, 255, 255, 255);
                using (Pen pen = new Pen(edge, 1f))
                    g.DrawPath(pen, path);
            }

            TextRenderer.DrawText(
                g,
                Text,
                Font,
                face,
                Enabled ? ForeColor : Color.FromArgb(170, 176, 186),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        static GraphicsPath RoundRect(Rectangle bounds, int radius)
        {
            int diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        static Color Lighten(Color color, int amount)
        {
            return Color.FromArgb(
                color.A,
                Math.Min(255, color.R + amount),
                Math.Min(255, color.G + amount),
                Math.Min(255, color.B + amount));
        }

        static Color Blend(Color from, Color to, float amount)
        {
            return Color.FromArgb(
                (int)(from.R + (to.R - from.R) * amount),
                (int)(from.G + (to.G - from.G) * amount),
                (int)(from.B + (to.B - from.B) * amount));
        }
    }

    enum StatusKind
    {
        Normal,
        Ok,
        Warn
    }

    enum StartResult
    {
        StillThere,
        InMenu,
        Unknown
    }

    sealed class ScreenWord
    {
        public string Text;
        public Rectangle Bounds;
    }

    sealed class ScreenLine
    {
        public string Text;
        public Rectangle Bounds;
        public List<ScreenWord> Words;
    }

    static class SimScreen
    {
        public static bool IsRunning()
        {
            return System.Diagnostics.Process.GetProcessesByName("FlightSimulator2024").Length > 0;
        }

        public static IntPtr FindWindow()
        {
            System.Diagnostics.Process[] processes = System.Diagnostics.Process.GetProcessesByName("FlightSimulator2024");
            IntPtr best = IntPtr.Zero;
            int bestArea = 0;
            foreach (System.Diagnostics.Process process in processes)
            {
                try
                {
                    IntPtr hwnd = process.MainWindowHandle;
                    if (hwnd == IntPtr.Zero)
                        continue;
                    Rectangle bounds = ClientBounds(hwnd);
                    int area = Math.Max(0, bounds.Width) * Math.Max(0, bounds.Height);
                    if (area > bestArea)
                    {
                        bestArea = area;
                        best = hwnd;
                    }
                }
                catch (Exception ex)
                {
                    Log.Write(ex.Message);
                }
            }
            return best;
        }

        public static Rectangle ClientBounds(IntPtr hwnd)
        {
            Native.RECT client;
            if (!Native.GetClientRect(hwnd, out client))
                return Rectangle.Empty;
            Native.POINT origin;
            origin.X = 0;
            origin.Y = 0;
            if (!Native.ClientToScreen(hwnd, ref origin))
                return Rectangle.Empty;
            int width = client.Right - client.Left;
            int height = client.Bottom - client.Top;
            if (width <= 0 || height <= 0)
                return Rectangle.Empty;
            return new Rectangle(origin.X, origin.Y, width, height);
        }

        public static void Focus(IntPtr hwnd)
        {
            IntPtr foreground = Native.GetForegroundWindow();
            uint foregroundThread = Native.GetWindowThreadProcessId(foreground, IntPtr.Zero);
            uint targetThread = Native.GetWindowThreadProcessId(hwnd, IntPtr.Zero);
            uint appThread = Native.GetCurrentThreadId();
            bool attachedForeground = false;
            bool attachedTarget = false;
            try
            {
                if (appThread != foregroundThread)
                    attachedForeground = Native.AttachThreadInput(appThread, foregroundThread, true);
                if (appThread != targetThread)
                    attachedTarget = Native.AttachThreadInput(appThread, targetThread, true);
                Native.ShowWindow(hwnd, Native.SW_SHOW);
                Native.SetForegroundWindow(hwnd);
                Native.BringWindowToTop(hwnd);
            }
            finally
            {
                if (attachedTarget)
                    Native.AttachThreadInput(appThread, targetThread, false);
                if (attachedForeground)
                    Native.AttachThreadInput(appThread, foregroundThread, false);
            }
        }

        public static List<ScreenLine> Read(IntPtr hwnd)
        {
            List<ScreenLine> lines = new List<ScreenLine>();
            Rectangle bounds = ClientBounds(hwnd);
            if (bounds.Width < 10 || bounds.Height < 10)
                return lines;

            string temp = Path.Combine(Path.GetTempPath(), "msfs2024-start-ocr.png");
            Bitmap bitmap = new Bitmap(bounds.Width, bounds.Height);
            try
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                    graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bitmap.Size);
                bitmap.Save(temp, System.Drawing.Imaging.ImageFormat.Png);
            }
            finally
            {
                bitmap.Dispose();
            }

            try
            {
                OcrEngine engine = OcrEngine.TryCreateFromUserProfileLanguages();
                if (engine == null)
                {
                    Log.Write("Windows OCR engine is not available.");
                    return lines;
                }

                StorageFile file = WinRt.Wait(StorageFile.GetFileFromPathAsync(temp));
                using (IRandomAccessStream stream = WinRt.Wait(file.OpenAsync(FileAccessMode.Read)))
                {
                    BitmapDecoder decoder = WinRt.Wait(BitmapDecoder.CreateAsync(stream));
                    SoftwareBitmap software = WinRt.Wait(decoder.GetSoftwareBitmapAsync());
                    try
                    {
                        if (software.BitmapPixelFormat != BitmapPixelFormat.Bgra8
                            || software.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
                        {
                            SoftwareBitmap converted = SoftwareBitmap.Convert(
                                software, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                            software.Dispose();
                            software = converted;
                        }

                        OcrResult result = WinRt.Wait(engine.RecognizeAsync(software));
                        foreach (OcrLine ocrLine in result.Lines)
                        {
                            ScreenLine line = new ScreenLine();
                            line.Text = ocrLine.Text;
                            line.Words = new List<ScreenWord>();
                            int left = int.MaxValue;
                            int top = int.MaxValue;
                            int right = int.MinValue;
                            int bottom = int.MinValue;
                            foreach (OcrWord ocrWord in ocrLine.Words)
                            {
                                Rect rect = ocrWord.BoundingRect;
                                Rectangle wordBounds = new Rectangle(
                                    bounds.Left + (int)Math.Round(rect.X),
                                    bounds.Top + (int)Math.Round(rect.Y),
                                    Math.Max(1, (int)Math.Round(rect.Width)),
                                    Math.Max(1, (int)Math.Round(rect.Height)));
                                ScreenWord word = new ScreenWord();
                                word.Text = ocrWord.Text;
                                word.Bounds = wordBounds;
                                line.Words.Add(word);
                                left = Math.Min(left, wordBounds.Left);
                                top = Math.Min(top, wordBounds.Top);
                                right = Math.Max(right, wordBounds.Right);
                                bottom = Math.Max(bottom, wordBounds.Bottom);
                            }
                            if (line.Words.Count == 0)
                                continue;
                            line.Bounds = Rectangle.FromLTRB(left, top, right, bottom);
                            lines.Add(line);
                        }
                    }
                    finally
                    {
                        if (software != null)
                            software.Dispose();
                    }
                }
            }
            finally
            {
                try
                {
                    File.Delete(temp);
                }
                catch (Exception)
                {
                }
            }
            return lines;
        }

        public static void Click(Point screen)
        {
            int ax;
            int ay;
            ToAbsolute(screen, out ax, out ay);
            uint move = Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK;
            Native.SendMouse(move, ax, ay);
            Native.SetCursorPos(screen.X, screen.Y);
            Thread.Sleep(220);
            Native.SendMouse(move | Native.MOUSEEVENTF_LEFTDOWN, ax, ay);
            Thread.Sleep(200);
            Native.SendMouse(move | Native.MOUSEEVENTF_LEFTUP, ax, ay);
        }

        public static void PressButton(Point screen)
        {
            int ax;
            int ay;
            ToAbsolute(screen, out ax, out ay);
            uint move = Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK;
            Native.SendMouse(Native.MOUSEEVENTF_LEFTUP, 0, 0);
            Native.SendMouse(move, ax, ay);
            Native.SetCursorPos(screen.X, screen.Y);
            Thread.Sleep(300);
            Native.SendMouse(Native.MOUSEEVENTF_LEFTDOWN, 0, 0);
            Thread.Sleep(400);
            Native.SendMouse(Native.MOUSEEVENTF_LEFTUP, 0, 0);
        }

        public static void ClickWindow(IntPtr hwnd, Point screen)
        {
            Native.POINT origin;
            origin.X = 0;
            origin.Y = 0;
            if (!Native.ClientToScreen(hwnd, ref origin))
                return;
            int cx = screen.X - origin.X;
            int cy = screen.Y - origin.Y;
            IntPtr packed = (IntPtr)((cy << 16) | (cx & 0xFFFF));
            Native.SendMessage(hwnd, Native.WM_MOUSEMOVE, IntPtr.Zero, packed);
            Thread.Sleep(80);
            Native.SendMessage(hwnd, Native.WM_LBUTTONDOWN, (IntPtr)0x0001, packed);
            Thread.Sleep(160);
            Native.SendMessage(hwnd, Native.WM_LBUTTONUP, IntPtr.Zero, packed);
        }

        public static void TapEnter()
        {
            Native.TapVirtualKey(0x0D);
        }

        public static void TapCtrlEnter()
        {
            Native.TapChord(0x11, 0x0D);
        }

        static void ToAbsolute(Point screen, out int ax, out int ay)
        {
            int left = Native.GetSystemMetrics(76);
            int top = Native.GetSystemMetrics(77);
            int width = Native.GetSystemMetrics(78);
            int height = Native.GetSystemMetrics(79);
            if (width < 2)
                width = 2;
            if (height < 2)
                height = 2;
            ax = (int)Math.Round((screen.X - left) * 65535.0 / (width - 1));
            ay = (int)Math.Round((screen.Y - top) * 65535.0 / (height - 1));
        }
    }

    static class WinRt
    {
        public static T Wait<T>(IAsyncOperation<T> operation)
        {
            ManualResetEvent done = new ManualResetEvent(false);
            Exception error = null;
            T result = default(T);
            operation.Completed = delegate(IAsyncOperation<T> source, AsyncStatus status)
            {
                try
                {
                    if (status == AsyncStatus.Completed)
                        result = source.GetResults();
                    else if (status == AsyncStatus.Error)
                        error = source.ErrorCode;
                    else
                        error = new Exception("The text recognition was canceled.");
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                finally
                {
                    done.Set();
                }
            };
            done.WaitOne();
            if (error != null)
                throw error;
            return result;
        }
    }

    static class StartupHook
    {
        const string AddonName = "MSFS 2024 Start";

        public static string SetEnabled(bool enabled, string exePath)
        {
            string[] files = ConfigFiles();
            if (files.Length == 0)
                return "MSFS 2024's config folder was not found. Start the simulator once, then turn this on again.";

            foreach (string file in files)
            {
                string error = UpdateFile(file, enabled, exePath);
                if (error != null)
                    return error;
            }
            return null;
        }

        static string UpdateFile(string path, bool enabled, string exePath)
        {
            try
            {
                string xml = File.Exists(path) ? File.ReadAllText(path) : NewDocument();
                if (!WellFormed(xml))
                    return "Left " + path + " unchanged because it is not valid XML.";

                if (enabled && AlreadyEnabled(xml, exePath))
                    return null;

                string backup = path + ".bak-before-msfsstart";
                if (File.Exists(path) && !File.Exists(backup))
                    File.Copy(path, backup);

                xml = RemoveAddon(xml);
                if (enabled)
                {
                    string endTag = "</SimBase.Document>";
                    int end = xml.LastIndexOf(endTag, StringComparison.Ordinal);
                    if (end < 0)
                        return "Could not update " + path + ".";
                    xml = xml.Insert(end, Block(exePath) + Environment.NewLine);
                }

                if (!WellFormed(xml))
                    return "Did not write " + path + " because the result was not valid XML.";

                Encoding encoding = xml.IndexOf("Windows-1252", StringComparison.OrdinalIgnoreCase) >= 0
                    ? Encoding.GetEncoding(1252)
                    : new UTF8Encoding(false);
                File.WriteAllText(path, xml, encoding);
                Log.Write((enabled ? "Enabled" : "Removed") + " startup hook in " + path);
                return null;
            }
            catch (Exception ex)
            {
                Log.Write(ex.ToString());
                return "Could not update the MSFS startup list: " + ex.Message;
            }
        }

        static bool AlreadyEnabled(string xml, string exePath)
        {
            string block = FindBlock(xml);
            if (block == null)
                return false;
            if (block.IndexOf("<Disabled>true</Disabled>", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            string pathTag = "<Path>" + SecurityElement.Escape(exePath) + "</Path>";
            return block.IndexOf(pathTag, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static string FindBlock(string xml)
        {
            string marker = "<Name>" + AddonName + "</Name>";
            int nameAt = xml.IndexOf(marker, StringComparison.Ordinal);
            if (nameAt < 0)
                return null;
            int start = xml.LastIndexOf("<Launch.Addon>", nameAt, StringComparison.Ordinal);
            int end = xml.IndexOf("</Launch.Addon>", nameAt, StringComparison.Ordinal);
            if (start < 0 || end < 0)
                return null;
            return xml.Substring(start, end - start);
        }

        static string RemoveAddon(string xml)
        {
            string marker = "<Name>" + AddonName + "</Name>";
            int nameAt = 0;
            while ((nameAt = xml.IndexOf(marker, nameAt, StringComparison.Ordinal)) >= 0)
            {
                int start = xml.LastIndexOf("<Launch.Addon>", nameAt, StringComparison.Ordinal);
                int end = xml.IndexOf("</Launch.Addon>", nameAt, StringComparison.Ordinal);
                if (start < 0 || end < 0)
                    break;
                end += "</Launch.Addon>".Length;
                if (end < xml.Length && xml[end] == '\r')
                    end++;
                if (end < xml.Length && xml[end] == '\n')
                    end++;
                xml = xml.Remove(start, end - start);
                nameAt = start;
            }
            return xml;
        }

        static string Block(string exePath)
        {
            return "    <Launch.Addon>" + Environment.NewLine
                + "        <Name>" + AddonName + "</Name>" + Environment.NewLine
                + "        <Disabled>False</Disabled>" + Environment.NewLine
                + "        <ManualLoad>False</ManualLoad>" + Environment.NewLine
                + "        <Path>" + SecurityElement.Escape(exePath) + "</Path>" + Environment.NewLine
                + "        <CommandLine></CommandLine>" + Environment.NewLine
                + "        <NewConsole>False</NewConsole>" + Environment.NewLine
                + "    </Launch.Addon>";
        }

        static string NewDocument()
        {
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + Environment.NewLine
                + "<SimBase.Document Type=\"SimConnect\" version=\"1,0\">" + Environment.NewLine
                + "    <Descr>SimConnect</Descr>" + Environment.NewLine
                + "    <Filename>SimConnect.xml</Filename>" + Environment.NewLine
                + "    <Disabled>False</Disabled>" + Environment.NewLine
                + "</SimBase.Document>" + Environment.NewLine;
        }

        static bool WellFormed(string xml)
        {
            try
            {
                XmlDocument document = new XmlDocument();
                document.LoadXml(xml);
                return document.DocumentElement != null
                    && document.DocumentElement.Name == "SimBase.Document";
            }
            catch (Exception)
            {
                return false;
            }
        }

        static string[] ConfigFiles()
        {
            List<string> files = new List<string>();
            string storeDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Packages\Microsoft.Limitless_8wekyb3d8bbwe\LocalCache");
            string steamDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft Flight Simulator 2024");
            if (Directory.Exists(storeDir))
                files.Add(Path.Combine(storeDir, "exe.xml"));
            if (Directory.Exists(steamDir))
                files.Add(Path.Combine(steamDir, "exe.xml"));
            return files.ToArray();
        }
    }

    sealed class Settings
    {
        public bool AutoStart = true;
        public bool HasStart;
        public bool HasQuit;
        public double StartX = 0.90;
        public double StartY = 0.95;
        public double QuitX = 0.20;
        public double QuitY = 0.95;

        static string FilePath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "MSFS2024Start",
                    "settings.txt");
            }
        }

        public static Settings Load()
        {
            Settings settings = new Settings();
            if (!File.Exists(FilePath))
                return settings;
            foreach (string raw in File.ReadAllLines(FilePath))
            {
                int split = raw.IndexOf('=');
                if (split <= 0)
                    continue;
                string key = raw.Substring(0, split).Trim();
                string value = raw.Substring(split + 1).Trim();
                if (key == "autoStart")
                    settings.AutoStart = value == "1";
                else if (key == "hasStart")
                    settings.HasStart = value == "1";
                else if (key == "hasQuit")
                    settings.HasQuit = value == "1";
                else if (key == "startX")
                    settings.StartX = Parse(value, settings.StartX);
                else if (key == "startY")
                    settings.StartY = Parse(value, settings.StartY);
                else if (key == "quitX")
                    settings.QuitX = Parse(value, settings.QuitX);
                else if (key == "quitY")
                    settings.QuitY = Parse(value, settings.QuitY);
            }
            return settings;
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            StringBuilder text = new StringBuilder();
            text.AppendLine("autoStart=" + (AutoStart ? "1" : "0"));
            text.AppendLine("hasStart=" + (HasStart ? "1" : "0"));
            text.AppendLine("hasQuit=" + (HasQuit ? "1" : "0"));
            text.AppendLine("startX=" + StartX.ToString("0.0000"));
            text.AppendLine("startY=" + StartY.ToString("0.0000"));
            text.AppendLine("quitX=" + QuitX.ToString("0.0000"));
            text.AppendLine("quitY=" + QuitY.ToString("0.0000"));
            File.WriteAllText(FilePath, text.ToString());
        }

        static double Parse(string value, double fallback)
        {
            double parsed;
            if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out parsed))
                return parsed;
            return fallback;
        }
    }

    static class Log
    {
        public static void Write(string message)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "MSFS2024Start");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "log.txt");
                FileInfo info = new FileInfo(path);
                if (info.Exists && info.Length > 200000)
                    info.Delete();
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + message + Environment.NewLine);
            }
            catch (Exception)
            {
            }
        }
    }

    static class Native
    {
        public const int DWMWA_BORDER_COLOR = 34;
        public const int DWMWA_CAPTION_COLOR = 35;
        public const int DWMWA_TEXT_COLOR = 36;
        public const int WH_MOUSE_LL = 14;
        public const int WM_MOUSEMOVE = 0x0200;
        public const int WM_LBUTTONDOWN = 0x0201;
        public const int WM_LBUTTONUP = 0x0202;
        public const int SW_SHOW = 5;
        public const uint MOUSEEVENTF_MOVE = 0x0001;
        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;
        public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
        public const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
        const uint KEYEVENTF_KEYUP = 0x0002;
        const uint INPUT_MOUSE = 0;

        public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        // 64-bit INPUT: the mouse union starts 8 bytes in.
        [StructLayout(LayoutKind.Explicit)]
        struct INPUT
        {
            [FieldOffset(0)]
            public uint type;
            [FieldOffset(8)]
            public MOUSEINPUT mi;
        }

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);

        [DllImport("user32.dll")]
        static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll")]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        public static void SetPerMonitorDpiAware()
        {
            try
            {
                if (!SetProcessDpiAwarenessContext(new IntPtr(-4)))
                    SetProcessDPIAware();
            }
            catch (Exception)
            {
                try
                {
                    SetProcessDPIAware();
                }
                catch (Exception)
                {
                }
            }
        }

        public static void SendMouse(uint flags, int dx, int dy)
        {
            INPUT input = new INPUT();
            input.type = INPUT_MOUSE;
            input.mi.dx = dx;
            input.mi.dy = dy;
            input.mi.dwFlags = flags;
            INPUT[] inputs = new INPUT[1];
            inputs[0] = input;
            uint sent = SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
            if (sent != 1)
                Log.Write("Mouse click was not sent. Win32 error " + Marshal.GetLastWin32Error());
        }

        public static void TapVirtualKey(byte virtualKey)
        {
            keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
            Thread.Sleep(80);
            keybd_event(virtualKey, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        public static void TapChord(byte modifier, byte key)
        {
            keybd_event(modifier, 0, 0, UIntPtr.Zero);
            Thread.Sleep(40);
            keybd_event(key, 0, 0, UIntPtr.Zero);
            Thread.Sleep(70);
            keybd_event(key, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            Thread.Sleep(30);
            keybd_event(modifier, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
    }
}
