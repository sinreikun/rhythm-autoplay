using System.Runtime.InteropServices;
using RhythmAutoplay.Core;

namespace RhythmAutoplay.App;

/// <summary>
/// RhythmAutoplay の GUI 本体。小さなウィンドウに必要な操作を集約する:
/// プロファイル切替、タイミング調整（スライダー＋数値）、プレイ/DRY-RUN/停止、
/// スナップショット、オーバレイ表示と範囲調整、状態とログの表示。
/// ウィンドウ位置と各種設定は GuiSettings に保存され、次回起動時に復元される。
/// </summary>
public sealed class MainForm : Form
{
    private readonly GuiSettings _settings;
    private List<GameProfile> _profiles = new();
    private GameProfile _baseProfile;
    private GameProfile _effectiveProfile;

    private readonly ComboBox _profileBox = new();
    private readonly Button _editProfileButton = new();
    private bool _suppressProfileEvent;
    private readonly TrackBar _offsetBar = new();
    private readonly NumericUpDown _offsetNum = new();
    private readonly Button _playButton = new();
    private readonly Button _dryButton = new();
    private readonly Button _stopButton = new();
    private readonly Button _snapshotButton = new();
    private readonly Button _resetGeometryButton = new();
    private readonly CheckBox _overlayCheck = new();
    private readonly CheckBox _editCheck = new();
    private readonly CheckBox _topmostCheck = new();
    private readonly Label _statusLabel = new();
    private readonly TextBox _logBox = new();
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly OverlayForm _overlay;

    private PlaySession? _session;
    private IntPtr _gameHwnd;
    private DateTime _lastSettingsSave = DateTime.MinValue;

    public MainForm()
    {
        _settings = GuiSettings.Load();
        _profiles = ProfileCatalog.LoadAll(msg => _pendingLogs.Add(msg));
        _baseProfile = _profiles.FirstOrDefault(p => p.GameId == _settings.ProfileId) ?? _profiles[0];
        _effectiveProfile = ApplyOverride(_baseProfile);
        _overlay = new OverlayForm(() => _effectiveProfile);
        _overlay.GeometryChanged += OnGeometryChanged;
        _overlay.ViewProvider = () => _session?.LatestView;

        BuildUi();
        RestoreWindowPosition();
        ApplySettingsToControls();
        UpdateRunButtons();

        _timer.Interval = 200;
        _timer.Tick += OnTimerTick;
        _timer.Start();

        foreach (string msg in _pendingLogs)
        {
            AppendLog(msg);
        }
        AppendLog("準備完了。ゲームを起動してプレイ開始を押してください。");
    }

    private readonly List<string> _pendingLogs = new();

    private GameProfile ApplyOverride(GameProfile profile) =>
        _settings.GeometryOverrides.TryGetValue(profile.GameId, out GeometryOverride? o)
            ? o.ApplyTo(profile)
            : profile;

