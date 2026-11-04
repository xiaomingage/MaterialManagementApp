using System;
using System.Collections;
using System.Reflection;

namespace MaterialManagementApp.Common;

/// <summary>
/// 台账表格「空白行防呆」校验器：判断一行数据是否属于用户没有填入任何有效内容的空白行。
///
/// 判定规则（与需求一致）：
/// - 自动填充字段不参与判定：主键/外键（Id、以 Id/ID 结尾）、日期时间、布尔标记
///   （IsDeleted/IsNewRow/IsChecked 等）一律跳过；
/// - 字符串字段：null、""、纯空白 视为未填写；
/// - 数值字段：等于默认值 0 视为未填写；
/// - 其余引用类型字段（图片路径对象等）不参与判定；
/// - 全部「用户可填字段」都未填写，且至少存在一个用户可填字段，才判定为空白行。
/// </summary>
public static class BlankRowGuard
{
    /// <summary>判断行对象是否为空白行。</summary>
    /// <param name="row">行数据对象。</param>
    /// <returns>true 表示该行所有用户可填字段均为空，应当删除。</returns>
    public static bool IsRowBlank(object? row)
    {
        if (row is null)
        {
            return false;
        }

        var type = row.GetType();
        // 匿名类型/系统类型不处理，只对业务模型生效
        if (type.Namespace is null || type.Namespace.StartsWith("System", StringComparison.Ordinal))
        {
            return false;
        }

        var hasUserField = false;
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || prop.GetIndexParameters().Length > 0 || IsAutoField(prop))
            {
                continue;
            }

            var propertyType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            object? value;
            try
            {
                value = prop.GetValue(row);
            }
            catch
            {
                // 个别属性 getter 抛异常时不让校验本身崩溃，按未填写处理
                continue;
            }

            hasUserField = true;

            if (propertyType == typeof(string))
            {
                if (!string.IsNullOrWhiteSpace(value as string))
                {
                    return false;
                }
            }
            else if (IsNumeric(propertyType))
            {
                if (!Equals(value, Convert.ChangeType(0, propertyType)))
                {
                    return false;
                }
            }
            else
            {
                // 其它类型字段视为系统自动维护，不参与判定
                hasUserField = hasUserField && true;
            }
        }

        return hasUserField;
    }

    /// <summary>从绑定集合中移除行（集合不是 IList 时返回 false）。</summary>
    public static bool TryRemove(IList? source, object? row)
    {
        if (source is not null && row is not null && source.Contains(row))
        {
            source.Remove(row);
            return true;
        }
        return false;
    }

    /// <summary>主键/标记/日期等自动填充字段不参与空白判定。</summary>
    private static bool IsAutoField(PropertyInfo prop)
    {
        var name = prop.Name;
        if (name is "Id" or "IsDeleted" or "IsNewRow" or "IsChecked" or "SupplierId" or "Selected")
        {
            return true;
        }
        if (name.EndsWith("Id", StringComparison.Ordinal) || name.EndsWith("ID", StringComparison.Ordinal))
        {
            return true;
        }
        if (name.Contains("Date", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Time", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return Nullable.GetUnderlyingType(prop.PropertyType) is null &&
               (prop.PropertyType == typeof(bool) ||
                prop.PropertyType == typeof(DateTime) ||
                prop.PropertyType == typeof(DateTimeOffset));
    }

    /// <summary>判断类型是否为算术数值类型。</summary>
    private static bool IsNumeric(Type type) =>
        type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) ||
        type == typeof(double) || type == typeof(float) || type == typeof(decimal) ||
        type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) || type == typeof(sbyte);
}
