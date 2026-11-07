using RhythmAutoplay.Core;

namespace RhythmAutoplay.App;

/// <summary>
/// ゲーム画面に重ねて認識範囲（レーン台形と判定ライン）を表示するオーバレイ。
/// 表示のみの時はクリックを透過し、調整モードではハンドルをドラッグして
/// レーン形状と判定ラインを変更できる。座標系はゲームのクライアント領域と
/// 同じ正規化座標 (0..1) で、本体側がこのフォームをクライアント領域に一致させる。
/// </summary>
public sealed class OverlayForm : Form
{
    private enum DragHandle { None, TopLeft, TopRight, BottomLeft, BottomRight, TopCenter, Judgment }

    private const int HandleSize = 14;

    private readonly Func<GameProfile> _profileProvider;
    private bool _editMode;
    private DragHandle _dragging = DragHandle.None;

    /// <summary>ドラッグで形状が変わった時に、新しいプロファイルで通知する。</summary>
    public event Action<GameProfile>? GeometryChanged;

    /// <summary>ライブ検出表示用のフレーム提供元（v0.3.59）。
    /// セッション実行中だけ値が返る。編集モードでは描かない。</summary>
    public Func<DetectionView?>? ViewProvider { get; set; }

    public OverlayForm(Func<GameProfile> profileProvider)
    {
        _profileProvider = profileProvider;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
        DoubleBuffered = true;
        StartPosition = FormStartPosition.Manual;
    }

    public bool EditMode
    {
        get => _editMode;
        set
        {
            if (_editMode == value)
            {
                return;
            }
            _editMode = value;
            RecreateHandle(); // 透過スタイルの切り替えに必要
            Invalidate();
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW（タスクバー/Alt+Tab に出さない）
            if (!_editMode)
            {
                cp.ExStyle |= 0x00000020; // WS_EX_TRANSPARENT（クリック透過）
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            }
            return cp;
        }
    }

    private (double X, double Y) ToNorm(Point p) =>
        (p.X / (double)Math.Max(1, ClientSize.Width), p.Y / (double)Math.Max(1, ClientSize.Height));

    private Point ToPoint(double nx, double ny) =>
        new((int)(nx * ClientSize.Width), (int)(ny * ClientSize.Height));

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        GameProfile profile = _profileProvider();
        LaneGeometry geo = profile.Geometry;
        Graphics g = e.Graphics;

        using var lanePen = new Pen(Color.Cyan, 2);
        using var edgePen = new Pen(Color.LimeGreen, 2);
        using var judgePen = new Pen(Color.Yellow, 3);

        double topHalf = geo.HalfWidthAt(geo.TopY);
        double judgeHalf = geo.HalfWidthAt(profile.JudgmentLineY);

        // レーン境界線（台形の縦線）
        for (int i = 0; i <= profile.LaneCount; i++)
        {
            double topX = geo.CenterX - topHalf + 2 * topHalf * i / profile.LaneCount;
            double bottomX = geo.CenterX - judgeHalf + 2 * judgeHalf * i / profile.LaneCount;
            g.DrawLine(lanePen, ToPoint(topX, geo.TopY), ToPoint(bottomX, profile.JudgmentLineY));
        }
        // 上端
        g.DrawLine(edgePen, ToPoint(geo.CenterX - topHalf, geo.TopY), ToPoint(geo.CenterX + topHalf, geo.TopY));
        // 判定ライン
        Point jl1 = ToPoint(geo.CenterX - judgeHalf, profile.JudgmentLineY);
        Point jl2 = ToPoint(geo.CenterX + judgeHalf, profile.JudgmentLineY);
        g.DrawLine(judgePen, jl1, jl2);

        // ライブ検出表示（v0.3.59）: セッション中の検出状態を重ねる。
        // 在席セル・矢印バンド・予約マーカーを DetectionPainter で描く。
        if (!_editMode && ViewProvider?.Invoke() is { } view)
        {
            DetectionPainter.Draw(g, view, profile, ClientSize.Width, ClientSize.Height, null);
        }

