// sojourn_web: the web pages of Sojourn's media (media on a prim, parcel media) in Chromium (CEF), off screen.
// Written by Claude Opus 5.5, 2026-10-26.
//
// Built as sojourn_web.dll, loaded by CEF's bootstrap.exe renamed sojourn_web.exe, so Chromium's own sandbox holds
// every renderer, GPU and utility process (the bootstrap is how CEF supports the Windows sandbox since M138). This
// process is the browser process: it runs no page content itself. The client (Sojourn.Client/Media) starts it, talks
// to it over the control pipe and reads frames and sound from each surface's shared section (common/protocol.h).
//
// Web content is untrusted. What this process enforces, whatever the client asks:
//   - top-level and frame navigations only to http, https, about:blank and data/blob in frames; never file:, chrome:,
//     devtools:, view-source: or any other scheme (secondlife:// links are reported, not followed);
//   - no request of any kind to a loopback or private address unless the client says the grid itself is local;
//   - the face's whitelist (the viewer's matching rules) on every main-frame navigation and redirect;
//   - no popups or new windows (reported), no downloads, no file dialogs, no permission grants (camera, mic,
//     location, notifications...), no JavaScript dialogs, no context menus, no devtools window.
#include <windows.h>

#include <map>
#include <mutex>
#include <regex>
#include <set>
#include <string>
#include <vector>

#include "include/base/cef_callback.h"
#include "include/cef_app.h"
#include "include/cef_browser.h"
#include "include/cef_client.h"
#include "include/cef_command_line.h"
#include "include/cef_devtools_message_observer.h"
#include "include/cef_request_context.h"
#include "include/cef_sandbox_win.h"
#include "include/cef_version.h"
#include "include/cef_version_info.h"
#include "include/wrapper/cef_closure_task.h"
#include "include/wrapper/cef_helpers.h"

#include "../common/channel.h"
#include "../common/urls.h"

