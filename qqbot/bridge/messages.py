"""将 OneBot 消息规范化为安全的 JSON 消息段。"""

import base64
from pathlib import Path
from urllib.parse import unquote, urlsplit

from transport import BridgeError

MAX_IMAGE = 4 * 1024 * 1024


class MessageNormalizer:
    """只读取持久数据和种子资源内的图片，不转发任意本地路径。"""

    def __init__(self, data_root, source_root):
        """记录允许访问的图片根目录。"""
        self.roots = [Path(data_root).resolve(), (Path(source_root) / "data").resolve()]

    def image(self, value):
        """将有界本地图片转换为 base64，保留标准网络图片地址。"""
        if isinstance(value, bytes):
            raw = value
        else:
            value = str(value)
            if value.startswith(("http://", "https://")):
                return value
            if value.startswith("base64://"):
                encoded = value[9:]
                if len(encoded) > ((MAX_IMAGE + 2) // 3) * 4:
                    raise BridgeError("ActionError")
                try:
                    raw = base64.b64decode(encoded, validate=True)
                except Exception:
                    raise BridgeError("ActionError") from None
            else:
                if value.startswith("file:"):
                    parsed = urlsplit(value)
                    if parsed.netloc:
                        raise BridgeError("ActionError")
                    value = unquote(parsed.path)
                    if len(value) > 2 and value[0] == "/" and value[2] == ":":
                        value = value[1:]
                path = Path(value).absolute()
                # 同时拒绝符号链接和 Windows 目录联接，不仅检查最终解析位置。
                if any(part.is_symlink() or part.is_junction() for part in [path, *path.parents]):
                    raise BridgeError("ActionError")
                path = path.resolve()
                if not any(path.is_relative_to(root) for root in self.roots):
                    raise BridgeError("ActionError")
                if not path.is_file() or path.stat().st_size > MAX_IMAGE:
                    raise BridgeError("ActionError")
                with path.open("rb") as stream:
                    raw = stream.read(MAX_IMAGE + 1)
        if not raw or len(raw) > MAX_IMAGE:
            raise BridgeError("ActionError")
        # 图片文件头限制阻止把允许目录里的配置或数据库当图片外传。
        if not (raw.startswith(b"\x89PNG\r\n\x1a\n") or raw.startswith(b"\xff\xd8\xff")
                or raw.startswith((b"GIF87a", b"GIF89a"))
                or (raw.startswith(b"RIFF") and raw[8:12] == b"WEBP")):
            raise BridgeError("ActionError")
        return "base64://" + base64.b64encode(raw).decode("ascii")

    def message(self, value, depth=0):
        """解析 CQ 字符串和 Message 对象，递归规范转发节点内容。"""
        from nonebot.adapters.onebot.v11 import Message, MessageSegment
        if depth > 8:
            raise BridgeError("ActionError")
        if isinstance(value, MessageSegment):
            segments = [value]
        elif isinstance(value, dict):
            segments = [value]
        elif isinstance(value, str):
            segments = Message(value)
        elif isinstance(value, (list, tuple, Message)):
            segments = value
        else:
            raise BridgeError("ActionError")
        output = []
        for segment in segments:
            if isinstance(segment, MessageSegment):
                kind, data = segment.type, dict(segment.data)
            elif isinstance(segment, dict):
                kind, data = segment.get("type"), dict(segment.get("data", {}))
            else:
                raise BridgeError("ActionError")
            if kind not in {"text", "image", "at", "reply", "node"}:
                raise BridgeError("ActionError")
            if kind == "image":
                data["file"] = self.image(data.get("file", ""))
            elif kind == "node" and "content" in data:
                data["content"] = self.message(data["content"], depth + 1)
            output.append({"type": kind, "data": data})
        return output

    def action(self, action, params):
        """规范所有发送动作的普通消息和合并转发消息。"""
        result = dict(params)
        for field in ("group_id", "user_id", "message_id"):
            if field in result:
                try:
                    result[field] = int(result[field])
                except (TypeError, ValueError):
                    raise BridgeError("ActionError") from None
        if action.startswith("send_"):
            for field in ("message", "messages"):
                if field in result:
                    result[field] = self.message(result[field])
        return result
