using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace TrelloMcp.Web.Mcp;

public sealed class McpOAuthSigningKeyStore
{
    private readonly IDataProtector _protector;
    private readonly McpOAuthSettings _settings;
    private readonly string _keyFile;
    private readonly string _idFile;
    private readonly ILogger<McpOAuthSigningKeyStore> _logger;
    private SigningKeyMaterial? _material;

    public McpOAuthSigningKeyStore(
        IDataProtectionProvider dataProtection,
        IOptions<McpOAuthSettings> settings,
        IWebHostEnvironment env,
        ILogger<McpOAuthSigningKeyStore> logger)
    {
        _logger = logger;
        _settings = settings.Value;
        _protector = dataProtection.CreateProtector("TrelloMcp.Web.McpOAuth.SigningKey.v1");
        var dir = McpDataPaths.GetRoot(env);
        _keyFile = Path.Combine(dir, "mcp-oauth-signing.dp");
        _idFile = Path.Combine(dir, "mcp-oauth-keyid.txt");
    }

    public SigningKeyMaterial GetMaterial() => _material ??= LoadOrCreate();

    private SigningKeyMaterial LoadOrCreate()
    {
        var configuredKey = _settings.SigningKeyPkcs8Base64?.Trim();
        if (!string.IsNullOrWhiteSpace(configuredKey))
        {
            try
            {
                var privateKey = Convert.FromBase64String(configuredKey);
                var rsa = RSA.Create();
                rsa.ImportPkcs8PrivateKey(privateKey, out _);
                var keyId = string.IsNullOrWhiteSpace(_settings.SigningKeyId)
                    ? "configured"
                    : _settings.SigningKeyId.Trim();
                _logger.LogInformation("Using MCP OAuth signing key from configuration (kid {KeyId}).", keyId);
                return new SigningKeyMaterial(rsa, keyId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "McpOAuth:SigningKeyPkcs8Base64 is invalid; falling back to persisted/generated key.");
            }
        }

        if (File.Exists(_keyFile) && File.Exists(_idFile))
        {
            try
            {
                var protectedBytes = Convert.FromBase64String(File.ReadAllText(_keyFile).Trim());
                var privateKey = _protector.Unprotect(protectedBytes);
                var rsa = RSA.Create();
                rsa.ImportRSAPrivateKey(privateKey, out _);
                var keyId = File.ReadAllText(_idFile).Trim();
                if (!string.IsNullOrEmpty(keyId))
                {
                    return new SigningKeyMaterial(rsa, keyId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load persisted MCP OAuth signing key; generating a new one.");
            }
        }

        var newRsa = RSA.Create(2048);
        var newKeyId = Guid.NewGuid().ToString("N");
        var exported = newRsa.ExportRSAPrivateKey();
        File.WriteAllText(_keyFile, Convert.ToBase64String(_protector.Protect(exported)));
        File.WriteAllText(_idFile, newKeyId);
        _logger.LogInformation("Created new MCP OAuth signing key (kid {KeyId}).", newKeyId);
        return new SigningKeyMaterial(newRsa, newKeyId);
    }
}

public sealed record SigningKeyMaterial(RSA Rsa, string KeyId);
