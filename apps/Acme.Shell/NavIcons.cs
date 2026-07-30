namespace Acme.Shell;

/// <summary>
/// 侧边栏导航图标的矢量 path data（Lucide 图标，MIT；viewBox 24×24，stroke-based）。
/// 用矢量图标替代 emoji，规避跨不同 DPI 显示器时 color font 字形被截断绘制的问题
/// （现象：emoji 右半边被整齐切掉，旁边的文字完好——根因在字形栅格化层，非布局）。
/// 这些字符串绑定到 &lt;Path Data="{Binding Icon}"/&gt;，不经过字体渲染，跨 DPI 一致。
/// </summary>
public static class NavIcons
{
    /// <summary>新聊天（message-square-plus）</summary>
    public const string Chat =
        "M22 17a2 2 0 0 1-2 2H6.828a2 2 0 0 0-1.414.586l-2.202 2.202A.71.71 0 0 1 2 21.286V5a2 2 0 0 1 2-2h16a2 2 0 0 1 2 2z M12 8v6 M9 11h6";

    /// <summary>专家（flask-conical）</summary>
    public const string Experts =
        "M14 2v6a2 2 0 0 0 .245.96l5.51 10.08A2 2 0 0 1 18 22H6a2 2 0 0 1-1.755-2.96l5.51-10.08A2 2 0 0 0 10 8V2 M6.453 15h11.094 M8.5 2h7";

    /// <summary>自动化（clock；circle 转 arc 与指针合并为单段 Geometry）</summary>
    public const string Automation =
        "M12 2 A10 10 0 1 0 12 22 A10 10 0 1 0 12 2 Z M12 6v6l4 2";

    /// <summary>笔记（notebook-pen）</summary>
    public const string Notes =
        "M13.4 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-7.4 M2 6h4 M2 10h4 M2 14h4 M2 18h4 M21.378 5.626a1 1 0 1 0-3.004-3.004l-5.01 5.012a2 2 0 0 0-.506.854l-.837 2.87a.5.5 0 0 0 .62.62l2.87-.837a2 2 0 0 0 .854-.506z";

    /// <summary>期货（trending-up）</summary>
    public const string Futures =
        "M16 7h6v6 m22 7-8.5 8.5-5-5L2 17";

    /// <summary>回测（chart-column）</summary>
    public const string Backtest =
        "M3 3v16a2 2 0 0 0 2 2h16 M18 17V9 M13 17V5 M8 17v-3";

    /// <summary>侧边栏展开/收起（menu）</summary>
    public const string Menu =
        "M4 12h16 M4 6h16 M4 18h16";

    /// <summary>设备连接配置（settings 齿轮）</summary>
    public const string Settings =
        "M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2z M12 9 A3 3 0 1 0 12 15 A3 3 0 1 0 12 9 Z";

    /// <summary>工作流（三节点流转）</summary>
    public const string Workflow =
        "M5 5a2 2 0 1 0 4 0a2 2 0 1 0-4 0 M5 19a2 2 0 1 0 4 0a2 2 0 1 0-4 0 M17 19a2 2 0 1 0 4 0a2 2 0 1 0-4 0 M7 6v12 M7 7h6a4 4 0 0 1 4 4v8";

    // —— 笔记节点可选图标（Lucide，viewBox 24×24，stroke-based）——

    /// <summary>文件文本（笔记）</summary>
    public const string FileText =
        "M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7z M14 2v4a2 2 0 0 0 2 2h4 M16 13H8 M16 17H8 M10 9H8";

    /// <summary>机器人（Agent）</summary>
    public const string Bot =
        "M12 8V4H8 M4 12a8 8 0 0 1 16 0v4a4 4 0 0 1-4 4H8a4 4 0 0 1-4-4z M2 14h2 M20 14h2 M15 13v2 M9 13v2";

    /// <summary>便签</summary>
    public const string StickyNote =
        "M15.5 3H5a2 2 0 0 0-2 2v14c0 1.1.9 2 2 2h14a2 2 0 0 0 2-2V8.5L15.5 3z M15 3v6h6";

