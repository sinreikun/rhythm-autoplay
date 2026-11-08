using RhythmAutoplay.Core;
using ArrowDirection = RhythmAutoplay.Core.ArrowDirection;

namespace RhythmAutoplay.App;

/// <summary>
/// プロファイル編集ダイアログ。対象プロセス・レーン数・キー割り当て・判定ライン・
/// レーン形状を GUI で編集し、利用者プロファイルフォルダへ JSON 保存する。
/// 新規作成と複製にも対応する。組み込みプロファイルも、同じ名前で保存すれば
/// 利用者ファイルとして上書きでき（元の組み込みは残る）、利用者ファイルを
/// 削除すれば組み込みの初期値に戻る。
/// </summary>
public sealed class ProfileEditorForm : Form
{
    private readonly List<GameProfile> _profiles;
    private string? _currentId;
    private bool _idEditable;
    private bool _loading;

    private readonly Panel _panel = new();
    private readonly ComboBox _selector = new();
    private readonly Button _newButton = new();
    private readonly Button _copyButton = new();
    private readonly Button _deleteButton = new();
    private readonly TextBox _idBox = new();
    private readonly TextBox _processBox = new();
    private readonly NumericUpDown _laneCountNum = new();
    private readonly GroupBox _basicGroup = new();
    private readonly GroupBox _keysGroup = new();
    private readonly Panel _lanePanel = new();
    private readonly List<TextBox> _redBoxes = new();
    private readonly List<TextBox> _blueBoxes = new();
    private readonly Label _arrowCaption = new();
    private readonly TextBox _arrowLeft = new();
    private readonly TextBox _arrowRight = new();
    private readonly TextBox _arrowUp = new();
    private readonly TextBox _arrowDown = new();
    private readonly GroupBox _timingGroup = new();
    private readonly NumericUpDown _judgmentNum = new();
    private readonly NumericUpDown _tapNum = new();
    private readonly NumericUpDown _geoCenterX = new();
    private readonly NumericUpDown _geoTopY = new();
    private readonly NumericUpDown _geoTopHalf = new();
    private readonly NumericUpDown _geoBottomY = new();
    private readonly NumericUpDown _geoBottomHalf = new();
    private readonly NumericUpDown _geoVanishY = new();
    private readonly Label _errorLabel = new();
    private readonly Button _saveButton = new();
    private readonly Button _cancelButton = new();

    /// <summary>保存または削除が行われたか。呼び出し側は true なら一覧を再読込する。</summary>
    public bool Changed { get; private set; }

    /// <summary>最後に保存したプロファイル名。</summary>
    public string? SavedId { get; private set; }

    /// <summary>最後に削除したプロファイル名。</summary>
    public string? DeletedId { get; private set; }

    public ProfileEditorForm(List<GameProfile> profiles, string currentId)
    {
        _profiles = new List<GameProfile>(profiles);
        BuildUi();
        _selector.SelectedItem = _profiles.FirstOrDefault(p => p.GameId == currentId) ?? _profiles.FirstOrDefault();
        if (_selector.SelectedItem is GameProfile selected)
        {
            LoadProfile(selected, idEditable: false);
        }
        else
        {
            LoadProfile(BlankProfile(), idEditable: true);
        }
    }

    private static GameProfile BlankProfile() => new(
        GameId: "",
        WindowProcessName: "",
        LaneCount: 4,
        JudgmentLineY: 0.94,
        Lanes: new[]
        {
            new LaneBinding(0, "", ""), new LaneBinding(1, "", ""),
            new LaneBinding(2, "", ""), new LaneBinding(3, "", ""),
        },
        ArrowKeys: new Dictionary<ArrowDirection, string>(),
        TapHoldMilliseconds: 40)
    {
        Geometry = LaneGeometry.Default,
    };

