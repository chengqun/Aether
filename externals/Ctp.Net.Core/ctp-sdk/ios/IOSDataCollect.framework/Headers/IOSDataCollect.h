#ifndef IOSDataCollect_h
#define IOSDataCollect_h

#import <UIKit/UIKit.h>


/*
 pSystemInfo: 加密后的采集信息。
 nLen: 获取到的采集信息的长度，错误返回 -1。
 nCode: 包含采集信息的错误信息。
 返回值nCode 不为0，表示采集信息有误，具体错误信息如下：

 返回值 & (0x01 << 0) 不为0，时间获取失败
 返回值 & (0x01 << 1) 不为0，IP获取失败
 返回值 & (0x01 << 2) 不为0，位置信息获取失败
 返回值 & (0x01 << 3) 不为0，操作系统版本获取失败
 返回值 & (0x01 << 4) 不为0，设备名称获取失败
 返回值 & (0x01 << 5) 不为0，设备类型获取失败
 返回值 & (0x01 << 6) 不为0，网络运营商获取失败
 返回值 & (0x01 << 7) 不为0，UUID获取失败

 */
//中继模式，获取加密信息
void CTP_GetSystemInfo(void (^GetSystemInfo)(char * pSystemInfo, int nLen, int nCode));
//直连模式，获取加密信息
void CTP_GetSystemInfoUnAesEncode(void (^GetSystemInfo)(char * pSystemInfo, int nLen, int nCode));


//版本号格式
//sfit + 生产还是测试秘钥(pro/tst) + 秘钥版本 + 编译时间 + 版本(内部)

const char * CTP_GetDataCollectApiVersion(void);

#endif /* IOSDataCollect_h */
