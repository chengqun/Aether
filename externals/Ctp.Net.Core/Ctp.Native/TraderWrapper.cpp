// Ctp.Native — CTP 6.7.2 交易接口的 C ABI 薄封装。
// 把 CThostFtdcTraderApi 的 C++ 虚函数接口 + SPI 回调转成 flat C ABI，
// 供 C# 经 P/Invoke 调用。结构体直接按指针传递（6.7.2 三平台布局一致）。
#include "ctp_api.h"
#include "ThostFtdcTraderApi.h"
#ifdef __APPLE__
#include <TargetConditionals.h>
#if !TARGET_OS_IPHONE
#include "DataCollect.h"   // macOS 直连模式穿透式采集库（CTP_GetSystemInfoUnAesEncode，同步）
#endif
#endif

#include <cstring>

#if defined(__APPLE__) && TARGET_OS_IPHONE
// iOS 采集同步封装（定义在 CollectionInfoIOS.mm）
extern "C" int CtpFetchCollectionInfoIOS(char* buf, int bufSize, int* outLen);
#endif

struct CtpTrader {
    CThostFtdcTraderApi* api;
    CThostFtdcTraderSpi* spi;     // 调用方拥有，Release 时释放
    CtpTraderCallbacks cb;
    void* ctx;
    // Mac 直连模式登录所需的采集信息（来自 CTP_GetSystemInfoUnAesEncode）
    char collectionInfo[273];
    int  collectionLen;
};

namespace {

// 定长 char 数组拷贝（保证终止符）
inline void copyStr(char* dst, const char* src, size_t dstSize) {
    if (!dst || dstSize == 0) return;
    if (src) { std::strncpy(dst, src, dstSize - 1); dst[dstSize - 1] = '\0'; }
    else { dst[0] = '\0'; }
}

// SPI 实现：把 CTP 回调原样转发到 C 函数指针。
class TraderSpi : public CThostFtdcTraderSpi {
public:
    explicit TraderSpi(CtpTrader* owner) : owner_(owner) {}

