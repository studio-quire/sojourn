"""Checks the assistant's providers (assistant/provider.py) under the real harness (auto mode, deny rules, hooks).

  python tools/provider_check.py                 against tools/provider_mock.py with decoy keys: nothing real is
                                                 sent and nothing spent. Kept keys, the proxy, the model ids on the
                                                 wire, what's left out for other makers' models, refusals, and that a
                                                 key never reaches a request or a shell's environment.
  python tools/provider_check.py real KIND [MODEL]
                                                 with the person's own kept key (KIND anthropic or openrouter; MODEL
                                                 an OpenRouter id, "claude" by default): the key checked, then one
                                                 short request and one picture through it (a few cents at most).

Run with the venv's python. Mock mode starts the mock on a free port and keeps decoy keys in a folder of its own
(local/assistant/provider-test, deleted after), so the person's kept keys are never touched.
"""
import asyncio
import base64
import json
import os
import shutil
import socket
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "assistant"))
os.environ.pop("CLAUDE_CODE_PLUGIN_DIRS", None)
for _k in ("ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL"):
    os.environ.pop(_k, None)

def png(w=8, h=8, rgb=(220, 30, 30)):
    """A small solid-red PNG, for the picture checks."""
    import struct
    import zlib

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))
    rows = b"".join(b"\x00" + bytes(rgb) * w for _ in range(h))
    return base64.b64encode(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0))
                            + chunk(b"IDAT", zlib.compress(rows)) + chunk(b"IEND", b"")).decode()


PNG = png()
fails = 0


def check(ok, what):
    global fails
    fails += not ok
    print(f"{'ok  ' if ok else 'FAIL'} {what}", flush=True)


async def session(prov, ask, model="claude-haiku-5-5[1m]", picture=False):
    """One request through the assistant's own harness and the provider's env. Returns (init, replies, tool results)."""
    import harness
    import home as homemod
    from claude_agent_sdk import (AssistantMessage, ClaudeAgentOptions, ClaudeSDKClient, ResultMessage, SystemMessage,
                                  TextBlock, UserMessage)
    from claude_agent_sdk.types import ToolResultBlock
    work = homemod.work_dir()
    work.mkdir(parents=True, exist_ok=True)
    # as brain.Session sets it up: auto mode, or with it off dontAsk and only harness.MANUAL_TOOLS
    guard = harness.Guard(log=lambda m: None, only=None if prov.auto else harness.MANUAL_TOOLS)
    opts = ClaudeAgentOptions(
        system_prompt="A provider check. Do what's asked, briefly.", model=model,
        settings=harness.settings_file(homemod.default_dir(), work, auto=prov.auto), setting_sources=[], strict_mcp_config=True,
        tools=None if prov.auto else harness.MANUAL_TOOLS,
        permission_mode="auto" if prov.auto else "dontAsk", cwd=str(work), env={**harness.env(), **prov.env()}, hooks=guard.hooks(),
        cli_path=__import__("provider").cli(), extra_args={"no-session-persistence": None, "effort": "low"}, max_turns=4)
    init, texts, results = {}, [], []
    async with ClaudeSDKClient(opts) as c:
        if picture:
            async def msg():
                yield {"type": "user", "message": {"role": "user", "content": [
                    {"type": "image", "source": {"type": "base64", "media_type": "image/png", "data": PNG}},
                    {"type": "text", "text": ask}]}, "parent_tool_use_id": None, "session_id": "default"}
            await c.query(msg())
        else:
            await c.query(ask)
        async for m in c.receive_response():
            if isinstance(m, SystemMessage) and m.subtype == "init":
                init = m.data
            elif isinstance(m, AssistantMessage):
                texts += [b.text for b in m.content if isinstance(b, TextBlock)]
            elif isinstance(m, UserMessage) and isinstance(m.content, list):
                results += [json.dumps(b.content, default=str) for b in m.content if isinstance(b, ToolResultBlock)]
            elif isinstance(m, ResultMessage):
                break
    return init, texts, results


def free_port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def mock_log(path, since):
    out = []
    for line in path.read_text(encoding="utf-8").splitlines() if path.exists() else []:
        r = json.loads(line)
        if r["t"] >= since:
            out.append(r)
    return out


