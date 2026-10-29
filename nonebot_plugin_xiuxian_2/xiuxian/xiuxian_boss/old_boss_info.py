try:
    import ujson as json
except ImportError:
    import json
from ..xiuxian_utils.state_paths import persistent_directory
import os


class OLD_BOSS_INFO(object):
    def __init__(self):
        """从持久目录读取或初始化玩法状态，保留升级前数据。"""
        self.dir_path = persistent_directory(__file__)
        self.data_path = os.path.join(self.dir_path, "boss_info.json")
        try:
            with open(self.data_path, 'r', encoding='utf-8') as f:
                self.data = json.load(f)
        except FileNotFoundError:
            self.info = {}
            data = json.dumps(self.info, ensure_ascii=False, indent=4)
            with open(self.data_path, mode="x", encoding="UTF-8") as f:
                f.write(data)
                f.close()
            with open(self.data_path, 'r', encoding='utf-8') as f:
                self.data = json.load(f)

    def __save(self):
        """
        :return:保存
        """
        with open(self.data_path, 'w', encoding='utf-8') as f:
            json.dump(self.data, f, ensure_ascii=False, indent=4)

    def save_boss(self, boss_data):
        """
        保存boss
        :param boss_data:
        """
        self.data = {}
        self.data.update(boss_data)
        self.__save()
        return True

    def read_boss_info(self):
        """
        读取boss信息
        """
        tp = self.data
        self.__save()
        return tp

old_boss_info = OLD_BOSS_INFO()
