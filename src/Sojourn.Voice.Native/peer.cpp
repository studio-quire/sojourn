// Peer connections, set up the way the viewer sets them up for SL voice (llwebrtc.cpp): Unified Plan, UDP ports
// 60000-60100, the client offers, one sendrecv Opus track "SLAudio" in stream "SLStream" starting disabled, an
// ordered data channel "SLData" made before the offer, and the offer's Opus lines edited to 48 kHz stereo with
// in-band FEC. Candidates gathered before the answer are held and sent after it, in SL's trickle form.
#include "internal.h"

#include <map>
#include <sstream>
#include <functional>

namespace sv {

class Peer;
static std::mutex peers_mu;
// never destroyed: at process exit, PeerConnection destructors would block on stopped threads
static auto& peers = *new std::map<int64_t, webrtc::scoped_refptr<Peer>>();
static std::atomic<int64_t> next_id{1};

class SdpCreated : public webrtc::CreateSessionDescriptionObserver {
public:
    std::function<void(std::unique_ptr<webrtc::SessionDescriptionInterface>)> ok;
    std::function<void(webrtc::RTCError)> fail;
    void OnSuccess(webrtc::SessionDescriptionInterface* d) override {
        ok(std::unique_ptr<webrtc::SessionDescriptionInterface>(d));
    }
    void OnFailure(webrtc::RTCError e) override { fail(std::move(e)); }
};

class LocalSet : public webrtc::SetLocalDescriptionObserverInterface {
public:
    std::function<void(webrtc::RTCError)> done;
    void OnSetLocalDescriptionComplete(webrtc::RTCError e) override { done(std::move(e)); }
};

class RemoteSet : public webrtc::SetRemoteDescriptionObserverInterface {
public:
    std::function<void(webrtc::RTCError)> done;
    void OnSetRemoteDescriptionComplete(webrtc::RTCError e) override { done(std::move(e)); }
};

// SL's trickle form of a candidate (llwebrtc.cpp iceCandidateToTrickleString).
static std::string TrickleString(const webrtc::Candidate& c) {
    std::ostringstream o;
    o << c.foundation() << " " << c.component() << " " << c.protocol() << " " << c.priority() << " "
      << c.address().ipaddr().ToString() << " " << c.address().PortAsString() << " typ ";
    switch (c.type()) {
        case webrtc::IceCandidateType::kHost: o << "host"; break;
        case webrtc::IceCandidateType::kSrflx: o << "srflx"; break;
        case webrtc::IceCandidateType::kRelay: o << "relay"; break;
        case webrtc::IceCandidateType::kPrflx: o << "prflx"; break;
        default: o << "host"; break;
    }
    if (c.type() != webrtc::IceCandidateType::kHost)
        o << " raddr " << c.related_address().ipaddr().ToString() << " rport " << c.related_address().PortAsString();
    if (c.protocol() == "tcp") o << " tcptype " << c.tcptype();
    return o.str();
}

// The viewer's offer edit: opus/48000/2 and an fmtp line asking for 48 kHz stereo with FEC. (The viewer appends
// its fmtp line to the original one; we replace it, which says the same thing once.)
static std::string EditOffer(const std::string& sdp) {
    std::istringstream in(sdp);
    std::ostringstream out;
    std::string line, opus;
    while (std::getline(in, line)) {
        if (!line.empty() && line.back() == '\r') line.pop_back();
        int pt = 0, rate = 0;
        if (sscanf(line.c_str(), "a=rtpmap:%d opus/%d/2", &pt, &rate) == 2) {
            opus = std::to_string(pt);
            out << "a=rtpmap:" << opus << " opus/48000/2\r\n";
        } else if (!opus.empty() && line.rfind("a=fmtp:" + opus + " ", 0) == 0) {
            out << "a=fmtp:" << opus
                << " minptime=10;useinbandfec=1;stereo=1;sprop-stereo=1;maxplaybackrate=48000;"
                   "sprop-maxplaybackrate=48000;sprop-maxcapturerate=48000\r\n";
        } else {
            out << line << "\r\n";
        }
    }
    return out.str();
}

class Peer : public webrtc::PeerConnectionObserver, public webrtc::DataChannelObserver, public webrtc::RefCountInterface {
public:
    int64_t id = 0;
    webrtc::scoped_refptr<webrtc::PeerConnectionInterface> pc;
    webrtc::scoped_refptr<webrtc::DataChannelInterface> dc;
    webrtc::scoped_refptr<webrtc::AudioTrackInterface> track;
    bool answer_set = false;  // signalling thread only
    bool answerer = false;
    bool closed = false;
    std::vector<std::unique_ptr<webrtc::IceCandidate>> held;
    webrtc::PeerConnectionInterface::PeerConnectionState state = webrtc::PeerConnectionInterface::PeerConnectionState::kNew;
    uint32_t disconnects = 0;