    private void BuildUi()
    {
        Text = "プロファイル編集";
        ClientSize = new Size(560, 720);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;

        _panel.Dock = DockStyle.Fill;
        _panel.AutoScroll = true;
        Controls.Add(_panel);

        _panel.Controls.Add(new Label { Text = "編集するプロファイル", Location = new Point(10, 10), AutoSize = true });
        _selector.Location = new Point(10, 32);
        _selector.Size = new Size(250, 28);
        _selector.DropDownStyle = ComboBoxStyle.DropDownList;
        _selector.DisplayMember = nameof(GameProfile.GameId);
        _selector.Items.AddRange(_profiles.Cast<object>().ToArray());
        _selector.SelectedIndexChanged += OnSelectorChanged;
        _panel.Controls.Add(_selector);
        _newButton.Text = "新規";
        _newButton.Location = new Point(268, 31);
        _newButton.Size = new Size(64, 30);
        _newButton.Click += (_, _) => LoadProfile(BlankProfile(), idEditable: true);
        _panel.Controls.Add(_newButton);
        _copyButton.Text = "複製";
        _copyButton.Location = new Point(338, 31);
        _copyButton.Size = new Size(64, 30);
        _copyButton.Click += OnCopyClicked;
        _panel.Controls.Add(_copyButton);
        _deleteButton.Text = "削除";
        _deleteButton.Location = new Point(408, 31);
        _deleteButton.Size = new Size(64, 30);
        _deleteButton.Click += OnDeleteClicked;
        _panel.Controls.Add(_deleteButton);
        _panel.Controls.Add(new Label
        {
            Text = "保存先: " + ProfileCatalog.UserProfilesDirectory,
            Location = new Point(10, 64),
            AutoSize = true,
            ForeColor = Color.Gray,
        });

        // ---- 基本設定 ----
        _basicGroup.Text = "基本設定";
        _basicGroup.Location = new Point(10, 88);
        _basicGroup.Size = new Size(524, 162);
        _basicGroup.Controls.Add(new Label { Text = "プロファイル名（ID）", Location = new Point(10, 22), AutoSize = true });
        _idBox.Location = new Point(10, 44);
        _idBox.Size = new Size(250, 26);
        _basicGroup.Controls.Add(_idBox);
        _basicGroup.Controls.Add(new Label { Text = "半角英数・-・_ のみ", Location = new Point(268, 47), AutoSize = true, ForeColor = Color.Gray });
        _basicGroup.Controls.Add(new Label { Text = "対象プロセス名", Location = new Point(10, 78), AutoSize = true });
        _processBox.Location = new Point(10, 100);
        _processBox.Size = new Size(250, 26);
        _basicGroup.Controls.Add(_processBox);
        _basicGroup.Controls.Add(new Label { Text = ".exe は不要です", Location = new Point(268, 103), AutoSize = true, ForeColor = Color.Gray });
        _basicGroup.Controls.Add(new Label { Text = "レーン数", Location = new Point(10, 134), AutoSize = true });
        ConfigureNum(_laneCountNum, 1, 8, 0, 1, 4);
        _laneCountNum.Location = new Point(90, 131);
        _laneCountNum.Size = new Size(70, 26);
        _laneCountNum.ValueChanged += (_, _) =>
        {
            if (!_loading)
            {
                RebuildLaneRows((int)_laneCountNum.Value, preserve: true);
            }
        };
        _basicGroup.Controls.Add(_laneCountNum);
        _panel.Controls.Add(_basicGroup);

        // ---- キー割り当て ----
        _keysGroup.Text = "キー割り当て";
        _keysGroup.Location = new Point(10, 258);
        _keysGroup.Size = new Size(524, 240);
        _keysGroup.Controls.Add(new Label { Text = "レーン", Location = new Point(10, 20), AutoSize = true });
        _keysGroup.Controls.Add(new Label { Text = "赤ノーツのキー", Location = new Point(90, 20), AutoSize = true });
        _keysGroup.Controls.Add(new Label { Text = "青ノーツのキー", Location = new Point(250, 20), AutoSize = true });
        _lanePanel.Location = new Point(0, 44);
        _lanePanel.Size = new Size(524, 120);
        _keysGroup.Controls.Add(_lanePanel);
        _arrowCaption.Text = "矢印キー（任意）";
        _arrowCaption.AutoSize = true;
        _keysGroup.Controls.Add(_arrowCaption);
        foreach ((TextBox box, string caption) in new[]
        {
            (_arrowLeft, "左"), (_arrowRight, "右"), (_arrowUp, "上"), (_arrowDown, "下"),
        })
        {
            box.CharacterCasing = CharacterCasing.Upper;
            box.MaxLength = 10;
            box.Size = new Size(62, 26);
            _keysGroup.Controls.Add(new Label { Text = caption, AutoSize = true, Tag = box });
        }
        _keysGroup.Controls.Add(_arrowLeft);
        _keysGroup.Controls.Add(_arrowRight);
        _keysGroup.Controls.Add(_arrowUp);
        _keysGroup.Controls.Add(_arrowDown);
        _panel.Controls.Add(_keysGroup);

        // ---- タイミングと形状 ----
        _timingGroup.Text = "タイミングとレーン形状（0〜1 の割合）";
        _timingGroup.Location = new Point(10, 506);
        _timingGroup.Size = new Size(524, 168);
        AddNumRow(_timingGroup, "判定ラインY", _judgmentNum, 0.300, 1.000, 0.001, 0.94, 10, 22);
        AddNumRow(_timingGroup, "タップ保持 [ms]", _tapNum, 5, 300, 5, 40, 250, 22, decimals: 0);
        AddNumRow(_timingGroup, "中心X", _geoCenterX, 0.000, 1.000, 0.005, 0.5, 10, 56);
        AddNumRow(_timingGroup, "消失点Y", _geoVanishY, -0.500, 0.900, 0.005, 0.10, 250, 56);
        AddNumRow(_timingGroup, "上端Y", _geoTopY, 0.000, 1.000, 0.005, 0.16, 10, 90);
        AddNumRow(_timingGroup, "上端の半幅", _geoTopHalf, 0.001, 0.500, 0.005, 0.06, 250, 90);
        AddNumRow(_timingGroup, "下端Y", _geoBottomY, 0.300, 1.000, 0.005, 0.94, 10, 124);
        AddNumRow(_timingGroup, "下端の半幅", _geoBottomHalf, 0.010, 0.500, 0.005, 0.21, 250, 124);
        _panel.Controls.Add(_timingGroup);

        _errorLabel.ForeColor = Color.Firebrick;
        _errorLabel.Location = new Point(10, 682);
        _errorLabel.Size = new Size(524, 28);
        _panel.Controls.Add(_errorLabel);
        _saveButton.Text = "保存";
        _saveButton.Location = new Point(334, 714);
        _saveButton.Size = new Size(100, 34);
        _saveButton.Click += OnSaveClicked;
        _panel.Controls.Add(_saveButton);
        _cancelButton.Text = "キャンセル";
        _cancelButton.Location = new Point(440, 714);
        _cancelButton.Size = new Size(94, 34);
        _cancelButton.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        _panel.Controls.Add(_cancelButton);
    }