namespace {

sm::Pipe g_pipe;
std::string g_ua_product;
bool g_private_ok = false;
bool g_quitting = false;

void Send(const sm::Json& j) { g_pipe.Send(j); }

void Log(const std::string& msg) {
    sm::Json j = sm::Json::Object();
    j.Set("ev", "log").Set("msg", msg);
    Send(j);
}

std::string Str(const CefString& s) { return s.ToString(); }

// ---------------------------------------------------------------------------------------------------------------
// One page on one surface.

class WebView : public CefClient,
                public CefRenderHandler,
                public CefAudioHandler,
                public CefLifeSpanHandler,
                public CefLoadHandler,
                public CefDisplayHandler,
                public CefRequestHandler,
                public CefResourceRequestHandler,
                public CefPermissionHandler,
                public CefDialogHandler,
                public CefDownloadHandler,
                public CefJSDialogHandler,
                public CefContextMenuHandler,
                public CefDevToolsMessageObserver {
public:
    WebView(int id, int w, int h, float scale, bool audio, std::vector<std::string> allow)
        : id_(id), w_(std::max(16, w)), h_(std::max(16, h)), scale_(scale), audio_(audio) {
        SetAllow(std::move(allow));
    }

    int id() const { return id_; }
    sm::Surface& surface() { return shm_; }
    CefRefPtr<CefBrowser> browser() { return browser_; }

    void SetAllow(std::vector<std::string> allow) {
        std::lock_guard<std::mutex> lk(policy_mu_);
        allow_ = std::move(allow);
    }

    // the surface's size in page pixels and the scale it's drawn at; the scale is held under what the section holds
    void Resize(int w, int h, float scale) {
        w_ = std::max(16, w);
        h_ = std::max(16, h);
        scale_ = scale;
        if (browser_) {
            browser_->GetHost()->NotifyScreenInfoChanged();
            browser_->GetHost()->WasResized();
        }
    }

    float EffectiveScale() const {
        float s = std::max(0.1f, std::min(scale_, 4.0f));
        if (shm_.Ok()) {
            s = std::min(s, (float)shm_.MaxWidth() / w_);
            s = std::min(s, (float)shm_.MaxHeight() / h_);
        }
        return s;
    }

    // ---- CefClient ----
    CefRefPtr<CefRenderHandler> GetRenderHandler() override { return this; }
    CefRefPtr<CefAudioHandler> GetAudioHandler() override { return this; }
    CefRefPtr<CefLifeSpanHandler> GetLifeSpanHandler() override { return this; }
    CefRefPtr<CefLoadHandler> GetLoadHandler() override { return this; }
    CefRefPtr<CefDisplayHandler> GetDisplayHandler() override { return this; }
    CefRefPtr<CefRequestHandler> GetRequestHandler() override { return this; }
    CefRefPtr<CefPermissionHandler> GetPermissionHandler() override { return this; }
    CefRefPtr<CefDialogHandler> GetDialogHandler() override { return this; }
    CefRefPtr<CefDownloadHandler> GetDownloadHandler() override { return this; }
    CefRefPtr<CefJSDialogHandler> GetJSDialogHandler() override { return this; }
    CefRefPtr<CefContextMenuHandler> GetContextMenuHandler() override { return this; }

    // ---- rendering ----
    void GetViewRect(CefRefPtr<CefBrowser>, CefRect& rect) override { rect = CefRect(0, 0, w_, h_); }

    bool GetScreenInfo(CefRefPtr<CefBrowser>, CefScreenInfo& info) override {
        info.device_scale_factor = EffectiveScale();
        info.rect = CefRect(0, 0, w_, h_);
        info.available_rect = info.rect;
        info.depth = 24;
        info.depth_per_component = 8;
        return true;
    }

    void OnPopupShow(CefRefPtr<CefBrowser>, bool show) override {
        popup_shown_ = show;
        if (!show) {
            popup_.clear();
            if (!view_.empty()) shm_.Publish(view_.data(), view_w_, view_h_, view_w_ * 4, 0, 0, 0, 0);
        }
    }

    void OnPopupSize(CefRefPtr<CefBrowser>, const CefRect& rect) override { popup_rect_ = rect; }

    void OnPaint(CefRefPtr<CefBrowser>, PaintElementType type, const RectList& dirty, const void* buffer, int width, int height) override {
        if (!shm_.Ok()) return;
        const uint8_t* px = (const uint8_t*)buffer;
        if (type == PET_VIEW) {
            view_w_ = width;
            view_h_ = height;
            view_.assign(px, px + (size_t)width * height * 4);
            int x0 = width, y0 = height, x1 = 0, y1 = 0;
            for (auto& r : dirty) {
                x0 = std::min(x0, r.x);
                y0 = std::min(y0, r.y);
                x1 = std::max(x1, r.x + r.width);
                y1 = std::max(y1, r.y + r.height);
            }
            if (popup_shown_ && !popup_.empty()) {
                Composite();
                return;
            }
            if (x1 <= x0 || y1 <= y0) { x0 = y0 = 0; x1 = width; y1 = height; }
            shm_.Publish(view_.data(), width, height, width * 4, x0, y0, x1 - x0, y1 - y0);
        } else if (type == PET_POPUP) {
            popup_.assign(px, px + (size_t)width * height * 4);
            popup_w_ = width;
            popup_h_ = height;
            if (!view_.empty()) Composite();
        }
    }

    void OnVirtualKeyboardRequested(CefRefPtr<CefBrowser>, TextInputMode mode) override {
        sm::Json j = sm::Json::Object();
        j.Set("ev", "keyboard").Set("id", id_).Set("mode", (int)mode);
        Send(j);
    }

    // ---- sound: captured here, so nothing plays on the system's device; it goes to the client's mix ----
    bool GetAudioParameters(CefRefPtr<CefBrowser>, CefAudioParameters& params) override {
        params.sample_rate = 48000;
        params.channel_layout = CEF_CHANNEL_LAYOUT_STEREO;
        params.frames_per_buffer = 480;
        return true;   // always captured: with audio off the packets are dropped rather than played on the device
    }
    void OnAudioStreamStarted(CefRefPtr<CefBrowser>, const CefAudioParameters& params, int channels) override {
        audio_rate_ = params.sample_rate;
        audio_channels_ = channels;
    }
    void OnAudioStreamPacket(CefRefPtr<CefBrowser>, const float** data, int frames, int64_t) override {
        if (audio_) shm_.WriteAudioPlanar(data, frames, audio_channels_, audio_rate_);
    }
    void OnAudioStreamStopped(CefRefPtr<CefBrowser>) override {}
    void OnAudioStreamError(CefRefPtr<CefBrowser>, const CefString& message) override { Log("audio: " + Str(message)); }

    void SetAudio(bool on) { audio_ = on; }

    // ---- life span: no popups or new windows ----
    bool OnBeforePopup(CefRefPtr<CefBrowser>, CefRefPtr<CefFrame>, int, const CefString& target_url, const CefString&,
                       CefLifeSpanHandler::WindowOpenDisposition, bool user_gesture, const CefPopupFeatures&, CefWindowInfo&, CefRefPtr<CefClient>&,
                       CefBrowserSettings&, CefRefPtr<CefDictionaryValue>&, bool*) override {
        Report("popup", Str(target_url), user_gesture ? "gesture" : "script");
        return true;
    }
    void OnAfterCreated(CefRefPtr<CefBrowser> browser) override {
        browser_ = browser;
        devtools_ = browser->GetHost()->AddDevToolsMessageObserver(this);
        browser->GetHost()->SetWindowlessFrameRate(fps_);
        browser->GetHost()->WasHidden(hidden_);
    }
    void OnBeforeClose(CefRefPtr<CefBrowser>) override;

    // ---- loading and display ----
    void OnLoadingStateChange(CefRefPtr<CefBrowser>, bool loading, bool back, bool fwd) override {
        sm::Json j = sm::Json::Object();
        j.Set("ev", "loading").Set("id", id_).Set("loading", loading).Set("back", back).Set("fwd", fwd);
        Send(j);
    }
    void OnLoadError(CefRefPtr<CefBrowser>, CefRefPtr<CefFrame> frame, ErrorCode code, const CefString& text, const CefString& url) override {
        if (!frame->IsMain() || code == ERR_ABORTED) return;
        sm::Json j = sm::Json::Object();
        j.Set("ev", "error").Set("id", id_).Set("code", (int)code).Set("text", Str(text)).Set("url", Str(url));
        Send(j);
    }
    void OnLoadEnd(CefRefPtr<CefBrowser>, CefRefPtr<CefFrame> frame, int status) override {
        if (!frame->IsMain()) return;
        sm::Json j = sm::Json::Object();
        j.Set("ev", "loaded").Set("id", id_).Set("status", status).Set("url", Str(frame->GetURL()));
        Send(j);
    }
    void OnAddressChange(CefRefPtr<CefBrowser>, CefRefPtr<CefFrame> frame, const CefString& url) override {
        if (!frame->IsMain()) return;
        sm::Json j = sm::Json::Object();
        j.Set("ev", "url").Set("id", id_).Set("url", Str(url));
        Send(j);
    }
    void OnTitleChange(CefRefPtr<CefBrowser>, const CefString& title) override {
        sm::Json j = sm::Json::Object();
        j.Set("ev", "title").Set("id", id_).Set("title", Str(title));
        Send(j);
    }
    bool OnCursorChange(CefRefPtr<CefBrowser>, CefCursorHandle, cef_cursor_type_t type, const CefCursorInfo&) override {
        if ((int)type != cursor_) {
            cursor_ = (int)type;
            sm::Json j = sm::Json::Object();
            j.Set("ev", "cursor").Set("id", id_).Set("type", cursor_);
            Send(j);
        }
        return true;
    }
    void OnFullscreenModeChange(CefRefPtr<CefBrowser>, bool fullscreen) override {
        sm::Json j = sm::Json::Object();
        j.Set("ev", "fullscreen").Set("id", id_).Set("on", fullscreen);
        Send(j);
    }
    bool OnConsoleMessage(CefRefPtr<CefBrowser>, cef_log_severity_t, const CefString&, const CefString&, int) override {
        return true;   // pages' console output isn't ours to log
    }

    // ---- navigation policy ----
    bool OnBeforeBrowse(CefRefPtr<CefBrowser>, CefRefPtr<CefFrame> frame, CefRefPtr<CefRequest> request, bool, bool is_redirect) override {
        std::string url = Str(request->GetURL());
        std::string why = sm::NavigationRefusal(url, frame->IsMain(), g_private_ok);
        if (why.empty() && frame->IsMain()) {
            std::lock_guard<std::mutex> lk(policy_mu_);
            if (!allow_.empty() && !sm::WhitelistAllows(allow_, url)) why = "whitelist";
        }
        if (why.empty()) return false;
        if (why == "slurl") Report("slurl", url, frame->IsMain() ? "main" : "frame");
        else Report("blocked", url, why + (is_redirect ? " (redirect)" : ""));
        return true;
    }
    bool OnOpenURLFromTab(CefRefPtr<CefBrowser>, CefRefPtr<CefFrame>, const CefString& url, CefRequestHandler::WindowOpenDisposition, bool user_gesture) override {
        Report("popup", Str(url), user_gesture ? "gesture" : "script");
        return true;
    }
    CefRefPtr<CefResourceRequestHandler> GetResourceRequestHandler(CefRefPtr<CefBrowser>, CefRefPtr<CefFrame>, CefRefPtr<CefRequest>, bool,
                                                                   bool, const CefString&, bool&) override {
        return this;
    }
    ReturnValue OnBeforeResourceLoad(CefRefPtr<CefBrowser>, CefRefPtr<CefFrame>, CefRefPtr<CefRequest> request, CefRefPtr<CefCallback>) override {
        // every request a page makes (scripts, images, fetch, XHR, websockets' handshakes), on the IO thread
        std::string url = Str(request->GetURL());
        if (sm::ResourceRefusal(url, g_private_ok).empty()) return RV_CONTINUE;
        return RV_CANCEL;
    }
    bool OnCertificateError(CefRefPtr<CefBrowser>, cef_errorcode_t, const CefString&, CefRefPtr<CefSSLInfo>, CefRefPtr<CefCallback>) override {
        return false;   // cancelled: OnLoadError reports it
    }
    void OnRenderProcessTerminated(CefRefPtr<CefBrowser>, TerminationStatus status, int code, const CefString& text) override {
        sm::Json j = sm::Json::Object();
        j.Set("ev", "crashed").Set("id", id_).Set("status", (int)status).Set("code", code).Set("text", Str(text));
        Send(j);
    }

    // ---- nothing a page asks for is granted ----
    bool OnRequestMediaAccessPermission(CefRefPtr<CefBrowser>, CefRefPtr<CefFrame>, const CefString&, uint32_t,
                                        CefRefPtr<CefMediaAccessCallback> callback) override {
        callback->Cancel();
        return true;
    }
    bool OnShowPermissionPrompt(CefRefPtr<CefBrowser>, uint64_t, const CefString&, uint32_t, CefRefPtr<CefPermissionPromptCallback> callback) override {
        callback->Continue(CEF_PERMISSION_RESULT_DENY);
        return true;
    }
    bool OnFileDialog(CefRefPtr<CefBrowser>, FileDialogMode, const CefString&, const CefString&, const std::vector<CefString>&,
                      const std::vector<CefString>&, const std::vector<CefString>&, CefRefPtr<CefFileDialogCallback> callback) override {
        callback->Cancel();
        return true;
    }
    bool CanDownload(CefRefPtr<CefBrowser>, const CefString& url, const CefString&) override {
        Report("blocked", Str(url), "download");
        return false;
    }
    bool OnBeforeDownload(CefRefPtr<CefBrowser>, CefRefPtr<CefDownloadItem>, const CefString&, CefRefPtr<CefBeforeDownloadCallback>) override {
        return true;   // handled: never continued
    }
    bool OnJSDialog(CefRefPtr<CefBrowser>, const CefString&, JSDialogType, const CefString&, const CefString&, CefRefPtr<CefJSDialogCallback>,
                    bool& suppress) override {
        suppress = true;
        return false;
    }
    bool OnBeforeUnloadDialog(CefRefPtr<CefBrowser>, const CefString&, bool, CefRefPtr<CefJSDialogCallback> callback) override {
        callback->Continue(true, CefString());
        return true;
    }
    void OnBeforeContextMenu(CefRefPtr<CefBrowser>, CefRefPtr<CefFrame>, CefRefPtr<CefContextMenuParams>, CefRefPtr<CefMenuModel> model) override {
        model->Clear();
    }

    // ---- reading the page for the assistant: Runtime.evaluate through the DevTools protocol, no devtools window ----
    void Eval(int req, const std::string& js) {
        if (!browser_) return;
        CefRefPtr<CefDictionaryValue> params = CefDictionaryValue::Create();
        params->SetString("expression", js);
        params->SetBool("returnByValue", true);
        params->SetBool("awaitPromise", true);
        int mid = browser_->GetHost()->ExecuteDevToolsMethod(0, "Runtime.evaluate", params);
        if (mid > 0) evals_[mid] = req;
        else {
            sm::Json j = sm::Json::Object();
            j.Set("ev", "eval").Set("id", id_).Set("req", req).Set("ok", false).Set("result", "not sent");
            Send(j);
        }
    }
    bool OnDevToolsMessage(CefRefPtr<CefBrowser>, const void*, size_t) override { return false; }
    void OnDevToolsMethodResult(CefRefPtr<CefBrowser>, int message_id, bool success, const void* result, size_t size) override {
        auto it = evals_.find(message_id);
        if (it == evals_.end()) return;
        int req = it->second;
        evals_.erase(it);
        std::string body((const char*)result, std::min<size_t>(size, 1u << 20));
        sm::Json j = sm::Json::Object();
        j.Set("ev", "eval").Set("id", id_).Set("req", req).Set("ok", success).Set("result", body);
        Send(j);
    }

    void SetFps(int fps) {
        fps_ = std::max(1, std::min(60, fps));
        if (browser_) browser_->GetHost()->SetWindowlessFrameRate(fps_);
    }
    void SetHidden(bool hidden) {
        hidden_ = hidden;
        if (browser_) browser_->GetHost()->WasHidden(hidden);
    }

    bool closing = false;

private:
    void Report(const char* ev, const std::string& url, const std::string& why) {
        sm::Json j = sm::Json::Object();
        j.Set("ev", ev).Set("id", id_).Set("url", url).Set("why", why);
        Send(j);
    }

    // a <select>'s list (a popup widget off screen) drawn over the page
    void Composite() {
        float s = EffectiveScale();
        int px = (int)(popup_rect_.x * s), py = (int)(popup_rect_.y * s);
        std::vector<uint8_t> out = view_;
        for (int y = 0; y < popup_h_; y++) {
            int ty = py + y;
            if (ty < 0 || ty >= view_h_) continue;
            int x0 = std::max(0, px), x1 = std::min(view_w_, px + popup_w_);
            if (x1 <= x0) continue;
            memcpy(&out[((size_t)ty * view_w_ + x0) * 4], &popup_[((size_t)y * popup_w_ + (x0 - px)) * 4], (size_t)(x1 - x0) * 4);
        }
        shm_.Publish(out.data(), view_w_, view_h_, view_w_ * 4, 0, 0, 0, 0);
    }

    int id_;
    int w_, h_;
    float scale_;
    bool audio_;
    int fps_ = 30;
    bool hidden_ = false;
    int cursor_ = -1;
    int audio_rate_ = 48000, audio_channels_ = 2;
    sm::Surface shm_;
    CefRefPtr<CefBrowser> browser_;
    CefRefPtr<CefRegistration> devtools_;
    std::map<int, int> evals_;
    std::mutex policy_mu_;
    std::vector<std::string> allow_;
    std::vector<uint8_t> view_, popup_;
    int view_w_ = 0, view_h_ = 0, popup_w_ = 0, popup_h_ = 0;
    bool popup_shown_ = false;
    CefRect popup_rect_;

    IMPLEMENT_REFCOUNTING(WebView);
};

std::map<int, CefRefPtr<WebView>> g_views;   // UI thread only

void WebView::OnBeforeClose(CefRefPtr<CefBrowser>) {
    devtools_ = nullptr;
    browser_ = nullptr;
    shm_.Close();
    sm::Json j = sm::Json::Object();
    j.Set("ev", "closed").Set("id", id_);
    Send(j);
    g_views.erase(id_);
    if (g_quitting && g_views.empty()) CefQuitMessageLoop();
}

std::vector<std::string> Strings(const sm::Json& a) {
    std::vector<std::string> out;
    if (a.kind != sm::Json::kArr) return out;
    for (auto& v : a.a)
        if (v.kind == sm::Json::kStr && out.size() < 64) out.push_back(v.s);
    return out;
}

uint32_t Mods(const sm::Json& c) {
    uint32_t m = 0;
    int mods = c.Int("mods");
    if (mods & 1) m |= EVENTFLAG_SHIFT_DOWN;
    if (mods & 2) m |= EVENTFLAG_CONTROL_DOWN;
    if (mods & 4) m |= EVENTFLAG_ALT_DOWN;
    return m;
}

void Quit() {
    g_quitting = true;
    if (g_views.empty()) {
        CefQuitMessageLoop();
        return;
    }
    for (auto& kv : g_views)
        if (kv.second->browser()) kv.second->browser()->GetHost()->CloseBrowser(true);
}

// A control line, on the UI thread.
void Handle(const std::string& line) {
    sm::Json c = sm::Json::Parse(line);
    std::string op = c.Str("op");
    int id = c.Int("id");
    if (op == "quit") { Quit(); return; }
    if (op == "open") {
        if (g_views.count(id)) return;
        CefRefPtr<WebView> v = new WebView(id, c.Int("w", 1024), c.Int("h", 1024), (float)c.Num("scale", 1), c.Flag("audio", true), Strings(c["allow"]));
        if (!v->surface().Open(c.Str("shm"))) {
            sm::Json j = sm::Json::Object();
            j.Set("ev", "error").Set("id", id).Set("code", -1).Set("text", "the surface's section didn't open");
            Send(j);
            return;
        }
        g_views[id] = v;
        v->SetFps(c.Int("fps", 30));
        CefWindowInfo wi;
        wi.SetAsWindowless(nullptr);
        CefBrowserSettings bs;
        bs.windowless_frame_rate = std::max(1, std::min(60, c.Int("fps", 30)));
        bs.background_color = CefColorSetARGB(255, 255, 255, 255);
        std::string url = c.Str("url", "about:blank");
        // the client checks the URL first; this is the same rule again, so a bad one opens blank and says why
        std::string why = sm::NavigationRefusal(url, true, g_private_ok);
        if (why.empty() && c["allow"].kind == sm::Json::kArr && !c["allow"].a.empty() && !sm::WhitelistAllows(Strings(c["allow"]), url)) why = "whitelist";
        if (!why.empty()) {
            sm::Json j = sm::Json::Object();
            j.Set("ev", "blocked").Set("id", id).Set("url", url).Set("why", why);
            Send(j);
            url = "about:blank";
        }
        CefBrowserHost::CreateBrowser(wi, v, url, bs, nullptr, nullptr);
        return;
    }
    auto it = g_views.find(id);
    if (it == g_views.end()) return;
    CefRefPtr<WebView> v = it->second;
    CefRefPtr<CefBrowser> b = v->browser();
    if (op == "close") {
        if (b && !v->closing) { v->closing = true; b->GetHost()->CloseBrowser(true); }
        return;
    }
    if (op == "allow") { v->SetAllow(Strings(c["allow"])); return; }
    if (op == "size") { v->Resize(c.Int("w", 1024), c.Int("h", 1024), (float)c.Num("scale", 1)); return; }
    if (op == "fps") { v->SetFps(c.Int("fps", 30)); return; }
    if (op == "hidden") { v->SetHidden(c.Flag("hidden")); return; }
    if (op == "audio") { v->SetAudio(c.Flag("on", true)); return; }
    if (!b) return;
    auto host = b->GetHost();
    if (op == "navigate") {
        std::string url = c.Str("url");
        std::string why = sm::NavigationRefusal(url, true, g_private_ok);
        if (!why.empty()) {
            sm::Json j = sm::Json::Object();
            j.Set("ev", "blocked").Set("id", id).Set("url", url).Set("why", why);
            Send(j);
            return;
        }
        b->GetMainFrame()->LoadURL(url);
    } else if (op == "back") b->GoBack();
    else if (op == "forward") b->GoForward();
    else if (op == "reload") b->Reload();
    else if (op == "stop") b->StopLoad();
    else if (op == "mouse") {
        CefMouseEvent e;
        e.x = c.Int("x");
        e.y = c.Int("y");
        e.modifiers = Mods(c);
        std::string kind = c.Str("kind");
        int button = c.Int("button");
        auto bt = button == 2 ? MBT_RIGHT : button == 1 ? MBT_MIDDLE : MBT_LEFT;
        if (kind == "down" || kind == "up") {
            if (button == 0) e.modifiers |= EVENTFLAG_LEFT_MOUSE_BUTTON;
            host->SetFocus(true);
            host->SendMouseClickEvent(e, bt, kind == "up", std::max(1, c.Int("clicks", 1)));
        } else if (kind == "move") {
            if (c.Flag("held")) e.modifiers |= EVENTFLAG_LEFT_MOUSE_BUTTON;
            host->SendMouseMoveEvent(e, false);
        } else if (kind == "leave") {
            host->SendMouseMoveEvent(e, true);
        } else if (kind == "wheel") {
            host->SendMouseWheelEvent(e, c.Int("dx"), c.Int("dy"));
        }
    } else if (op == "key") {
        CefKeyEvent e;
        std::string kind = c.Str("kind");
        e.type = kind == "up" ? KEYEVENT_KEYUP : kind == "char" ? KEYEVENT_CHAR : KEYEVENT_RAWKEYDOWN;
        e.windows_key_code = c.Int("code");
        e.native_key_code = c.Int("code");
        e.character = (char16_t)c.Int("char");
        e.unmodified_character = e.character;
        e.modifiers = Mods(c);
        host->SetFocus(true);
        host->SendKeyEvent(e);
    } else if (op == "text") {
        host->SetFocus(true);
        std::wstring w = sm::Wide(c.Str("text"));
        for (wchar_t ch : w) {
            if (ch == L'\r') continue;
            CefKeyEvent e;
            e.type = KEYEVENT_CHAR;
            e.windows_key_code = ch == L'\n' ? VK_RETURN : ch;
            e.character = (char16_t)(ch == L'\n' ? L'\r' : ch);
            e.unmodified_character = e.character;
            host->SendKeyEvent(e);
        }
    } else if (op == "focus") {
        host->SetFocus(c.Flag("on", true));
    } else if (op == "eval") {
        v->Eval(c.Int("req"), c.Str("js"));
    } else if (op == "zoom") {
        host->SetZoomLevel(c.Num("level", 0));
    }
}

// ---------------------------------------------------------------------------------------------------------------

class App : public CefApp, public CefBrowserProcessHandler {
public:
    CefRefPtr<CefBrowserProcessHandler> GetBrowserProcessHandler() override { return this; }

