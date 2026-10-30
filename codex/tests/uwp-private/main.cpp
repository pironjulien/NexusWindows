// Private UWP qualification fixture: genuine AppContainer, CoreWindow and stock Windows XAML peers.
// No network/device capability, custom UIA provider, persistent debugger or user data access.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <winrt/Windows.ApplicationModel.h>
#include <winrt/Windows.ApplicationModel.Activation.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Storage.h>
#include <winrt/Windows.UI.ViewManagement.h>
#include <winrt/Windows.UI.Xaml.h>
#include <winrt/Windows.UI.Xaml.Automation.h>
#include <winrt/Windows.UI.Xaml.Controls.h>
#include <winrt/Windows.UI.Xaml.Controls.Primitives.h>
#include <string>
#include <exception>

using namespace winrt;
using namespace Windows::Foundation;
using namespace Windows::UI::Xaml;
using namespace Windows::UI::Xaml::Controls;
using namespace Windows::UI::Xaml::Automation;
using namespace Windows::UI::ViewManagement;

// Diagnostics belong only to this unique private package's LocalState. They never stand in for
// the independent package state or XAML counters required by the lifecycle qualification.
namespace
{
    char const* startupStage = "entry";

    std::string JsonString(std::string const& value)
    {
        std::string result = "\"";
        char const hex[] = "0123456789abcdef";
        for (unsigned char c : value)
        {
            if (c == '"' || c == '\\') { result += '\\'; result += static_cast<char>(c); }
            else if (c < 0x20) { result += "\\u00"; result += hex[c >> 4]; result += hex[c & 15]; }
            else result += static_cast<char>(c);
        }
        return result + '"';
    }

    void Diagnostic(char const* kind, int32_t code = 0, std::string const& message = {}) noexcept
    {
        try
        {
            auto path = std::wstring(Windows::Storage::ApplicationData::Current().LocalFolder().Path())
                + L"\\startup.json";
            auto json = std::string("{\"schema\":1,\"pid\":") + std::to_string(GetCurrentProcessId())
                + ",\"kind\":" + JsonString(kind) + ",\"stage\":" + JsonString(startupStage)
                + ",\"hresult\":" + std::to_string(code) + ",\"message\":" + JsonString(message) + "}\n";
            auto file = CreateFile2(path.c_str(), GENERIC_WRITE, FILE_SHARE_READ, CREATE_ALWAYS, nullptr);
            if (file == INVALID_HANDLE_VALUE) return;
            DWORD written{};
            WriteFile(file, json.data(), static_cast<DWORD>(json.size()), &written, nullptr);
            CloseHandle(file);
        }
        catch (...) { OutputDebugStringW(L"Nexus private fixture could not persist its LocalState diagnostic.\n"); }
    }

    void Stage(char const* value) noexcept { startupStage = value; Diagnostic("progress"); }
    void Failure() noexcept
    {
        try { throw; }
        catch (hresult_error const& error) { Diagnostic("hresult_error", error.code().value, to_string(error.message())); }
        catch (std::exception const& error) { Diagnostic("std_exception", 0, error.what()); }
        catch (...) { Diagnostic("unknown_exception"); }
    }
}

struct FixtureApp : ApplicationT<FixtureApp>
{
    TextBlock status{ nullptr };
    unsigned resumed{}, suspending{}, background{};

    FixtureApp()
    {
        Stage("application_constructor");
        Resuming([weak = get_weak()](auto const&, auto const&) {
            if (auto self = weak.get()) { ++self->resumed; self->Update(); }
        });
        Suspending([weak = get_weak()](auto const&, auto const&) {
            if (auto self = weak.get()) { ++self->suspending; self->Update(); }
        });
        EnteredBackground([weak = get_weak()](auto const&, auto const&) {
            if (auto self = weak.get()) { ++self->background; self->Update(); }
        });
    }
    void Update()
    {
        if (!status) return;
        auto text = L"Nexus private UWP ready | resume=" + std::to_wstring(resumed)
            + L" | suspend=" + std::to_wstring(suspending) + L" | background=" + std::to_wstring(background)
            + L" | pid=" + std::to_wstring(GetCurrentProcessId());
        status.Text(text);
        AutomationProperties::SetName(status, text);
    }
    void OnLaunched(Windows::ApplicationModel::Activation::LaunchActivatedEventArgs const&)
    {
        try
        {
        Stage("on_launched_view");
        ApplicationView::PreferredLaunchViewSize({ 600, 260 });
        ApplicationView::PreferredLaunchWindowingMode(ApplicationViewWindowingMode::PreferredLaunchViewSize);
        ApplicationView::GetForCurrentView().Title(L"Nexus private UWP qualification");
        Stage("on_launched_controls");
        StackPanel panel;
        panel.Margin({ 24, 24, 24, 24 });
        TextBlock heading;
        heading.Text(L"Isolated Nexus UWP qualification");
        heading.FontSize(22);
        panel.Children().Append(heading);
        status = TextBlock{};
        status.Margin({ 0, 20, 0, 20 });
        status.TextWrapping(TextWrapping::Wrap);
        AutomationProperties::SetAutomationId(status, L"NexusUwpState");
        panel.Children().Append(status);
        Button close;
        close.Content(box_value(L"Close private fixture"));
        AutomationProperties::SetAutomationId(close, L"NexusUwpClose");
        close.Click([](auto const&, auto const&) { Application::Current().Exit(); });
        panel.Children().Append(close);
        Update();
        Stage("on_launched_content");
        Window::Current().Content(panel);
        Stage("on_launched_activate");
        Window::Current().Activate();
        Stage("ready");
        }
        catch (...) { Failure(); throw; }
    }
};

int __stdcall wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
    try
    {
    // The Store CRT has already called RoInitialize(RO_INIT_MULTITHREADED) before wWinMain
    // (MSVC crt/src/vcruntime/utility_app.cpp and app_appinit.cpp). Requesting STA here changes
    // that existing apartment and fails with RPC_E_CHANGED_MODE. Application::Start creates the
    // main-view UI context and invokes its callback there; keep the CRT's entry-thread apartment.
    Stage("application_start");
    Application::Start([](auto const&) {
        try { Stage("application_make"); make<FixtureApp>(); }
        catch (...) { Failure(); throw; }
    });
    return 0;
    }
    catch (...) { Failure(); return 1; }
}
