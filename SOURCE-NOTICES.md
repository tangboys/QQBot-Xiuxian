# QQBot 修仙二开来源与分发记录

本仓库是 MyXiaoNan/nonebot_plugin_xiuxian_2_pmv 的 Fork。

- 原仓：https://github.com/MyXiaoNan/nonebot_plugin_xiuxian_2_pmv
- 基线：128e31643b224350f1f0ce31f210553fd34334c1
- Fork：https://github.com/tangboys/QQBot-Xiuxian
- 二开分支：qqbot-integration
- QQBot 入口：qqbot/plugin，Python 桥：qqbot/bridge；玩法源码仍在原目录。

上游根 LICENSE 是 MIT，版权 2024 少姜，原文完整保留。上游还引用 QingMuCat/nonebot_plugin_xiuxian_2；该仓 README 标注 MIT，而 master/pyproject.toml 写 GPL-3.0-or-later。此差异未由作者澄清，本二开不替上游重授许可、不删除版权，也不声称已确认所有历史贡献的授权。需要继续对外发布衍生版本时应结合实际采用文件核对来源。

原 data/xiuxian 的图片、字体和数据均逐项保留并记录 SHA256，具体资产作者和许可尚未逐项确认，见 qqbot/audit/asset-manifest.json。不能仅凭根目录 MIT 声明，就断言全部第三方素材均可无限制再分发。原辅助脚本 jjsp.py 作为对应源码保留，程序不会调用它。

发行包 engine/source/upstream-128e316.zip 提供完整对应原源码；当前修改源码随完整 QQBot 源码包及本 Fork 提供。第三方 Python 包各自的 LICENSE、NOTICE 与 dist-info 元数据随解释器保留，包名、版本、来源及 SHA256 见 engine/source/dependencies.json。CPython 原版许可在 runtime/win-x64/LICENSE.txt，Windows x64 固定解释器为官方 3.13.17。并不将 CPython、Pillow、NumPy 等全部笼统归为本仓 MIT。

改动和未验证边界见 qqbot/audit/REVIEW.md。源码保留128个匹配器与14项定时注册，不等于已完成全部实机验收；所有功能由用户按 qqbot/README-QQBOT.md 的清单执行。
