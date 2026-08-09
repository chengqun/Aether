using Volo.Abp.Features;

namespace Aether.Server.Domain.Features;

public class AetherFeatureDefinitionProvider : FeatureDefinitionProvider
{
    public override void Define(IFeatureDefinitionContext context)
    {
        var stockAppGroup = context.AddGroup("StockApp");
        stockAppGroup.AddFeature("StockApp.Quote", "false", isVisibleToClients: true);
        stockAppGroup.AddFeature("StockApp.Analysis", "false", isVisibleToClients: true);

        var cloudDocsGroup = context.AddGroup("CloudDocs");
        cloudDocsGroup.AddFeature("CloudDocs.Upload", "false", isVisibleToClients: true);
        cloudDocsGroup.AddFeature("CloudDocs.Query", "false", isVisibleToClients: true);

        var yushuGroup = context.AddGroup("Yushu");
        yushuGroup.AddFeature("Yushu.DataQuery", "false", isVisibleToClients: true);
        yushuGroup.AddFeature("Yushu.SqlQuery", "false", isVisibleToClients: true);

        var chatGroup = context.AddGroup("Chat");
        chatGroup.AddFeature("Chat.Moment", "true", isVisibleToClients: true);
        chatGroup.AddFeature("Chat.Group", "true", isVisibleToClients: true);
        chatGroup.AddFeature("Chat.ServiceAccount", "true", isVisibleToClients: true);

        var marketplaceGroup = context.AddGroup("Marketplace");
        marketplaceGroup.AddFeature("Marketplace.Publish", "true", isVisibleToClients: true);
        marketplaceGroup.AddFeature("Marketplace.Install", "true", isVisibleToClients: true);
    }
}