    /// <summary>星标</summary>
    public const string Star =
        "M12 2l3.09 6.26L22 9.27l-5 4.87 1.18 6.88L12 17.77l-6.18 3.25L7 14.14 2 9.27l6.91-1.01L12 2z";

    /// <summary>书签</summary>
    public const string Bookmark =
        "M19 21l-7-5-7 5V5a2 2 0 0 1 2-2h10a2 2 0 0 1 2 2z";

    /// <summary>代码</summary>
    public const string Code =
        "M16 18l6-6-6-6 M8 6l-6 6 6 6";

    /// <summary>任务（勾选方框）</summary>
    public const string CheckSquare =
        "M9 11l3 3L22 4 M21 12v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11";

    /// <summary>灯泡</summary>
    public const string Lightbulb =
        "M15 14c.2-1 .7-1.7 1.5-2.5 1-.9 1.5-2.2 1.5-3.5A6 6 0 0 0 6 8c0 1 .2 2.2 1.5 3.5.8.8 1.3 1.5 1.5 2.5 M9 18h6 M10 22h4";

    /// <summary>文件夹</summary>
    public const string Folder =
        "M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2z";

    /// <summary>目录树</summary>
    public const string FolderTree =
        "M20 10a1 1 0 0 0 1-1V6a1 1 0 0 0-1-1h-2.5a1 1 0 0 1-.8-.4l-.9-1.2A1 1 0 0 0 15 3h-2a1 1 0 0 0-1 1v5a1 1 0 0 0 1 1z M20 21a1 1 0 0 0 1-1v-3a1 1 0 0 0-1-1h-2.9a1 1 0 0 1-.88-.55l-.42-.85a1 1 0 0 0-.92-.6H13a1 1 0 0 0-1 1v5a1 1 0 0 0 1 1z M3 5a2 2 0 0 0 2 2h3 M3 3v13a2 2 0 0 0 2 2h3";

    /// <summary>链接</summary>
    public const string Link =
        "M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71 M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71";

    /// <summary>心</summary>
    public const string Heart =
        "M19 14c1.49-1.46 3-3.21 3-5.5A5.5 5.5 0 0 0 16.5 3c-1.76 0-3 .5-4.5 2-1.5-1.5-2.74-2-4.5-2A5.5 5.5 0 0 0 2 8.5c0 2.3 1.5 4.05 3 5.5l7 7z";

    // —— 笔记页工具栏动作图标（Lucide）——

    /// <summary>保存</summary>
    public const string Save =
        "M19 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11l5 5v11a2 2 0 0 1-2 2z M17 21v-8H7v8 M7 3v5h8";

    /// <summary>编辑（铅笔）</summary>
    public const string Pencil =
        "M12 20h9 M16.5 3.5a2.121 2.121 0 0 1 3 3L7 19l-4 1 1-4z";

    /// <summary>预览（眼睛）</summary>
    public const string Eye =
        "M2 12s3-7 10-7 10 7 10 7-3 7-10 7-10-7-10-7z M12 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6";

    /// <summary>关闭预览（划线眼睛）</summary>
    public const string EyeOff =
        "M9.88 9.88a3 3 0 1 0 4.24 4.24 M10.73 5.08A10.43 10.43 0 0 1 12 5c7 0 10 7 10 7a13.16 13.16 0 0 1-1.67 2.68 M6.61 6.61A13.526 13.526 0 0 0 2 12s3 7 10 7a9.74 9.74 0 0 0 5.39-1.61 M2 2l20 20";

    // —— 风控/数据 场景图标（Lucide）——

    /// <summary>查找（放大镜）</summary>
    public const string Search =
        "M11 19a8 8 0 1 0 0-16 8 8 0 0 0 0 16z M21 21l-4.35-4.35";

