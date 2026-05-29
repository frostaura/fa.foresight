using FrostAura.Foresight.Domain.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethereum.Signer;
using Nethereum.Signer.EIP712;

namespace FrostAura.Foresight.Infrastructure.Adapters;

/// <summary>
/// Live signing adapter backed by a local private key (Nethereum). Replaces <see cref="LocalKeyVault"/>
/// when a key is configured. The key is held only in memory and never logged; the platform calls
/// <see cref="SignTypedDataAsync"/> with an EIP-712 typed-data JSON payload and gets back a signature —
/// it never sees raw key bytes (the <see cref="IKeyVault"/> contract).
///
/// This is the Phase-3 live-execution dependency. Configuring a key does NOT by itself trade live:
/// execution also requires Polymarket:LiveTrading=true and an explicit /golive confirmation.
/// </summary>
public sealed class NethereumKeyVault : IKeyVault
{
    private readonly EthECKey _key;
    private readonly string _address;

    public string AdapterId => "nethereum-local";

    public NethereumKeyVault(IOptions<KeyVaultOptions> opts, ILogger<NethereumKeyVault> logger)
    {
        var pk = opts.Value.PrivateKey.Trim();
        if (string.IsNullOrWhiteSpace(pk))
            throw new InvalidOperationException("NethereumKeyVault requires KeyVault:PrivateKey.");
        _key = new EthECKey(pk.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? pk : "0x" + pk);
        _address = _key.GetPublicAddress();
        logger.LogInformation("NethereumKeyVault initialised for address {Address}", _address);
    }

    public Task<string> SignTypedDataAsync(string typedDataPayload, CancellationToken ct)
    {
        // EIP-712 v4 over the raw typed-data JSON (Polymarket CLOB orders + auth messages are signed this way).
        var signer = new Eip712TypedDataSigner();
        var signature = signer.SignTypedDataV4(typedDataPayload, _key);
        return Task.FromResult(signature);
    }

    public Task<string> GetPublicAddressAsync(CancellationToken ct) => Task.FromResult(_address);
}