    private static void ConfigureNum(NumericUpDown num, double min, double max, int decimals, double step, double value)
    {
        num.Minimum = (decimal)min;
        num.Maximum = (decimal)max;
        num.DecimalPlaces = decimals;
        num.Increment = (decimal)step;
        num.Value = (decimal)Math.Clamp(value, min, max);
    }

    private void AddNumRow(GroupBox group, string caption, NumericUpDown num, double min, double max,
        double step, double value, int x, int y, int decimals = 4)
    {
        group.Controls.Add(new Label { Text = caption, Location = new Point(x, y + 3), AutoSize = true });
        ConfigureNum(num, min, max, decimals, step, value);
        num.Location = new Point(x + 105, y);
        num.Size = new Size(90, 26);
        group.Controls.Add(num);
    }

    private void LayoutSections()
    {
        int laneCount = _redBoxes.Count;
        _lanePanel.Height = laneCount * 30;
        int arrowY = 44 + _lanePanel.Height + 10;
        _arrowCaption.Location = new Point(10, arrowY + 3);
        int x = 110;
        foreach (TextBox box in new[] { _arrowLeft, _arrowRight, _arrowUp, _arrowDown })
        {
            foreach (Control c in _keysGroup.Controls)
            {
                if (c is Label label && ReferenceEquals(label.Tag, box))
                {
                    label.Location = new Point(x, arrowY + 3);
                }
            }
            box.Location = new Point(x + 26, arrowY);
            x += 98;
        }
        _keysGroup.Height = arrowY + 42;
        _keysGroup.Top = _basicGroup.Bottom + 8;
        _timingGroup.Top = _keysGroup.Bottom + 8;
        _errorLabel.Top = _timingGroup.Bottom + 8;
        _saveButton.Top = _errorLabel.Bottom + 4;
        _cancelButton.Top = _saveButton.Top;
        _panel.AutoScrollMinSize = new Size(0, _saveButton.Bottom + 10);
    }

