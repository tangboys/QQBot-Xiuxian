# 上游静态审计与功能保留说明

基线提交：`128e31643b224350f1f0ce31f210553fd34334c1`。

## 验证边界

仅执行自编清单脚本，对源码做 AST 解析、读取文件、计算 SHA-256 与生成 Git diff。未导入任何上游模块，未启动游戏、调用机器人 API、执行游戏测试或运行资源中的 jjsp.py。下述“保留”表示源码入口和资源保留，不表示运行验证通过。运行验证由用户完成。

## 功能覆盖

55 个原 Python 源文件均保留，新增 1 个持久目录辅助文件。16 个业务包全部显式加载：背包坊市/拍卖、灵庄、基础修仙、仙途奇缘、世界 Boss、功法闭关洞天、仙石对决、合成、虚神界、虚神界对战、个人信息、轮回、炼丹、秘境、宗门、悬赏。

共 128 个匹配器：88 on_command、37 on_fullmatch、3 on_regex；14 个定时任务。一个匹配器可能覆盖多个子操作，因此该数不是“全部玩法条数”。每个主命令、别名、正则、权限、优先级、源路径、行号、定时参数及 API 调用均记录在 feature-manifest.json。upstream-manifest.json 保留改动前 55 文件清单与哈希，source-changes.json 记录前后哈希，upstream.patch 记录受跟踪文件差异（新增 state_paths.py 直接随源码提供）。

OneBot action：get_group_list、send_group_msg、send_private_msg、send_group_forward_msg、send_private_forward_msg。bot.send 为适配器便捷方法；Boss 的动态 api 字符串静态追踪为 send_group_msg。

## 已修改的确定问题

- 根入口以明确包名加载主包及全部 16 业务包，任何加载失败抛错，避免目录扫描误把普通工具模块当插件以及漏玩法。
- 下载入口不再在导入时从浮动远端下载 zip 或覆盖 data。改为检查离线基础 JSON、字体与必要图片。完整资源清单另见 asset-manifest.json。安装必须携带全部原资源；缺失不得运行时下载安装代码。
- 原目录内配置、坊市、Boss、秘境、双修次数、虚神对战与世界状态统一迁往持久 data/xiuxian/runtime/<模块>。首次复制源目录 JSON，目标存在就不覆盖。五个状态初始化器只对文件不存在生成默认值；损坏 JSON 会明确失败，不再误走独占创建报 FileExistsError。
- 世界 Boss 发送回退原先使用可能未定义的 bot，并把未 await 的 get_group_list 协程与群号做包含判断。现获取机器人、等待群列表并比较返回记录中的 group_id。
- JsonDate.exercises_level_data 访问未定义 exercises_level_jsonpath；补齐已有炼体境界.json 路径。
- 头像下载使用 HTTPS；第三次失败后立即进入既有回退，不再额外睡眠；用户头像目录允许并发重复创建。原每次请求 20 秒、三次重试策略保留，未宣称头像瞬时返回。
- create_user 原调用已 commit 的 _create_user 后又 commit，删除重复一次。未重排其他经济事务。
- 重置耐药性、背包消耗两处将用户/物品编号等 SQL 字符串拼接改为参数绑定，不改数值。
- 图片编码失败转 JPEG 前清空缓冲，避免混合失败编码残留和 JPEG 数据。
- 主代理复核自动回血：原定时器只读取方法对象，方法内部又未执行SELECT。改为实际调用并用单条UPDATE完成原速率恢复，限制不超过满血并释放游标。
- 桥内显式将官方 Bot.send 的 send_msg 收敛为固定群/私聊发送，移除无关目标字段；入站引用段不触发额外消息查询。ready模块数量只计16个业务包。
- 重入仙途的续答支持原NoneBot临时matcher；同群同用户在原会话过期前可提交数字选择，不会把普通群数字启动成游戏。越界编号和不能解析的Unicode数字修复未初始化msg/ValueError，沿用原最佳灵根回退。

## 下载器与 jjsp.py 的准确结论

原下载器会在导入时取远端未锁哈希 zip 并按路径覆盖文件，有供应链、覆盖状态和路径穿越风险。已移除网络/解压实现。data/xiuxian/jjsp.py 是原资源中的手工 JSON rank 迁移脚本，带 __main__ 入口；55 个上游模块没有 import/exec 该脚本的调用证据，不能宣称原项目在导入时执行它。该资源按要求原样保留，桥不得执行它。

## 数据库、并发与其余限制

- 用户名、宗门名等主要写入原已参数化。schema 字段名来自内置配置，不宜将所有 f-string 误报为注入。update_back_equipment 接受调用方构造 SQL；仍是审查边界，未全面重写数据库层。
- 两个 SQLite 管理类均为单例且已有 shutdown close 回调；未发现必须额外创建/关闭每次查询连接的理由。局部 cursor 生命周期及错误分支未全面改造。
- 原经济玩法存在跨多次提交及 JSON/数据库混合更新。即使桥串行处理消息，崩溃中断、定时任务与等待网络后的交错仍不能保证全局事务原子性。未以未经验证的大规模锁或事务重写冒充修复。
- update_back_j 等路径仍假设调用方已确认物品存在；空库存、极端数量、损坏旧存档、字符串数值及同时拍卖/交易需用户实际验收。不能从静态清点得出所有边界正确。
- 原 JSON 配置加载函数仍有宽泛异常处理，部分可能重建默认配置；本次仅五个运行状态初始化器收窄异常，未宣称所有数据损坏分支解决。
- Pillow 渲染仍保留原字体、布局及图片；部分同步渲染和图片文件生命周期未全面优化。没有进行实际图片生成或视觉比对。
- 迁移路径依赖桥将 cwd 固定到持久用户目录，卸载/更新必须保留该目录。源码旁的历史 JSON 只在首次目标缺失时迁移，禁止更新时回写覆盖玩家数据。

## 依赖与资源

静态第三方 import：nonebot2、nonebot-adapter-onebot、nonebot-plugin-apscheduler、Pillow、numpy、aiohttp、httpx、pydantic（原已使用 pydantic.v1）、wcwidth、ujson（可回退标准 json）。wget 已随下载实现移除。没有 requests、psutil、aiofiles 的直接导入；asyncio/pathlib 等为标准库。httpx 是原 requirements 漏列项，需打入依赖包。

data/xiuxian 全部文件逐项路径、大小和 SHA-256 在 asset-manifest.json 中；原 JSON、图片、字体及辅助脚本未修改。图片/字体具体作者、授权链及再分发条件未能从仓库中逐项确认，统一标记 unknown，不能把代码仓库 MIT 声明推定覆盖全部第三方资产。发行者需另行确认资源授权。

## 复核方式

运行 `python qqbot/audit/inventory.py` 只刷新静态清单，不导入游戏。前后 feature-manifest 的 128 入口和 14 定时任务保持一致；Python AST 解析完成。这不替代用户负责的功能、交易并发、图片、升级数据保留与机器人互通测试。