    bool Create(const std::string& ice) {
        webrtc::PeerConnectionInterface::RTCConfiguration config;
        std::istringstream in(ice);
        std::string url;
        while (std::getline(in, url)) {
            if (!url.empty() && url.back() == '\r') url.pop_back();
            if (url.empty()) continue;
            webrtc::PeerConnectionInterface::IceServer s;
            s.urls.push_back(url);
            config.servers.push_back(s);
        }
        config.sdp_semantics = webrtc::SdpSemantics::kUnifiedPlan;
        config.set_min_port(60000);
        config.set_max_port(60100);
        webrtc::PeerConnectionDependencies deps(this);
        auto r = Factory()->CreatePeerConnectionOrError(config, std::move(deps));
        if (!r.ok()) {
            Emit(id, SV_EV_ERROR, r.error().message(), nullptr, 0);
            return false;
        }
        pc = r.MoveValue();

        if (!answerer) {
            webrtc::DataChannelInit init;
            init.ordered = true;
            auto d = pc->CreateDataChannelOrError("SLData", &init);
            if (d.ok()) {
                dc = d.MoveValue();
                dc->RegisterObserver(this);
            }
        }

        webrtc::AudioOptions opts;  // our own APM does the processing; the engine has none
        opts.echo_cancellation = false;
        opts.auto_gain_control = false;
        opts.noise_suppression = false;
        opts.highpass_filter = false;
        opts.init_recording_on_send = false;
        auto source = Factory()->CreateAudioSource(opts);
        track = Factory()->CreateAudioTrack("SLAudio", source.get());
        track->set_enabled(false);
        auto added = pc->AddTrack(track, {"SLStream"});
        if (!added.ok()) {
            Emit(id, SV_EV_ERROR, added.error().message(), nullptr, 0);
            return false;
        }
        return true;
    }

    void Offer() {
        auto obs = webrtc::make_ref_counted<SdpCreated>();
        webrtc::scoped_refptr<Peer> self(this);
        obs->ok = [self](std::unique_ptr<webrtc::SessionDescriptionInterface> d) {
            std::string sdp;
            d->ToString(&sdp);
            std::string edited = EditOffer(sdp);
            auto local = webrtc::CreateSessionDescription(webrtc::SdpType::kOffer, edited);
            if (!local) {
                Emit(self->id, SV_EV_ERROR, "edited offer didn't parse", nullptr, 0);
                Emit(self->id, SV_EV_RENEGOTIATE, nullptr, nullptr, 0);
                return;
            }
            auto set = webrtc::make_ref_counted<LocalSet>();
            set->done = [self](webrtc::RTCError e) {
                if (!e.ok()) Emit(self->id, SV_EV_ERROR, e.message(), nullptr, 0);
            };
            self->pc->SetLocalDescription(std::move(local), set);
            Emit(self->id, SV_EV_OFFER, edited.c_str(), nullptr, 0);
        };
        obs->fail = [self](webrtc::RTCError e) {
            Emit(self->id, SV_EV_ERROR, e.message(), nullptr, 0);
            Emit(self->id, SV_EV_RENEGOTIATE, nullptr, nullptr, 0);
        };
        pc->CreateOffer(obs.get(), webrtc::PeerConnectionInterface::RTCOfferAnswerOptions());
    }

    // Tests: answer a local offer.
    void AcceptOffer(const std::string& sdp) {
        webrtc::scoped_refptr<Peer> self(this);
        auto remote = webrtc::CreateSessionDescription(webrtc::SdpType::kOffer, sdp);
        if (!remote) {
            Emit(id, SV_EV_ERROR, "offer didn't parse", nullptr, 0);
            return;
        }
        auto rs = webrtc::make_ref_counted<RemoteSet>();
        rs->done = [self](webrtc::RTCError e) {
            if (!e.ok()) {
                Emit(self->id, SV_EV_ERROR, e.message(), nullptr, 0);
                return;
            }
            self->answer_set = true;
            self->FlushHeld();
            auto obs = webrtc::make_ref_counted<SdpCreated>();
            obs->ok = [self](std::unique_ptr<webrtc::SessionDescriptionInterface> d) {
                std::string a;
                d->ToString(&a);
                auto set = webrtc::make_ref_counted<LocalSet>();
                set->done = [self](webrtc::RTCError e) {
                    if (!e.ok()) Emit(self->id, SV_EV_ERROR, e.message(), nullptr, 0);
                };
                self->pc->SetLocalDescription(std::move(d), set);
                Emit(self->id, SV_EV_ANSWER, a.c_str(), nullptr, 0);
            };
            obs->fail = [self](webrtc::RTCError e) { Emit(self->id, SV_EV_ERROR, e.message(), nullptr, 0); };
            self->pc->CreateAnswer(obs.get(), webrtc::PeerConnectionInterface::RTCOfferAnswerOptions());
        };
        pc->SetRemoteDescription(std::move(remote), rs);
    }

