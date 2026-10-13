// MDNative shim:D2D 资源创建的单次原生调用入口。
// 背景:.NET 进程中,若工厂创建与 CreateDCRenderTarget 拆到两次 P/Invoke,
//       第二次调用会稳定返回 E_INVALIDARG(D2D 怪癖,纯 C++ 进程无此问题);
//       单次原生调用内完成全部创建则 100% 稳定。故 D2D 资源创建集中在此。
// 绘制调用仍由 C# 通过原始 vtable 指针执行(绘制路径实测稳定)。
#include <windows.h>
#include <d2d1.h>

// 开发构建编成 MDNative.dll 导出；NativeAOT 发布定义 MD_STATIC，静态链接进 exe，不再导出。
#ifdef MD_STATIC
#define MD_API extern "C"
#else
#define MD_API extern "C" __declspec(dllexport)
#endif
// 依赖的系统导入库随 obj 记录，静态链接时由宿主的链接器带上。
#pragma comment(lib, "d2d1.lib")
#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "windowscodecs.lib")
#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "ole32.lib")

// 创建工厂 + DC 渲染目标 + SetDpi + 两支纯色笔刷,单次调用完成。
// hrOut 返回失败位置的 HRESULT;返回值:0=成功,非 0=失败阶段
MD_API int __stdcall MD_CreateD2D(
    float dpiX, float dpiY,
    unsigned long long* rtOut,
    unsigned long long* brushInkOut,
    unsigned long long* brushCodeOut,
    unsigned long long* brushHairOut,
    unsigned long long* brushQuoteOut,
    unsigned* hrOut)
{
    *rtOut = 0; *brushInkOut = 0; *brushCodeOut = 0; *brushHairOut = 0; *brushQuoteOut = 0;

    ID2D1Factory* f = nullptr;
    HRESULT hr = D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, __uuidof(ID2D1Factory), nullptr, (void**)&f);
    if (FAILED(hr)) { *hrOut = (unsigned)hr; return 1; }

    D2D1_RENDER_TARGET_PROPERTIES p = D2D1::RenderTargetProperties();
    p.pixelFormat = D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_IGNORE);
    ID2D1DCRenderTarget* rt = nullptr;
    hr = f->CreateDCRenderTarget(&p, &rt);
    if (FAILED(hr) || !rt) { *hrOut = (unsigned)hr; f->Release(); return 2; }

    rt->SetDpi(dpiX, dpiY);

    D2D1_BRUSH_PROPERTIES bp = D2D1::BrushProperties(1.0f, D2D1::Matrix3x2F::Identity());
    D2D1_COLOR_F ink  = D2D1::ColorF(0.090f, 0.090f, 0.090f, 1.0f);  // #171717
    D2D1_COLOR_F code = D2D1::ColorF(0.949f, 0.945f, 0.933f, 1.0f);  // #F2F1EE
    D2D1_COLOR_F hair  = D2D1::ColorF(0.898f, 0.898f, 0.890f, 1.0f);  // #E5E5E3
    D2D1_COLOR_F quote = D2D1::ColorF(0.839f, 0.839f, 0.839f, 1.0f);  // #D6D6D6
    ID2D1SolidColorBrush* b1 = nullptr;
    ID2D1SolidColorBrush* b2 = nullptr;
    ID2D1SolidColorBrush* b3 = nullptr;
    ID2D1SolidColorBrush* b4 = nullptr;
    hr = rt->CreateSolidColorBrush(&ink, &bp, &b1);
    if (FAILED(hr) || !b1) { *hrOut = (unsigned)hr; rt->Release(); f->Release(); return 3; }
    hr = rt->CreateSolidColorBrush(&code, &bp, &b2);
    if (FAILED(hr) || !b2) { *hrOut = (unsigned)hr; b1->Release(); rt->Release(); f->Release(); return 4; }
    hr = rt->CreateSolidColorBrush(&hair, &bp, &b3);
    if (FAILED(hr) || !b3) { *hrOut = (unsigned)hr; b2->Release(); b1->Release(); rt->Release(); f->Release(); return 5; }
    hr = rt->CreateSolidColorBrush(&quote, &bp, &b4);
    if (FAILED(hr) || !b4) { *hrOut = (unsigned)hr; b3->Release(); b2->Release(); b1->Release(); rt->Release(); f->Release(); return 6; }

    *rtOut = (unsigned long long)rt;
    *brushInkOut = (unsigned long long)b1;
    *brushCodeOut = (unsigned long long)b2;
    *brushHairOut = (unsigned long long)b3;
    *brushQuoteOut = (unsigned long long)b4;
    *hrOut = 0;
    // The render target retains the factory; release this function's creation reference.
    f->Release();
    return 0;
}

#include <d3d11.h>
#include <d2d1_3.h>
#include <d2d1svg.h>
#include <wincodec.h>
#include <shlwapi.h>
#include <wrl/client.h>
using Microsoft::WRL::ComPtr;

