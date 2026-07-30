#ifndef CTP_API_H
#define CTP_API_H

#include <stdbool.h>

/* 跨平台导出宏：
 * - Windows DLL：必须显式 __declspec(dllexport)，否则 extern "C" 符号不导出
 *   （C# P/Invoke 找不到入口点 "Unable to find entry point ..."）。
 * - macOS/Linux：符号默认导出，宏为空即可。 */
#undef CTP_API
#if defined(_WIN32) || defined(__CYGWIN__)
  #if defined(CTP_NATIVE_EXPORTS)
    #define CTP_API __declspec(dllexport)
  #else
    #define CTP_API __declspec(dllimport)
  #endif
#else
  #define CTP_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* 不透明 handle（指针） */
typedef struct CtpTrader* CtpTraderHandle;

/* CTP 结构体前向声明（完整定义由 CTP 头文件提供；C# 侧自行镜像布局） */
struct CThostFtdcRspAuthenticateField;
struct CThostFtdcRspUserLoginField;
struct CThostFtdcRspInfoField;

/* —— wrapper 定义的稳定 POD（wrapper 从 CTP 结构体提取关键字段填充；C# 镜像这些小结构） —— */

/* 资金账户（来自 CThostFtdcTradingAccountField） */
typedef struct CtpAccountInfo {
    char BrokerID[11];
    char AccountID[13];
    double Balance;
    double Available;
    double WithdrawQuota;
    double FrozenMargin;
    double FrozenCash;
    double FrozenCommission;
    double Commission;
    double CurrMargin;
    double PositionProfit;
    double CloseProfit;
    char TradingDay[9];
} CtpAccountInfo;

/* 持仓（来自 CThostFtdcInvestorPositionField） */
typedef struct CtpPositionInfo {
    char InstrumentID[81];
    char ExchangeID[9];
    char PosiDirection;   // '1' Long / '2' Short
    char HedgeFlag;       // '1' Speculation / '2' Arbitrage / '3' Hedge
    char PositionDate;    // '1' Today / '2' History
    int   YdPosition;
    int   Position;
    int   LongFrozen;
    int   ShortFrozen;
    int   TodayPosition;
    double PositionCost;
    double UseMargin;
    double PositionProfit;
    double CloseProfit;
} CtpPositionInfo;

/* 报单（来自 CThostFtdcOrderField） */
typedef struct CtpOrderInfo {
    char InstrumentID[81];
    char ExchangeID[9];
    char OrderRef[13];
    int  FrontID;
    int  SessionID;
    char OrderSysID[21];
    char Direction;        // '0' Buy / '1' Sell
    char OffsetFlag;       // CombOffsetFlag[0]
    char OrderPriceType;   // '2' Limit / '0' Market
    double LimitPrice;
    int  VolumeTotalOriginal;
    int  VolumeTraded;
    int  VolumeTotal;
    char OrderStatus;      // see CTP THOST_FTDC_OST_*
    char InsertTime[9];
    char UpdateTime[9];
    char StatusMsg[81];
} CtpOrderInfo;

/* 成交（来自 CThostFtdcTradeField） */
typedef struct CtpTradeInfo {
    char InstrumentID[81];
    char OrderRef[13];
    char TradeID[21];
    char OrderSysID[21];
    char Direction;
    char OffsetFlag;
    char HedgeFlag;
    double Price;
    int  Volume;
    char TradeTime[9];
    char TradingDay[9];
} CtpTradeInfo;

/* 下单请求输入（wrapper 据此构建 CThostFtdcInputOrderField） */
typedef struct CtpOrderInsertRequest {
    char BrokerID[11];
    char InvestorID[13];
    char InstrumentID[81];
    char OrderRef[13];
    char Direction;       // '0' Buy / '1' Sell
    char OffsetFlag;      // '0' Open / '1' Close / '3' CloseToday
    char OrderPriceType;  // '2' Limit / '0' Market
    double LimitPrice;
    int  VolumeTotalOriginal;
    char TimeCondition;   // '3' GFD / '1' IOC / '2' FOK
} CtpOrderInsertRequest;

/* 回调约定：由 CTP 内部线程触发，wrapper 原样转发。
 * C# 侧收到后须立即拷贝结构体并快速返回，不得阻塞或在此线程改 UI。
 * 结构体指针仅在回调期间有效。 */
typedef struct CtpTraderCallbacks {
    void (*OnFrontConnected)(void* ctx);
    void (*OnFrontDisconnected)(void* ctx, int reason);
    void (*OnRspError)(void* ctx,
        const struct CThostFtdcRspInfoField* info, int requestId, bool isLast);
    void (*OnRspAuthenticate)(void* ctx,
        const struct CThostFtdcRspAuthenticateField* field,
        const struct CThostFtdcRspInfoField* info, int requestId, bool isLast);
    void (*OnRspUserLogin)(void* ctx,
        const struct CThostFtdcRspUserLoginField* field,
        const struct CThostFtdcRspInfoField* info, int requestId, bool isLast);
    void (*OnRspQryTradingAccount)(void* ctx,
        const CtpAccountInfo* account,
        const struct CThostFtdcRspInfoField* info, int requestId, bool isLast);
    void (*OnRspQryInvestorPosition)(void* ctx,
        const CtpPositionInfo* position,
        const struct CThostFtdcRspInfoField* info, int requestId, bool isLast);
    void (*OnRspQryOrder)(void* ctx,
        const CtpOrderInfo* order,
        const struct CThostFtdcRspInfoField* info, int requestId, bool isLast);
    void (*OnRspOrderInsert)(void* ctx,
        const struct CThostFtdcRspInfoField* info, int requestId, bool isLast);
    void (*OnErrRtnOrderInsert)(void* ctx,
        const struct CThostFtdcRspInfoField* info);
    void (*OnRspOrderAction)(void* ctx,
        const struct CThostFtdcRspInfoField* info, int requestId, bool isLast);
    void (*OnRtnOrder)(void* ctx, const CtpOrderInfo* order);
    void (*OnRtnTrade)(void* ctx, const CtpTradeInfo* trade);
    void (*OnRspSettlementInfoConfirm)(void* ctx,
        const struct CThostFtdcRspInfoField* info, int requestId, bool isLast);
} CtpTraderCallbacks;

