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
