namespace LiveTranslator.Core.Pipeline;

/// <summary>Thread-safe LRU map from (provider, language, normalized text) to a finished translation.</summary>
public sealed class TranslationCache
{
    private readonly int _capacity;
    private readonly Dictionary<string, LinkedListNode<(string Key, string Value)>> _map = new();
    private readonly LinkedList<(string Key, string Value)> _order = new();
    private readonly object _gate = new();

    public TranslationCache(int capacity)
    {
        _capacity = Math.Max(1, capacity);
    }

    public bool TryGet(string key, out string value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }
        value = "";
        return false;
    }

    public void Set(string key, string value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
                _order.Remove(existing);
            var node = new LinkedListNode<(string, string)>((key, value));
            _order.AddFirst(node);
            _map[key] = node;
            while (_map.Count > _capacity)
            {
                var last = _order.Last!;
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _order.Clear();
        }
    }
}