/* 生命周期 */
CTP_API CtpTraderHandle CtpTrader_Create(const char* flowPath,
    const CtpTraderCallbacks* callbacks, void* ctx);
CTP_API void CtpTrader_Release(CtpTraderHandle h);
CTP_API int  CtpTrader_Join(CtpTraderHandle h);

/* 连接 */
CTP_API int CtpTrader_RegisterFront(CtpTraderHandle h, const char* front);
CTP_API int CtpTrader_SubscribePrivateTopic(CtpTraderHandle h, int resumeType);
CTP_API int CtpTrader_SubscribePublicTopic(CtpTraderHandle h, int resumeType);
CTP_API int CtpTrader_Init(CtpTraderHandle h);

/* 认证（Win/Linux/Mac 一致，不需采集信息） */
CTP_API int CtpTrader_Authenticate(CtpTraderHandle h,
    const char* brokerId, const char* userId,
    const char* appId, const char* authCode, int requestId);

/* 登录（Mac 内部 4 参 + 采集信息；Win/Linux 2 参。C# 无感） */
CTP_API int CtpTrader_Login(CtpTraderHandle h,
    const char* brokerId, const char* userId, const char* password,
    const char* userAgent, int requestId);

/* Mac 采集信息接入点（生产穿透式用；SimNow 仿真可不调） */
CTP_API int CtpTrader_SetCollectionInfo(CtpTraderHandle h, const char* buf, int len);

/* ===================== 查询 / 下单 / 撤单 ===================== */
/* 查询资金账户 */
CTP_API int CtpTrader_QryTradingAccount(CtpTraderHandle h,
    const char* brokerId, const char* accountId, int requestId);
/* 查询投资者持仓（instrumentId 传空串查全部） */
CTP_API int CtpTrader_QryInvestorPosition(CtpTraderHandle h,
    const char* brokerId, const char* investorId, const char* instrumentId, int requestId);
/* 下单 */
CTP_API int CtpTrader_OrderInsert(CtpTraderHandle h,
    const CtpOrderInsertRequest* req, int requestId);
/* 撤单（按 FrontID/SessionID/OrderRef 定位） */
CTP_API int CtpTrader_OrderAction(CtpTraderHandle h,
    const char* brokerId, const char* investorId,
    int frontId, int sessionId, const char* orderRef, const char* exchangeId, int requestId);

CTP_API int CtpTrader_QryOrder(CtpTraderHandle h,
    const char* brokerId, const char* investorId,
    const char* instrumentId, int requestId);
/* 确认结算结果（每个交易日下单前必须先调） */
CTP_API int CtpTrader_SettlementInfoConfirm(CtpTraderHandle h,
    const char* brokerId, const char* investorId, int requestId);

/* ===================== 行情接口（Md） ===================== */
typedef struct CtpMd* CtpMdHandle;

struct CThostFtdcSpecificInstrumentField;
struct CThostFtdcDepthMarketDataField;

typedef struct CtpMdCallbacks {
    void (*OnFrontConnected)(void* ctx);
    void (*OnFrontDisconnected)(void* ctx, int reason);
    void (*OnRspError)(void* ctx,
        const struct CThostFtdcRspInfoField* info, int requestId, bool isLast);
    void (*OnRspUserLogin)(void* ctx,
        const struct CThostFtdcRspUserLoginField* field,
        const struct CThostFtdcRspInfoField* info, int requestId, bool isLast);
    void (*OnRspSubMarketData)(void* ctx,
        const struct CThostFtdcSpecificInstrumentField* field,
        const struct CThostFtdcRspInfoField* info, int requestId, bool isLast);
    void (*OnRspUnSubMarketData)(void* ctx,
        const struct CThostFtdcSpecificInstrumentField* field,
        const struct CThostFtdcRspInfoField* info, int requestId, bool isLast);
    void (*OnRtnDepthMarketData)(void* ctx,
        const struct CThostFtdcDepthMarketDataField* field);
} CtpMdCallbacks;

CTP_API CtpMdHandle CtpMd_Create(const char* flowPath,
    const CtpMdCallbacks* callbacks, void* ctx);
CTP_API void CtpMd_Release(CtpMdHandle h);
CTP_API int  CtpMd_Join(CtpMdHandle h);

CTP_API int CtpMd_RegisterFront(CtpMdHandle h, const char* front);
CTP_API int CtpMd_Init(CtpMdHandle h);
/* Md 登录为 2 参（行情登录不需要穿透式采集信息） */
CTP_API int CtpMd_Login(CtpMdHandle h,
    const char* brokerId, const char* userId, const char* password, int requestId);
CTP_API int CtpMd_SubscribeMarketData(CtpMdHandle h, const char* const* symbols, int count);
CTP_API int CtpMd_UnSubscribeMarketData(CtpMdHandle h, const char* const* symbols, int count);

#ifdef __cplusplus
}
#endif

#endif /* CTP_API_H */
