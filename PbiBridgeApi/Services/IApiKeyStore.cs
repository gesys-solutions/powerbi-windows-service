namespace PbiBridgeApi.Services;

/// <summary>
/// Store for API keys mapping api_key -> client_id.
/// DA-014: isolation par client_id.
/// </summary>
public interface IApiKeyStore
{
    /// <summary>Try to resolve an api_key to a client_id.</summary>
    bool TryGetClientId(string apiKey, out string? clientId);

    /// <summary>Register a new client api_key. Returns false if key already exists.</summary>
    bool RegisterClient(string clientId, string apiKey);

    /// <summary>Revoke a client by client_id. Returns false if not found.</summary>
    bool RevokeClient(string clientId);

    /// <summary>List all registered clients (clientId, maskedKey).</summary>
    IEnumerable<(string ClientId, string MaskedKey)> ListClients();
}
