using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using Microsoft.Win32;
using System.Windows.Forms;

namespace IconP1MVmixBridge;

public sealed class MainForm : Form
{
    private const int ReleaseEchoGuardMs = 100;
    private const int MotorEchoSuppressMs = 250;
    private const int UntouchedAutoGrabRawDelta = 96;
    private readonly FileLogger _logger;
    private readonly BridgeProfile _profile;
    private readonly VMixClient _vmix;
    private readonly MidiDeviceManager _midi;
    private readonly BridgeApiServer _apiServer;
    private readonly System.Windows.Forms.Timer _pollTimer = new();
    private readonly System.Windows.Forms.Timer _faderSendTimer = new();
    private readonly DataGridView _grid = new();
    private readonly ComboBox _midiInputCombo = new();
    private readonly ComboBox _midiOutputCombo = new();
    private readonly TextBox _hostText = new();
    private readonly NumericUpDown _httpPort = new();
    private readonly NumericUpDown _tcpPort = new();
    private readonly NumericUpDown _pollMs = new();
    private readonly NumericUpDown _faderWriteMs = new();
    private readonly NumericUpDown _motorHoldMs = new();
    private readonly NumericUpDown _apiPort = new();
    private readonly NumericUpDown _channelCount = new();
    private readonly CheckBox _motorFeedback = new();
    private readonly CheckBox _displayText = new();
    private readonly CheckBox _minimizeToTray = new();
    private readonly CheckBox _startWithWindows = new();
    private readonly Label _statusLabel = new();
    private readonly Label _logLabel = new();
    private readonly Button _connectButton = new();
    private readonly Button _stopBridgeButton = new();
    private readonly Button _saveButton = new();
    private readonly Button _refreshVmixButton = new();
    private readonly Button _openMidiButton = new();
    private readonly Button _testMidiButton = new();
    private readonly Button _previousBankButton = new();
    private readonly Button _nextBankButton = new();
    private readonly Label _bankLabel = new();
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
    private readonly double[] _pendingFaderPercent = Enumerable.Repeat(double.NaN, 8).ToArray();
    private readonly bool[] _pendingFaderDirty = new bool[8];
    private readonly bool[] _faderSendInFlight = new bool[8];
    private readonly string[] _lastStripLabels = Enumerable.Repeat("", 8).ToArray();
    private readonly bool[] _faderTouched = new bool[8];
    private readonly DateTime[] _ignoreLocalFaderUntil = Enumerable.Repeat(DateTime.MinValue, 8).ToArray();
    private readonly NotifyIcon _trayIcon = new();
    private bool _allowExit;
    private bool _gridEditing;
    private string _lastInputChoicesSignature = "";
    private int _bankStartChannel;
    private bool _forceNextHardwareRefresh;

