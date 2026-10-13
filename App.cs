using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SearchLayout
{
    internal interface ISearchController : IDisposable
    {
        bool Enabled { get; set; }
        int Count { get; }
        event Action<string> Faulted;
    }
    internal sealed class SearchController : ISearchController
    {
        readonly CorrectionEngine engine;
        public SearchController() { engine = new CorrectionEngine(AppCatalog.ReadInstalled(), AppCatalog.ReadInstalled); engine.Faulted += message => { var handler = Faulted; if (handler != null) handler(message); }; }
        public bool Enabled { get { return engine.Enabled; } set { engine.Enabled = value; } }
        public int Count { get { return engine.Count; } }
        public event Action<string> Faulted;
        public void Dispose() { engine.Dispose(); }
    }
    internal interface ITraySurface : IDisposable
    {
        event Action OpenRequested;
        event Action ToggleRequested;
        event Action ExitRequested;
        bool Visible { get; set; }
        void Update(bool canToggle, bool enabled, string status);
    }
    internal sealed class WindowsTraySurface : ITraySurface
    {
        readonly NotifyIcon icon;
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly ToolStripMenuItem toggle;
        readonly Icon image;
        public event Action OpenRequested;
        public event Action ToggleRequested;
        public event Action ExitRequested;
        public WindowsTraySurface()
        {
            image = UiPalette.LoadIcon();
            var open = new ToolStripMenuItem("Открыть");
            toggle = new ToolStripMenuItem("Включить");
            var exit = new ToolStripMenuItem("Завершить");
            open.Click += (s, e) => Raise(OpenRequested);
            toggle.Click += (s, e) => Raise(ToggleRequested);
            exit.Click += (s, e) => Raise(ExitRequested);
            menu.Items.AddRange(new ToolStripItem[] { open, toggle, new ToolStripSeparator(), exit });
            icon = new NotifyIcon { Icon = image, ContextMenuStrip = menu, Text = "LayoutSeek: выключено", Visible = false };
            icon.DoubleClick += (s, e) => Raise(OpenRequested);
        }
        static void Raise(Action handler) { if (handler != null) handler(); }
        public bool Visible { get { return icon.Visible; } set { icon.Visible = value; } }
        public void Update(bool canToggle, bool enabled, string status)
        {
            toggle.Enabled = canToggle; toggle.Text = enabled ? "Выключить" : "Включить";
            icon.Text = "LayoutSeek: " + status;
        }
        public void Dispose() { icon.Visible = false; icon.Dispose(); menu.Dispose(); image.Dispose(); }
    }
    internal sealed class ReadableCheckBox : CheckBox
    {
        protected override AccessibleObject CreateAccessibilityInstance() { return new ActionAccessibleObject(this); }
        sealed class ActionAccessibleObject : CheckBoxAccessibleObject
        {
            readonly ReadableCheckBox owner;
            public ActionAccessibleObject(ReadableCheckBox owner) : base(owner) { this.owner = owner; }
            // Route the legacy UIA/MSAA default action through the same click as mouse and Space.
            public override void DoDefaultAction() { if (owner.Enabled) owner.OnClick(EventArgs.Empty); }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Enabled) return;
            // The stock disabled label can ignore ForeColor on a contrast palette.
            int glyph = CheckBoxRenderer.GetGlyphSize(e.Graphics, System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedDisabled).Width;
            var textArea = new Rectangle(glyph + 3, 0, Math.Max(0, Width - glyph - 3), Height);
            using (var background = new SolidBrush(BackColor)) e.Graphics.FillRectangle(background, textArea);
            TextRenderer.DrawText(e.Graphics, Text, Font, textArea, ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsClipping);
        }
    }
    internal sealed class UiPalette
    {
        public Color Surface, Panel, Text, Secondary, Accent, AccentText, Border;
        public bool HighContrast;
        public static UiPalette FromSystem()
        {
            bool contrast = SystemInformation.HighContrast;
            Color accent = SystemColors.Highlight;
            Color ink = Contrast(accent, Color.White) >= Contrast(accent, Color.Black) ? Color.White : Color.Black;
            return new UiPalette { HighContrast = contrast, Surface = SystemColors.Window, Panel = contrast ? SystemColors.Window : Color.FromArgb(245, 247, 250), Text = SystemColors.WindowText, Secondary = contrast ? SystemColors.WindowText : Color.FromArgb(70, 77, 88), Accent = accent, AccentText = contrast ? SystemColors.HighlightText : ink, Border = contrast ? SystemColors.WindowText : Color.FromArgb(203, 210, 220) };
        }
        public static double Contrast(Color a, Color b)
        {
            double x = Luminance(a), y = Luminance(b); return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
        }
        static double Luminance(Color c) { return .2126 * Channel(c.R) + .7152 * Channel(c.G) + .0722 * Channel(c.B); }
        static double Channel(byte b) { double x = b / 255.0; return x <= .04045 ? x / 12.92 : Math.Pow((x + .055) / 1.055, 2.4); }
        public static Icon LoadIcon()
        {
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? (Icon)SystemIcons.Application.Clone(); }
            catch { return (Icon)SystemIcons.Application.Clone(); }
        }
    }
    public sealed class MainForm : Form
    {
        readonly Func<ISearchController> factory;
        readonly ITraySurface tray;
        readonly bool renderOnly;
        readonly UiPalette fixedPalette;
        readonly EventWaitHandle showSignal;
        readonly IStartupService startupService;
        readonly bool startupLaunch;
        readonly object pendingGate = new object();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly TableLayoutPanel layout = new TableLayoutPanel();
        readonly TableLayoutPanel statusPanel = new TableLayoutPanel();
        readonly TableLayoutPanel actions = new TableLayoutPanel();
        readonly Label heading = new Label();
        readonly Label intro = new Label();
        readonly Label state = new Label();
        readonly Label stateHelp = new Label();
        readonly Label example = new Label();
        readonly Label explanation = new Label();
        readonly Label note = new Label();
        readonly Label details = new Label();
        readonly Button primary = new Button();
        readonly Button toTray = new Button();
        readonly LinkLabel more = new LinkLabel();
        readonly CheckBox startupOption = new ReadableCheckBox();
        readonly LinkLabel startupStatus = new LinkLabel();
        readonly Icon windowIcon;
        ISearchController controller;
        LoadResult pending;
        string pendingFault;
        volatile bool stopped;
        bool initializing, initializationFailed, detailsOpen, inTray, fitting;
        bool startupLaunchHandled, enableAfterLoad, updatingStartupPresentation;
        Task<StartupResult> startupTask;
        bool startupWriting;
        StartupResult startupResult;
        string fault;
        internal UiPalette Palette { get; private set; }
        internal bool InTray { get { return inTray; } }
        internal bool Loading { get { return initializing; } }
        internal string StatusText { get { return state.Text; } }
        internal string PrimaryText { get { return primary.Text; } }
        internal bool PrimaryEnabled { get { return primary.Enabled; } }
        internal bool DetailsOpen { get { return detailsOpen; } }
        internal bool Stopped { get { return stopped; } }
        internal bool StartupBusy { get { return startupTask != null; } }
        internal bool StartupChecked { get { return startupOption.CheckState == CheckState.Checked; } }
        internal bool StartupUnknown { get { return startupOption.CheckState == CheckState.Indeterminate; } }
        internal bool StartupCanChange { get { return startupOption.Enabled; } }
        internal string StartupStatusText { get { return startupStatus.Text; } }
        sealed class LoadResult { public ISearchController Controller; public bool Failed; }

        public MainForm() : this(null, false) { }
        public MainForm(EventWaitHandle showSignal) : this(showSignal, false) { }
        public MainForm(EventWaitHandle showSignal, bool startupLaunch) : this(() => new SearchController(), new WindowsTraySurface(), false, null, new StartupService(), startupLaunch) { this.showSignal = showSignal; }
        internal MainForm(Func<ISearchController> factory, ITraySurface tray, bool renderOnly, UiPalette palette, IStartupService startupService, bool startupLaunch = false)
        {
            this.factory = factory; this.tray = tray; this.renderOnly = renderOnly; fixedPalette = palette;
            this.startupService = startupService; this.startupLaunch = startupLaunch; enableAfterLoad = startupLaunch;
            Text = "LayoutSeek";
            Font = new Font("Segoe UI", 10F);
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96F, 96F);
            ClientSize = new Size(520, 440); FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen; AutoScroll = true;
            windowIcon = UiPalette.LoadIcon(); Icon = windowIcon;
            layout.Name = "MainLayout"; layout.Dock = DockStyle.Top; layout.AutoSize = true; layout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            layout.ColumnCount = 1; layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); layout.Padding = new Padding(24); layout.Margin = Padding.Empty;
            ConfigureLabel(heading, "LayoutSeek", 18F, FontStyle.Bold, 4);
            ConfigureLabel(intro, "Исправление раскладки в поиске Windows.", 10.5F, FontStyle.Regular, 20);
            statusPanel.Name = "CorrectionStatus"; statusPanel.AutoSize = true; statusPanel.Dock = DockStyle.Fill; statusPanel.Padding = new Padding(16);
            statusPanel.Margin = new Padding(0, 0, 0, 20); statusPanel.ColumnCount = 2;
            statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 144));
            ConfigureLabel(state, "Подготовка", 11.5F, FontStyle.Bold, 0); state.Name = "StatusSummary"; state.Margin = new Padding(0, 0, 12, 0); state.Anchor = AnchorStyles.Left;
            ConfigureLabel(stateHelp, "Читаем список приложений на этом ПК.", 10F, FontStyle.Regular, 0); stateHelp.Margin = new Padding(0, 10, 0, 0);
            primary.Name = "PrimaryAction"; primary.Size = new Size(144, 40); primary.Dock = DockStyle.Fill; primary.Margin = Padding.Empty; primary.TabIndex = 0;
            primary.AccessibleRole = AccessibleRole.PushButton; primary.Click += (s, e) => PerformPrimaryAction();
            statusPanel.Controls.Add(state, 0, 0); statusPanel.Controls.Add(primary, 1, 0); statusPanel.Controls.Add(stateHelp, 0, 1); statusPanel.SetColumnSpan(stateHelp, 2);
            ConfigureLabel(example, "ср  →  ch", 22F, FontStyle.Regular, 4); example.AccessibleName = "Пример: ср исправляется на ch";
            ConfigureLabel(explanation, "Подходящие приложения и выбор результата остаются в обычном поиске Windows.", 10.5F, FontStyle.Regular, 18);
            ConfigureLabel(note, "Локально · без истории запросов\nЭкспериментальная версия", 9.5F, FontStyle.Regular, 16);
            startupOption.Name = "StartupOption"; startupOption.Text = "Запускать при входе в Windows";
            startupOption.AutoSize = true; startupOption.AutoCheck = false; startupOption.ThreeState = true; startupOption.Dock = DockStyle.Fill; startupOption.Margin = Padding.Empty; startupOption.TabIndex = 1;
            startupOption.AccessibleDescription = "После входа в Windows программа запускается в трее с включённым исправлением.";
            startupOption.CheckStateChanged += (s, e) => { if (!updatingStartupPresentation) ChangeStartup(); };
            // Click requests a change; the asynchronous result alone sets the displayed check state.
            startupOption.Click += (s, e) => ChangeStartup();
            ConfigureLabel(startupStatus, "Проверяем настройку автозапуска…", 9.5F, FontStyle.Regular, 16);
            startupStatus.Name = "StartupStatus"; startupStatus.Margin = new Padding(22, 3, 0, 16); startupStatus.TabIndex = 2;
            startupStatus.LinkBehavior = LinkBehavior.AlwaysUnderline; startupStatus.LinkClicked += (s, e) => BeginStartupRead();
            actions.Name = "WindowActions"; actions.ColumnCount = 2; actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100)); actions.AutoSize = true; actions.Dock = DockStyle.Fill; actions.Margin = Padding.Empty;
            more.Name = "DetailsLink"; more.Text = "Как работает и ограничения"; more.AutoSize = true; more.Anchor = AnchorStyles.Left; more.Margin = new Padding(0, 0, 12, 0); more.TabIndex = 3; more.LinkBehavior = LinkBehavior.AlwaysUnderline; more.LinkClicked += (s, e) => ToggleDetails();
            toTray.Name = "TrayAction"; toTray.Text = "В трей"; toTray.Size = new Size(100, 36); toTray.Dock = DockStyle.Fill; toTray.Margin = Padding.Empty; toTray.TabIndex = 4; toTray.AccessibleDescription = "Скрывает окно, оставляя выбранный режим работы. Крестик завершает программу."; toTray.Click += (s, e) => MoveToTray();
            actions.Controls.Add(more, 0, 0); actions.Controls.Add(toTray, 1, 0);
            ConfigureLabel(details, "Работает только в штатном поиске Windows. Использует установленные раскладки для исправления в английскую и из английской по физическим клавишам. Составной ввод, IME и символы без однозначного соответствия пропускаются.\n\nНазвания установленных приложений берутся из Windows. Каталог обновляется в фоне не чаще раза в минуту. Каталог и запрос остаются в памяти; запросы не сохраняются и не отправляются программой в сеть. У самого поиска Windows могут быть сетевые функции.\n\nПри спорном запросе из двух символов приоритет у английского: «ср» становится «ch». Продолжение может вернуть исходное слово, например «срочно».\n\nПеред заменой программа повторно проверяет поле поиска и текст запроса. Если проверка не пройдена, исправление пропускается. При неподтверждённом результате режим выключается. Поведение проверено на одном ПК с Windows 11 Enterprise LTSC 24H2; совместимость с другими сборками Windows ещё не подтверждена\n\n«В трей» скрывает окно. Крестик и «Завершить» останавливают программу. Автозапуск включается отдельным флажком и добавляет ярлык в папку автозагрузки текущего пользователя. Windows может отдельно отключить этот ярлык. Перед переносом или удалением папки программы выключите автозапуск", 10F, FontStyle.Regular, 0);
            details.Name = "DetailsText"; details.Margin = new Padding(0, 20, 0, 0); details.Visible = false;
            AddRow(heading); AddRow(intro); AddRow(statusPanel); AddRow(example); AddRow(explanation); AddRow(note); AddRow(startupOption); AddRow(startupStatus); AddRow(actions); AddRow(details);
            Controls.Add(layout);
            tray.OpenRequested += RestoreFromTray; tray.ToggleRequested += PerformPrimaryAction; tray.ExitRequested += RequestExit;
            timer.Interval = 200; timer.Tick += (s, e) => Pulse(); timer.Start();
            ApplyPalette(palette ?? UiPalette.FromSystem()); RefreshStartupPresentation(); RefreshPresentation();
        }
        void ConfigureLabel(Label label, string text, float size, FontStyle style, int after)
        {
            label.Text = text; label.Font = new Font("Segoe UI", size, style); label.AutoSize = true; label.Dock = DockStyle.Fill;
            label.Margin = new Padding(0, 0, 0, after); label.UseMnemonic = false; label.TabStop = false;
        }
        void AddRow(Control control) { int row = layout.RowCount++; layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.Controls.Add(control, 0, row); }
        protected override bool ShowWithoutActivation { get { return renderOnly || base.ShowWithoutActivation; } }
        protected override CreateParams CreateParams
        {
            get { var parameters = base.CreateParams; if (renderOnly) parameters.ExStyle |= 0x08000000 | 0x00000080; return parameters; }
        }
        protected override void SetVisibleCore(bool value)
        {
            if (value && startupLaunch && !startupLaunchHandled)
            {
                startupLaunchHandled = true;
                if (!IsHandleCreated) CreateHandle();
                MoveToTray(); BeginInitialization(); BeginStartupRead();
                base.SetVisibleCore(false); return;
            }
            base.SetVisibleCore(value);
        }
        protected override void OnShown(EventArgs e) { base.OnShown(e); BeginInitialization(); BeginStartupRead(); FitToContent(); }
        protected override void OnActivated(EventArgs e) { base.OnActivated(e); if (startupService != null) BeginStartupRead(); }
        internal void BeginInitialization()
        {
            if (stopped || initializing || controller != null) return;
            initializing = true; initializationFailed = false; fault = null; RefreshPresentation();
            var worker = new Thread(() =>
            {
                var result = new LoadResult();
                try { result.Controller = factory(); result.Controller.Enabled = false; }
                catch { result.Failed = true; if (result.Controller != null) { result.Controller.Dispose(); result.Controller = null; } }
                lock (pendingGate)
                {
                    if (!stopped) { pending = result; return; }
                }
                if (result.Controller != null) result.Controller.Dispose();
            }) { IsBackground = true, Name = "Search layout initialization" };
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();
        }
        internal void Pulse()
        {
            if (stopped) return;
            LoadResult result;
            lock (pendingGate) { result = pending; pending = null; }
            if (result != null)
            {
                initializing = false; initializationFailed = result.Failed;
                controller = result.Controller;
                if (controller != null)
                {
                    controller.Faulted += OnEngineFault;
                    if (enableAfterLoad) { enableAfterLoad = false; controller.Enabled = true; }
                }
            }
            if (startupTask != null && startupTask.IsCompleted)
            {
                try { startupResult = startupTask.GetAwaiter().GetResult() ?? StartupResult.Failed(); }
                catch { startupResult = StartupResult.Failed(); }
                startupTask = null; startupWriting = false; RefreshStartupPresentation();
            }
            string message = Interlocked.Exchange(ref pendingFault, null);
            if (message != null) { fault = message; if (controller != null) controller.Enabled = false; }
            RefreshPresentation();
            if (showSignal != null && showSignal.WaitOne(0)) RestoreFromTray();
        }
        void OnEngineFault(string message) { Interlocked.Exchange(ref pendingFault, message); }
        internal void BeginStartupRead()
        {
            if (stopped || startupTask != null) return;
            try { startupTask = startupService.ReadAsync(); }
            catch { startupTask = Task.FromResult(StartupResult.Failed()); }
            if (startupTask == null) startupTask = Task.FromResult(StartupResult.Failed());
            RefreshStartupPresentation();
        }
        internal void ChangeStartup()
        {
            if (stopped || startupWriting || startupResult == null || !startupResult.CanChange) return;
            // A read started by activation must not swallow the click that activated the window.
            // A write supersedes that read; its result is the only one observed by Pulse.
            startupWriting = true;
            try { startupTask = startupService.SetEnabledAsync(!startupResult.Enabled); }
            catch { startupTask = Task.FromResult(StartupResult.Failed()); }
            if (startupTask == null) startupTask = Task.FromResult(StartupResult.Failed());
            RefreshStartupPresentation();
        }
        void RefreshStartupPresentation()
        {
            updatingStartupPresentation = true;
            try { startupOption.CheckState = startupResult != null && startupResult.Available ? startupResult.Enabled ? CheckState.Checked : CheckState.Unchecked : CheckState.Indeterminate; }
            finally { updatingStartupPresentation = false; }
            startupOption.Enabled = !startupWriting && startupResult != null && startupResult.CanChange;
            bool retry = false; string text;
            if (startupWriting || startupResult == null) text = "Проверяем настройку автозапуска…";
            else if (!startupResult.Available) { text = "Не удалось проверить автозапуск."; retry = true; }
            else if (startupResult.Portable) text = startupResult.Enabled ? "Ярлык автозапуска добавлен. Windows может отдельно отключить его в настройках автозагрузки." : "Ярлык автозапуска не добавлен.";
            else if (startupResult.State == StartupState.DisabledByUser) { text = "Выключено в настройках Windows. Раздел «Приложения → Автозагрузка»."; retry = true; }
            else if (startupResult.State == StartupState.DisabledByPolicy) text = "Выключено правилами вашей организации.";
            else if (startupResult.State == StartupState.EnabledByPolicy) text = "Включено правилами вашей организации.";
            else text = startupResult.Enabled ? "При входе запустится в трее с включённым исправлением." : "При входе в Windows программа не запускается.";
            startupStatus.Links.Clear(); startupStatus.Text = retry ? text + " Проверить снова" : text;
            if (retry) startupStatus.Links.Add(text.Length + 1, "Проверить снова".Length);
            startupStatus.TabStop = retry;
            startupOption.AccessibleDescription = startupStatus.Text;
            if (IsHandleCreated) FitToContent();
        }
        internal void PerformPrimaryAction()
        {
            if (stopped || initializing) return;
            if (controller == null) { BeginInitialization(); return; }
            if (fault != null) { fault = null; controller.Enabled = true; }
            else controller.Enabled = !controller.Enabled;
            RefreshPresentation();
        }
        void RefreshPresentation()
        {
            bool ready = controller != null;
            bool enabled = ready && controller.Enabled && fault == null;
            if (initializing || (!ready && !initializationFailed))
            {
                state.Text = "Подготовка"; stateHelp.Text = "Читаем список приложений на этом ПК."; primary.Text = "Подождите"; primary.Enabled = false;
            }
            else if (initializationFailed)
            {
                state.Text = "Не удалось подготовить поиск"; stateHelp.Text = "Список приложений недоступен. Попробуйте загрузить его ещё раз."; primary.Text = "Повторить"; primary.Enabled = true;
            }
            else
            {
                state.Text = (fault != null ? "Остановлено" : enabled ? "Включено" : "Выключено") + " · Исправлено: " + controller.Count;
                stateHelp.Text = fault != null ? fault + " Проверьте запрос, затем включите снова." : enabled ? "Откройте поиск Windows и печатайте." : "Включите перед поиском приложения.";
                primary.Text = fault != null ? "Включить снова" : enabled ? "Выключить" : "Включить"; primary.Enabled = true;
            }
            primary.AccessibleName = primary.Text; primary.AccessibleDescription = state.Text + ". " + stateHelp.Text;
            tray.Update(ready && !initializing, enabled, initializationFailed || fault != null ? "нужна проверка" : initializing ? "подготовка" : enabled ? "включено" : "выключено");
            if (IsHandleCreated) FitToContent();
        }
        internal void ToggleDetails()
        {
            detailsOpen = !detailsOpen; details.Visible = detailsOpen;
            more.Text = detailsOpen ? "Скрыть подробности" : "Как работает и ограничения"; FitToContent();
        }
        internal void MoveToTray()
        {
            if (stopped) return;
            tray.Visible = true; inTray = true; Hide();
        }
        internal void RestoreFromTray()
        {
            if (stopped) return;
            inTray = false; tray.Visible = false;
            if (!renderOnly) { Show(); WindowState = FormWindowState.Normal; Activate(); }
        }
        internal void RequestExit() { Close(); Dispose(); }
        internal void FitToContent()
        {
            if (fitting || stopped || layout.Parent == null) return;
            fitting = true;
            try
            {
                int width = Math.Max(200, ClientSize.Width - layout.Padding.Horizontal);
                foreach (var label in new[] { heading, intro, example, explanation, note, details }) label.MaximumSize = new Size(width, 0);
                startupOption.MaximumSize = new Size(width, 0); startupStatus.MaximumSize = new Size(Math.Max(100, width - startupStatus.Margin.Horizontal), 0);
                state.MaximumSize = new Size(Math.Max(100, width - statusPanel.Padding.Horizontal - (int)statusPanel.ColumnStyles[1].Width - state.Margin.Horizontal), 0);
                stateHelp.MaximumSize = new Size(Math.Max(100, width - statusPanel.Padding.Horizontal), 0);
                layout.PerformLayout();
                int desired = layout.GetPreferredSize(new Size(ClientSize.Width, 0)).Height;
                int limit = Math.Max(240, Screen.FromControl(this).WorkingArea.Height - (Height - ClientSize.Height) - 32);
                int height = Math.Min(desired, limit);
                if (height > 0 && ClientSize.Height != height) Height = height + (Height - ClientSize.Height);
            }
            finally { fitting = false; }
        }
        void ApplyPalette(UiPalette palette)
        {
            Palette = palette; BackColor = palette.Surface; ForeColor = palette.Text;
            statusPanel.BackColor = palette.Panel;
            foreach (var label in new[] { heading, state, example }) label.ForeColor = palette.Text;
            foreach (var label in new[] { intro, stateHelp, explanation, note, details }) label.ForeColor = palette.Secondary;
            startupOption.ForeColor = palette.Text; startupStatus.ForeColor = palette.Secondary;
            primary.FlatStyle = FlatStyle.Flat; primary.UseVisualStyleBackColor = false; primary.BackColor = palette.Accent; primary.ForeColor = palette.AccentText; primary.FlatAppearance.BorderColor = palette.Accent; primary.FlatAppearance.BorderSize = 1;
            toTray.FlatStyle = FlatStyle.Flat; toTray.UseVisualStyleBackColor = false; toTray.BackColor = palette.Surface; toTray.ForeColor = palette.Text; toTray.FlatAppearance.BorderColor = palette.Border;
            Color link = UiPalette.Contrast(palette.Accent, palette.Surface) >= 4.5 ? palette.Accent : palette.Text;
            more.LinkColor = link; more.ActiveLinkColor = link; more.VisitedLinkColor = link;
            startupStatus.LinkColor = link; startupStatus.ActiveLinkColor = link; startupStatus.VisitedLinkColor = link;
        }
        protected override void OnSystemColorsChanged(EventArgs e) { base.OnSystemColorsChanged(e); if (layout != null && fixedPalette == null) ApplyPalette(UiPalette.FromSystem()); }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !stopped)
            {
                stopped = true; timer.Stop(); timer.Dispose();
                LoadResult result; lock (pendingGate) { result = pending; pending = null; }
                if (controller != null) { controller.Faulted -= OnEngineFault; controller.Dispose(); }
                if (result != null && result.Controller != null) result.Controller.Dispose();
                tray.Dispose(); windowIcon.Dispose();
            }
            base.Dispose(disposing);
        }
    }
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            bool startupLaunch = IsStartupInvocation(args);
            bool firstInstance;
            using (var mutex = new Mutex(true, @"Local\SearchLayoutPrototype.20261011", out firstInstance))
            using (var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SearchLayout.Show.20261011"))
            {
                if (!firstInstance) { if (!startupLaunch) showSignal.Set(); return; }
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                try { Application.Run(new MainForm(showSignal, startupLaunch)); }
                finally { mutex.ReleaseMutex(); }
            }
        }
        internal static bool IsStartupInvocation(string[] args) { return args != null && Array.Exists(args, a => string.Equals(a, "--startup", StringComparison.OrdinalIgnoreCase)); }
    }
}
