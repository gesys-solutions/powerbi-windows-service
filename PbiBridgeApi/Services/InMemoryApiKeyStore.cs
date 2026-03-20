using System.Collections.Concurrent;

namespace PbiBridgeApi.Services;

/// <summary>
/// In-memory implementation of IApiKeyStore using a ConcurrentDictionary (thread-safe).
/// DA-014: isolation stricte par client_id.
/// </summary>
public class InMemoryApiKeyStore : IApiKeyStore
{
    // apiKey -> clientId
    private readonly ConcurrentDictionary<string, string> _store = new(StringComparer.Ordinal);

    public bool TryGetClientId(string apiKey, out string? clientId)
        => _store.TryGetValue(apiKey, out clientId);

    public bool RegisterClient(string clientId, string apiKey)
    {
        // Prevent duplicate keys or duplicate client_ids
        if (_store.Values.Contains(clientId, StringComparer.Ordinal))
            return false;
        return _store.TryAdd(apiKey, clientId);
    }

    public bool RevokeClient(string clientId)
    {
        var pair = _store.FirstOrDefault(kv => kv.Value == clientId);
        if (pair.Key is null) return false;
        return _store.TryRemove(pair.Key, out _);
    }

    public IEnumerable<(string ClientId, string MaskedKey)> ListClients()
        => _store.Select(kv => (kv.Value, MaskKey(kv.Key)));

    private static string MaskKey(string key)
        => key.Length > 8 ? key[..4] + "****" + key[^4..] : "****";
}
