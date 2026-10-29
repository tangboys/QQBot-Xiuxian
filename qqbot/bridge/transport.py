"""有界 JSONL 通道；只向父进程传递协议数据。"""

import asyncio
import io
import json
import sys
import threading
from collections import OrderedDict

MAX_LINE = 8 * 1024 * 1024
ERROR_TYPES = frozenset({
    "ProtocolError", "InitializationError", "ModuleLoadError", "QueueFull",
    "InvalidEvent", "ActionError", "ActionTimeout", "Inactive", "Shutdown",
    "EventError", "InternalError",
})
_STDOUT = sys.stdout.buffer
_STDERR = sys.stderr
_STDIN = sys.stdin.buffer


def diagnostic(code):
    """仅输出预先定义的安全诊断类别，不包含消息、异常文本或路径。"""
    if code not in ERROR_TYPES:
        code = "InternalError"
    _STDERR.write("[xiuxian-bridge] " + code + "\n")
    _STDERR.flush()


class RedactedOutput(io.TextIOBase):
    """丢弃上游任意输出，首次写入时仅报告固定脱敏标记。"""

    def __init__(self):
        """初始化一次性提示状态。"""
        self.reported = False

    def write(self, value):
        """接收输出但不保留内容，避免日志泄露或污染协议。"""
        if value and not self.reported:
            self.reported = True
            _STDERR.write("[xiuxian-bridge] upstream_output_redacted\n")
            _STDERR.flush()
        return len(value)

    def flush(self):
        """本输出器不缓存上游文本。"""


def redact_output():
    """在导入 NoneBot 和业务模块前隔离所有 Python 标准输出。"""
    sys.stdout = RedactedOutput()
    sys.stderr = RedactedOutput()


class BridgeError(Exception):
    """只携带固定错误类别的协议异常。"""

    def __init__(self, code):
        """将不可信错误类别转换为固定安全类别。"""
        self.code = code if code in ERROR_TYPES else "InternalError"
        super().__init__(self.code)


class Transport:
    """通过后台只读线程接入管道，事件和动作响应分别调度。"""

    def __init__(self):
        """创建有界接收队列、动作限制和重复事件缓存。"""
        self.inbox = asyncio.Queue(maxsize=128)
        self.events = asyncio.Queue(maxsize=64)
        self.stopped = asyncio.Event()
        self.initialized = asyncio.get_running_loop().create_future()
        self.pending = {}
        self.action_slots = asyncio.Semaphore(8)
        self.sequence = 0
        self.accepted = set()
        self.completed = OrderedDict()
        self.encoder = json.JSONEncoder

    def send(self, packet):
        """原子写出一行 UTF-8 JSON，限制输出大小。"""
        raw = json.dumps(packet, ensure_ascii=False, cls=self.encoder,
                         separators=(",", ":"), allow_nan=False).encode("utf-8")
        if len(raw) > MAX_LINE:
            raise BridgeError("ProtocolError")
        _STDOUT.write(raw + b"\n")
        _STDOUT.flush()

    def start_reader(self):
        """启动守护线程，避免 Windows 管道阻塞事件循环及退出。"""
        loop = asyncio.get_running_loop()

        def read_lines():
            """逐行读取有界输入，向主循环施加接收背压。"""
            try:
                while not loop.is_closed():
                    raw = _STDIN.readline(MAX_LINE + 2)
                    if not raw:
                        packet = {"type": "shutdown"}
                    elif len(raw.rstrip(b"\r\n")) > MAX_LINE or not raw.endswith(b"\n"):
                        packet = {"type": "protocol_error"}
                    else:
                        packet = json.loads(raw)
                        if not isinstance(packet, dict):
                            packet = {"type": "protocol_error"}
                    asyncio.run_coroutine_threadsafe(self.inbox.put(packet), loop).result()
                    if packet.get("type") in {"shutdown", "protocol_error"}:
                        return
            except Exception:
                if not loop.is_closed():
                    asyncio.run_coroutine_threadsafe(
                        self.inbox.put({"type": "protocol_error"}), loop)

        threading.Thread(target=read_lines, name="xiuxian-stdin", daemon=True).start()

    def complete(self, event_id, code=None, continuation_until=0):
        """记住有限个完成结果，重发不会再次执行结算。"""
        packet = {"type": "event_done", "id": event_id, "ok": code is None,
                  "continuation_until": continuation_until}
        if code:
            packet["error_type"] = code if code in ERROR_TYPES else "EventError"
        self.accepted.discard(event_id)
        self.completed[event_id] = packet
        while len(self.completed) > 256:
            self.completed.popitem(last=False)
        self.send(packet)

    async def receive(self):
        """持续处理动作回包；消息排队不会阻塞动作响应。"""
        try:
            while True:
                packet = await self.inbox.get()
                kind = packet.get("type")
                if kind == "shutdown":
                    return
                if kind == "initialize" and not self.initialized.done():
                    self.initialized.set_result(packet)
                elif kind == "action_result":
                    action_id = packet.get("id")
                    future = self.pending.get(action_id) if isinstance(action_id, str) else None
                    if future is not None and not future.done():
                        future.set_result(packet)
                elif kind == "event" and self.initialized.done():
                    event_id = packet.get("id")
                    if not isinstance(event_id, str) or not 1 <= len(event_id) <= 256:
                        raise BridgeError("ProtocolError")
                    if event_id in self.completed:
                        self.send(self.completed[event_id])
                    elif event_id not in self.accepted:
                        try:
                            self.events.put_nowait(packet)
                            self.accepted.add(event_id)
                        except asyncio.QueueFull:
                            self.complete(event_id, "QueueFull")
                else:
                    raise BridgeError("ProtocolError")
        except Exception:
            diagnostic("ProtocolError")
            self.send({"type": "fatal", "error_type": "ProtocolError"})
        finally:
            self.stopped.set()
            if not self.initialized.done():
                self.initialized.cancel()
            for future in self.pending.values():
                if not future.done():
                    future.cancel()

    async def action(self, action, params):
        """限制并发动作数；包含等待配额在内最多等待三十秒。"""
        try:
            async with asyncio.timeout(30):
                async with self.action_slots:
                    if self.stopped.is_set():
                        raise BridgeError("Shutdown")
                    self.sequence += 1
                    action_id = str(self.sequence)
                    future = asyncio.get_running_loop().create_future()
                    self.pending[action_id] = future
                    try:
                        self.send({"type": "action", "id": action_id,
                                   "action": action, "params": params})
                        result = await future
                        if result.get("ok") is not True:
                            if action == "qqbot.ensure_active" and result.get("error_type") == "OperationCanceledException":
                                raise BridgeError("Inactive")
                            raise BridgeError("ActionError")
                        return result.get("data")
                    finally:
                        self.pending.pop(action_id, None)
        except TimeoutError:
            raise BridgeError("ActionTimeout") from None

    async def ensure_active(self, group_id=None):
        """向父进程确认当前全局和群开关，未知状态一律拒绝。"""
        if group_id is not None:
            try:
                group_id = int(group_id)
            except (TypeError, ValueError):
                raise BridgeError("ActionError") from None
            if group_id <= 0:
                raise BridgeError("ActionError")
        result = await self.action("qqbot.ensure_active", {"group_id": group_id})
        if not isinstance(result, dict) or result.get("active") is not True:
            raise BridgeError("Inactive")
