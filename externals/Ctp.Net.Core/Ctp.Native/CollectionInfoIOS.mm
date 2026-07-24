// iOS 直连模式穿透式采集：CTP_GetSystemInfoUnAesEncode 是异步 block 回调，
// 这里用 dispatch_semaphore 同步等待结果，供 TraderWrapper 的 ReqUserLogin 使用。
// 仅 iOS 编译（CMake 在 iOS 分支纳入）。需 UIKit/CoreLocation/CoreTelephony + 应用定位权限。
#import <Foundation/Foundation.h>
#include "IOSDataCollect.h"
#include <string.h>

extern "C" int CtpFetchCollectionInfoIOS(char* buf, int bufSize, int* outLen)
{
    *outLen = 0;
    if (!buf || bufSize <= 0) return -1;

    struct CollectResult { char data[273]; int len; int code; };
    __block CollectResult r = {{0}, 0, -1};

    dispatch_semaphore_t sem = dispatch_semaphore_create(0);
    CTP_GetSystemInfoUnAesEncode(^(char *pSystemInfo, int nLen, int nCode) {
        if (pSystemInfo && nLen > 0) {
            int n = nLen < (int)sizeof(r.data) ? nLen : (int)sizeof(r.data);
            memcpy(r.data, pSystemInfo, n);
            r.len = n;
        }
        r.code = nCode;
        dispatch_semaphore_signal(sem);
    });
    // 最多等 5 秒（采集可能涉及定位/网络，避免无限阻塞登录线程）
    dispatch_semaphore_wait(sem, dispatch_time(DISPATCH_TIME_NOW, 5 * NSEC_PER_SEC));

    int copyLen = r.len < bufSize ? r.len : bufSize;
    if (copyLen > 0) memcpy(buf, r.data, copyLen);
    *outLen = r.len;
    return r.code;
}
