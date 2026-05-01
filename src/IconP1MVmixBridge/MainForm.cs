using System.Diagnostics;
using System.Globalization;
using System.Windows.Forms;

namespace IconP1MVmixBridge;

public sealed class MainForm : Form
{
    private readonly FileLogger _logger;
    private readonly BridgeProfile _profile;
    private readonly VMixClient _vmix;
    private readonly MidiDeviceManager _midi;
    private readonly System.Windows.Forms.Timer _pollTimer = new();
    private readonly DataGridView _grid = new();
    private readonly ComboBox _midiInputCombo = new();
    private readonly ComboBox _midiOutputCombo = new();
    private readonly TextBox _hostText = new();
    private readonly NumericUpDown _httpPort = new();
    private readonly NumericUpDown _tcpPort = new();
    private readonly NumericUpDown _pollMs = new();
    private readonly CheckBox _motorFeedback = new();
    private readonly CheckBox _displayText = new();
    private readonly Label _statusLabel = new();
    private readonly Label _logLabel = new();
    private readonly Button _connectButton = new();
    private readonly Button _saveButton = new();
    private VMixState _state = new();
    private CancellationTokenSource? _pollCts;
    private bool _connected;
    private bool _updatingGrid;
    private readonly DateTime[] _lastFaderTouch = Enumerable.Repeat(DateTime.MinValue, 8).ToArray();

