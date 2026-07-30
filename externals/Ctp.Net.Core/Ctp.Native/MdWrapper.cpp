// Ctp.Native — CTP 6.7.2 行情接口（Md）的 C ABI 薄封装。
// 镜像 TraderWrapper：把 CThostFtdcMdApi + MdSpi 转成 flat C ABI。
#include "ctp_api.h"
#include "ThostFtdcMdApi.h"

#include <cstring>

struct CtpMd {
    CThostFtdcMdApi* api;
    CThostFtdcMdSpi* spi;
    CtpMdCallbacks cb;
    void* ctx;
};

namespace {

class MdSpi : public CThostFtdcMdSpi {
public:
    explicit MdSpi(CtpMd* owner) : owner_(owner) {}

    void OnFrontConnected() override {
        if (owner_->cb.OnFrontConnected) owner_->cb.OnFrontConnected(owner_->ctx);
    }
    void OnFrontDisconnected(int nReason) override {
        if (owner_->cb.OnFrontDisconnected) owner_->cb.OnFrontDisconnected(owner_->ctx, nReason);
    }
    void OnRspError(CThostFtdcRspInfoField* pRspInfo, int nRequestID, bool bIsLast) override {
        if (owner_->cb.OnRspError) owner_->cb.OnRspError(owner_->ctx, pRspInfo, nRequestID, bIsLast);
    }
    void OnRspUserLogin(CThostFtdcRspUserLoginField* pField,
                        CThostFtdcRspInfoField* pRspInfo, int nRequestID, bool bIsLast) override {
        if (owner_->cb.OnRspUserLogin)
            owner_->cb.OnRspUserLogin(owner_->ctx, pField, pRspInfo, nRequestID, bIsLast);
    }
    void OnRspSubMarketData(CThostFtdcSpecificInstrumentField* pField,
                            CThostFtdcRspInfoField* pRspInfo, int nRequestID, bool bIsLast) override {
        if (owner_->cb.OnRspSubMarketData)
            owner_->cb.OnRspSubMarketData(owner_->ctx, pField, pRspInfo, nRequestID, bIsLast);
    }
    void OnRspUnSubMarketData(CThostFtdcSpecificInstrumentField* pField,
                              CThostFtdcRspInfoField* pRspInfo, int nRequestID, bool bIsLast) override {
        if (owner_->cb.OnRspUnSubMarketData)
            owner_->cb.OnRspUnSubMarketData(owner_->ctx, pField, pRspInfo, nRequestID, bIsLast);
    }
    void OnRtnDepthMarketData(CThostFtdcDepthMarketDataField* pField) override {
        if (owner_->cb.OnRtnDepthMarketData) owner_->cb.OnRtnDepthMarketData(owner_->ctx, pField);
    }
private:
    CtpMd* owner_;
};

} // namespace

extern "C" {

CTP_API CtpMdHandle CtpMd_Create(const char* flowPath,
                         const CtpMdCallbacks* callbacks, void* ctx) {
    if (!callbacks) return nullptr;
    auto* m = new CtpMd{};
    m->cb = *callbacks;
    m->ctx = ctx;
    m->api = CThostFtdcMdApi::CreateFtdcMdApi(flowPath ? flowPath : "");
    m->spi = new MdSpi(m);
    m->api->RegisterSpi(m->spi);
    return m;
}

CTP_API void CtpMd_Release(CtpMdHandle h) {
    if (!h) return;
    if (h->api) h->api->Release();
    delete h->spi;
    delete h;
}

CTP_API int CtpMd_Join(CtpMdHandle h) {
    if (!h || !h->api) return -1;
    return h->api->Join();
}

CTP_API int CtpMd_RegisterFront(CtpMdHandle h, const char* front) {
    if (!h || !h->api || !front) return -1;
    h->api->RegisterFront(const_cast<char*>(front));
    return 0;
}

CTP_API int CtpMd_Init(CtpMdHandle h) {
    if (!h || !h->api) return -1;
    h->api->Init();
    return 0;
}

CTP_API int CtpMd_Login(CtpMdHandle h,
                const char* brokerId, const char* userId, const char* password, int requestId) {
    if (!h || !h->api) return -1;
    CThostFtdcReqUserLoginField f{};
    if (brokerId) std::strncpy(f.BrokerID, brokerId, sizeof(f.BrokerID) - 1);
    if (userId)   std::strncpy(f.UserID,   userId,   sizeof(f.UserID) - 1);
    if (password) std::strncpy(f.Password, password, sizeof(f.Password) - 1);
    // Md 登录为 2 参（行情登录不要求穿透式采集信息）
    return h->api->ReqUserLogin(&f, requestId);
}

CTP_API int CtpMd_SubscribeMarketData(CtpMdHandle h, const char* const* symbols, int count) {
    if (!h || !h->api || !symbols || count <= 0) return -1;
    // CTP 接口要求 char**；它内部会拷贝合约号，调用期间指针有效即可。
    return h->api->SubscribeMarketData(const_cast<char**>(symbols), count);
}

CTP_API int CtpMd_UnSubscribeMarketData(CtpMdHandle h, const char* const* symbols, int count) {
    if (!h || !h->api || !symbols || count <= 0) return -1;
    return h->api->UnSubscribeMarketData(const_cast<char**>(symbols), count);
}

} // extern "C"
