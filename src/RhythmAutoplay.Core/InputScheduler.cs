namespace RhythmAutoplay.Core;

/// <summary>
/// t_hit 順に入力イベントを保持し、時刻が来たものを取り出すスケジューラ。
/// 発火スレッド（高分解能タイマ + 最終区間のビジーウェイト）は本体側が担当し、
/// ここは順序と取り出しの正しさだけを担う。
/// </summary>
public sealed class InputScheduler
{
    private readonly List<ScheduledInput> _queue = new();

    public int Count => _queue.Count;

    public void Enqueue(ScheduledInput input)
    {
        // 同時刻は登録順を保つため、安定する位置に挿入する。
        int index = _queue.Count;
        while (index > 0 && _queue[index - 1].TimeSeconds > input.TimeSeconds)
        {
            index--;
        }
        _queue.Insert(index, input);
    }

    /// <summary>次の発火時刻。空なら null。</summary>
    public double? NextTimeSeconds => _queue.Count > 0 ? _queue[0].TimeSeconds : null;

    /// <summary>
    /// タグ付きイベントの時刻をまとめてずらす（予約の事後修正、v0.3.37）。
    /// そのタグの down イベントがまだキューに残っている場合だけ適用し、
    /// 適用したら true を返す。down が既に取り出されていたら何もせず
    /// false（発火済みの予約は動かせない）。ずらした後は時刻順を保つ。
    /// </summary>
    public bool ShiftPending(long tag, double deltaSeconds)
    {
        if (tag == 0 || deltaSeconds == 0)
        {
            return false;
        }
        bool hasDown = false;
        foreach (ScheduledInput e in _queue)
        {
            if (e.Tag == tag && e.IsDown)
            {
                hasDown = true;
                break;
            }
        }
        if (!hasDown)
        {
            return false;
        }
        for (int i = 0; i < _queue.Count; i++)
        {
            if (_queue[i].Tag == tag)
            {
                _queue[i] = _queue[i] with { TimeSeconds = _queue[i].TimeSeconds + deltaSeconds };
            }
        }
        // 時刻順を安定ソートで回復する（同タグ・同時刻の順序は保つ）。
        var sorted = _queue.OrderBy(e => e.TimeSeconds).ToList();
        _queue.Clear();
        _queue.AddRange(sorted);
        return true;
    }

    /// <summary>now 以前に発火すべきイベントを時刻順に取り出して返す。</summary>
    public IReadOnlyList<ScheduledInput> CollectDue(double nowSeconds)
    {
        var due = new List<ScheduledInput>();
        while (_queue.Count > 0 && _queue[0].TimeSeconds <= nowSeconds)
        {
            due.Add(_queue[0]);
            _queue.RemoveAt(0);
        }
        return due;
    }

    public void Clear() => _queue.Clear();
}