    private void BuildUi()
    {
        Text = "RhythmAutoplay";
        ClientSize = new Size(356, 486);
        MinimumSize = new Size(340, 460);
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.Manual;

        int y = 10;
        Controls.Add(new Label { Text = "対象ゲーム（プロファイル）", Location = new Point(10, y), AutoSize = true });
        y += 22;
        _profileBox.Location = new Point(10, y);
        _profileBox.Size = new Size(216, 28);
        _profileBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _profileBox.DisplayMember = nameof(GameProfile.GameId);
        _profileBox.Items.AddRange(_profiles.Cast<object>().ToArray());
        _profileBox.SelectedItem = _baseProfile;
        _profileBox.SelectedIndexChanged += OnProfileChanged;
        Controls.Add(_profileBox);
        _editProfileButton.Text = "プロファイル編集…";
        _editProfileButton.Location = new Point(234, y);
        _editProfileButton.Size = new Size(112, 28);
        _editProfileButton.Click += OnEditProfilesClicked;
        Controls.Add(_editProfileButton);
        y += 40;

        Controls.Add(new Label { Text = "タイミング調整 [ms]（負で早く、正で遅く）", Location = new Point(10, y), AutoSize = true });
        y += 22;
        _offsetBar.Location = new Point(10, y);
        _offsetBar.Size = new Size(240, 30);
        _offsetBar.Minimum = -200;
        _offsetBar.Maximum = 200;
        _offsetBar.TickFrequency = 20;
        _offsetBar.Scroll += OnOffsetBarChanged;
        Controls.Add(_offsetBar);
        _offsetNum.Location = new Point(258, y);
        _offsetNum.Size = new Size(88, 28);
        _offsetNum.Minimum = -500;
        _offsetNum.Maximum = 500;
        _offsetNum.ValueChanged += OnOffsetNumChanged;
        Controls.Add(_offsetNum);
        y += 42;

        _playButton.Text = "▶ プレイ開始";
        _playButton.Location = new Point(10, y);
        _playButton.Size = new Size(108, 34);
        _playButton.Click += (_, _) => StartSession(dryRun: false);
        Controls.Add(_playButton);
        _dryButton.Text = "DRY-RUN";
        _dryButton.Location = new Point(124, y);
        _dryButton.Size = new Size(108, 34);
        _dryButton.Click += (_, _) => StartSession(dryRun: true);
        Controls.Add(_dryButton);
        _stopButton.Text = "■ 停止";
        _stopButton.Location = new Point(238, y);
        _stopButton.Size = new Size(108, 34);
        _stopButton.Click += (_, _) => _session?.RequestStop();
        Controls.Add(_stopButton);
        y += 44;

        _snapshotButton.Text = "スナップショット（8秒・較正用）";
        _snapshotButton.Location = new Point(10, y);
        _snapshotButton.Size = new Size(336, 30);
        _snapshotButton.Click += OnSnapshotClicked;
        Controls.Add(_snapshotButton);
        y += 40;

        _overlayCheck.Text = "オーバレイ表示（認識範囲）";
        _overlayCheck.Location = new Point(10, y);
        _overlayCheck.AutoSize = true;
        _overlayCheck.CheckedChanged += (_, _) =>
        {
            _settings.ShowOverlay = _overlayCheck.Checked;
            SaveSettingsSoon();
            if (!_overlayCheck.Checked && _overlay.Visible)
            {
                _overlay.Hide();
            }
        };
        Controls.Add(_overlayCheck);
        y += 26;
        _editCheck.Text = "範囲調整（ドラッグで変更）";
        _editCheck.Location = new Point(10, y);
        _editCheck.AutoSize = true;
        _editCheck.CheckedChanged += (_, _) =>
        {
            _overlay.EditMode = _editCheck.Checked;
            AppendLog(_editCheck.Checked ? "範囲調整モード: オーバレイのハンドルをドラッグしてください。" : "範囲調整モードを終了しました。");
        };
        Controls.Add(_editCheck);

        _resetGeometryButton.Text = "形状を初期値に戻す";
        _resetGeometryButton.Location = new Point(216, y - 3);
        _resetGeometryButton.Size = new Size(130, 24);
        _resetGeometryButton.Click += OnResetGeometryClicked;
        Controls.Add(_resetGeometryButton);
        y += 26;
        _topmostCheck.Text = "このウィンドウを常に手前に";
        _topmostCheck.Location = new Point(10, y);
        _topmostCheck.AutoSize = true;
        _topmostCheck.CheckedChanged += (_, _) =>
        {
            TopMost = _topmostCheck.Checked;
            _settings.AlwaysOnTop = _topmostCheck.Checked;
            SaveSettingsSoon();
        };
        Controls.Add(_topmostCheck);
        y += 30;

        _statusLabel.Location = new Point(10, y);
        _statusLabel.Size = new Size(336, 36);
        _statusLabel.Text = "待機中";
        Controls.Add(_statusLabel);
        y += 40;

        _logBox.Location = new Point(10, y);
        _logBox.Size = new Size(336, 486 - y - 10);
        _logBox.Multiline = true;
        _logBox.ReadOnly = true;
        _logBox.ScrollBars = ScrollBars.Vertical;
        _logBox.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(_logBox);
    }

