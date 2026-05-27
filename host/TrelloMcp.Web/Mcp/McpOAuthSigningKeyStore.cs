using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace TrelloMcp.Web.Mcp;

public sealed class McpOAuthSigningKeyStore
{
    private readonly IDataProtector _protector;
    private readonly string _keyFile;
    private readonly string _idFile;
    private readonly ILogger<McpOAuthSigningKeyStore> _logger;
    private SigningKeyMaterial? _material;

    public McpOAuthSigningKeyStore(
        IDataProtectionProvider dataProtection,
        IWebHostEnvironment env,
        ILogger<McpOAuthSigningKeyStore> logger)
    {
        _logger = logger;
        _protector = dataProtection.CreateProtector("TrelloMcp.Web.McpOAuth.SigningKey.v1");
        var dir = Path.Combine(env.ContentRootPath, "App_Data");
        Directory.CreateDirectory(dir);
        _keyFile = Path.Combine(dir, "mcp-oauth-signing.dp");
        _idFile = Path.Combine(dir, "mcp-oauth-keyid.txt");
    }

    public SigningKeyMaterial GetMaterial() => _material ??= LoadOrCreate();

    private SigningKeyMaterial LoadOrCreate()
    {
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