    public MainForm(FileLogger logger)
    {
        _logger = logger;
        _profile = BridgeProfile.LoadOrCreate(AppPaths.ConfigFile, logger);
        _vmix = new VMixClient(logger);
        _midi = new MidiDeviceManager(logger);
        _midi.MessageReceived += OnMidiMessageReceived;

        Text = "iCON P1-M vMix Bridge";
        Width = 1040;
        Height = 720;
        MinimumSize = new Size(900, 620);

        BuildUi();
        LoadProfileIntoUi();
        RefreshMidiDevices();
        ConfigurePollTimer();

        _logger.Info("Main window loaded");
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(12)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 128));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        Controls.Add(root);

        var settings = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 8,
            RowCount = 3
        };
        for (var i = 0; i < 8; i++)
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 12.5f));

        root.Controls.Add(settings, 0, 0);

        AddLabeled(settings, "vMix Host", _hostText, 0, 0);
        AddLabeled(settings, "HTTP Port", _httpPort, 2, 0);
        AddLabeled(settings, "TCP Port", _tcpPort, 4, 0);
        AddLabeled(settings, "Poll ms", _pollMs, 6, 0);
        AddLabeled(settings, "MIDI Input", _midiInputCombo, 0, 1, 4);
        AddLabeled(settings, "MIDI Output", _midiOutputCombo, 4, 1, 4);

        _httpPort.Minimum = 1;
        _httpPort.Maximum = 65535;
        _tcpPort.Minimum = 1;
        _tcpPort.Maximum = 65535;
        _pollMs.Minimum = 100;
        _pollMs.Maximum = 5000;
        _pollMs.Increment = 50;

        _motorFeedback.Text = "Motor fader feedback";
        _motorFeedback.Checked = true;
        _displayText.Text = "Display text feedback";
        _displayText.Checked = true;
        _connectButton.Text = "Connect";
        _connectButton.Click += (_, _) => ToggleConnection();
        _saveButton.Text = "Save Profile";
        _saveButton.Click += (_, _) => SaveProfile();
        var refreshMidi = new Button { Text = "Refresh MIDI", Dock = DockStyle.Fill };
        refreshMidi.Click += (_, _) => RefreshMidiDevices();

        settings.Controls.Add(_motorFeedback, 0, 2);
        settings.SetColumnSpan(_motorFeedback, 2);
        settings.Controls.Add(_displayText, 2, 2);
        settings.SetColumnSpan(_displayText, 2);
        settings.Controls.Add(refreshMidi, 4, 2);
        settings.Controls.Add(_saveButton, 5, 2);
        settings.Controls.Add(_connectButton, 6, 2);
        settings.SetColumnSpan(_connectButton, 2);

        ConfigureGrid();
        root.Controls.Add(_grid, 0, 1);

        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        root.Controls.Add(_statusLabel, 0, 2);

        _logLabel.Dock = DockStyle.Fill;
        _logLabel.TextAlign = ContentAlignment.MiddleLeft;
        _logLabel.Text = $"Logs: {_logger.CurrentLogFile}";
        _logLabel.Cursor = Cursors.Hand;
        _logLabel.Click += (_, _) => Process.Start("explorer.exe", $"/select,\"{_logger.CurrentLogFile}\"");
        root.Controls.Add(_logLabel, 0, 3);
    }

    private static void AddLabeled(TableLayoutPanel panel, string label, Control control, int column, int row, int span = 2)
    {
        var wrapper = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        wrapper.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        wrapper.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        wrapper.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill }, 0, 0);
        control.Dock = DockStyle.Fill;
        wrapper.Controls.Add(control, 0, 1);
        panel.Controls.Add(wrapper, column, row);
        panel.SetColumnSpan(wrapper, span);
    }

    private void ConfigureGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.RowHeadersVisible = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.CellValueChanged += (_, _) => { if (!_updatingGrid) SaveProfile(); };
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Channel",
            HeaderText = "P1-M Channel",
            ReadOnly = true,
            FillWeight = 70
        });
        _grid.Columns.Add(new DataGridViewComboBoxColumn
        {
            Name = "Kind",
            HeaderText = "Assignment",
            DataSource = Enum.GetValues<AssignmentKind>(),
            FillWeight = 100
        });
        _grid.Columns.Add(new DataGridViewComboBoxColumn
        {
            Name = "InputKey",
            HeaderText = "vMix Input",
            DisplayMember = "Title",
            ValueMember = "Key",
            FillWeight = 180
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Label",
            HeaderText = "Label Override",
            FillWeight = 140
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "LiveVolume",
            HeaderText = "Volume",
            ReadOnly = true,
            FillWeight = 65
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Meter",
            HeaderText = "Meter",
            ReadOnly = true,
            FillWeight = 65
        });
    }

    private void LoadProfileIntoUi()
    {
        _hostText.Text = _profile.VMixHost;
        _httpPort.Value = _profile.VMixHttpPort;
        _tcpPort.Value = _profile.VMixTcpPort;
        _pollMs.Value = _profile.PollIntervalMs;
        _motorFeedback.Checked = _profile.SendMotorFaderFeedback;
        _displayText.Checked = _profile.SendMackieScribbleStripText;
        PopulateGridRows();
    }

    private void PopulateGridRows()
    {
        _updatingGrid = true;
        try
        {
            _grid.Rows.Clear();
            UpdateInputColumnDataSource();
            foreach (var assignment in _profile.Channels)
            {
                _grid.Rows.Add(assignment.Channel, assignment.Kind, assignment.InputKey, assignment.LabelOverride, "", "");
            }
        }
        finally
        {
            _updatingGrid = false;
        }
    }

    private void RefreshMidiDevices()
    {
        var inputName = _midiInputCombo.Text;
        var outputName = _midiOutputCombo.Text;
        _midiInputCombo.Items.Clear();
        _midiOutputCombo.Items.Clear();
        foreach (var input in MidiDeviceManager.GetInputs())
            _midiInputCombo.Items.Add(input.Name);
        foreach (var output in MidiDeviceManager.GetOutputs())
            _midiOutputCombo.Items.Add(output.Name);

        _midiInputCombo.Text = PickDeviceText(_midiInputCombo, _profile.MidiInputName, inputName);
        _midiOutputCombo.Text = PickDeviceText(_midiOutputCombo, _profile.MidiOutputName, outputName);
        _logger.Info("MIDI refresh complete. Inputs: {0}, outputs: {1}", _midiInputCombo.Items.Count, _midiOutputCombo.Items.Count);
    }

    private static string PickDeviceText(ComboBox combo, string saved, string previous)
    {
        foreach (var candidate in new[] { saved, previous })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && combo.Items.Contains(candidate))
                return candidate;
        }
        return combo.Items.Count > 0 ? combo.Items[0]?.ToString() ?? "" : "";
    }

    private void ConfigurePollTimer()
    {
        _pollTimer.Interval = (int)_pollMs.Value;
        _pollTimer.Tick += async (_, _) => await PollOnceAsync();
    }

    private void ToggleConnection()
    {
        if (_connected)
        {
            Disconnect();
            return;
        }

        try
        {
            SaveProfile();
            _vmix.Configure(_profile.VMixHost, _profile.VMixHttpPort, _profile.VMixTcpPort);
            _midi.Open(_profile.MidiInputName, _profile.MidiOutputName);
            _pollCts = new CancellationTokenSource();
            _pollTimer.Interval = _profile.PollIntervalMs;
            _pollTimer.Start();
            _connected = true;
            _connectButton.Text = "Disconnect";
            _statusLabel.Text = "Connected to MIDI. Waiting for vMix state...";
            _logger.Info("Bridge connected");
            _ = PollOnceAsync();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Connect failed");
            MessageBox.Show(this, ex.Message, "Connect failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Disconnect();
        }
    }

    private void Disconnect()
    {
        _pollTimer.Stop();
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;
        _midi.Close();
        _connected = false;
        _connectButton.Text = "Connect";
        _statusLabel.Text = "Disconnected";
        _logger.Info("Bridge disconnected");
    }

    private async Task PollOnceAsync()
    {
        if (_pollCts?.IsCancellationRequested != false)
            return;

        try
        {
            _state = await _vmix.GetStateAsync(_pollCts.Token);
            _statusLabel.Text = $"{_state.Status} MIDI in: {_midi.InputOpen}. MIDI out: {_midi.OutputOpen}.";
            UpdateInputColumnDataSource();
            UpdateLiveGridAndHardware();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Poll failed");
            _statusLabel.Text = $"Poll failed: {ex.Message}";
        }
    }

    private void UpdateInputColumnDataSource()
    {
        var choices = _state.Inputs.Count == 0
            ? new List<InputChoice> { new("", "No vMix inputs loaded") }
            : _state.Inputs.Select(input => new InputChoice(input.Key, $"{input.Number}: {input.Title}")).ToList();

        if (_grid.Columns["InputKey"] is DataGridViewComboBoxColumn combo)
            combo.DataSource = choices;
    }

    private void UpdateLiveGridAndHardware()
    {
        var assignments = ReadAssignmentsFromGrid();
        for (var i = 0; i < assignments.Count; i++)
        {
            var assignment = assignments[i];
            var live = ResolveLiveChannel(assignment);
            if (i < _grid.Rows.Count)
            {
                _grid.Rows[i].Cells["LiveVolume"].Value = live.VolumePercent.HasValue
                    ? live.VolumePercent.Value.ToString("0", CultureInfo.InvariantCulture)
                    : "";
                _grid.Rows[i].Cells["Meter"].Value = live.MeterPercent.HasValue
                    ? live.MeterPercent.Value.ToString("0", CultureInfo.InvariantCulture)
                    : "";
            }

            if (_profile.SendMotorFaderFeedback &&
                live.VolumePercent.HasValue &&
                DateTime.Now - _lastFaderTouch[i] > TimeSpan.FromMilliseconds(750))
            {
                _midi.SendPitchBend(i, PercentToFourteenBit(live.VolumePercent.Value));
            }

            if (_profile.SendMackieScribbleStripText)
                _midi.SendMackieScribbleText(i, live.Label);

            if (live.MeterPercent.HasValue)
                _midi.SendMackieMeter(i, (int)Math.Round(live.MeterPercent.Value / 100.0 * 12));
        }
    }

    private void SaveProfile()
    {
        _profile.VMixHost = _hostText.Text.Trim();
        _profile.VMixHttpPort = (int)_httpPort.Value;
        _profile.VMixTcpPort = (int)_tcpPort.Value;
        _profile.PollIntervalMs = (int)_pollMs.Value;
        _profile.MidiInputName = _midiInputCombo.Text.Trim();
        _profile.MidiOutputName = _midiOutputCombo.Text.Trim();
        _profile.SendMotorFaderFeedback = _motorFeedback.Checked;
        _profile.SendMackieScribbleStripText = _displayText.Checked;
        _profile.Channels = ReadAssignmentsFromGrid();
        _profile.Save(AppPaths.ConfigFile);
        _logger.Info("Saved profile to {0}", AppPaths.ConfigFile);
    }

    private List<ChannelAssignment> ReadAssignmentsFromGrid()
    {
        var assignments = new List<ChannelAssignment>();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.IsNewRow)
                continue;

            var channel = Convert.ToInt32(row.Cells["Channel"].Value, CultureInfo.InvariantCulture);
            var kind = row.Cells["Kind"].Value is AssignmentKind assignmentKind
                ? assignmentKind
                : Enum.TryParse<AssignmentKind>(row.Cells["Kind"].Value?.ToString(), out var parsed) ? parsed : AssignmentKind.None;
            var inputKey = row.Cells["InputKey"].Value?.ToString();
            var label = row.Cells["Label"].Value?.ToString() ?? "";
            assignments.Add(new ChannelAssignment
            {
                Channel = channel,
                Kind = kind,
                InputKey = inputKey,
                LabelOverride = label,
                FollowInputName = string.IsNullOrWhiteSpace(label)
            });
        }
        return assignments;
    }

    private LiveChannel ResolveLiveChannel(ChannelAssignment assignment)
    {
        if (assignment.Kind == AssignmentKind.Input && !string.IsNullOrWhiteSpace(assignment.InputKey))
        {
            var input = _state.Inputs.FirstOrDefault(candidate => candidate.Key == assignment.InputKey);
            if (input is not null)
            {
                var label = string.IsNullOrWhiteSpace(assignment.LabelOverride) ? input.Title : assignment.LabelOverride;
                return new LiveChannel(label, XmlVolumeToFaderPercent(input.Volume), Math.Max(input.MeterF1, input.MeterF2) * 100);
            }
        }

        var outputName = assignment.Kind switch
        {
            AssignmentKind.Master => "masterVolume",
            AssignmentKind.BusA => "busAVolume",
            AssignmentKind.BusB => "busBVolume",
            AssignmentKind.BusC => "busCVolume",
            AssignmentKind.BusD => "busDVolume",
            AssignmentKind.BusE => "busEVolume",
            AssignmentKind.BusF => "busFVolume",
            AssignmentKind.BusG => "busGVolume",
            _ => ""
        };

        if (!string.IsNullOrWhiteSpace(outputName) && _state.OutputVolumes.TryGetValue(outputName, out var volume))
        {
            var label = string.IsNullOrWhiteSpace(assignment.LabelOverride) ? assignment.Kind.ToString() : assignment.LabelOverride;
            return new LiveChannel(label, XmlVolumeToFaderPercent(volume), null);
        }

        return new LiveChannel(string.IsNullOrWhiteSpace(assignment.LabelOverride) ? assignment.Kind.ToString() : assignment.LabelOverride, null, null);
    }

    private async void OnMidiMessageReceived(object? sender, MidiMessageEventArgs e)
    {
        if (!_connected || _pollCts?.IsCancellationRequested != false)
            return;

        try
        {
            var assignments = ReadAssignmentsFromGrid();
            if (e.Command == 0xE0 && e.Channel is >= 0 and < 8)
            {
                var value14 = e.Data1 | (e.Data2 << 7);
                var percent = value14 / 16383.0 * 100.0;
                _lastFaderTouch[e.Channel] = DateTime.Now;
                _logger.Debug("MIDI fader ch {0}: {1:0.##}", e.Channel + 1, percent);
                await _vmix.SetAssignmentVolumeAsync(assignments[e.Channel], percent, _pollCts.Token);
            }
            else if (e.Command == 0x90 && e.Data2 > 0 && e.Data1 is >= 16 and <= 23)
            {
                var channel = e.Data1 - 16;
                _logger.Debug("MIDI mute button ch {0}", channel + 1);
                await _vmix.ToggleMuteAsync(assignments[channel], _pollCts.Token);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "MIDI action failed. Raw message {0:X6}", e.RawMessage);
            BeginInvoke(new MethodInvoker(() => _statusLabel.Text = $"MIDI action failed: {ex.Message}"));
        }
    }

    private static int PercentToFourteenBit(double percent) => (int)Math.Round(Math.Clamp(percent, 0, 100) / 100.0 * 16383);

    private static double XmlVolumeToFaderPercent(double xmlVolume)
    {
        // vMix XML volume is amplitude scaled 0-100; the API fader position is 0-100.
        var amplitude = Math.Clamp(xmlVolume, 0, 100) / 100.0;
        return Math.Pow(amplitude, 0.25) * 100.0;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SaveProfile();
        Disconnect();
        _vmix.Dispose();
        _midi.Dispose();
        base.OnFormClosing(e);
    }

    private sealed record InputChoice(string Key, string Title);
    private sealed record LiveChannel(string Label, double? VolumePercent, double? MeterPercent);
}