    void Answer(const std::string& sdp) {
        webrtc::scoped_refptr<Peer> self(this);
        auto remote = webrtc::CreateSessionDescription(webrtc::SdpType::kAnswer, sdp);
        if (!remote) {
            Emit(id, SV_EV_ERROR, "answer didn't parse", nullptr, 0);
            Emit(id, SV_EV_RENEGOTIATE, nullptr, nullptr, 0);
            return;
        }
        auto rs = webrtc::make_ref_counted<RemoteSet>();
        rs->done = [self](webrtc::RTCError e) {
            if (!e.ok()) {
                Emit(self->id, SV_EV_ERROR, e.message(), nullptr, 0);
                Emit(self->id, SV_EV_RENEGOTIATE, nullptr, nullptr, 0);
                return;
            }
            self->answer_set = true;
            self->FlushHeld();
        };
        pc->SetRemoteDescription(std::move(remote), rs);
    }

    void FlushHeld() {
        for (auto& c : held) SendCandidate(c.get());
        held.clear();
        if (pc && pc->ice_gathering_state() == webrtc::PeerConnectionInterface::kIceGatheringComplete)
            Emit(id, SV_EV_GATHERED, nullptr, nullptr, 0);
    }

    void SendCandidate(const webrtc::IceCandidate* c) {
        std::string s = answerer ? c->ToString() : TrickleString(c->candidate());
        std::string mid = c->sdp_mid();
        Emit(id, SV_EV_CANDIDATE, s.c_str(), mid.c_str(), c->sdp_mline_index());
    }

    void Close() {
        if (closed) return;
        closed = true;
        if (dc) {
            dc->UnregisterObserver();
            dc->Close();
        }
        if (pc) pc->Close();
        dc = nullptr;
        track = nullptr;
        pc = nullptr;
    }

    // --- PeerConnectionObserver (signalling thread) ---
    void OnSignalingChange(webrtc::PeerConnectionInterface::SignalingState) override {}
    void OnDataChannel(webrtc::scoped_refptr<webrtc::DataChannelInterface> channel) override {
        if (dc) dc->UnregisterObserver();
        dc = channel;
        dc->RegisterObserver(this);
        if (dc->state() == webrtc::DataChannelInterface::kOpen) Emit(id, SV_EV_DATA_OPEN, nullptr, nullptr, 0);
    }
    void OnIceGatheringChange(webrtc::PeerConnectionInterface::IceGatheringState s) override {
        if (s == webrtc::PeerConnectionInterface::kIceGatheringComplete && answer_set)
            Emit(id, SV_EV_GATHERED, nullptr, nullptr, 0);
    }
    void OnIceCandidate(const webrtc::IceCandidate* c) override {
        if (!c) return;
        if (answer_set)
            SendCandidate(c);
        else
            held.push_back(webrtc::CreateIceCandidate(c->sdp_mid(), c->sdp_mline_index(), c->candidate()));
    }
    void OnConnectionChange(webrtc::PeerConnectionInterface::PeerConnectionState s) override {
        state = s;
        Emit(id, SV_EV_STATE, nullptr, nullptr, (int)s);
        using S = webrtc::PeerConnectionInterface::PeerConnectionState;
        if (s == S::kFailed) {
            Emit(id, SV_EV_RENEGOTIATE, nullptr, nullptr, 0);
        } else if (s == S::kDisconnected) {
            // the viewer waits 10 s for the connection to come back on its own before renegotiating
            uint32_t n = ++disconnects;
            webrtc::scoped_refptr<Peer> self(this);
            SignalingThread()->PostDelayedTask(
                [self, n]() {
                    if (!self->closed && n == self->disconnects && self->state == S::kDisconnected)
                        Emit(self->id, SV_EV_RENEGOTIATE, nullptr, nullptr, 0);
                },
                webrtc::TimeDelta::Millis(10000));
        }
    }

