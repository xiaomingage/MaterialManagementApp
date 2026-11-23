using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace MaterialManagementApp.Common;

/// <summary>
/// 把异常转换成面向用户的「人话 + 下一步」提示。
/// 技术细节仍由调用方写入日志，这里只负责界面文案，不做任何异常处理或吞异常。
/// </summary>
public static class UserErrorText
{
    /// <summary>根据异常类型给出可操作的提示；未知异常给通用下一步。</summary>
    /// <param name="exception">捕获到的异常。</param>
    /// <returns>一句可直接展示给用户的说明。</returns>
    public static string Describe(Exception exception) => exception switch
    {
        SqliteException sqlite when sqlite.SqliteErrorCode is 5 or 6 =>
            "数据文件正被占用，请关闭其他正在使用该文件的程序后重试。",
        SqliteException =>
            "数据库操作未完成，请重启软件后重试；若仍失败请联系技术支持。",
        UnauthorizedAccessException =>
            "没有写入权限，请检查目录权限或更换保存位置后重试。",
        DirectoryNotFoundException =>
            "找不到指定目录，请确认设置中的路径存在后重试。",
        FileNotFoundException =>
            "找不到指定文件，可能已被移动或删除，请重新选择。",
        IOException =>
            "文件读写失败，请确认磁盘空间充足、文件未被其他程序占用后重试。",
        _ => "操作未完成，请稍后重试；若持续失败，请联系技术支持并附上日志。"
    };
}