    void OnFrontConnected() override {
        if (owner_->cb.OnFrontConnected) owner_->cb.OnFrontConnected(owner_->ctx);
    }
    void OnFrontDisconnected(int nReason) override {
        if (owner_->cb.OnFrontDisconnected) owner_->cb.OnFrontDisconnected(owner_->ctx, nReason);
    }
    void OnRspError(CThostFtdcRspInfoField* pRspInfo, int nRequestID, bool bIsLast) override {
        if (owner_->cb.OnRspError) owner_->cb.OnRspError(owner_->ctx, pRspInfo, nRequestID, bIsLast);
    }
    void OnRspAuthenticate(CThostFtdcRspAuthenticateField* pField,
                           CThostFtdcRspInfoField* pRspInfo, int nRequestID, bool bIsLast) override {
        if (owner_->cb.OnRspAuthenticate)
            owner_->cb.OnRspAuthenticate(owner_->ctx, pField, pRspInfo, nRequestID, bIsLast);
    }
    void OnRspUserLogin(CThostFtdcRspUserLoginField* pField,
                        CThostFtdcRspInfoField* pRspInfo, int nRequestID, bool bIsLast) override {
        if (owner_->cb.OnRspUserLogin)
            owner_->cb.OnRspUserLogin(owner_->ctx, pField, pRspInfo, nRequestID, bIsLast);
    }
    void OnRspQryTradingAccount(CThostFtdcTradingAccountField* pField,
                                CThostFtdcRspInfoField* pRspInfo, int nRequestID, bool bIsLast) override {
        if (!owner_->cb.OnRspQryTradingAccount) return;
        CtpAccountInfo info{};
        if (pField) {
            copyStr(info.BrokerID, pField->BrokerID, sizeof(info.BrokerID));
            copyStr(info.AccountID, pField->AccountID, sizeof(info.AccountID));
            info.Balance = pField->Balance;
            info.Available = pField->Available;
            info.WithdrawQuota = pField->WithdrawQuota;
            info.FrozenMargin = pField->FrozenMargin;
            info.FrozenCash = pField->FrozenCash;
            info.FrozenCommission = pField->FrozenCommission;
            info.Commission = pField->Commission;
            info.CurrMargin = pField->CurrMargin;
            info.PositionProfit = pField->PositionProfit;
            info.CloseProfit = pField->CloseProfit;
            copyStr(info.TradingDay, pField->TradingDay, sizeof(info.TradingDay));
        }
        owner_->cb.OnRspQryTradingAccount(owner_->ctx, &info, pRspInfo, nRequestID, bIsLast);
    }
    void OnRspQryInvestorPosition(CThostFtdcInvestorPositionField* pField,
                                  CThostFtdcRspInfoField* pRspInfo, int nRequestID, bool bIsLast) override {
        if (!owner_->cb.OnRspQryInvestorPosition) return;
        CtpPositionInfo info{};
        if (pField) {
            copyStr(info.InstrumentID, pField->InstrumentID, sizeof(info.InstrumentID));
            copyStr(info.ExchangeID, pField->ExchangeID, sizeof(info.ExchangeID));
            info.PosiDirection = pField->PosiDirection;
            info.HedgeFlag = pField->HedgeFlag;
            info.PositionDate = pField->PositionDate;
            info.YdPosition = pField->YdPosition;
            info.Position = pField->Position;
            info.LongFrozen = pField->LongFrozen;
            info.ShortFrozen = pField->ShortFrozen;
            info.TodayPosition = pField->TodayPosition;
            info.PositionCost = pField->PositionCost;
            info.UseMargin = pField->UseMargin;
            info.PositionProfit = pField->PositionProfit;
            info.CloseProfit = pField->CloseProfit;
        }
        owner_->cb.OnRspQryInvestorPosition(owner_->ctx, &info, pRspInfo, nRequestID, bIsLast);
    }
    void OnRspQryOrder(CThostFtdcOrderField* pField,
                       CThostFtdcRspInfoField* pRspInfo, int nRequestID, bool bIsLast) override {
        if (!owner_->cb.OnRspQryOrder || !pField) return;
        CtpOrderInfo info{};
        copyStr(info.InstrumentID, pField->InstrumentID, sizeof(info.InstrumentID));
        copyStr(info.ExchangeID, pField->ExchangeID, sizeof(info.ExchangeID));
        copyStr(info.OrderRef, pField->OrderRef, sizeof(info.OrderRef));
        info.FrontID = pField->FrontID;
        info.SessionID = pField->SessionID;
        copyStr(info.OrderSysID, pField->OrderSysID, sizeof(info.OrderSysID));
        info.Direction = pField->Direction;
        info.OffsetFlag = pField->CombOffsetFlag[0];
        info.OrderPriceType = pField->OrderPriceType;
        info.LimitPrice = pField->LimitPrice;
        info.VolumeTotalOriginal = pField->VolumeTotalOriginal;
        info.VolumeTraded = pField->VolumeTraded;
        info.VolumeTotal = pField->VolumeTotal;
        info.OrderStatus = pField->OrderStatus;
        copyStr(info.InsertTime, pField->InsertTime, sizeof(info.InsertTime));
        copyStr(info.UpdateTime, pField->UpdateTime, sizeof(info.UpdateTime));
        copyStr(info.StatusMsg, pField->StatusMsg, sizeof(info.StatusMsg));
        owner_->cb.OnRspQryOrder(owner_->ctx, &info, pRspInfo, nRequestID, bIsLast);
    }
    void OnRspOrderInsert(CThostFtdcInputOrderField* pField,
                          CThostFtdcRspInfoField* pRspInfo, int nRequestID, bool bIsLast) override {
        if (owner_->cb.OnRspOrderInsert)
            owner_->cb.OnRspOrderInsert(owner_->ctx, pRspInfo, nRequestID, bIsLast);
    }
    void OnErrRtnOrderInsert(CThostFtdcInputOrderField* pField,
                             CThostFtdcRspInfoField* pRspInfo) override {
        if (owner_->cb.OnErrRtnOrderInsert)
            owner_->cb.OnErrRtnOrderInsert(owner_->ctx, pRspInfo);
    }
    void OnRspOrderAction(CThostFtdcInputOrderActionField* pField,
                          CThostFtdcRspInfoField* pRspInfo, int nRequestID, bool bIsLast) override {
        if (owner_->cb.OnRspOrderAction)
            owner_->cb.OnRspOrderAction(owner_->ctx, pRspInfo, nRequestID, bIsLast);
    }
    void OnRtnOrder(CThostFtdcOrderField* pField) override {
        if (!owner_->cb.OnRtnOrder || !pField) return;
        CtpOrderInfo info{};
        copyStr(info.InstrumentID, pField->InstrumentID, sizeof(info.InstrumentID));
        copyStr(info.ExchangeID, pField->ExchangeID, sizeof(info.ExchangeID));
        copyStr(info.OrderRef, pField->OrderRef, sizeof(info.OrderRef));
        info.FrontID = pField->FrontID;
        info.SessionID = pField->SessionID;
        copyStr(info.OrderSysID, pField->OrderSysID, sizeof(info.OrderSysID));
        info.Direction = pField->Direction;
        info.OffsetFlag = pField->CombOffsetFlag[0];
        info.OrderPriceType = pField->OrderPriceType;
        info.LimitPrice = pField->LimitPrice;
        info.VolumeTotalOriginal = pField->VolumeTotalOriginal;
        info.VolumeTraded = pField->VolumeTraded;
        info.VolumeTotal = pField->VolumeTotal;
        info.OrderStatus = pField->OrderStatus;
        copyStr(info.InsertTime, pField->InsertTime, sizeof(info.InsertTime));
        copyStr(info.UpdateTime, pField->UpdateTime, sizeof(info.UpdateTime));
        copyStr(info.StatusMsg, pField->StatusMsg, sizeof(info.StatusMsg));
        owner_->cb.OnRtnOrder(owner_->ctx, &info);
    }
    void OnRtnTrade(CThostFtdcTradeField* pField) override {
        if (!owner_->cb.OnRtnTrade || !pField) return;
        CtpTradeInfo info{};
        copyStr(info.InstrumentID, pField->InstrumentID, sizeof(info.InstrumentID));
        copyStr(info.OrderRef, pField->OrderRef, sizeof(info.OrderRef));
        copyStr(info.TradeID, pField->TradeID, sizeof(info.TradeID));
        copyStr(info.OrderSysID, pField->OrderSysID, sizeof(info.OrderSysID));
        info.Direction = pField->Direction;
        info.OffsetFlag = pField->OffsetFlag;
        info.HedgeFlag = pField->HedgeFlag;
        info.Price = pField->Price;
        info.Volume = pField->Volume;
        copyStr(info.TradeTime, pField->TradeTime, sizeof(info.TradeTime));
        copyStr(info.TradingDay, pField->TradingDay, sizeof(info.TradingDay));
        owner_->cb.OnRtnTrade(owner_->ctx, &info);
    }
    void OnRspSettlementInfoConfirm(CThostFtdcSettlementInfoConfirmField* pField,
                                    CThostFtdcRspInfoField* pRspInfo, int nRequestID, bool bIsLast) override {
        if (owner_->cb.OnRspSettlementInfoConfirm)
            owner_->cb.OnRspSettlementInfoConfirm(owner_->ctx, pRspInfo, nRequestID, bIsLast);
    }
private:
    CtpTrader* owner_;
};

} // namespace

