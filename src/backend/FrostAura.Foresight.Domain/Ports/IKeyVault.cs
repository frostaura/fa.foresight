namespace FrostAura.Foresight.Domain.Ports;

/// <summary>
/// Order signing capability. v1 adapter: local keystore. Future: hardware wallet, KMS, MPC, ERC-4337.
/// The platform never sees raw private-key bytes — adapters expose signing as a service only.
/// </summary>
public interface IKeyVault
{
    string AdapterId { get; }

    Task<string> SignTypedDataAsync(string typedDataPayload, CancellationToken ct);

    Task<string> GetPublicAddressAsync(CancellationToken ct);
}
