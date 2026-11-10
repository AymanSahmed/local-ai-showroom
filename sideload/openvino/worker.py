"""Local AI Showroom - OpenVINO sideload worker.

Runs an ONNX Runtime GenAI model (for example the *-openvino-npu / *-openvino-gpu variants that Foundry Local
downloads) with a *newer* OpenVINO execution provider than the one Foundry Local ships. This is a workaround for the
Foundry Local issue where those variants fail with "OrtEpApi_ReleaseEpContextConfig_SinceV28 is not available".

The bridge starts this script once per loaded model and talks to it with one JSON object per line:

  stdin  {"cmd":"load","model_dir":"...","device":"NPU|GPU|CPU|default"}
         {"cmd":"generate","id":1,"messages":[{"role":"user","content":"..."}],"max_tokens":256,"temperature":0.7}
         {"cmd":"cancel"}   {"cmd":"ping"}   {"cmd":"shutdown"}
  stdout {"type":"loaded","ms":...}  {"type":"delta","id":1,"text":"..."}  {"type":"done","id":1,...}
         {"type":"error","id":1,"message":"..."}  {"type":"pong",...}

Everything else (library logging, native prints) is sent to stderr so the protocol channel stays clean.
The worker exits when its stdin closes, so it can never outlive the bridge.
"""
import json
import os
import queue
import sys
import threading
import time
import traceback

_TRACE = os.environ.get("SHOWROOM_TRACE")


def trace(message):
    """Optional diagnostics: set SHOWROOM_TRACE to a file path to log what the worker is doing."""
    if _TRACE:
        with open(_TRACE, "a", encoding="utf-8") as handle:
            handle.write(f"{time.strftime('%H:%M:%S')} {message}\n")


# Keep the real stdout for the protocol; route all other output (including native code writing to fd 1) to stderr.
trace("start")
_proto = os.fdopen(os.dup(1), "w", encoding="utf-8", newline="\n", buffering=1)
os.dup2(2, 1)
sys.stdout = sys.stderr
trace("stdout redirected")

SITE = os.environ.get("SHOWROOM_SITE_PACKAGES")
if SITE and SITE not in sys.path:
    sys.path.insert(0, SITE)


def send(obj):
    _proto.write(json.dumps(obj, ensure_ascii=False) + "\n")
    _proto.flush()


def versions():
    out = {"python": sys.version.split()[0]}
    for name, module in (("onnxruntime", "onnxruntime"), ("onnxruntime_genai", "onnxruntime_genai")):
        try:
            out[name] = __import__(module).__version__
        except Exception as exc:  # noqa: BLE001
            out[name] = f"missing ({type(exc).__name__})"
    try:
        from importlib.metadata import version

        out["onnxruntime_ep_openvino"] = version("onnxruntime-ep-openvino")
    except Exception as exc:  # noqa: BLE001
        out["onnxruntime_ep_openvino"] = f"missing ({type(exc).__name__})"
    return out


