using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PKHeX.Core;

namespace SysBot.Pokemon;

/// <summary> Single-pass batch lifecycle. No queue, hardware, or retry operations. </summary>
public sealed class SVBatchTradeCoordinator<TPoke> where TPoke : PKM, new()
{
    public IReadOnlyList<PokeTradeDetail<TPoke>> Members { get; }
    public int CurrentIndex { get; private set; } = -1;
    public bool CurrentCompleted { get; set; }
    public bool IsContinuation => CurrentIndex > 0;
    public bool KeepConnectionOpen => CurrentIndex >= 0 && CurrentIndex + 1 < Members.Count;

    public SVBatchTradeCoordinator(PokeTradeDetail<TPoke> root)
    {
        var source = root.BatchTrades ?? throw new ArgumentException("Missing batch.", nameof(root));
        var snapshot = new PokeTradeDetail<TPoke>[source.Count];
        for (var i = 0; i < snapshot.Length; i++)
            snapshot[i] = source[i];
        Members = Array.AsReadOnly(snapshot);
    }

    public bool HasValidShape(PokeTradeDetail<TPoke> root)
    {
        if (Members.Count is < 2 or > 12 || !ReferenceEquals(Members[0], root))
            return false;
        for (var i = 0; i < Members.Count; i++)
        {
            var member = Members[i];
            if (member is null || member.Type != PokeTradeType.Specific ||
                (i != 0 && member.BatchTrades is not null) || member.Code != root.Code ||
                member.Trainer != root.Trainer)
                return false;
            for (var j = 0; j < i; j++)
                if (ReferenceEquals(Members[j], member))
                    return false;
        }
        return true;
    }

    public void BeginMember(int index)
    {
        if (index != CurrentIndex + 1 || index >= Members.Count || (CurrentIndex >= 0 && !CurrentCompleted))
            throw new InvalidOperationException("Batch members must run once, in order, after previous completion.");
        CurrentIndex = index;
        CurrentCompleted = false;
    }

    /// <summary> Attempts every outstanding callback even if a notifier fails. </summary>
    public async Task NotifyAbortedAsync(Func<PokeTradeDetail<TPoke>, string, Task> notify,
        Func<PokeTradeDetail<TPoke>, PokeTradeResult, Task> cancel, PokeTradeResult result)
    {
        var errors = new List<Exception>();
        for (var i = Math.Max(0, CurrentIndex); i < Members.Count; i++)
        {
            var member = Members[i];
            if (member is null || (i == CurrentIndex && CurrentCompleted))
                continue;
            // Malformed batches must not report a completed member a second time.
            var duplicate = false;
            for (var j = 0; j < i; j++)
                duplicate |= ReferenceEquals(Members[j], member);
            if (duplicate)
                continue;
            member.IsRetry = true;
            member.IsProcessing = false;
            if (i > CurrentIndex)
            {
                try { await notify(member, $"BatchNotStarted: batch interrupted ({result}).").ConfigureAwait(false); }
                catch (Exception ex) { errors.Add(ex); }
            }
            try { await cancel(member, result).ConfigureAwait(false); }
            catch (Exception ex) { errors.Add(ex); }
        }
        if (errors.Count != 0)
            throw new AggregateException("Batch cancellation notifier failures.", errors);
    }
}
