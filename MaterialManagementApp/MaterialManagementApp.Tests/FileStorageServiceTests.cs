using System;
using System.IO;
using MaterialManagementApp.Services;
using Xunit;

namespace MaterialManagementApp.Tests;

/// <summary>
/// 归档相关测试共享集合：部分用例会临时改写设置中的归档根，
/// 禁止与其他测试集合并行，避免共享单例设置互相干扰。
/// </summary>
[CollectionDefinition("FileStorage", DisableParallelization = true)]
public sealed class FileStorageCollection
{
}

/// <summary>
/// 文件归档功能的单元测试：文件名清洗、大小写不敏感去重、日期目录结构、
/// 可写性探测、导入/导出真实落盘以及写入失败清理。
/// </summary>
[Collection("FileStorage")]
public sealed class FileStorageServiceTests
{
    // ==================== 文件名清洗 ====================

    /// <summary>Windows 非法字符应被替换为下划线。</summary>
    [Fact]
    public void SanitizeFileName_ReplacesInvalidCharacters()
    {
        var result = FileStorageService.SanitizeFileName("供:应*商?合同\".pdf");

        Assert.DoesNotContain(':', result);
        Assert.DoesNotContain('*', result);
        Assert.DoesNotContain('?', result);
        Assert.DoesNotContain('"', result);
        Assert.EndsWith(".pdf", result);
    }

    /// <summary>保留设备名（如 CON）加前缀后仍可作为文件名。</summary>
    [Fact]
    public void SanitizeFileName_PrefixesReservedDeviceName()
    {
        Assert.Equal("_CON.txt", FileStorageService.SanitizeFileName("CON.txt"));
        Assert.Equal("_com1.xlsx", FileStorageService.SanitizeFileName("com1.xlsx"));
    }

    /// <summary>空文件名回退为 file，避免生成空路径。</summary>
    [Fact]
    public void SanitizeFileName_UsesFallbackForEmptyName()
    {
        Assert.Equal("file", FileStorageService.SanitizeFileName("   "));
        Assert.Equal("file", FileStorageService.SanitizeFileName(null));
    }

    /// <summary>结尾的点与空格在 Windows 上会被吞掉，必须主动去掉。</summary>
    [Fact]
    public void SanitizeFileName_TrimsTrailingDotsAndSpaces()
    {
        Assert.Equal("合同", FileStorageService.SanitizeFileName("合同...  "));
    }

    /// <summary>超长文件名截断后仍保留扩展名且不超过 120 字符。</summary>
    [Fact]
    public void SanitizeFileName_LimitsLengthAndKeepsExtension()
    {
        var result = FileStorageService.SanitizeFileName(new string('a', 300) + ".xlsx");

        Assert.True(result.Length <= 120);
        Assert.EndsWith(".xlsx", result);
    }

    /// <summary>中文文件名应原样保留。</summary>
    [Fact]
    public void SanitizeFileName_KeepsChineseCharacters()
    {
        Assert.Equal("供应商合同-2026.pdf", FileStorageService.SanitizeFileName("供应商合同-2026.pdf"));
    }

    // ==================== 重名去重 ====================

