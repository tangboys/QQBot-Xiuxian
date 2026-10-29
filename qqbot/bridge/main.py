"""QQBot 修仙桥入口：由宿主启动，所有通信仅使用标准输入输出。"""

import asyncio
import sys
from pathlib import Path

# 嵌入式 Python 的 ._pth 隔离模式不会自动加入脚本所在目录。
sys.path.insert(0, str(Path(__file__).resolve().parent))

from transport import BridgeError, Transport, diagnostic, redact_output


async def main():
    """接收初始化，运行引擎，关闭时收回所有排队事件和动作等待。"""
    transport = Transport()
    transport.start_reader()
    receiver = asyncio.create_task(transport.receive())
    worker = None
    engine = None
    try:
        initialization = await transport.initialized
        from runtime import Engine
        engine = Engine(transport)
        modules = engine.configure(initialization)
        await engine.start()
        if transport.stopped.is_set():
            return
        engine.ready(modules)
        worker = asyncio.create_task(engine.consume())

        def worker_finished(task):
            """业务消费器意外退出时结束会话，避免父进程永久等待。"""
            transport.stopped.set()

        worker.add_done_callback(worker_finished)
        await transport.stopped.wait()
    except asyncio.CancelledError:
        pass
    except Exception as error:
        code = error.code if isinstance(error, BridgeError) else "InitializationError"
        diagnostic(code)
        transport.send({"type": "fatal", "error_type": code})
    finally:
        transport.stopped.set()
        for pending in transport.pending.values():
            if not pending.done():
                pending.cancel()
        if worker is not None:
            worker.cancel()
            await asyncio.gather(worker, return_exceptions=True)
        while not transport.events.empty():
            packet = transport.events.get_nowait()
            transport.complete(packet["id"], "Shutdown")
            transport.events.task_done()
        if engine is not None:
            await engine.stop()
        receiver.cancel()
        await asyncio.gather(receiver, return_exceptions=True)


if __name__ == "__main__":
    redact_output()
    try:
        asyncio.run(main())
    except BaseException:
        diagnostic("InternalError")
        raise SystemExit(1) from None
