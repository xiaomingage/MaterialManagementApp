using MaterialManagementApp.Models;
using Xunit;

namespace MaterialManagementApp.Tests;

/// <summary>
/// API Key DPAPI 加密存储的单元测试：验证「内存明文 / 落盘密文」的读写往返、
/// 旧版明文兼容与跨机解密失败降级。测试进程与真实用户配置互不影响。
/// </summary>
public sealed class AiApiKeyEncryptionTests
{
    [Fact]
    public void Stored_Value_Is_Encrypted_With_Prefix()
    {
        var settings = new AppSettings { AiApiKey = "sk-test-123456" };

        var stored = settings.AiApiKeyStored;

        Assert.NotEqual("sk-test-123456", stored);
        Assert.StartsWith("dpapi:", stored);
    }

    [Fact]
    public void RoundTrip_Protect_Then_Unprotect_Returns_Plain()
    {
        var original = "ark-abcdef1234567890";
        var writer = new AppSettings { AiApiKey = original };
        var stored = writer.AiApiKeyStored;

        var reader = new AppSettings { AiApiKeyStored = stored };

        Assert.Equal(original, reader.AiApiKey);
        Assert.True(reader.IsAiConfigured == false); // 只有 Key，无地址与模型名，不构成完整配置
    }

    [Fact]
    public void Legacy_Plain_Value_Is_Kept_As_Is()
    {
        // 旧版本 settings.json 里是明文：读入后原样可用，下次保存才会转为密文
        var reader = new AppSettings { AiApiKeyStored = "sk-legacy-plain-key" };

        Assert.Equal("sk-legacy-plain-key", reader.AiApiKey);
    }

    [Fact]
    public void Tampered_Cipher_Falls_Back_To_Empty()
    {
        var reader = new AppSettings { AiApiKeyStored = "dpapi:not-a-valid-base64!!!" };

        Assert.Equal(string.Empty, reader.AiApiKey);
    }

    [Fact]
    public void Empty_Key_Stays_Empty()
    {
        var settings = new AppSettings { AiApiKey = string.Empty };

        Assert.Equal(string.Empty, settings.AiApiKeyStored);
    }
}
