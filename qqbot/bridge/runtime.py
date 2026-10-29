"""在无网络驱动中完整注册修仙命令和定时任务。"""

import asyncio
import functools
import inspect
import os
import sys
from contextvars import ContextVar
from pathlib import Path

from transport import BridgeError, diagnostic


class Engine:
    """持有唯一 Bot 和串行业务执行锁，不创建 HTTP 或 WebSocket 服务。"""

    def __init__(self, transport):
        """保存协议通道和业务生命周期状态。"""
        self.transport = transport
        self.serial = asyncio.Lock()
        self.driver = None
        self.adapter = None
        self.bot = None
        self.scheduler = None
        self.errors = ContextVar("xiuxian_event_errors", default=None)

    def configure(self, initialization):
        """仅使用父进程提供的身份、管理员和数据目录初始化框架。"""
        self_id = initialization.get("self_id")
        administrators = initialization.get("administrators")
        root = initialization.get("data_root")
        if (type(self_id) is not int or self_id <= 0
                or not isinstance(administrators, list)
                or any(type(value) is not int or value <= 0 for value in administrators)
                or not isinstance(root, str) or not Path(root).is_absolute()):
            raise BridgeError("InitializationError")
        source = Path(os.environ.get("QQBOT_XIUXIAN_SOURCE_ROOT",
                                     str(Path(__file__).resolve().parents[2]))).resolve()
        if not (source / "nonebot_plugin_xiuxian_2" / "__init__.py").is_file():
            raise BridgeError("InitializationError")
        Path(root).mkdir(parents=True, exist_ok=True)
        os.chdir(root)
        sys.path.insert(0, str(source))

        import nonebot
        from nonebot.log import logger
        from nonebot.utils import DataclassEncoder
        from nonebot.adapters.onebot.v11 import Adapter, Bot
        from nonebot.adapters.onebot.v11.exception import NetworkError
        from messages import MessageNormalizer

        # 所有上游日志直接丢弃，桥只输出白名单诊断类别。
        logger.remove()
        self.transport.encoder = DataclassEncoder
        nonebot.init(
            driver="~none", _env_file=(str(source / "qqbot" / "bridge" / ".no-env"),),
            superusers={str(value) for value in administrators},
            nickname={initialization.get("nickname") or "QQBot"},
            command_start={"", "/"}, command_sep={"."},
            onebot_access_token=None, onebot_secret=None, onebot_ws_urls=set(),
            onebot_api_roots={}, apscheduler_autostart=False,
            apscheduler_config={"apscheduler.timezone": "Asia/Shanghai"},
        )
        self.driver = nonebot.get_driver()
        transport = self.transport
        normalizer = MessageNormalizer(root, source)

        class PipeAdapter(Adapter):
            """复用官方 OneBot 模型和 Bot，仅替换网络收发层。"""

            def _setup(self):
                """不注册网络端点或连接任务。"""

            async def _call_api(self, bot, api, **data):
                """将 OneBot 动作交给唯一父进程，发送前再次检查开关。"""
                try:
                    # 官方 Bot.send 使用通用 send_msg；在桥内收敛为宿主已有的固定目标发送动作。
                    if api == "send_msg":
                        group = data.get("message_type") == "group" or data.get("group_id") is not None
                        api = "send_group_msg" if group else "send_private_msg"
                        target = "group_id" if group else "user_id"
                        data = {target: data.get(target), "message": data.get("message", [])}
                    await transport.ensure_active(data.get("group_id"))
                    data.pop("_timeout", None)
                    return await transport.action(api, normalizer.action(api, data))
                except BridgeError as error:
                    if error.code == "Inactive":
                        raise
                    raise NetworkError("QQBotActionUnavailable") from None

        self.driver.register_adapter(PipeAdapter)
        self.adapter = nonebot.get_adapter(PipeAdapter)
        self.bot = Bot(self.adapter, str(self_id))
        self.install_preprocessors()
        self.scheduler = nonebot.require("nonebot_plugin_apscheduler").scheduler
        self.guard_scheduler()

        if nonebot.load_plugin("nonebot_plugin_xiuxian_2") is None:
            raise BridgeError("ModuleLoadError")
        base = "nonebot_plugin_xiuxian_2.xiuxian"
        business_root = source / "nonebot_plugin_xiuxian_2" / "xiuxian"
        # 工具目录是命名空间包，旧辅助文件不作为插件强制执行。
        expected = [base + "." + path.name for path in sorted(business_root.iterdir())
                    if path.is_dir() and (path / "__init__.py").is_file()]
        for name in expected:
            plugin = nonebot.get_plugin_by_module_name(name)
            if plugin is None or plugin.module_name != name:
                raise BridgeError("ModuleLoadError")
        if not expected or base not in sys.modules:
            raise BridgeError("ModuleLoadError")
        return expected

    def install_preprocessors(self):
        """在权限判断前通过真实群成员接口补全身份，并收集处理失败。"""
        from nonebot.message import event_preprocessor, run_postprocessor
        from nonebot.adapters.onebot.v11 import Bot, GroupMessageEvent
        from nonebot.adapters.onebot.v11.event import Sender
        from nonebot.compat import type_validate_python
        errors = self.errors

        @event_preprocessor
        async def authenticate_sender(bot: Bot, event: GroupMessageEvent):
            """忽略入站角色，接口失败时维持普通成员身份。"""
            event.sender.role = "member"
            event.sender.user_id = event.user_id
            try:
                member = await bot.get_group_member_info(
                    group_id=event.group_id, user_id=event.user_id, no_cache=True)
                if (isinstance(member, dict)
                        and member.get("user_id") == event.user_id
                        and member.get("group_id") == event.group_id):
                    sender = type_validate_python(Sender, member)
                    sender.role = sender.role if sender.role in {"owner", "admin"} else "member"
                    event.sender = sender
            except Exception:
                diagnostic("ActionError")

        @run_postprocessor
        async def record_failure(exception: Exception | None):
            """将框架捕获的业务异常记录为固定类别，不输出异常内容。"""
            current = errors.get()
            if exception is not None and current is not None:
                current.append(exception.code if isinstance(exception, BridgeError) else "EventError")

    def guard_scheduler(self):
        """覆盖任务注册入口，使静态和动态定时任务均遵守门禁和串行锁。"""
        from apscheduler.util import ref_to_obj
        add_job = self.scheduler.add_job

        def guarded_add_job(func, *args, **kwargs):
            """保留 APScheduler 原有配置和签名，仅包装实际任务函数。"""
            original = ref_to_obj(func) if isinstance(func, str) else func

            @functools.wraps(original)
            async def guarded(*job_args, **job_kwargs):
                """任务触发后检查全局和已知群开关，停用时不执行结算。"""
                try:
                    async with self.serial:
                        bound = inspect.signature(original).bind_partial(*job_args, **job_kwargs)
                        group_id = bound.arguments.get("group_id")
                        await self.transport.ensure_active(group_id)
                        result = original(*job_args, **job_kwargs)
                        if inspect.isawaitable(result):
                            return await result
                        return result
                except BridgeError as error:
                    if error.code != "Inactive":
                        diagnostic(error.code)
                except Exception:
                    diagnostic("EventError")

            return add_job(guarded, *args, **kwargs)

        self.scheduler.add_job = guarded_add_job

    async def start(self):
        """在当前任务进入官方生命周期，再登记 Bot 并启动定时任务。"""
        await self.driver._lifespan.startup()
        self.adapter.bot_connect(self.bot)
        self.scheduler.start()

    def ready(self, modules):
        """报告实际导入模块、已注册响应器和当前任务数量。"""
        from nonebot.matcher import matchers
        self.transport.send({"type": "ready", "modules": modules,
                             "commands": sum(len(group) for group in matchers.values()),
                             "jobs": len(self.scheduler.get_jobs())})

    def continuation_until(self, event):
        """只报告官方临时响应器正在等待的当前会话及其真实到期时间。"""
        from datetime import datetime
        from nonebot.matcher import matchers
        from nonebot.permission import User
        session = event.get_session_id()
        expirations = [matcher.expire_time for group in matchers.values() for matcher in group
                       if matcher.temp and matcher.expire_time is not None
                       and matcher.expire_time > datetime.now()
                       and any(isinstance(checker.call, User) and session in checker.call.users
                               for checker in matcher.permission.checkers)]
        return int(max(expirations).timestamp() * 1000) if expirations else 0

    async def consume(self):
        """串行分发完整 OneBot 消息模型，重复事件由传输层拦截。"""
        from nonebot.compat import type_validate_python
        from nonebot.adapters.onebot.v11 import GroupMessageEvent, PrivateMessageEvent
        while not self.transport.stopped.is_set():
            packet = await self.transport.events.get()
            error_code = None
            event = None
            failures = []
            token = self.errors.set(failures)
            try:
                async with self.serial:
                    payload = packet.get("event")
                    if not isinstance(payload, dict) or payload.get("self_id") != int(self.bot.self_id):
                        raise BridgeError("InvalidEvent")
                    kind = payload.get("message_type")
                    if kind not in {"group", "private"}:
                        raise BridgeError("InvalidEvent")
                    model = GroupMessageEvent if kind == "group" else PrivateMessageEvent
                    try:
                        event = type_validate_python(model, payload)
                    except Exception:
                        raise BridgeError("InvalidEvent") from None
                    # 在所有上游预处理器执行前降权，防止并发预处理读到伪造角色。
                    event.sender.role = "member"
                    await self.transport.ensure_active(getattr(event, "group_id", None))
                    await self.bot.handle_event(event)
                    if failures:
                        error_code = "Inactive" if "Inactive" in failures else "EventError"
            except BridgeError as error:
                error_code = error.code
            except asyncio.CancelledError:
                error_code = "Shutdown"
                raise
            except Exception:
                error_code = "EventError"
            finally:
                self.errors.reset(token)
                self.transport.complete(packet["id"], error_code,
                                        self.continuation_until(event) if event is not None else 0)
                self.transport.events.task_done()

    async def stop(self):
        """停止调度并在进入生命周期的同一任务退出，取消未完成工作。"""
        if self.scheduler is not None and self.scheduler.running:
            self.scheduler.shutdown(wait=False)
            await asyncio.sleep(0)
        if self.adapter is not None and self.bot.self_id in self.adapter.bots:
            self.adapter.bot_disconnect(self.bot)
        if self.driver is not None and self.driver._lifespan._task_group is not None:
            await self.driver._lifespan.shutdown()
