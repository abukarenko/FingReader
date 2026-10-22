namespace FS80H;

sealed class SetupForm : Form
{
    readonly TextBox directory = new() { ReadOnly = true, Width = 420, AccessibleName = "Папка сохранения" };
    readonly CheckBox series = new() { Text = "Собирать серию вариантов одного пальца", AutoSize = true };
    readonly NumericUpDown samples = new() { Minimum = 2, Maximum = 10, Width = 70, AccessibleName = "Снимков в серии" };
    readonly CheckBox automatic = new() { Text = "Авто: сохранять после 2 секунд неподвижности", AutoSize = true };
    readonly TrackBar recognitionThreshold = new() { Minimum = 30, Maximum = 60, Value = 40, TickFrequency = 5, SmallChange = 1, LargeChange = 5, AutoSize = false, Width = 420, Height = 34, AccessibleName = "Порог распознавания" };
    readonly Label thresholdLabel = new() { AutoSize = true };
    public AppPreferences Result { get; private set; }

    public SetupForm(AppPreferences settings)
    {
        Result = settings;
        Text = "Setup — настройки"; Font = new Font("Segoe UI", 10);
        ClientSize = new Size(465, 515); FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent; MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
        directory.Text = settings.SaveDirectory; series.Checked = settings.Series; samples.Value = settings.Samples;
        automatic.Checked = settings.Auto; samples.Enabled = series.Checked;
        recognitionThreshold.Value = settings.RecognitionThreshold; UpdateThresholdLabel();
        recognitionThreshold.ValueChanged += (_, _) => UpdateThresholdLabel();
        var resetThreshold = new Button { Text = "Сбросить порог к 40", AutoSize = true, AccessibleName = "Сбросить порог к 40" };
        resetThreshold.Click += (_, _) => recognitionThreshold.Value = 40;
        var folder = new Button { Text = "Выбрать папку…", AutoSize = true };
        folder.Click += (_, _) =>
        {
            using var chooser = new FolderBrowserDialog { Description = "Папка сохранения отпечатков", UseDescriptionForTitle = true, SelectedPath = Directory.Exists(directory.Text) ? directory.Text : "" };
            if (chooser.ShowDialog(this) == DialogResult.OK) directory.Text = chooser.SelectedPath;
        };
        series.CheckedChanged += (_, _) => samples.Enabled = series.Checked;
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), FlowDirection = FlowDirection.TopDown, WrapContents = false };
        var count = new FlowLayoutPanel { Width = 420, Height = 35, WrapContents = false };
        count.Controls.AddRange([new Label { Text = "Снимков в серии", AutoSize = true, Padding = new Padding(0, 5, 12, 0) }, samples]);
        var save = new Button { Text = "Сохранить", AutoSize = true };
        var cancel = new Button { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel };
        var actions = new FlowLayoutPanel { Width = 420, Height = 40, FlowDirection = FlowDirection.RightToLeft };
        actions.Controls.AddRange([cancel, save]);
        save.Click += (_, _) =>
        {
            if (directory.Text.Length > 0 && !Directory.Exists(directory.Text)) { MessageBox.Show(this, "Выберите существующую папку."); return; }
            Result = settings with { SaveDirectory = directory.Text, Series = series.Checked, Samples = (int)samples.Value, Auto = automatic.Checked, RecognitionThreshold = recognitionThreshold.Value };
            DialogResult = DialogResult.OK;
        };
        layout.Controls.AddRange([new Label { Text = "Папка сохранения", AutoSize = true }, directory, folder, series, count, automatic,
            thresholdLabel, recognitionThreshold, resetThreshold,
            new Label { Text = "Промежуточные кадры остаются в памяти. Сохраняются только принятые снимки.", AutoSize = true, MaximumSize = new Size(420, 0) }, actions]);
        foreach (Control control in layout.Controls) control.Margin = new Padding(3, 3, 3, 7);
        Controls.Add(layout); AcceptButton = save; CancelButton = cancel;
    }
    void UpdateThresholdLabel() => thresholdLabel.Text = $"Порог: {recognitionThreshold.Value} · ← мягче / строже →";
}