async def mock():
    import provider
    test = ROOT / "local" / "assistant" / "provider-test"
    shutil.rmtree(test, ignore_errors=True)
    test.mkdir(parents=True)
    port = free_port()
    log = test / "mock.log"
    decoy_a = "sk-ant-api03-DECOY" + os.urandom(8).hex()
    decoy_o = "sk-or-v1-DECOY" + os.urandom(8).hex()
    m = subprocess.Popen([sys.executable, str(ROOT / "tools" / "provider_mock.py"), "--port", str(port), "--log", str(log),
                          "--secret", decoy_a, "--secret", decoy_o], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    os.environ["SOJOURN_KEYS"] = str(test / "keys")
    os.environ["SOJOURN_PROVIDER_UPSTREAM"] = f"http://127.0.0.1:{port}"
    try:
        time.sleep(1.5)
        p = provider.Provider(log=lambda s: None)

        p.configure({"provider": "anthropic"})
        check(p.state == "no_key" and not p.ready and "launcher" in p.how(), f"anthropic without a key: off, said how ({p.state})")

        provider.save_key("anthropic", decoy_a)
        kept = (test / "keys" / "anthropic.key").read_bytes()
        check(decoy_a.encode() not in kept and b"DECOY" not in kept and provider.load_key("anthropic") == decoy_a,
              "a kept key is encrypted on disk and comes back whole")
        p.configure({"provider": "anthropic", "key_stamp": 1})
        env = p.env()
        check(p.ready and p.state == "ready" and decoy_a not in json.dumps(env) and env["ANTHROPIC_BASE_URL"].startswith("http://127.0.0.1"),
              f"anthropic with a key: ready, the sessions get the proxy and its token, not the key ({p.state})")
        t = time.time()
        init, texts, results = await session(p, 'MOCK_BASH: python -c "import os;print(sorted(os.environ.items()))"')
        reqs = mock_log(log, t)
        main = [r for r in reqs if r.get("post") and r.get("tools")]
        check(init.get("permissionMode") == "auto", f"auto mode on through the proxy ({init.get('permissionMode')})")
        check(bool(main) and all(r["auth"] == "x-api-key" and r["auth_is_secret"] for r in reqs if r.get("post")),
              "every request reached the provider with the key as x-api-key")
        check(not any(r.get("secret_in_body") for r in reqs), "the key in no request's body (no model saw it)")
        check(bool(results) and not any(decoy_a in r for r in results) and any("ANTHROPIC_API_KEY" in r for r in results),
              "a shell's environment holds the proxy's token, never the key")
        check(any(r.get("model") == "claude-haiku-5-5" for r in main), "the model id passed through as Claude Code sent it")

        provider.save_key("anthropic", "sk-ant-api03-bad" + os.urandom(8).hex())
        p.configure({"provider": "anthropic", "key_stamp": 2})
        check(p.state == "refused" and not p.ready and "refused" in p.how(), f"a refused key: off, said so ({p.state})")
        check(not p.differs({"provider": "anthropic", "key_stamp": 2}), "settings unchanged: no reconfigure")
        check(p.differs({"provider": "anthropic", "key_stamp": 2, "auto_mode": False}), "auto mode switched: the sessions start again")

        p.configure({"provider": "openrouter"})
        check(p.state == "no_key", "openrouter without a key: off")
        provider.save_key("openrouter", decoy_o)
        p.configure({"provider": "openrouter", "openrouter_model": "claude", "key_stamp": 3})
        check(p.ready and not p.single, f"openrouter with Claude: ready, the three roles kept ({p.state})")
        t = time.time()
        init, texts, results = await session(p, 'MOCK_BASH: python -c "print(1)"')
        reqs = [r for r in mock_log(log, t) if r.get("post")]
        models = {r.get("model") for r in reqs}
        check(init.get("permissionMode") == "auto" and "anthropic/claude-haiku-5.5" in models,
              f"openrouter with Claude: auto mode, Haiku as OpenRouter names it ({sorted(models)})")
        check("anthropic/claude-sonnet-5" in models, "the safety checks went to Claude Sonnet 5 through OpenRouter")
        check(all(r["auth"] == "bearer" and r["auth_is_secret"] for r in reqs) and not any(r.get("secret_in_body") for r in reqs),
              "OpenRouter got the key as a Bearer token, in no body")
        check(any(r.get("output_config") for r in reqs if r.get("model") == "anthropic/claude-haiku-5.5"),
              "Claude through OpenRouter keeps its effort")
        msgs = [r for r in reqs if r.get("post", "").endswith("/v1/messages")]
        check(bool(msgs) and all((r.get("provider_prefs") or {}).get("data_collection") == "deny" for r in msgs),
              f"no data collection (the default): every OpenRouter request asks for providers that don't collect, the classifier's too ({len(msgs)})")

        p.configure({"provider": "openrouter", "openrouter_model": "claude", "key_stamp": 3, "openrouter_deny_data": False})
        t = time.time()
        await session(p, "Hello.")
        msgs = [r for r in mock_log(log, t) if r.get("post", "").endswith("/v1/messages")]
        check(bool(msgs) and not any(r.get("provider_prefs") for r in msgs), "data collection allowed: no provider preference sent")

        p.configure({"provider": "openrouter", "openrouter_model": "claude", "key_stamp": 3, "auto_mode": False})
        check("Auto mode is off" in p.prompt_note() and "CLAUDE_CODE_AUTO_MODE_MODEL" not in p.env(),
              "auto mode off: the model is told what's limited, no classifier model set")
        t = time.time()
        init, texts, results = await session(p, 'MOCK_BASH: python -c "print(12345)"')
        reqs = [r for r in mock_log(log, t) if r.get("post")]
        models = {r.get("model") for r in reqs}
        check(init.get("permissionMode") == "dontAsk" and "Bash" not in (init.get("tools") or []),
              f"auto mode off: dontAsk, no shell ({init.get('permissionMode')}, {sorted(init.get('tools') or [])[:8]})")
        check(not any("security monitor" in (r.get("system") or "") for r in reqs) and "anthropic/claude-sonnet-5" not in models,
              f"auto mode off: no classifier call at all ({sorted(m for m in models if m)})")
        check(not any("12345" in r for r in results), "auto mode off: the command didn't run")

        p.configure({"provider": "openrouter", "openrouter_model": "test/no-private", "key_stamp": 3})
        check(p.ready, f"a model with no provider that keeps to no data collection: set up ({p.state})")
        await session(p, "Hello.", model="claude-opus-5-5[1m]")
        check(p.state == "no_private" and "allow data collection" in p.how() and "no provider" in p.status()["short"],
              f"OpenRouter refused it for the data policy: said why and what to change ({p.state})")

        p.configure({"provider": "openrouter", "openrouter_model": "test/no-pictures", "key_stamp": 3})
        check(p.ready and p.single == "test/no-pictures" and p.window == 200_000 and "can't see pictures" in p.caveat,
              f"another maker's model: one model for all, its window, the caveat said ({p.caveat})")
        check("test/no-pictures" in p.prompt_note() and "OpenRouter" in p.prompt_note(), "the model is told what it is")
        t = time.time()
        init, texts, results = await session(p, "What's in this picture?", model="claude-opus-5-5[1m]", picture=True)
        reqs = [r for r in mock_log(log, t) if r.get("post")]
        mine = [r for r in reqs if r.get("model") == "test/no-pictures"]
        check(init.get("permissionMode") == "auto" and bool(mine), f"its requests carry its id, auto mode still on ({sorted({r.get('model') for r in reqs})})")
        check(all(not r.get("output_config") and not r.get("thinking") for r in mine), "Claude-only effort and thinking left out for it")
        check(all(r.get("images") == 0 for r in mine), "a picture it can't see is left out (a line says so)")

        p.configure({"provider": "openrouter", "openrouter_model": "google/gemini-4-argon", "key_stamp": 3})
        check(p.ready and p.window == 1_000_000 and "can't see pictures" not in p.caveat, "a model that sees: no picture caveat")
        t = time.time()
        await session(p, "What's in this picture?", model="claude-opus-5-5[1m]", picture=True)
        mine = [r for r in mock_log(log, t) if r.get("model") == "google/gemini-4-argon"]
        check(bool(mine) and any(r.get("images") for r in mine), "a model that sees gets the picture")
        p.configure({"provider": "openrouter", "openrouter_model": "test/no-tools", "key_stamp": 3})
        check(not p.ready and p.state == "no_tools" and "another model" in p.how(), f"a model without tools: off, said why ({p.state})")
        p.configure({"provider": "openrouter", "openrouter_model": "no/such-model", "key_stamp": 3})
        check(not p.ready and p.state == "no_tools", "a model OpenRouter doesn't have: off")

        provider.save_key("openrouter", "sk-or-v1-credits" + os.urandom(8).hex())
        p.configure({"provider": "openrouter", "openrouter_model": "claude", "key_stamp": 4})
        check(p.state == "credits" and "credits" in p.how(), f"no credits left: off, said so ({p.state})")

        p.configure({"provider": "claude"})
        check(p.state in ("ready", "no_login") and p.env() == {}, f"the Claude account: as signed in here ({p.state})")
        import paths
        paths.PACKAGED = True   # a packaged build offers no Claude account: its "claude" is none chosen yet
        try:
            p.app = "access"
            p.configure({"provider": "claude"})
            told = p.how()
            check(not p.ready and p.state == "none" and "Alt+0" in told and "Studio Quire" in told and "Claude" not in told,
                  f"packaged: no Claude account, none chosen, how to choose said ({p.state}: {told[:80]}...)")
        finally:
            paths.PACKAGED, p.app = False, None
    finally:
        m.terminate()
        m.wait(5)
        shutil.rmtree(test, ignore_errors=True)
    print(f"{'all as they should be' if not fails else f'{fails} failed'}")


async def real(kind, model):
    import provider
    if not provider.has_key(kind):
        sys.exit(f"no {kind} key kept (the launcher's key box, Access's Alt+0, or tools/assistant.py key)")
    p = provider.Provider(log=print)
    p.configure({"provider": kind, "openrouter_model": model})
    print(p.status())
    if not p.ready:
        sys.exit(1)
    for picture in (False, True):
        t = time.time()
        init, texts, _ = await session(p, "Say what colour this one-pixel picture is, in one word." if picture else
                                       "Reply with one word: ready.", picture=picture)
        print(f"{'picture' if picture else 'text'}: {init.get('permissionMode')}, {time.time() - t:.1f}s: {' '.join(texts)[:200]!r}")


if __name__ == "__main__":
    for f in (sys.stdout, sys.stderr):
        f.reconfigure(encoding="utf-8", errors="replace")
    if len(sys.argv) > 2 and sys.argv[1] == "real":
        asyncio.run(real(sys.argv[2], sys.argv[3] if len(sys.argv) > 3 else "claude"))
    else:
        asyncio.run(mock())
