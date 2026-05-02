using System.Diagnostics;
using System.Drawing;
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
    private readonly NumericUpDown _faderWriteMs = new();
    private readonly NumericUpDown _motorHoldMs = new();
    private readonly CheckBox _motorFeedback = new();
    private readonly CheckBox _displayText = new();
    private readonly Label _statusLabel = new();
    private readonly Label _logLabel = new();
    private readonly Button _connectButton = new();
    private readonly Button _stopBridgeButton = new();
    private readonly Button _saveButton = new();
    private readonly Button _refreshVmixButton = new();
    private readonly Button _openMidiButton = new();
    private readonly Button _testMidiButton = new();
    private VMixState _state = new();
    private CancellationTokenSource? _pollCts;
    private bool _connected;
    private bool _updatingGrid;
    private string _lastMidiMessage = "none";
    private readonly DateTime[] _lastFaderTouch = Enumerable.Repeat(DateTime.MinValue, 8).ToArray();
    private readonly DateTime[] _lastVmixFaderSend = Enumerable.Repeat(DateTime.MinValue, 8).ToArray();
    private readonly DateTime[] _suppressIncomingFaderUntil = Enumerable.Repeat(DateTime.MinValue, 8).ToArray();
    private readonly double[] _lastSentFaderPercent = Enumerable.Repeat(double.NaN, 8).ToArray();
    private readonly int[] _lastMotorFeedbackValue = Enumerable.Repeat(-1, 8).ToArray();

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
            RowCount = 5,
            Padding = new Padding(12)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 126));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        Controls.Add(root);

        var settings = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 10,
            RowCount = 3
        };
        for (var i = 0; i < 10; i++)
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 10f));

        root.Controls.Add(settings, 0, 0);

        AddLabeled(settings, "vMix Host", _hostText, 0, 0, 3);
        AddLabeled(settings, "HTTP Port", _httpPort, 3, 0, 2);
        AddLabeled(settings, "TCP Port", _tcpPort, 5, 0, 2);
        AddLabeled(settings, "Poll ms", _pollMs, 7, 0, 3);
        AddLabeled(settings, "MIDI Input", _midiInputCombo, 0, 1, 5);
        AddLabeled(settings, "MIDI Output", _midiOutputCombo, 5, 1, 5);
        AddLabeled(settings, "Fader ms", _faderWriteMs, 0, 2, 2);
        AddLabeled(settings, "Motor hold ms", _motorHoldMs, 2, 2, 3);

        _httpPort.Minimum = 1;
        _httpPort.Maximum = 65535;
        _tcpPort.Minimum = 1;
        _tcpPort.Maximum = 65535;
        _pollMs.Minimum = 100;
        _pollMs.Maximum = 5000;
        _pollMs.Increment = 50;
        _faderWriteMs.Minimum = 25;
        _faderWriteMs.Maximum = 500;
        _faderWriteMs.Increment = 25;
        _motorHoldMs.Minimum = 250;
        _motorHoldMs.Maximum = 5000;
        _motorHoldMs.Increment = 250;
        _midiInputCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _midiOutputCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _midiInputCombo.SelectedIndexChanged += (_, _) => SuggestMatchingMidiOutput();

        _motorFeedback.Text = "Motor fader feedback";
        _motorFeedback.Checked = true;
        _displayText.Text = "Display text feedback";
        _displayText.Checked = true;
        _connectButton.Text = "Start Bridge";
        _connectButton.Click += (_, _) => ToggleConnection();
        _stopBridgeButton.Text = "Stop Bridge";
        _stopBridgeButton.Enabled = false;
        _stopBridgeButton.Click += (_, _) => Disconnect();
        _saveButton.Text = "Save Profile";
        _saveButton.Click += (_, _) => SaveProfile();
        _refreshVmixButton.Text = "Refresh vMix";
        _refreshVmixButton.Click += async (_, _) => await RefreshVmixInputsAsync();
        _openMidiButton.Text = "Open MIDI";
        _openMidiButton.Click += (_, _) => ToggleMidi();
        _testMidiButton.Text = "Test MIDI";
        _testMidiButton.Click += (_, _) => TestMidiOutput();
        var refreshMidi = new Button { Text = "Refresh MIDI", Dock = DockStyle.Fill };
        refreshMidi.Click += (_, _) => RefreshMidiDevices();

        var commandBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 6, 0, 4)
        };
        commandBar.Controls.Add(refreshMidi);
        commandBar.Controls.Add(_refreshVmixButton);
        commandBar.Controls.Add(_saveButton);
        commandBar.Controls.Add(_openMidiButton);
        commandBar.Controls.Add(_testMidiButton);
        commandBar.Controls.Add(_connectButton);
        commandBar.Controls.Add(_stopBridgeButton);
        commandBar.Controls.Add(_motorFeedback);
        commandBar.Controls.Add(_displayText);
        foreach (Control control in commandBar.Controls)
        {
            control.Width = control is CheckBox ? 150 : 104;
            control.Height = 28;
            control.Margin = new Padding(0, 0, 8, 0);
        }
        root.Controls.Add(commandBar, 0, 1);

        ConfigureGrid();
        root.Controls.Add(_grid, 0, 2);

        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.AutoEllipsis = true;
        root.Controls.Add(_statusLabel, 0, 3);

        _logLabel.Dock = DockStyle.Fill;
        _logLabel.TextAlign = ContentAlignment.MiddleLeft;
        _logLabel.Text = $"Logs: {_logger.CurrentLogFile}";
        _logLabel.Cursor = Cursors.Hand;
        _logLabel.Click += (_, _) => Process.Start("explorer.exe", $"/select,\"{_logger.CurrentLogFile}\"");
        root.Controls.Add(_logLabel, 0, 4);
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
        _grid.DataError += (_, e) =>
        {
            _logger.Warn("Grid data error at row {0}, column {1}: {2}", e.RowIndex, e.ColumnIndex, e.Exception?.Message ?? "Unknown error");
            e.ThrowException = false;
        };
        _grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || _grid.Columns[e.ColumnIndex].Name != "StripColor")
                return;

            var colorName = e.Value?.ToString();
            if (e.CellStyle is not null && Enum.TryParse<StripColor>(colorName, out var color))
            {
                e.CellStyle.BackColor = ToDrawingColor(color);
                e.CellStyle.ForeColor = color is StripColor.Off or StripColor.Blue or StripColor.Purple or StripColor.Red
                    ? Color.White
                    : Color.Black;
            }
        };
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
            FillWeight = 120
        });
        _grid.Columns.Add(new DataGridViewComboBoxColumn
        {
            Name = "StripColor",
            HeaderText = "Strip Color",
            DataSource = Enum.GetValues<StripColor>(),
            FillWeight = 90
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
        _faderWriteMs.Value = _profile.FaderWriteIntervalMs;
        _motorHoldMs.Value = _profile.MotorFeedbackHoldMs;
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
                _grid.Rows.Add(assignment.Channel, assignment.Kind, assignment.InputKey ?? "", assignment.LabelOverride, assignment.StripColor, "", "");
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
        {
            _midiInputCombo.Items.Add(input.Name);
            _logger.Info("MIDI input device: {0}: {1}", input.Id, input.Name);
        }
        foreach (var output in MidiDeviceManager.GetOutputs())
        {
            _midiOutputCombo.Items.Add(output.Name);
            _logger.Info("MIDI output device: {0}: {1}", output.Id, output.Name);
        }

        _midiInputCombo.Text = PickDeviceText(_midiInputCombo, _profile.MidiInputName, inputName);
        _midiOutputCombo.Text = PickMidiOutputText(_midiOutputCombo, _profile.MidiOutputName, outputName, _midiInputCombo.Text);
        _logger.Info("MIDI refresh complete. Inputs: {0}, outputs: {1}", _midiInputCombo.Items.Count, _midiOutputCombo.Items.Count);
        _logger.Info("Selected MIDI input '{0}', output '{1}'", _midiInputCombo.Text, _midiOutputCombo.Text);
        UpdateStatus();
    }

    private static string PickDeviceText(ComboBox combo, string saved, string previous)
    {
        foreach (var candidate in new[] { saved, previous })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && combo.Items.Contains(candidate))
                return candidate;
        }
        foreach (var item in combo.Items.Cast<object>().Select(item => item.ToString() ?? ""))
        {
            if (LooksLikeIconDevice(item))
                return item;
        }
        return combo.Items.Count > 0 ? combo.Items[0]?.ToString() ?? "" : "";
    }

    private static string PickMidiOutputText(ComboBox combo, string saved, string previous, string selectedInput)
    {
        foreach (var candidate in new[] { saved, previous })
        {
            if (!string.IsNullOrWhiteSpace(candidate) &&
                combo.Items.Contains(candidate) &&
                !IsMicrosoftSynth(candidate))
            {
                return candidate;
            }
        }

        foreach (var item in combo.Items.Cast<object>().Select(item => item.ToString() ?? ""))
        {
            if (SameIconFamily(item, selectedInput))
                return item;
        }

        foreach (var item in combo.Items.Cast<object>().Select(item => item.ToString() ?? ""))
        {
            if (LooksLikeIconDevice(item) && !IsMicrosoftSynth(item))
                return item;
        }

        foreach (var item in combo.Items.Cast<object>().Select(item => item.ToString() ?? ""))
        {
            if (!IsMicrosoftSynth(item))
                return item;
        }

        return "";
    }

    private void SuggestMatchingMidiOutput()
    {
        if (_midiOutputCombo.Items.Count == 0)
            return;

        if (string.IsNullOrWhiteSpace(_midiOutputCombo.Text) || IsMicrosoftSynth(_midiOutputCombo.Text))
            _midiOutputCombo.Text = PickMidiOutputText(_midiOutputCombo, "", "", _midiInputCombo.Text);
    }

    private static bool LooksLikeIconDevice(string name) =>
        name.Contains("iCON", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("P1", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("V1", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("MIDIIN", StringComparison.OrdinalIgnoreCase) && name.Contains("P1", StringComparison.OrdinalIgnoreCase);

    private static bool SameIconFamily(string output, string input)
    {
        if (string.IsNullOrWhiteSpace(output) || string.IsNullOrWhiteSpace(input))
            return false;
        if (!LooksLikeIconDevice(output) || IsMicrosoftSynth(output))
            return false;

        var normalizedInput = input
            .Replace("MIDIIN", "", StringComparison.OrdinalIgnoreCase)
            .Replace("MIDIOUT", "", StringComparison.OrdinalIgnoreCase)
            .Trim();
        return output.Contains(normalizedInput, StringComparison.OrdinalIgnoreCase) ||
            normalizedInput.Contains("P1", StringComparison.OrdinalIgnoreCase) && output.Contains("P1", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMicrosoftSynth(string name) => name.Contains("Microsoft GS Wavetable", StringComparison.OrdinalIgnoreCase);

    private void ConfigurePollTimer()
    {
        _pollTimer.Interval = (int)_pollMs.Value;
        _pollTimer.Tick += async (_, _) => await PollOnceAsync();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await RefreshVmixInputsAsync();
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
            if (IsMicrosoftSynth(_profile.MidiOutputName))
                throw new InvalidOperationException("The selected MIDI output is Microsoft GS Wavetable Synth. Select the iCON P1-M output port instead.");

            _vmix.Configure(_profile.VMixHost, _profile.VMixHttpPort, _profile.VMixTcpPort);
            _midi.Open(_profile.MidiInputName, _profile.MidiOutputName);
            if (!_midi.InputOpen || !_midi.OutputOpen)
                throw new InvalidOperationException($"MIDI did not fully open. Input open: {_midi.InputOpen}. Output open: {_midi.OutputOpen}. Check selected P1-M ports.");
            _pollCts = new CancellationTokenSource();
            _pollTimer.Interval = _profile.PollIntervalMs;
            _pollTimer.Start();
            _connected = true;
            _connectButton.Enabled = false;
            _stopBridgeButton.Enabled = true;
            _openMidiButton.Text = "Close MIDI";
            _logger.Info("Bridge connected");
            UpdateStatus();
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
        _connectButton.Enabled = true;
        _stopBridgeButton.Enabled = false;
        _openMidiButton.Text = "Open MIDI";
        UpdateStatus();
        _logger.Info("Bridge disconnected");
    }

    private void ToggleMidi()
    {
        try
        {
            if (_midi.InputOpen || _midi.OutputOpen)
            {
                _midi.Close();
                _openMidiButton.Text = "Open MIDI";
                _lastMidiMessage = "none";
                UpdateStatus();
                return;
            }

            SaveProfile();
            if (IsMicrosoftSynth(_profile.MidiOutputName))
                throw new InvalidOperationException("The selected MIDI output is Microsoft GS Wavetable Synth. Select the iCON P1-M output port instead.");

            _midi.Open(_profile.MidiInputName, _profile.MidiOutputName);
            _openMidiButton.Text = "Close MIDI";
            _logger.Info("MIDI opened from UI. Input open: {0}, output open: {1}", _midi.InputOpen, _midi.OutputOpen);
            UpdateStatus();
            UpdateLiveGridAndHardware();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Open MIDI failed");
            UpdateStatus($"MIDI open failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Open MIDI failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void TestMidiOutput()
    {
        try
        {
            if (!_midi.OutputOpen)
                ToggleMidi();

            if (!_midi.OutputOpen)
                throw new InvalidOperationException("MIDI output is still closed after attempting to open it.");

            for (var i = 0; i < 8; i++)
            {
                _midi.SendMackieScribbleText(i, $"VMIX {i + 1}");
                _midi.SendPitchBend(i, PercentToFourteenBit(i % 2 == 0 ? 75 : 25));
                _midi.SendMackieMeter(i, 8);
            }
            _midi.SendIconDisplayColors(Enum.GetValues<StripColor>()
                .Where(color => color != StripColor.Off)
                .Take(8)
                .ToList());
            _logger.Info("Sent MIDI hardware test to output '{0}'", _midi.OpenOutputName);
            UpdateStatus("Sent MIDI test: labels VMIX 1-8, colors, alternating faders, meters");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "MIDI test failed");
            UpdateStatus($"MIDI test failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, "MIDI test failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task PollOnceAsync()
    {
        if (_pollCts?.IsCancellationRequested != false)
            return;

        await RefreshVmixStateAsync(_pollCts.Token);
    }

    private async Task RefreshVmixInputsAsync()
    {
        SaveProfile();
        _vmix.Configure(_profile.VMixHost, _profile.VMixHttpPort, _profile.VMixTcpPort);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await RefreshVmixStateAsync(cts.Token);
    }

    private async Task RefreshVmixStateAsync(CancellationToken cancellationToken)
    {
        try
        {
            _state = await _vmix.GetStateAsync(cancellationToken);
            UpdateInputColumnDataSource();
            UpdateLiveGridAndHardware();
            UpdateStatus();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Poll failed");
            UpdateStatus($"vMix poll failed: {ex.Message}");
        }
    }

    private void UpdateStatus(string? alert = null)
    {
        var midiInput = _midi.InputOpen ? $"open ({_midi.OpenInputName})" : $"closed (selected: {EmptyIfBlank(_midiInputCombo.Text)})";
        var midiOutput = _midi.OutputOpen ? $"open ({_midi.OpenOutputName})" : $"closed (selected: {EmptyIfBlank(_midiOutputCombo.Text)})";
        var bridge = _connected ? "running" : "stopped";
        var vmix = _state.Connected ? _state.Status : $"vMix not connected: {_state.Status}";
        _statusLabel.Text = alert is null
            ? $"Bridge: {bridge} | {vmix} | MIDI In: {midiInput} | MIDI Out: {midiOutput} | Last MIDI: {_lastMidiMessage}"
            : $"{alert} | Bridge: {bridge} | {vmix} | MIDI In: {midiInput} | MIDI Out: {midiOutput}";
    }

    private static string EmptyIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? "<none>" : value.Trim();

    private void UpdateInputColumnDataSource()
    {
        var choices = new List<InputChoice>
        {
            new("", _state.Inputs.Count == 0 ? "No vMix inputs loaded" : "(none)")
        };
        choices.AddRange(_state.Inputs.Select(input => new InputChoice(input.Key, $"{input.Number}: {input.Title}")));
        foreach (var savedKey in _profile.Channels
            .Select(channel => channel.InputKey)
            .Concat(_grid.Rows.Cast<DataGridViewRow>().Select(row => row.Cells["InputKey"].Value?.ToString()))
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!choices.Any(choice => choice.Key.Equals(savedKey, StringComparison.OrdinalIgnoreCase)))
                choices.Add(new InputChoice(savedKey!, $"Missing input ({savedKey})"));
        }

        if (_grid.Columns["InputKey"] is DataGridViewComboBoxColumn combo)
            combo.DataSource = choices;
    }

    private void UpdateLiveGridAndHardware()
    {
        var assignments = ReadAssignmentsFromGrid();
        _midi.SendIconDisplayColors(assignments.Select(assignment => assignment.StripColor).ToList());
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
                DateTime.Now - _lastFaderTouch[i] > TimeSpan.FromMilliseconds(_profile.MotorFeedbackHoldMs))
            {
                var feedbackValue = PercentToFourteenBit(live.VolumePercent.Value);
                if (Math.Abs(feedbackValue - _lastMotorFeedbackValue[i]) > 8)
                {
                    _lastMotorFeedbackValue[i] = feedbackValue;
                    _suppressIncomingFaderUntil[i] = DateTime.Now.AddMilliseconds(250);
                    _midi.SendPitchBend(i, feedbackValue);
                }
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
        _profile.FaderWriteIntervalMs = (int)_faderWriteMs.Value;
        _profile.MotorFeedbackHoldMs = (int)_motorHoldMs.Value;
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
            var color = row.Cells["StripColor"].Value is StripColor stripColor
                ? stripColor
                : Enum.TryParse<StripColor>(row.Cells["StripColor"].Value?.ToString(), out var parsedColor) ? parsedColor : StripColor.Blue;
            assignments.Add(new ChannelAssignment
            {
                Channel = channel,
                Kind = kind,
                InputKey = inputKey,
                LabelOverride = label,
                FollowInputName = string.IsNullOrWhiteSpace(label),
                StripColor = color
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
        {
            _lastMidiMessage = $"ignored {e.Status:X2} {e.Data1:X2} {e.Data2:X2}";
            _logger.Debug("MIDI ignored while bridge stopped: status {0:X2}, data1 {1:X2}, data2 {2:X2}, channel {3}", e.Status, e.Data1, e.Data2, e.Channel + 1);
            BeginInvoke(new MethodInvoker(() => UpdateStatus()));
            return;
        }

        try
        {
            _lastMidiMessage = $"{e.Status:X2} {e.Data1:X2} {e.Data2:X2} ch {e.Channel + 1}";
            _logger.Debug("MIDI received: status {0:X2}, data1 {1:X2}, data2 {2:X2}, channel {3}, command {4:X2}", e.Status, e.Data1, e.Data2, e.Channel + 1, e.Command);
            var assignments = ReadAssignmentsFromGrid();
            if (e.Command == 0xE0 && e.Channel is >= 0 and < 8)
            {
                var value14 = e.Data1 | (e.Data2 << 7);
                var percent = value14 / 16383.0 * 100.0;
                if (DateTime.Now < _suppressIncomingFaderUntil[e.Channel] &&
                    Math.Abs(value14 - _lastMotorFeedbackValue[e.Channel]) <= 64)
                {
                    _logger.Debug("Suppressed motor-feedback echo ch {0}: {1:0.##}", e.Channel + 1, percent);
                    return;
                }

                _lastFaderTouch[e.Channel] = DateTime.Now;
                if (DateTime.Now - _lastVmixFaderSend[e.Channel] < TimeSpan.FromMilliseconds(_profile.FaderWriteIntervalMs) &&
                    !double.IsNaN(_lastSentFaderPercent[e.Channel]) &&
                    Math.Abs(percent - _lastSentFaderPercent[e.Channel]) < 1.5)
                {
                    _logger.Debug("Rate-limited MIDI fader ch {0}: {1:0.##}", e.Channel + 1, percent);
                    return;
                }

                _lastVmixFaderSend[e.Channel] = DateTime.Now;
                _lastSentFaderPercent[e.Channel] = percent;
                _logger.Debug("MIDI fader ch {0}: {1:0.##}", e.Channel + 1, percent);
                await _vmix.SetAssignmentVolumeAsync(assignments[e.Channel], percent, _pollCts.Token);
                BeginInvoke(new MethodInvoker(() =>
                {
                    if (e.Channel < _grid.Rows.Count)
                        _grid.Rows[e.Channel].Cells["LiveVolume"].Value = percent.ToString("0", CultureInfo.InvariantCulture);
                    UpdateStatus();
                }));
            }
            else if (e.Command == 0x90 && e.Data2 > 0 && e.Data1 is >= 16 and <= 23)
            {
                var channel = e.Data1 - 16;
                _logger.Debug("MIDI mute button ch {0}", channel + 1);
                await _vmix.ToggleMuteAsync(assignments[channel], _pollCts.Token);
                BeginInvoke(new MethodInvoker(() => UpdateStatus()));
            }
            else
            {
                BeginInvoke(new MethodInvoker(() => UpdateStatus("MIDI received, but not mapped to a fader/mute action")));
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "MIDI action failed. Raw message {0:X6}", e.RawMessage);
            BeginInvoke(new MethodInvoker(() => UpdateStatus($"MIDI action failed: {ex.Message}")));
        }
    }

    private static int PercentToFourteenBit(double percent) => (int)Math.Round(Math.Clamp(percent, 0, 100) / 100.0 * 16383);

    private static Color ToDrawingColor(StripColor color) => color switch
    {
        StripColor.Off => Color.Black,
        StripColor.White => Color.White,
        StripColor.Red => Color.Firebrick,
        StripColor.Orange => Color.Orange,
        StripColor.Yellow => Color.Gold,
        StripColor.Green => Color.ForestGreen,
        StripColor.Cyan => Color.DarkTurquoise,
        StripColor.Blue => Color.RoyalBlue,
        StripColor.Purple => Color.MediumPurple,
        StripColor.Pink => Color.HotPink,
        _ => Color.RoyalBlue
    };

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
