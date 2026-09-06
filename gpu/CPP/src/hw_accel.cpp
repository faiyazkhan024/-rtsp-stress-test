#include "hw_accel.h"
#include <algorithm>
#include <vector>

std::shared_ptr<HwAccelManager> HwAccelManager::create(const std::string& typePreference, int streamCount) {
    (void)streamCount;
    auto mgr = std::shared_ptr<HwAccelManager>(new HwAccelManager());

    std::string pref = typePreference;
    std::transform(pref.begin(), pref.end(), pref.begin(), ::tolower);

    if (pref == "none" || pref == "cpu") {
        return mgr;
    }

    std::vector<enum AVHWDeviceType> candidates;

    if (pref == "cuda") {
        candidates.push_back(AV_HWDEVICE_TYPE_CUDA);
    } else if (pref == "vaapi") {
        candidates.push_back(AV_HWDEVICE_TYPE_VAAPI);
    } else if (pref == "videotoolbox") {
        candidates.push_back(AV_HWDEVICE_TYPE_VIDEOTOOLBOX);
    } else if (pref == "d3d11va") {
        candidates.push_back(AV_HWDEVICE_TYPE_D3D11VA);
    } else { // "auto"
#if defined(__linux__)
        candidates.push_back(AV_HWDEVICE_TYPE_CUDA);
        candidates.push_back(AV_HWDEVICE_TYPE_VAAPI);
#elif defined(__APPLE__)
        candidates.push_back(AV_HWDEVICE_TYPE_VIDEOTOOLBOX);
#elif defined(_WIN32)
        candidates.push_back(AV_HWDEVICE_TYPE_D3D11VA);
        candidates.push_back(AV_HWDEVICE_TYPE_CUDA);
#else
        candidates.push_back(AV_HWDEVICE_TYPE_CUDA);
        candidates.push_back(AV_HWDEVICE_TYPE_VAAPI);
#endif
    }

    for (auto type : candidates) {
        if (mgr->initDevice(type)) {
            std::cout << "[HwAccel] Successfully initialized GPU hardware acceleration: "
                      << mgr->deviceName() << std::endl;
            return mgr;
        }
    }

    std::cerr << "[HwAccel] Warning: No requested hardware acceleration device initialized. "
              << "Falling back to GPU-shaded direct rendering." << std::endl;
    return mgr;
}

HwAccelManager::~HwAccelManager() {
    if (m_hwDeviceCtx) {
        av_buffer_unref(&m_hwDeviceCtx);
        m_hwDeviceCtx = nullptr;
    }
}

bool HwAccelManager::initDevice(enum AVHWDeviceType type, const char* device) {
    const char* typeName = av_hwdevice_get_type_name(type);
    if (!typeName) {
        return false;
    }

    int ret = av_hwdevice_ctx_create(&m_hwDeviceCtx, type, device, nullptr, 0);
    if (ret < 0) {
        char errBuf[256];
        av_strerror(ret, errBuf, sizeof(errBuf));
        std::cout << "[HwAccel] Could not initialize device type " << typeName
                  << " (" << errBuf << "). Trying next..." << std::endl;
        return false;
    }

    m_hwDeviceType = type;
    m_deviceName = typeName;

    switch (type) {
        case AV_HWDEVICE_TYPE_CUDA:
            m_hwPixFormat = AV_PIX_FMT_CUDA;
            break;
        case AV_HWDEVICE_TYPE_VAAPI:
            m_hwPixFormat = AV_PIX_FMT_VAAPI;
            break;
        case AV_HWDEVICE_TYPE_VIDEOTOOLBOX:
            m_hwPixFormat = AV_PIX_FMT_VIDEOTOOLBOX;
            break;
        case AV_HWDEVICE_TYPE_D3D11VA:
            m_hwPixFormat = AV_PIX_FMT_D3D11;
            break;
        default:
            m_hwPixFormat = AV_PIX_FMT_NONE;
            break;
    }

    return true;
}

AVBufferRef* HwAccelManager::createDeviceRef() {
    if (!m_hwDeviceCtx) return nullptr;
    return av_buffer_ref(m_hwDeviceCtx);
}

enum AVPixelFormat HwAccelManager::getHwFormat(AVCodecContext* ctx, const enum AVPixelFormat* pix_fmts) {
    auto* mgr = static_cast<HwAccelManager*>(ctx->opaque);
    if (!mgr || !mgr->isInitialized()) {
        return pix_fmts[0];
    }

    enum AVPixelFormat targetFmt = mgr->hwPixFormat();
    for (const enum AVPixelFormat* p = pix_fmts; *p != -1; p++) {
        if (*p == targetFmt) {
            return *p;
        }
    }

    std::cerr << "[HwAccel] Target hardware format " << av_get_pix_fmt_name(targetFmt)
              << " not found in codec formats list. Falling back." << std::endl;
    return pix_fmts[0];
}
