"""将上游可变 JSON 放入持久数据目录，升级时不覆盖已有玩家状态。"""
from pathlib import Path
from shutil import copy2


def persistent_directory(source_file):
    """首次迁移模块内 JSON，此后仅使用 data/xiuxian/runtime 下的副本。"""
    source = Path(source_file).parent
    target = Path() / "data" / "xiuxian" / "runtime" / source.name
    target.mkdir(parents=True, exist_ok=True)
    for seed in source.glob("*.json"):
        destination = target / seed.name
        if not destination.exists():
            copy2(seed, destination)
    return target