    /// <summary>同名（大小写不敏感）时依次追加 _1、_2，绝不覆盖已有文件。</summary>
    [Fact]
    public void BuildUniqueFileName_AppendsIndexCaseInsensitive()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "导出.xlsx"), string.Empty);
            File.WriteAllText(Path.Combine(directory, "导出_1.xlsx"), string.Empty);

            var result = FileStorageService.BuildUniqueFileName(directory, "导出.XLSX");

            // 扩展名大小写按用户输入保留，判重本身大小写不敏感
            Assert.Equal("导出_2.XLSX", result);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    /// <summary>目录内无同名文件时返回 sanitize 后的原名。</summary>
    [Fact]
    public void BuildUniqueFileName_ReturnsOriginalWhenNoConflict()
    {
        var directory = CreateTempDirectory();
        try
        {
            var result = FileStorageService.BuildUniqueFileName(directory, "库存流水.xlsx");
            Assert.Equal("库存流水.xlsx", result);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    /// <summary>已存在 _2 但缺 _1 时，应复用最小的空序号 _1。</summary>
    [Fact]
    public void BuildUniqueFileName_UsesFirstFreeIndex()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "台账.xlsx"), string.Empty);
            File.WriteAllText(Path.Combine(directory, "台账_2.xlsx"), string.Empty);

            var result = FileStorageService.BuildUniqueFileName(directory, "台账.xlsx");

            Assert.Equal("台账_1.xlsx", result);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    /// <summary>目录不存在时视为无冲突，不创建目录。</summary>
    [Fact]
    public void BuildUniqueFileName_WorksForMissingDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mma-storage-tests", Guid.NewGuid().ToString("N"));

        Assert.Equal("a.xlsx", FileStorageService.BuildUniqueFileName(directory, "a.xlsx"));
        Assert.False(Directory.Exists(directory));
    }

    // ==================== 目录结构与预览 ====================

    /// <summary>导入日期目录必须为 Imports\类别\yyyyMMdd。</summary>
    [Fact]
    public void ImportDateDir_BuildsCategoryAndDateFolders()
    {
        var root = CreateTempDirectory();
        try
        {
            var directory = AppPaths.ImportDateDir(root, FileStorageService.ContractCategory, new DateTime(2026, 11, 17));

            Assert.Equal(Path.Combine(root, "Imports", "合同", "20261117"), directory);
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    /// <summary>导出日期目录必须为 Exports\类别\yyyyMMdd。</summary>
    [Fact]
    public void ExportDateDir_BuildsCategoryAndDateFolders()
    {
        var root = CreateTempDirectory();
        try
        {
            var directory = AppPaths.ExportDateDir(root, FileStorageService.ExcelCategory, new DateTime(2026, 11, 17));

            Assert.Equal(Path.Combine(root, "Exports", "Excel", "20261117"), directory);
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    /// <summary>设置为空时预览使用默认归档根；设置非空时按输入值推算。</summary>
    [Fact]
    public void PreviewLayout_UsesDefaultWhenEmptyAndOverrideWhenSet()
    {
        FileStorageService.PreviewLayout(null, out var defaultRoot, out var defaultImports, out var defaultExports);
        Assert.Equal(AppPaths.DefaultArchiveRoot, defaultRoot);
        Assert.Equal(Path.Combine(defaultRoot, "Imports"), defaultImports);
        Assert.Equal(Path.Combine(defaultRoot, "Exports"), defaultExports);

        var custom = CreateTempDirectory();
        try
        {
            FileStorageService.PreviewLayout($"  {custom}  ", out var root, out var imports, out _);

            // 前后空格应被去除，预览不做任何磁盘写入
            Assert.Equal(custom, root);
            Assert.Equal(Path.Combine(custom, "Imports"), imports);
        }
        finally
        {
            DeleteTempDirectory(custom);
        }
    }

    // ==================== 可写性探测 ====================

    /// <summary>可写目录返回 true，且目录会被创建。</summary>
    [Fact]
    public void TryEnsureWritable_CreatesAndAcceptsDirectory()
    {
        var root = CreateTempDirectory();
        try
        {
            var target = Path.Combine(root, "新目录");

            Assert.True(FileStorageService.TryEnsureWritable(target, out var error));
            Assert.Null(error);
            Assert.True(Directory.Exists(target));
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    /// <summary>路径指向已存在的文件时不可写，返回原因而不是抛出。</summary>
    [Fact]
    public void TryEnsureWritable_RejectsFilePath()
    {
        var root = CreateTempDirectory();
        try
        {
            var file = Path.Combine(root, "占用.txt");
            File.WriteAllText(file, "x");

            Assert.False(FileStorageService.TryEnsureWritable(file, out var error));
            Assert.False(string.IsNullOrWhiteSpace(error));
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    /// <summary>空路径与相对路径直接判定不可写，避免在进程当前目录乱建文件夹。</summary>
    [Fact]
    public void TryEnsureWritable_RejectsEmptyAndRelativePath()
    {
        Assert.False(FileStorageService.TryEnsureWritable("   ", out var emptyError));
        Assert.False(string.IsNullOrWhiteSpace(emptyError));

        Assert.False(FileStorageService.TryEnsureWritable("relative\\folder", out var relativeError));
        Assert.False(string.IsNullOrWhiteSpace(relativeError));
    }

    // ==================== 导入 / 导出真实落盘 ====================

    /// <summary>导入文件应复制到 Imports\类别\yyyyMMdd，内容保持一致且不改动源文件。</summary>
    [Fact]
    public void StoreImport_CopiesIntoImportsCategoryDateFolder()
    {
        var root = CreateTempDirectory();
        var sourceDirectory = CreateTempDirectory();
        try
        {
            var source = Path.Combine(sourceDirectory, "采购合同.pdf");
            File.WriteAllText(source, "contract-content");
            var today = DateTime.Now.ToString("yyyyMMdd");

            var result = WithArchiveRoot(root, () => FileStorageService.Instance.StoreImport(
                FileStorageService.ContractCategory,
                source));

            Assert.True(result.Success);
            Assert.NotNull(result.Path);
            Assert.True(File.Exists(result.Path));
            Assert.Equal("contract-content", File.ReadAllText(result.Path!));
            Assert.Contains(
                $"{Path.DirectorySeparatorChar}Imports{Path.DirectorySeparatorChar}合同{Path.DirectorySeparatorChar}{today}{Path.DirectorySeparatorChar}",
                result.Path);

            // 源文件保持在原位，不被移动
            Assert.True(File.Exists(source));
        }
        finally
        {
            DeleteTempDirectory(root);
            DeleteTempDirectory(sourceDirectory);
        }
    }

    /// <summary>连续导入同名文件时自动追加序号，两份文件内容互不覆盖。</summary>
    [Fact]
    public void StoreImport_NeverOverwritesSameName()
    {
        var root = CreateTempDirectory();
        var sourceDirectory = CreateTempDirectory();
        try
        {
            var first = Path.Combine(sourceDirectory, "a");
            var second = Path.Combine(sourceDirectory, "b");
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);

            var sourceOne = Path.Combine(first, "扫描单.jpg");
            var sourceTwo = Path.Combine(second, "扫描单.jpg");
            File.WriteAllText(sourceOne, "one");
            File.WriteAllText(sourceTwo, "two");

            var resultOne = WithArchiveRoot(root, () => FileStorageService.Instance.StoreImport(
                FileStorageService.ReceiptCategory, sourceOne));
            var resultTwo = WithArchiveRoot(root, () => FileStorageService.Instance.StoreImport(
                FileStorageService.ReceiptCategory, sourceTwo));

            Assert.Equal("扫描单.jpg", Path.GetFileName(resultOne.Path));
            Assert.Equal("扫描单_1.jpg", Path.GetFileName(resultTwo.Path));
            Assert.Equal("one", File.ReadAllText(resultOne.Path!));
            Assert.Equal("two", File.ReadAllText(resultTwo.Path!));
        }
        finally
        {
            DeleteTempDirectory(root);
            DeleteTempDirectory(sourceDirectory);
        }
    }

    /// <summary>源文件不存在时返回失败原因，不创建任何归档文件。</summary>
    [Fact]
    public void StoreImport_FailsWhenSourceMissing()
    {
        var root = CreateTempDirectory();
        try
        {
            var missing = Path.Combine(root, "不存在.pdf");

            var result = WithArchiveRoot(root, () => FileStorageService.Instance.StoreImport(
                FileStorageService.ContractCategory, missing));

            Assert.False(result.Success);
            Assert.Null(result.Path);
            Assert.False(string.IsNullOrWhiteSpace(result.Error));
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    /// <summary>导出应写入 Exports\类别\yyyyMMdd；同名导出自动追加 _1，不覆盖旧文件。</summary>
    [Fact]
    public void StoreExport_WritesIntoExportsAndAppendsIndex()
    {
        var root = CreateTempDirectory();
        try
        {
            var today = DateTime.Now.ToString("yyyyMMdd");
            var first = WithArchiveRoot(root, () => FileStorageService.Instance.StoreExport(
                FileStorageService.ExcelCategory,
                "库存流水.xlsx",
                stream => stream.WriteByte(1)));
            var second = WithArchiveRoot(root, () => FileStorageService.Instance.StoreExport(
                FileStorageService.ExcelCategory,
                "库存流水.xlsx",
                stream => stream.WriteByte(2)));

            Assert.True(first.Success);
            Assert.True(second.Success);
            Assert.Equal("库存流水.xlsx", Path.GetFileName(first.Path));
            Assert.Equal("库存流水_1.xlsx", Path.GetFileName(second.Path));
            Assert.Contains(
                $"{Path.DirectorySeparatorChar}Exports{Path.DirectorySeparatorChar}Excel{Path.DirectorySeparatorChar}{today}{Path.DirectorySeparatorChar}",
                first.Path);

            Assert.Equal(1, File.ReadAllBytes(first.Path!)[0]);
            Assert.Equal(2, File.ReadAllBytes(second.Path!)[0]);
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    /// <summary>写入委托中途抛异常时返回失败原因，并删除不完整文件。</summary>
    [Fact]
    public void StoreExport_RemovesPartialFileWhenWriterThrows()
    {
        var root = CreateTempDirectory();
        try
        {
            var now = DateTime.Now;
            var result = WithArchiveRoot(root, () => FileStorageService.Instance.StoreExport(
                FileStorageService.WordCategory,
                "工作日志.docx",
                stream =>
                {
                    stream.WriteByte(1);
                    throw new InvalidOperationException("模拟写入磁盘失败");
                }));

            Assert.False(result.Success);
            Assert.Null(result.Path);
            Assert.Contains("模拟写入磁盘失败", result.Error ?? string.Empty);

            var directory = AppPaths.ExportDateDir(root, FileStorageService.WordCategory, now);
            Assert.True(!Directory.Exists(directory) || Directory.GetFiles(directory).Length == 0);
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    // ==================== 辅助方法 ====================

    /// <summary>
    /// 临时把设置中的归档根指向测试目录执行断言，结束后恢复原值，
    /// 避免污染其他用例或真实用户配置（不会调用 Save 落盘）。
    /// </summary>
    private static T WithArchiveRoot<T>(string root, Func<T> action)
    {
        var settings = SettingsService.Instance.Settings;
        var previous = settings.ArchiveRootPath;
        try
        {
            settings.ArchiveRootPath = root;
            return action();
        }
        finally
        {
            settings.ArchiveRootPath = previous;
        }
    }

    /// <summary>创建唯一临时目录。</summary>
    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mma-storage-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>递归删除临时目录；失败不抛异常，避免掩盖断言结果。</summary>
    private static void DeleteTempDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // 临时目录清理失败不影响测试结论
        }
    }
}
