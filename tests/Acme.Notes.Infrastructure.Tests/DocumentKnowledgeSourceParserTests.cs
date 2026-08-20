using Acme.Notes.Domain.Knowledge;
using Acme.Notes.Infrastructure.Knowledge;

namespace Acme.Notes.Infrastructure.Tests;

/// <summary>
/// 文档版（.ok 文件）知识对象解析器测试。核心：文档版 link 的 targetId 是**相对路径字符串**
/// （指向项目内另一个 .ok 文件，如 Hive表/fin_dw.dwd_xxx），而 note 版 KnowledgeLink.TargetId 是 Guid?。
/// note 版解析器把路径字符串塞进 Guid? 会抛异常 → 整个 front-matter 解析失败；文档版独立解析器把
/// targetId 当字符串读、存进 KnowledgeLink.Target，round-trip 稳定。
/// </summary>
public class DocumentKnowledgeSourceParserTests
{
    private readonly DocumentKnowledgeSourceParser _parser = new();

    /// <summary>用户真实 .ok 文件样例：kind=数据源，links 用 targetId 存相对路径（Hive表/文件stem）。</summary>
    private const string UserSample = """
        ---
        kind: 数据源
        links:
        - role: 详解
          targetId: Hive表/hdp_credit.prd_hbase_jr_rds_event_log_encry
        - role: 详解
          targetId: Hive表/fin_dw.dwd_market_vip_sale_process_refund_apply_a
        - role: 关联
          targetId: Hive表/fin_dw.dwd_user_login_behavior_record_pdi
        ---
        # Hive 表：fin_dw.dwd_market_vip_sale_process_refund_apply_a

        会员销售退款申请记录表。
        """;

    [Fact]
    public void Parse_UserSample_KindAndRelativePathLinks()
    {
        var ok = _parser.TryParse(UserSample, out var def, out var error);
        Assert.True(ok, $"解析应成功，错误：{error}");
        Assert.NotNull(def);
        Assert.Equal("数据源", def!.Kind);
        Assert.Equal(3, def.Links.Count);

        Assert.Equal("详解", def.Links[0].Role);
        Assert.Equal("Hive表/hdp_credit.prd_hbase_jr_rds_event_log_encry", def.Links[0].Target);
        Assert.Null(def.Links[0].TargetId); // 文档版：路径存 Target，TargetId(Guid?) 留空

        Assert.Equal("详解", def.Links[1].Role);
        Assert.Equal("Hive表/fin_dw.dwd_market_vip_sale_process_refund_apply_a", def.Links[1].Target);

        Assert.Equal("关联", def.Links[2].Role);
        Assert.Equal("Hive表/fin_dw.dwd_user_login_behavior_record_pdi", def.Links[2].Target);

        Assert.Contains("会员销售退款申请记录表", def.Body);
    }

    [Fact]
    public void Serialize_RoundTrips_TargetIdPathField()
    {
        _parser.TryParse(UserSample, out var def, out _);
        var reserialized = _parser.Serialize(def!);
        // 再解析一次，确认 round-trip 稳定（targetId 字段保留，路径不变）
        var ok = _parser.TryParse(reserialized, out var def2, out var error);
        Assert.True(ok, $"再解析应成功，错误：{error}");
        Assert.Equal(def!.Kind, def2!.Kind);
        Assert.Equal(def.Links.Count, def2.Links.Count);
        for (var i = 0; i < def.Links.Count; i++)
        {
            Assert.Equal(def.Links[i].Role, def2.Links[i].Role);
            Assert.Equal(def.Links[i].Target, def2.Links[i].Target);
        }
        // 序列化产物里 targetId 字段仍在（保持用户既有格式，非 target 字段）
        Assert.Contains("targetId: Hive表/hdp_credit.prd_hbase_jr_rds_event_log_encry", reserialized);
    }

    /// <summary>note 版解析器对文档版（targetId=路径）应解析失败——证明为何须独立文档版解析器。</summary>
    [Fact]
    public void NoteParser_Fails_On_PathTargetId()
    {
        var noteParser = new KnowledgeSourceParser();
        var ok = noteParser.TryParse(UserSample, out var def, out _);
        // 失败或解析不出 links（Guid? 转换路径字符串失败）
        Assert.False(ok && def is not null && def.Links.Count == 3,
            "note 版解析器不应能解析文档版相对路径 targetId——这正是文档版独立解析器存在的原因");
    }

    /// <summary>兼容旧/手写格式：link 用 target 字段（非 targetId）存路径 → 回退到 Target。</summary>
    [Fact]
    public void Parse_LegacyTargetField_FallsBack()
    {
        const string legacy = """
            ---
            kind: 规则
            links:
            - role: uses
              target: Hive表/some_other_table
            ---
            规则正文
            """;
        var ok = _parser.TryParse(legacy, out var def, out var error);
        Assert.True(ok, $"解析应成功，错误：{error}");
        Assert.Equal("Hive表/some_other_table", def!.Links[0].Target);
    }

    [Fact]
    public void Parse_NoFrontMatter_BodyOnly()
    {
        const string plain = "# 纯正文，无 front-matter";
        var ok = _parser.TryParse(plain, out var def, out var error);
        Assert.True(ok, $"解析应成功，错误：{error}");
        Assert.Equal(string.Empty, def!.Kind);
        Assert.Empty(def.Links);
        Assert.Equal(plain, def.Body);
    }

    [Fact]
    public void Parse_Empty_ReturnsFalse()
    {
        var ok = _parser.TryParse("   ", out _, out var error);
        Assert.False(ok);
        Assert.NotEmpty(error);
    }
}