class Engine:
    """One loaded model."""

    def __init__(self):
        self.og = None
        self.model = None
        self.tokenizer = None
        self.context_length = 4096
        self.provider_registered = False

    def load(self, model_dir, device):
        import onnxruntime_genai as og

        self.og = og
        if not os.path.isfile(os.path.join(model_dir, "genai_config.json")):
            raise FileNotFoundError(f"genai_config.json not found in {model_dir}")
        with open(os.path.join(model_dir, "genai_config.json"), encoding="utf-8") as handle:
            self.context_length = int(json.load(handle).get("model", {}).get("context_length", 4096))

        config = og.Config(model_dir)
        if device in ("NPU", "GPU", "CPU"):
            import onnxruntime_ep_openvino as ov

            if not self.provider_registered:
                og.register_execution_provider_library(ov.get_ep_name(), ov.get_library_path())
                self.provider_registered = True
            config.clear_providers()
            config.append_provider("OpenVINO")
            config.set_provider_option("OpenVINO", "device_type", device)
        self.model = og.Model(config)
        self.tokenizer = og.Tokenizer(self.model)

    def prompt_for(self, messages):
        try:
            return self.tokenizer.apply_chat_template(json.dumps(messages), add_generation_prompt=True)
        except Exception:  # noqa: BLE001 - models without a chat template
            lines = [f"{m['role']}: {m['content']}" for m in messages]
            return "\n".join(lines) + "\nassistant:"

    def generate(self, request_id, messages, max_tokens, temperature, cancelled):
        if self.model is None:
            raise RuntimeError("No model is loaded. Load a model first.")
        og = self.og
        prompt_ids = self.tokenizer.encode(self.prompt_for(messages))
        prompt_tokens = len(prompt_ids)
        budget = max(1, min(int(max_tokens), self.context_length - prompt_tokens - 1))

        params = og.GeneratorParams(self.model)
        params.set_search_options(
            max_length=prompt_tokens + budget,
            temperature=max(0.0, float(temperature)),
            do_sample=float(temperature) > 0.0,
        )
        generator = og.Generator(self.model, params)
        generator.append_tokens(prompt_ids)
        stream = self.tokenizer.create_stream()

        started = time.time()
        produced = 0
        status = "Complete"
        finish = "length"
        try:
            while not generator.is_done():
                if cancelled.is_set():
                    status, finish = "Cancelled", "cancelled"
                    break
                generator.generate_next_token()
                text = stream.decode(generator.get_next_tokens()[0])
                produced += 1
                if text:
                    send({"type": "delta", "id": request_id, "text": text})
                if produced >= budget:
                    break
            else:
                finish = "stop"
        finally:
            del generator
        send({
            "type": "done",
            "id": request_id,
            "status": status,
            "finish_reason": finish,
            "prompt_tokens": prompt_tokens,
            "completion_tokens": produced,
            "ms": int((time.time() - started) * 1000),
        })


def main():
    if "--selftest" in sys.argv:
        info = versions()
        try:
            import onnxruntime as ort
            import onnxruntime_ep_openvino as ov

            ort.register_execution_provider_library("openvino_ep_selftest", ov.get_library_path())
            info["openvino_devices"] = sorted(
                {d.ep_metadata.get("ov_device", "?") for d in ort.get_ep_devices() if d.ep_name == ov.get_ep_name() and d.ep_metadata}
            )
            info["ok"] = True
        except Exception as exc:  # noqa: BLE001
            info["ok"] = False
            info["error"] = f"{type(exc).__name__}: {exc}"
        print(json.dumps(info), file=_proto, flush=True)
        return 0 if info.get("ok") else 1

    commands = queue.Queue()
    cancelled = threading.Event()

    def read_stdin():
        for line in sys.stdin:
            line = line.strip()
            if not line:
                continue
            try:
                command = json.loads(line)
            except json.JSONDecodeError:
                continue
            if command.get("cmd") == "cancel":
                cancelled.set()
            else:
                commands.put(command)
        cancelled.set()
        commands.put(None)  # the bridge is gone: stop

    # Import everything BEFORE the stdin reader thread starts. On Windows a thread blocked reading a pipe can stall
    # other operations on the same handle, and a Python import freezes that way.
    engine = Engine()
    versions_info = versions()
    trace("versions collected")
    try:
        import onnxruntime_genai  # noqa: F401
        import onnxruntime_ep_openvino  # noqa: F401

        engine.preloaded = True
    except Exception as exc:  # noqa: BLE001
        engine.preloaded = False
        versions_info["import_error"] = f"{type(exc).__name__}: {exc}"
    trace("packages imported")

    threading.Thread(target=read_stdin, daemon=True).start()
    trace("reader thread started")

    send({"type": "ready", "versions": versions_info})
    trace("ready sent")

    while True:
        command = commands.get()
        if command is None or command.get("cmd") == "shutdown":
            return 0
        name = command.get("cmd")
        request_id = command.get("id")
        try:
            if name == "ping":
                send({"type": "pong", "versions": versions()})
            elif name == "load":
                started = time.time()
                engine.load(command["model_dir"], command.get("device", "default"))
                send({"type": "loaded", "ms": int((time.time() - started) * 1000), "device": command.get("device", "default")})
            elif name == "generate":
                cancelled.clear()
                engine.generate(
                    request_id,
                    command["messages"],
                    command.get("max_tokens", 256),
                    command.get("temperature", 0.7),
                    cancelled,
                )
            else:
                send({"type": "error", "id": request_id, "message": f"Unknown command '{name}'."})
        except Exception as exc:  # noqa: BLE001
            tail = "".join(traceback.format_exception_only(type(exc), exc)).strip()
            send({"type": "error", "id": request_id, "message": tail[:1500]})


if __name__ == "__main__":
    sys.exit(main())