    private void LoadProfile(GameProfile profile, bool idEditable)
    {
        _loading = true;
        try
        {
            _currentId = idEditable ? null : profile.GameId;
            _idEditable = idEditable;
            _idBox.Text = profile.GameId;
            _idBox.ReadOnly = !idEditable;
            _processBox.Text = profile.WindowProcessName;
            _laneCountNum.Value = Math.Clamp(profile.LaneCount, 1, 8);
            RebuildLaneRows(profile.LaneCount, preserve: false);
            for (int i = 0; i < _redBoxes.Count; i++)
            {
                LaneBinding? lane = profile.Lanes.FirstOrDefault(l => l.Lane == i);
                _redBoxes[i].Text = lane?.RedKey ?? "";
                _blueBoxes[i].Text = lane?.BlueKey ?? "";
            }
            _arrowLeft.Text = GetArrow(profile, ArrowDirection.Left);
            _arrowRight.Text = GetArrow(profile, ArrowDirection.Right);
            _arrowUp.Text = GetArrow(profile, ArrowDirection.Up);
            _arrowDown.Text = GetArrow(profile, ArrowDirection.Down);
            _judgmentNum.Value = (decimal)Math.Clamp(profile.JudgmentLineY, 0.300, 1.000);
            _tapNum.Value = (decimal)Math.Clamp(profile.TapHoldMilliseconds, 5, 300);
            _geoCenterX.Value = (decimal)Math.Clamp(profile.Geometry.CenterX, 0.000, 1.000);
            _geoTopY.Value = (decimal)Math.Clamp(profile.Geometry.TopY, 0.000, 1.000);
            _geoTopHalf.Value = (decimal)Math.Clamp(profile.Geometry.TopHalfWidth, 0.001, 0.500);
            _geoBottomY.Value = (decimal)Math.Clamp(profile.Geometry.BottomY, 0.300, 1.000);
            _geoBottomHalf.Value = (decimal)Math.Clamp(profile.Geometry.BottomHalfWidth, 0.010, 0.500);
            _geoVanishY.Value = (decimal)Math.Clamp(profile.Geometry.VanishingY, -0.500, 0.900);
            _errorLabel.Text = "";
            UpdateDeleteButton();
        }
        finally
        {
            _loading = false;
        }
        LayoutSections();
    }

    private static string GetArrow(GameProfile profile, ArrowDirection dir) =>
        profile.ArrowKeys.TryGetValue(dir, out string? key) ? key : "";

    private void RebuildLaneRows(int count, bool preserve)
    {
        var reds = _redBoxes.Select(b => b.Text).ToList();
        var blues = _blueBoxes.Select(b => b.Text).ToList();
        _lanePanel.Controls.Clear();
        _redBoxes.Clear();
        _blueBoxes.Clear();
        for (int i = 0; i < count; i++)
        {
            int y = i * 30;
            _lanePanel.Controls.Add(new Label { Text = $"レーン {i + 1}", Location = new Point(10, y + 3), AutoSize = true });
            var red = new TextBox
            {
                Location = new Point(90, y),
                Size = new Size(140, 26),
                CharacterCasing = CharacterCasing.Upper,
                MaxLength = 10,
                Text = preserve && i < reds.Count ? reds[i] : "",
            };
            var blue = new TextBox
            {
                Location = new Point(250, y),
                Size = new Size(140, 26),
                CharacterCasing = CharacterCasing.Upper,
                MaxLength = 10,
                Text = preserve && i < blues.Count ? blues[i] : "",
            };
            _lanePanel.Controls.Add(red);
            _lanePanel.Controls.Add(blue);
            _redBoxes.Add(red);
            _blueBoxes.Add(blue);
        }
        LayoutSections();
    }