    private void RestoreWindowPosition()
    {
        if (_settings.WindowX is int x && _settings.WindowY is int y)
        {
            // 画面外に復元しないよう、作業領域と重なる場合だけ採用する。
            var area = new Rectangle(x, y, Width, Height);
            if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(area)))
            {
                Location = new Point(x, y);
                return;
            }
        }
        StartPosition = FormStartPosition.CenterScreen;
    }

    private void ApplySettingsToControls()
    {
        double offset = _settings.OffsetMilliseconds;
        _offsetNum.Value = (decimal)Math.Max(-500, Math.Min(500, offset));
        _offsetBar.Value = (int)Math.Max(-200, Math.Min(200, offset));
        _overlayCheck.Checked = _settings.ShowOverlay;
        _topmostCheck.Checked = _settings.AlwaysOnTop;
        TopMost = _settings.AlwaysOnTop;
    }

    private void OnProfileChanged(object? sender, EventArgs e)
    {
        if (_suppressProfileEvent)
        {
            return;
        }
        if (_profileBox.SelectedItem is not GameProfile selected)
        {
            return;
        }
        _baseProfile = selected;
        _effectiveProfile = ApplyOverride(selected);
        _settings.ProfileId = selected.GameId;
        _gameHwnd = IntPtr.Zero;
        SaveSettingsSoon();
        _overlay.Invalidate();
        AppendLog($"プロファイル: {selected.GameId}（プロセス {selected.WindowProcessName}.exe）");
    }

    private void OnEditProfilesClicked(object? sender, EventArgs e)
    {
        // 編集画面には、GUI の形状上書きを反映した「実際に効いている」プロファイルを渡す。
        var effective = _profiles.Select(ApplyOverride).ToList();
        using var dialog = new ProfileEditorForm(effective, _baseProfile.GameId);
        dialog.ShowDialog(this);
        if (!dialog.Changed)
        {
            return;
        }
        // 保存されたプロファイルはファイル側に形状を持つので、GUI の形状上書きは破棄する。
        if (dialog.SavedId is string savedId)
        {
            _settings.GeometryOverrides.Remove(savedId);
        }
        string preferId = dialog.SavedId ?? _baseProfile.GameId;
        _profiles = ProfileCatalog.LoadAll(msg => AppendLog(msg));
        _baseProfile = _profiles.FirstOrDefault(p => p.GameId == preferId) ?? _profiles[0];
        _effectiveProfile = ApplyOverride(_baseProfile);
        _settings.ProfileId = _baseProfile.GameId;
        _gameHwnd = IntPtr.Zero;
        SaveSettingsSoon();
        _suppressProfileEvent = true;
        _profileBox.Items.Clear();
        _profileBox.Items.AddRange(_profiles.Cast<object>().ToArray());
        _profileBox.SelectedItem = _baseProfile;
        _suppressProfileEvent = false;
        _overlay.Invalidate();
        AppendLog(dialog.SavedId is not null
            ? $"プロファイル「{dialog.SavedId}」を保存しました。"
            : "プロファイル一覧を更新しました。");
    }

    private void OnOffsetBarChanged(object? sender, EventArgs e)
    {
        _offsetNum.Value = _offsetBar.Value;
        SaveOffset(_offsetBar.Value);
    }

    private void OnOffsetNumChanged(object? sender, EventArgs e)
    {
        int v = (int)_offsetNum.Value;
        if (v >= _offsetBar.Minimum && v <= _offsetBar.Maximum && _offsetBar.Value != v)
        {
            _offsetBar.Value = v;
        }
        SaveOffset(v);
    }

    private void SaveOffset(double ms)
    {
        if (Math.Abs(_settings.OffsetMilliseconds - ms) < 0.001)
        {
            return;
        }
        _settings.OffsetMilliseconds = ms;
        SaveSettingsSoon();
        if (_session is not null)
        {
            AppendLog($"タイミング {ms:+0;-0} ms（次回開始から反映）");
        }
    }

    private void StartSession(bool dryRun)
    {
        if (_session is not null)
        {
            return;
        }
        var options = new PlayOptions
        {
            DryRun = dryRun,
            OffsetMilliseconds = _settings.OffsetMilliseconds,
            DurationSeconds = 0,
            EventLogDirectory = GuiSettings.EventLogDirectory,
            // オーバレイ表示中はウィンドウ単体キャプチャにする（v0.3.63）。
            // 画面方式だとオーバレイ自身が映り込んで検出が死ぬため。
            // 切り替えはセッション開始時点のチェック状態で決まる。
            PrintWindowCapture = _overlayCheck.Checked,
        };
        var session = new PlaySession(_effectiveProfile, options, AppendLog);
        _session = session;
        UpdateRunButtons();
        Task.Run(() =>
        {
            try
            {
                session.Run();
            }
            catch (Exception ex)
            {
                AppendLog("エラー: " + ex.Message);
            }
            finally
            {
                BeginInvoke(() =>
                {
                    _session = null;
                    UpdateRunButtons();
                });
            }
        });
    }

    private void OnSnapshotClicked(object? sender, EventArgs e)
    {
        _snapshotButton.Enabled = false;
        var session = new SnapshotSession(
            _effectiveProfile,
            new SnapshotOptions { Seconds = 8, IntervalMilliseconds = 500 },
            AppendLog);
        Task.Run(() =>
        {
            try
            {
                session.Run();
            }
            catch (Exception ex)
            {
                AppendLog("スナップショットのエラー: " + ex.Message);
            }
            finally
            {
                BeginInvoke(() => _snapshotButton.Enabled = true);
            }
        });
    }

    private void UpdateRunButtons()
    {
        bool running = _session is not null;
        _playButton.Enabled = !running;
        _dryButton.Enabled = !running;
        _stopButton.Enabled = running;
        _editProfileButton.Enabled = !running;
    }

    private void OnResetGeometryClicked(object? sender, EventArgs e)
    {
        _settings.GeometryOverrides.Remove(_baseProfile.GameId);
        _effectiveProfile = _baseProfile;
        _overlay.Invalidate();
        SaveSettingsSoon();
        AppendLog("形状（認識範囲・判定ライン）をプロファイル初期値に戻しました。");
    }

    private void OnGeometryChanged(GameProfile updated)
    {
        _effectiveProfile = updated;
        _settings.GeometryOverrides[_baseProfile.GameId] = GeometryOverride.FromProfile(updated);
        SaveSettingsSoon();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        // 状態表示
        if (_session is not null)
        {
            PlayStats s = _session.Stats;
            _statusLabel.Text = $"実行中: フレーム={s.Frames} 予定={s.Scheduled} 発火={s.Fired}";
        }
        else
        {
            _statusLabel.Text = _gameHwnd != IntPtr.Zero ? "待機中（ゲーム検出済み）" : "待機中（ゲーム未検出）";
        }

        FollowGameWindow();
    }

    private void FollowGameWindow()
    {
        if (!_overlayCheck.Checked)
        {
            return;
        }
        if (_gameHwnd == IntPtr.Zero || !IsWindow(_gameHwnd))
        {
            _gameHwnd = WindowCapturer.FindWindowByProcessName(_effectiveProfile.WindowProcessName);
        }
        if (_gameHwnd == IntPtr.Zero)
        {
            if (_overlay.Visible)
            {
                _overlay.Hide();
            }
            return;
        }
        if (!GetClientRect(_gameHwnd, out RECT rect))
        {
            return;
        }
        var origin = new POINT { X = 0, Y = 0 };
        ClientToScreen(_gameHwnd, ref origin);
        int w = rect.Right - rect.Left;
        int h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0)
        {
            return;
        }
        var bounds = new Rectangle(origin.X, origin.Y, w, h);
        if (_overlay.Bounds != bounds)
        {
            _overlay.Bounds = bounds;
        }
        if (!_overlay.Visible)
        {
            _overlay.Show();
        }
        _overlay.Invalidate();
    }

    public void AppendLog(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLog(message));
            return;
        }
        _logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        if (_logBox.TextLength > 12000)
        {
            _logBox.Text = _logBox.Text[^8000..];
            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.ScrollToCaret();
        }
    }

    private void SaveSettingsSoon()
    {
        if (DateTime.UtcNow - _lastSettingsSave < TimeSpan.FromMilliseconds(400))
        {
            return;
        }
        _lastSettingsSave = DateTime.UtcNow;
        _settings.Save();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _session?.RequestStop();
        _settings.WindowX = Location.X;
        _settings.WindowY = Location.Y;
        _settings.Save();
        base.OnFormClosing(e);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT pt);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
}