        if (_editMode)
        {
            using var handleBrush = new SolidBrush(Color.OrangeRed);
            foreach (Point h in HandlePoints(profile))
            {
                g.FillRectangle(handleBrush, h.X - HandleSize / 2, h.Y - HandleSize / 2, HandleSize, HandleSize);
            }
            using var textBrush = new SolidBrush(Color.White);
            g.DrawString("範囲調整中: ハンドルをドラッグ（端=幅/上端、中央上=中心、判定ライン中央=判定位置）",
                SystemFonts.MessageBoxFont, textBrush, 8, 8);
        }
    }

    private IEnumerable<Point> HandlePoints(GameProfile profile)
    {
        LaneGeometry geo = profile.Geometry;
        double topHalf = geo.HalfWidthAt(geo.TopY);
        double judgeHalf = geo.HalfWidthAt(profile.JudgmentLineY);
        yield return ToPoint(geo.CenterX - topHalf, geo.TopY);          // TopLeft
        yield return ToPoint(geo.CenterX + topHalf, geo.TopY);          // TopRight
        yield return ToPoint(geo.CenterX - judgeHalf, profile.JudgmentLineY); // BottomLeft
        yield return ToPoint(geo.CenterX + judgeHalf, profile.JudgmentLineY); // BottomRight
        yield return ToPoint(geo.CenterX, geo.TopY);                    // TopCenter
        yield return ToPoint(geo.CenterX, profile.JudgmentLineY);       // Judgment
    }

    private DragHandle HitTest(Point p, GameProfile profile)
    {
        var handles = new[]
        {
            DragHandle.TopLeft, DragHandle.TopRight, DragHandle.BottomLeft,
            DragHandle.BottomRight, DragHandle.TopCenter, DragHandle.Judgment,
        };
        int i = 0;
        foreach (Point h in HandlePoints(profile))
        {
            if (Math.Abs(p.X - h.X) <= HandleSize && Math.Abs(p.Y - h.Y) <= HandleSize)
            {
                return handles[i];
            }
            i++;
        }
        return DragHandle.None;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!_editMode || e.Button != MouseButtons.Left)
        {
            return;
        }
        _dragging = HitTest(e.Location, _profileProvider());
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = DragHandle.None;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_editMode || _dragging == DragHandle.None)
        {
            return;
        }
        GameProfile profile = _profileProvider();
        LaneGeometry geo = profile.Geometry;
        (double nx, double ny) = ToNorm(e.Location);

        double centerX = geo.CenterX, topY = geo.TopY, topHalf = geo.TopHalfWidth;
        double bottomHalf = geo.BottomHalfWidth, judgeY = profile.JudgmentLineY;

        switch (_dragging)
        {
            case DragHandle.TopLeft:
            case DragHandle.TopRight:
                topY = Clamp(ny, 0.02, 0.80);
                topHalf = Clamp(Math.Abs(nx - centerX), 0.01, 0.45);
                break;
            case DragHandle.BottomLeft:
            case DragHandle.BottomRight:
                bottomHalf = Clamp(Math.Abs(nx - centerX), 0.03, 0.49);
                break;
            case DragHandle.TopCenter:
                centerX = Clamp(nx, 0.20, 0.80);
                break;
            case DragHandle.Judgment:
                judgeY = Clamp(ny, 0.50, 0.995);
                break;
        }

        GameProfile updated = profile with
        {
            JudgmentLineY = judgeY,
            Geometry = new LaneGeometry(centerX, topY, topHalf, judgeY, bottomHalf, geo.VanishingY),
        };
        GeometryChanged?.Invoke(updated);
        Invalidate();
    }

    private static double Clamp(double v, double min, double max) => Math.Max(min, Math.Min(max, v));
}