    void OnBeforeCommandLineProcessing(const CefString& process_type, CefRefPtr<CefCommandLine> cl) override {
        if (!process_type.empty()) return;
        cl->AppendSwitchWithValue("autoplay-policy", "no-user-gesture-required");   // as the viewer's pages: boards autoplay
        cl->AppendSwitch("disable-extensions");
        cl->AppendSwitch("disable-component-update");
        cl->AppendSwitch("disable-background-networking");
        cl->AppendSwitch("disable-default-apps");
        cl->AppendSwitch("disable-print-preview");
        cl->AppendSwitch("deny-permission-prompts");
        cl->AppendSwitch("no-first-run");
        cl->AppendSwitchWithValue("renderer-process-limit", "6");
        cl->AppendSwitchWithValue("disable-features", "MediaRouter,DialMediaRouteProvider,GlobalMediaControls,HardwareMediaKeyHandling");
    }

    void OnContextInitialized() override {
        sm::Json j = sm::Json::Object();
        j.Set("ev", "ready").Set("cef", CEF_VERSION).Set("sandbox", sandboxed);
        Send(j);
    }

    bool sandboxed = false;

    IMPLEMENT_REFCOUNTING(App);
};

int RunMain(HINSTANCE instance, void* sandbox_info) {
    CefMainArgs args(instance);
    CefRefPtr<App> app = new App();
    int code = CefExecuteProcess(args, app, sandbox_info);
    if (code >= 0) return code;   // a sub-process (renderer, GPU, utility): done

    CefRefPtr<CefCommandLine> cl = CefCommandLine::CreateCommandLine();
    cl->InitFromString(::GetCommandLineW());
    std::string pipe = Str(cl->GetSwitchValue("sojourn-pipe"));
    std::string profile = Str(cl->GetSwitchValue("sojourn-profile"));
    g_ua_product = Str(cl->GetSwitchValue("sojourn-ua"));
    g_private_ok = cl->HasSwitch("sojourn-private-ok");
    if (pipe.empty() || profile.empty()) return 2;
    if (!g_pipe.Connect(sm::Wide(pipe))) return 3;

    CefSettings s;
    s.no_sandbox = sandbox_info == nullptr;
    app->sandboxed = sandbox_info != nullptr;
    s.windowless_rendering_enabled = true;
    s.multi_threaded_message_loop = false;
    s.persist_session_cookies = true;
    s.background_color = CefColorSetARGB(255, 255, 255, 255);
    CefString(&s.root_cache_path) = profile;
    CefString(&s.cache_path) = profile + "\\default";
    CefString(&s.log_file) = profile + "\\cef.log";
    s.log_severity = LOGSEVERITY_WARNING;
    CefString(&s.locale) = "en-US";
    // Chrome's own (reduced) user agent with Sojourn's product after it, as the viewer adds SecondLife/<version>
    std::string ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/" +
                     std::to_string(CHROME_VERSION_MAJOR) + ".0.0.0 Safari/537.36";
    if (!g_ua_product.empty()) ua += " " + g_ua_product;
    CefString(&s.user_agent) = ua;
    if (!CefInitialize(args, s, app, sandbox_info)) return CefGetExitCode();

    g_pipe.Start([](const std::string& line) { CefPostTask(TID_UI, base::BindOnce(&Handle, line)); },
                 [] { CefPostTask(TID_UI, base::BindOnce(&Quit)); });
    CefRunMessageLoop();
    CefShutdown();
    return 0;
}

}  // namespace

CEF_BOOTSTRAP_EXPORT int RunWinMain(HINSTANCE instance, LPWSTR, int, void* sandbox_info, cef_version_info_t*) {
    return RunMain(instance, sandbox_info);
}