    public MainForm(FileLogger logger)
    {
        _logger = logger;
        _profile = BridgeProfile.LoadOrCreate(AppPaths.ConfigFile, logger);
        _vmix = new VMixClient(logger);
        _midi = new MidiDeviceManager(logger);
        _apiServer = new BridgeApiServer(logger, GetApiSnapshotThreadSafe, SetAssignmentFromApiAsync);
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
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
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
        AddLabeled(settings, "API Port", _apiPort, 5, 2, 2);
        AddLabeled(settings, "Channels", _channelCount, 7, 2, 3);

        _httpPort.Minimum = 1;
        _httpPort.Maximum = 65535;
        _tcpPort.Minimum = 1;
        _tcpPort.Maximum = 65535;
        _pollMs.Minimum = 50;
        _pollMs.Maximum = 5000;
        _pollMs.Increment = 50;
        _faderWriteMs.Minimum = 10;
        _faderWriteMs.Maximum = 500;
        _faderWriteMs.Increment = 5;
        _motorHoldMs.Minimum = 250;
        _motorHoldMs.Maximum = 5000;
        _motorHoldMs.Increment = 250;
        _apiPort.Minimum = 1;
        _apiPort.Maximum = 65535;
        _channelCount.Minimum = 8;
        _channelCount.Maximum = 64;
        _channelCount.Increment = 8;
        _midiInputCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _midiOutputCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _midiInputCombo.SelectedIndexChanged += (_, _) => SuggestMatchingMidiOutput();

        _motorFeedback.Text = "Motor fader feedback";
        _motorFeedback.Checked = true;
        _displayText.Text = "Display text feedback";
        _displayText.Checked = true;
        _minimizeToTray.Text = "Minimize to tray";
        _startWithWindows.Text = "Start with Windows";
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
        _previousBankButton.Text = "<< Bank";
        _previousBankButton.Click += (_, _) => ChangeBank(-8, "UI previous bank");
        _nextBankButton.Text = "Bank >>";
        _nextBankButton.Click += (_, _) => ChangeBank(8, "UI next bank");
        _bankLabel.Text = "Bank 1-8";
        _bankLabel.TextAlign = ContentAlignment.MiddleLeft;
        var refreshMidi = new Button { Text = "Refresh MIDI", Dock = DockStyle.Fill };
        refreshMidi.Click += (_, _) => RefreshMidiDevices();

        var commandBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Padding = new Padding(0, 6, 0, 4)
        };
        commandBar.Controls.Add(refreshMidi);
        commandBar.Controls.Add(_refreshVmixButton);
        commandBar.Controls.Add(_saveButton);
        commandBar.Controls.Add(_openMidiButton);
        commandBar.Controls.Add(_testMidiButton);
        commandBar.Controls.Add(_connectButton);
        commandBar.Controls.Add(_stopBridgeButton);
        commandBar.Controls.Add(_previousBankButton);
        commandBar.Controls.Add(_nextBankButton);
        commandBar.Controls.Add(_bankLabel);
        commandBar.Controls.Add(_motorFeedback);
        commandBar.Controls.Add(_displayText);
        commandBar.Controls.Add(_minimizeToTray);
        commandBar.Controls.Add(_startWithWindows);
        foreach (Control control in commandBar.Controls)
        {
            control.Width = control == _bankLabel ? 156 : control is CheckBox ? 132 : 104;
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

        ConfigureTrayIcon();
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

    private void ConfigureTrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show", null, (_, _) => ShowFromTray());
        menu.Items.Add("Start Bridge", null, (_, _) =>
        {
            if (!_connected)
                ToggleConnection();
        });
        menu.Items.Add("Stop Bridge", null, (_, _) =>
        {
            if (_connected)
                Disconnect();
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) =>
        {
            _allowExit = true;
            Close();
        });

        _trayIcon.Icon = SystemIcons.Application;
        _trayIcon.Text = "iCON P1-M vMix Bridge";
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.Visible = true;
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ConfigureGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.RowHeadersVisible = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.CellValueChanged += (_, e) =>
        {
            if (_updatingGrid || e.RowIndex < 0 || e.ColumnIndex < 0)
                return;

            var columnName = _grid.Columns[e.ColumnIndex].Name;
            if (columnName is "LiveVolume" or "Meter")
                return;

            SaveProfile();
        };
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
        _grid.CellBeginEdit += (_, _) => _gridEditing = true;
        _grid.CellEndEdit += (_, _) =>
        {
            _gridEditing = false;
            UpdateInputColumnDataSource();
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
            ValueType = typeof(StripColor),
            FlatStyle = FlatStyle.Flat,
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
        _apiPort.Value = _profile.ApiPort;
        _channelCount.Value = _profile.ChannelCount;
        _motorFeedback.Checked = _profile.SendMotorFaderFeedback;
        _displayText.Checked = _profile.SendMackieScribbleStripText;
        _minimizeToTray.Checked = _profile.MinimizeToTray;
        _startWithWindows.Checked = _profile.StartWithWindows;
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
                _grid.Rows.Add(assignment.Channel, assignment.Kind, assignment.InputKey ?? "", assignment.LabelOverride, NormalizeStripColor(assignment.StripColor), "", "");
            }
            ClampBankStart();
            UpdateBankUi();
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
        _faderSendTimer.Interval = (int)_faderWriteMs.Value;
        _faderSendTimer.Tick += (_, _) => FlushPendingFaders();
    }

