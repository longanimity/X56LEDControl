namespace X56LedControl;

/// <summary>Colour + brightness controls for one half of the HOTAS.</summary>
internal sealed class DevicePanel : UserControl
{
    static readonly Color[] Presets =
    {
        Color.FromArgb(255, 0, 0), Color.FromArgb(255, 96, 0), Color.FromArgb(255, 200, 0), Color.FromArgb(0, 255, 0),
        Color.FromArgb(0, 255, 200), Color.FromArgb(0, 80, 255), Color.FromArgb(160, 0, 255), Color.FromArgb(255, 255, 255),
    };

    readonly Label _title = new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 13f) };
    readonly Label _status = new() { AutoSize = true, ForeColor = Theme.Muted };
    readonly Panel _swatch = new() { Height = 72, Dock = DockStyle.Top, Cursor = Cursors.Hand, BorderStyle = BorderStyle.FixedSingle };
    readonly TrackBar _r = Slider(255), _g = Slider(255), _b = Slider(255), _bright = Slider(100);
    readonly Label _rv = ValueLabel(), _gv = ValueLabel(), _bv = ValueLabel(), _brightv = ValueLabel();
    readonly TextBox _hex = new() { Width = 80, CharacterCasing = CharacterCasing.Upper, MaxLength = 7 };
    readonly Button _read = Theme.Button("Read from device"), _save = Theme.Button("Save to device");
    readonly System.Windows.Forms.Timer _sendTimer = new() { Interval = 40 };
    bool _colourDirty, _brightDirty, _updating;

    public X56Kind Kind { get; }
    public X56Device? Device { get; private set; }

    /// <summary>Raised after a user edit (sender, colourChanged, brightnessChanged) so a linked panel can mirror it.</summary>
    public event Action<DevicePanel, bool, bool>? UserChanged;

    public Color Colour => Color.FromArgb(_r.Value, _g.Value, _b.Value);
    public int Brightness => _bright.Value;

    public DevicePanel(X56Kind kind)
    {
        Kind = kind;
        BackColor = Theme.Card;
        ForeColor = Theme.Text;
        Padding = new Padding(16);
        _title.Text = kind == X56Kind.Stick ? "Stick" : "Throttle";

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
        AddRow(layout, "Red", _r, _rv);
        AddRow(layout, "Green", _g, _gv);
        AddRow(layout, "Blue", _b, _bv);
        AddRow(layout, "Brightness", _bright, _brightv);

        var hexRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 8, 0, 0) };
        hexRow.Controls.Add(new Label { Text = "Hex", AutoSize = true, Margin = new Padding(0, 6, 8, 0) });
        hexRow.Controls.Add(_hex);
        Theme.StyleInput(_hex);

        var presets = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 8, 0, 4) };
        foreach (var c in Presets)
        {
            var p = new Panel { Width = 28, Height = 28, BackColor = c, Margin = new Padding(0, 0, 6, 0), Cursor = Cursors.Hand };
            p.Click += (_, _) => { SetValues(c, null); OnUserEdit(colour: true, brightness: false); };
            presets.Controls.Add(p);
        }

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 12, 0, 0) };
        buttons.Controls.Add(_read);
        buttons.Controls.Add(_save);

        var header = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, Padding = new Padding(0, 0, 0, 8) };
        header.Controls.Add(_title);
        header.Controls.Add(_status);

        // Dock order: last added sits on top.
        Controls.Add(buttons);
        Controls.Add(hexRow);
        Controls.Add(layout);
        Controls.Add(presets);
        Controls.Add(_swatch);
        Controls.Add(header);
        layout.Dock = DockStyle.Top;

        foreach (var tb in new[] { _r, _g, _b })
            tb.ValueChanged += (_, _) => { if (!_updating) { SyncLabels(); OnUserEdit(colour: true, brightness: false); } };
        _bright.ValueChanged += (_, _) => { if (!_updating) { SyncLabels(); OnUserEdit(colour: false, brightness: true); } };
        _hex.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { ApplyHex(); e.SuppressKeyPress = true; } };
        _hex.Leave += (_, _) => ApplyHex();
        _swatch.Click += (_, _) => PickColour();
        _read.Click += async (_, _) => await ReadFromDeviceAsync();
        _save.Click += (_, _) => Run(() => Device?.SaveToEeprom(), "Saved to device memory");
        _sendTimer.Tick += (_, _) => Flush();

        SetValues(Color.FromArgb(0, 80, 255), 100);
        AttachDevice(null);
    }

    static TrackBar Slider(int max) => new()
    {
        Maximum = max, TickStyle = TickStyle.None, Dock = DockStyle.Fill, AutoSize = false, Height = 30,
        BackColor = Theme.Card,
    };

    static Label ValueLabel() => new() { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Theme.Muted };

    static void AddRow(TableLayoutPanel t, string name, TrackBar bar, Label value)
    {
        t.Controls.Add(new Label { Text = name, AutoSize = true, Anchor = AnchorStyles.Left });
        t.Controls.Add(bar);
        t.Controls.Add(value);
    }

    public void AttachDevice(X56Device? device)
    {
        Device = device;
        _status.Text = device == null ? "Not connected" : $"Connected (feature report {device.FeatureLength} bytes)";
        _status.ForeColor = device == null ? Theme.Warn : Theme.Ok;
        _read.Enabled = _save.Enabled = device != null;
    }

    /// <summary>Sets the controls without echoing them back as a user edit.</summary>
    public void SetValues(Color? colour, int? brightness)
    {
        _updating = true;
        if (colour is { } c) { _r.Value = c.R; _g.Value = c.G; _b.Value = c.B; }
        if (brightness is { } b) _bright.Value = Math.Clamp(b, 0, 100);
        _updating = false;
        SyncLabels();
    }

    /// <summary>Applies values that came from the linked panel and sends them to this device.</summary>
    public void MirrorFrom(DevicePanel other, bool colour, bool brightness)
    {
        SetValues(colour ? other.Colour : null, brightness ? other.Brightness : null);
        QueueSend(colour, brightness);
    }

    void SyncLabels()
    {
        _rv.Text = _r.Value.ToString();
        _gv.Text = _g.Value.ToString();
        _bv.Text = _b.Value.ToString();
        _brightv.Text = _bright.Value + "%";
        _swatch.BackColor = Colour;
        if (!_hex.Focused) _hex.Text = $"#{_r.Value:X2}{_g.Value:X2}{_b.Value:X2}";
    }

    void OnUserEdit(bool colour, bool brightness)
    {
        QueueSend(colour, brightness);
        UserChanged?.Invoke(this, colour, brightness);
    }

    void QueueSend(bool colour, bool brightness)
    {
        _colourDirty |= colour;
        _brightDirty |= brightness;
        _sendTimer.Start();
    }

    void Flush()
    {
        _sendTimer.Stop();
        if (Device == null) { _colourDirty = _brightDirty = false; return; }
        Run(() =>
        {
            if (_colourDirty) Device.SetColour((byte)_r.Value, (byte)_g.Value, (byte)_b.Value);
            if (_brightDirty) Device.SetBrightness(_bright.Value);
        }, null);
        _colourDirty = _brightDirty = false;
    }

    void ApplyHex()
    {
        var s = _hex.Text.Trim().TrimStart('#');
        if (s.Length == 6 && int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out int v))
        {
            SetValues(Color.FromArgb((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF), null);
            OnUserEdit(colour: true, brightness: false);
        }
        else SyncLabels();
    }

    void PickColour()
    {
        using var dlg = new ColorDialog { Color = Colour, FullOpen = true, AnyColor = true };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        SetValues(dlg.Color, null);
        OnUserEdit(colour: true, brightness: false);
    }

    public async Task ReadFromDeviceAsync()
    {
        if (Device == null) return;
        try
        {
            var c = await Device.QueryColourAsync();
            var b = await Device.QueryBrightnessAsync();
            SetValues(c is { } v ? Color.FromArgb(v.r, v.g, v.b) : null, b);
            _status.Text = c == null && b == null ? "Device did not answer the query" : "Read current settings from device";
            _status.ForeColor = c == null && b == null ? Theme.Warn : Theme.Ok;
        }
        catch (Exception ex) { ShowError(ex); }
    }

    void Run(Action action, string? success)
    {
        try
        {
            action();
            if (success != null) { _status.Text = success; _status.ForeColor = Theme.Ok; }
        }
        catch (Exception ex) { ShowError(ex); }
    }

    void ShowError(Exception ex)
    {
        _status.Text = ex.Message;
        _status.ForeColor = Theme.Warn;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _sendTimer.Dispose();
        base.Dispose(disposing);
    }
}
