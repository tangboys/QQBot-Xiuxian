from pathlib import Path
from nonebot.log import logger


def download_xiuxian_data():
    """检查随包提供的离线基础资源，缺失时报错而不下载或执行远程代码。"""
    path_data = Path() / "data" / "xiuxian"
    required = (
        "灵根.json", "境界.json", "炼体境界.json", "突破概率.json",
        "宗门玩法配置.json", "items.json", "image/background.png",
        "image/banner.png", "font/SarasaMonoSC-Bold.ttf", "info_img/init.png",
    )
    missing = [name for name in required if not (path_data / name).is_file()]
    if missing:
        raise FileNotFoundError("修仙离线资源不完整，请修复安装包：" + "、".join(missing))
    logger.info("修仙离线基础资源检查完成")
