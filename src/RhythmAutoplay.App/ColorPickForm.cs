using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using RhythmAutoplay.Core;

namespace RhythmAutoplay.App;

/// <summary>
/// スポイトダイアログ。ゲーム画面（対象プロセスのウィンドウ）か画像ファイル
/// から色を 1 色拾って返す。画像をクリックした画素の色をプレビューし、
/// 「この色を追加」で確定する。16 進数での直接入力もできる。
/// </summary>
public sealed class ColorPickForm : Form
{
    private readonly string _processName;
    private Bitmap? _bitmap;
    private readonly PictureBox _picture = new();
    private readonly Panel _preview = new();
    private readonly Label _infoLabel = new();
    private readonly TextBox _hexBox = new();
    private readonly Button _okButton = new();

    /// <summary>確定した色（色を選んで OK の時だけ意味を持つ）。</summary>
    public Color SelectedColor { get; private set; } = Color.Empty;

    public ColorPickForm(string processName)
    {
        _processName = processName;
        Text = "スポイト（色を拾う）";
        ClientSize = new Size(560, 520);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;

        var captureButton = new Button { Text = "ゲーム画面から取得", Location = new Point(10, 10), Size = new Size(150, 30) };
        captureButton.Click += OnCaptureClicked;
        Controls.Add(captureButton);
        var fileButton = new Button { Text = "画像ファイルを開く", Location = new Point(168, 10), Size = new Size(150, 30) };
        fileButton.Click += OnFileClicked;
        Controls.Add(fileButton);
        Controls.Add(new Label
        {
            Text = "画像をクリックすると、その画素の色を拾います。",
            Location = new Point(10, 47),
            AutoSize = true,
            ForeColor = Color.Gray,
        });

        _picture.Location = new Point(10, 68);
        _picture.Size = new Size(540, 330);
        _picture.SizeMode = PictureBoxSizeMode.Zoom;
        _picture.BorderStyle = BorderStyle.FixedSingle;
        _picture.BackColor = Color.Black;
        _picture.MouseClick += OnPictureClicked;
        Controls.Add(_picture);

        _preview.Location = new Point(10, 410);
        _preview.Size = new Size(40, 40);
        _preview.BorderStyle = BorderStyle.FixedSingle;
        Controls.Add(_preview);
        _infoLabel.Location = new Point(60, 410);
        _infoLabel.Size = new Size(240, 40);
        _infoLabel.Text = "まだ色を選んでいません。";
        Controls.Add(_infoLabel);
        Controls.Add(new Label { Text = "16進数", Location = new Point(310, 412), AutoSize = true });
        _hexBox.Location = new Point(310, 430);
        _hexBox.Size = new Size(90, 26);
        _hexBox.CharacterCasing = CharacterCasing.Upper;
        Controls.Add(_hexBox);
        var applyHex = new Button { Text = "反映", Location = new Point(406, 428), Size = new Size(60, 30) };
        applyHex.Click += OnHexApply;
        Controls.Add(applyHex);

        _okButton.Text = "この色を追加";
        _okButton.Location = new Point(310, 470);
        _okButton.Size = new Size(110, 34);
        _okButton.Enabled = false;
        _okButton.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        Controls.Add(_okButton);
        var cancel = new Button { Text = "キャンセル", Location = new Point(430, 470), Size = new Size(110, 34) };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(cancel);
    }

    private void SetPicked(Color color)
    {
        SelectedColor = color;
        _preview.BackColor = color;
        _infoLabel.Text = $"RGB ({color.R}, {color.G}, {color.B})\n#{color.R:X2}{color.G:X2}{color.B:X2}";
        _hexBox.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        _okButton.Enabled = true;
    }

    private void OnHexApply(object? sender, EventArgs e)
    {
        try
        {
            ProfileColor parsed = ProfileColor.Parse(_hexBox.Text);
            SetPicked(Color.FromArgb(parsed.R, parsed.G, parsed.B));
        }
        catch (FormatException)
        {
            MessageBox.Show(this, "色は #RRGGBB 形式で入力してください。",
                "スポイト", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OnCaptureClicked(object? sender, EventArgs e)
    {
        string process = _processName.Trim();
        if (process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            process = process[..^4];
        }
        if (process.Length == 0)
        {
            MessageBox.Show(this, "先に対象プロセス名を入力してください。",
                "スポイト", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        IntPtr hwnd = WindowCapturer.FindWindowByProcessName(process);
        if (hwnd == IntPtr.Zero)
        {
            MessageBox.Show(this, $"「{process}」のウィンドウが見つかりませんでした。ゲームを起動してからお試しください。",
                "スポイト", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try
        {
            using var capturer = new WindowCapturer(hwnd) { UsePrintWindow = true };
            CapturedFrame frame = capturer.Capture();
            SetBitmap(FrameToBitmap(frame));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "画面の取得に失敗しました: " + ex.Message,
                "スポイト", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OnFileClicked(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "画像ファイル|*.png;*.jpg;*.jpeg;*.bmp;*.gif|すべてのファイル|*.*",
            Title = "色を拾う画像を選んでください",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        try
        {
            using var stream = File.OpenRead(dialog.FileName);
            using var loaded = new Bitmap(stream);
            SetBitmap(new Bitmap(loaded));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "画像を開けませんでした: " + ex.Message,
                "スポイト", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void SetBitmap(Bitmap bitmap)
    {
        _bitmap?.Dispose();
        _bitmap = bitmap;
        _picture.Image = bitmap;
    }

    private void OnPictureClicked(object? sender, MouseEventArgs e)
    {
        if (_bitmap is null)
        {
            return;
        }
        // Zoom 表示の実描画領域を求め、クリック位置を画像座標へ戻す。
        double scale = Math.Min(
            (double)_picture.Width / _bitmap.Width,
            (double)_picture.Height / _bitmap.Height);
        double dw = _bitmap.Width * scale, dh = _bitmap.Height * scale;
        double ox = (_picture.Width - dw) / 2, oy = (_picture.Height - dh) / 2;
        int x = (int)((e.X - ox) / scale), y = (int)((e.Y - oy) / scale);
        if (x < 0 || y < 0 || x >= _bitmap.Width || y >= _bitmap.Height)
        {
            return;
        }
        SetPicked(_bitmap.GetPixel(x, y));
    }

    private static Bitmap FrameToBitmap(CapturedFrame frame)
    {
        var bitmap = new Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppArgb);
        if (frame.Bgra.Length == frame.Width * frame.Height * 4)
        {
            BitmapData data = bitmap.LockBits(
                new Rectangle(0, 0, frame.Width, frame.Height),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                Marshal.Copy(frame.Bgra, 0, data.Scan0, frame.Bgra.Length);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }
        else
        {
            for (int y = 0; y < frame.Height; y++)
            {
                for (int x = 0; x < frame.Width; x++)
                {
                    var (r, g, b) = frame.PixelAt(x, y);
                    bitmap.SetPixel(x, y, Color.FromArgb(r, g, b));
                }
            }
        }
        return bitmap;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _bitmap?.Dispose();
        }
        base.Dispose(disposing);
    }
}