    /// <summary>变量/CPU</summary>
    public const string Cpu =
        "M4 4h16v16H4z M9 9h6v6H9z M9 1v3 M15 1v3 M9 20v3 M15 20v3 M20 9h3 M20 14h3 M1 9h3 M1 14h3";

    /// <summary>数据库</summary>
    public const string Database =
        "M3 5V19A9 3 0 0 0 21 19V5 M3 12A9 3 0 0 0 21 12";

    /// <summary>过滤（漏斗）</summary>
    public const string Filter =
        "M22 3H2l8 9.46V19l4 2v-8.54z";

    /// <summary>追踪（靶心）</summary>
    public const string Target =
        "M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20z M12 18a6 6 0 1 0 0-12 6 6 0 0 0 0 12z M12 14a2 2 0 1 0 0-4 2 2 0 0 0 0 4z";

    /// <summary>定位（十字准星）</summary>
    public const string Crosshair =
        "M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20z M22 12h-4 M6 12H2 M12 6V2 M12 22v-4";

    /// <summary>路径</summary>
    public const string Route =
        "M9 19h8.5a3.5 3.5 0 0 0 0-7h-11a3.5 3.5 0 0 1 0-7H15";

    /// <summary>监控（心电波）</summary>
    public const string Activity =
        "M22 12h-2.48a2 2 0 0 0-1.93 1.46l-2.35 8.36a.25.25 0 0 1-.48 0L9.24 2.18a.25.25 0 0 0-.48 0l-2.35 8.36A2 2 0 0 1 4.49 12H2";

    /// <summary>验证（盾牌勾）</summary>
    public const string ShieldCheck =
        "M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z M9 12l2 2 4-4";

    /// <summary>风险（盾牌感叹）</summary>
    public const string ShieldAlert =
        "M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z M12 8v4 M12 16h.01";

    /// <summary>预警（三角感叹）</summary>
    public const string AlertTriangle =
        "M10.29 3.86L1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z M12 9v4 M12 17h.01";

    /// <summary>评估（仪表盘）</summary>
    public const string Gauge =
        "M12 14l4-4 M3.34 19a10 10 0 1 1 17.32 0";

    /// <summary>柱状图</summary>
    public const string BarChart =
        "M3 3v16a2 2 0 0 0 2 2h16 M18 17V9 M13 17V5 M8 17v-3";

    /// <summary>饼图</summary>
    public const string PieChart =
        "M21.21 15.89A10 10 0 1 1 8 2.83 M21 12c.552 0 1.005-.449.95-.998a10 10 0 0 0-8.953-8.951c-.55-.055-.998.398-.998.95v8a1 1 0 0 0 1 1z";

    /// <summary>折线图</summary>
    public const string LineChart =
        "M3 3v16a2 2 0 0 0 2 2h16 M19 9l-5 5-4-4-3 3";

    /// <summary>提醒（铃铛）</summary>
    public const string Bell =
        "M6 8a6 6 0 0 1 12 0c0 7 3 9 3 9H3s3-2 3-9 M10.3 21a1.94 1.94 0 0 0 3.4 0";

    /// <summary>标记（旗帜）</summary>
    public const string Flag =
        "M4 15s1-1 4-1 5 2 8 2 4-1 4-1V3s-1 1-4 1-5-2-8-2-4 1-4 1z M4 22V15";

    // —— 通用图标（Lucide）——

    /// <summary>大纲/层级列表</summary>
    public const string ListTree =
        "M8 5h13 M13 12h8 M13 19h8 M3 10a2 2 0 0 0 2 2h3 M3 5v12a2 2 0 0 0 2 2h3";

    /// <summary>打开的书</summary>
    public const string BookOpen =
        "M12 5v16 M20 19a2 2 0 0 0 2-2V5a2 2 0 0 0-2-2l-6 .002A5 5 0 0 0 12 5a5 5 0 0 0-4-2H4a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h4a5 5 0 0 1 4 2 5 5 0 0 1 4-2z";