extern "C" {

CTP_API CtpTraderHandle CtpTrader_Create(const char* flowPath,
                                 const CtpTraderCallbacks* callbacks, void* ctx) {
    if (!callbacks) return nullptr;
    auto* t = new CtpTrader{};
    t->cb = *callbacks;
    t->ctx = ctx;
    t->collectionLen = 0;
    t->api = CThostFtdcTraderApi::CreateFtdcTraderApi(flowPath ? flowPath : "");
    t->spi = new TraderSpi(t);
    t->api->RegisterSpi(t->spi);
    return t;
}

CTP_API void CtpTrader_Release(CtpTraderHandle h) {
    if (!h) return;
    // 注意：Release 不得在 CTP 回调线程内调用，调用方须先 Join 退出后再 Release。
    if (h->api) h->api->Release();
    delete h->spi;
    delete h;
}

CTP_API int CtpTrader_Join(CtpTraderHandle h) {
    if (!h || !h->api) return -1;
    return h->api->Join();
}

CTP_API int CtpTrader_RegisterFront(CtpTraderHandle h, const char* front) {
    if (!h || !h->api || !front) return -1;
    h->api->RegisterFront(const_cast<char*>(front));
    return 0;
}

CTP_API int CtpTrader_SubscribePrivateTopic(CtpTraderHandle h, int resumeType) {
    if (!h || !h->api) return -1;
    h->api->SubscribePrivateTopic(static_cast<THOST_TE_RESUME_TYPE>(resumeType));
    return 0;
}

CTP_API int CtpTrader_SubscribePublicTopic(CtpTraderHandle h, int resumeType) {
    if (!h || !h->api) return -1;
    h->api->SubscribePublicTopic(static_cast<THOST_TE_RESUME_TYPE>(resumeType));
    return 0;
}

CTP_API int CtpTrader_Init(CtpTraderHandle h) {
    if (!h || !h->api) return -1;
    h->api->Init();
    return 0;
}

CTP_API int CtpTrader_Authenticate(CtpTraderHandle h,
                           const char* brokerId, const char* userId,
                           const char* appId, const char* authCode, int requestId) {
    if (!h || !h->api) return -1;
    CThostFtdcReqAuthenticateField f{};
    if (brokerId) std::strncpy(f.BrokerID, brokerId, sizeof(f.BrokerID) - 1);
    if (userId)   std::strncpy(f.UserID,   userId,   sizeof(f.UserID) - 1);
    if (appId)    std::strncpy(f.AppID,    appId,    sizeof(f.AppID) - 1);
    if (authCode) std::strncpy(f.AuthCode, authCode, sizeof(f.AuthCode) - 1);
    return h->api->ReqAuthenticate(&f, requestId);
}

CTP_API int CtpTrader_Login(CtpTraderHandle h,
                    const char* brokerId, const char* userId, const char* password,
                    const char* userAgent, int requestId) {
    if (!h || !h->api) return -1;
    CThostFtdcReqUserLoginField f{};
    if (brokerId) std::strncpy(f.BrokerID, brokerId, sizeof(f.BrokerID) - 1);
    if (userId)   std::strncpy(f.UserID,   userId,   sizeof(f.UserID) - 1);
    if (password) std::strncpy(f.Password, password, sizeof(f.Password) - 1);
    (void)userAgent;
#ifdef __APPLE__
    // Mac/iOS 直连模式：4 参 ReqUserLogin + 穿透式采集信息。
    // 若调用方经 SetCollectionInfo 手动注入则用注入值（覆盖）。
    if (h->collectionLen > 0) {
        return h->api->ReqUserLogin(&f, requestId, h->collectionLen, h->collectionInfo);
    }
    char info[273] = {0};
    int infoLen = 0;
  #if TARGET_OS_IPHONE
    // iOS：采集为异步 block（需 UIKit/CoreLocation/CoreTelephony），CollectionInfoIOS.mm 同步等待
    CtpFetchCollectionInfoIOS(info, (int)sizeof(info), &infoLen);
  #else
    // macOS：同步采集
    infoLen = (int)sizeof(info);
    (void)CTP_GetSystemInfoUnAesEncode(info, infoLen);
  #endif
    return h->api->ReqUserLogin(&f, requestId, infoLen, info);
#else
    // Win/Linux：2 参版。
    return h->api->ReqUserLogin(&f, requestId);
#endif
}

CTP_API int CtpTrader_SetCollectionInfo(CtpTraderHandle h, const char* buf, int len) {
    if (!h) return -1;
    if (len < 0) len = 0;
    if (len > (int)sizeof(h->collectionInfo)) len = (int)sizeof(h->collectionInfo);
    if (buf && len > 0) std::memcpy(h->collectionInfo, buf, len);
    h->collectionLen = len;
    return 0;
}

CTP_API int CtpTrader_QryTradingAccount(CtpTraderHandle h,
                                const char* brokerId, const char* accountId, int requestId) {
    if (!h || !h->api) return -1;
    CThostFtdcQryTradingAccountField f{};
    if (brokerId)  std::strncpy(f.BrokerID, brokerId, sizeof(f.BrokerID) - 1);
    if (accountId) std::strncpy(f.AccountID, accountId, sizeof(f.AccountID) - 1);
    return h->api->ReqQryTradingAccount(&f, requestId);
}

CTP_API int CtpTrader_QryInvestorPosition(CtpTraderHandle h,
                                  const char* brokerId, const char* investorId,
                                  const char* instrumentId, int requestId) {
    if (!h || !h->api) return -1;
    CThostFtdcQryInvestorPositionField f{};
    if (brokerId)    std::strncpy(f.BrokerID, brokerId, sizeof(f.BrokerID) - 1);
    if (investorId)  std::strncpy(f.InvestorID, investorId, sizeof(f.InvestorID) - 1);
    if (instrumentId && instrumentId[0])
        std::strncpy(f.InstrumentID, instrumentId, sizeof(f.InstrumentID) - 1);
    return h->api->ReqQryInvestorPosition(&f, requestId);
}

CTP_API int CtpTrader_OrderInsert(CtpTraderHandle h, const CtpOrderInsertRequest* req, int requestId) {
    if (!h || !h->api || !req) return -1;
    CThostFtdcInputOrderField f{};
    copyStr(f.BrokerID, req->BrokerID, sizeof(f.BrokerID));
    copyStr(f.InvestorID, req->InvestorID, sizeof(f.InvestorID));
    copyStr(f.InstrumentID, req->InstrumentID, sizeof(f.InstrumentID));
    copyStr(f.OrderRef, req->OrderRef, sizeof(f.OrderRef));
    copyStr(f.UserID, req->InvestorID, sizeof(f.UserID));   // SimNow: UserID == InvestorID
    f.OrderPriceType = req->OrderPriceType;
    f.Direction = req->Direction;
    f.CombOffsetFlag[0] = req->OffsetFlag;   // 组合开平标志取首字符
    f.CombHedgeFlag[0] = '1';                 // 投机
    f.LimitPrice = req->LimitPrice;
    f.VolumeTotalOriginal = req->VolumeTotalOriginal;
    f.TimeCondition = req->TimeCondition;
    f.VolumeCondition = '1';                  // AnyVolume
    f.MinVolume = 1;
    f.ContingentCondition = '1';              // Immediately
    f.ForceCloseReason = '0';                 // NotForceClose
    copyStr(f.ExchangeID, req->ExchangeID, sizeof(f.ExchangeID));   // M2：SimNow 等前置要求非空（镜像 OrderAction L352）
    return h->api->ReqOrderInsert(&f, requestId);
}

CTP_API int CtpTrader_OrderAction(CtpTraderHandle h,
                          const char* brokerId, const char* investorId,
                          int frontId, int sessionId, const char* orderRef,
                          const char* exchangeId, int requestId) {
    if (!h || !h->api) return -1;
    CThostFtdcInputOrderActionField f{};
    if (brokerId)   std::strncpy(f.BrokerID, brokerId, sizeof(f.BrokerID) - 1);
    if (investorId) std::strncpy(f.InvestorID, investorId, sizeof(f.InvestorID) - 1);
    f.FrontID = frontId;
    f.SessionID = sessionId;
    if (orderRef) std::strncpy(f.OrderRef, orderRef, sizeof(f.OrderRef) - 1);
    if (exchangeId) std::strncpy(f.ExchangeID, exchangeId, sizeof(f.ExchangeID) - 1);
    f.ActionFlag = '0';   // Delete（撤单）
    copyStr(f.UserID, investorId ? investorId : "", sizeof(f.UserID));
    return h->api->ReqOrderAction(&f, requestId);
}

CTP_API int CtpTrader_QryOrder(CtpTraderHandle h,
                       const char* brokerId, const char* investorId,
                       const char* instrumentId, int requestId) {
    if (!h || !h->api) return -1;
    CThostFtdcQryOrderField f{};
    if (brokerId)   std::strncpy(f.BrokerID, brokerId, sizeof(f.BrokerID) - 1);
    if (investorId) std::strncpy(f.InvestorID, investorId, sizeof(f.InvestorID) - 1);
    if (instrumentId) std::strncpy(f.InstrumentID, instrumentId, sizeof(f.InstrumentID) - 1);
    return h->api->ReqQryOrder(&f, requestId);
}

CTP_API int CtpTrader_SettlementInfoConfirm(CtpTraderHandle h,
                                    const char* brokerId, const char* investorId, int requestId) {
    if (!h || !h->api) return -1;
    CThostFtdcSettlementInfoConfirmField f{};
    if (brokerId)   std::strncpy(f.BrokerID, brokerId, sizeof(f.BrokerID) - 1);
    if (investorId) std::strncpy(f.InvestorID, investorId, sizeof(f.InvestorID) - 1);
    return h->api->ReqSettlementInfoConfirm(&f, requestId);
}

} // extern "C"