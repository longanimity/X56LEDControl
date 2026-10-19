namespace X56LedControl;

internal static class Theme
{
    public static readonly Color Back = Color.FromArgb(24, 26, 31);
    public static readonly Color Card = Color.FromArgb(34, 37, 44);
    public static readonly Color Text = Color.FromArgb(230, 232, 236);
    public static readonly Color Muted = Color.FromArgb(150, 156, 168);
    public static readonly Color Ok = Color.FromArgb(110, 200, 140);
    public static readonly Color Warn = Color.FromArgb(240, 170, 90);
    public static readonly Color Accent = Color.FromArgb(58, 64, 78);

    public static Button Button(string text) => new()
    {
        Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Accent, ForeColor = Text,
        Padding = new Padding(8, 2, 8, 2), Margin = new Padding(0, 0, 8, 0),
        FlatAppearance = { BorderColor = Color.FromArgb(80, 88, 104) },
    };

    public static void StyleInput(TextBox t)
    {
        t.BackColor = Accent;
        t.ForeColor = Text;
        t.BorderStyle = BorderStyle.FixedSingle;
    }
}

internal sealed class MainForm : Form
{
    readonly DevicePanel _stick = new(X56Kind.Stick) { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0) };
    readonly DevicePanel _throttle = new(X56Kind.Throttle) { Dock = DockStyle.Fill, Margin = new Padding(8, 0, 0, 0) };
    readonly CheckBox _link = new() { Text = "Link stick && throttle", AutoSize = true, Margin = new Padding(16, 6, 0, 0) };
    readonly Label _summary = new() { AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(16, 7, 0, 0) };
    readonly System.Windows.Forms.Timer _poll = new() { Interval = 2000 };
    List<X56Device> _devices = new();

    public MainForm()
    {
        Text = "X56 LED Control";
        BackColor = Theme.Back;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9.5f);
        MinimumSize = new Size(760, 560);
        Size = new Size(860, 600);
        StartPosition = FormStartPosition.CenterScreen;
        Padding = new Padding(16);

        var refresh = Theme.Button("Rescan devices");
        refresh.Click += async (_, _) => await RescanAsync(readBack: true);

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 12) };
        top.Controls.Add(refresh);
        top.Controls.Add(_link);
        top.Controls.Add(_summary);

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.Controls.Add(_stick, 0, 0);
        grid.Controls.Add(_throttle, 1, 0);

        var note = new Label
        {
            Dock = DockStyle.Bottom, AutoSize = true, ForeColor = Theme.Muted, Padding = new Padding(0, 10, 0, 0),
            Text = "Each unit has one RGB backlight zone. Changes apply live; \"Save to device\" stores them in the unit's EEPROM.",
        };

        Controls.Add(grid);
        Controls.Add(note);
        Controls.Add(top);

        _stick.UserChanged += Mirror;
        _throttle.UserChanged += Mirror;
        _poll.Tick += async (_, _) =>
        {
            // Pick up hot-plugged units without user action.
            if (_devices.Count < 2 && MoreDevicesPresent()) await RescanAsync(readBack: true);
        };
        Shown += async (_, _) => { await RescanAsync(readBack: true); _poll.Start(); };
    }

    void Mirror(DevicePanel source, bool colour, bool brightness)
    {
        if (!_link.Checked) return;
        var other = source == _stick ? _throttle : _stick;
        other.MirrorFrom(source, colour, brightness);
    }

    bool MoreDevicesPresent()
    {
        var found = X56Device.Enumerate();
        int count = found.Count;
        foreach (var d in found) d.Dispose();
        return count > _devices.Count;
    }

    async Task RescanAsync(bool readBack)
    {
        foreach (var d in _devices) d.Dispose();
        _devices = X56Device.Enumerate();
        _stick.AttachDevice(_devices.FirstOrDefault(d => d.Kind == X56Kind.Stick));
        _throttle.AttachDevice(_devices.FirstOrDefault(d => d.Kind == X56Kind.Throttle));
        _summary.Text = _devices.Count switch
        {
            0 => "No X56 found - plug in the stick and/or throttle",
            1 => $"Found {_devices[0].Name} only",
            _ => "Found stick and throttle",
        };
        if (readBack)
        {
            await _stick.ReadFromDeviceAsync();
            await _throttle.ReadFromDeviceAsync();
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _poll.Stop();
        foreach (var d in _devices) d.Dispose();
        base.OnFormClosed(e);
    }
}