    /// <summary>文件夹组</summary>
    public const string Folders =
        "M20 5a2 2 0 0 1 2 2v7a2 2 0 0 1-2 2H9a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h2.5a1.5 1.5 0 0 1 1.2.6l.6.8a1.5 1.5 0 0 0 1.2.6z M3 8.268a2 2 0 0 0-1 1.738V19a2 2 0 0 0 2 2h11a2 2 0 0 0 1.732-1";

    /// <summary>归档</summary>
    public const string Archive =
        "M2 6h20 M4 8v11a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8 M10 12h4";

    /// <summary>日历</summary>
    public const string Calendar =
        "M3 4a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2v16a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z M8 2v4 M16 2v4 M3 10h18";

    /// <summary>时钟</summary>
    public const string Clock =
        "M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20z M12 6v6l4 2";

    /// <summary>密钥</summary>
    public const string Key =
        "M15.5 7.5l2.3 2.3a1 1 0 0 0 1.4 0l2.1-2.1a1 1 0 0 0 0-1.4L19 4 M21 2l-9.6 9.6";

    /// <summary>锁定</summary>
    public const string Lock =
        "M5 11h14a2 2 0 0 1 2 2v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-7a2 2 0 0 1 2-2z M8 11V7a4 4 0 0 1 8 0v4";

    /// <summary>用户</summary>
    public const string User =
        "M19 21v-2a4 4 0 0 0-4-4H9a4 4 0 0 0-4 4v2 M8 7a4 4 0 1 0 8 0 4 4 0 1 0-8 0";

    /// <summary>用户组</summary>
    public const string Users =
        "M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2 M16 3.128a4 4 0 0 1 0 7.744 M22 21v-2a4 4 0 0 0-3-3.87";

    /// <summary>邮件</summary>
    public const string Mail =
        "M2 5a2 2 0 0 1 2-2h16a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2z m22 7-8.991 5.727a2 2 0 0 1-2.009 0L2 7";

    /// <summary>全球</summary>
    public const string Globe =
        "M12 2a14.5 14.5 0 0 0 0 20 14.5 14.5 0 0 0 0-20 M2 12h20";

    /// <summary>云</summary>
    public const string Cloud =
        "M17.5 19H9a7 7 0 1 1 6.71-9h1.79a4.5 4.5 0 1 1 0 9z";

    /// <summary>终端</summary>
    public const string Terminal =
        "M12 19h8 M4 17l6-6-6-6";

    /// <summary>文件</summary>
    public const string File =
        "M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z M14 2v6h6";

    /// <summary>删除（垃圾桶）</summary>
    public const string Trash =
        "M3 6h18 M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6 M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2 M10 11v6 M14 11v6";

    /// <summary>下载</summary>
    public const string Download =
        "M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4 M7 10l5 5 5-5 M12 15V3";

    /// <summary>上传</summary>
    public const string Upload =
        "M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4 M17 8l-5-5-5 5 M12 3v12";

    /// <summary>刷新</summary>
    public const string RefreshCw =
        "M21 12a9 9 0 1 1-9-9c2.52 0 4.93 1 6.74 2.74L21 8 M21 3v5h-5";

    /// <summary>运行（播放）</summary>
    public const string Play =
        "M5 5a2 2 0 0 1 3.008-1.728l11.997 6.998a2 2 0 0 1 .003 3.458l-12 7A2 2 0 0 1 5 19z";

    /// <summary>附件（回形针）</summary>
    public const string Paperclip =
        "M16 6l-8.414 8.586a2 2 0 0 0 2.829 2.829l8.414-8.586a4 4 0 1 0-5.657-5.657l-8.379 8.551a6 6 0 1 0 8.485 8.485l8.379-8.551";

    /// <summary>勾选</summary>
    public const string Check =
        "M20 6L9 17l-5-5";

    /// <summary>关闭（叉）</summary>
    public const string Close =
        "M18 6L6 18 M6 6l12 12";

    /// <summary>新增（加号）</summary>
    public const string Plus =
        "M12 5v14 M5 12h14";
}
