from nonebot import require, load_plugin

# 显式加载主包与全部业务包，加载失败立即停止，避免静默缺少玩法。
require('nonebot_plugin_apscheduler')
if load_plugin(__name__ + ".xiuxian") is None:
    raise RuntimeError("修仙主包加载失败")
for module in (
    "xiuxian_back", "xiuxian_bank", "xiuxian_base", "xiuxian_beg",
    "xiuxian_boss", "xiuxian_buff", "xiuxian_dufang", "xiuxian_fusion",
    "xiuxian_impart", "xiuxian_impart_pk", "xiuxian_info", "xiuxian_lunhui",
    "xiuxian_mixelixir", "xiuxian_rift", "xiuxian_sect", "xiuxian_work",
):
    if load_plugin(__name__ + ".xiuxian." + module) is None:
        raise RuntimeError("修仙业务包加载失败：" + module)
