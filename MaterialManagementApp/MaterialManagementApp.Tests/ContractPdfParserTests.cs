using System;
using System.Linq;
using MaterialManagementApp.Services;
using Xunit;

namespace MaterialManagementApp.Tests;

/// <summary>
/// 合同 PDF 解析器的 JSON 容错解析单元测试：
/// 覆盖 ```json 围栏剥离、price 字符串/数字兼容、缺失字段容错、
/// 无效材料行过滤与日期归一化。纯文本解析，不触网、不渲染 PDF。
/// </summary>
public sealed class ContractPdfParserTests
{
    [Fact]
    public void Parse_Plain_Json_Returns_Fields_And_Materials()
    {
        const string raw = """
            {"supplier_name":"某某建材有限公司","contract_no":"HT-2026-001","contact_person":"张三","phone":"13800000000","sign_date":"2026-03-01","materials":[{"name":"中砂","spec":"含泥量<3%","unit":"方","price":128.5}]}
            """;

        var result = ContractPdfParser.ParseAiJson(raw);

        Assert.Equal("某某建材有限公司", result.SupplierName);
        Assert.Equal("HT-2026-001", result.ContractNo);
        Assert.Equal("张三", result.ContactPerson);
        Assert.Equal("13800000000", result.Phone);
        Assert.Equal("2026-03-01", result.SignDate);
        var material = Assert.Single(result.Materials);
        Assert.Equal("中砂", material.Name);
        Assert.Equal("含泥量<3%", material.Spec);
        Assert.Equal("方", material.Unit);
        Assert.Equal(128.5, material.Price);
    }

    [Fact]
    public void Parse_Markdown_Fenced_Json_Strips_Fences_And_Noise()
    {
        // 模型常见输出：带 ```json 围栏、前后夹杂解释文字
        const string raw = """
            好的，以下是解析结果：
            ```json
            {"supplier_name":"甲供材贸易部","contract_no":"","contact_person":"","phone":"","sign_date":"","materials":[]}
            ```
            希望对你有帮助。
            """;

        var result = ContractPdfParser.ParseAiJson(raw);

        Assert.Equal("甲供材贸易部", result.SupplierName);
        Assert.Equal(string.Empty, result.ContractNo);
        Assert.Empty(result.Materials);
    }

    [Fact]
    public void Parse_Price_As_String_Is_Converted_To_Number()
    {
        const string raw = """
            {"supplier_name":"","contract_no":"","contact_person":"","phone":"","sign_date":"","materials":[{"name":"水泥","spec":"P.O 42.5","unit":"吨","price":"368"}]}
            """;

        var result = ContractPdfParser.ParseAiJson(raw);

        var material = Assert.Single(result.Materials);
        Assert.Equal(368.0, material.Price);
    }

    [Fact]
    public void Parse_Price_Null_Is_Treated_As_Zero()
    {
        const string raw = """
            {"supplier_name":"","contract_no":"","contact_person":"","phone":"","sign_date":"","materials":[{"name":"碎石","spec":"","unit":"","price":null}]}
            """;

        var result = ContractPdfParser.ParseAiJson(raw);

        var material = Assert.Single(result.Materials);
        Assert.Equal(0.0, material.Price);
    }

    [Fact]
    public void Parse_Blank_Material_Rows_Are_Filtered_Out()
    {
        // 名称与单价均为空的行属于模型噪声，导入后会被空白行防呆删除，解析阶段直接过滤
        const string raw = """
            {"supplier_name":"","contract_no":"","contact_person":"","phone":"","sign_date":"","materials":[{"name":"","spec":"","unit":"","price":0},{"name":"中砂","spec":"","unit":"","price":0},{"name":"","spec":"20-40","unit":"","price":95.5}]}
            """;

        var result = ContractPdfParser.ParseAiJson(raw);

        Assert.Equal(2, result.Materials.Count);
        Assert.Equal("中砂", result.Materials[0].Name);
        // 名称空但单价有效：保留，交由用户在表格中补全
        Assert.Equal(string.Empty, result.Materials[1].Name);
        Assert.Equal(95.5, result.Materials[1].Price);
    }

    [Fact]
    public void Parse_SignDate_Is_Normalized_To_Iso_Format()
    {
        const string raw = """
            {"supplier_name":"","contract_no":"","contact_person":"","phone":"","sign_date":"2026/1/5","materials":[]}
            """;

        var result = ContractPdfParser.ParseAiJson(raw);

        Assert.Equal("2026-01-05", result.SignDate);
    }

    [Fact]
    public void Parse_Unparsable_SignDate_Becomes_Empty()
    {
        const string raw = """
            {"supplier_name":"","contract_no":"","contact_person":"","phone":"","sign_date":"待定","materials":[]}
            """;

        var result = ContractPdfParser.ParseAiJson(raw);

        Assert.Equal(string.Empty, result.SignDate);
    }

    [Fact]
    public void Parse_Non_Json_Text_Throws_Friendly_Error()
    {
        Assert.Throws<InvalidOperationException>(() => ContractPdfParser.ParseAiJson("抱歉，我无法识别这张图片。"));
    }

    [Fact]
    public void Parse_Empty_Text_Throws_Friendly_Error()
    {
        Assert.Throws<InvalidOperationException>(() => ContractPdfParser.ParseAiJson(string.Empty));
    }

    [Fact]
    public void Parse_Malformed_Json_Throws_Friendly_Error()
    {
        // JSON 结构被截断（引号未闭合），反序列化必须失败而不是崩溃
        Assert.Throws<InvalidOperationException>(() =>
            ContractPdfParser.ParseAiJson("{\"supplier_name\":\"某某公司\", \"materials\":[{\"name\":\"中砂\"}"));
    }
}
