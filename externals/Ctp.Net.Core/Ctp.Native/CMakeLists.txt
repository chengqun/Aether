cmake_minimum_required(VERSION 3.20)
project(ctp_native CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

# CTP 6.7.2 二进制 SDK 根目录（随仓库分发于 Ctp.Net.Core/ctp-sdk，含 ios/linux/macos/win 全平台）。
# 不再依赖外部 externals/ctp-6.7.2 —— ctp-sdk 已入库，所有平台开箱即用。
set(CTP_SDK ${CMAKE_SOURCE_DIR}/../ctp-sdk)

# 按平台选择 CTP 二进制目录 + 头文件目录 + 库类型
set(IOS_EXTRA_SRC "")
if(CMAKE_SYSTEM_NAME STREQUAL "iOS")
    set(CTP_ROOT ${CTP_SDK}/ios)
    set(CTP_INC
        ${CTP_ROOT}/thosttraderapi_se.framework/Headers
        ${CTP_ROOT}/thostmduserapi_se.framework/Headers
        ${CTP_ROOT}/IOSDataCollect.framework/Headers)
    set(IOS_EXTRA_SRC CollectionInfoIOS.mm)
    set(LIB_TYPE STATIC)        # iOS：静态库（MAUI 链入 app；framework 在 app 链接期解析）
elseif(APPLE)
    set(CTP_ROOT ${CTP_SDK}/macos-universal)
    set(CTP_INC
        ${CTP_ROOT}/thosttraderapi_se.framework/Versions/A/Headers
        ${CTP_ROOT}/thostmduserapi_se.framework/Versions/A/Headers)
    set(LIB_TYPE SHARED)
elseif(UNIX)
    set(CTP_ROOT ${CTP_SDK}/linux-x64)
    set(CTP_INC ${CTP_ROOT})
    set(LIB_TYPE SHARED)
elseif(WIN32)
    if(CMAKE_SIZEOF_VOID_P EQUAL 8)
        set(CTP_ROOT ${CTP_SDK}/win-x64)
    else()
        set(CTP_ROOT ${CTP_SDK}/win-x86)
    endif()
    set(CTP_INC ${CTP_ROOT})
    set(LIB_TYPE SHARED)
endif()

# native 库输出到 AlphaAgent/src/Ctp.Net.Core/build/native（及 iOS 子目录）
if(CMAKE_SYSTEM_NAME STREQUAL "iOS")
    set(CMAKE_ARCHIVE_OUTPUT_DIRECTORY ${CMAKE_SOURCE_DIR}/../build/native/ios)
else()
    set(CMAKE_LIBRARY_OUTPUT_DIRECTORY ${CMAKE_SOURCE_DIR}/../build/native)
    set(CMAKE_RUNTIME_OUTPUT_DIRECTORY ${CMAKE_SOURCE_DIR}/../build/native)
endif()

add_library(ctp_native ${LIB_TYPE}
    TraderWrapper.cpp
    MdWrapper.cpp
    ${IOS_EXTRA_SRC}
)

target_include_directories(ctp_native PRIVATE
    ${CMAKE_SOURCE_DIR}/include
    ${CTP_INC}
)

# MSVC 默认按系统编码（中文 Windows = GBK）解析源文件，而 wrapper 源码/头文件是
# UTF-8（含中文注释）。GBK 误读多字节字符会导致行号错位、注释吞语法 → 一堆 C2059。
# /utf-8 同时设源与执行字符集为 UTF-8。CTP 官方头文件同样按 UTF-8 处理无碍。
if(MSVC)
    target_compile_options(ctp_native PRIVATE /utf-8 /W3)
endif()

set_target_properties(ctp_native PROPERTIES OUTPUT_NAME ctp_native)

if(CMAKE_SYSTEM_NAME STREQUAL "iOS")
    # iOS：3 framework + 采集（异步 block，见 CollectionInfoIOS.mm）+ UIKit/CoreLocation/CoreTelephony
    target_link_libraries(ctp_native PRIVATE
        "-F${CTP_ROOT}"
        "-framework thosttraderapi_se"
        "-framework thostmduserapi_se"
        "-framework IOSDataCollect"
        "-framework UIKit"
        "-framework CoreLocation"
        "-framework CoreTelephony"
        "-framework Foundation"
    )
    target_compile_definitions(ctp_native PRIVATE CTP_IOS=1)
    set_target_properties(ctp_native PROPERTIES
        XCODE_ATTRIBUTE_IPHONEOS_DEPLOYMENT_TARGET "11.0")
elseif(APPLE)
    # macOS：framework（install_name 为 @rpath/...）+ 采集库
    target_link_libraries(ctp_native PRIVATE
        "-F${CTP_ROOT}"
        "-framework thosttraderapi_se"
        "-framework thostmduserapi_se"
        "-framework MacDataCollect"
        "-framework IOKit"
        "-framework Foundation"
        "-framework CoreFoundation"
    )
    target_include_directories(ctp_native PRIVATE
        ${CTP_ROOT}/MacDataCollect.framework/Versions/A/Headers
    )
    set_target_properties(ctp_native PROPERTIES
        BUILD_RPATH "${CTP_ROOT}"
        INSTALL_RPATH "@loader_path"
    )
    target_compile_definitions(ctp_native PRIVATE CTP_MAC=1)
elseif(UNIX)
    # Linux：.so 直接链接
    target_link_directories(ctp_native PRIVATE ${CTP_ROOT})
    target_link_libraries(ctp_native PRIVATE thosttraderapi_se thostmduserapi_se)
    set_target_properties(ctp_native PROPERTIES
        BUILD_RPATH "${CTP_ROOT}"
        INSTALL_RPATH "$ORIGIN"
    )
    target_compile_definitions(ctp_native PRIVATE CTP_LINUX=1)
    add_custom_command(TARGET ctp_native POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            ${CTP_ROOT}/thosttraderapi_se.so ${CTP_ROOT}/thostmduserapi_se.so
            ${CMAKE_LIBRARY_OUTPUT_DIRECTORY}
    )
elseif(WIN32)
    # Windows：链接 .lib，运行时部署 .dll
    target_link_directories(ctp_native PRIVATE ${CTP_ROOT})
    target_link_libraries(ctp_native PRIVATE thosttraderapi_se thostmduserapi_se)
    target_compile_definitions(ctp_native PRIVATE CTP_WIN=1 _CRT_SECURE_NO_WARNINGS CTP_NATIVE_EXPORTS)
    add_custom_command(TARGET ctp_native POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            ${CTP_ROOT}/thosttraderapi_se.dll ${CTP_ROOT}/thostmduserapi_se.dll
            ${CMAKE_RUNTIME_OUTPUT_DIRECTORY}
    )
endif()