    // --- DataChannelObserver ---
    void OnStateChange() override {
        if (!dc) return;
        auto st = dc->state();
        if (st == webrtc::DataChannelInterface::kOpen) Emit(id, SV_EV_DATA_OPEN, nullptr, nullptr, 0);
        if (st == webrtc::DataChannelInterface::kClosed) Emit(id, SV_EV_DATA_CLOSED, nullptr, nullptr, 0);
    }
    void OnMessage(const webrtc::DataBuffer& b) override {
        std::string s((const char*)b.data.cdata(), b.size());
        Emit(id, SV_EV_DATA, s.c_str(), nullptr, b.binary ? 1 : 0);
    }
};

static webrtc::scoped_refptr<Peer> Find(int64_t id) {
    std::lock_guard<std::mutex> lk(peers_mu);
    auto it = peers.find(id);
    return it == peers.end() ? nullptr : it->second;
}

template <class F>
static void OnSignaling(F&& f) {
    if (auto* t = SignalingThread()) t->PostTask(std::forward<F>(f));
}

void PeersCloseAll() {
    std::map<int64_t, webrtc::scoped_refptr<Peer>> all;
    {
        std::lock_guard<std::mutex> lk(peers_mu);
        all.swap(peers);
    }
    if (auto* t = SignalingThread())
        t->BlockingCall([&]() {
            for (auto& [id, p] : all) p->Close();
        });
}

static int64_t NewPeer(const char* ice, const char* offer) {
    if (!Factory()) return -1;
    auto p = webrtc::make_ref_counted<Peer>();
    p->id = next_id++;
    p->answerer = offer != nullptr;
    {
        std::lock_guard<std::mutex> lk(peers_mu);
        peers[p->id] = p;
    }
    std::string ice_s = ice ? ice : "";
    std::string offer_s = offer ? offer : "";
    OnSignaling([p, ice_s, offer_s]() {
        if (!p->Create(ice_s)) {
            Emit(p->id, SV_EV_RENEGOTIATE, nullptr, nullptr, 0);
            return;
        }
        if (p->answerer)
            p->AcceptOffer(offer_s);
        else
            p->Offer();
    });
    return p->id;
}

}  // namespace sv

using namespace sv;

SV_API int64_t sv_pc_new(const char* ice) { return NewPeer(ice, nullptr); }
SV_API int64_t sv_pc_new_answerer(const char* ice, const char* offer) { return NewPeer(ice, offer ? offer : ""); }

SV_API void sv_pc_answer(int64_t id, const char* sdp) {
    auto p = Find(id);
    if (!p || !sdp) return;
    std::string s = sdp;
    OnSignaling([p, s]() {
        if (p->pc) p->Answer(s);
    });
}

SV_API void sv_pc_add_candidate(int64_t id, const char* mid, int mline, const char* cand) {
    auto p = Find(id);
    if (!p || !cand) return;
    std::string m = mid ? mid : "", c = cand;
    OnSignaling([p, m, mline, c]() {
        if (!p->pc) return;
        webrtc::SdpParseError err;
        std::unique_ptr<webrtc::IceCandidate> ic(webrtc::CreateIceCandidate(m, mline, c, &err));
        if (!ic) {
            Emit(p->id, SV_EV_ERROR, ("bad candidate: " + err.description).c_str(), nullptr, 0);
            return;
        }
        p->pc->AddIceCandidate(std::move(ic), [](webrtc::RTCError) {});
    });
}

SV_API void sv_pc_send(int64_t id, const char* text) {
    auto p = Find(id);
    if (!p || !text) return;
    std::string s = text;
    OnSignaling([p, s]() {
        if (p->dc && p->dc->state() == webrtc::DataChannelInterface::kOpen)
            p->dc->SendAsync(webrtc::DataBuffer(s), [](webrtc::RTCError) {});
    });
}

SV_API void sv_pc_mute_send(int64_t id, int muted) {
    auto p = Find(id);
    if (!p) return;
    bool enable = muted == 0;
    OnSignaling([p, enable]() {
        if (p->track) p->track->set_enabled(enable);
    });
}

SV_API void sv_pc_receive_volume(int64_t id, float v) {
    auto p = Find(id);
    if (!p) return;
    OnSignaling([p, v]() {
        if (!p->pc) return;
        for (auto& r : p->pc->GetReceivers()) {
            auto t = r->track();
            if (t && t->kind() == webrtc::MediaStreamTrackInterface::kAudioKind)
                static_cast<webrtc::AudioTrackInterface*>(t.get())->GetSource()->SetVolume(v);
        }
    });
}

SV_API void sv_pc_close(int64_t id) {
    webrtc::scoped_refptr<Peer> p;
    {
        std::lock_guard<std::mutex> lk(peers_mu);
        auto it = peers.find(id);
        if (it == peers.end()) return;
        p = it->second;
        peers.erase(it);
    }
    OnSignaling([p]() { p->Close(); });
}