    private void UpdateDeleteButton()
    {
        _deleteButton.Enabled = _currentId is not null && ProfileCatalog.HasUserProfile(_currentId);
    }

    private void OnSelectorChanged(object? sender, EventArgs e)
    {
        if (_loading)
        {
            return;
        }
        if (_selector.SelectedItem is GameProfile selected)
        {
            LoadProfile(selected, idEditable: false);
        }
    }

    private void OnCopyClicked(object? sender, EventArgs e)
    {
        GameProfile source = ReadFieldsLenient();
        string baseId = string.IsNullOrWhiteSpace(_idBox.Text) ? "profile" : _idBox.Text.Trim();
        GameProfile copy = source with { GameId = baseId + "-copy" };
        _loading = true;
        _selector.SelectedIndex = -1;
        _loading = false;
        LoadProfile(copy, idEditable: true);
    }

    private void OnDeleteClicked(object? sender, EventArgs e)
    {
        if (_currentId is null)
        {
            return;
        }
        string id = _currentId;
        bool isBuiltin = id == GameProfile.HypnosyncDefault().GameId;
        string message = isBuiltin
            ? $"「{id}」の利用者ファイルを削除します。組み込みの初期値に戻ります。よろしいですか？"
            : $"プロファイル「{id}」を削除します。よろしいですか？";
        if (MessageBox.Show(this, message, "削除の確認", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }
        try
        {
            if (!ProfileCatalog.DeleteUserProfile(id))
            {
                _errorLabel.Text = "利用者ファイルが見つかりませんでした。";
                return;
            }
        }
        catch (Exception ex)
        {
            _errorLabel.Text = "削除に失敗しました: " + ex.Message;
            return;
        }
        Changed = true;
        DeletedId = id;
        _profiles.RemoveAll(p => p.GameId == id);
        // 組み込みは一覧に残る（初期値で読み直される）ため、削除対象が組み込みなら残す。
        _loading = true;
        _selector.Items.Clear();
        List<GameProfile> reloaded = ProfileCatalog.LoadAll();
        _profiles.Clear();
        _profiles.AddRange(reloaded);
        _selector.Items.AddRange(_profiles.Cast<object>().ToArray());
        _selector.SelectedItem = _profiles.FirstOrDefault();
        _loading = false;
        if (_selector.SelectedItem is GameProfile selected)
        {
            LoadProfile(selected, idEditable: false);
        }
        else
        {
            LoadProfile(BlankProfile(), idEditable: true);
        }
    }

    /// <summary>検証なしで現在の入力欄からプロファイルを作る（複製用）。</summary>
    private GameProfile ReadFieldsLenient()
    {
        var lanes = new List<LaneBinding>();
        for (int i = 0; i < _redBoxes.Count; i++)
        {
            lanes.Add(new LaneBinding(i, _redBoxes[i].Text.Trim(), _blueBoxes[i].Text.Trim()));
        }
        var arrows = new Dictionary<ArrowDirection, string>();
        void AddArrow(ArrowDirection dir, TextBox box)
        {
            if (!string.IsNullOrWhiteSpace(box.Text))
            {
                arrows[dir] = box.Text.Trim();
            }
        }
        AddArrow(ArrowDirection.Left, _arrowLeft);
        AddArrow(ArrowDirection.Right, _arrowRight);
        AddArrow(ArrowDirection.Up, _arrowUp);
        AddArrow(ArrowDirection.Down, _arrowDown);
        string process = _processBox.Text.Trim();
        if (process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            process = process[..^4];
        }
        return new GameProfile(
            _idBox.Text.Trim(), process, _redBoxes.Count, (double)_judgmentNum.Value,
            lanes, arrows, (double)_tapNum.Value)
        {
            Geometry = new LaneGeometry(
                (double)_geoCenterX.Value, (double)_geoTopY.Value, (double)_geoTopHalf.Value,
                (double)_geoBottomY.Value, (double)_geoBottomHalf.Value, (double)_geoVanishY.Value),
        };
    }

    private void OnSaveClicked(object? sender, EventArgs e)
    {
        string? error = ValidateFields(out GameProfile? profile);
        if (error is not null || profile is null)
        {
            _errorLabel.Text = error ?? "入力に問題があります。";
            return;
        }
        // 既存の別プロファイルを上書きする時だけ確認する。
        if (_idEditable && _profiles.Any(p => p.GameId == profile.GameId))
        {
            if (MessageBox.Show(this, $"既存のプロファイル「{profile.GameId}」を上書きしますか？",
                    "上書きの確認", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }
        }
        try
        {
            ProfileCatalog.SaveProfile(profile);
            // 保存したものを読み直して、実際に使える形になっているか確認する。
            GameProfile? reloaded = ProfileCatalog.LoadAll()
                .FirstOrDefault(p => p.GameId == profile.GameId);
            if (reloaded is null || reloaded.WindowProcessName != profile.WindowProcessName
                || reloaded.LaneCount != profile.LaneCount)
            {
                _errorLabel.Text = "保存はできましたが、読み直しの確認に失敗しました。";
                return;
            }
        }
        catch (Exception ex)
        {
            _errorLabel.Text = "保存に失敗しました: " + ex.Message;
            return;
        }
        Changed = true;
        SavedId = profile.GameId;
        DialogResult = DialogResult.OK;
        Close();
    }

    private string? ValidateFields(out GameProfile? profile)
    {
        profile = null;
        string id = _idBox.Text.Trim();
        if (_idEditable)
        {
            if (!ProfileCatalog.IsValidGameId(id))
            {
                return "プロファイル名は半角英数・ハイフン・アンダースコアのみで入力してください。";
            }
        }
        else if (_currentId is not null)
        {
            id = _currentId;
        }
        if (id.Length == 0)
        {
            return "プロファイル名を入力してください。";
        }

        string process = _processBox.Text.Trim();
        if (process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            process = process[..^4];
        }
        if (process.Length == 0)
        {
            return "対象プロセス名を入力してください。";
        }

        var usedKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? CheckKey(string key, string owner)
        {
            if (key.Length == 0)
            {
                return $"{owner}のキーが空です。";
            }
            try
            {
                InputSender.KeyNameToVirtualKey(key);
            }
            catch (ArgumentException)
            {
                return $"{owner}のキー「{key}」は使えません。英字・数字か SPACE / ENTER / ESC / 矢印名で入力してください。";
            }
            if (usedKeys.TryGetValue(key, out string? other))
            {
                return $"キー「{key}」が {other} と重複しています。";
            }
            usedKeys[key] = owner;
            return null;
        }

        var lanes = new List<LaneBinding>();
        for (int i = 0; i < _redBoxes.Count; i++)
        {
            string red = _redBoxes[i].Text.Trim();
            string blue = _blueBoxes[i].Text.Trim();
            if (CheckKey(red, $"レーン {i + 1}（赤）") is string e1)
            {
                return e1;
            }
            if (CheckKey(blue, $"レーン {i + 1}（青）") is string e2)
            {
                return e2;
            }
            lanes.Add(new LaneBinding(i, red, blue));
        }

        var arrows = new Dictionary<ArrowDirection, string>();
        foreach ((ArrowDirection dir, TextBox box, string name) in new[]
        {
            (ArrowDirection.Left, _arrowLeft, "矢印（左）"),
            (ArrowDirection.Right, _arrowRight, "矢印（右）"),
            (ArrowDirection.Up, _arrowUp, "矢印（上）"),
            (ArrowDirection.Down, _arrowDown, "矢印（下）"),
        })
        {
            string key = box.Text.Trim();
            if (key.Length == 0)
            {
                continue;
            }
            if (CheckKey(key, name) is string e3)
            {
                return e3;
            }
            arrows[dir] = key;
        }

        profile = new GameProfile(id, process, lanes.Count, (double)_judgmentNum.Value,
            lanes, arrows, (double)_tapNum.Value)
        {
            Geometry = new LaneGeometry(
                (double)_geoCenterX.Value, (double)_geoTopY.Value, (double)_geoTopHalf.Value,
                (double)_geoBottomY.Value, (double)_geoBottomHalf.Value, (double)_geoVanishY.Value),
        };
        return null;
    }
}