    private void StartApiServer()
    {
        try
        {
            _apiServer.Start(_profile.ApiPort);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Could not start assignment API on port {0}", _profile.ApiPort);
            UpdateStatus($"Assignment API failed on port {_profile.ApiPort}: {ex.Message}");
        }
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        StartApiServer();
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
            _faderSendTimer.Interval = _profile.FaderWriteIntervalMs;
            _pollTimer.Start();
            _faderSendTimer.Start();
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
        _faderSendTimer.Stop();
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;
        _midi.Close();
        ResetHardwareFeedbackCache();
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
            ResetHardwareFeedbackCache();
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
            ResetHardwareFeedbackCache();
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

    private void FlushPendingFaders()
    {
        if (!_connected || _pollCts?.IsCancellationRequested != false)
            return;

        var assignments = ReadAssignmentsFromGrid();
        for (var channel = 0; channel < 8 && channel < assignments.Count; channel++)
        {
            if (!_pendingFaderDirty[channel] || _faderSendInFlight[channel])
                continue;

            var logical = LogicalIndexForPhysical(channel);
            if (logical < 0 || logical >= assignments.Count)
                continue;

            var percent = _pendingFaderPercent[channel];
            if (double.IsNaN(percent))
                continue;

            _pendingFaderDirty[channel] = false;
            _lastVmixFaderSend[channel] = DateTime.Now;
            _lastSentFaderPercent[channel] = percent;
            _faderSendInFlight[channel] = true;
            _ = SendFaderUpdateAsync(channel, assignments[logical], percent);
        }
    }

    private async Task SendFaderUpdateAsync(int channel, ChannelAssignment assignment, double percent)
    {
        try
        {
            await _vmix.SetAssignmentVolumeFastAsync(assignment, percent, _pollCts?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Shutdown path.
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Fast vMix fader update failed for channel {0}", channel + 1);
            BeginInvoke(new MethodInvoker(() => UpdateStatus($"Fader send failed: {ex.Message}")));
        }
        finally
        {
            _faderSendInFlight[channel] = false;
        }
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
        var api = $"API: 127.0.0.1:{(int)_apiPort.Value}";
        var bank = $"Bank: {_bankStartChannel + 1}-{Math.Min(_bankStartChannel + 8, Math.Max(_grid.Rows.Count, 8))}";
        _statusLabel.Text = alert is null
            ? $"Bridge: {bridge} | {vmix} | MIDI In: {midiInput} | MIDI Out: {midiOutput} | {bank} | {api} | Last MIDI: {_lastMidiMessage}"
            : $"{alert} | Bridge: {bridge} | {vmix} | MIDI In: {midiInput} | MIDI Out: {midiOutput} | {bank} | {api}";
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

        var signature = string.Join("|", choices.Select(choice => $"{choice.Key}={choice.Title}"));
        if (signature == _lastInputChoicesSignature)
            return;

        if (_gridEditing || _grid.IsCurrentCellInEditMode)
            return;

        if (_grid.Columns["InputKey"] is DataGridViewComboBoxColumn combo)
        {
            combo.DataSource = choices;
            _lastInputChoicesSignature = signature;
        }
    }

    private void UpdateLiveGridAndHardware()
    {
        var assignments = ReadAssignmentsFromGrid();
        ClampBankStart(assignments.Count);
        UpdateBankUi();
        var forceHardwareRefresh = _forceNextHardwareRefresh;
        _forceNextHardwareRefresh = false;
        _midi.SendIconDisplayColors(GetActiveBankAssignments(assignments).Select(assignment => assignment.StripColor).ToList());
        for (var i = 0; i < assignments.Count; i++)
        {
            var assignment = assignments[i];
            var live = ResolveLiveChannel(assignment);
            if (!_gridEditing && !_grid.IsCurrentCellInEditMode && i < _grid.Rows.Count)
            {
                SetGridCellValue(i, "LiveVolume", live.VolumePercent.HasValue
                    ? live.VolumePercent.Value.ToString("0", CultureInfo.InvariantCulture)
                    : "");
                SetGridCellValue(i, "Meter", live.MeterPercent.HasValue
                    ? live.MeterPercent.Value.ToString("0", CultureInfo.InvariantCulture)
                    : "");
            }

        }

        for (var physical = 0; physical < 8; physical++)
        {
            var logical = _bankStartChannel + physical;
            if (logical >= assignments.Count)
            {
                if (_profile.SendMackieScribbleStripText && !string.Equals(_lastStripLabels[physical], "", StringComparison.Ordinal))
                {
                    _lastStripLabels[physical] = "";
                    _midi.SendMackieScribbleText(physical, "");
                }
                _midi.SendMackieMeter(physical, 0);
                if (_profile.SendMotorFaderFeedback)
                    SendMotorFeedback(physical, 0, "inactive bank slot", force: forceHardwareRefresh);
                continue;
            }

            var assignment = assignments[logical];
            var live = ResolveLiveChannel(assignment);
            if (_profile.SendMotorFaderFeedback &&
                live.VolumePercent.HasValue &&
                !_faderTouched[physical] &&
                (forceHardwareRefresh || DateTime.Now - _lastFaderTouch[physical] > TimeSpan.FromMilliseconds(_profile.MotorFeedbackHoldMs)))
            {
                SendMotorFeedback(physical, live.VolumePercent.Value, $"vMix poll logical ch {logical + 1}", force: forceHardwareRefresh);
            }
            else if (_profile.SendMotorFaderFeedback && !live.VolumePercent.HasValue && !_faderTouched[physical])
            {
                SendMotorFeedback(physical, 0, $"no live volume logical ch {logical + 1}", force: forceHardwareRefresh);
            }

            if (_profile.SendMackieScribbleStripText && !string.Equals(_lastStripLabels[physical], live.Label, StringComparison.Ordinal))
            {
                _lastStripLabels[physical] = live.Label;
                _midi.SendMackieScribbleText(physical, live.Label);
            }

            if (live.MeterPercent.HasValue)
                _midi.SendMackieMeter(physical, MeterPercentToMackieLevel(live.MeterPercent.Value));
            else
                _midi.SendMackieMeter(physical, 0);
        }
    }

    private List<ChannelAssignment> GetActiveBankAssignments(IReadOnlyList<ChannelAssignment> assignments)
    {
        var active = new List<ChannelAssignment>();
        for (var physical = 0; physical < 8; physical++)
        {
            var logical = _bankStartChannel + physical;
            active.Add(logical < assignments.Count ? assignments[logical] : new ChannelAssignment { Channel = logical + 1 });
        }
        return active;
    }

    private int LogicalIndexForPhysical(int zeroBasedPhysicalChannel) => _bankStartChannel + zeroBasedPhysicalChannel;

    private void ClampBankStart(int? assignmentCount = null)
    {
        var count = assignmentCount ?? _grid.Rows.Count;
        var maxStart = Math.Max(0, count - 8);
        _bankStartChannel = Math.Clamp(_bankStartChannel, 0, maxStart);
    }

    private void ChangeBank(int delta, string reason)
    {
        var oldStart = _bankStartChannel;
        _bankStartChannel += delta;
        ClampBankStart();
        if (_bankStartChannel == oldStart)
        {
            _logger.Info("Bank unchanged at {0}-{1}; reason={2}", _bankStartChannel + 1, Math.Min(_bankStartChannel + 8, _grid.Rows.Count), reason);
            UpdateBankUi();
            return;
        }

        ResetHardwareFeedbackCache();
        _forceNextHardwareRefresh = true;
        UpdateBankUi();
        _logger.Info("Changed active P1-M bank to logical channels {0}-{1}; reason={2}",
            _bankStartChannel + 1,
            Math.Min(_bankStartChannel + 8, _grid.Rows.Count),
            reason);
        UpdateLiveGridAndHardware();
        UpdateStatus();
    }

    private void UpdateBankUi()
    {
        var end = Math.Min(_bankStartChannel + 8, Math.Max(_grid.Rows.Count, 8));
        _bankLabel.Text = $"Bank {_bankStartChannel + 1}-{end} of {_grid.Rows.Count}";
        _previousBankButton.Enabled = _bankStartChannel > 0;
        _nextBankButton.Enabled = _bankStartChannel + 8 < _grid.Rows.Count;
        for (var row = 0; row < _grid.Rows.Count; row++)
        {
            var inBank = row >= _bankStartChannel && row < _bankStartChannel + 8;
            _grid.Rows[row].DefaultCellStyle.BackColor = inBank ? Color.FromArgb(232, 242, 255) : Color.White;
        }
    }

    private void SendMotorFeedback(int zeroBasedChannel, double volumePercent, string reason, int suppressMs = MotorEchoSuppressMs, bool force = false)
    {
        var feedbackValue = PercentToFourteenBit(volumePercent);
        if (!force && Math.Abs(feedbackValue - _lastMotorFeedbackValue[zeroBasedChannel]) <= 8)
            return;

        _lastMotorFeedbackValue[zeroBasedChannel] = feedbackValue;
        _suppressIncomingFaderUntil[zeroBasedChannel] = DateTime.Now.AddMilliseconds(suppressMs);
        _logger.Debug("Motor feedback ch {0}: {1:0.##}% raw {2}; reason={3}; force={4}; suppress incoming until {5:HH:mm:ss.fff}",
            zeroBasedChannel + 1,
            volumePercent,
            feedbackValue,
            reason,
            force,
            _suppressIncomingFaderUntil[zeroBasedChannel]);
        _midi.SendPitchBend(zeroBasedChannel, feedbackValue);
    }

    private async Task SetChannelVolumeFromSurfaceAsync(int zeroBasedChannel, double volumePercent, string reason)
    {
        var assignments = ReadAssignmentsFromGrid();
        var logical = LogicalIndexForPhysical(zeroBasedChannel);
        if (zeroBasedChannel < 0 || zeroBasedChannel >= 8 || logical < 0 || logical >= assignments.Count)
            return;

        volumePercent = Math.Clamp(volumePercent, 0, 100);
        _pendingFaderDirty[zeroBasedChannel] = false;
        _pendingFaderPercent[zeroBasedChannel] = double.NaN;
        _lastSentFaderPercent[zeroBasedChannel] = volumePercent;

        _logger.Info("Surface button set physical channel {0}, logical channel {1} to {2:0.##}% ({3})",
            zeroBasedChannel + 1,
            logical + 1,
            volumePercent,
            reason);
        await _vmix.SetAssignmentVolumeFastAsync(assignments[logical], volumePercent, _pollCts?.Token ?? CancellationToken.None);
        SetGridCellValue(logical, "LiveVolume", volumePercent.ToString("0", CultureInfo.InvariantCulture));
        SendMotorFeedback(zeroBasedChannel, volumePercent, reason);
    }

    private void SetGridCellValue(int rowIndex, string columnName, object value)
    {
        if (rowIndex < 0 || rowIndex >= _grid.Rows.Count)
            return;

        var cell = _grid.Rows[rowIndex].Cells[columnName];
        if (Equals(cell.Value, value))
            return;

        var wasUpdating = _updatingGrid;
        _updatingGrid = true;
        try
        {
            cell.Value = value;
        }
        finally
        {
            _updatingGrid = wasUpdating;
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
        var previousApiPort = _profile.ApiPort;
        var previousChannelCount = _profile.ChannelCount;
        _profile.ApiPort = (int)_apiPort.Value;
        _profile.ChannelCount = (int)_channelCount.Value;
        _profile.MidiInputName = _midiInputCombo.Text.Trim();
        _profile.MidiOutputName = _midiOutputCombo.Text.Trim();
        _profile.SendMotorFaderFeedback = _motorFeedback.Checked;
        _profile.SendMackieScribbleStripText = _displayText.Checked;
        _profile.MinimizeToTray = _minimizeToTray.Checked;
        _profile.StartWithWindows = _startWithWindows.Checked;
        _profile.Channels = ReadAssignmentsFromGrid();
        _profile.Save(AppPaths.ConfigFile);
        ConfigureStartupRegistration();
        if (_profile.ApiPort != previousApiPort)
            StartApiServer();
        if (_profile.ChannelCount != previousChannelCount || _grid.Rows.Count != _profile.ChannelCount)
            PopulateGridRows();
        else
            UpdateLiveGridAndHardware();
        _logger.Info("Saved profile to {0}", AppPaths.ConfigFile);
    }

    private ApiSnapshot GetApiSnapshotThreadSafe()
    {
        if (!InvokeRequired)
            return BuildApiSnapshot();

        var source = new TaskCompletionSource<ApiSnapshot>();
        BeginInvoke(new MethodInvoker(() =>
        {
            try
            {
                source.SetResult(BuildApiSnapshot());
            }
            catch (Exception ex)
            {
                source.SetException(ex);
            }
        }));
        return source.Task.GetAwaiter().GetResult();
    }

    private ApiSnapshot BuildApiSnapshot()
    {
        var channels = ReadAssignmentsFromGrid().Select(ToApiChannel).ToList();
        var inputs = _state.Inputs
            .Select(input => new ApiInput(input.Number, input.Key, input.Title))
            .ToList();
        return new ApiSnapshot(
            channels,
            inputs,
            Enum.GetNames<AssignmentKind>(),
            Enum.GetNames<StripColor>());
    }

    private Task<ApiAssignmentResponse> SetAssignmentFromApiAsync(int channel, ApiAssignmentRequest request)
    {
        var source = new TaskCompletionSource<ApiAssignmentResponse>();
        BeginInvoke(new MethodInvoker(() =>
        {
            try
            {
                source.SetResult(ApplyApiAssignment(channel, request));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Assignment API update failed for channel {0}", channel);
                source.SetResult(new ApiAssignmentResponse(false, ex.Message));
            }
        }));
        return source.Task;
    }

    private ApiAssignmentResponse ApplyApiAssignment(int channel, ApiAssignmentRequest request)
    {
        if (channel < 1 || channel > _profile.ChannelCount)
            return new ApiAssignmentResponse(false, $"Channel must be 1-{_profile.ChannelCount}.");

        var rowIndex = channel - 1;
        if (rowIndex >= _grid.Rows.Count)
            return new ApiAssignmentResponse(false, $"Channel {channel} is not available.");

        var current = ReadAssignmentsFromGrid().FirstOrDefault(assignment => assignment.Channel == channel)
            ?? new ChannelAssignment { Channel = channel };
        var kind = current.Kind;
        if (!string.IsNullOrWhiteSpace(request.Kind) &&
            !Enum.TryParse(request.Kind, ignoreCase: true, out kind))
        {
            return new ApiAssignmentResponse(false, $"Unknown assignment kind '{request.Kind}'.");
        }

        var inputKey = current.InputKey;
        if (kind == AssignmentKind.Input)
        {
            inputKey = ResolveApiInputKey(request, inputKey);
            if (string.IsNullOrWhiteSpace(inputKey))
                return new ApiAssignmentResponse(false, "Input assignments require inputKey, inputNumber, or inputTitle.");
        }
        else
        {
            inputKey = "";
        }

        var color = current.StripColor;
        if (!string.IsNullOrWhiteSpace(request.StripColor) &&
            !Enum.TryParse(request.StripColor, ignoreCase: true, out color))
        {
            return new ApiAssignmentResponse(false, $"Unknown strip color '{request.StripColor}'.");
        }

        color = NormalizeStripColor(color);
        var label = request.LabelOverride ?? current.LabelOverride;
        var assignment = new ChannelAssignment
        {
            Channel = channel,
            Kind = kind,
            InputKey = inputKey,
            LabelOverride = label,
            FollowInputName = string.IsNullOrWhiteSpace(label),
            StripColor = color
        };

        _updatingGrid = true;
        try
        {
            _grid.Rows[rowIndex].Cells["Kind"].Value = assignment.Kind;
            _grid.Rows[rowIndex].Cells["InputKey"].Value = assignment.InputKey ?? "";
            _grid.Rows[rowIndex].Cells["Label"].Value = assignment.LabelOverride;
            _grid.Rows[rowIndex].Cells["StripColor"].Value = assignment.StripColor;
        }
        finally
        {
            _updatingGrid = false;
        }

        SaveProfile();
        UpdateInputColumnDataSource();
        UpdateLiveGridAndHardware();
        _logger.Info("Assignment API updated channel {0}: kind={1}, inputKey={2}, label='{3}', color={4}",
            channel,
            assignment.Kind,
            assignment.InputKey ?? "",
            assignment.LabelOverride,
            assignment.StripColor);
        return new ApiAssignmentResponse(true, "Assignment updated.", ToApiChannel(assignment));
    }

    private string? ResolveApiInputKey(ApiAssignmentRequest request, string? currentInputKey)
    {
        if (!string.IsNullOrWhiteSpace(request.InputKey))
        {
            var input = _state.Inputs.FirstOrDefault(candidate => candidate.Key.Equals(request.InputKey, StringComparison.OrdinalIgnoreCase));
            return input?.Key ?? request.InputKey;
        }

        if (request.InputNumber.HasValue)
            return _state.Inputs.FirstOrDefault(input => input.Number == request.InputNumber.Value)?.Key;

        if (!string.IsNullOrWhiteSpace(request.InputTitle))
            return _state.Inputs.FirstOrDefault(input => input.Title.Equals(request.InputTitle, StringComparison.OrdinalIgnoreCase))?.Key;

        return currentInputKey;
    }

    private static ApiChannelAssignment ToApiChannel(ChannelAssignment assignment) => new(
        assignment.Channel,
        assignment.Kind.ToString(),
        assignment.InputKey,
        assignment.LabelOverride,
        assignment.StripColor.ToString());

    private void ConfigureStartupRegistration()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true)
                ?? Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);

            if (_profile.StartWithWindows)
            {
                key.SetValue("IconP1MVmixBridge", $"\"{Application.ExecutablePath}\"");
                _logger.Info("Configured app to start with Windows");
            }
            else
            {
                key.DeleteValue("IconP1MVmixBridge", throwOnMissingValue: false);
                _logger.Info("Removed app from Windows startup");
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Could not update Windows startup setting");
        }
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
            color = NormalizeStripColor(color);
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
                return new LiveChannel(label, XmlVolumeToFaderPercent(input.Volume), VmixMeterToDisplayPercent(Math.Max(input.MeterF1, input.MeterF2)));
            }
        }

        var outputName = assignment.Kind switch
        {
            AssignmentKind.Master => "master",
            AssignmentKind.BusA => "busA",
            AssignmentKind.BusB => "busB",
            AssignmentKind.BusC => "busC",
            AssignmentKind.BusD => "busD",
            AssignmentKind.BusE => "busE",
            AssignmentKind.BusF => "busF",
            AssignmentKind.BusG => "busG",
            _ => ""
        };

        if (!string.IsNullOrWhiteSpace(outputName) && _state.OutputVolumes.TryGetValue(outputName, out var volume))
        {
            var label = string.IsNullOrWhiteSpace(assignment.LabelOverride) ? assignment.Kind.ToString() : assignment.LabelOverride;
            double? meter = _state.OutputMeters.TryGetValue(outputName, out var meterValue)
                ? VmixMeterToDisplayPercent(meterValue)
                : null;
            return new LiveChannel(label, XmlVolumeToFaderPercent(volume), meter);
        }

        return new LiveChannel(string.IsNullOrWhiteSpace(assignment.LabelOverride) ? assignment.Kind.ToString() : assignment.LabelOverride, null, null);
    }

    private async void OnMidiMessageReceived(object? sender, MidiMessageEventArgs e)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new MethodInvoker(() => OnMidiMessageReceived(sender, e)));
            return;
        }

        if (!_connected || _pollCts?.IsCancellationRequested != false)
        {
            _lastMidiMessage = $"ignored {e.Status:X2} {e.Data1:X2} {e.Data2:X2}";
            _logger.Debug("MIDI ignored while bridge stopped: status {0:X2}, data1 {1:X2}, data2 {2:X2}, channel {3}", e.Status, e.Data1, e.Data2, e.Channel + 1);
            UpdateStatus();
            return;
        }

        try
        {
            _lastMidiMessage = $"{e.Status:X2} {e.Data1:X2} {e.Data2:X2} ch {e.Channel + 1}";
            _logger.Debug("MIDI received: status {0:X2}, data1 {1:X2}, data2 {2:X2}, channel {3}, command {4:X2}", e.Status, e.Data1, e.Data2, e.Channel + 1, e.Command);
            if (e.Command == 0x90 && e.Data1 is >= 0x68 and <= 0x6F)
            {
                var channel = e.Data1 - 0x68;
                var now = DateTime.Now;
                var touched = e.Data2 > 0;
                if (now < _suppressIncomingFaderUntil[channel])
                {
                    _faderTouched[channel] = false;
                    _logger.Debug("Ignored motor fader touch echo ch {0}: {1}; suppress remaining {2:0} ms",
                        channel + 1,
                        touched ? "on" : "off",
                        (_suppressIncomingFaderUntil[channel] - now).TotalMilliseconds);
                    return;
                }

                if (now < _ignoreLocalFaderUntil[channel])
                {
                    _faderTouched[channel] = false;
                    _logger.Debug("Ignored post-release fader touch ch {0}: {1}; ignore remaining {2:0} ms",
                        channel + 1,
                        touched ? "on" : "off",
                        (_ignoreLocalFaderUntil[channel] - now).TotalMilliseconds);
                    return;
                }

                _faderTouched[channel] = touched;
                _lastFaderTouch[channel] = touched ? now : DateTime.MinValue;
                if (!touched)
                {
                    _ignoreLocalFaderUntil[channel] = now.AddMilliseconds(ReleaseEchoGuardMs);

                    if (_lastMotorFeedbackValue[channel] >= 0)
                    {
                        _suppressIncomingFaderUntil[channel] = now.AddMilliseconds(ReleaseEchoGuardMs);
                        _logger.Debug("MIDI fader release hold ch {0}: raw {1}, {2:0.##}%; release guard {3} ms until {4:HH:mm:ss.fff}; output open: {5}",
                            channel + 1,
                            _lastMotorFeedbackValue[channel],
                            _lastMotorFeedbackValue[channel] / 16383.0 * 100.0,
                            ReleaseEchoGuardMs,
                            _suppressIncomingFaderUntil[channel],
                            _midi.OutputOpen);
                        _midi.SendPitchBend(channel, _lastMotorFeedbackValue[channel]);
                    }
                    else
                    {
                        _logger.Debug("MIDI fader release hold ch {0}: skipped because no current fader value is known", channel + 1);
                    }
                }

                _logger.Debug("MIDI fader touch ch {0}: {1}", channel + 1, _faderTouched[channel] ? "on" : "off");
                UpdateStatus();
                return;
            }

            if (TryHandleNavigationMidi(e))
                return;

            if (e.Command == 0xE0 && e.Channel is >= 0 and < 8)
            {
                var logical = LogicalIndexForPhysical(e.Channel);
                var value14 = e.Data1 | (e.Data2 << 7);
                var percent = value14 / 16383.0 * 100.0;
                var now = DateTime.Now;
                if (logical < 0 || logical >= _grid.Rows.Count)
                {
                    _logger.Debug("Ignored fader on physical ch {0}: no logical assignment in active bank {1}-{2}",
                        e.Channel + 1,
                        _bankStartChannel + 1,
                        Math.Min(_bankStartChannel + 8, _grid.Rows.Count));
                    return;
                }

                if (now < _ignoreLocalFaderUntil[e.Channel])
                {
                    _logger.Debug("Ignored post-release fader movement ch {0}: {1:0.##}% raw {2}; ignore remaining {3:0} ms",
                        e.Channel + 1,
                        percent,
                        value14,
                        (_ignoreLocalFaderUntil[e.Channel] - now).TotalMilliseconds);
                    return;
                }

                if (now < _suppressIncomingFaderUntil[e.Channel])
                {
                    _logger.Debug("Suppressed motor-feedback echo ch {0}: {1:0.##}% raw {2}; suppress remaining {3:0} ms",
                        e.Channel + 1,
                        percent,
                        value14,
                        (_suppressIncomingFaderUntil[e.Channel] - now).TotalMilliseconds);
                    return;
                }

                if (_profile.InputFadersAreTouchSensitive && !_faderTouched[e.Channel])
                {
                    var rawDelta = _lastMotorFeedbackValue[e.Channel] < 0
                        ? int.MaxValue
                        : Math.Abs(value14 - _lastMotorFeedbackValue[e.Channel]);
                    if (rawDelta < UntouchedAutoGrabRawDelta)
                    {
                        _logger.Debug("Ignored untouched fader ch {0}: {1:0.##}% raw {2}; raw delta {3}",
                            e.Channel + 1,
                            percent,
                            value14,
                            rawDelta);
                        return;
                    }

                    _faderTouched[e.Channel] = true;
                    _logger.Debug("Auto-grabbed fader ch {0}: {1:0.##}% raw {2}; raw delta {3}; touch message was late/missing",
                        e.Channel + 1,
                        percent,
                        value14,
                        rawDelta);
                }

                _lastFaderTouch[e.Channel] = now;
                _lastMotorFeedbackValue[e.Channel] = value14;
                if (!double.IsNaN(_lastSentFaderPercent[e.Channel]) &&
                    Math.Abs(percent - _lastSentFaderPercent[e.Channel]) < 0.2)
                {
                    _logger.Debug("Ignored duplicate fader ch {0}: {1:0.##}% raw {2}; last sent {3:0.##}%",
                        e.Channel + 1,
                        percent,
                        value14,
                        _lastSentFaderPercent[e.Channel]);
                    return;
                }

                _pendingFaderPercent[e.Channel] = percent;
                _pendingFaderDirty[e.Channel] = true;
                _logger.Debug("MIDI fader physical ch {0}, logical ch {1}: {2:0.##}% raw {3}; touched={4}",
                    e.Channel + 1,
                    logical + 1,
                    percent,
                    value14,
                    _faderTouched[e.Channel]);
                SetGridCellValue(logical, "LiveVolume", percent.ToString("0", CultureInfo.InvariantCulture));
                UpdateStatus();
            }
            else if (e.Command == 0x90 && e.Data2 > 0 && e.Data1 is >= 0 and <= 7)
            {
                var channel = e.Data1;
                _logger.Debug("MIDI record button ch {0}: set to 0 dB", channel + 1);
                await SetChannelVolumeFromSurfaceAsync(channel, 100, "record button 0 dB");
                UpdateStatus();
            }
            else if (e.Command == 0x90 && e.Data2 > 0 && e.Data1 is >= 16 and <= 23)
            {
                var channel = e.Data1 - 16;
                _logger.Debug("MIDI mute button ch {0}: set to -inf", channel + 1);
                await SetChannelVolumeFromSurfaceAsync(channel, 0, "mute button -inf");
                UpdateStatus();
            }
            else
            {
                UpdateStatus("MIDI received, but not mapped to a fader/mute action");
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "MIDI action failed. Raw message {0:X6}", e.RawMessage);
            UpdateStatus($"MIDI action failed: {ex.Message}");
        }
    }

    private bool TryHandleNavigationMidi(MidiMessageEventArgs e)
    {
        if (e.Command == 0x90 && e.Data2 > 0)
        {
            switch (e.Data1)
            {
                case 0x2E:
                    ChangeBank(-8, "MIDI bank left");
                    return true;
                case 0x2F:
                    ChangeBank(8, "MIDI bank right");
                    return true;
                case 0x30:
                    ChangeBank(-1, "MIDI channel left");
                    return true;
                case 0x31:
                    ChangeBank(1, "MIDI channel right");
                    return true;
                case 0x5B:
                    ChangeBank(-8, "MIDI rewind/previous bank");
                    return true;
                case 0x5C:
                    ChangeBank(8, "MIDI fast-forward/next bank");
                    return true;
            }
        }

        if (e.Command == 0xB0 && e.Data1 == 0x3C)
        {
            var delta = e.Data2 <= 0x3F ? 8 : -8;
            ChangeBank(delta, $"MIDI jog wheel value {e.Data2}");
            return true;
        }

        return false;
    }

    private static int PercentToFourteenBit(double percent) => (int)Math.Round(Math.Clamp(percent, 0, 100) / 100.0 * 16383);

    private void ResetHardwareFeedbackCache()
    {
        Array.Fill(_lastStripLabels, "");
        Array.Fill(_lastMotorFeedbackValue, -1);
        Array.Fill(_faderTouched, false);
        Array.Fill(_lastFaderTouch, DateTime.MinValue);
        Array.Fill(_ignoreLocalFaderUntil, DateTime.MinValue);
        Array.Fill(_suppressIncomingFaderUntil, DateTime.MinValue);
        Array.Fill(_lastSentFaderPercent, double.NaN);
        Array.Fill(_pendingFaderPercent, double.NaN);
        Array.Fill(_pendingFaderDirty, false);
    }

    private static StripColor NormalizeStripColor(StripColor color) => Enum.IsDefined(color) ? color : StripColor.Blue;

    private static double VmixMeterToDisplayPercent(double rawMeter)
    {
        var normalized = rawMeter > 1.0 ? rawMeter / 100.0 : rawMeter;
        normalized = Math.Clamp(normalized, 0.0, 1.0);
        if (normalized <= 0.00001)
            return 0;

        var db = 20.0 * Math.Log10(normalized);
        return Math.Clamp((db + 60.0) / 54.0 * 100.0, 0.0, 100.0);
    }

    private static int MeterPercentToMackieLevel(double meterPercent)
    {
        if (meterPercent <= 0)
            return 0;
        return (int)Math.Clamp(Math.Round(meterPercent / 100.0 * 12.0), 1, 12);
    }

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
        // vMix XML volume is amplitude scaled 0-100; vMix UI/API fader position is 0-100.
        var amplitude = Math.Clamp(xmlVolume, 0, 100) / 100.0;
        return Math.Pow(amplitude, 0.25) * 100.0;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_minimizeToTray.Checked && WindowState == FormWindowState.Minimized)
            Hide();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowExit && _minimizeToTray.Checked && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        SaveProfile();
        Disconnect();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _apiServer.Dispose();
        _vmix.Dispose();
        _midi.Dispose();
        base.OnFormClosing(e);
    }

    private sealed record InputChoice(string Key, string Title);
    private sealed record LiveChannel(string Label, double? VolumePercent, double? MeterPercent);
}
