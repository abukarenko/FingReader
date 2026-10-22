namespace FS80H;

sealed class MainForm : Form
{
    readonly TextBox user = new() { Width = 285, MaxLength = 80, AccessibleName = "Имя или псевдоним" };
    readonly Button setup = new() { Text = "⚙", Width = 36, Height = 36, Font = new Font("Segoe UI Symbol", 21), FlatStyle = FlatStyle.Flat, AccessibleName = "Setup — настройки" };
    readonly ToolTip tips = new();
    readonly System.Windows.Forms.Timer aliasDebounce = new() { Interval = 500 };
    readonly string? settingsPath;
    AppPreferences preferences;
    readonly Button scan = new() { Text = "Считать", AutoSize = true };
    readonly Button ok = new() { Text = "ОК", Enabled = false, AutoSize = true, AccessibleName = "ОК — сохранить кадр" };
    readonly Button stop = new() { Text = "Завершить", Enabled = false, AutoSize = true };
    readonly PictureBox preview = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(23, 31, 42), AccessibleName = "Текущий кадр сканера" };
    readonly Label status = new() { AutoSize = true, MaximumSize = new Size(330, 0), Text = "Приложите палец для распознавания. «Считать» — сохранить новую серию." };
    readonly Label quality = new() { AutoSize = true, MaximumSize = new Size(330, 0), Text = "Оценка кадра появится при сканировании." };
    readonly Label progress = new() { AutoSize = true, Text = "Сохранено: 0" };
    readonly Label hint = new() { AutoSize = true, Font = new Font("Segoe UI", 11, FontStyle.Bold), MaximumSize = new Size(330, 0) };
    readonly ProgressBar stable = new() { Width = 300, Maximum = 2000 };
    readonly ListBox saved = new() { Width = 320, Height = 100, HorizontalScrollbar = true };
    readonly StabilityTracker stability = new();
    readonly VariantTracker variants = new();
    CancellationTokenSource? cts;
    CaptureData? current;
    string directory = "", sessionId = "", sessionUser = "";
    int requested, completed;
    bool waitingForLift, done, closing, stoppedByUser;
    string lastBlocker = "палец ещё не приложен";
    string? saveFailure;
    double absentSince = -1, lastFrame;
    int indicatorWaiting = 1;
    Task lampStartup = Task.CompletedTask;
    bool lampStarted, lampShutdown;
    bool recognitionProcessing;
    readonly RecognitionTouchGate touchGate = new();
    Task automaticRecognitionTask = Task.CompletedTask;
    bool automaticRunning, automaticPaused, transitioning;

    readonly System.Windows.Forms.Timer freshness = new() { Interval = 200 };
    static readonly string[] Positions = ["Подушечка по центру", "Немного сместите палец влево", "Немного сместите палец вправо", "Приложите чуть ближе к кончику", "Приложите чуть ближе к основанию"];

    public MainForm(string? settingsPath = null)
    {
        this.settingsPath = settingsPath;
        preferences = OutputPreferences.LoadSettings(settingsPath);
        directory = preferences.SaveDirectory; user.Text = preferences.Alias;
        Text = "Futronic FS80H — отпечатки";
        Font = new Font("Segoe UI", 10); ClientSize = new Size(640, 465); MinimumSize = new Size(640, 465);
        StartPosition = FormStartPosition.CenterScreen;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(18) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var imagePanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, Padding = new Padding(0, 0, 12, 0) };
        imagePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 314)); imagePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        imagePanel.Controls.Add(preview, 0, 0);
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (int height in new[] { 46, 47, 40, 25, 28, 62 }) panel.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 87)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
        header.Controls.Add(new Label { Text = "Псевдоним", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        user.Dock = DockStyle.Fill; user.Anchor = AnchorStyles.Left | AnchorStyles.Right; user.AccessibleName = "Псевдоним";
        header.Controls.Add(user, 1, 0); header.Controls.Add(setup, 2, 0);
        setup.FlatAppearance.BorderSize = 0; tips.SetToolTip(setup, "Setup — настройки");
        using (var stream = typeof(MainForm).Assembly.GetManifestResourceStream("FS80H.assets.setup.png"))
        {
            if (stream != null) { using var source = Image.FromStream(stream); setup.Image = new Bitmap(source, 26, 26); setup.Text = ""; }
        }
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, FlowDirection = FlowDirection.RightToLeft };
        scan.AutoSize = ok.AutoSize = stop.AutoSize = false;
        scan.Width = 80; ok.Width = 48; stop.Width = 105;
        scan.Height = ok.Height = stop.Height = 30;
        actions.Controls.AddRange([stop, ok, scan]);
        hint.Font = new Font("Segoe UI", 10, FontStyle.Bold);
        SetState("Готово", "Готово к считыванию");
        foreach (Control control in new Control[] { hint, quality, progress, status }) { control.Dock = DockStyle.Fill; control.Margin = new Padding(3); }
        stable.Dock = DockStyle.Fill; saved.Dock = DockStyle.Fill;
        panel.Controls.Add(header, 0, 0); panel.Controls.Add(hint, 0, 1); panel.Controls.Add(quality, 0, 2);
        panel.Controls.Add(stable, 0, 3); panel.Controls.Add(progress, 0, 4); panel.Controls.Add(status, 0, 5); panel.Controls.Add(saved, 0, 6); panel.Controls.Add(actions, 0, 7);
        root.Controls.Add(imagePanel, 0, 0); root.Controls.Add(panel, 1, 0); Controls.Add(root);
        setup.Click += (_, _) => OpenSetup();
        scan.Click += StartCapture; ok.Click += (_, _) => SaveCurrent(false); stop.Click += (_, _) => ToggleStop();
        user.TextChanged += (_, _) => { aliasDebounce.Stop(); aliasDebounce.Start(); };
        aliasDebounce.Tick += (_, _) => { aliasDebounce.Stop(); SavePreferences(); };
        UpdateMode();
        freshness.Tick += (_, _) =>
        {
            if (cts != null && !recognitionProcessing && StabilityTracker.Now - lastFrame > 1)
            {
                InvalidateCurrent();
                if (!waitingForLift) { ClearPreview(); SetState("Ожидание", "Нет свежего кадра — ожидаем палец / ответ сканера"); }
            }
        };
        freshness.Start();
        Shown += async (_, _) =>
        {
            lampStarted = true; lampStartup = Task.Run(() => Scanner.SetIndicator(true));
            try { await lampStartup; }
            catch (Exception ex) { if (!IsDisposed) tips.SetToolTip(preview, "Индикатор недоступен: " + ex.GetBaseException().Message); }
            if (!closing && !IsDisposed) StartAutomaticRecognition();
        };
        FormClosing += async (_, e) =>
        {
            SavePreferences();
            if (cts != null) { e.Cancel = true; closing = true; cts.Cancel(); status.Text = "Завершение вызова сканера…"; return; }
            if (lampStarted && !lampShutdown)
            {
                e.Cancel = true; closing = true;
                try { await lampStartup; } catch { }
                try { await Task.Run(() => Scanner.SetIndicator(false)); } catch { }
                lampShutdown = true; Close();
            }
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            freshness.Dispose(); aliasDebounce.Dispose(); tips.Dispose(); setup.Image?.Dispose(); setup.Image = null;
            InvalidateCurrent(); variants.Clear(); ClearPreview();
        }
        base.Dispose(disposing);
    }

    async void StartCapture(object? sender, EventArgs args)
    {
        if (transitioning || closing || (cts != null && !automaticRunning)) return;
        transitioning = true; UpdateIdleControls();
        try { await SuspendAutomaticRecognition(); if (!closing) { transitioning = false; completed = 0; await RunEnrollmentAsync(); } }
        finally { transitioning = false; if (!closing) { UpdateIdleControls(); StartAutomaticRecognition(completed > 0); } }
    }
    async Task RunEnrollmentAsync()
    {
        string name = user.Text.Trim();
        if (name.Length == 0 || name.Any(char.IsControl)) { MessageBox.Show(this, "Введите имя или псевдоним без управляющих символов."); return; }
        if (!Directory.Exists(directory) && !ChooseDirectory()) return;
        sessionUser = name; aliasDebounce.Stop(); SavePreferences();
        sessionId = Guid.NewGuid().ToString("N"); requested = preferences.Series ? preferences.Samples : 1;
        completed = 0; saved.Items.Clear(); variants.Clear(); waitingForLift = done = stoppedByUser = false; absentSince = -1;
        lastBlocker = "палец ещё не приложен";
        saveFailure = null;
        InvalidateCurrent(); ClearPreview(); lastFrame = StabilityTracker.Now;
        cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        scan.Enabled = user.Enabled = setup.Enabled = false; stop.Enabled = true; stop.Text = "Завершить";
        hint.Font = new Font("Segoe UI", 10, FontStyle.Bold); hint.ForeColor = SystemColors.ControlText;
        progress.Text = $"Сохранено: 0 / {requested}"; hint.Text = Positions[0];
        status.Text = preferences.Auto ? "Приложите палец и удерживайте 2 секунды. Снимок сохранится автоматически." : "Ручной режим: для сохранения нажмите ОК.";
        bool failed = false;
        SetState("Запуск");
        try
        {
            var token = cts.Token;
            // Synchronous handoff bounds queued frames to one. SDK buffer is never used concurrently.
            await Task.Run(() => Scanner.Stream(
                token,
                (frame, error) => (bool)Invoke(new Func<bool>(() => OnFrame(frame, error, token))),
                () => Volatile.Read(ref indicatorWaiting) != 0,
                message => { BeginInvoke(new Action(() => { if (!IsDisposed) tips.SetToolTip(preview, "Индикатор недоступен: " + message); })); }));
            token.ThrowIfCancellationRequested();
            if (done) status.Text = $"Готово. Сохранено {completed} из {requested}. Файлы в выбранной папке.";
        }
        catch (OperationCanceledException)
        {
            string reason = closing ? "Окно закрыто" : stoppedByUser ? "Остановлено кнопкой «Завершить»" : "Истекли 5 минут ожидания";
            status.Text = saveFailure != null ? $"Не удалось сохранить: {saveFailure}. Сохранено: {completed} / {requested}." : $"{reason}. Сохранено: {completed} / {requested}. Последнее ожидание: {lastBlocker}.";
        }
        catch (Exception ex) { failed = true; status.Text = "Ошибка: " + ex.GetBaseException().Message + $" Уже сохранено: {completed}."; }
        finally
        {
            cts.Dispose(); cts = null; InvalidateCurrent(); variants.Clear(); ClearPreview(); hint.Text = "";
            SetState(done ? "Готово" : failed || saveFailure != null ? "Ошибка" : "Остановлено", status.Text);
            scan.Enabled = user.Enabled = setup.Enabled = true; stop.Enabled = false;
            if (closing) Close();
        }
    }

    void ToggleStop()
    {
        if (transitioning || closing) return;
        if (automaticRunning) { automaticPaused = true; cts?.Cancel(); }
        else if (cts != null) { stoppedByUser = true; cts.Cancel(); }
        else { automaticPaused = false; StartAutomaticRecognition(); }
    }
    async Task SuspendAutomaticRecognition()
    {
        if (!automaticRunning) return;
        cts?.Cancel(); await automaticRecognitionTask;
    }
    void UpdateIdleControls()
    {
        scan.Enabled = setup.Enabled = user.Enabled = !transitioning;
        stop.Enabled = !transitioning; stop.Text = automaticRunning ? "Пауза" : "Продолжить";
        ok.Enabled = false;
    }
    void StartAutomaticRecognition(bool requireLift = false)
    {
        if (closing || IsDisposed || automaticPaused || transitioning || cts != null || automaticRunning) return;
        touchGate.Reset(requireLift); automaticRecognitionTask = RunAutomaticRecognitionAsync();
    }
    async Task RunAutomaticRecognitionAsync()
    {
        if (!Directory.Exists(directory))
        { status.Text = "Выберите папку базы в Setup. «Считать» — сохранить новую серию."; UpdateIdleControls(); return; }
        automaticRunning = true; cts = new CancellationTokenSource(); UpdateIdleControls();
        InvalidateCurrent(); ClearPreview(); waitingForLift = false; recognitionProcessing = true;
        hint.Font = new Font("Segoe UI", 12, FontStyle.Bold); hint.ForeColor = SystemColors.ControlText;
        hint.Text = "Приложите палец"; progress.Text = "База: загрузка…";
        status.Text = "Распознавание включено автоматически."; SetState("База"); bool failed = false;
        try
        {
            var token = cts.Token; string baseDirectory = directory; int threshold = preferences.RecognitionThreshold;
            await Task.Run(() =>
            {
                bool firstLoad = true;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var database = FingerprintDatabase.Load(baseDirectory, token);
                    if (database.Count == 0) throw new InvalidOperationException(database.Skipped > 0 ? "В базе нет пригодных архивов. Проверьте файлы." : "База пуста. Сохраните серию кнопкой «Считать».");
                    Invoke(new Action(() =>
                    {
                        progress.Text = $"База: {database.Count} снимков, псевдонимов: {database.AliasCount}";
                        if (firstLoad) { saved.Items.Clear(); if (database.Skipped > 0) saved.Items.Add($"Пропущено архивов: {database.Skipped}"); }
                        firstLoad = false; recognitionProcessing = false; lastFrame = StabilityTracker.Now; SetState("Ожидание");
                    }));
                    CaptureData? probe = null;
                    try
                    {
                        Scanner.Stream(token,
                            (frame, error) => (bool)Invoke(new Func<bool>(() => OnAutomaticFrame(frame, error, token, ready => probe = ready))),
                            () => Volatile.Read(ref indicatorWaiting) != 0,
                            message => { BeginInvoke(new Action(() => tips.SetToolTip(preview, "Индикатор: " + message))); });
                        token.ThrowIfCancellationRequested();
                        if (probe == null) throw new InvalidOperationException("Не получен кадр для сравнения.");
                        Invoke(new Action(() => { recognitionProcessing = true; SetState("Сравнение"); }));
                        var result = database.Identify(probe, token, threshold: threshold); token.ThrowIfCancellationRequested();
                        Invoke(new Action(() => { ShowRecognitionResult(result); touchGate.RequireLift(); stability.Reset(); SetState("Готово", "Уберите палец перед следующей попыткой."); }));
                    }
                    finally { if (probe != null) Array.Clear(probe.Pixels); }
                }
            });
        }
        catch (OperationCanceledException)
        { if (automaticPaused && !closing) status.Text = "Распознавание приостановлено."; }
        catch (Exception ex) { failed = true; status.Text = "Ошибка: " + ex.GetBaseException().Message; }
        finally
        {
            try { await Task.Run(() => Scanner.SetIndicator(false)); } catch { }
            cts.Dispose(); cts = null; automaticRunning = false; recognitionProcessing = false; InvalidateCurrent(); ClearPreview();
            SetState(failed ? "Ошибка" : "Остановлено", status.Text); UpdateIdleControls();
            if (closing) Close();
        }
    }
    bool OnAutomaticFrame(CaptureData? frame, uint error, CancellationToken token, Action<CaptureData> ready)
    {
        if (token.IsCancellationRequested) return false;
        if (touchGate.WaitingForLift)
        {
            touchGate.AcceptFrame(frame != null, error, StabilityTracker.Now);
            stability.Reset(); stable.Value = 0;
            if (frame != null) { lastFrame = StabilityTracker.Now; UpdatePreview(frame); } else ClearPreview();
            SetState("Ожидание", "Уберите палец, затем приложите следующий."); return true;
        }
        if (frame != null && quality.Text == "Ожидание") { hint.Text = "Распознавание…"; hint.ForeColor = SystemColors.ControlText; }
        return OnRecognitionFrame(frame, error, token, ready);
    }

    bool OnRecognitionFrame(CaptureData? frame, uint error, CancellationToken token, Action<CaptureData> ready)
    {
        if (token.IsCancellationRequested) return false;
        double now = StabilityTracker.Now;
        if (frame == null)
        {
            InvalidateCurrent(); ClearPreview(); lastBlocker = "ожидание пальца";
            SetState("Ожидание", error == 0x20000001 ? "Удерживайте палец неподвижно." : "Приложите палец."); return true;
        }
        lastFrame = now; UpdatePreview(frame);
        var assessment = QualityCheck.Evaluate(frame);
        bool stableEnough = stability.Update(frame.Pixels, assessment.Acceptable, now);
        stable.Value = Math.Clamp((int)(stability.Seconds * 1000), 0, 2000);
        SetState(assessment.Acceptable ? "Сканирование" : "Ожидание", assessment.Message);
        if (!stableEnough) return true;
        ready(frame with { Pixels = (byte[])frame.Pixels.Clone() }); return false;
    }
    void ShowRecognitionResult(RecognitionResult result)
    {
        hint.Text = result.Display; hint.ForeColor = result.Found ? Color.DarkGreen : Color.Firebrick;
        tips.SetToolTip(hint, result.Display);
        status.Text = (result.Found ? "Совпадение найдено." : result.Ambiguous ? "Близкие результаты разных псевдонимов." : "Совпадение не найдено.") + $" Балл: {result.Score:0.0}; порог: {result.Threshold:0}.";
        tips.SetToolTip(quality, $"Оценка сходства: {result.Score:0.0}; порог: {result.Threshold:0}");
    }

    bool OnFrame(CaptureData? frame, uint error, CancellationToken token)
    {
        if (token.IsCancellationRequested || done) return false;
        double now = StabilityTracker.Now;
        if (frame == null)
        {
            InvalidateCurrent(); ClearPreview();
            if (waitingForLift)
            {
                // Only an empty frame counts as a release; motion/no-frame must not rearm the session.
                if (error == 4306)
                {
                    if (absentSince < 0) absentSince = now;
                    if (now - absentSince >= .4) { waitingForLift = false; absentSince = -1; hint.Text = Positions[completed % Positions.Length]; }
                }
                else absentSince = -1;
                quality.Text = waitingForLift ? "Уберите палец со сканера перед следующим вариантом." : "Снова приложите тот же палец, немного изменив положение.";
            }
            else quality.Text = error == 0x20000001 ? "Палец движется — удерживайте неподвижно." : "Ожидание пальца…";
            lastBlocker = waitingForLift ? "нужно убрать палец перед следующим снимком" : error == 0x20000001 ? "движение пальца" : "сканер не видит палец";
            SetState("Ожидание", quality.Text);
            return true;
        }
        absentSince = -1; lastFrame = now;
        UpdatePreview(frame);
        if (waitingForLift) { InvalidateCurrent(); SetState("Ожидание", "Снимок принят. Уберите палец, затем приложите заново."); return true; }
        InvalidatePixels(); current = frame with { Pixels = (byte[])frame.Pixels.Clone() };
        var assessment = QualityCheck.Evaluate(frame);
        bool distinct = variants.IsDistinct(frame);
        bool acceptable = assessment.Acceptable && distinct;
        bool ready = stability.Update(frame.Pixels, acceptable, now);
        stable.Value = Math.Clamp((int)(stability.Seconds * 1000), 0, 2000);
        quality.Text = (distinct ? assessment.Message : "Кадр очень похож на сохранённый — немного измените положение.") + $"\nНеподвижно: {Math.Min(stability.Seconds, 2):0.0} / 2.0 с";
        SetState(acceptable ? "Сканирование" : "Ожидание", quality.Text);
        ok.Enabled = acceptable;
        lastBlocker = !distinct ? "нужно немного изменить положение пальца" : !assessment.Acceptable ? "недостаточно текстуры" : !preferences.Auto ? "авто выключено; требуется ОК" : ready ? "кадр готов" : "удержание неподвижно в течение 2 секунд";
        if (preferences.Auto && ready) SaveCurrent(true);
        return !done && !token.IsCancellationRequested;
    }

    void SaveCurrent(bool auto)
    {
        if (cts == null || cts.IsCancellationRequested || current == null || waitingForLift || done) return;
        if (StabilityTracker.Now - lastFrame > 1 || !QualityCheck.Evaluate(current).Acceptable || !variants.IsDistinct(current)) { InvalidateCurrent(); return; }
        try
        {
            var info = new CaptureInfo(sessionId, completed + 1, requested, Positions[completed % Positions.Length], auto ? "auto" : "manual", stability.Seconds);
            string path = Storage.Save(directory, sessionUser, current, info);
            variants.Add(current);
            completed++; saved.Items.Add($"{completed}. {Path.GetFileName(path)}"); saved.TopIndex = saved.Items.Count - 1;
            progress.Text = $"Сохранено: {completed} / {requested}"; status.Text = "Последний файл: " + Path.GetFileName(path);
            InvalidateCurrent(); done = completed >= requested; waitingForLift = !done; absentSince = -1;
            hint.Text = done ? "Серия завершена" : "Уберите палец, затем приложите заново";
            SetState(done ? "Готово" : "Ожидание", hint.Text);
        }
        catch (Exception ex)
        {
            // Stop on IO failure, avoiding automatic repeated writes/dialogs on every frame.
            saveFailure = ex.Message;
            status.Text = "Не удалось сохранить: " + saveFailure; preferences = preferences with { Auto = false }; UpdateMode();
            MessageBox.Show(this, status.Text, "Ошибка сохранения"); cts.Cancel();
        }
    }
    void InvalidatePixels() { if (current != null) Array.Clear(current.Pixels); current = null; ok.Enabled = false; }
    void InvalidateCurrent() { InvalidatePixels(); stability.Reset(); stable.Value = 0; }
    void ReplacePreview(Image image) { var old = preview.Image; preview.Image = image; old?.Dispose(); }
    void UpdatePreview(CaptureData frame)
    {
        if (preview.Image is Bitmap bitmap && bitmap.Width == frame.Width && bitmap.Height == frame.Height)
        { PreviewImage.Update(bitmap, frame); preview.Invalidate(); }
        else ReplacePreview(PreviewImage.Create(frame));
    }
    void ClearPreview() { var old = preview.Image; preview.Image = null; old?.Dispose(); }
    void SetState(string state, string? details = null)
    {
        quality.Text = state; tips.SetToolTip(quality, details ?? state);
        Volatile.Write(ref indicatorWaiting, state == "Ожидание" ? 1 : 0);
    }
    void UpdateMode()
    {
        tips.SetToolTip(scan, preferences.Auto ? $"Авто: { (preferences.Series ? preferences.Samples : 1) } снимков, сохранение после 2 секунд" : "Ручной режим: сохранение по ОК");
    }
    void SavePreferences()
    {
        preferences = preferences with { SaveDirectory = directory, Alias = user.Text.Trim() };
        try { OutputPreferences.SaveSettings(preferences, settingsPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { status.Text = "Не удалось запомнить настройки: " + ex.Message; }
    }
    async void OpenSetup()
    {
        if (transitioning || closing || (cts != null && !automaticRunning)) return;
        transitioning = true; UpdateIdleControls();
        try
        {
            await SuspendAutomaticRecognition(); if (closing) return;
            using var dialog = new SetupForm(preferences with { SaveDirectory = directory, Alias = user.Text.Trim() });
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try { OutputPreferences.SaveSettings(dialog.Result, settingsPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { MessageBox.Show(this, "Не удалось сохранить настройки: " + ex.Message); return; }
            preferences = dialog.Result; directory = preferences.SaveDirectory; UpdateMode();
        }
        finally { transitioning = false; if (!closing) { UpdateIdleControls(); StartAutomaticRecognition(); } }
    }
    bool ChooseDirectory()
    {
        using var chooser = new FolderBrowserDialog { Description = "Локальная папка для принятых снимков", UseDescriptionForTitle = true, SelectedPath = Directory.Exists(directory) ? directory : "" };
        if (chooser.ShowDialog(this) != DialogResult.OK) return false;
        directory = chooser.SelectedPath; SavePreferences(); return true;
    }
}
