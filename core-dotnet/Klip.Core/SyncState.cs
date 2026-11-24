namespace Klip.Core;

public enum OutgoingDecision
{
    Send,
    SkipDisabled,
    SkipSensitive,
    SkipBlank,
    SkipAlreadySent,
    SkipJustReceived,
}

public enum IncomingDecision
{
    Accept,
    IgnoreSelf,
    IgnoreDuplicate,
}

/// <summary>Persistable copy of <see cref="SyncState"/>.</summary>
public sealed record SyncSnapshot(string? LastSentHash = null, string? LastReceivedHash = null, IReadOnlyList<string>? ProcessedMessageIds = null);

/// <summary>Anti-loop and anti-duplicate rules. See docs/protocol.md §7. Thread-safe.</summary>
public sealed class SyncState
{
    private readonly string _localDeviceId;
    private readonly Lock _lock = new();
    private readonly LinkedList<string> _processedIds;
    private string? _lastSentHash;
    private string? _lastReceivedHash;

    public SyncState(string localDeviceId, SyncSnapshot? snapshot = null)
    {
        _localDeviceId = localDeviceId;
        _lastSentHash = snapshot?.LastSentHash;
        _lastReceivedHash = snapshot?.LastReceivedHash;
        _processedIds = new LinkedList<string>((snapshot?.ProcessedMessageIds ?? []).TakeLast(Protocol.ProcessedIdsCapacity));
    }

    public OutgoingDecision EvaluateOutgoing(string content, bool isTest = false, bool isSensitive = false, bool syncEnabled = true)
    {
        lock (_lock)
        {
            if (!syncEnabled) return OutgoingDecision.SkipDisabled;
            if (isSensitive) return OutgoingDecision.SkipSensitive;
            if (Content.IsBlank(content)) return OutgoingDecision.SkipBlank;
            if (isTest) return OutgoingDecision.Send;
            var hash = Content.Hash(content);
            if (hash == _lastSentHash) return OutgoingDecision.SkipAlreadySent;
            if (hash == _lastReceivedHash) return OutgoingDecision.SkipJustReceived;
            return OutgoingDecision.Send;
        }
    }

    /// <summary>Call after a non-test send.</summary>
    public void MarkSent(string contentHash)
    {
        lock (_lock) _lastSentHash = contentHash;
    }

    /// <summary>
    /// Applies the incoming rules. On <see cref="IncomingDecision.Accept"/> the message is recorded and
    /// lastReceivedHash updated, so call this BEFORE writing to the clipboard.
    /// </summary>
    public IncomingDecision AcceptIncoming(string originDeviceId, string messageId, string contentHash)
    {
        lock (_lock)
        {
            if (originDeviceId == _localDeviceId) return IncomingDecision.IgnoreSelf;
            if (_processedIds.Contains(messageId)) return IncomingDecision.IgnoreDuplicate;
            _processedIds.AddLast(messageId);
            while (_processedIds.Count > Protocol.ProcessedIdsCapacity) _processedIds.RemoveFirst();
            _lastReceivedHash = contentHash;
            return IncomingDecision.Accept;
        }
    }

    public SyncSnapshot Snapshot()
    {
        lock (_lock) return new SyncSnapshot(_lastSentHash, _lastReceivedHash, _processedIds.ToList());
    }
}