// Validated, bounded SVG bytes only. No filesystem/network resolution occurs here.
MD_API int __stdcall MD_DecodeSvg(
    const unsigned char* bytes, unsigned length, unsigned width, unsigned height,
    unsigned long long* bitmapOut)
{
    if (!bitmapOut) return (int)E_POINTER;
    *bitmapOut = 0;
    if (!bytes || !length || length > 8u * 1024 * 1024 || !width || !height || width > 4096 || height > 4096 ||
        (unsigned long long)width * height > 16u * 1024 * 1024) return (int)E_INVALIDARG;
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> immediate;
    HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr,
        D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION, &device, nullptr, &immediate);
    if (FAILED(hr)) return (int)hr;
    ComPtr<IDXGIDevice> dxgi;
    hr = device.As(&dxgi); if (FAILED(hr)) return (int)hr;
    ComPtr<ID2D1Factory1> factory;
    hr = D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, __uuidof(ID2D1Factory1), nullptr, reinterpret_cast<void**>(factory.GetAddressOf()));
    if (FAILED(hr)) return (int)hr;
    ComPtr<ID2D1Device> drawingDevice;
    hr = factory->CreateDevice(dxgi.Get(), &drawingDevice); if (FAILED(hr)) return (int)hr;
    ComPtr<ID2D1DeviceContext> baseContext;
    hr = drawingDevice->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &baseContext); if (FAILED(hr)) return (int)hr;
    ComPtr<ID2D1DeviceContext5> context;
    hr = baseContext.As(&context); if (FAILED(hr)) return (int)hr;
    D2D1_BITMAP_PROPERTIES1 targetProperties = D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_TARGET,
        D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED));
    ComPtr<ID2D1Bitmap1> target;
    hr = context->CreateBitmap(D2D1::SizeU(width, height), nullptr, 0, &targetProperties, &target);
    if (FAILED(hr)) return (int)hr;
    ComPtr<IStream> stream;
    stream.Attach(SHCreateMemStream(bytes, length)); if (!stream) return (int)E_OUTOFMEMORY;
    ComPtr<ID2D1SvgDocument> svg;
    hr = context->CreateSvgDocument(stream.Get(), D2D1::SizeF((float)width, (float)height), &svg);
    if (FAILED(hr)) return (int)hr;
    context->SetTarget(target.Get());
    context->BeginDraw();
    context->Clear(D2D1::ColorF(0, 0, 0, 0));
    context->DrawSvgDocument(svg.Get());
    hr = context->EndDraw(); context->SetTarget(nullptr); if (FAILED(hr)) return (int)hr;
    D2D1_BITMAP_PROPERTIES1 readProperties = D2D1::BitmapProperties1(
        D2D1_BITMAP_OPTIONS_CPU_READ | D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
        D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED));
    ComPtr<ID2D1Bitmap1> readable;
    hr = context->CreateBitmap(D2D1::SizeU(width, height), nullptr, 0, &readProperties, &readable);
    if (FAILED(hr)) return (int)hr;
    hr = readable->CopyFromBitmap(nullptr, target.Get(), nullptr); if (FAILED(hr)) return (int)hr;
    D2D1_MAPPED_RECT mapped{};
    hr = readable->Map(D2D1_MAP_OPTIONS_READ, &mapped); if (FAILED(hr)) return (int)hr;
    ComPtr<IWICImagingFactory> wic;
    hr = CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&wic));
    ComPtr<IWICBitmap> bitmap;
    if (SUCCEEDED(hr)) hr = wic->CreateBitmapFromMemory(width, height, GUID_WICPixelFormat32bppPBGRA,
        mapped.pitch, mapped.pitch * height, mapped.bits, &bitmap);
    readable->Unmap();
    if (FAILED(hr)) return (int)hr;
    *bitmapOut = (unsigned long long)bitmap.Detach();
    return 0;
}

#include <new>

#include <dwrite.h>
class MdInlineBitmap final : public IDWriteInlineObject {
    LONG refs_ = 1;
    ComPtr<ID2D1RenderTarget> target_;
    ComPtr<ID2D1Bitmap> bitmap_;
    float width_, height_;
public:
    MdInlineBitmap(ID2D1RenderTarget* target, ID2D1Bitmap* bitmap, float width, float height)
        : target_(target), bitmap_(bitmap), width_(width), height_(height) {}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (iid == __uuidof(IUnknown) || iid == __uuidof(IDWriteInlineObject)) { *result = this; AddRef(); return S_OK; }
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return (ULONG)InterlockedIncrement(&refs_); }
    ULONG STDMETHODCALLTYPE Release() override { LONG result = InterlockedDecrement(&refs_); if (!result) delete this; return (ULONG)result; }
    HRESULT STDMETHODCALLTYPE Draw(void*, IDWriteTextRenderer*, FLOAT x, FLOAT y, BOOL, BOOL, IUnknown*) override {
        target_->DrawBitmap(bitmap_.Get(), D2D1::RectF(x, y, x + width_, y + height_), 1.f, D2D1_BITMAP_INTERPOLATION_MODE_LINEAR);
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetMetrics(DWRITE_INLINE_OBJECT_METRICS* metrics) override {
        if (!metrics) return E_POINTER;
        *metrics = { width_, height_, height_ * .85f, FALSE }; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetOverhangMetrics(DWRITE_OVERHANG_METRICS* metrics) override {
        if (!metrics) return E_POINTER; *metrics = {}; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetBreakConditions(DWRITE_BREAK_CONDITION* before, DWRITE_BREAK_CONDITION* after) override {
        if (!before || !after) return E_POINTER;
        *before = *after = DWRITE_BREAK_CONDITION_CAN_BREAK; return S_OK;
    }
};
MD_API int __stdcall MD_SetInlineBitmap(
    IDWriteTextLayout* layout, ID2D1RenderTarget* target, ID2D1Bitmap* bitmap,
    unsigned start, unsigned length, float width, float height)
{
    if (!layout || !target || !bitmap || !length || width <= 0 || height <= 0) return (int)E_INVALIDARG;
    auto* object = new(std::nothrow) MdInlineBitmap(target, bitmap, width, height);
    if (!object) return (int)E_OUTOFMEMORY;
    HRESULT result = layout->SetInlineObject(object, DWRITE_TEXT_RANGE{start, length});
    object->Release(); return (int)result;
}
