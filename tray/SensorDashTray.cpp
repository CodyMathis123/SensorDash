// SensorDash native user-session tray. No CLR, WinForms, ASP.NET or hardware access.
// The Windows service owns data; this process only reads /api/tray-state.
#define UNICODE
#define _UNICODE
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <winhttp.h>
#include <string>
#include <vector>
#include <sstream>
#include <algorithm>
#include <cwchar>

#pragma comment(lib, "user32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "winhttp.lib")

namespace {
constexpr UINT kCallback = WM_APP + 41;
constexpr UINT_PTR kTimer = 1;
constexpr UINT kOpen = 101, kConfigure = 102, kExit = 103;
constexpr int kSize = 32;
HWND windowHandle = nullptr;
HICON images[7] = {};
bool added[7] = {};
std::wstring previous[7];
struct Widget { std::wstring label, value, unit, color; };

std::wstring Wide(const std::string& utf8) {
    if (utf8.empty()) return {};
    int required = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS,
        utf8.data(), static_cast<int>(utf8.size()), nullptr, 0);
    if (required <= 0) return L"?";
    std::wstring out(static_cast<size_t>(required), L'\0');
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS,
        utf8.data(), static_cast<int>(utf8.size()), out.data(), required);
    return out;
}
std::vector<std::string> Fields(const std::string& line) {
    std::vector<std::string> result;
    size_t pos = 0;
    do {
        size_t end = line.find('\t', pos);
        result.push_back(line.substr(pos, end == std::string::npos ? end : end-pos));
        if (end == std::string::npos) break;
        pos = end + 1;
    } while (result.size() < 8);
    return result;
}
bool ReadStatus(std::string& output) {
    HINTERNET session = WinHttpOpen(L"SensorDash Tray/1", WINHTTP_ACCESS_TYPE_NO_PROXY,
        WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    if (!session) return false;
    WinHttpSetTimeouts(session, 500, 500, 1000, 1000);
    HINTERNET connection = WinHttpConnect(session, L"127.0.0.1", 8765, 0);
    HINTERNET request = connection ? WinHttpOpenRequest(connection, L"GET",
        L"/api/tray-state", nullptr, WINHTTP_NO_REFERER,
        WINHTTP_DEFAULT_ACCEPT_TYPES, 0) : nullptr;
    bool ok = false;
    if (request && WinHttpSendRequest(request, WINHTTP_NO_ADDITIONAL_HEADERS,
        0, WINHTTP_NO_REQUEST_DATA, 0, 0, 0) &&
        WinHttpReceiveResponse(request, nullptr)) {
        DWORD status = 0, length = sizeof(status);
        if (WinHttpQueryHeaders(request, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
            WINHTTP_HEADER_NAME_BY_INDEX, &status, &length, WINHTTP_NO_HEADER_INDEX) &&
            status == 200) {
            ok = true;
            while (output.size() < 8192) {
                DWORD available = 0, received = 0;
                if (!WinHttpQueryDataAvailable(request, &available)) { ok = false; break; }
                if (!available) break;
                char buffer[1024];
                DWORD count = std::min<DWORD>(available, sizeof(buffer));
                if (!WinHttpReadData(request, buffer, count, &received)) { ok = false; break; }
                if (!received) break;
                output.append(buffer, received);
            }
            if (output.size() >= 8192) ok = false;
        }
    }
    if (request) WinHttpCloseHandle(request);
    if (connection) WinHttpCloseHandle(connection);
    WinHttpCloseHandle(session);
    return ok;
}
COLORREF Color(const std::wstring& input) {
    if (input.size() != 7 || input[0] != L'#') return RGB(31, 71, 120);
    unsigned int red = 0, green = 0, blue = 0;
    if (swscanf_s(input.c_str(), L"#%02x%02x%02x", &red, &green, &blue) != 3)
        return RGB(31, 71, 120);
    return RGB(red, green, blue);
}
HICON DrawIcon(const std::wstring& value, const std::wstring& color) {
    HDC screen = GetDC(nullptr);
    if (!screen) return LoadIconW(nullptr, IDI_APPLICATION);
    HDC dc = CreateCompatibleDC(screen);
    HBITMAP bitmap = CreateCompatibleBitmap(screen, kSize, kSize);
    HBITMAP mask = CreateBitmap(kSize, kSize, 1, 1, nullptr);
    if (!dc || !bitmap || !mask) {
        if (bitmap) DeleteObject(bitmap);
        if (mask) DeleteObject(mask);
        if (dc) DeleteDC(dc);
        ReleaseDC(nullptr, screen);
        return LoadIconW(nullptr, IDI_APPLICATION);
    }
    auto old = SelectObject(dc, bitmap);
    RECT rect{0, 0, kSize, kSize};
    HBRUSH brush = CreateSolidBrush(Color(color));
    FillRect(dc, &rect, brush);
    DeleteObject(brush);
    SetBkMode(dc, TRANSPARENT);
    SetTextColor(dc, RGB(255, 255, 255));
    int fontSize = value.size() > 3 ? 15 : (value.size() > 2 ? 19 : 23);
    HFONT font = CreateFontW(-fontSize, 0, 0, 0, FW_BOLD, FALSE, FALSE, FALSE,
        DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
        DEFAULT_PITCH, L"Segoe UI");
    HGDIOBJ oldFont = SelectObject(dc, font);
    DrawTextW(dc, value.c_str(), -1, &rect, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
    SelectObject(dc, oldFont);
    DeleteObject(font);
    SelectObject(dc, old);
    ICONINFO info{};
    info.fIcon = TRUE;
    info.hbmColor = bitmap;
    info.hbmMask = mask;
    HICON icon = CreateIconIndirect(&info);
    DeleteObject(bitmap);
    DeleteObject(mask);
    DeleteDC(dc);
    ReleaseDC(nullptr, screen);
    return icon;
}
void RemoveIcon(int id) {
    if (added[id]) {
        NOTIFYICONDATAW info{};
        info.cbSize = sizeof(info);
        info.hWnd = windowHandle;
        info.uID = static_cast<UINT>(id);
        Shell_NotifyIconW(NIM_DELETE, &info);
        added[id] = false;
    }
    if (images[id]) {
        DestroyIcon(images[id]);
        images[id] = nullptr;
    }
    previous[id].clear();
}
void SetIcon(int id, const std::wstring& value,
             const std::wstring& tooltip, const std::wstring& color) {
    auto signature = value + L"|" + tooltip + L"|" + color;
    if (added[id] && signature == previous[id]) return;
    HICON image = DrawIcon(value, color);
    if (!image) return;
    NOTIFYICONDATAW info{};
    info.cbSize = sizeof(info);
    info.hWnd = windowHandle;
    info.uID = static_cast<UINT>(id);
    info.uFlags = NIF_ICON | NIF_TIP | NIF_MESSAGE;
    info.uCallbackMessage = kCallback;
    info.hIcon = image;
    wcsncpy_s(info.szTip, _countof(info.szTip), tooltip.c_str(), _TRUNCATE);
    if (!Shell_NotifyIconW(added[id] ? NIM_MODIFY : NIM_ADD, &info)) {
        DestroyIcon(image);
        return;
    }
    HICON old = images[id];
    images[id] = image;
    if (old) DestroyIcon(old);
    added[id] = true;
    previous[id] = signature;
}
void Open(bool configure) {
    ShellExecuteW(windowHandle, L"open",
        configure ? L"http://127.0.0.1:8765/#tray" : L"http://127.0.0.1:8765/",
        nullptr, nullptr, SW_SHOWNORMAL);
}
void Refresh() {
    std::string payload;
    bool connected = ReadStatus(payload);
    std::vector<Widget> widgets;
    bool online = false;
    if (connected) {
        std::istringstream reader(payload);
        std::string line;
        while (std::getline(reader, line)) {
            if (!line.empty() && line.back() == '\r') line.pop_back();
            auto parts = Fields(line);
            if (parts.size() == 2 && parts[0] == "status")
                online = parts[1] == "online";
            else if (parts.size() == 5 && parts[0] == "widget" && widgets.size() < 6)
                widgets.push_back({Wide(parts[1]), Wide(parts[2]), Wide(parts[3]), Wide(parts[4])});
        }
    }
    SetIcon(0, L"SD", online ? L"SensorDash - Online" : L"SensorDash - Collector offline",
        online ? L"#205C80" : L"#6D4653");
    for (int i = 1; i <= 6; ++i) {
        if (connected && static_cast<size_t>(i) <= widgets.size()) {
            const auto& widget = widgets[static_cast<size_t>(i-1)];
            auto tooltip = widget.label + L": " + widget.value + L" " + widget.unit;
            SetIcon(i, widget.value, tooltip, widget.color);
        } else {
            RemoveIcon(i);
        }
    }
}
void ShowMenu() {
    HMENU menu = CreatePopupMenu();
    if (!menu) return;
    AppendMenuW(menu, MF_STRING, kOpen, L"Open SensorDash");
    AppendMenuW(menu, MF_STRING, kConfigure, L"Configure tray sensors");
    AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(menu, MF_STRING, kExit, L"Exit tray (service stays running)");
    POINT point{};
    GetCursorPos(&point);
    SetForegroundWindow(windowHandle);
    TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, point.x, point.y,
        0, windowHandle, nullptr);
    DestroyMenu(menu);
    PostMessageW(windowHandle, WM_NULL, 0, 0);
}
LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam) {
    switch (message) {
    case WM_CREATE:
        SetTimer(window, kTimer, 2000, nullptr);
        PostMessageW(window, WM_TIMER, kTimer, 0);
        return 0;
    case WM_TIMER:
        if (wParam == kTimer) Refresh();
        return 0;
    case kCallback:
        if (lParam == WM_LBUTTONDBLCLK) Open(false);
        else if (lParam == WM_RBUTTONUP || lParam == WM_CONTEXTMENU) ShowMenu();
        return 0;
    case WM_COMMAND:
        switch (LOWORD(wParam)) {
        case kOpen: Open(false); break;
        case kConfigure: Open(true); break;
        case kExit: DestroyWindow(window); break;
        }
        return 0;
    case WM_CLOSE:
        DestroyWindow(window);
        return 0;
    case WM_DESTROY:
        KillTimer(window, kTimer);
        for (int id = 0; id <= 6; ++id) RemoveIcon(id);
        PostQuitMessage(0);
        return 0;
    default: return DefWindowProcW(window, message, wParam, lParam);
    }
}
} // namespace

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int) {
    HANDLE mutex = CreateMutexW(nullptr, TRUE, L"Local\\SensorDash.Tray");
    if (!mutex) return 1;
    if (GetLastError() == ERROR_ALREADY_EXISTS) {
        CloseHandle(mutex);
        Open(false);
        return 0;
    }
    WNDCLASSW cls{};
    cls.hInstance = instance;
    cls.lpszClassName = L"SensorDash.NativeTray";
    cls.lpfnWndProc = WindowProc;
    cls.hIcon = LoadIconW(instance, MAKEINTRESOURCEW(101));
    if (!RegisterClassW(&cls)) { CloseHandle(mutex); return 2; }
    windowHandle = CreateWindowExW(0, cls.lpszClassName,
        L"SensorDash Tray", WS_OVERLAPPEDWINDOW, CW_USEDEFAULT, CW_USEDEFAULT,
        220, 100, nullptr, nullptr, instance, nullptr);
    if (!windowHandle) { CloseHandle(mutex); return 3; }
    MSG message{};
    while (GetMessageW(&message, nullptr, 0, 0) > 0) {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
    CloseHandle(mutex);
    return 0;
}
